using System.Numerics;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules.SpecialPowers;

/// <summary>
/// The Air Force General's command center sending a Spectre (the original's SpectreGunshipDeploymentUpdate):
/// the gunship (GunshipTemplateName) appears at the map edge (CreateLocation) facing the target and
/// is told to use the same power there.
/// </summary>
public sealed class SpectreGunshipDeploymentUpdate : Module, IUpdateModule, ICreateModule, ISpecialPowerUpdate
{
    SpecialPowerModule? specialPowerModule;
    uint gunshipId;
    Vector3 initialTargetPosition;

    public void OnCreate()
    {
        if (Game.Catalog.SpecialPower(Setting("SpecialPowerTemplate")) is { } power)
            specialPowerModule = SpecialPowerRules.ModuleFor(Owner, power);
        else
            Log.Warning($"{Owner.Template.Name}'s SpectreGunshipDeploymentUpdate has no SpecialPowerTemplate.");
    }

    public bool PassesScienceTest() =>
        Setting("RequiredScience") is not { Length: > 0 } science || Owner.Owner.HasScience(science);

    public bool InitiateIntentToDoSpecialPower(SpecialPowerTemplate power, GameObject? target, Vector3? position, uint options)
    {
        if (specialPowerModule?.Power != power || position is not { } targetPos)
            return false;
        if ((options & ObjectCommandButtons.CommandFiredByScript) != 0)
            specialPowerModule.SetReadyFrame(Frame);
        initialTargetPosition = targetPos;

        if (Game.FindObject(gunshipId) != null)
            gunshipId = 0; // a new one each time; the old one finishes on its own
        if (Game.Templates.Find(Setting("GunshipTemplateName")) is { } gunshipTemplate && Owner.Team is { } team)
        {
            var gunship = Game.CreateObject(gunshipTemplate, Owner.Owner, Owner.Position, 0, team);
            gunship.ProducerId = Owner.Id;
            var creation = Setting("CreateLocation").ToUpperInvariant() switch
            {
                "CREATE_AT_EDGE_NEAR_SOURCE" => Game.Terrain.ClosestEdgePoint(Owner.Position),
                "CREATE_AT_EDGE_FARTHEST_FROM_SOURCE" => Game.Terrain.FarthestEdgePoint(Owner.Position),
                "CREATE_AT_EDGE_NEAR_TARGET" => Game.Terrain.ClosestEdgePoint(targetPos),
                _ => Game.Terrain.FarthestEdgePoint(targetPos),
            };
            // (The original pushes the start out by an orbit radius it never reads in, so by nothing.)
            var delta = initialTargetPosition - creation;
            float distance = delta.Length();
            if (distance > 0)
                delta = Vector3.Normalize(delta) * distance;
            creation.X = initialTargetPosition.X - delta.X;
            creation.Y = initialTargetPosition.Y - delta.Y;
            creation.Z = gunship.AI?.CurrentLocomotor?.PreferredHeight ?? creation.Z;
            gunship.SetPosition(creation);
            gunship.SetOrientation(MathF.Atan2(initialTargetPosition.Y - creation.Y, initialTargetPosition.X - creation.X));
            gunshipId = gunship.Id;
            if (SpecialPowerRules.ModuleFor(gunship, power) is { } shipPower)
            {
                shipPower.MarkSpecialPowerTriggered(initialTargetPosition);
                shipPower.DoSpecialPowerAtLocation(initialTargetPosition, ObjectCommandButtons.InvalidAngle, options);
            }
            if (Owner.Owner.IsLocalPlayer)
                Game.Events.ForceSelect(gunship, false, "");
        }
        specialPowerModule.MarkSpecialPowerTriggered(initialTargetPosition);
        return true;
    }

    public int Update()
    {
        var me = Owner;
        if (me.HasStatus(Flags.ObjectStatus.IndexOf("SOLD")) || me.IsUnderConstruction || me.IsEffectivelyDead)
            return LogicTime.SleepForever;
        return 0;
    }
}

/// <summary>
/// The Spectre gunship itself (the original's SpectreGunshipUpdate): it flies in with afterburners,
/// then circles the target at GunshipOrbitRadius for OrbitTime. Its gattling (a contained
/// GattlingTemplateName) hoses enemies under the targeting reticle, which the player can steer within
/// AttackAreaRadius; the howitzer fires every HowitzerFiringRate where the gattling has settled. Then
/// it leaves the map.
/// </summary>
public sealed class SpectreGunshipUpdate : Module, IUpdateModule, ICreateModule, ISpecialPowerUpdate
{
    enum GunshipStatus { Inserting, Orbiting, Departing, Idle }

    const float OrbitInsertionSlopeMax = 0.8f, OrbitInsertionSlopeMin = 0.5f;
    const int LotsOfShots = 9999;

    SpecialPowerModule? specialPowerModule;
    uint gattlingId;
    GunshipStatus status = GunshipStatus.Idle;
    Vector3 initialTargetPosition, overrideTargetDestination, gattlingTargetPosition, positionToShootAt, satellitePosition;
    int orbitEscapeFrame;
    int okToFireHowitzerCounter;
    uint afterburnerSound;

    public Vector3 AttackAreaCenter => initialTargetPosition;
    public Vector3 ReticlePosition => overrideTargetDestination;
    public bool ShowsDecals => status is GunshipStatus.Inserting or GunshipStatus.Orbiting;

    float AttackAreaRadius => FloatSetting("AttackAreaRadius", 200);
    float TargetingReticleRadius => FloatSetting("TargetingReticleRadius", 25);
    float GunshipOrbitRadius => FloatSetting("GunshipOrbitRadius", 250);

    public void OnCreate()
    {
        if (Game.Catalog.SpecialPower(Setting("SpecialPowerTemplate")) is { } power)
            specialPowerModule = SpecialPowerRules.ModuleFor(Owner, power);
        satellitePosition = Owner.Position;
    }

    public bool InitiateIntentToDoSpecialPower(SpecialPowerTemplate power, GameObject? target, Vector3? position, uint options)
    {
        if (specialPowerModule?.Power != power || position is not { } targetPos)
            return false;
        if ((options & ObjectCommandButtons.CommandFiredByScript) == 0)
        {
            initialTargetPosition = targetPos;
            overrideTargetDestination = targetPos;
            gattlingTargetPosition = targetPos;
        }
        else
        {
            specialPowerModule.SetReadyFrame(Frame);
            initialTargetPosition = targetPos;
            status = GunshipStatus.Inserting;
        }
        var gunship = Owner;
        if (gunship.AI is { } shipAi)
        {
            shipAi.SetLocomotorSet("SET_PANIC");
            if (shipAi.CurrentLocomotor is { } loco)
            {
                loco.AllowInvalidPosition = true;
                loco.UltraAccurate = true;
            }
        }
        gunship.SetCondition("DOOR_1_OPENING", false);
        gunship.SetCondition("DOOR_1_CLOSING");
        EnableAfterburners(true);
        status = GunshipStatus.Inserting;
        if (gunship.Contain is { } contain)
        {
            if (Game.FindObject(gattlingId) != null)
                gattlingId = 0;
            if (Game.Templates.Find(Setting("GattlingTemplateName")) is { } gattlingTemplate && gunship.Team is { } team)
            {
                var gattling = Game.CreateObject(gattlingTemplate, gunship.Owner, gunship.Position, 0, team);
                contain.AddToContain(gattling);
                gattlingId = gattling.Id;
            }
            Game.FindObject(gattlingId)?.SetDisabled("DISABLED_PARALYZED");
        }
        specialPowerModule.MarkSpecialPowerTriggered(initialTargetPosition);
        return true;
    }

    public bool DoesSpecialPowerHaveOverridableDestination => true;
    public bool DoesSpecialPowerHaveOverridableDestinationActive => status < GunshipStatus.Departing;

    public void SetSpecialPowerOverridableDestination(Vector3 location)
    {
        var me = Owner;
        if (me.IsDisabled)
            return;
        overrideTargetDestination = location;
        if (me.Owner.IsLocalPlayer)
            Game.Events.PlaySound(me.Template.UnitSound("VoiceAttack"), source: me);
    }

    bool IsPointOffMap(Vector3 p)
    {
        float border = Game.Terrain.Border * Formats.Maps.MapFile.CellSize;
        return p.X < -border || p.Y < -border || p.X > Game.Terrain.WorldWidth + border || p.Y > Game.Terrain.WorldHeight + border;
    }

    bool IsFairDistanceFromShip(GameObject target) =>
        new Vector2(Owner.Position.X - target.Position.X, Owner.Position.Y - target.Position.Y).Length() > GunshipOrbitRadius * 0.75f;

    /// <summary>Live, visible, attackable enemies around a point, nearest first (the original's filter chain).</summary>
    IEnumerable<GameObject> EnemiesAround(Vector3 center, float radius)
    {
        var gunship = Owner;
        return Game.ObjectsInRadius(center, radius + 50)
            .Where(o => !o.IsEffectivelyDead && o.IsOffMap == gunship.IsOffMap && gunship.RelationshipTo(o) == Relationship.Enemies)
            .Where(o => !(o.HasStatus(Status.Stealthed) && !o.HasStatus(Status.Detected)))
            .Where(o => !o.IsKindOf(Kind.Unattackable))
            .Where(o => Game.Shroud.StatusOf(o, gunship.Owner) <= Vision.ObjectShroud.PartialClear)
            .Where(o => new Vector2(o.Position.X - center.X, o.Position.Y - center.Y).Length() - o.BoundingCircleRadius <= radius)
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, center));
    }

    public int Update()
    {
        var gunship = Owner;
        if (gunship.IsEffectivelyDead)
            return LogicTime.SleepForever;
        var shipAi = gunship.AI;
        var gattling = Game.FindObject(gattlingId);
        var gattlingAi = gattling?.AI;

        if (status is GunshipStatus.Inserting or GunshipStatus.Orbiting)
        {
            var perigee = gunship.Position - initialTargetPosition;
            perigee.Z = 0;
            float distanceToTarget = perigee.Length();
            if (distanceToTarget > 0)
                perigee = Vector3.Normalize(perigee);
            var apogee = new Vector3(-perigee.Y, perigee.X, 0);
            float n1 = Math.Min(OrbitInsertionSlopeMax, Math.Max(OrbitInsertionSlopeMin, FloatSetting("OrbitInsertionSlope", 0.7f)));
            float n2 = 1 - n1;
            float orbitalRadius = GunshipOrbitRadius;
            var declination = new Vector3(perigee.X * n1 + apogee.X * n2, perigee.Y * n1 + apogee.Y * n2, 0) * orbitalRadius;
            satellitePosition.X = initialTargetPosition.X + declination.X;
            satellitePosition.Y = initialTargetPosition.Y + declination.Y;
            shipAi?.MoveTo(satellitePosition, CommandSource.AI);

            // Keep the reticle inside the attack area.
            float constraintRadius = AttackAreaRadius - TargetingReticleRadius;
            var overrideDelta = initialTargetPosition - overrideTargetDestination;
            if (overrideDelta.Length() > constraintRadius)
            {
                overrideDelta = Vector3.Normalize(overrideDelta) * constraintRadius;
                overrideTargetDestination.X = initialTargetPosition.X - overrideDelta.X;
                overrideTargetDestination.Y = initialTargetPosition.Y - overrideDelta.Y;
            }

            if (status == GunshipStatus.Inserting && distanceToTarget < orbitalRadius)
            {
                status = GunshipStatus.Orbiting;
                orbitEscapeFrame = Frame + FramesSetting("OrbitTime");
                gattling?.SetDisabled("DISABLED_PARALYZED", false);
                if (shipAi != null)
                {
                    shipAi.SetLocomotorSet("SET_NORMAL");
                    if (shipAi.CurrentLocomotor is { } loco)
                    {
                        loco.AllowInvalidPosition = true;
                        loco.UltraAccurate = true;
                    }
                }
                gunship.SetCondition("DOOR_1_CLOSING", false);
                gunship.SetCondition("DOOR_1_OPENING");
                EnableAfterburners(false);
            }
        }

        if (status == GunshipStatus.Orbiting)
        {
            if (Frame >= orbitEscapeFrame)
            {
                CleanUp();
                status = GunshipStatus.Departing;
                DisengageAndDepartAO(gunship);
            }
            else
            {
                int rate = Math.Max(1, FramesSetting("HowitzerFiringRate", 10 * LogicTime.MillisecondsPerFrame));
                if (Frame % rate < 1)
                {
                    positionToShootAt = overrideTargetDestination;
                    var validTarget = EnemiesAround(overrideTargetDestination, TargetingReticleRadius).FirstOrDefault(IsFairDistanceFromShip);
                    if (!gunship.Owner.IsHuman && validTarget == null)
                    {
                        // A computer player hunts the whole area.
                        validTarget = EnemiesAround(initialTargetPosition, AttackAreaRadius).FirstOrDefault(IsFairDistanceFromShip);
                        if (validTarget != null)
                            positionToShootAt = validTarget.Position;
                    }
                    if (gattlingAi != null)
                    {
                        if (validTarget != null)
                            gattlingAi.AttackObject(validTarget, CommandSource.AI, LotsOfShots);
                        else
                            gattlingAi.AttackPosition(gattlingTargetPosition, CommandSource.AI, LotsOfShots);
                    }
                    if (okToFireHowitzerCounter > FramesSetting("HowitzerFollowLag"))
                    {
                        string howitzer = Setting("HowitzerWeaponTemplate");
                        if (howitzer.Length > 0)
                        {
                            float offs = FloatSetting("RandomOffsetForHowitzer", 20);
                            var at = new Vector3(gattlingTargetPosition.X + Game.Random.Int((int)-offs, (int)offs),
                                gattlingTargetPosition.Y + Game.Random.Int((int)-offs, (int)offs), gattlingTargetPosition.Z);
                            Game.Damage.FireTemporaryWeapon(howitzer, gunship, at);
                            Game.Events.PlaySound(gunship.Template.UnitSound("HowitzerFire"), source: gunship);
                        }
                    }
                }

                // The gattling's aim walks toward where it should shoot, StrafingIncrement at a time.
                string strafe = Setting("GattlingStrafeFXParticleSystem");
                if (gattling != null && gattling.HasStatus(Status.IsFiringWeapon))
                {
                    var delta = positionToShootAt - gattlingTargetPosition;
                    float increment = FloatSetting("StrafingIncrement", 20);
                    if (delta.Length() < increment)
                    {
                        gattlingTargetPosition = positionToShootAt;
                        ++okToFireHowitzerCounter;
                    }
                    else
                    {
                        okToFireHowitzerCounter = 0;
                        gattlingTargetPosition += Vector3.Normalize(delta) * increment;
                    }
                    if (strafe.Length > 0 && Game.Players.Local is { } local && Game.Shroud.StatusOf(gunship, local) <= Vision.ObjectShroud.PartialClear)
                    {
                        var impact = new Vector3(gattlingTargetPosition.X + Random.Shared.NextSingle() * 10 - 5, gattlingTargetPosition.Y + Random.Shared.NextSingle() * 10 - 5, 0);
                        impact.Z = Game.Terrain.GroundHeight(impact);
                        Game.Events.ParticleSystem(strafe, impact);
                    }
                }
            }
        }
        else if (status == GunshipStatus.Departing && IsPointOffMap(gunship.Position))
        {
            Game.DestroyObject(gunship);
            status = GunshipStatus.Idle;
            CleanUp();
        }
        return 0;
    }

    void EnableAfterburners(bool on)
    {
        var gunship = Owner;
        gunship.SetCondition("JETAFTERBURNER", on);
        if (on)
        {
            if (afterburnerSound == 0)
                afterburnerSound = Game.Events.PlayHandledSound(gunship.Template.UnitSound("Afterburner"), gunship);
        }
        else
        {
            Game.Events.StopHandledSound(afterburnerSound);
            afterburnerSound = 0;
        }
    }

    void CleanUp()
    {
        if (Game.FindObject(gattlingId) is { } gattling)
            Game.DestroyObject(gattling);
    }

    void DisengageAndDepartAO(GameObject gunship)
    {
        if (gunship.AI is { } shipAi)
        {
            var forward = new Vector3(MathF.Cos(gunship.Orientation), MathF.Sin(gunship.Orientation), 0);
            shipAi.MoveTo(gunship.Position + forward * 99999f, CommandSource.AI);
        }
        Game.FindObject(gattlingId)?.SetDisabled("DISABLED_PARALYZED");
        if (gunship.AI is { } ai)
        {
            ai.SetLocomotorSet("SET_PANIC");
            if (ai.CurrentLocomotor is { } loco)
            {
                loco.AllowInvalidPosition = true;
                loco.UltraAccurate = true;
            }
        }
        gunship.SetCondition("DOOR_1_OPENING", false);
        gunship.SetCondition("DOOR_1_CLOSING");
        EnableAfterburners(true);
        CleanUp();
    }
}

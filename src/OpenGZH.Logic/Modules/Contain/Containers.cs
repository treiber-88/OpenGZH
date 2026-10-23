using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Stealth;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Modules.Contain;

/// <summary>
/// Vehicles and aircraft that carry units (the original TransportContain): Humvees, Chinooks, the
/// Battle Bus, cargo planes. Capacity is in transport slots; riders are held (DISABLED_HELD) and may
/// heal (HealthRegen%PerSec). InitialPayload fills it when it first appears.
/// </summary>
public class TransportContain : ContainModule
{
    int slotCapacity;
    int extraSlotsInUse;
    int frameExitNotBusy;
    bool payloadCreated;
    string exitBone = "";
    bool orientLikeContainerOnExit, keepContainerVelocityOnExit, goAggressiveOnExit, resetMoodCheckTimeOnExit;
    bool destroyRidersWhoAreNotFreeToExit, armedRidersUpgradeWeaponSet, delayExitInAir;
    float exitPitchRate, healthRegenPercentPerSec;
    int exitDelay;
    string initialPayloadName = "";
    int initialPayloadCount;

    public override int ContainMax => slotCapacity;
    public override int ExtraSlotsInUse => extraSlotsInUse;

    protected override void InitializeContain()
    {
        slotCapacity = IntSetting("Slots");
        exitBone = Setting("ExitBone");
        orientLikeContainerOnExit = BoolSetting("OrientLikeContainerOnExit");
        keepContainerVelocityOnExit = BoolSetting("KeepContainerVelocityOnExit");
        goAggressiveOnExit = BoolSetting("GoAggressiveOnExit");
        resetMoodCheckTimeOnExit = BoolSetting("ResetMoodCheckTimeOnExit", true);
        destroyRidersWhoAreNotFreeToExit = BoolSetting("DestroyRidersWhoAreNotFreeToExit");
        exitPitchRate = RuleUnits.AngularVelocity(Setting("ExitPitchRate"));
        healthRegenPercentPerSec = FloatSetting("HealthRegen%PerSec");
        exitDelay = FramesSetting("ExitDelay");
        armedRidersUpgradeWeaponSet = BoolSetting("ArmedRidersUpgradeMyWeaponSet");
        delayExitInAir = BoolSetting("DelayExitInAir");
        string[] payload = RuleValue.Words(Setting("InitialPayload"));
        if (payload.Length > 0)
        {
            initialPayloadName = payload[0];
            initialPayloadCount = payload.Length > 1 ? RuleValue.Int(payload[1], 1) : 1;
        }
        InitializeTransport();
    }

    protected virtual void InitializeTransport() { }

    public override bool IsValidContainerFor(GameObject rider, bool checkCapacity)
    {
        // A parachute counts as its occupant.
        if (rider.Contain is { IsSpecialZeroSlotContainer: true } chute && chute.Passengers.FirstOrDefault() is { } inside)
            rider = inside;
        if (!base.IsValidContainerFor(rider, checkCapacity))
            return false;
        if (rider.Owner != Owner.Owner)
            return false;
        int slots = SlotsFor(rider);
        if (slots == 0)
            return false;
        return !checkCapacity || extraSlotsInUse + ContainCount + slots <= ContainMax;
    }

    protected override void OnContaining(GameObject rider)
    {
        base.OnContaining(rider);
        rider.SetDisabled("DISABLED_HELD");
        extraSlotsInUse += SlotsFor(rider) - 1;
        if (ContainCount == 1)
            Owner.SetCondition("LOADED");
        if (armedRidersUpgradeWeaponSet)
            LetRidersUpgradeWeaponSet();
    }

    protected override void OnRemoving(GameObject rider)
    {
        base.OnRemoving(rider);
        rider.SetDisabled("DISABLED_HELD", false);
        if (exitBone.Length > 0 && Game.Bones.TryGetBone(Owner, exitBone, out var bone))
            rider.SetPosition(bone.Translation);
        if (orientLikeContainerOnExit)
            rider.SetOrientation(Owner.Orientation);
        if (keepContainerVelocityOnExit && Owner.FindModule<PhysicsBehavior>() is { } parent && rider.FindModule<PhysicsBehavior>() is { } child)
        {
            child.ApplyMotiveForce(parent.Velocity * child.Mass);
            child.PitchRate = child.CenterOfMassOffset * exitPitchRate;
        }
        extraSlotsInUse -= SlotsFor(rider) - 1;
        if (ContainCount == 0)
            Owner.SetCondition("LOADED", false);
        if (Owner.IsAboveTerrain && rider.FindModule<PhysicsBehavior>() is { } physics)
            physics.AllowToFall = true;
        if (Owner.IsEffectivelyDead)
            ScatterToNearbyPosition(rider);
        frameExitNotBusy = Frame + exitDelay;
        if (armedRidersUpgradeWeaponSet)
            LetRidersUpgradeWeaponSet();
        if (goAggressiveOnExit && rider.AI is { } exitAi)
            exitAi.Aggressive = true;
        if (resetMoodCheckTimeOnExit)
            rider.AI?.WakeAndAttemptToTarget();
    }

    /// <summary>The Battle Bus: armed infantry inside switch it to its PLAYER_UPGRADE weapons.</summary>
    void LetRidersUpgradeWeaponSet()
    {
        bool armed = Passengers.Any(rider => rider.IsKindOf(Kind.Infantry)
            && rider.Weapons.All.Any(w => !w.Template.IsContactWeapon && w.Template.PrimaryDamage > 0));
        Owner.SetWeaponSetFlag("PLAYER_UPGRADE", armed);
    }

    void CreatePayload()
    {
        payloadCreated = true;
        if (initialPayloadName.Length == 0 || Game.Templates.Find(initialPayloadName) is not { } template)
            return;
        EnableLoadSounds(false);
        for (int i = 0; i < initialPayloadCount; i++)
        {
            var payload = Game.CreateObject(template, Owner.Owner, Owner.Position, Owner.Orientation, Owner.Owner.DefaultTeam);
            if (IsValidContainerFor(payload, true))
                AddToContain(payload);
            else
                Game.DestroyObject(payload);
        }
        EnableLoadSounds(true);
    }

    public override int Update()
    {
        if (!payloadCreated)
            CreatePayload();
        if (healthRegenPercentPerSec > 0)
        {
            foreach (var rider in Passengers.ToList())
                if (rider.Body is { } body && body.Health < body.MaxHealth)
                    body.AttemptHealing(body.MaxHealth * healthRegenPercentPerSec / 100f * LogicTime.SecondsPerFrame, Owner.Id);
        }
        return base.Update();
    }

    protected override void KillRidersWhoAreNotFreeToExit()
    {
        foreach (var rider in Passengers.ToList())
        {
            if (IsSpecificRiderFreeToExit(rider))
                continue;
            if (destroyRidersWhoAreNotFreeToExit)
                Game.DestroyObject(rider);
            else
                rider.Kill();
        }
    }

    /// <summary>Riders may get out unless the transport says no (a plane before it opens its doors), or they couldn't stand where it is.</summary>
    protected virtual bool IsSpecificRiderFreeToExit(GameObject rider)
    {
        if (Owner.AI is { } ai && !ai.IsFreeToExit(rider))
            return false;
        if (Owner.AI?.CurrentLocomotor is { } myLocomotor && (myLocomotor.Surfaces & Locomotion.Surfaces.Air) != 0)
            return true;
        if (rider.AI?.CurrentLocomotor is not { } theirs)
            return false;
        return Game.Pathfinder.IsValidMovementTerrain(Owner.Position, theirs.Surfaces);
    }

    public override bool ReserveDoorForExit(GameObject rider) => IsSpecificRiderFreeToExit(rider);

    public override bool IsExitBusy => delayExitInAir && Owner.IsAboveTerrain || Frame < frameExitNotBusy;

    public override bool IsPassengerAllowedToFire(GameObject passenger)
    {
        if (!passenger.IsKindOf(Kind.Infantry))
            return false;
        if (Game.FindObject(Owner.ContainedById)?.Contain is { IsSpecialOverlordStyleContainer: true } overlord)
            return overlord.IsPassengerAllowedToFire(passenger);
        return base.IsPassengerAllowedToFire(passenger);
    }

    public override void OnCapture(Player oldOwner, Player newOwner)
    {
        if (oldOwner == newOwner)
            return;
        if (Owner.IsDisabledBy("DISABLED_UNMANNED"))
            RemoveAllContained();
        else
            OrderAllPassengersToExit(CommandSource.AI, false);
    }
}

/// <summary>Infantry that hack the internet for money inside a building (China's Internet Center).</summary>
public sealed class InternetHackContain : TransportContain
{
    protected override void OnContaining(GameObject rider)
    {
        base.OnContaining(rider);
        rider.AI?.HackInternet(CommandSource.AI);
    }
}

/// <summary>
/// A parachute (the original ParachuteContain): drops in free fall, opens after ParachuteOpenDist,
/// then floats down swaying, steering to its landing point. On touching the ground the soldier (or
/// crate) is let go; landing in water or on a cliff kills them; a parachute killed high up lets its
/// rider fall (FreeFallDamagePercent).
/// </summary>
public sealed class ParachuteContain : ContainModule
{
    const float NoStartZ = 1e10f;

    float pitchRateMax, rollRateMax, lowAltitudeDamping, paraOpenDist, killWhenLandingInWaterSlop, freeFallDamagePercent;
    bool opened;
    float pitch, roll, pitchRate, rollRate;
    float startZ = NoStartZ;
    Vector3? landingOverride;

    public override bool IsSpecialZeroSlotContainer => true;
    public override bool IsEnclosingContainerFor(GameObject? obj) => false;

    /// <summary>The parachute's sway (the client tilts the parachute and rider by it).</summary>
    public (float Pitch, float Roll) Sway => (pitch, roll);
    public bool IsOpen => opened;

    protected override void InitializeContain()
    {
        pitchRateMax = RuleUnits.AngularVelocity(Setting("PitchRateMax"));
        rollRateMax = RuleUnits.AngularVelocity(Setting("RollRateMax"));
        lowAltitudeDamping = FloatSetting("LowAltitudeDamping");
        paraOpenDist = FloatSetting("ParachuteOpenDist");
        killWhenLandingInWaterSlop = FloatSetting("KillWhenLandingInWaterSlop");
        freeFallDamagePercent = FloatSetting("FreeFallDamagePercent");
        pitchRate = Game.Random.Float(-pitchRateMax, pitchRateMax);
        rollRate = Game.Random.Float(-rollRateMax, rollRateMax);
    }

    public override void OnCreate()
    {
        base.OnCreate();
        Owner.SetStatus(Flags.ObjectStatus.IndexOf("PARACHUTING"));
    }

    public override bool IsValidContainerFor(GameObject rider, bool checkCapacity)
    {
        if (!base.IsValidContainerFor(rider, checkCapacity))
            return false;
        if (SlotsFor(rider) == 0 && !rider.IsKindOf(Kind.Infantry) && !rider.IsKindOf("PARACHUTABLE"))
            return false;
        return ContainCount == 0;
    }

    /// <summary>Land exactly here (a paradrop's target) instead of wherever the wind takes it.</summary>
    public void SetOverrideDestination(Vector3 destination) => landingOverride = destination;

    public override FlagSet DisabledTypesToProcess => DisabledMask.Held;

    public override int Update()
    {
        base.Update();
        var parachute = Owner;
        if (parachute.IsDisabledBy("DISABLED_HELD"))
            return 0; // still in the plane

        var rider = Passengers.FirstOrDefault();
        if (startZ == NoStartZ)
        {
            startZ = parachute.Position.Z;
            float ground = Game.Terrain.GroundHeight(parachute.Position);
            if (startZ - ground < 2 * paraOpenDist)
                startZ = ground + 2 * paraOpenDist;
        }

        if (!opened)
        {
            if (MathF.Abs(startZ - parachute.Position.Z) >= paraOpenDist)
            {
                opened = true;
                parachute.SetCondition("FREEFALL", false);
                parachute.SetCondition("PARACHUTING");
                if (rider != null)
                {
                    rider.SetCondition("FREEFALL", false);
                    rider.SetCondition("PARACHUTING");
                    Game.Events.PlaySound(Setting("ParachuteOpenSound"), rider.Position, source: rider);
                }
                if (parachute.AI is { } ai)
                {
                    var target = parachute.Position;
                    if (landingOverride is { } over)
                    {
                        target = over;
                        if (ai.CurrentLocomotor is { } locomotor)
                            locomotor.UltraAccurate = true;
                    }
                    else if (Game.Pathfinder.NearestPassable(target, Locomotion.Surfaces.Ground, 10) is { } free)
                    {
                        target = free;
                    }
                    ai.MoveTo(target, CommandSource.AI);
                }
            }
            else if (rider != null)
            {
                rider.SetCondition("PARACHUTING", false);
                rider.SetCondition("FREEFALL");
            }
        }

        // Collisions only once open and carrying someone.
        bool collide = opened && ContainCount > 0;
        parachute.SetStatus(Core.Status.NoCollisions, !collide);
        rider?.SetStatus(Core.Status.NoCollisions, !collide);

        if (parachute.AI is { } chuteAi && !parachute.IsEffectivelyDead)
        {
            chuteAi.SetLocomotorSet(opened ? "SET_NORMAL" : "SET_FREEFALL");
            if (chuteAi.CurrentLocomotor is { } locomotor)
            {
                float altitudeDamping = rider != null && rider.HeightAboveTerrain <= 20 ? lowAltitudeDamping : 0;
                if (opened)
                {
                    // A damped spring keeps the parachute swaying.
                    var t = locomotor.Template;
                    pitchRate += -t.PitchStiffness * pitch + -(t.PitchDamping + altitudeDamping) * pitchRate;
                    rollRate += -t.RollStiffness * roll + -(t.RollDamping + altitudeDamping) * rollRate;
                    pitch += pitchRate;
                    roll += rollRate;
                    if (landingOverride != null)
                    {
                        locomotor.CloseEnoughDistance = 10;
                        locomotor.IsCloseEnoughDist3D = false;
                    }
                }
                PositionRiders();
            }
        }

        if (ContainCount == 0)
            parachute.Kill();
        if (!parachute.IsEffectivelyDead && Game.Terrain.IsUnderwater(parachute.Position.X, parachute.Position.Y, out float waterZ)
            && parachute.Position.Z - waterZ < killWhenLandingInWaterSlop)
            parachute.Kill();
        return 0;
    }

    /// <summary>The rider hangs below the parachute's attach bone.</summary>
    void PositionRiders()
    {
        foreach (var rider in Passengers.ToList())
            PositionRider(rider);
    }

    void PositionRider(GameObject rider)
    {
        var attach = Game.Bones.TryGetBone(Owner, "PARA_ATTCH", out var a) ? a.Translation - Owner.Position : Vector3.Zero;
        var riderAttach = Game.Bones.TryGetBone(rider, "PARA_MAN", out var m) ? m.Translation - rider.Position : new Vector3(0, 0, rider.Height);
        var pos = Owner.Position + attach - riderAttach;
        rider.SetPosition(pos);
        float alt = rider.HeightAboveTerrain;
        if (alt < 0)
            rider.SetPosition(pos with { Z = pos.Z - alt });
        rider.SetOrientation(Owner.Orientation);
        bool held = rider.IsDisabledBy("DISABLED_HELD");
        rider.Pitch = held ? -pitch : 0;
        rider.Roll = held ? roll : 0;
        Owner.Pitch = -pitch;
        Owner.Roll = roll;
    }

    public override void RedeployOccupants() => PositionRiders();

    protected override void OnContaining(GameObject rider)
    {
        base.OnContaining(rider);
        rider.SetDisabled("DISABLED_HELD");
        rider.SetStatus(Flags.ObjectStatus.IndexOf("PARACHUTING"));
        rider.SetCondition("PARACHUTING", false);
        rider.SetCondition("FREEFALL");
        PositionRider(rider);
    }

    protected override void OnRemoving(GameObject rider)
    {
        base.OnRemoving(rider);
        rider.SetDisabled("DISABLED_HELD", false);
        rider.SetStatus(Flags.ObjectStatus.IndexOf("PARACHUTING"), false);
        Owner.SetStatus(Core.Status.NoCollisions);
        PositionRider(rider);
        rider.SetCondition("FREEFALL", false);
        rider.SetCondition("PARACHUTING", false);
        if (rider.FindModule<PhysicsBehavior>() is { } physics)
        {
            physics.AllowToFall = true;
            physics.ApplyForce(Vector3.Zero); // wake it up
        }

        if (rider.AI is { } ai)
        {
            // Paratroopers from a factory's air drop walk to its rally point; computer players hunt.
            bool rallied = false;
            if (Game.FindObject(rider.ProducerId) is { } transport && Game.FindObject(transport.ProducerId) is { } factory
                && factory.FindModules<Production.IProductionExit>().Select(e => e.RallyPoint).FirstOrDefault(r => r != null) is { } rally)
            {
                ai.MoveTo(rally, CommandSource.AI);
                rallied = true;
            }
            if (!rallied)
                ai.Stop(CommandSource.AI);
        }

        var p = rider.Position;
        if (Game.Terrain.IsUnderwater(p.X, p.Y, out float waterZ) && p.Z <= waterZ + killWhenLandingInWaterSlop)
            rider.Damage(new DamageInfo { DamageType = "WATER", DeathType = "FLOODED", Amount = DamageInfo.HugeDamage });
        // Landing off the map, on a cliff or in impassable terrain is fatal.
        var (cx, cy) = Game.Terrain.CellAt(p.X, p.Y);
        if (!Game.Terrain.IsInsidePlayableArea(p.X, p.Y) || Game.Pathfinder.TypeAt(cx, cy) is Locomotion.Pathfinder.CellType.Cliff or Locomotion.Pathfinder.CellType.Water
            || !Game.Terrain.IsInsideGrid(cx, cy))
            rider.Kill();
    }

    public override void OnDie(DamageInfo killingBlow)
    {
        if (Owner.IsSignificantlyAboveTerrain && Passengers.FirstOrDefault() is { } rider)
        {
            RemoveAllContained();
            if (freeFallDamagePercent > 0 && rider.Body is { } body)
                rider.Damage(new DamageInfo { DamageType = "FALLING", DeathType = "SPLATTED", SourceId = killingBlow.SourceId, Amount = body.MaxHealth * freeFallDamagePercent });
            if (rider.FindModule<PhysicsBehavior>() is { } physics)
            {
                physics.AllowToFall = true;
                physics.IsInFreeFall = true;
                physics.ApplyForce(Vector3.Zero);
            }
        }
        base.OnDie(killingBlow);
    }

    public override void OnCollide(GameObject? other, Vector3 location, Vector3 normal)
    {
        // Touched the ground: let go.
        if (other == null && !Owner.IsContained)
        {
            RemoveAllContained();
            Owner.Kill();
        }
    }
}

/// <summary>
/// Buildings infantry can garrison (the original GarrisonContain): the building switches to the
/// occupants' side, soldiers move to the window (FIREPOINT bone) nearest their target and shoot out
/// with the GARRISONED bonus, and a building that gets really damaged throws them out (unless
/// GARRISONABLE_UNTIL_DESTROYED). Non-enclosing garrisons (e.g. the Fire Base) show the occupants at
/// STATION bones instead.
/// </summary>
public class GarrisonContain : ContainModule
{
    public override bool IsBustable => true;

    const int MaxPoints = 40;
    enum PointCondition { Pristine = 0, Damaged = 1, ReallyDamaged = 2 }

    sealed class PointData
    {
        public uint ObjectId;
        public uint TargetId;
        public int PlaceFrame;
    }

    bool mobileGarrison, healObjects, immuneToClearBuildingAttacks, isEnclosing;
    float framesForFullHeal;
    string initialRosterName = "";
    int initialRosterCount;

    readonly Vector3[,] points = new Vector3[3, MaxPoints];
    readonly PointData[] pointData = Enumerable.Range(0, MaxPoints).Select(_ => new PointData()).ToArray();
    int pointsInUse;
    bool pointsLoaded, stationPointsLoaded;
    readonly List<(Vector3 Position, uint OccupantId)> stationPoints = new();
    Team? originalTeam;
    bool hideGarrisonedFromNonAllies;

    public override bool IsGarrisonable => true;
    public override bool IsImmuneToClearBuildingAttacks => immuneToClearBuildingAttacks;
    public override bool IsEnclosingContainerFor(GameObject? obj) => isEnclosing;

    protected override void InitializeContain()
    {
        mobileGarrison = BoolSetting("MobileGarrison");
        healObjects = BoolSetting("HealObjects");
        framesForFullHeal = Math.Max(1, FloatSetting("TimeForFullHeal", 1) / LogicTime.MillisecondsPerFrame);
        immuneToClearBuildingAttacks = BoolSetting("ImmuneToClearBuildingAttacks");
        isEnclosing = BoolSetting("IsEnclosingContainer", true);
        string[] roster = RuleValue.Words(Setting("InitialRoster"));
        if (roster.Length > 0)
        {
            initialRosterName = roster[0];
            initialRosterCount = roster.Length > 1 ? RuleValue.Int(roster[1], 1) : 1;
        }
    }

    public override bool IsValidContainerFor(GameObject obj, bool checkCapacity)
    {
        // Default AllowInsideKindOf for garrisons is INFANTRY.
        if (Setting("AllowInsideKindOf").Length == 0 && !obj.IsKindOf(Kind.Infantry))
            return false;
        if (!base.IsValidContainerFor(obj, checkCapacity))
            return false;
        if (Owner.Body is { } body && (body.Health <= 0 || body.DamageState == BodyDamageState.ReallyDamaged && !Owner.IsKindOf("GARRISONABLE_UNTIL_DESTROYED")))
            return false;
        if (obj.IsKindOf("NO_GARRISON"))
            return false;
        return !checkCapacity || ContainCount < ContainMax;
    }

    public override void OnCreate()
    {
        base.OnCreate();
        if (initialRosterName.Length == 0 || Game.Templates.Find(initialRosterName) is not { } template)
            return;
        for (int i = 0; i < initialRosterCount; i++)
        {
            var occupant = Game.CreateObject(template, Owner.Owner, Owner.Position, 0, Owner.Owner.DefaultTeam);
            if (IsValidContainerFor(occupant, true))
                AddToContain(occupant);
            else
                Game.DestroyObject(occupant);
        }
    }

    PointCondition ConditionIndex => (Owner.Body?.DamageState ?? BodyDamageState.Pristine) switch
    {
        BodyDamageState.Pristine => PointCondition.Pristine,
        BodyDamageState.Damaged => PointCondition.Damaged,
        _ => PointCondition.ReallyDamaged,
    };

    /// <summary>Reads the FIREPOINT bones for each damage state (the model differs per state).</summary>
    void LoadGarrisonPoints()
    {
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < MaxPoints; i++)
                points[c, i] = Owner.Position;
        var original = Owner.ModelConditions;
        (string[] Clear, string Set)[] states =
        [
            (["REALLYDAMAGED", "RUBBLE", "SPECIAL_DAMAGED", "DAMAGED"], "GARRISONED"),
            (["REALLYDAMAGED", "RUBBLE", "SPECIAL_DAMAGED"], "DAMAGED"),
            (["RUBBLE", "SPECIAL_DAMAGED", "DAMAGED"], "REALLYDAMAGED"),
        ];
        for (int c = 0; c < 3; c++)
        {
            foreach (string clear in states[c].Clear)
                Owner.SetCondition(clear, false);
            Owner.SetCondition(states[c].Set);
            var bones = Game.Bones.NumberedBones(Owner, "FIREPOINT", MaxPoints);
            for (int i = 0; i < bones.Count; i++)
                points[c, i] = bones[i].Translation;
        }
        Owner.ReplaceModelConditions(original);
        pointsLoaded = true;
    }

    void LoadStationPoints()
    {
        var original = Owner.ModelConditions;
        foreach (string clear in new[] { "REALLYDAMAGED", "RUBBLE", "SPECIAL_DAMAGED", "DAMAGED" })
            Owner.SetCondition(clear, false);
        Owner.SetCondition("GARRISONED");
        stationPoints.Clear();
        foreach (var bone in Game.Bones.NumberedBones(Owner, "STATION", ContainMax))
            stationPoints.Add((bone.Translation, 0));
        Owner.ReplaceModelConditions(original);
        stationPointsLoaded = true;
    }

    int ClosestFreePoint(PointCondition condition, Vector3 target)
    {
        if (pointsInUse == MaxPoints)
            return -1;
        int best = -1;
        float bestDist = -1;
        for (int i = 0; i < MaxPoints; i++)
        {
            if (pointData[i].ObjectId != 0)
                continue;
            float d = Vector3.DistanceSquared(target, points[(int)condition, i]);
            if (d < bestDist || bestDist < 0)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    int PointIndexOf(GameObject obj) => Array.FindIndex(pointData, p => p.ObjectId == obj.Id);

    void PutAtPoint(GameObject obj, uint targetId, PointCondition condition, int index)
    {
        if (index < 0 || pointData[index].ObjectId != 0)
            return;
        obj.SetPosition(points[(int)condition, index]);
        pointData[index].ObjectId = obj.Id;
        pointData[index].TargetId = targetId;
        pointData[index].PlaceFrame = Frame;
        pointsInUse++;
    }

    void PutAtBestPoint(GameObject obj, GameObject? target, Vector3? targetPos)
    {
        var aim = target?.Position ?? targetPos;
        if (aim == null || PointIndexOf(obj) >= 0)
            return;
        var condition = ConditionIndex;
        PutAtPoint(obj, target?.Id ?? 0, condition, ClosestFreePoint(condition, aim.Value));
    }

    void RemoveFromPoint(GameObject obj, int index = -1)
    {
        if (!isEnclosing)
            return;
        if (index < 0)
            index = PointIndexOf(obj);
        if (index < 0)
            return;
        pointData[index] = new PointData();
        pointsInUse--;
        obj.SetPosition(Owner.Position);
    }

    public override bool AttemptBestFirePointPosition(GameObject source, Weapon weapon, GameObject? victim, Vector3? position)
    {
        if (!pointsLoaded)
            LoadGarrisonPoints();
        int existing = PointIndexOf(source);
        if (existing >= 0)
            RemoveFromPoint(source, existing);
        PutAtBestPoint(source, victim, position);
        bool inRange = victim != null ? weapon.IsInRange(victim) : weapon.IsInRange(position!.Value);
        if (inRange)
            return true;
        existing = PointIndexOf(source);
        if (existing >= 0)
            RemoveFromPoint(source, existing);
        return false;
    }

    void MatchObjectsToPoints()
    {
        if (!isEnclosing)
        {
            if (!stationPointsLoaded)
                LoadStationPoints();
            foreach (var occupant in Passengers)
            {
                int spot = stationPoints.FindIndex(s => s.OccupantId == occupant.Id);
                if (spot < 0)
                {
                    spot = stationPoints.FindIndex(s => s.OccupantId == 0);
                    if (spot < 0)
                        continue;
                    stationPoints[spot] = (stationPoints[spot].Position, occupant.Id);
                }
                occupant.SetPosition(stationPoints[spot].Position);
            }
            return;
        }
        if (!pointsLoaded)
            LoadGarrisonPoints();

        // Soldiers no longer shooting (or out of range) step back from the windows...
        for (int i = 0; i < MaxPoints; i++)
        {
            if (Game.FindObject(pointData[i].ObjectId) is not { } occupant)
                continue;
            bool valid = true;
            if (Game.FindObject(occupant.AI?.TargetId ?? 0) is { } goal && occupant.Weapons.Current is { } weapon && !weapon.IsInRange(goal))
                valid = false;
            if (!occupant.HasStatus(Core.Status.IsAttacking) || !valid)
                RemoveFromPoint(occupant, i);
        }
        // ...and soldiers with targets take the window nearest them.
        var condition = ConditionIndex;
        foreach (var occupant in Passengers)
        {
            if (occupant.AI is not { } ai)
                continue;
            var victim = Game.FindObject(ai.TargetId);
            Vector3? victimPos = victim == null && ai.State == AI.AIState.AttackingPosition ? ai.TargetPosition : null;
            if (victim == null && victimPos == null)
                continue;
            int index = PointIndexOf(occupant);
            if (index < 0)
            {
                PutAtBestPoint(occupant, victim, victimPos);
                continue;
            }
            var aim = victim?.Position ?? victimPos!.Value;
            int better = ClosestFreePoint(condition, aim);
            if (better >= 0 && Vector3.DistanceSquared(aim, points[(int)condition, better]) < Vector3.DistanceSquared(aim, occupant.Position))
            {
                RemoveFromPoint(occupant, index);
                PutAtPoint(occupant, victim?.Id ?? 0, condition, better);
            }
        }
    }

    public override void RedeployOccupants()
    {
        stationPointsLoaded = false;
        pointsLoaded = false;
        for (int i = 0; i < MaxPoints; i++)
            pointData[i] = new PointData();
        pointsInUse = 0;
        MatchObjectsToPoints();
    }

    public override int Update()
    {
        base.Update();
        foreach (var occupant in Passengers.ToList())
            if (occupant.IsEffectivelyDead)
                RemoveFromContain(occupant);
        MatchObjectsToPoints();
        if (healObjects)
            foreach (var occupant in Passengers)
                HealSingleObject(occupant, framesForFullHeal);
        if (mobileGarrison && Owner.IsMobile)
            foreach (var occupant in Passengers)
                occupant.SetPosition(Owner.Position);
        return 0;
    }

    void HealSingleObject(GameObject obj, float frames)
    {
        if (obj.Body is not { } body)
            return;
        float amount = Frame - obj.ContainedByFrame >= frames ? body.MaxHealth : body.MaxHealth / frames;
        body.AttemptHealing(amount, Owner.Id);
    }

    /// <summary>The building takes the side of whoever is inside (and goes back when they leave).</summary>
    void RecalcApparentControllingPlayer()
    {
        originalTeam ??= Owner.Team;
        if (Passengers.FirstOrDefault() is { } rider)
        {
            hideGarrisonedFromNonAllies = !rider.HasStatus(Core.Status.Detected) && StealthUnitsContained == ContainCount;
            if (rider.Owner.DefaultTeam != Owner.Team)
                Owner.SetTeam(rider.Owner.DefaultTeam);
            Owner.SetCondition("GARRISONED");
        }
        else
        {
            if (originalTeam != null && originalTeam != Owner.Team)
                Owner.SetTeam(originalTeam);
            hideGarrisonedFromNonAllies = false;
            Owner.SetCondition("GARRISONED", false);
        }
    }

    /// <summary>A stealthy garrison still looks like its old owner's building to enemies.</summary>
    public Player ApparentOwnerFor(Player observer) =>
        hideGarrisonedFromNonAllies && originalTeam != null && !Owner.Owner.IsAllyOf(observer) ? originalTeam.Owner : Owner.Owner;

    protected override void OnContaining(GameObject obj)
    {
        base.OnContaining(obj);
        obj.SetDisabled("DISABLED_HELD");
        Owner.SetStatus(Flags.ObjectStatus.IndexOf("CAN_ATTACK"));
        obj.SetWeaponBonus("GARRISONED");
        if (isEnclosing)
            obj.SetPosition(Owner.Position);
        RecalcApparentControllingPlayer();
    }

    protected override void OnRemoving(GameObject obj)
    {
        base.OnRemoving(obj);
        if (isEnclosing)
        {
            RemoveFromPoint(obj);
        }
        else
        {
            int spot = stationPoints.FindIndex(s => s.OccupantId == obj.Id);
            if (spot >= 0)
                stationPoints[spot] = (stationPoints[spot].Position, 0);
            obj.SetPosition(obj.Position with { Z = Game.Terrain.GroundHeight(obj.Position) });
        }
        obj.SetWeaponBonus("GARRISONED", false);
        obj.SetDisabled("DISABLED_HELD", false);
        if (ContainCount == 0)
        {
            Owner.SetStatus(Flags.ObjectStatus.IndexOf("CAN_ATTACK"), false);
            hideGarrisonedFromNonAllies = false;
        }
        else if (StealthUnitsContained != ContainCount)
        {
            hideGarrisonedFromNonAllies = false;
        }
        RecalcApparentControllingPlayer();
    }

    public override void ExitObjectViaDoor(GameObject exiter)
    {
        RemoveFromContain(exiter);
        var start = Owner.Position;
        float angle = Owner.Orientation;
        if (exiter.AI?.CurrentLocomotor is { } locomotor && !Game.Pathfinder.IsValidMovementTerrain(start, locomotor.Surfaces))
        {
            // Step out of the front or back if the centre isn't walkable.
            float offset = Owner.Geometry.MajorRadius;
            start -= MathUtil.Forward(angle) * offset;
            if (!Game.Pathfinder.IsValidMovementTerrain(start, locomotor.Surfaces))
            {
                start += MathUtil.Forward(angle) * 2 * offset;
                if (!Game.Pathfinder.IsValidMovementTerrain(start, locomotor.Surfaces))
                    start = Owner.Position;
            }
        }
        if (isEnclosing)
            exiter.SetPosition(start with { Z = Game.Terrain.GroundHeight(start) });
        exiter.SetOrientation(angle);
        if (exiter.AI is { } ai)
        {
            var end = Game.Pathfinder.NearestPassable(start, ai.Surfaces) ?? start;
            ai.FollowWaypoints([end], CommandSource.AI);
        }
        RecalcApparentControllingPlayer();
    }

    public override void RemoveAllContained(bool exposeStealthUnits = false)
    {
        base.RemoveAllContained(exposeStealthUnits);
        RecalcApparentControllingPlayer();
    }

    public override void OnSelling()
    {
        RemoveAllContained(false);
        base.OnSelling();
    }

    public override bool IsPassengerAllowedToFire(GameObject passenger) => !Owner.IsDisabledBy("DISABLED_SUBDUED");

    public override void OnBodyDamageStateChange(BodyDamageState oldState, BodyDamageState newState)
    {
        if (newState == BodyDamageState.ReallyDamaged && !Owner.IsKindOf("GARRISONABLE_UNTIL_DESTROYED") && ContainCount > 0)
            OrderAllPassengersToExit(CommandSource.AI, false);
    }
}

/// <summary>
/// The Overlord and its relatives (the original OverlordContain): its one "rider" is the portable
/// structure on top (gattling cannon, bunker, propaganda tower). Once a bunker is on, the Overlord
/// passes everything about passengers on to the bunker's own container.
/// </summary>
public sealed class OverlordContain : TransportContain
{
    bool redirectionActivated;
    bool payloadCreated;
    bool experienceSinkForRider;

    public override bool IsSpecialOverlordStyleContainer => true;

    protected override void InitializeTransport() => experienceSinkForRider = BoolSetting("ExperienceSinkForRider", true);

    GameObject? PortableStructure => ContainList.Count > 0 ? Game.FindObject(ContainList[0]) : null;

    ContainModule? Redirected => redirectionActivated && PortableStructure?.Contain is { } inner ? inner : null;

    public override void OnCreate()
    {
        base.OnCreate();
        CreatePayload();
    }

    void CreatePayload()
    {
        EnableLoadSounds(false);
        foreach (string name in ListSetting("PayloadTemplateName").SelectMany(RuleValue.Words))
        {
            if (Game.Templates.Find(name) is not { } template)
                continue;
            var payload = Game.CreateObject(template, Owner.Owner, Owner.Position, Owner.Orientation, Owner.Team);
            if (IsValidContainerFor(payload, true))
                AddToContain(payload);
            else
                Game.DestroyObject(payload);
        }
        EnableLoadSounds(true);
        payloadCreated = true;
    }

    public override void OnBodyDamageStateChange(BodyDamageState oldState, BodyDamageState newState)
    {
        if (newState != BodyDamageState.Rubble && ContainList.Count == 1 && PortableStructure?.Body is { } body)
            body.SetDamageState(newState);
    }

    public override void OnDie(DamageInfo killingBlow)
    {
        if (Redirected == null)
        {
            base.OnDie(killingBlow);
            return;
        }
        redirectionActivated = false;
        PortableStructure?.Kill();
        base.OnDie(killingBlow);
    }

    public override void OnDelete()
    {
        if (Redirected is { } inner)
        {
            inner.RemoveAllContained();
            redirectionActivated = false;
            RemoveAllContained();
        }
        base.OnDelete();
    }

    public override void OnCapture(Player oldOwner, Player newOwner)
    {
        if (PortableStructure is { } top)
            top.SetTeam(newOwner.DefaultTeam);
    }

    public override bool IsGarrisonable => Redirected?.IsGarrisonable ?? false;
    public override bool IsKickOutOnCapture => Redirected?.IsKickOutOnCapture ?? false;
    public override bool IsDisplayedOnControlBar => Redirected?.IsDisplayedOnControlBar ?? false;

    public override void AddToContain(GameObject obj)
    {
        if (Redirected is { } inner)
            inner.AddToContain(obj);
        else
            base.AddToContain(obj);
    }

    public override void RemoveFromContain(GameObject obj, bool exposeStealthUnits = false)
    {
        if (Redirected is { } inner)
            inner.RemoveFromContain(obj, exposeStealthUnits);
        else
            base.RemoveFromContain(obj, exposeStealthUnits);
    }

    public override void RemoveAllContained(bool exposeStealthUnits = false)
    {
        if (Redirected is { } inner)
        {
            foreach (var rider in inner.Passengers.ToList())
                RemoveFromContain(rider, exposeStealthUnits);
            return;
        }
        base.RemoveAllContained(exposeStealthUnits);
    }

    public override void KillAllContained() => Redirected?.KillAllContained();

    protected override void OnContaining(GameObject obj)
    {
        base.OnContaining(obj);
        if (!obj.IsKindOf("PORTABLE_STRUCTURE"))
            return;
        redirectionActivated = true;
        if (experienceSinkForRider)
            obj.ExperienceSinkId = Owner.Id;
        if (Owner.HasStatus(Core.Status.Stealthed))
            obj.FindModule<StealthUpdate>()?.ReceiveGrant(true);
    }

    public override bool IsValidContainerFor(GameObject obj, bool checkCapacity) =>
        Redirected is { } inner ? inner.IsValidContainerFor(obj, checkCapacity) : base.IsValidContainerFor(obj, checkCapacity);

    public override int ContainCount => Redirected?.ContainCount ?? base.ContainCount;
    public override int ContainMax => Redirected?.ContainMax ?? base.ContainMax;
    public override IEnumerable<GameObject> Passengers => Redirected?.Passengers ?? base.Passengers;
    public override GameObject? Rider => PortableStructure;

    /// <summary>The portable structure on top rides in plain view; anything else is inside.</summary>
    public override bool IsEnclosingContainerFor(GameObject? obj) => !(ContainList.Count > 0 && obj != null && obj.Id == ContainList[0]);

    public override bool IsPassengerAllowedToFire(GameObject passenger)
    {
        if (!passenger.IsKindOf(Kind.Infantry) && !passenger.IsKindOf("PORTABLE_STRUCTURE"))
            return false;
        if (Owner.IsContained)
            return false;
        return PassengersAllowedToFire || base.IsPassengerAllowedToFire(passenger);
    }

    public override void RedeployOccupants()
    {
        // The structure on top sits on the Overlord, turning with it.
        if (PortableStructure is { } top)
        {
            top.SetPosition(Owner.Position);
            top.SetOrientation(Owner.Orientation);
        }
    }

    public bool PayloadCreated => payloadCreated;
}

/// <summary>
/// The Helix: carries infantry and one portable structure (a gattling cannon, bunker or
/// propaganda speaker) under it, which rides along at its position.
/// </summary>
public sealed class HelixContain : TransportContain
{
    uint portableStructureId;
    bool payloadCreated;

    GameObject? PortableStructure => Game.FindObject(portableStructureId);
    public override GameObject? Rider => PortableStructure;

    public override void OnCreate()
    {
        base.OnCreate();
        EnableLoadSounds(false);
        foreach (string name in ListSetting("PayloadTemplateName").SelectMany(RuleValue.Words))
        {
            if (Game.Templates.Find(name) is not { } template)
                continue;
            var payload = Game.CreateObject(template, Owner.Owner, Owner.Position, Owner.Orientation, Owner.Team);
            if (IsValidContainerFor(payload, true))
                AddToContain(payload);
            else
                Game.DestroyObject(payload);
        }
        EnableLoadSounds(true);
        payloadCreated = true;
    }

    public override int Update()
    {
        if (PortableStructure is { } portable)
        {
            portable.SetPosition(Owner.Position);
            portable.SetOrientation(Owner.Orientation);
        }
        return base.Update();
    }

    public override void RedeployOccupants()
    {
        var firePos = Owner.Position + new Vector3(0, 0, 8);
        foreach (var rider in base.Passengers)
            rider.SetPosition(firePos);
    }

    public override void OnBodyDamageStateChange(BodyDamageState oldState, BodyDamageState newState)
    {
        if (newState != BodyDamageState.Rubble && PortableStructure?.Body is { } body)
            body.SetDamageState(newState);
    }

    public override void OnDie(DamageInfo killingBlow)
    {
        PortableStructure?.Kill();
        base.OnDie(killingBlow);
    }

    public override void OnDelete()
    {
        if (PortableStructure is { } portable)
            Game.DestroyObject(portable);
        base.OnDelete();
    }

    public override void OnCapture(Player oldOwner, Player newOwner) => PortableStructure?.SetTeam(newOwner.DefaultTeam);

    public override void AddToContain(GameObject obj)
    {
        if (obj.IsKindOf("PORTABLE_STRUCTURE") && portableStructureId == 0)
        {
            portableStructureId = obj.Id;
            obj.OnContainedBy(Owner);
            OnContaining(obj);
            return;
        }
        base.AddToContain(obj);
    }

    public override void RemoveFromContain(GameObject obj, bool exposeStealthUnits = false)
    {
        if (obj.IsKindOf("PORTABLE_STRUCTURE") && obj.Id == portableStructureId)
        {
            portableStructureId = 0;
            return;
        }
        base.RemoveFromContain(obj, exposeStealthUnits);
    }

    public override bool IsValidContainerFor(GameObject obj, bool checkCapacity) =>
        obj.IsKindOf("PORTABLE_STRUCTURE") && portableStructureId == 0 || base.IsValidContainerFor(obj, checkCapacity);

    public override bool IsEnclosingContainerFor(GameObject? obj) => !(obj != null && obj.Id == portableStructureId) && base.IsEnclosingContainerFor(obj);

    public override bool IsPassengerAllowedToFire(GameObject passenger)
    {
        if (Owner.IsContained)
            return false;
        if (passenger.Id == portableStructureId && portableStructureId != 0)
            return true;
        return passenger.IsKindOf(Kind.Infantry) && base.IsPassengerAllowedToFire(passenger);
    }

    protected override void OnContaining(GameObject obj)
    {
        base.OnContaining(obj);
        obj.SetWeaponBonus("GARRISONED");
        obj.SetDisabled("DISABLED_HELD");
        if (obj.IsKindOf("PORTABLE_STRUCTURE") && Owner.HasStatus(Core.Status.Stealthed))
            obj.FindModule<StealthUpdate>()?.ReceiveGrant(true);
    }

    protected override void OnRemoving(GameObject obj)
    {
        base.OnRemoving(obj);
        obj.SetWeaponBonus("GARRISONED", false);
        obj.SetDisabled("DISABLED_HELD", false);
    }

    public bool PayloadCreated => payloadCreated;
}

/// <summary>
/// A tunnel network entrance (GLA): all of a player's tunnels share one set of occupants (the
/// player's TunnelTracker). Units heal inside; when the last tunnel goes, everyone inside dies.
/// </summary>
public sealed class TunnelContain : ContainModule
{
    public override bool IsBustable => true;

    bool registered;
    bool needToRunOnBuildComplete = true;
    float framesForFullHeal;

    TunnelTracker Tracker => Owner.Owner.Tunnels;

    public override bool IsTunnelContain => true;
    public override bool IsKickOutOnCapture => false;
    public override bool IsDisplayedOnControlBar => true;
    public override int ContainCount => Tracker.ContainCount;
    public override int ContainMax => Game.Catalog.GameData.MaxTunnelCapacity;
    public override IEnumerable<GameObject> Passengers => Tracker.Occupants;

    protected override void InitializeContain() =>
        framesForFullHeal = Math.Max(1, FloatSetting("TimeForFullHeal", 1) / LogicTime.MillisecondsPerFrame);

    protected override void AddToContainList(GameObject obj) => Tracker.Add(obj);

    public override bool IsValidContainerFor(GameObject obj, bool checkCapacity) =>
        !obj.IsKindOf("AIRCRAFT") && (!checkCapacity || ContainCount < ContainMax) && base.IsValidContainerFor(obj, false);

    public override void RemoveFromContain(GameObject obj, bool exposeStealthUnits = false)
    {
        if (!Tracker.Contains(obj))
            return;
        OnRemoving(obj);
        obj.OnRemovedFrom(Owner);
        Tracker.Remove(obj);
    }

    public override void RemoveAllContained(bool exposeStealthUnits = false)
    {
        foreach (var obj in Tracker.Occupants.ToList())
            RemoveFromContain(obj, exposeStealthUnits);
    }

    public override void KillAllContained()
    {
        foreach (var obj in Tracker.Occupants.ToList())
        {
            RemoveFromContain(obj, true);
            obj.Kill();
        }
    }

    protected override void OnContaining(GameObject obj)
    {
        base.OnContaining(obj);
        obj.SetDisabled("DISABLED_HELD");
    }

    protected override void OnRemoving(GameObject obj)
    {
        base.OnRemoving(obj);
        obj.SetDisabled("DISABLED_HELD", false);
        SetInWorld(obj, true);
        obj.SetPosition(Owner.Position);
        DoUnloadSound();
    }

    public override void OnSelling()
    {
        if (Tracker.TunnelCount == 1)
            RemoveAllContained(false);
        if (registered)
        {
            Tracker.OnTunnelDestroyed(Owner);
            registered = false;
        }
    }

    public override void OnDie(DamageInfo killingBlow)
    {
        if (!registered)
            return;
        Tracker.OnTunnelDestroyed(Owner);
        registered = false;
    }

    public override void OnDelete()
    {
        if (!registered)
            return;
        Tracker.OnTunnelDestroyed(Owner);
        registered = false;
    }

    public override void OnBuildComplete()
    {
        if (!needToRunOnBuildComplete)
            return;
        needToRunOnBuildComplete = false;
        Tracker.OnTunnelCreated(Owner);
        registered = true;
    }

    public override void OnCreate()
    {
        base.OnCreate();
        // Tunnels placed on the map (not built) count straight away.
        if (!Owner.IsUnderConstruction)
            OnBuildComplete();
    }

    public override void OnCapture(Player oldOwner, Player newOwner)
    {
        if (!registered)
            return;
        oldOwner.Tunnels.OnTunnelDestroyed(Owner);
        newOwner.Tunnels.OnTunnelCreated(Owner);
    }

    public override int Update()
    {
        base.Update();
        Tracker.HealOccupants(framesForFullHeal);
        // Remember who attacks the tunnel, so units inside can come out and fight them.
        if (Owner.Body is { } body && body.LastDamageFrame + LogicTime.FramesPerSecond > Frame
            && Game.FindObject(body.LastDamageSourceId) is { } attacker && Owner.Owner.IsEnemyOf(attacker.Owner))
            Tracker.UpdateNemesis(attacker);
        return 0;
    }
}

/// <summary>A player's tunnel network: who is inside, and which tunnels lead to it.</summary>
public sealed class TunnelTracker(GameLogic game)
{
    readonly List<uint> occupants = new();
    readonly List<uint> tunnels = new();
    uint nemesisId;
    int nemesisFrame;

    public int ContainCount => occupants.Count;
    public int TunnelCount => tunnels.Count;
    public IEnumerable<GameObject> Occupants => occupants.Select(game.FindObject).Where(o => o != null)!;
    public bool Contains(GameObject obj) => occupants.Contains(obj.Id);
    public void Add(GameObject obj) => occupants.Add(obj.Id);
    public void Remove(GameObject obj) => occupants.Remove(obj.Id);

    public void OnTunnelCreated(GameObject tunnel) => tunnels.Add(tunnel.Id);

    public void OnTunnelDestroyed(GameObject tunnel)
    {
        tunnels.Remove(tunnel.Id);
        if (tunnels.Count == 0)
        {
            // The last way out is gone.
            foreach (var obj in Occupants.ToList())
            {
                obj.OnRemovedFrom(tunnel);
                game.DestroyObject(obj);
            }
            occupants.Clear();
        }
        else if (game.FindObject(tunnels[0]) is { } valid)
        {
            foreach (var obj in Occupants)
                if (obj.ContainedById == tunnel.Id)
                    obj.OnContainedBy(valid);
        }
    }

    public void HealOccupants(float framesForFullHeal)
    {
        foreach (var obj in Occupants)
            if (obj.Body is { } body)
                body.AttemptHealing(game.Frame - obj.ContainedByFrame >= framesForFullHeal ? body.MaxHealth : body.MaxHealth / framesForFullHeal);
    }

    public void UpdateNemesis(GameObject target)
    {
        if (CurrentNemesis == null)
        {
            if (target.IsKindOf("VEHICLE") || target.IsKindOf(Kind.Structure) || target.IsKindOf(Kind.Infantry) || target.IsKindOf("AIRCRAFT"))
            {
                nemesisId = target.Id;
                nemesisFrame = game.Frame;
            }
        }
        else if (CurrentNemesis == target)
        {
            nemesisFrame = game.Frame;
        }
    }

    /// <summary>The last attacker of the network, for 4 seconds (ignoring hidden stealth units).</summary>
    public GameObject? CurrentNemesis
    {
        get
        {
            if (nemesisId == 0)
                return null;
            if (nemesisFrame + 4 * LogicTime.FramesPerSecond < game.Frame)
            {
                nemesisId = 0;
                return null;
            }
            var target = game.FindObject(nemesisId);
            if (target != null && target.HasStatus(Core.Status.Stealthed) && !target.HasStatus(Core.Status.Detected) && !target.HasStatus(Core.Status.Disguised))
                target = null;
            if (target is { IsEffectivelyDead: true })
                target = null;
            if (target == null)
                nemesisId = 0;
            return target;
        }
    }
}

/// <summary>Buildings that heal the units inside and then send them out (e.g. a hospital or repair bay).</summary>
public sealed class HealContain : ContainModule
{
    int framesForFullHeal;

    public override bool IsHealContain => true;

    protected override void InitializeContain() => framesForFullHeal = Math.Max(1, FramesSetting("TimeForFullHeal"));

    public override int Update()
    {
        base.Update();
        foreach (var obj in Passengers.ToList())
        {
            if (obj.Body is not { } body)
                continue;
            bool done = Frame - obj.ContainedByFrame >= framesForFullHeal;
            body.AttemptHealing(done ? body.MaxHealth : body.MaxHealth / framesForFullHeal, Owner.Id);
            if (done && ReserveDoorForExit(obj))
                ExitObjectViaDoor(obj);
        }
        return 0;
    }
}

/// <summary>
/// The Combat Cycle (the original RiderChangeContain): the bike takes on its rider's look, weapons,
/// status, command set and locomotor (Rider1..8 lines: rider, model condition, weapon set, status,
/// command set, locomotor set) and veterancy. Without a rider it topples over after ScuttleDelay.
/// </summary>
public sealed class RiderChangeContain : TransportContain
{
    sealed record RiderInfo(string Template, string Condition, string WeaponSet, string Status, string CommandSet, string LocomotorSet);

    readonly List<RiderInfo> riders = new();
    int scuttleFrames;
    string scuttleState = "TOPPLED";
    int scuttledOnFrame;
    bool containing;

    public override bool IsRiderChangeContain => true;
    public override bool IsExitBusy => false;

    protected override void InitializeTransport()
    {
        for (int i = 1; i <= 8; i++)
        {
            string[] w = RuleValue.Words(Setting($"Rider{i}"));
            if (w.Length >= 6)
                riders.Add(new RiderInfo(w[0], w[1], w[2], w[3], w[4], w[5]));
        }
        scuttleFrames = FramesSetting("ScuttleDelay");
        if (Setting("ScuttleStatus").Length > 0)
            scuttleState = Setting("ScuttleStatus");
    }

    RiderInfo? InfoFor(GameObject rider) =>
        riders.FirstOrDefault(r => Game.Templates.Find(r.Template) is { } t && t.IsEquivalentTo(rider.Template));

    public override bool IsValidContainerFor(GameObject rider, bool checkCapacity) =>
        base.IsValidContainerFor(rider, false) && scuttledOnFrame == 0 && InfoFor(rider) != null;

    protected override void OnContaining(GameObject rider)
    {
        var bike = Owner;
        containing = true;
        if (InfoFor(rider) is { } info)
        {
            bike.SetCondition(info.Condition);
            bike.SetWeaponSetFlag(info.WeaponSet);
            bike.SetStatus(info.Status);
            bike.CommandSetOverride = info.CommandSet;
            bike.AI?.SetLocomotorSet(info.LocomotorSet);
            if (bike.HasStatus(Core.Status.Stealthed))
                bike.FindModule<StealthUpdate>()?.MarkAsDetected();
            // The bike gets the rider's rank; the rider keeps none while riding.
            bike.SetVeterancy(rider.Veterancy);
            rider.ResetExperience();
        }
        base.OnContaining(rider);
        containing = false;
    }

    protected override void OnRemoving(GameObject rider)
    {
        var bike = Owner;
        if (bike.IsEffectivelyDead)
        {
            Game.DestroyObject(rider);
            return;
        }
        base.OnRemoving(rider);
        if (InfoFor(rider) is { } info)
        {
            bike.SetCondition(info.Condition, false);
            bike.SetCondition("DOOR_1_CLOSING", false);
            bike.SetWeaponSetFlag(info.WeaponSet, false);
            bike.SetStatus(info.Status, false);
            rider.SetVeterancy(bike.Veterancy);
            bike.ResetExperience();
        }
        if (!containing)
        {
            // No rider: the bike falls over and is gone after ScuttleDelay.
            scuttledOnFrame = Frame;
            bike.SetStatus(Core.Status.Unselectable);
            bike.SetCondition(scuttleState);
            if (bike.AI is { IsMoving: false })
                bike.SetStatus(Flags.ObjectStatus.IndexOf("IMMOBILE"));
        }
    }

    public override int Update()
    {
        if (scuttledOnFrame != 0 && scuttledOnFrame + scuttleFrames <= Frame)
            Owner.Kill("TOPPLED");
        return base.Update();
    }
}

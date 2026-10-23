using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Contain;
using OpenGZH.Logic.Modules.Projectiles;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>How a delivery is carried out (the DeliverPayload nugget's settings).</summary>
public sealed class DeliverPayloadData
{
    public float DistToTarget, PreOpenDistance;
    public int MaxAttempts = 1;
    public int DropDelayFrames;
    public Vector3 DropOffset, DropVariance;
    public bool InheritTransportVelocity, ParachuteDirectly, SelfDestructObject, FireWeapon;
    public float ExitPitchRate;
    public int VisibleItemsDroppedPerInterval;
    public string VisibleDropBoneName = "", VisibleSubObjectName = "", VisiblePayloadTemplateName = "";
    public int VisibleNumBones;
    public WeaponTemplate? VisiblePayloadWeapon;
    public float DiveStartDistance, DiveEndDistance;
    public int StrafingWeaponSlot = -1;
    public string StrafeFx = "";
    public float DeliveryDecalRadius;
    public Effects.RadiusDecalTemplate? DeliveryDecal;

    public static DeliverPayloadData FromRules(RuleNode n, Templates.RulesCatalog catalog)
    {
        string S(string key) => n.GetString(key);
        return new DeliverPayloadData
        {
            DistToTarget = RuleValue.Float(S("DeliveryDistance")),
            PreOpenDistance = RuleValue.Float(S("PreOpenDistance")),
            MaxAttempts = RuleValue.Int(S("MaxAttempts"), 1),
            DropDelayFrames = RuleUnits.Duration(S("DropDelay")),
            DropOffset = RuleValue.Coord(S("DropOffset")),
            DropVariance = RuleValue.Coord(S("DropVariance")),
            InheritTransportVelocity = RuleValue.Bool(S("InheritTransportVelocity")),
            ExitPitchRate = RuleUnits.AngularVelocity(S("ExitPitchRate")),
            ParachuteDirectly = RuleValue.Bool(S("ParachuteDirectly")),
            VisibleItemsDroppedPerInterval = RuleValue.Int(S("VisibleItemsDroppedPerInterval")),
            VisibleDropBoneName = S("VisibleDropBoneBaseName"),
            VisibleSubObjectName = S("VisibleSubObjectBaseName"),
            VisibleNumBones = RuleValue.Int(S("VisibleNumBones")),
            VisiblePayloadTemplateName = S("VisiblePayloadTemplateName"),
            VisiblePayloadWeapon = catalog.Weapon(S("VisiblePayloadWeaponTemplate")),
            SelfDestructObject = RuleValue.Bool(S("SelfDestructObject")),
            FireWeapon = RuleValue.Bool(S("FireWeapon")),
            DiveStartDistance = RuleValue.Float(S("DiveStartDistance")),
            DiveEndDistance = RuleValue.Float(S("DiveEndDistance")),
            StrafingWeaponSlot = S("StrafingWeaponSlot").ToUpperInvariant() switch
            {
                "PRIMARY" => 0, "SECONDARY" => 1, "TERTIARY" => 2, _ => -1,
            },
            StrafeFx = S("StrafeWeaponFX"),
            DeliveryDecalRadius = RuleValue.Float(S("DeliveryDecalRadius")),
            DeliveryDecal = Effects.RadiusDecalTemplate.Parse(n.TryGet("DeliveryDecal", out var decal) ? decal : null),
        };
    }
}

/// <summary>
/// AI for delivery aircraft (the original DeliverPayloadAIUpdate): airstrikes, paradrops, supply
/// drops. A small state machine: APPROACH the target; DELIVER (open the doors after DoorDelay,
/// then drop one item every DropDelay while close enough); if it overshot, CONSIDER a NEW APPROACH
/// (up to MaxAttempts); a plane that strays off the map RECOVERs and comes back in; finally HEAD
/// OFF the MAP and CLEAN UP. Dive bombers (DiveStartDistance) swoop down and can strafe.
/// </summary>
public sealed class DeliverPayloadAIUpdate : AIUpdateInterface
{
    enum DeliveryState { None, Approach, Delivering, ConsiderNewApproach, RecoverFromOffMap, HeadOffMap, CleanUp }
    enum Dive { PreDive, Diving, PostDive }

    DeliverPayloadData data = new();
    DeliveryState state;
    Dive dive = Dive.PostDive;
    Vector3 moveToPos, targetPos;
    int visibleItemsDelivered;
    float previousDistanceSqr;
    bool freeToExit;
    bool acceptingCommands = true;
    int doorDelayFrames;
    int dropDelayLeft;
    int newApproachEntries;
    int reEntryFrame;
    Vector3 facingUponDelivery;

    public string PutInContainer => Setting("PutInContainer");
    public Vector3 TargetPos => targetPos;

    protected override void Initialize()
    {
        base.Initialize();
        doorDelayFrames = FramesSetting("DoorDelay");
    }

    public override bool IsFreeToExit(GameObject rider) => !Owner.IsEffectivelyDead && freeToExit;

    /// <summary>Delivery planes ignore player orders once they head home.</summary>
    public override void MoveTo(Vector3 destination, CommandSource source = CommandSource.Player)
    {
        if (!acceptingCommands && source != CommandSource.AI)
            return;
        base.MoveTo(destination, source);
    }

    /// <summary>Starts a delivery (called by the DeliverPayload nugget, or with the module's own settings).</summary>
    public void DeliverPayload(Vector3 moveTo, Vector3 target, DeliverPayloadData settings)
    {
        moveToPos = moveTo;
        targetPos = target;
        data = settings;
        KillDeliveryDecal();
        deliveryDecalId = Game.Effects.CreateRadiusDecal(data.DeliveryDecal, target, data.DeliveryDecalRadius, Owner.Owner, Owner);
        dive = data.DiveStartDistance <= 0 ? Dive.PostDive : Dive.PreDive;
        visibleItemsDelivered = 0;
        newApproachEntries = 0;
        for (int i = 1; i <= data.VisibleNumBones; i++)
            if (data.VisibleSubObjectName.Length > 0)
                Owner.ShowSubObject($"{data.VisibleSubObjectName}{i:00}", true);
        Enter(DeliveryState.Approach);
    }

    /// <summary>A delivery using only this module's settings (e.g. a supply plane spawned by a script).</summary>
    public void DeliverPayloadViaModuleData(Vector3 moveTo) => DeliverPayload(moveTo, moveTo, new DeliverPayloadData
    {
        DropOffset = RuleValue.Coord(Setting("DropOffset")),
        DropVariance = RuleValue.Coord(Setting("DropVariance")),
        DistToTarget = FloatSetting("DeliveryDistance"),
        MaxAttempts = IntSetting("MaxAttempts", 1),
        DropDelayFrames = FramesSetting("DropDelay"),
        DeliveryDecalRadius = FloatSetting("DeliveryDecalRadius"),
        DeliveryDecal = Effects.RadiusDecalTemplate.Parse(Definition.Settings.TryGet("DeliveryDecal", out var decal) ? decal : null),
    });

    int deliveryDecalId;

    /// <summary>The target circle goes once the plane heads home (the original's killDeliveryDecal).</summary>
    public void KillDeliveryDecal()
    {
        Game.Effects.DestroyRadiusDecal(deliveryDecalId);
        deliveryDecalId = 0;
    }

    float MinTurnRadius(out float timeToTravel)
    {
        timeToTravel = 0;
        if (CurrentLocomotor is not { } locomotor)
            return 999999f;
        float maxSpeed = locomotor.MaxSpeedFor(Owner);
        float turnRate = locomotor.MaxTurnRateFor(Owner);
        float radius = turnRate > 0 ? maxSpeed / turnRate : 999999f;
        timeToTravel = maxSpeed > 0 ? radius / maxSpeed : 0;
        return radius;
    }

    /// <summary>Close enough to drop (a little further while still flying in, by PreOpenDistance).</summary>
    bool IsCloseEnoughToTarget()
    {
        float allowed = data.DistToTarget;
        float current = MathUtil.DistanceSquared2D(Owner.Position, targetPos);
        bool inbound = previousDistanceSqr > current;
        previousDistanceSqr = current;
        if (inbound)
            allowed += data.PreOpenDistance;
        return allowed * allowed > current;
    }

    /// <summary>Outside the map including its border (where planes come in from).</summary>
    bool IsOffMap()
    {
        var p = Owner.Position;
        float border = Game.Terrain.Border * Formats.Maps.MapFile.CellSize;
        return p.X < -border || p.Y < -border || p.X > Game.Terrain.WorldWidth + border || p.Y > Game.Terrain.WorldHeight + border;
    }

    void Enter(DeliveryState next)
    {
        if (state == DeliveryState.Delivering && next != DeliveryState.Delivering)
        {
            // Leaving the delivery: doors shut.
            freeToExit = false;
            Owner.SetCondition("DOOR_1_OPENING", false);
            Owner.SetCondition("DOOR_1_CLOSING");
        }
        if (state == DeliveryState.ConsiderNewApproach && CurrentLocomotor is { } l)
            l.AllowInvalidPosition = true;
        state = next;
        switch (next)
        {
            case DeliveryState.Approach:
                if (Owner.IsEffectivelyDead)
                {
                    Enter(DeliveryState.ConsiderNewApproach);
                    return;
                }
                base.MoveTo(moveToPos, CommandSource.AI);
                break;

            case DeliveryState.Delivering:
                Owner.SetCondition("DOOR_1_CLOSING", false);
                Owner.SetCondition("DOOR_1_OPENING");
                dropDelayLeft = doorDelayFrames;
                break;

            case DeliveryState.ConsiderNewApproach:
            {
                if (++newApproachEntries > data.MaxAttempts)
                {
                    Enter(DeliveryState.HeadOffMap);
                    return;
                }
                // Loop round: fly on for a couple of turning circles, then come back.
                float distance = MinTurnRadius(out _) * 2.2f;
                var ahead = Owner.Position + MathUtil.Forward(Owner.Orientation) * distance;
                base.MoveTo(ahead with { Z = 0 }, CommandSource.AI);
                if (CurrentLocomotor is { } locomotor)
                    locomotor.AllowInvalidPosition = true;
                break;
            }

            case DeliveryState.RecoverFromOffMap:
            {
                base.MoveTo(Owner.Position, CommandSource.AI);
                MinTurnRadius(out float travelTime);
                reEntryFrame = Frame + (int)MathF.Ceiling(travelTime);
                Owner.FindModule<PhysicsBehavior>()?.ResetDynamicPhysics();
                Owner.IsHiddenInContainer = true; // off the map: hidden and out of the world for a moment
                Game.Partition.Remove(Owner);
                break;
            }

            case DeliveryState.HeadOffMap:
            {
                KillDeliveryDecal();
                if (data.SelfDestructObject)
                {
                    Game.DestroyObject(Owner);
                    return;
                }
                float huge = 1.2f * MathF.Sqrt(Game.Terrain.WorldWidth * Game.Terrain.WorldWidth + Game.Terrain.WorldHeight * Game.Terrain.WorldHeight);
                var exit = Owner.Position + MathUtil.Forward(Owner.Orientation) * huge;
                if (CurrentLocomotor is { } locomotor)
                {
                    locomotor.AllowInvalidPosition = true;
                    locomotor.UltraAccurate = true;
                }
                base.MoveTo(exit, CommandSource.AI);
                acceptingCommands = false;
                facingUponDelivery = ForwardVector3D();
                break;
            }

            case DeliveryState.CleanUp:
                Game.DestroyObject(Owner);
                break;
        }
    }

    Vector3 ForwardVector3D()
    {
        float cp = MathF.Cos(Owner.Pitch);
        return new Vector3(MathF.Cos(Owner.Orientation) * cp, MathF.Sin(Owner.Orientation) * cp, MathF.Sin(Owner.Pitch));
    }

    void UpdateStateMachine()
    {
        // The original checks this before the current state's update.
        if (state == DeliveryState.ConsiderNewApproach && IsOffMap())
        {
            Enter(DeliveryState.RecoverFromOffMap);
            return;
        }
        switch (state)
        {
            case DeliveryState.Approach:
                if (Owner.IsEffectivelyDead)
                {
                    Enter(DeliveryState.ConsiderNewApproach);
                    return;
                }
                if (IsCloseEnoughToTarget())
                {
                    Enter(DeliveryState.Delivering);
                    return;
                }
                if (State != AIState.Moving)
                {
                    if (State == AIState.Idle)
                        Enter(DeliveryState.ConsiderNewApproach);
                    else
                        base.MoveTo(moveToPos, CommandSource.AI);
                }
                break;

            case DeliveryState.Delivering:
                UpdateDelivering();
                break;

            case DeliveryState.ConsiderNewApproach:
                if (State != AIState.Moving)
                    Enter(DeliveryState.Approach);
                break;

            case DeliveryState.RecoverFromOffMap:
                if (Frame < reEntryFrame)
                    break;
                Owner.IsHiddenInContainer = false;
                Game.Partition.Add(Owner);
                var enter = Game.Terrain.ClosestEdgePoint(Owner.Position);
                if (Owner.IsAboveTerrain)
                    enter.Z = Owner.Position.Z;
                Owner.SetPosition(enter);
                Owner.SetOrientation(MathF.Atan2(moveToPos.Y - enter.Y, moveToPos.X - enter.X));
                Owner.FindModule<PhysicsBehavior>()?.ResetDynamicPhysics();
                Enter(DeliveryState.Approach);
                break;

            case DeliveryState.HeadOffMap:
                if (IsOffMap())
                {
                    Enter(DeliveryState.CleanUp);
                    return;
                }
                // A plane that can't turn away in time (it would fly back over its target) is lost.
                if (Owner.FindModule<PhysicsBehavior>() is { Turning: not Locomotion.Turning.None }
                    && Vector3.Dot(facingUponDelivery, ForwardVector3D()) < 0.3f)
                    Owner.Kill();
                break;
        }
    }

    void UpdateDelivering()
    {
        if (dropDelayLeft > 0)
        {
            dropDelayLeft--;
            return;
        }
        freeToExit = true;
        dropDelayLeft = data.DropDelayFrames;
        if (!IsCloseEnoughToTarget())
        {
            Enter(DeliveryState.ConsiderNewApproach);
            return;
        }
        var contain = Owner.Contain;
        var item = contain?.Passengers.FirstOrDefault();
        if (item == null && visibleItemsDelivered == data.VisibleNumBones)
        {
            Enter(DeliveryState.HeadOffMap);
            return;
        }

        if (item != null)
        {
            if (data.FireWeapon)
            {
                Owner.FireCurrentWeapon(targetPos + data.DropOffset);
                Game.DestroyObject(item);
            }
            else
            {
                item.AI?.ExitContainerInstantly(CommandSource.AI);
                if (item.IsContained)
                    contain!.RemoveFromContain(item);
                var pos = item.Position;
                if (data.DropVariance.X > 0) pos.X += Game.Random.Float(-data.DropVariance.X, data.DropVariance.X);
                if (data.DropVariance.Y > 0) pos.Y += Game.Random.Float(-data.DropVariance.Y, data.DropVariance.Y);
                if (data.DropVariance.Z > 0) pos.Z += Game.Random.Float(-data.DropVariance.Z, data.DropVariance.Z);
                item.SetPosition(pos + data.DropOffset);
                if (data.ParachuteDirectly && item.Contain is ParachuteContain chute)
                    chute.SetOverrideDestination(targetPos);
                else
                    item.AI?.MoveTo(moveToPos, CommandSource.AI);
                foreach (var minefield in item.FindModules<IMinefieldTarget>())
                    minefield.SetMinefieldTarget(moveToPos);
                item.FindModule<Behaviors.SmartBombTargetHomingUpdate>()?.SetTargetPosition(moveToPos);
                if (data.InheritTransportVelocity && Owner.FindModule<PhysicsBehavior>() is { } myPhysics)
                    item.FindModule<PhysicsBehavior>()?.ApplyForce(myPhysics.Velocity);
            }
        }

        // Bombs drawn on the plane drop as real projectiles.
        if (visibleItemsDelivered < data.VisibleNumBones)
        {
            int attempts = data.VisibleItemsDroppedPerInterval;
            while (attempts > 0 && visibleItemsDelivered < data.VisibleNumBones)
            {
                int number = visibleItemsDelivered + 1;
                if (data.VisibleSubObjectName.Length > 0)
                    Owner.ShowSubObject($"{data.VisibleSubObjectName}{number:00}", false);
                if (data.VisiblePayloadTemplateName.Length > 0)
                    DropVisiblePayload(number);
                attempts--;
                visibleItemsDelivered++;
            }
        }
    }

    void DropVisiblePayload(int number)
    {
        if (Game.Templates.Find(data.VisiblePayloadTemplateName) is not { } template)
            return;
        var pos = data.VisibleDropBoneName.Length > 0 && Game.Bones.TryGetBone(Owner, $"{data.VisibleDropBoneName}{number:00}", out var bone)
            ? bone.Translation : Owner.Position;
        var payload = Game.CreateObject(template, Owner.Owner, pos, Owner.Orientation, Owner.Owner.DefaultTeam);
        payload.ProducerId = Owner.Id;
        var myPhysics = Owner.FindModule<PhysicsBehavior>();
        var payloadPhysics = payload.FindModule<PhysicsBehavior>();
        if (data.InheritTransportVelocity && myPhysics != null && payloadPhysics != null)
        {
            payloadPhysics.ApplyMotiveForce(myPhysics.Velocity * payloadPhysics.Mass);
            payload.SetPosition(payload.Position - myPhysics.Velocity);
        }
        if (payload.FindModule<ProjectileModule>() is { } projectile)
        {
            if (data.VisiblePayloadWeapon is { } weapon)
                projectile.Launch(Owner, weapon, null, targetPos, WeaponBonus.None);
            return;
        }
        if (data.ExitPitchRate != 0 && payloadPhysics != null)
            payloadPhysics.PitchRate = payloadPhysics.CenterOfMassOffset * data.ExitPitchRate;
        payload.AI?.MoveTo(moveToPos, CommandSource.AI);
    }

    void UpdateDive()
    {
        if (dive == Dive.PostDive)
            return;
        if (dive == Dive.PreDive)
        {
            if (MathUtil.DistanceSquared2D(Owner.Position, targetPos) <= data.DiveStartDistance * data.DiveStartDistance)
            {
                dive = Dive.Diving;
                if (CurrentLocomotor is { } locomotor)
                    locomotor.UsePreciseZPos = true;
                Game.Events.PlaySound(Owner.Template.UnitSound("StartDive"), Owner.Position, source: Owner);
            }
            return;
        }
        float currentSqr = Vector3.DistanceSquared(Owner.Position, targetPos);
        if (currentSqr <= data.DiveEndDistance * data.DiveEndDistance)
        {
            dive = Dive.PostDive;
            if (CurrentLocomotor is { } locomotor)
                locomotor.UsePreciseZPos = false;
        }
        if (data.StrafingWeaponSlot >= 0 && Owner.FindModule<PhysicsBehavior>() is { } physics && physics.Velocity.Z < 5)
        {
            // Strafe: the shots walk across the target as the plane dives.
            float ratio = (data.DiveStartDistance - MathF.Sqrt(currentSqr)) / (data.DiveStartDistance - data.DiveEndDistance);
            var velocity = physics.Velocity with { Z = 0 };
            if (velocity.LengthSquared() > 0)
                velocity = Vector3.Normalize(velocity);
            velocity *= ratio * 100;
            var strafe = targetPos - velocity * 0.33f + velocity;
            strafe.Z = Game.Terrain.GroundHeight(strafe);
            Owner.Weapons.LockSlot((WeaponSlot)data.StrafingWeaponSlot);
            Owner.FireCurrentWeapon(strafe);
            Game.Events.PlayFx(data.StrafeFx, strafe);
        }
    }

    public override int Update()
    {
        if (!Owner.IsEffectivelyDead && state != DeliveryState.None)
            UpdateStateMachine();
        UpdateDive();
        base.Update();
        return 0;
    }
}

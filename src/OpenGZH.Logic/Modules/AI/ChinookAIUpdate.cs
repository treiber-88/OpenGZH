using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Locomotion;
using OpenGZH.Logic.Modules.Production;
using OpenGZH.Logic.Modules.Supply;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// The USA Chinook: a flying supply truck (UpgradedSupplyBoost more per trip with Supply Lines) and
/// troop transport. It lands whenever someone wants to get in or out, and takes off again for any
/// other order (which waits until it is airborne). Told to "enter" an airfield it lands beside it
/// to be repaired, and takes off once healthy.
///
/// Combat drop: it flies over the target (high enough above a building: MinDropHeight), hovers,
/// drops NumRopes ropes from its RopeStart/RopeEnd bones, and its CAN_RAPPEL passengers slide down
/// one by one (PerRopeDelayMin..Max apart, RappelSpeed). Landing on a building, a rappeller kills up
/// to two enemies inside (dying if it kills two) and then garrisons it.
///
/// Scripts also send it to a point to unload ("move and evacuate"), optionally flying back off the map.
/// </summary>
public sealed class ChinookAIUpdate : SupplyTruckAIUpdate
{
    const float BigNumber = LocomotorTemplate.BigNumber;

    public enum FlightStatus { Flying, TakingOff, Landing, Landed, DoingCombatDrop }

    enum ChinookState
    {
        None, TakingOff, Landing, MoveToAndLand, MoveToAndEvac, LandAndEvac, EvacAndTakeoff,
        MoveToAndEvacAndExitInit, MoveToAndEvacAndExit, LandAndEvacAndExit, EvacAndExit, TakeoffAndExit,
        HeadOffMap, MoveToCombatDrop, DoCombatDrop,
    }

    enum Result { Continue, Success, Failure }

    sealed class Rope
    {
        public Vector3 Start;
        public Matrix4x4 DropStart;
        public float Speed;
        public float Length;
        public float MaxLength;
        public int NextDropFrame;
        public readonly List<uint> Rappellers = new();
    }

    // Settings
    float rappelSpeed, ropeDropSpeed, ropeFinalHeight, minDropHeight;
    int numRopes, perRopeDelayMin, perRopeDelayMax, upgradedSupplyBoost;
    bool waitForRopesToDrop;
    string rotorWash = "";

    public FlightStatus Status { get; private set; } = FlightStatus.Flying;

    Action? pendingOrder;
    uint airfieldForHealing;
    Vector3 originalPosition;
    PhysicsBehavior? physics;

    ChinookState state;
    uint goalObjectId;
    Vector3 goalPosition;

    // Per-state data
    Vector3 destination;
    float oldPreferredHeight, destZ;
    readonly List<Rope> ropes = new();

    /// <summary>The combat-drop ropes, for drawing: where each hangs from and how long it is now.</summary>
    public IEnumerable<(Vector3 Start, float Length)> Ropes => ropes.Select(r => (r.Start, r.Length));

    protected override void Initialize()
    {
        base.Initialize();
        float gravity = 64f / (LogicTime.FramesPerSecond * LogicTime.FramesPerSecond);
        rappelSpeed = Definition.Settings.Has("RappelSpeed") ? RuleUnits.Velocity(Setting("RappelSpeed")) : gravity * LogicTime.FramesPerSecond * 0.5f;
        ropeDropSpeed = Definition.Settings.Has("RopeDropSpeed") ? RuleUnits.Velocity(Setting("RopeDropSpeed")) : 1e10f;
        ropeFinalHeight = FloatSetting("RopeFinalHeight");
        numRopes = IntSetting("NumRopes", 4);
        perRopeDelayMin = Definition.Settings.Has("PerRopeDelayMin") ? FramesSetting("PerRopeDelayMin") : int.MaxValue;
        perRopeDelayMax = Definition.Settings.Has("PerRopeDelayMax") ? FramesSetting("PerRopeDelayMax") : int.MaxValue;
        minDropHeight = FloatSetting("MinDropHeight", 30);
        waitForRopesToDrop = BoolSetting("WaitForRopesToDrop", true);
        rotorWash = Setting("RotorWashParticleSystem");
        upgradedSupplyBoost = IntSetting("UpgradedSupplyBoost");
    }

    public override void OnCreate()
    {
        base.OnCreate();
        physics = Owner.FindModule<PhysicsBehavior>();
    }

    protected override int UpgradedSupplyBoost => Owner.Owner.HasUpgrade("Upgrade_AmericaSupplyLines") ? upgradedSupplyBoost : 0;

    protected override bool IsAvailableForSupplying =>
        state == ChinookState.None && Owner.Contain is { } contain
        && contain.Count == 0 && !contain.HasObjectsWantingToEnterOrExit && !contain.IsSpecialOverlordStyleContainer;

    public override bool IsIdle
    {
        get
        {
            if (pendingOrder != null)
                return false;
            bool idle = base.IsIdle && state == ChinookState.None;
            if (idle && Status == FlightStatus.Landed && Owner.Contain is { HasObjectsWantingToEnterOrExit: true })
                idle = false;
            return idle;
        }
    }

    public override bool IsFreeToExit(GameObject rider) =>
        Status == FlightStatus.Landed || (Status == FlightStatus.DoingCombatDrop && rider.IsKindOf("CAN_RAPPEL"));

    public override bool SetLocomotorSet(string setName) =>
        base.SetLocomotorSet(Status == FlightStatus.Landed ? "SET_TAXIING" : setName);

    void LoseAllBoxes()
    {
        while (Gatherer.LoseOneBox()) { }
    }

    // ---- Airfield repairs ----

    ParkingPlaceBehavior? AirfieldPp(uint id) =>
        Game.FindObject(id) is { IsEffectivelyDead: false } airfield && airfield.IsKindOf("FS_AIRFIELD")
            ? airfield.FindModule<ParkingPlaceBehavior>() : null;

    void SetAirfieldForHealing(uint id)
    {
        if (airfieldForHealing != 0 && airfieldForHealing != id)
            AirfieldPp(airfieldForHealing)?.SetHealee(Owner, false);
        airfieldForHealing = id;
    }

    // ---- Orders ----

    protected override bool OnOrder(AIOrder order, Action replay)
    {
        SetAirfieldForHealing(0);
        if (Status is FlightStatus.TakingOff or FlightStatus.Landing or FlightStatus.DoingCombatDrop)
        {
            pendingOrder = replay;
            return true;
        }

        switch (order.Type)
        {
            case AIOrderType.Stop:
                break;

            case AIOrderType.MoveToAndEvacuate:
            case AIOrderType.MoveToAndEvacuateAndExit:
            {
                var pos = order.Position ?? Owner.Position;
                if (Vector3.DistanceSquared(Owner.Position, pos) > 3 * 3 && Status == FlightStatus.Landed)
                {
                    pendingOrder = replay;
                    SetMyState(ChinookState.TakingOff, null, null, CommandSource.AI);
                }
                else
                {
                    var next = order.Type == AIOrderType.MoveToAndEvacuate ? ChinookState.MoveToAndEvac : ChinookState.MoveToAndEvacAndExitInit;
                    SetMyState(next, null, pos, CommandSource.AI);
                }
                return true;
            }

            case AIOrderType.Evacuate:
            case AIOrderType.ExitContainer:
                if (Status != FlightStatus.Landed)
                {
                    pendingOrder = replay;
                    SetMyState(ChinookState.Landing, null, null, CommandSource.AI);
                    return true;
                }
                break;

            default:
                if (Status != FlightStatus.Flying)
                {
                    pendingOrder = replay;
                    SetMyState(ChinookState.TakingOff, null, null, CommandSource.AI);
                    return true;
                }
                break;
        }

        pendingOrder = null;

        switch (order.Type)
        {
            case AIOrderType.CombatDrop:
                StartCombatDrop(Game.FindObject(order.TargetId), order.Position ?? Owner.Position, order.Source);
                return true;
            case AIOrderType.Enter when Game.FindObject(order.TargetId) is { } airfield && airfield.IsKindOf("FS_AIRFIELD"):
                GetRepairedAt(airfield, order.Source);
                return true;
            case AIOrderType.Stop:
                // Stopping also stops the Overlord-style rider (a Gattling or speaker on top).
                if (Owner.Contain?.Rider is { } rider && Owner.Contain.IsSpecialOverlordStyleContainer)
                    rider.AI?.Stop(order.Source);
                break;
        }

        ClearState();
        return false;
    }

    void GetRepairedAt(GameObject airfield, CommandSource source)
    {
        if (Status is FlightStatus.Landing or FlightStatus.Landed)
            return;
        if (airfield.IsEffectivelyDead || !(airfield.Owner == Owner.Owner || airfield.Owner.IsAllyOf(Owner.Owner)))
            return;
        SetAirfieldForHealing(airfield.Id);
        var pos = NearestFreeSpot(airfield.Position, airfield.BoundingCircleRadius * 100);
        SetMyState(ChinookState.MoveToAndLand, null, pos, source);
    }

    void StartCombatDrop(GameObject? target, Vector3 position, CommandSource source)
    {
        if (target != null && source == CommandSource.Player && !CanCombatDropInto(target))
            return;
        var pos = target == null ? NearestFreeSpot(position, Owner.BoundingCircleRadius * 100) : position;
        SetMyState(ChinookState.MoveToCombatDrop, target, pos, source);
    }

    /// <summary>A building the passengers could take: garrisonable (or holding enemies), not our own team's full one.</summary>
    bool CanCombatDropInto(GameObject target) =>
        !target.IsEffectivelyDead && target.IsKindOf(Kind.Structure) && target.Contain is { IsGarrisonable: true }
        && Owner.Contain?.Passengers.Any(p => p.IsKindOf("CAN_RAPPEL")) == true;

    Vector3 NearestFreeSpot(Vector3 around, float maxRadius)
    {
        var spot = Game.Pathfinder.NearestPassable(around, Surfaces.Ground);
        return spot is { } s && MathUtil.Distance2D(s, around) <= maxRadius ? s : around;
    }

    // ---- The per-frame update ----

    public override int Update()
    {
        if (AirfieldPp(airfieldForHealing) is { } pp)
        {
            if (Status == FlightStatus.Landed && pendingOrder == null && Owner.Health == Owner.MaxHealth)
            {
                pp.SetHealee(Owner, false);
                SetMyState(ChinookState.TakingOff, null, null, CommandSource.AI);
            }
            else
                pp.SetHealee(Owner, Status == FlightStatus.Landed);
        }
        else
            SetAirfieldForHealing(0);

        if (Owner.Contain is { } contain)
        {
            if (base.IsIdle && state == ChinookState.None)
            {
                bool wanting = contain.HasObjectsWantingToEnterOrExit;
                if (pendingOrder is { } order)
                {
                    pendingOrder = null;
                    order();
                }
                else if (wanting && Status != FlightStatus.Landed)
                    SetMyState(ChinookState.Landing, null, null, CommandSource.AI);
                else if (!wanting && Status == FlightStatus.Landed && airfieldForHealing == 0)
                    SetMyState(ChinookState.TakingOff, null, null, CommandSource.AI);
            }

            // Passengers that may shoot out join in on whatever the Chinook attacks.
            if (Frame % 10 == 1 && CurrentVictim is { } victim)
            {
                foreach (var passenger in contain.Passengers.ToList())
                    if (contain.IsPassengerAllowedToFire(passenger) && passenger.AI is { } pai && pai.CurrentVictim == null)
                        pai.AttackObject(victim, CommandSource.AI, 999);
            }
        }

        // Rotor wash kicks up dust close to the ground.
        if (rotorWash.Length > 0 && Status is FlightStatus.Landing or FlightStatus.TakingOff or FlightStatus.Landed && Frame % 6 == 0)
        {
            float elevation = Owner.Position.Z - (Game.Terrain.GroundHeight(Owner.Position) + 3);
            if (elevation < 5 || Frame % 30 == 0)
                Game.Events.ParticleSystem(rotorWash, Owner.Position with { Z = Game.Terrain.GroundHeight(Owner.Position) + 3 });
        }

        return base.Update();
    }

    // Attacks (only if the Chinook itself can attack): infantry passengers allowed to fire join in.

    public override void AttackObject(GameObject target, CommandSource source = CommandSource.Player, int maxShots = -1)
    {
        if (!Owner.IsKindOf("CAN_ATTACK"))
            return;
        foreach (var passenger in AttackingPassengers(source))
            passenger.AI!.AttackObject(target, source, maxShots);
        base.AttackObject(target, source, maxShots);
    }

    public override void AttackPosition(Vector3 position, CommandSource source = CommandSource.Player, int maxShots = -1)
    {
        if (!Owner.IsKindOf("CAN_ATTACK"))
            return;
        foreach (var passenger in AttackingPassengers(source))
            passenger.AI!.AttackPosition(position, source, maxShots);
        base.AttackPosition(position, source, maxShots);
    }

    static bool IsDisabledPortable(GameObject p) =>
        p.IsKindOf("PORTABLE_STRUCTURE") && (p.IsDisabledBy("DISABLED_HACKED") || p.IsDisabledBy("DISABLED_EMP")
            || p.IsDisabledBy("DISABLED_SUBDUED") || p.IsDisabledBy("DISABLED_PARALYZED"));

    List<GameObject> AttackingPassengers(CommandSource source)
    {
        var result = new List<GameObject>();
        if (Owner.Contain is not { } contain || source is not (CommandSource.Player or CommandSource.Script))
            return result;
        result.AddRange(contain.Passengers.Where(p => contain.IsPassengerAllowedToFire(p) && p.IsKindOf(Kind.Infantry) && !IsDisabledPortable(p) && p.AI != null));
        if (contain.Rider is { } rider && rider.IsKindOf("PORTABLE_STRUCTURE") && !IsDisabledPortable(rider) && rider.AI != null && !result.Contains(rider))
            result.Add(rider);
        return result;
    }

    // ---- The Chinook state machine ----

    protected override bool UpdateDerivedState()
    {
        if (state == ChinookState.None)
            return false;
        var result = UpdateState(state);
        if (result != Result.Continue)
            SetState(Next(state, result));
        return state != ChinookState.None;
    }

    void SetMyState(ChinookState next, GameObject? target, Vector3? position, CommandSource source)
    {
        ClearState();
        ResetToIdle(source);
        goalObjectId = target?.Id ?? 0;
        goalPosition = position ?? Owner.Position;
        SetState(next);
    }

    void ClearState()
    {
        if (state != ChinookState.None)
            ExitState(state);
        state = ChinookState.None;
    }

    void SetState(ChinookState next)
    {
        for (int guard = 0; guard < 16; guard++)
        {
            if (state != ChinookState.None)
                ExitState(state);
            state = next;
            if (next == ChinookState.None)
            {
                State = AIState.Idle;
                return;
            }
            State = AIState.Busy;
            var result = EnterState(next);
            if (result == Result.Continue)
                return;
            next = Next(next, result);
        }
        ClearState();
        State = AIState.Idle;
    }

    static ChinookState Next(ChinookState state, Result result)
    {
        if (result != Result.Success)
            return ChinookState.None;
        return state switch
        {
            ChinookState.MoveToCombatDrop => ChinookState.DoCombatDrop,
            ChinookState.MoveToAndLand => ChinookState.Landing,
            ChinookState.MoveToAndEvac => ChinookState.LandAndEvac,
            ChinookState.LandAndEvac => ChinookState.EvacAndTakeoff,
            ChinookState.EvacAndTakeoff => ChinookState.TakingOff,
            ChinookState.MoveToAndEvacAndExitInit => ChinookState.MoveToAndEvacAndExit,
            ChinookState.MoveToAndEvacAndExit => ChinookState.LandAndEvacAndExit,
            ChinookState.LandAndEvacAndExit => ChinookState.EvacAndExit,
            ChinookState.EvacAndExit => ChinookState.TakeoffAndExit,
            ChinookState.TakeoffAndExit => ChinookState.HeadOffMap,
            _ => ChinookState.None,
        };
    }

    Result EnterState(ChinookState s) => s switch
    {
        ChinookState.TakingOff or ChinookState.TakeoffAndExit => EnterTakeoffOrLanding(landing: false),
        ChinookState.Landing or ChinookState.LandAndEvac or ChinookState.LandAndEvacAndExit => EnterTakeoffOrLanding(landing: true),
        ChinookState.EvacAndTakeoff or ChinookState.EvacAndExit => Evacuate(),
        ChinookState.MoveToAndLand or ChinookState.MoveToAndEvac or ChinookState.MoveToAndEvacAndExit => EnterMoveTo(),
        ChinookState.MoveToAndEvacAndExitInit => RecordCreation(),
        ChinookState.HeadOffMap => EnterHeadOffMap(),
        ChinookState.MoveToCombatDrop => EnterMoveToBuilding(),
        ChinookState.DoCombatDrop => EnterCombatDrop(),
        _ => Result.Failure,
    };

    Result UpdateState(ChinookState s) => s switch
    {
        ChinookState.TakingOff or ChinookState.TakeoffAndExit or ChinookState.Landing
            or ChinookState.LandAndEvac or ChinookState.LandAndEvacAndExit => UpdateTakeoffOrLanding(),
        ChinookState.EvacAndTakeoff or ChinookState.EvacAndExit or ChinookState.MoveToAndEvacAndExitInit => Result.Success,
        ChinookState.MoveToAndLand or ChinookState.MoveToAndEvac or ChinookState.MoveToAndEvacAndExit => UpdateMoveTo(),
        ChinookState.HeadOffMap => UpdateHeadOffMap(),
        ChinookState.MoveToCombatDrop => UpdateMoveToBuilding(),
        ChinookState.DoCombatDrop => UpdateCombatDrop(),
        _ => Result.Failure,
    };

    void ExitState(ChinookState s)
    {
        switch (s)
        {
            case ChinookState.TakingOff:
            case ChinookState.TakeoffAndExit:
                ExitTakeoffOrLanding(landing: false);
                break;
            case ChinookState.Landing:
            case ChinookState.LandAndEvac:
            case ChinookState.LandAndEvacAndExit:
                ExitTakeoffOrLanding(landing: true);
                break;
            case ChinookState.MoveToCombatDrop:
                if (CurrentLocomotor is { } loco)
                {
                    loco.PreferredHeight = oldPreferredHeight;
                    loco.UltraAccurate = false;
                }
                break;
            case ChinookState.DoCombatDrop:
                ExitCombatDrop();
                break;
            case ChinookState.HeadOffMap:
                if (CurrentLocomotor is { } l)
                    l.AllowInvalidPosition = false;
                break;
        }
    }

    // Taking off and landing: straight up or down on the spot (finding a clear spot to land on).

    Result EnterTakeoffOrLanding(bool landing)
    {
        Status = landing ? FlightStatus.Landing : FlightStatus.TakingOff;
        if (landing)
            LoseAllBoxes();
        physics?.ScrubVelocity2D(0);
        SetLocomotorSet("SET_NORMAL");
        if (CurrentLocomotor is not { } loco)
            return Result.Failure;
        loco.UsePreciseZPos = true;
        loco.UltraAccurate = true;
        destination = Owner.Position with { Z = Game.Terrain.LayerHeight(Owner.Position.X, Owner.Position.Y) };
        if (landing)
        {
            destination = NearestFreeSpot(destination, Owner.BoundingCircleRadius * 100);
            destination.Z = Game.Terrain.LayerHeight(destination.X, destination.Y);
        }
        else
            destination.Z += loco.PreferredHeight;
        return Result.Continue;
    }

    Result UpdateTakeoffOrLanding()
    {
        if (Owner.IsEffectivelyDead)
            return Result.Failure;
        SetGoalPositionExplicit(destination);
        return Vector3.DistanceSquared(Owner.Position, destination) <= 3 * 3 ? Result.Success : Result.Continue;
    }

    void ExitTakeoffOrLanding(bool landing)
    {
        Status = landing ? FlightStatus.Landed : FlightStatus.Flying;
        if (CurrentLocomotor is { } loco)
        {
            loco.UsePreciseZPos = false;
            loco.UltraAccurate = false;
            if (!Owner.IsEffectivelyDead)
                loco.SetMaxLift(BigNumber);
        }
        if (landing)
            SetLocomotorSet("SET_TAXIING");
    }

    Result Evacuate()
    {
        Owner.Contain?.RemoveAllContained();
        return Result.Success;
    }

    Result RecordCreation()
    {
        originalPosition = Owner.Position;
        return Result.Success;
    }

    Result EnterMoveTo()
    {
        var goal = Game.FindObject(goalObjectId)?.Position ?? goalPosition;
        MoveInternal(goal);
        return Result.Continue;
    }

    Result UpdateMoveTo()
    {
        if (Owner.IsEffectivelyDead)
            return Result.Failure;
        return !HasPath || FollowPath() ? Result.Success : Result.Continue;
    }

    // Leaving the map after a reinforcement drop.

    Result EnterHeadOffMap()
    {
        MoveInternal(originalPosition);
        if (CurrentLocomotor is { } loco)
            loco.AllowInvalidPosition = true;
        return Result.Continue;
    }

    Result UpdateHeadOffMap()
    {
        var p = Owner.Position;
        bool offMap = p.X < 0 || p.Y < 0 || p.X > Game.Terrain.WorldWidth || p.Y > Game.Terrain.WorldHeight;
        if (offMap || !HasPath || FollowPath())
        {
            Game.DestroyObject(Owner);
            return Result.Success;
        }
        return Result.Continue;
    }

    // Combat drop: get over the target, then rappel.

    Result EnterMoveToBuilding()
    {
        if (CurrentLocomotor is not { } loco)
            return Result.Failure;
        loco.UltraAccurate = true;
        oldPreferredHeight = loco.PreferredHeight;
        float newHeight = oldPreferredHeight;
        Vector3 dest;
        if (Game.FindObject(goalObjectId) is { IsEffectivelyDead: false } bldg && bldg.IsKindOf(Kind.Structure))
        {
            dest = bldg.Position;
            newHeight = Math.Max(oldPreferredHeight, bldg.Geometry.MaxHeightAbovePosition + minDropHeight);
        }
        else
            dest = goalPosition;
        loco.PreferredHeight = newHeight;
        destZ = Game.Terrain.GroundHeight(dest) + newHeight;
        destination = dest;
        IgnoreObstacle(Game.FindObject(goalObjectId));
        MoveInternal(dest);
        return Result.Continue;
    }

    Result UpdateMoveToBuilding()
    {
        if (Owner.IsEffectivelyDead)
            return Result.Failure;
        bool arrived = !HasPath || FollowPath();
        if (!arrived)
            return Result.Continue;
        SetGoalPositionExplicit(destination with { Z = destZ });
        return MathF.Abs(Owner.Position.Z - destZ) > 3 ? Result.Continue : Result.Success;
    }

    GameObject? PotentialRappeller() => Owner.Contain?.Passengers.FirstOrDefault(p => p.IsKindOf("CAN_RAPPEL"));

    Result EnterCombatDrop()
    {
        Owner.SetDisabled("DISABLED_HELD");
        Status = FlightStatus.DoingCombatDrop;
        LoseAllBoxes();
        var starts = Game.Bones.NumberedBones(Owner, "RopeStart", 32);
        var ends = Game.Bones.NumberedBones(Owner, "RopeEnd", 32);
        int count = Math.Min(numRopes, Math.Min(starts.Count, ends.Count));
        ropes.Clear();
        if (count <= 0)
        {
            // Models without rope bones drop from the middle of the helicopter.
            if (numRopes <= 0)
                return Result.Failure;
            count = 1;
            starts = [Matrix4x4.CreateTranslation(Owner.Position)];
            ends = [Matrix4x4.CreateRotationZ(Owner.Orientation) * Matrix4x4.CreateTranslation(Owner.Position)];
        }
        int now = Frame;
        for (int i = 0; i < count; i++)
        {
            var start = starts[i].Translation;
            ropes.Add(new Rope
            {
                Start = start,
                DropStart = ends[i],
                Length = 1,
                MaxLength = start.Z - Game.Terrain.LayerHeight(start.X, start.Y) - ropeFinalHeight,
                NextDropFrame = now + RandomDelay() - perRopeDelayMin,
            });
        }
        return Result.Continue;
    }

    int RandomDelay() => perRopeDelayMin == int.MaxValue ? int.MaxValue / 2 : Game.Random.Int(perRopeDelayMin, perRopeDelayMax);

    Result UpdateCombatDrop()
    {
        if (Owner.IsEffectivelyDead)
            return Result.Failure;
        foreach (var rope in ropes)
            rope.Rappellers.RemoveAll(id => Game.FindObject(id) is not { IsEffectivelyDead: false } r || !r.IsAboveTerrain);

        int now = Frame;
        int ropesInUse = 0;
        float gravity = MathF.Abs(Game.Catalog.GameData.Gravity);
        foreach (var rope in ropes)
        {
            if (rope.Length < rope.MaxLength)
            {
                rope.Speed = Math.Min(rope.Speed + gravity, ropeDropSpeed);
                rope.Length += rope.Speed;
                if (waitForRopesToDrop)
                {
                    rope.NextDropFrame++;
                    continue;
                }
            }
            if (now >= rope.NextDropFrame && PotentialRappeller() is { } rappeller && Owner.Contain is { } contain)
            {
                if (contain.ReserveDoorForExit(rappeller))
                    contain.ExitObjectViaDoor(rappeller);
                rappeller.SetPosition(rope.DropStart.Translation);
                rappeller.SetOrientation(MathF.Atan2(rope.DropStart.M12, rope.DropStart.M11));
                if (rappeller.AI is { } rai)
                {
                    rai.SetDesiredSpeed(rappelSpeed);
                    rai.RappelInto(Game.FindObject(goalObjectId), goalPosition, CommandSource.AI);
                }
                rope.Rappellers.Add(rappeller.Id);
                rope.NextDropFrame = now + RandomDelay();
            }
            if (rope.Rappellers.Count > 0)
                ropesInUse++;
        }
        return ropesInUse == 0 && PotentialRappeller() == null ? Result.Success : Result.Continue;
    }

    void ExitCombatDrop()
    {
        Owner.SetDisabled("DISABLED_HELD", false);
        Status = FlightStatus.Flying;
        if (Owner.IsEffectivelyDead)
        {
            foreach (var id in ropes.SelectMany(r => r.Rappellers))
                Game.FindObject(id)?.AI?.Stop(CommandSource.AI);
        }
        ropes.Clear();
    }
}

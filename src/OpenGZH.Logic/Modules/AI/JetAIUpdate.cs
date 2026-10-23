using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Locomotion;
using OpenGZH.Logic.Modules.Production;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// Planes that live on an airfield (Raptor, MiG, Aurora, Comanche-style helipad craft...).
///
/// A new plane taxis from the hangar to its parking space. Given an order, it waits for its
/// runway, taxis to the runway start, pauses (TakeoffPause, afterburners lit), and takes off,
/// gaining lift as it runs down the runway. Out of RETURN_TO_BASE ammo, idle in the air for
/// ReturnToBaseIdleTime, or told to stop, it flies back, waits for its runway, lands, taxis to its
/// space, turns to face out, and reloads (the time scales with how empty the clips are). Parked planes
/// are repaired by the airfield. If its airfield is gone it looks for another; with none, it circles
/// where the airfield was, losing OutOfAmmoDamagePerSecond of its health, until one turns up.
///
/// Planes that don't need a runway (NeedsRunway = No) rise straight up from their space instead.
/// Orders given during takeoff or landing wait until it's done. While attacking and for
/// AttackLocomotorPersistTime after, it uses AttackLocomotorType; going home for ammo,
/// ReturnForAmmoLocomotorType. For AttackersMissPersistTime after attacking, shots at it are aimed
/// SneakyOffsetWhenAttacking behind it.
/// </summary>
public sealed class JetAIUpdate : AIUpdateInterface, IOwnershipModule
{
    const float BigNumber = LocomotorTemplate.BigNumber;
    const int HowOftenToCheckAirfield = LogicTime.FramesPerSecond;

    enum JetState
    {
        None, TaxiFromHangar, TakingOffAwaitClearance, TaxiToTakeoff, PauseBeforeTakeoff, TakingOff,
        LandingAwaitClearance, Landing, TaxiFromLanding, OrientForParkingPlace, ReloadAmmo,
        ReturningForLanding, ReturnToDeadAirfield, CirclingDeadAirfield,
    }

    enum Result { Continue, Success, Failure }

    enum TaxiMode { FromHangar, FromParking, ToParking }

    // Settings
    float outOfAmmoDamagePerSecond, minHeight, sneakyOffset;
    bool needsRunway = true, keepsParkingSpaceWhenAirborne = true;
    int takeoffPause, attackLocoPersistFrames, attackersMissPersistFrames, returnToBaseIdleFrames;
    string attackingLoco = "SET_NORMAL", returningLoco = "SET_NORMAL";

    public float ParkingOffset { get; private set; }

    // Flags (the original's m_flags)
    Action? pendingOrder;
    AIOrderType? pendingOrderType;

    /// <summary>The order waiting for takeoff or landing to finish (the original's friend_getPendingCommandType).</summary>
    public AIOrderType? PendingOrderType => pendingOrder != null ? pendingOrderType : null;

    public void PurgePendingOrder() => pendingOrder = null;
    bool allowAirLoco, hasProducerLocation, takeoffInProgress, landingInProgress;
    bool useSpecialReturnLoco, allowCircling, allowInterruptForReload, taxiInProgress;

    Vector3 producerLocation;
    int attackLocoExpireFrame, attackersMissExpireFrame, returnToBaseFrame;
    Vector3 landingPosForHelipad;
    PhysicsBehavior? physics;

    JetState jetState;

    // Per-state data
    int checkAirfieldCountdown;
    float stateMaxLift;
    Vector3 circleForLandingPos;
    bool landingSoundPlayed;
    readonly Vector3[] heliPath = new Vector3[2];
    int heliIndex;
    Vector3 heliParkingLoc;
    float heliParkingOrientation;
    Vector3 facePosition;
    int pauseWhen, pauseWhenTransfer;
    uint waitedForTaxiId;
    bool pauseResetTimer, pauseAfterburners;
    int reloadTime, reloadDoneFrame;

    protected override void Initialize()
    {
        base.Initialize();
        outOfAmmoDamagePerSecond = FloatSetting("OutOfAmmoDamagePerSecond");
        needsRunway = BoolSetting("NeedsRunway", true);
        keepsParkingSpaceWhenAirborne = BoolSetting("KeepsParkingSpaceWhenAirborne", true);
        takeoffPause = FramesSetting("TakeoffPause");
        minHeight = FloatSetting("MinHeight");
        ParkingOffset = FloatSetting("ParkingOffset");
        sneakyOffset = FloatSetting("SneakyOffsetWhenAttacking");
        attackingLoco = Setting("AttackLocomotorType", "SET_NORMAL");
        attackLocoPersistFrames = FramesSetting("AttackLocomotorPersistTime");
        attackersMissPersistFrames = FramesSetting("AttackersMissPersistTime");
        returningLoco = Setting("ReturnForAmmoLocomotorType", "SET_NORMAL");
        returnToBaseIdleFrames = FramesSetting("ReturnToBaseIdleTime");
    }

    public override void OnCreate()
    {
        base.OnCreate();
        physics = Owner.FindModule<PhysicsBehavior>();
        allowAirLoco = false;
        SetLocomotorSet("SET_TAXIING");
    }

    // ---- Queries ----

    public bool IsTakeoffOrLandingInProgress => takeoffInProgress || landingInProgress;

    public override bool IsIdle => pendingOrder == null && base.IsIdle;

    public bool IsReloading => jetState == JetState.ReloadAmmo;

    public bool IsTaxiingToParking => jetState is JetState.TaxiFromHangar or JetState.TaxiFromLanding or JetState.OrientForParkingPlace
        or JetState.ReloadAmmo or JetState.TakingOffAwaitClearance or JetState.TaxiToTakeoff or JetState.PauseBeforeTakeoff or JetState.TakingOff;

    /// <summary>All its RETURN_TO_BASE weapons are empty.</summary>
    public bool IsOutOfSpecialReloadAmmo
    {
        get
        {
            int specials = 0, empty = 0;
            foreach (var weapon in Owner.Weapons.All)
            {
                if (!weapon.Template.ReloadsAtBase)
                    continue;
                specials++;
                if (weapon.Status == WeaponStatus.OutOfAmmo)
                    empty++;
            }
            return specials > 0 && empty == specials;
        }
    }

    /// <summary>While (and just after) attacking, shots at it are aimed this far behind it.</summary>
    public bool TryGetSneakyTargetingOffset(out Vector3 offset)
    {
        offset = default;
        if (attackersMissExpireFrame == 0 || Frame >= attackersMissExpireFrame)
            return false;
        offset = new Vector3(MathF.Cos(Owner.Orientation), MathF.Sin(Owner.Orientation), 0) * sneakyOffset;
        return true;
    }

    public override bool IsDoingGroundMovement => false;

    protected override bool TreatAsAircraftForLocoDistToGoal => !taxiInProgress && base.TreatAsAircraftForLocoDistToGoal;

    // ---- Airfields ----

    IAirfield? ParkingAt(uint airfieldId) => ParkingAt(airfieldId, out _);

    IAirfield? ParkingAt(uint airfieldId, out GameObject? airfield)
    {
        airfield = null;
        var obj = Game.FindObject(airfieldId);
        if (obj == null || obj.IsEffectivelyDead || !obj.IsKindOf("FS_AIRFIELD") || obj.HasStatus(Status.Sold))
            return null;
        airfield = obj;
        return obj.FindModule<IAirfield>();
    }

    IAirfield? Pp => ParkingAt(Owner.ProducerId);

    /// <summary>The nearest friendly airfield with a space for this plane (the space gets reserved).</summary>
    GameObject? FindSuitableAirfield()
    {
        // The original reserves a space at every airfield its search filter looks at; we only
        // reserve at the one chosen (nearest first).
        var candidates = Game.Objects
            .Where(o => o.IsKindOf("FS_AIRFIELD") && !o.IsEffectivelyDead && !o.IsUnderConstruction && !o.HasStatus(Status.Sold)
                && (o.Owner == Owner.Owner || o.Owner.IsAllyOf(Owner.Owner)))
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, Owner.Position));
        foreach (var airfield in candidates)
            if (ParkingAt(airfield.Id) is { } pp && pp.ReserveSpace(Owner.Id, 0))
                return airfield;
        return null;
    }

    void GetProducerLocation()
    {
        if (hasProducerLocation)
            return;
        producerLocation = Game.FindObject(Owner.ProducerId)?.Position ?? Owner.Position;
        var pp = Pp;
        if (pp == null || !pp.HasReservedSpace(Owner.Id))
        {
            allowAirLoco = true;
            SetLocomotorSet("SET_NORMAL");
        }
        else
        {
            allowAirLoco = false;
            SetLocomotorSet("SET_TAXIING");
        }
        hasProducerLocation = true;
    }

    bool IsParkedAt(GameObject? obj) =>
        !allowAirLoco && !Owner.IsKindOf("PRODUCED_AT_HELIPAD") && obj != null
        && ParkingAt(Owner.ProducerId, out var airfield) != null && airfield == obj;

    // ---- Locomotion overrides ----

    public override bool SetLocomotorSet(string setName)
    {
        if (!allowAirLoco)
            setName = "SET_TAXIING";
        else if (attackLocoExpireFrame != 0)
            setName = attackingLoco;
        else if (useSpecialReturnLoco)
            setName = returningLoco;
        return base.SetLocomotorSet(setName);
    }

    /// <summary>Taking off or landing, a plane can't just stop: it keeps flying straight ahead.</summary>
    protected override void Decelerate()
    {
        if ((takeoffInProgress || landingInProgress) && allowAirLoco && !allowCircling)
            SetGoalPositionExplicit(Owner.Position + new Vector3(MathF.Cos(Owner.Orientation), MathF.Sin(Owner.Orientation), 0) * 1000f);
        else
            base.Decelerate();
    }

    void EnableAfterburners(bool on)
    {
        Owner.SetCondition("JETAFTERBURNER", on);
        if (on && Owner.Template.UnitSound("Afterburner") is { Length: > 0 } sound)
            Game.Events.PlaySound(sound, Owner.Position, source: Owner);
    }

    // ---- Orders ----

    protected override bool OnOrder(AIOrder order, Action replay)
    {
        GetProducerLocation();
        pendingOrderType = order.Type;

        if (takeoffInProgress || landingInProgress)
        {
            pendingOrder = replay;
            return true;
        }
        if (order.Type == AIOrderType.Stop && jetState == JetState.ReloadAmmo)
        {
            pendingOrder = replay;
            return true;
        }
        if (order.Type == AIOrderType.Stop && Owner.IsAirborneTarget && !Owner.IsKindOf("PRODUCED_AT_HELIPAD"))
        {
            LastCommandSource = CommandSource.AI;
            SetJetState(JetState.ReturningForLanding, clear: true);
            return true;
        }
        if (!allowAirLoco)
        {
            switch (order.Type)
            {
                case AIOrderType.Stop:
                    break;
                case AIOrderType.Enter when IsParkedAt(Game.FindObject(order.TargetId)):
                    return true;
                default:
                    pendingOrder = replay;
                    LastCommandSource = CommandSource.AI;
                    SetJetState(JetState.TakingOffAwaitClearance, clear: true);
                    return true;
            }
        }

        allowInterruptForReload = order.Type == AIOrderType.Guard;
        lastGuard = allowInterruptForReload ? replay : null;
        pendingOrder = null;

        if (order.Type == AIOrderType.Enter)
        {
            // "Entering" an airfield means landing there.
            if (!landingInProgress && Game.FindObject(order.TargetId) is { } airfield && CanLandAt(airfield))
                DoLandingCommand(airfield, order.Source);
            return true;
        }

        ClearJetState();
        return false;
    }

    bool CanLandAt(GameObject airfield) =>
        !airfield.IsEffectivelyDead && airfield.FindModule<IAirfield>() != null
        && (airfield.Owner == Owner.Owner || airfield.Owner.IsAllyOf(Owner.Owner));

    void DoLandingCommand(GameObject airfield, CommandSource source)
    {
        if (Owner.IsKindOf("PRODUCED_AT_HELIPAD"))
        {
            landingPosForHelipad = airfield.Position;
            var free = Game.Pathfinder.NearestPassable(airfield.Position, Surfaces.Ground);
            if (free is { } spot && MathUtil.Distance2D(spot, airfield.Position) <= airfield.BoundingCircleRadius * 10)
                landingPosForHelipad = spot;
        }
        if (airfield.FindModule<IAirfield>() is not { } pp)
            return;
        if (!Owner.IsKindOf("PRODUCED_AT_HELIPAD") && !pp.ReserveSpace(Owner.Id, ParkingOffset))
            return;
        var oldPp = Pp;
        if (oldPp != null && oldPp != pp)
            oldPp.ReleaseSpace(Owner.Id);
        Owner.ProducerId = airfield.Id;
        useSpecialReturnLoco = false;
        allowInterruptForReload = false;
        LastCommandSource = source;
        SetJetState(JetState.ReturningForLanding, clear: true);
    }

    public override void FollowExitProductionPath(IReadOnlyList<Vector3> points, GameObject producer, CommandSource source = CommandSource.AI)
    {
        IgnoreObstacle(producer);
        LastCommandSource = source;
        SetJetState(Owner.IsKindOf("PRODUCED_AT_HELIPAD") ? JetState.TakingOffAwaitClearance : JetState.TaxiFromHangar, clear: true);
    }

    protected override void OnVictimDead()
    {
        if (needsRunway)
            returnToBaseFrame = Frame;
    }

    // ---- The per-frame update ----

    public override int Update()
    {
        GetProducerLocation();
        var pp = Pp;
        int now = Frame;

        if (base.IsIdle || jetState == JetState.ReloadAmmo)
        {
            if (pp != null)
            {
                if (!allowAirLoco && pendingOrder == null && Owner.IsKindOf("PRODUCED_AT_HELIPAD") && Owner.Health == Owner.MaxHealth)
                {
                    pp.SetHealee(Owner, false);
                    allowAirLoco = true;
                    LastCommandSource = CommandSource.AI;
                    SetJetState(JetState.TakingOffAwaitClearance, clear: true);
                }
                else
                    pp.SetHealee(Owner, !allowAirLoco);
            }

            if (IsOutOfSpecialReloadAmmo && allowAirLoco)
            {
                returnToBaseFrame = 0;
                useSpecialReturnLoco = true;
                LastCommandSource = CommandSource.AI;
                SetJetState(JetState.ReturningForLanding, clear: true);
            }
            else if (pendingOrder != null && jetState != JetState.ReloadAmmo)
            {
                returnToBaseFrame = 0;
                var order = pendingOrder;
                pendingOrder = null;
                order();
            }
            else if (returnToBaseFrame != 0 && now >= returnToBaseFrame && allowAirLoco)
            {
                returnToBaseFrame = 0;
                useSpecialReturnLoco = false;
                LastCommandSource = CommandSource.AI;
                SetJetState(JetState.ReturningForLanding, clear: true);
            }
            else if (returnToBaseFrame == 0 && returnToBaseIdleFrames > 0 && allowAirLoco)
            {
                returnToBaseFrame = now + returnToBaseIdleFrames;
            }
        }
        else
        {
            pp?.SetHealee(Owner, false);
            returnToBaseFrame = 0;
            if (allowInterruptForReload && IsOutOfSpecialReloadAmmo && allowAirLoco)
            {
                // Guarding: go and reload, then carry on guarding.
                useSpecialReturnLoco = true;
                allowInterruptForReload = false;
                var resume = MostRecentGuard();
                pendingOrder = resume;
                LastCommandSource = CommandSource.AI;
                SetJetState(JetState.ReturningForLanding, clear: true);
            }
        }

        // Planes sitting on the ground are drawn at least MinHeight up (their wheels).
        float min = minHeight + (pp?.LandingDeckHeightOffset ?? 0);
        bool checkMinHeight = jetState != JetState.None || !Owner.IsAboveTerrain || !allowAirLoco;
        if (checkMinHeight || Owner.HasStatus("DECK_HEIGHT_OFFSET"))
        {
            float height = Owner.IsAboveTerrain ? Owner.HeightAboveTerrain : 0;
            Owner.DrawHeightOffset = height < min ? min - height : 0;
        }
        else
            Owner.DrawHeightOffset = 0;

        Owner.SetCondition("JETEXHAUST", (physics?.VelocityMagnitude ?? 0) > 0 && allowAirLoco);

        if (Owner.HasStatus(Status.IsAttacking))
        {
            attackLocoExpireFrame = now + attackLocoPersistFrames;
            attackersMissExpireFrame = now + attackersMissPersistFrames;
        }
        else
        {
            if (attackLocoExpireFrame != 0 && now >= attackLocoExpireFrame)
                attackLocoExpireFrame = 0;
            if (attackersMissExpireFrame != 0 && now >= attackersMissExpireFrame)
                attackersMissExpireFrame = 0;
        }

        if (attackLocoExpireFrame != 0)
            SetLocomotorSet(attackingLoco);
        else if (useSpecialReturnLoco)
            SetLocomotorSet(returningLoco);

        base.Update();
        return 0;
    }

    Action? lastGuard;

    Action? MostRecentGuard() => lastGuard;

    // ---- The jet state machine ----

    protected override bool UpdateDerivedState()
    {
        if (jetState == JetState.None)
            return false;
        var result = UpdateState(jetState);
        if (result != Result.Continue)
            SetJetState(Next(jetState, result));
        return jetState != JetState.None;
    }

    void ClearJetState()
    {
        if (jetState != JetState.None)
            ExitState(jetState);
        jetState = JetState.None;
    }

    void SetJetState(JetState next, bool clear = false)
    {
        if (clear)
        {
            var source = LastCommandSource;
            ResetToIdle(source);
        }
        for (int guard = 0; guard < 16; guard++)
        {
            if (jetState != JetState.None)
                ExitState(jetState);
            jetState = next;
            if (next == JetState.None)
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
        ClearJetState();
        State = AIState.Idle;
    }

    /// <summary>The original's state tables (JetAIStateMachine and HeliAIStateMachine).</summary>
    JetState Next(JetState state, Result result)
    {
        bool ok = result == Result.Success;
        if (needsRunway)
        {
            return state switch
            {
                JetState.ReturningForLanding => ok ? JetState.LandingAwaitClearance : JetState.ReturnToDeadAirfield,
                JetState.TakingOffAwaitClearance => ok ? JetState.TaxiToTakeoff : JetState.None,
                JetState.TaxiToTakeoff => ok ? JetState.PauseBeforeTakeoff : JetState.None,
                JetState.PauseBeforeTakeoff => ok ? JetState.TakingOff : JetState.None,
                JetState.LandingAwaitClearance => ok ? JetState.Landing : JetState.None,
                JetState.Landing => ok ? JetState.TaxiFromLanding : JetState.None,
                JetState.TaxiFromLanding => ok ? JetState.OrientForParkingPlace : JetState.None,
                JetState.TaxiFromHangar => ok ? JetState.OrientForParkingPlace : JetState.None,
                JetState.OrientForParkingPlace => ok ? JetState.ReloadAmmo : JetState.None,
                JetState.ReturnToDeadAirfield => ok ? JetState.CirclingDeadAirfield : JetState.ReturnToDeadAirfield,
                _ => JetState.None,
            };
        }
        return state switch
        {
            JetState.ReturningForLanding => ok ? JetState.LandingAwaitClearance : JetState.ReturnToDeadAirfield,
            JetState.TakingOffAwaitClearance => ok ? JetState.TakingOff : JetState.None,
            JetState.LandingAwaitClearance => ok ? JetState.OrientForParkingPlace : JetState.None,
            JetState.OrientForParkingPlace => ok ? JetState.Landing : JetState.None,
            JetState.Landing => ok ? JetState.ReloadAmmo : JetState.None,
            JetState.ReturnToDeadAirfield => ok ? JetState.CirclingDeadAirfield : JetState.ReturnToDeadAirfield,
            _ => JetState.None,
        };
    }

    Result EnterState(JetState state) => state switch
    {
        JetState.TakingOffAwaitClearance => EnterAwaitRunway(landing: false),
        JetState.LandingAwaitClearance => needsRunway ? EnterAwaitRunway(landing: true) : Result.Success,
        JetState.TaxiToTakeoff => EnterTaxi(TaxiMode.FromParking),
        JetState.TaxiFromLanding => EnterTaxi(TaxiMode.ToParking),
        JetState.TaxiFromHangar => EnterTaxi(TaxiMode.FromHangar),
        JetState.PauseBeforeTakeoff => EnterPauseBeforeTakeoff(),
        JetState.TakingOff => needsRunway ? EnterTakeoffOrLanding(landing: false) : EnterHeliTakeoffOrLanding(landing: false),
        JetState.Landing => needsRunway ? EnterTakeoffOrLanding(landing: true) : EnterHeliTakeoffOrLanding(landing: true),
        JetState.OrientForParkingPlace => EnterParkOrient(),
        JetState.ReloadAmmo => EnterReload(),
        JetState.ReturningForLanding => EnterReturnForLanding(),
        JetState.ReturnToDeadAirfield => EnterReturnToDeadAirfield(),
        JetState.CirclingDeadAirfield => EnterCircling(),
        _ => Result.Failure,
    };

    Result UpdateState(JetState state)
    {
        if (!needsRunway && state == JetState.TakingOffAwaitClearance)
            return Result.Success;
        return state switch
        {
            JetState.TakingOffAwaitClearance => UpdateAwaitRunway(landing: false),
            JetState.LandingAwaitClearance => needsRunway ? UpdateAwaitRunway(landing: true) : Result.Success,
            JetState.TaxiToTakeoff or JetState.TaxiFromLanding or JetState.TaxiFromHangar => UpdateTaxi(),
            JetState.PauseBeforeTakeoff => UpdatePauseBeforeTakeoff(),
            JetState.TakingOff => needsRunway ? UpdateTakeoffOrLanding(landing: false) : UpdateHeliTakeoffOrLanding(),
            JetState.Landing => needsRunway ? UpdateTakeoffOrLanding(landing: true) : UpdateHeliTakeoffOrLanding(),
            JetState.OrientForParkingPlace => UpdateParkOrient(),
            JetState.ReloadAmmo => UpdateReload(),
            JetState.ReturningForLanding or JetState.ReturnToDeadAirfield => UpdateMoveTo(),
            JetState.CirclingDeadAirfield => UpdateCircling(),
            _ => Result.Failure,
        };
    }

    void ExitState(JetState state)
    {
        switch (state)
        {
            case JetState.TakingOffAwaitClearance:
            case JetState.LandingAwaitClearance:
                takeoffInProgress = landingInProgress = false;
                allowCircling = false;
                break;
            case JetState.TaxiToTakeoff:
            case JetState.TaxiFromLanding:
            case JetState.TaxiFromHangar:
                SetPrecise(false, allowInvalid: false);
                takeoffInProgress = landingInProgress = taxiInProgress = false;
                break;
            case JetState.TakingOff:
            case JetState.Landing:
                if (needsRunway)
                    ExitTakeoffOrLanding(state == JetState.Landing);
                else
                    ExitHeliTakeoffOrLanding(state == JetState.Landing);
                break;
            case JetState.OrientForParkingPlace:
                takeoffInProgress = landingInProgress = false;
                IgnoreObstacle(null);
                break;
            case JetState.PauseBeforeTakeoff:
            case JetState.ReloadAmmo:
                takeoffInProgress = landingInProgress = false;
                break;
        }
    }

    void SetPrecise(bool on, bool allowInvalid)
    {
        if (CurrentLocomotor is not { } loco)
            return;
        loco.UsePreciseZPos = on;
        loco.UltraAccurate = on;
        loco.AllowInvalidPosition = allowInvalid;
    }

    // Waiting for the runway (and a parking space).

    Result EnterAwaitRunway(bool landing)
    {
        takeoffInProgress = !landing;
        landingInProgress = landing;
        allowCircling = true;
        return Result.Continue;
    }

    Result UpdateAwaitRunway(bool landing)
    {
        if (Owner.IsEffectivelyDead)
            return Result.Failure;
        if (Pp is not { } pp)
            return Result.Success;
        if (!pp.ReserveSpace(Owner.Id, ParkingOffset))
            return Result.Failure;
        if (pp.ReserveRunway(Owner.Id, landing))
            return Result.Success;
        if (Owner.HasStatus("DECK_HEIGHT_OFFSET") && !landing
            && pp.CalcBestParkingAssignment(Owner.Id, out var bestPos, out _, out _))
        {
            // On a carrier only the plane at the front of the row gets the catapult: move up.
            taxiInProgress = true;
            allowAirLoco = false;
            SetLocomotorSet("SET_TAXIING");
            SetDirectPath([bestPos]);
            SetPrecise(true, allowInvalid: true);
            IgnoreObstacle(Game.FindObject(Owner.ProducerId));
        }
        Decelerate();
        return Result.Continue;
    }

    // Circling where the airfield was.

    Result EnterCircling()
    {
        if (!IsOutOfSpecialReloadAmmo && Owner.ProducerId == 0)
            return Result.Failure;
        Decelerate();
        checkAirfieldCountdown = HowOftenToCheckAirfield;
        if (Owner.Template.UnitSound("VoiceLowFuel") is { Length: > 0 } sound)
            Game.Events.PlaySound(sound, Owner.Position, Owner.Owner, Owner);
        return Result.Continue;
    }

    Result UpdateCircling()
    {
        Decelerate();
        if (outOfAmmoDamagePerSecond > 0)
        {
            float amount = outOfAmmoDamagePerSecond * LogicTime.SecondsPerFrame * Owner.MaxHealth;
            Owner.Damage(new DamageInfo { Amount = amount, DamageType = "UNRESISTABLE", DeathType = "NORMAL" });
        }
        if (--checkAirfieldCountdown <= 0)
        {
            checkAirfieldCountdown = HowOftenToCheckAirfield;
            if (FindSuitableAirfield() is { } airfield)
            {
                Owner.ProducerId = airfield.Id;
                return Result.Success;
            }
        }
        return Result.Continue;
    }

    // Flying somewhere (back for landing, or to where the dead airfield was).

    Result EnterReturnToDeadAirfield() => StartMove(producerLocation);

    Result EnterReturnForLanding()
    {
        var pp = Pp;
        if (pp == null)
        {
            Owner.ProducerId = 0;
            var airfield = FindSuitableAirfield();
            pp = airfield != null ? ParkingAt(airfield.Id) : null;
            if (airfield == null || pp == null)
                return Result.Failure;
            Owner.ProducerId = airfield.Id;
        }
        Vector3 goal;
        if (Owner.IsKindOf("PRODUCED_AT_HELIPAD"))
            goal = landingPosForHelipad;
        else
        {
            if (!pp.ReserveSpace(Owner.Id, ParkingOffset, out var info))
                return Result.Failure;
            goal = needsRunway ? info.RunwayApproach : info.ParkingSpace;
        }
        return StartMove(goal);
    }

    Result StartMove(Vector3 goal)
    {
        MoveInternal(goal);
        return HasPath || MathUtil.Distance2D(Owner.Position, goal) < 2 * Formats.Maps.MapFile.CellSize ? Result.Continue : Result.Failure;
    }

    Result UpdateMoveTo() => !HasPath || FollowPath() ? Result.Success : Result.Continue;

    // Taxiing on the ground.

    static bool IntersectInfiniteLine2D(float ax, float ay, float ao, float cx, float cy, float co, out float ix, out float iy)
    {
        float bx = ax + MathF.Cos(ao), by = ay + MathF.Sin(ao);
        float dx = cx + MathF.Cos(co), dy = cy + MathF.Sin(co);
        float denom = (bx - ax) * (dy - cy) - (by - ay) * (dx - cx);
        ix = iy = 0;
        if (denom == 0)
            return false;
        float r = ((ay - cy) * (dx - cx) - (ax - cx) * (dy - cy)) / denom;
        ix = ax + r * (bx - ax);
        iy = ay + r * (by - ay);
        return true;
    }

    Result EnterTaxi(TaxiMode mode)
    {
        takeoffInProgress = mode != TaxiMode.ToParking;
        landingInProgress = mode == TaxiMode.ToParking;
        taxiInProgress = true;
        if (mode == TaxiMode.ToParking)
            Owner.FindModule<CountermeasuresBehavior>()?.ReloadCountermeasures();
        allowAirLoco = false;
        SetLocomotorSet("SET_TAXIING");

        if (ParkingAt(Owner.ProducerId, out var airfield) is not { } pp)
            return Result.Success;
        if (!pp.ReserveSpace(Owner.Id, ParkingOffset, out var info))
            return Result.Failure;

        // A corner on the way between the parking space and the runway prep spot.
        Vector3 intermediate = default;
        bool hasIntermediate = false;
        float orient = MathF.Atan2(info.RunwayPrep.Y - info.ParkingSpace.Y, info.RunwayPrep.X - info.ParkingSpace.X);
        if (MathF.Abs(MathUtil.AngleDifference(orient, info.ParkingOrientation)) > MathF.PI / 128)
        {
            intermediate.Z = (info.ParkingSpace.Z + info.RunwayPrep.Z) * 0.5f;
            hasIntermediate = IntersectInfiniteLine2D(info.ParkingSpace.X, info.ParkingSpace.Y, info.ParkingOrientation,
                info.RunwayPrep.X, info.RunwayPrep.Y, info.ParkingOrientation + MathF.PI / 2, out intermediate.X, out intermediate.Y);
        }

        taxiMode = mode;
        var nodes = new List<Vector3>();
        bool onDeck = Owner.HasStatus("DECK_HEIGHT_OFFSET");
        switch (mode)
        {
            case TaxiMode.ToParking when onDeck:
                // Along the carrier's taxiway, then as far up the row as there is room.
                nodes.AddRange(pp.TaxiLocations(Owner.Id) ?? []);
                pp.CalcBestParkingAssignment(Owner.Id, out var best, out _, out _);
                nodes.Add(best);
                break;
            case TaxiMode.FromParking when onDeck:
                if (info.RunwayStart != info.RunwayPrep)
                    nodes.Add(info.RunwayStart);
                break;
            case TaxiMode.FromHangar when onDeck:
                if (Owner.HasStatus("REASSIGN_PARKING"))
                {
                    // FlightDeckBehavior moved us up the row: just roll forward.
                    Owner.SetStatus("REASSIGN_PARKING", false);
                    nodes.Add(info.RunwayPrep);
                }
                else
                {
                    if (pp.CreationLocations(Owner.Id) is not { } creation)
                        return Result.Failure;
                    nodes.AddRange(creation.Skip(1));   // the first is where we were made
                    nodes.Add(info.RunwayPrep);
                }
                break;
            case TaxiMode.ToParking:
                nodes.Add(info.RunwayPrep);
                if (hasIntermediate)
                    nodes.Add(intermediate);
                nodes.Add(info.ParkingSpace);
                break;
            case TaxiMode.FromParking:
                if (hasIntermediate)
                    nodes.Add(intermediate);
                nodes.Add(info.RunwayPrep);
                nodes.Add(info.RunwayStart);
                break;
            case TaxiMode.FromHangar:
                nodes.Add(info.ParkingSpace);
                break;
        }
        SetDirectPath(nodes);
        SetPrecise(true, allowInvalid: true);
        IgnoreObstacle(airfield);
        return Result.Continue;
    }

    TaxiMode taxiMode;

    Result UpdateTaxi()
    {
        if (Owner.IsEffectivelyDead)
            return Result.Failure;
        // A carrier plane keeps moving up the row while spaces ahead free up.
        if ((taxiMode == TaxiMode.ToParking || taxiMode == TaxiMode.FromHangar)
            && Pp is { } pp && pp.CalcBestParkingAssignment(Owner.Id, out var best, out _, out _))
            AppendPathNode(best);
        return FollowPath() ? Result.Success : Result.Continue;
    }

    // Pausing at the runway start before takeoff.

    Result EnterPauseBeforeTakeoff()
    {
        takeoffInProgress = true;
        landingInProgress = false;
        pauseWhen = pauseWhenTransfer = 0;
        waitedForTaxiId = 0;
        pauseResetTimer = pauseAfterburners = false;
        if (Pp is not { } pp || !pp.ReserveSpace(Owner.Id, ParkingOffset, out var info))
            return Result.Success;
        facePosition = info.RunwayEnd;
        return Result.Continue;
    }

    bool OtherPlaneTaxiingToTakeoff()
    {
        if (Pp is not { } pp)
            return false;
        for (int i = 0; i < pp.RunwayCount; i++)
        {
            var other = Game.FindObject(pp.RunwayReservation(i));
            if (other == null || other == Owner || other.AI is not JetAIUpdate otherJet)
                continue;
            if (otherJet.jetState == JetState.TaxiToTakeoff)
            {
                if (waitedForTaxiId == 0)
                    waitedForTaxiId = other.Id;
                return true;
            }
        }
        return false;
    }

    Result UpdatePauseBeforeTakeoff()
    {
        if (Owner.IsEffectivelyDead)
            return Result.Failure;
        float angle = MathUtil.AngleTo(Owner.Position, facePosition);
        var faced = Result.Continue;
        if (MathF.Abs(MathUtil.AngleDifference(Owner.Orientation, angle)) < 0.01f)
        {
            base.Decelerate();
            faced = Result.Success;
        }
        else
            SetGoalAngle(angle);

        if (OtherPlaneTaxiingToTakeoff())
            return Result.Continue;

        int now = Frame;
        if (!pauseResetTimer)
        {
            pauseWhen = now + takeoffPause;
            if (waitedForTaxiId == 0)
            {
                waitedForTaxiId = Owner.Id;
                pauseWhenTransfer = now + 1;
            }
            else
                pauseWhenTransfer = now + 2;
            pauseResetTimer = true;
        }
        if (!pauseAfterburners)
        {
            EnableAfterburners(true);
            pauseAfterburners = true;
        }
        if (Pp is { } pp && now >= pauseWhenTransfer)
            pp.TransferRunwayReservationToNextInLineForTakeoff(Owner.Id);
        return now >= pauseWhen ? faced : Result.Continue;
    }

    // Taking off down the runway, or landing on it.

    Result EnterTakeoffOrLanding(bool landing)
    {
        if (Owner.IsEffectivelyDead)
            return Result.Failure;
        takeoffInProgress = !landing;
        landingInProgress = landing;
        allowAirLoco = true;
        SetLocomotorSet("SET_NORMAL");
        if (CurrentLocomotor is not { } loco)
            return Result.Failure;
        loco.SetMaxLift(BigNumber);
        stateMaxLift = loco.MaxLiftFor(Owner);
        landingSoundPlayed = false;
        if (landing)
            loco.SetMaxSpeed(loco.MinSpeed);
        else
            loco.SetMaxLift(0);
        loco.UsePreciseZPos = true;
        loco.UltraAccurate = true;
        IgnoreObstacle(Game.FindObject(Owner.ProducerId));

        if (Pp is not { } pp)
            return Result.Success;
        if (!pp.ReserveSpace(Owner.Id, ParkingOffset, out var info))
            return Result.Failure;
        if (landing && !pp.ReserveRunway(Owner.Id, true))
            return Result.Failure;

        var nodes = new List<Vector3>();
        if (landing)
        {
            circleForLandingPos = info.RunwayApproach;
            circleForLandingPos.Z = (info.RunwayEnd.Z + info.RunwayApproach.Z) * 0.5f;
            nodes.Add(info.RunwayApproach);
            if (Owner.HasStatus("DECK_HEIGHT_OFFSET"))
            {
                // Carriers have their own landing strip.
                nodes.Add(info.RunwayLandingStart);
                nodes.Add(info.RunwayLandingEnd);
            }
            else
            {
                // Land the way we took off, in reverse.
                nodes.Add(info.RunwayEnd);
                nodes.Add(info.RunwayStart);
            }
        }
        else
        {
            info.RunwayEnd.Z = info.RunwayApproach.Z;
            nodes.Add(info.RunwayEnd);
            nodes.Add(info.RunwayExit);
        }
        SetDirectPath(nodes);
        return Result.Continue;
    }

    Result UpdateTakeoffOrLanding(bool landing)
    {
        if (Owner.IsEffectivelyDead || CurrentLocomotor is not { } loco)
            return Result.Failure;
        var pp = Pp;
        if (landing)
        {
            if (Owner.Position.Z > circleForLandingPos.Z)
            {
                // Still too high: head for the circling spot (the original's circle-to-descend is compiled out).
                SetGoalPositionExplicit(circleForLandingPos);
                return Result.Continue;
            }
            loco.SetMaxLift(BigNumber);
            if (!landingSoundPlayed)
            {
                float groundZ = Game.Terrain.LayerHeight(Owner.Position.X, Owner.Position.Y) + (pp?.LandingDeckHeightOffset ?? 0);
                if (Owner.Position.Z - 0.25f <= groundZ)
                {
                    landingSoundPlayed = true;
                    if (Game.Catalog.MiscAudio("AircraftWheelScreech") is { Length: > 0 } screech)
                        Game.Events.PlaySound(screech, Owner.Position, source: Owner);
                }
            }
        }
        else if (pp != null)
        {
            pp.TransferRunwayReservationToNextInLineForTakeoff(Owner.Id);
            var info = pp.CalcInfo(Owner.Id);
            float dist = Vector3.Distance(info.RunwayEnd, Owner.Position);
            float ratio = info.RunwayTakeoffDist > 0 ? 1 - dist / info.RunwayTakeoffDist : 1;
            ratio *= ratio;
            loco.SetMaxLift(stateMaxLift * Math.Clamp(ratio, 0, 1));
        }
        return FollowPath() ? Result.Success : Result.Continue;
    }

    void ExitTakeoffOrLanding(bool landing)
    {
        takeoffInProgress = landingInProgress = false;
        EnableAfterburners(false);
        if (CurrentLocomotor is { } loco)
        {
            loco.UsePreciseZPos = false;
            loco.UltraAccurate = false;
            if (!Owner.IsEffectivelyDead)
                loco.SetMaxLift(BigNumber);
            loco.SetMaxSpeed(BigNumber);
        }
        IgnoreObstacle(null);
        var pp = Pp;
        if (!landing && pp != null && !keepsParkingSpaceWhenAirborne)
            pp.ReleaseSpace(Owner.Id);
        pp?.ReleaseRunway(Owner.Id);
    }

    // Helicopters and VTOL craft rising from or settling onto their space.

    Result EnterHeliTakeoffOrLanding(bool landing)
    {
        takeoffInProgress = !landing;
        landingInProgress = landing;
        allowAirLoco = true;
        SetLocomotorSet("SET_NORMAL");
        SetPrecise(true, allowInvalid: false);
        IgnoreObstacle(Game.FindObject(Owner.ProducerId));

        if (Pp is not { } pp)
            return Result.Success;
        Vector3 approach;
        if (Owner.IsKindOf("PRODUCED_AT_HELIPAD"))
        {
            heliParkingLoc = landing ? landingPosForHelipad : Owner.Position;
            heliParkingOrientation = Owner.Orientation;
            approach = heliParkingLoc;
            approach.Z += pp.ApproachHeight + pp.LandingDeckHeightOffset;
        }
        else
        {
            if (!pp.ReserveSpace(Owner.Id, ParkingOffset, out var info))
                return Result.Failure;
            heliParkingLoc = info.ParkingSpace;
            heliParkingOrientation = info.ParkingOrientation;
            approach = heliParkingLoc;
            approach.Z += info.RunwayApproach.Z - info.RunwayEnd.Z;
        }
        heliPath[0] = landing ? approach : heliParkingLoc;
        heliPath[1] = landing ? heliParkingLoc : approach;
        heliIndex = 0;
        return Result.Continue;
    }

    Result UpdateHeliTakeoffOrLanding()
    {
        if (Owner.IsEffectivelyDead)
            return Result.Failure;
        physics?.ScrubVelocity2D(0);
        // As in the original, it is simply placed over the spot and moved up or down.
        var hover = heliPath[heliIndex] with { Z = Owner.Position.Z };
        Owner.SetPosition(hover);
        Owner.SetOrientation(heliParkingOrientation);
        SetGoalPositionExplicit(heliPath[heliIndex]);
        if (Vector3.DistanceSquared(Owner.Position, heliPath[heliIndex]) <= 3 * 3)
            heliIndex++;
        return heliIndex >= 2 ? Result.Success : Result.Continue;
    }

    void ExitHeliTakeoffOrLanding(bool landing)
    {
        takeoffInProgress = landingInProgress = false;
        if (CurrentLocomotor is { } loco)
        {
            loco.UsePreciseZPos = false;
            loco.UltraAccurate = false;
            if (!Owner.IsEffectivelyDead)
                loco.SetMaxLift(BigNumber);
        }
        IgnoreObstacle(null);
        if (landing)
        {
            allowAirLoco = false;
            SetLocomotorSet("SET_TAXIING");
        }
        else if (Pp is { } pp && !keepsParkingSpaceWhenAirborne)
            pp.ReleaseSpace(Owner.Id);
    }

    // Turning to face out of the parking space.

    Result EnterParkOrient()
    {
        if (Owner.IsKindOf("PRODUCED_AT_HELIPAD"))
            return Result.Success;
        takeoffInProgress = false;
        landingInProgress = true;
        IgnoreObstacle(Game.FindObject(Owner.ProducerId));
        return Result.Continue;
    }

    Result UpdateParkOrient()
    {
        if (Owner.IsEffectivelyDead || Pp is not { } pp)
            return Result.Failure;
        if (!pp.ReserveSpace(Owner.Id, ParkingOffset, out var info))
            return Result.Failure;
        if (MathF.Abs(MathUtil.AngleDifference(Owner.Orientation, info.ParkingOrientation)) <= 0.001f)
            return Result.Success;
        physics?.ScrubVelocity2D(0);
        var hover = (Owner.HasStatus("DECK_HEIGHT_OFFSET") ? info.RunwayPrep : info.ParkingSpace) with { Z = Owner.Position.Z };
        Owner.SetPosition(hover);
        SetGoalAngle(info.ParkingOrientation);
        return Result.Continue;
    }

    // Reloading in the parking space.

    Result EnterReload()
    {
        takeoffInProgress = landingInProgress = false;
        useSpecialReturnLoco = false;
        reloadTime = 0;
        foreach (var weapon in Owner.Weapons.All)
        {
            int time = weapon.ClipReloadFramesFor;
            if (weapon.ClipSize > 0)
                time = time * (weapon.ClipSize - weapon.RemainingAmmo) / weapon.ClipSize;
            reloadTime = Math.Max(reloadTime, time);
        }
        reloadTime = Math.Max(reloadTime, 1);
        reloadDoneFrame = Frame + reloadTime;
        return Result.Continue;
    }

    Result UpdateReload()
    {
        int now = Frame;
        bool allDone = true;
        foreach (var weapon in Owner.Weapons.All)
        {
            weapon.SetClipPercentFull(now >= reloadDoneFrame ? 1f : (float)(reloadTime - (reloadDoneFrame - now)) / reloadTime);
            if (weapon.ClipSize > 0 && weapon.RemainingAmmo != weapon.ClipSize)
                allDone = false;
        }
        return allDone ? Result.Success : Result.Continue;
    }

    /// <summary>Removing the plane frees its parking space.</summary>
    public void OnDelete() => Pp?.ReleaseSpace(Owner.Id);
}

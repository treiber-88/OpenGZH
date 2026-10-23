using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.AI;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules.Production;

/// <summary>
/// An aircraft carrier's flight deck (the original's FlightDeckBehavior). It is the carrier's AI: the
/// player's attack, guard and stop orders become standing orders for its jets (PayloadTemplate), which
/// start parked in rows of NumSpacesPerRunway behind each runway's catapult. The front jet of a row is
/// launched (ramp up, catapult steam) every LaunchWaveDelay while there are orders; the rest move up the
/// row as spaces free (every ParkingCleanupPeriod); landed jets taxi back along RunwayNTaxi and are
/// repaired; lost jets are replaced (ReplacementDelay), coming up the lift along RunwayNCreation.
/// </summary>
public sealed class FlightDeckBehavior : AIUpdateInterface, IAirfield, IProductionExit, IDieModule
{
    const int MaxRunways = 2;
    static readonly int HealRateFrames = LogicTime.FramesPerSecond / 5;

    sealed class DeckSpace
    {
        public Vector3 Prep;
        public float Orientation;
        public int Runway;
        public uint ObjectInSpace;
    }

    sealed class Runway
    {
        public Vector3 Start, End, LandingStart, LandingEnd;
        public uint InUseByForTakeoff, InUseByForLanding;
        public readonly List<Vector3> Taxi = new();
        public readonly List<Vector3> Creation = new();
        public float StartOrient;
    }

    readonly List<DeckSpace> spaces = new();
    readonly List<Runway> runways = new();
    readonly List<(uint Id, int StartFrame)> healing = new();
    bool gotInfo;
    int numRows, numCols;
    int nextHealFrame = int.MaxValue, nextCleanupFrame, nextAllowedProductionFrame;
    ObjectTemplate? payload;

    // The standing order for the jets (the original's designated command, target and position).
    AIOrderType? designatedCommand;
    uint designatedTarget;
    Vector3 designatedPosition;

    readonly int[] nextLaunchWaveFrame = new int[MaxRunways];
    readonly int[] rampUpFrame = new int[MaxRunways];
    readonly int[] catapultSystemFrame = new int[MaxRunways];
    readonly int[] lowerRampFrame = new int[MaxRunways];
    readonly bool[] rampUp = new bool[MaxRunways];

    public float ApproachHeight => FloatSetting("ApproachHeight");
    public float LandingDeckHeightOffset => FloatSetting("LandingDeckHeightOffset");
    public int RunwayCount => runways.Count;

    public override void OnCreate()
    {
        base.OnCreate();
        numRows = IntSetting("NumSpacesPerRunway");
        numCols = Math.Min(MaxRunways, IntSetting("NumRunways"));
        for (int i = 0; i < MaxRunways; i++)
            catapultSystemFrame[i] = lowerRampFrame[i] = int.MaxValue;
    }

    static float ZRotation(Matrix4x4 m) => MathF.Atan2(m.M12, m.M11);

    Vector3 Bone(string name) => Game.Bones.BonePosition(Owner, name);

    /// <summary>Reads the deck from the bones and fills it with jets (the original's buildInfo).</summary>
    void BuildInfo(bool createUnits = true)
    {
        if (gotInfo)
            return;
        payload = Game.Templates.Find(Setting("PayloadTemplate"));
        if (Owner.IsUnderConstruction || Owner.HasStatus(Status.Sold))
            return;
        var spaceBones = new List<string>[numCols];
        for (int col = 0; col < numCols; col++)
            spaceBones[col] = RuleValue.Words(Setting($"Runway{col + 1}Spaces")).ToList();
        for (int row = 0; row < numRows; row++)
        {
            for (int col = 0; col < numCols; col++)
            {
                var space = new DeckSpace { Runway = col };
                string bone = row < spaceBones[col].Count ? spaceBones[col][row] : "";
                if (Game.Bones.TryGetBone(Owner, bone, out var mtx))
                {
                    space.Prep = mtx.Translation;
                    space.Orientation = ZRotation(mtx);
                }
                else
                    space.Prep = Owner.Position;
                if (payload != null && createUnits)
                {
                    var jet = Game.CreateObject(payload, Owner.Owner, space.Prep, space.Orientation, Owner.Owner.DefaultTeam);
                    jet.ProducerId = Owner.Id;
                    jet.SetStatus("DECK_HEIGHT_OFFSET");
                    space.ObjectInSpace = jet.Id;
                }
                spaces.Add(space);
            }
        }
        for (int col = 0; col < numCols; col++)
        {
            string[] takeoff = RuleValue.Words(Setting($"Runway{col + 1}Takeoff"));
            string[] landing = RuleValue.Words(Setting($"Runway{col + 1}Landing"));
            var runway = new Runway
            {
                Start = takeoff.Length > 0 ? Bone(takeoff[0]) : Owner.Position,
                End = takeoff.Length > 1 ? Bone(takeoff[1]) : Owner.Position,
                LandingStart = landing.Length > 0 ? Bone(landing[0]) : Owner.Position,
                LandingEnd = landing.Length > 1 ? Bone(landing[1]) : Owner.Position,
            };
            foreach (string name in RuleValue.Words(Setting($"Runway{col + 1}Taxi")))
                runway.Taxi.Add(Bone(name));
            bool first = true;
            foreach (string name in RuleValue.Words(Setting($"Runway{col + 1}Creation")))
            {
                Game.Bones.TryGetBone(Owner, name, out var mtx);
                if (first)
                {
                    first = false;
                    runway.StartOrient = ZRotation(mtx);
                }
                runway.Creation.Add(mtx.Translation == Vector3.Zero ? Owner.Position : mtx.Translation);
            }
            runways.Add(runway);
        }
        gotInfo = true;
    }

    bool IsGone(uint id) => Game.FindObject(id) is not { IsEffectivelyDead: false };

    void PurgeDead()
    {
        BuildInfo();
        foreach (var space in spaces)
            if (space.ObjectInSpace != 0 && IsGone(space.ObjectInSpace))
                space.ObjectInSpace = 0;
        foreach (var runway in runways)
        {
            if (runway.InUseByForTakeoff != 0 && IsGone(runway.InUseByForTakeoff))
                runway.InUseByForTakeoff = 0;
            if (runway.InUseByForLanding != 0 && IsGone(runway.InUseByForLanding))
                runway.InUseByForLanding = 0;
        }
        if (healing.RemoveAll(h => IsGone(h.Id)) > 0)
            ResetWakeFrame();
    }

    DeckSpace? FindSpace(uint id) => !gotInfo || id == 0 ? null : spaces.FirstOrDefault(s => s.ObjectInSpace == id);

    // ---- IAirfield ----

    public bool HasReservedSpace(uint id) => FindSpace(id) != null;

    public bool HasAvailableSpaceFor(ObjectTemplate unit) => gotInfo && spaces.Any(s => s.ObjectInSpace == 0 || IsGone(s.ObjectInSpace));

    public bool ReserveSpace(uint id, float parkingOffset = 0) => ReserveSpace(id, parkingOffset, out _);

    public bool ReserveSpace(uint id, float parkingOffset, out ParkingInfo info)
    {
        info = default;
        BuildInfo();
        PurgeDead();
        var space = FindSpace(id) ?? spaces.FirstOrDefault(s => s.ObjectInSpace == 0);
        if (space == null)
            return false;
        space.ObjectInSpace = id;
        if (LandingDeckHeightOffset != 0)
            Game.FindObject(id)?.SetStatus("DECK_HEIGHT_OFFSET");
        info = CalcInfo(id);
        if (parkingOffset != 0)
            info.ParkingSpace += new Vector3(MathF.Cos(space.Orientation), MathF.Sin(space.Orientation), 0) * parkingOffset;
        return true;
    }

    public ParkingInfo CalcInfo(uint id)
    {
        var info = new ParkingInfo();
        if (FindSpace(id) is not { } space)
            return info;
        var rr = runways[space.Runway];
        const float approachDist = 0.75f;
        info.ParkingSpace = info.RunwayPrep = space.Prep;
        info.ParkingOrientation = space.Orientation;
        info.RunwayStart = rr.Start;
        info.RunwayEnd = rr.End;
        info.RunwayExit = rr.End + (rr.End - rr.Start) * approachDist;
        info.RunwayExit.Z = rr.End.Z + ApproachHeight + LandingDeckHeightOffset;
        info.RunwayLandingStart = rr.LandingStart;
        info.RunwayLandingEnd = rr.LandingEnd;
        info.RunwayApproach = rr.LandingStart + (rr.LandingStart - rr.LandingEnd) * approachDist;
        info.RunwayApproach.Z = rr.LandingStart.Z + ApproachHeight + LandingDeckHeightOffset;
        info.RunwayTakeoffDist = Vector3.Distance(info.RunwayStart, info.RunwayEnd);
        // The jet on the catapult is already at its start.
        if (runways.Any(r => r.InUseByForTakeoff == id))
            info.RunwayStart = info.RunwayPrep;
        return info;
    }

    public void ReleaseSpace(uint id)
    {
        BuildInfo();
        PurgeDead();
        foreach (var space in spaces.Where(s => s.ObjectInSpace == id))
            space.ObjectInSpace = 0;
        Game.FindObject(id)?.SetStatus("DECK_HEIGHT_OFFSET", false);
    }

    public uint RunwayReservation(int runway)
    {
        BuildInfo();
        PurgeDead();
        return runway < runways.Count ? runways[runway].InUseByForTakeoff : 0;
    }

    public uint LandingReservation(int runway) => runway < runways.Count ? runways[runway].InUseByForLanding : 0;

    // (Empty in the original: carriers launch one at a time from the front of each row.)
    public void TransferRunwayReservationToNextInLineForTakeoff(uint id) { }

    public bool ReserveRunway(uint id, bool forLanding)
    {
        BuildInfo();
        PurgeDead();
        int runway = -1;
        // Taking off needs a front-row space (the catapults); landing any space.
        var candidates = forLanding ? spaces : spaces.Take(numCols);
        foreach (var space in candidates)
        {
            if (space.ObjectInSpace == id)
            {
                runway = space.Runway;
                break;
            }
        }
        if (runway == -1)
            return false;
        var info = runways[runway];
        if (!forLanding && info.InUseByForTakeoff == id || forLanding && info.InUseByForLanding == id)
            return true;
        if (!forLanding && info.InUseByForTakeoff == 0)
        {
            info.InUseByForTakeoff = id;
            return true;
        }
        if (forLanding && info.InUseByForLanding == 0)
        {
            info.InUseByForLanding = id;
            return true;
        }
        return false;
    }

    public void ReleaseRunway(uint id)
    {
        BuildInfo();
        PurgeDead();
        foreach (var runway in runways)
        {
            if (runway.InUseByForTakeoff == id)
                runway.InUseByForTakeoff = 0;
            if (runway.InUseByForLanding == id)
                runway.InUseByForLanding = 0;
        }
    }

    int RunwayOf(uint id) => spaces.FirstOrDefault(s => s.ObjectInSpace == id)?.Runway ?? -1;

    public IReadOnlyList<Vector3>? TaxiLocations(uint id) => RunwayOf(id) is var r and >= 0 ? runways[r].Taxi : null;

    public IReadOnlyList<Vector3>? CreationLocations(uint id) => RunwayOf(id) is var r and >= 0 ? runways[r].Creation : null;

    /// <summary>A jet that is busy elsewhere (flying, or about to) may lose its space to one moving up.</summary>
    bool IsAbleToGiveUpParkingSpace(GameObject? jet)
    {
        if (jet == null || jet.IsAirborneTarget)
            return true;
        if (jet.AI is JetAIUpdate ai && !ai.IsIdle && !ai.IsTaxiingToParking)
        {
            if (ai.PendingOrderType == AIOrderType.Enter || designatedCommand == AIOrderType.Stop)
            {
                ai.PurgePendingOrder();
                return false;
            }
            return !runways.Any(r => r.InUseByForTakeoff == jet.Id);
        }
        return false;
    }

    bool IsInPositionToTakeoff(GameObject jet)
    {
        if (jet.AI == null)
            return false;
        for (int i = 0; i < numCols && i < spaces.Count; i++)
            if (spaces[i].ObjectInSpace == jet.Id)
                return MathUtil.DistanceSquared2D(jet.Position, spaces[i].Prep) < 10;
        return false;
    }

    static bool IsAbleToMoveForward(GameObject jet) =>
        jet.AI is JetAIUpdate ai && !jet.IsAirborneTarget && (ai.IsIdle || ai.IsReloading);

    /// <summary>
    /// Swaps the jet into the frontmost space of its row that it can reach without passing a jet that
    /// is staying put (the original's calcBestParkingAssignment).
    /// </summary>
    public bool CalcBestParkingAssignment(uint id, out Vector3 position, out int oldIndex, out int newIndex)
    {
        position = default;
        newIndex = -1;
        int myIndex = spaces.FindIndex(s => s.ObjectInSpace == id);
        oldIndex = myIndex < 0 ? spaces.Count : myIndex;
        if (myIndex < 0)
            return false;
        var mine = spaces[myIndex];
        int runway = mine.Runway;
        position = mine.Prep;
        bool checkForPlaneInWay = false;
        DeckSpace? best = null;
        GameObject? bestJet = null;
        int bestIndex = 0;
        for (int index = 0; index < spaces.Count; index++)
        {
            var that = spaces[index];
            var nonIdleJet = Game.FindObject(that.ObjectInSpace);
            if (that == mine)
            {
                if (best == null)
                    return false;
                mine.ObjectInSpace = bestJet?.Id ?? 0;
                best.ObjectInSpace = id;
                newIndex = bestIndex;
                return true;
            }
            if (that.Runway != runway)
                continue;
            if (nonIdleJet == null || IsAbleToGiveUpParkingSpace(nonIdleJet))
            {
                if (!checkForPlaneInWay)
                {
                    best = that;
                    bestJet = nonIdleJet;
                    bestIndex = index;
                    checkForPlaneInWay = true;
                    position = that.Prep;
                }
            }
            else if (checkForPlaneInWay)
            {
                checkForPlaneInWay = false;
                position = mine.Prep;
                best = null;
            }
        }
        return false;
    }

    void ResetWakeFrame() => nextHealFrame = healing.Count == 0 ? int.MaxValue : Frame + HealRateFrames;

    public void SetHealee(GameObject healee, bool add)
    {
        if (add)
        {
            if (healing.Any(h => h.Id == healee.Id))
                return;
            healing.Add((healee.Id, Frame));
            ResetWakeFrame();
        }
        else if (healing.RemoveAll(h => h.Id == healee.Id) > 0)
            ResetWakeFrame();
    }

    bool TakeoffOrLandingInProgress(GameObject jet) => jet.AI is JetAIUpdate ai && ai.IsTakeoffOrLandingInProgress;

    public void DefectAllParkedUnits(Players.Team newTeam, int detectionFrames)
    {
        BuildInfo();
        PurgeDead();
        foreach (var space in spaces.ToList())
        {
            if (Game.FindObject(space.ObjectInSpace) is not { IsEffectivelyDead: false } jet)
                continue;
            if (jet.IsAboveTerrain && !TakeoffOrLandingInProgress(jet))
            {
                if (newTeam.Owner != jet.Owner)
                {
                    ReleaseSpace(jet.Id);
                    if (jet.ProducerId == Owner.Id)
                        jet.ProducerId = 0;
                }
            }
            else
                jet.Defect(newTeam, detectionFrames);
        }
        PurgeDead();
    }

    public void KillAllParkedUnits()
    {
        BuildInfo();
        PurgeDead();
        foreach (var space in spaces.ToList())
        {
            if (Game.FindObject(space.ObjectInSpace) is not { IsEffectivelyDead: false } jet)
                continue;
            if (jet.IsAboveTerrain && !TakeoffOrLandingInProgress(jet))
                continue;
            jet.Kill();
        }
        PurgeDead();
    }

    public void OnDie(DamageInfo killingBlow) => KillAllParkedUnits();

    // ---- The deck crew ----

    public override int Update()
    {
        BuildInfo();
        PurgeDead();
        int now = Frame;

        if (now >= nextHealFrame)
        {
            nextHealFrame = now + HealRateFrames;
            float amount = HealRateFrames * FloatSetting("HealAmountPerSecond") * LogicTime.SecondsPerFrame;
            foreach (var (id, _) in healing.ToList())
                Game.FindObject(id)?.Body?.AttemptHealing(amount, Owner.Id);
        }

        if (now >= nextCleanupFrame)
            MoveJetsUpTheRows(now);

        // Replace a lost jet (one at a time).
        if (spaces.Any(s => s.ObjectInSpace == 0) && Owner.FindModule<ProductionUpdate>() is { } pu
            && pu.Queue.Count == 0 && now >= nextAllowedProductionFrame && payload != null)
        {
            pu.QueueUnit(payload);
            nextAllowedProductionFrame = now + FramesSetting("ReplacementDelay") + FramesSetting("DockAnimationDelay");
        }

        Owner.SetStatus("NO_ATTACK", !spaces.Any(s => s.ObjectInSpace != 0));
        LaunchFromCatapults(now);
        return 0;
    }

    /// <summary>Parked jets roll forward into spaces left by jets that took off (one per row each time).</summary>
    void MoveJetsUpTheRows(int now)
    {
        nextCleanupFrame = now + FramesSetting("ParkingCleanupPeriod");
        var complete = new bool[MaxRunways];
        for (int spaceId = 0; spaceId < spaces.Count; spaceId++)
        {
            var space = spaces[spaceId];
            var nonIdleJet = Game.FindObject(space.ObjectInSpace);
            if (nonIdleJet != null && !IsAbleToGiveUpParkingSpace(nonIdleJet))
                continue;
            // The space behind it in the same row (numCols further on).
            int behind = spaceId + numCols;
            if (behind >= spaces.Count || complete[space.Runway])
                continue;
            var temp = spaces[behind];
            if (Game.FindObject(temp.ObjectInSpace) is { } parkedJet && IsAbleToMoveForward(parkedJet))
            {
                space.ObjectInSpace = parkedJet.Id;
                temp.ObjectInSpace = nonIdleJet?.Id ?? 0;
                parkedJet.SetStatus("REASSIGN_PARKING");
                parkedJet.AI?.FollowExitProductionPath([space.Prep], Owner, CommandSource.AI);
                complete[space.Runway] = true;
                nextCleanupFrame = now + FramesSetting("HumanFollowPeriod");
            }
        }
    }

    void LaunchFromCatapults(int now)
    {
        for (int i = 0; i < numCols && i < spaces.Count; i++)
        {
            var jet = Game.FindObject(spaces[i].ObjectInSpace);
            if (jet != null && !IsAbleToGiveUpParkingSpace(jet) && IsInPositionToTakeoff(jet) && HasTakeoffOrders()
                && nextLaunchWaveFrame[i] <= now)
            {
                if (!rampUp[i])
                {
                    rampUp[i] = true;
                    rampUpFrame[i] = now + FramesSetting("LaunchRampDelay");
                    lowerRampFrame[i] = int.MaxValue;
                    SetRamp(i, up: true);
                }
                if (rampUp[i] && rampUpFrame[i] <= now && jet.AI != null)
                {
                    PropagateOrderToSpecificPlane(jet);
                    nextLaunchWaveFrame[i] = now + FramesSetting("LaunchWaveDelay");
                    catapultSystemFrame[i] = now + FramesSetting("CatapultFireDelay");
                    lowerRampFrame[i] = now + FramesSetting("LowerRampDelay");
                }
            }
            if (catapultSystemFrame[i] <= now)
            {
                catapultSystemFrame[i] = int.MaxValue;
                string steam = Setting($"Runway{i + 1}CatapultSystem");
                if (steam.Length > 0 && i < runways.Count)
                    Game.Events.ParticleSystem(steam, runways[i].Start);
            }
            if (rampUp[i] && lowerRampFrame[i] <= now)
            {
                rampUp[i] = false;
                SetRamp(i, up: false);
            }
        }
    }

    /// <summary>The blast deflector of runway i rises (DOOR_2 for the first runway, DOOR_3 for the second).</summary>
    void SetRamp(int runway, bool up)
    {
        int door = runway + 2;
        Owner.SetCondition($"DOOR_{door}_OPENING", up);
        Owner.SetCondition($"DOOR_{door}_CLOSING", !up);
    }

    // ---- IProductionExit: replacement jets come up the lift ----

    public Vector3? RallyPoint { get; set; }
    public bool CanExitNow => true;
    public bool CanReserveFor(ObjectTemplate unit) => true;
    public int ReserveDoorForExit(ObjectTemplate unit) => 0;
    public void Exit(GameObject unit) => ExitViaDoor(unit, 0);

    public void ExitViaDoor(GameObject jet, int door)
    {
        var space = spaces.FirstOrDefault(s => s.ObjectInSpace == 0);
        if (space == null)
            return;
        space.ObjectInSpace = jet.Id;
        jet.ProducerId = Owner.Id;
        float parkingOffset = (jet.AI as JetAIUpdate)?.ParkingOffset ?? 0;
        ReserveSpace(jet.Id, parkingOffset);
        if (CreationLocations(jet.Id) is not { Count: > 0 } creation)
            return;
        jet.SetPosition(creation[0]);
        jet.SetOrientation(runways[space.Runway].StartOrient);
        jet.AI?.FollowExitProductionPath([space.Prep], Owner, CommandSource.AI);
    }

    // ---- Orders: the carrier passes them on to its jets ----

    protected override bool OnOrder(AIOrder order, Action replay)
    {
        if (order.Source == CommandSource.AI)
            return true;
        switch (order.Type)
        {
            case AIOrderType.Guard:
            case AIOrderType.AttackPosition:
            case AIOrderType.AttackMoveTo:
                designatedTarget = 0;
                designatedPosition = order.Position ?? Owner.Position;
                designatedCommand = order.Type;
                PropagateOrdersToPlanes();
                break;
            case AIOrderType.AttackObject:
                designatedTarget = order.TargetId;
                designatedPosition = Vector3.Zero;
                designatedCommand = order.Type;
                PropagateOrdersToPlanes();
                break;
            case AIOrderType.Stop:
                designatedTarget = 0;
                designatedPosition = Vector3.Zero;
                designatedCommand = order.Type;
                PropagateOrdersToPlanes();
                break;
            default:
                designatedCommand = null;
                break;
        }
        return true;
    }

    void PropagateOrdersToPlanes()
    {
        foreach (var space in spaces.ToList())
            if (Game.FindObject(space.ObjectInSpace) is { } jet && IsAbleToGiveUpParkingSpace(jet))
                PropagateOrderToSpecificPlane(jet);
    }

    bool HasTakeoffOrders()
    {
        switch (designatedCommand)
        {
            case AIOrderType.Guard:
            case AIOrderType.AttackPosition:
            case AIOrderType.AttackMoveTo:
                return true;
            case AIOrderType.AttackObject:
                if (Game.FindObject(designatedTarget) != null)
                    return true;
                designatedCommand = null;
                designatedTarget = 0;
                return false;
            default:
                return false;
        }
    }

    void PropagateOrderToSpecificPlane(GameObject jet)
    {
        if (jet.AI is not { } ai)
            return;
        switch (designatedCommand)
        {
            case AIOrderType.Guard:
                ai.Guard(designatedPosition, CommandSource.AI);
                break;
            case AIOrderType.AttackPosition:
                ai.AttackPosition(designatedPosition, CommandSource.AI);
                break;
            case AIOrderType.AttackObject:
                if (Game.FindObject(designatedTarget) is { } target)
                    ai.AttackObject(target, CommandSource.Player);
                break;
            case AIOrderType.AttackMoveTo:
                ai.AttackMoveTo(designatedPosition, CommandSource.AI);
                break;
            case AIOrderType.Stop:
                ai.Enter(Owner, CommandSource.AI);
                break;
        }
    }
}

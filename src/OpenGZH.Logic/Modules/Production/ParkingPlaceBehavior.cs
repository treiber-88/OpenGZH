using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules.Production;

/// <summary>Where a plane parks, taxis and takes off at its airfield (the original's PPInfo).</summary>
public struct ParkingInfo
{
    public Vector3 ParkingSpace;
    public float ParkingOrientation;
    public Vector3 RunwayPrep;
    public Vector3 RunwayStart;
    public Vector3 RunwayEnd;
    public Vector3 RunwayExit;
    public Vector3 RunwayApproach;
    /// <summary>Carriers land on a separate strip (the original's runwayLandingStart/End).</summary>
    public Vector3 RunwayLandingStart;
    public Vector3 RunwayLandingEnd;
    public Vector3 HangarInternal;
    public float HangarInternalOrient;
    public float RunwayTakeoffDist;
}

/// <summary>
/// What planes need from where they are based (the original's ParkingPlaceBehaviorInterface):
/// airfields (<see cref="ParkingPlaceBehavior"/>) and aircraft carriers (FlightDeckBehavior).
/// </summary>
public interface IAirfield : IParkingPlace
{
    float ApproachHeight { get; }
    float LandingDeckHeightOffset { get; }
    int RunwayCount { get; }
    bool ReserveSpace(uint id, float parkingOffset, out ParkingInfo info);
    bool ReserveSpace(uint id, float parkingOffset = 0);
    ParkingInfo CalcInfo(uint id);
    void ReleaseSpace(uint id);
    /// <summary>Who holds a runway for takeoff.</summary>
    uint RunwayReservation(int runway);
    void TransferRunwayReservationToNextInLineForTakeoff(uint id);
    bool ReserveRunway(uint id, bool forLanding);
    void ReleaseRunway(uint id);
    void SetHealee(GameObject healee, bool add);
    /// <summary>Carrier deck: the taxi route from the landing strip to the parking row.</summary>
    IReadOnlyList<Vector3>? TaxiLocations(uint id) => null;
    /// <summary>Carrier deck: where a new plane appears (the hangar lift) and its route to the row.</summary>
    IReadOnlyList<Vector3>? CreationLocations(uint id) => null;
    /// <summary>Carrier deck: moves the plane up the row to the best free space (the original's calcBestParkingAssignment).</summary>
    bool CalcBestParkingAssignment(uint id, out Vector3 position, out int oldIndex, out int newIndex)
    {
        position = default;
        oldIndex = newIndex = -1;
        return false;
    }
}

/// <summary>
/// An airfield's (or helipad's) parking spaces and runways. NumRows x NumCols spaces, read from the
/// model's bones (Runway{col}Parking{row}, Runway{col}Prep{row}, Runway{col}Park{row}Han, and
/// RunwayStart{col}/RunwayEnd{col}); each column of spaces shares one runway. New planes are built
/// into a space (reserved when they are queued), taxi out of the hangar, and keep their space while
/// flying unless KeepsParkingSpaceWhenAirborne is No. Parked planes are repaired at HealAmountPerSecond.
/// One plane may use a runway at a time, with one more waiting next in line to take off.
/// If the airfield dies, the planes on the ground die with it.
/// </summary>
public sealed class ParkingPlaceBehavior : Module, IUpdateModule, IDieModule, IAirfield, IProductionExit
{
    const int HealRateFrames = LogicTime.FramesPerSecond / 5;
    const float ApproachDist = 0.75f;

    sealed class Space
    {
        public Vector3 HangarStart;
        public float HangarStartOrient;
        public Vector3 Location;
        public Vector3 Prep;
        public float Orientation;
        public int Runway;
        public int Door;
        public uint ObjectInSpace;
        public bool ReservedForExit;
    }

    sealed class Runway
    {
        public Vector3 Start;
        public Vector3 End;
        public uint InUseBy;
        public uint NextInLineForTakeoff;
        public bool WasInLine;
    }

    float healAmount;
    int numRows, numCols;
    bool hasRunways, parkInHangars;

    readonly List<Space> spaces = new();
    readonly List<Runway> runways = new();
    readonly List<(uint Id, int StartFrame)> healing = new();
    int nextHealFrame = int.MaxValue;
    bool gotInfo;
    Vector3? heliRallyPoint;

    public float ApproachHeight { get; private set; }
    public float LandingDeckHeightOffset { get; private set; }
    public int RunwayCount => runways.Count;

    protected override void Initialize()
    {
        healAmount = FloatSetting("HealAmountPerSecond");
        numRows = IntSetting("NumRows");
        numCols = IntSetting("NumCols");
        ApproachHeight = FloatSetting("ApproachHeight");
        LandingDeckHeightOffset = FloatSetting("LandingDeckHeightOffset");
        hasRunways = BoolSetting("HasRunways");
        parkInHangars = BoolSetting("ParkInHangars");
    }

    static float ZRotation(Matrix4x4 m) => MathF.Atan2(m.M12, m.M11);

    void BuildInfo()
    {
        if (gotInfo || Owner.IsUnderConstruction || Owner.HasStatus(Status.Sold))
            return;
        int door = 0;
        for (int row = 0; row < numRows; row++)
        {
            for (int col = 0; col < numCols; col++)
            {
                var space = new Space { Runway = col, Door = door++ };
                if (Game.Bones.TryGetBone(Owner, $"Runway{col + 1}Park{row + 1}Han", out var han))
                {
                    space.HangarStart = han.Translation;
                    space.HangarStartOrient = ZRotation(han);
                }
                else
                    space.HangarStart = Owner.Position;
                if (Game.Bones.TryGetBone(Owner, $"Runway{col + 1}Parking{row + 1}", out var park))
                {
                    space.Location = park.Translation;
                    space.Orientation = ZRotation(park);
                }
                else
                    space.Location = Owner.Position;
                space.Prep = Game.Bones.BonePosition(Owner, $"Runway{col + 1}Prep{row + 1}");
                spaces.Add(space);
            }
        }
        if (hasRunways)
        {
            for (int col = 0; col < numCols; col++)
            {
                runways.Add(new Runway
                {
                    Start = Game.Bones.BonePosition(Owner, $"RunwayStart{col + 1}"),
                    End = Game.Bones.BonePosition(Owner, $"RunwayEnd{col + 1}"),
                });
            }
        }
        gotInfo = true;
    }

    bool IsGone(uint id) => Game.FindObject(id) is not { IsEffectivelyDead: false };

    void PurgeDead()
    {
        BuildInfo();
        foreach (var space in spaces)
        {
            if (space.ObjectInSpace != 0 && IsGone(space.ObjectInSpace))
            {
                space.ObjectInSpace = 0;
                space.ReservedForExit = false;
            }
        }
        foreach (var runway in runways)
        {
            if (runway.InUseBy != 0 && IsGone(runway.InUseBy))
            {
                runway.InUseBy = 0;
                runway.WasInLine = false;
            }
            if (runway.NextInLineForTakeoff != 0 && IsGone(runway.NextInLineForTakeoff))
                runway.NextInLineForTakeoff = 0;
        }
        if (healing.RemoveAll(h => h.Id != 0 && IsGone(h.Id)) > 0)
            ResetHealFrame();
    }

    public bool HasReservedSpace(uint id) => gotInfo && id != 0 && spaces.Any(s => s.ObjectInSpace == id);

    public int SpaceIndex(uint id) => id == 0 ? -1 : spaces.FindIndex(s => s.ObjectInSpace == id);

    Space? FindSpace(uint id) => !gotInfo || id == 0 ? null : spaces.FirstOrDefault(s => s.ObjectInSpace == id);

    Space? FindEmptySpace() => !gotInfo ? null : spaces.FirstOrDefault(s => s.ObjectInSpace == 0 && !s.ReservedForExit);

    /// <summary>Helicopters (PRODUCED_AT_HELIPAD) need no parking space.</summary>
    public bool ShouldReserveDoorWhenQueued(ObjectTemplate unit) => !unit.Is("PRODUCED_AT_HELIPAD");

    public bool HasAvailableSpaceFor(ObjectTemplate unit)
    {
        if (!gotInfo)
            return false;
        if (unit.Is("PRODUCED_AT_HELIPAD"))
            return true;
        return spaces.Any(s => (s.ObjectInSpace == 0 || IsGone(s.ObjectInSpace)) && !s.ReservedForExit);
    }

    /// <summary>Claims a space for the plane (keeps the one it has). Fills in <paramref name="info"/> if asked.</summary>
    public bool ReserveSpace(uint id, float parkingOffset, out ParkingInfo info)
    {
        info = default;
        BuildInfo();
        PurgeDead();
        var space = FindSpace(id) ?? FindEmptySpace();
        if (space == null)
            return false;
        space.ObjectInSpace = id;
        space.ReservedForExit = false;
        if (LandingDeckHeightOffset != 0 && Game.FindObject(id) is { } obj)
            obj.SetStatus("DECK_HEIGHT_OFFSET");
        info = CalcInfo(id);
        if (parkingOffset != 0)
        {
            info.ParkingSpace.X += parkingOffset * MathF.Cos(space.Orientation);
            info.ParkingSpace.Y += parkingOffset * MathF.Sin(space.Orientation);
        }
        return true;
    }

    public bool ReserveSpace(uint id, float parkingOffset = 0) => ReserveSpace(id, parkingOffset, out _);

    public ParkingInfo CalcInfo(uint id)
    {
        var info = new ParkingInfo();
        var space = FindSpace(id);
        if (space == null)
            return info;
        var runway = space.Runway < runways.Count ? runways[space.Runway] : new Runway { Start = Owner.Position, End = Owner.Position };
        info.ParkingSpace = parkInHangars ? space.HangarStart : space.Location;
        info.RunwayPrep = space.Prep;
        info.ParkingOrientation = parkInHangars ? space.HangarStartOrient : space.Orientation;
        info.RunwayStart = runway.Start;
        info.RunwayEnd = runway.End;
        info.RunwayApproach = runway.End;
        info.RunwayApproach.X += (runway.End.X - runway.Start.X) * ApproachDist;
        info.RunwayApproach.Y += (runway.End.Y - runway.Start.Y) * ApproachDist;
        info.RunwayApproach.Z = runway.End.Z + ApproachHeight + LandingDeckHeightOffset;
        info.RunwayExit = info.RunwayApproach;
        info.HangarInternal = space.HangarStart;
        info.HangarInternalOrient = space.HangarStartOrient;
        info.RunwayTakeoffDist = Vector3.Distance(info.RunwayStart, info.RunwayEnd);
        // A plane that was waiting in line starts its takeoff run from the prep spot.
        if (runways.Any(r => r.InUseBy == id && r.WasInLine))
            info.RunwayStart = info.RunwayPrep;
        return info;
    }

    public void ReleaseSpace(uint id)
    {
        BuildInfo();
        PurgeDead();
        foreach (var space in spaces.Where(s => s.ObjectInSpace == id))
        {
            space.ObjectInSpace = 0;
            space.ReservedForExit = false;
        }
        Game.FindObject(id)?.SetStatus("DECK_HEIGHT_OFFSET", false);
    }

    public uint RunwayReservation(int runway)
    {
        BuildInfo();
        PurgeDead();
        return runways[runway].InUseBy;
    }

    public void TransferRunwayReservationToNextInLineForTakeoff(uint id)
    {
        BuildInfo();
        PurgeDead();
        foreach (var runway in runways.Where(r => r.InUseBy == id && r.NextInLineForTakeoff != 0))
        {
            runway.InUseBy = runway.NextInLineForTakeoff;
            runway.WasInLine = true;
            runway.NextInLineForTakeoff = 0;
        }
    }

    /// <summary>Claims the runway of the plane's space; a plane taking off may queue as next in line.</summary>
    public bool ReserveRunway(uint id, bool forLanding)
    {
        BuildInfo();
        PurgeDead();
        var space = spaces.FirstOrDefault(s => s.ObjectInSpace == id);
        if (space == null || space.Runway >= runways.Count)
            return false;
        var runway = runways[space.Runway];
        if (runway.InUseBy == id)
            return true;
        if (runway.InUseBy == 0)
        {
            runway.InUseBy = id;
            if (runway.NextInLineForTakeoff == id)
            {
                runway.NextInLineForTakeoff = 0;
                runway.WasInLine = true;
            }
            else
                runway.WasInLine = false;
            return true;
        }
        if (!forLanding && runway.NextInLineForTakeoff == 0)
            runway.NextInLineForTakeoff = id;
        return false;
    }

    public void ReleaseRunway(uint id)
    {
        BuildInfo();
        PurgeDead();
        foreach (var runway in runways)
        {
            if (runway.InUseBy == id)
            {
                runway.InUseBy = 0;
                runway.WasInLine = false;
            }
            if (runway.NextInLineForTakeoff == id)
                runway.NextInLineForTakeoff = 0;
        }
    }

    void ResetHealFrame() => nextHealFrame = healing.Count == 0 ? int.MaxValue : Frame + HealRateFrames;

    /// <summary>A parked plane starts or stops being repaired.</summary>
    public void SetHealee(GameObject healee, bool add)
    {
        if (add)
        {
            if (healing.Any(h => h.Id == healee.Id))
                return;
            healing.Add((healee.Id, Frame));
            ResetHealFrame();
        }
        else if (healing.RemoveAll(h => h.Id == healee.Id) > 0)
            ResetHealFrame();
    }

    bool TakeoffOrLandingInProgress(GameObject plane) =>
        plane.AI is Modules.AI.JetAIUpdate jet && jet.IsTakeoffOrLandingInProgress;

    public void KillAllParkedUnits()
    {
        BuildInfo();
        PurgeDead();
        foreach (var space in spaces.ToList())
        {
            if (Game.FindObject(space.ObjectInSpace) is not { IsEffectivelyDead: false } plane)
                continue;
            if (plane.IsAboveTerrain && !TakeoffOrLandingInProgress(plane))
                continue;
            plane.Kill();
        }
        PurgeDead();
    }

    /// <summary>The airfield was captured: planes on the ground change sides, ones in the air lose their space.</summary>
    public void DefectAllParkedUnits(Players.Team newTeam, int detectionFrames)
    {
        BuildInfo();
        PurgeDead();
        foreach (var space in spaces.ToList())
        {
            if (Game.FindObject(space.ObjectInSpace) is not { IsEffectivelyDead: false } plane)
                continue;
            if (plane.IsAboveTerrain && !TakeoffOrLandingInProgress(plane))
            {
                if (newTeam.Owner != plane.Owner)
                {
                    ReleaseSpace(plane.Id);
                    if (plane.ProducerId == Owner.Id)
                        plane.ProducerId = 0;
                }
            }
            else
                plane.Defect(newTeam, detectionFrames);
        }
        PurgeDead();
    }

    public void OnDie(DamageInfo killingBlow) => KillAllParkedUnits();

    public int Update()
    {
        BuildInfo();
        PurgeDead();
        if (Frame >= nextHealFrame)
        {
            nextHealFrame = Frame + HealRateFrames;
            foreach (var (id, _) in healing.ToList())
            {
                if (Game.FindObject(id) is not { IsEffectivelyDead: false } plane)
                {
                    healing.RemoveAll(h => h.Id == id);
                    continue;
                }
                plane.Body?.AttemptHealing(HealRateFrames * healAmount * LogicTime.SecondsPerFrame, Owner.Id);
            }
        }
        return 0;
    }

    // ---- As the airfield's production exit ----

    public Vector3? RallyPoint
    {
        get => heliRallyPoint;
        set => heliRallyPoint = value;
    }

    public bool CanExitNow => true;

    public bool CanReserveFor(ObjectTemplate unit)
    {
        BuildInfo();
        PurgeDead();
        return !ShouldReserveDoorWhenQueued(unit) || FindEmptySpace() != null;
    }

    public int ReserveDoorForExit(ObjectTemplate unit)
    {
        BuildInfo();
        PurgeDead();
        if (unit.Is("PRODUCED_AT_HELIPAD"))
            return ProductionDoor.NoneNeeded;
        if (FindEmptySpace() is not { } space)
            return ProductionDoor.NoneAvailable;
        space.ObjectInSpace = 0;
        space.ReservedForExit = true;
        return space.Door;
    }

    public void UnreserveDoorForExit(int door)
    {
        if (door < 0)
            return;
        foreach (var space in spaces.Where(s => s.Door == door))
        {
            space.ObjectInSpace = 0;
            space.ReservedForExit = false;
        }
    }

    public void Exit(GameObject unit) => ExitViaDoor(unit, ReserveDoorForExit(unit.Template));

    public void ExitViaDoor(GameObject plane, int door)
    {
        if (door != ProductionDoor.NoneNeeded)
        {
            var space = spaces.FirstOrDefault(s => s.ObjectInSpace == 0 && s.ReservedForExit && s.Door == door);
            if (space == null)
                return;
            space.ObjectInSpace = plane.Id;
            space.ReservedForExit = false;
        }

        float parkingOffset = plane.AI is Modules.AI.JetAIUpdate jet ? jet.ParkingOffset : 0;
        bool producedAtHelipad = plane.IsKindOf("PRODUCED_AT_HELIPAD");
        var info = new ParkingInfo();
        if (producedAtHelipad)
        {
            if (Game.Bones.TryGetBone(Owner, "HeliPark01", out var bone))
            {
                info.HangarInternal = bone.Translation;
                info.HangarInternalOrient = ZRotation(bone);
            }
            else
            {
                info.HangarInternal = Owner.Position;
                info.HangarInternalOrient = Owner.Orientation;
            }
            info.ParkingSpace = info.HangarInternal;
            info.ParkingOrientation = info.HangarInternalOrient;
        }
        else if (!ReserveSpace(plane.Id, parkingOffset, out info))
        {
            info.ParkingSpace = Owner.Position;
            info.ParkingOrientation = Owner.Orientation;
            info.HangarInternal = Owner.Position;
            info.HangarInternalOrient = Owner.Orientation;
        }

        plane.SetPosition(info.HangarInternal);
        plane.SetOrientation(info.HangarInternalOrient);

        if (plane.AI is not { } ai)
            return;
        if (producedAtHelipad && RallyPoint is { } rally)
            ai.MoveTo(rally, CommandSource.AI);
        else if (!producedAtHelipad)
            ai.FollowExitProductionPath([info.ParkingSpace], Owner);
        else
            ai.MoveTo(info.ParkingSpace, CommandSource.AI);
    }
}

/// <summary>Special door numbers of production exits.</summary>
public static class ProductionDoor
{
    public const int NoneAvailable = -1;
    public const int NoneNeeded = -2;
}

using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Dock;

/// <summary>
/// A building other units dock with (the original's DockUpdate): dockers queue on approach
/// positions (DockWaiting01.. bones, NumberApproachPositions of them, or as many as needed with -1),
/// then one at a time drive to DockStart, DockAction (where <see cref="Action"/> runs) and DockEnd.
/// Without dock bones the docker docks where it stands.
/// </summary>
public abstract class DockUpdate : Module, IUpdateModule
{
    const int DynamicApproachVectorFlag = -1;
    const int DefaultApproachVectorSize = 10;

    readonly List<Vector3> approachPositions = new();
    readonly List<uint> approachPositionOwners = new();
    readonly List<bool> approachPositionReached = new();
    int numberApproachPositions;
    int numberApproachPositionBones = -1;
    bool positionsLoaded;
    Vector3 enterPosition, dockPosition, exitPosition;
    uint activeDocker;

    public bool IsDockOpen { get; set; } = true;
    public bool IsDockCrippled { get; set; }
    public bool IsDockerInside { get; private set; }
    public uint ActiveDocker => activeDocker;

    protected override void Initialize()
    {
        numberApproachPositions = IntSetting("NumberApproachPositions");
        int size = numberApproachPositions != DynamicApproachVectorFlag ? Math.Max(0, numberApproachPositions) : DefaultApproachVectorSize;
        for (int i = 0; i < size; i++)
        {
            approachPositions.Add(Vector3.Zero);
            approachPositionOwners.Add(0);
            approachPositionReached.Add(false);
        }
        InitializeDock();
    }

    protected virtual void InitializeDock() { }

    /// <summary>Docking does its job; false when finished (the docker then leaves).</summary>
    public abstract bool Action(GameObject docker, GameObject? drone);

    /// <summary>Frames between actions for this docker (supply trucks wait per box; others act every frame).</summary>
    public virtual int ActionDelayFor(GameObject docker) => 0;

    /// <summary>A few docks send the docker on to their rally point afterwards (repair bays).</summary>
    public virtual bool IsRallyPointAfterDockType => false;

    public bool IsAllowPassthroughType => BoolSetting("AllowsPassthrough", true);

    public bool IsClearToApproach(GameObject docker)
    {
        if (numberApproachPositions == DynamicApproachVectorFlag)
            return true;
        foreach (uint owner in approachPositionOwners)
            if (owner == 0 || owner == docker.Id)
                return true;
        return false;
    }

    public bool ReserveApproachPosition(GameObject docker, out Vector3 position, ref int index)
    {
        if (!positionsLoaded)
            LoadDockPositions();
        for (int i = 0; i < approachPositionOwners.Count; i++)
        {
            if (approachPositionOwners[i] == docker.Id)
            {
                position = ComputeApproachPosition(i, docker);
                index = i;
                return true;
            }
            if (approachPositionOwners[i] == 0)
            {
                approachPositionOwners[i] = docker.Id;
                position = ComputeApproachPosition(i, docker);
                index = i;
                return true;
            }
        }
        if (numberApproachPositions == DynamicApproachVectorFlag)
        {
            approachPositions.Add(Vector3.Zero);
            approachPositionOwners.Add(0);
            approachPositionReached.Add(false);
            LoadDockPositions();
            int last = approachPositionOwners.Count - 1;
            approachPositionOwners[last] = docker.Id;
            position = ComputeApproachPosition(last, docker);
            index = last;
            return true;
        }
        position = default;
        return false;
    }

    public bool AdvanceApproachPosition(GameObject docker, out Vector3 position, ref int index)
    {
        if (!positionsLoaded)
            LoadDockPositions();
        position = default;
        if (index <= 0 || approachPositionOwners[index - 1] != 0)
            return false;
        int his = index, previous = index - 1;
        approachPositionOwners[previous] = docker.Id;
        approachPositionReached[previous] = false;
        approachPositionOwners[his] = 0;
        approachPositionReached[his] = false;
        position = ComputeApproachPosition(previous, docker);
        index = previous;
        return true;
    }

    public virtual bool IsClearToEnter(GameObject docker) => docker.Id == activeDocker;

    public bool IsClearToAdvance(GameObject docker, int dockerIndex)
    {
        if (dockerIndex < 0 || dockerIndex >= approachPositionOwners.Count)
            return false;
        bool correctRequest = docker.Id == approachPositionOwners[dockerIndex];
        bool reached = approachPositionReached[dockerIndex];
        bool nextSpotFree = dockerIndex > 0 && approachPositionOwners[dockerIndex - 1] == 0;
        return correctRequest && reached && nextSpotFree;
    }

    public Vector3 GetEnterPosition(GameObject docker)
    {
        if (!positionsLoaded)
            LoadDockPositions();
        if (enterPosition == Vector3.Zero)
            return docker.IsAboveTerrain ? Owner.Position : docker.Position;
        return Art.ModelBones.ToWorld(Owner, enterPosition);
    }

    // (The original tests the enter bone for all three, so a dock without DockStart docks in place.)
    public Vector3 GetDockPosition(GameObject docker)
    {
        if (!positionsLoaded)
            LoadDockPositions();
        return enterPosition == Vector3.Zero ? docker.Position : Art.ModelBones.ToWorld(Owner, dockPosition);
    }

    public Vector3 GetExitPosition(GameObject docker)
    {
        if (!positionsLoaded)
            LoadDockPositions();
        return enterPosition == Vector3.Zero ? docker.Position : Art.ModelBones.ToWorld(Owner, exitPosition);
    }

    public void OnApproachReached(GameObject docker)
    {
        int i = approachPositionOwners.IndexOf(docker.Id);
        if (i >= 0)
            approachPositionReached[i] = true;
    }

    public void OnEnterReached(GameObject docker)
    {
        foreach (var obj in new[] { Owner, docker })
        {
            obj.SetCondition("DOCKING_BEGINNING");
            obj.SetCondition("DOCKING");
            obj.SetCondition("DOCKING_ENDING", false);
        }
        IsDockerInside = true;
        int i = approachPositionOwners.IndexOf(docker.Id);
        if (i >= 0)
        {
            approachPositionOwners[i] = 0;
            approachPositionReached[i] = false;
        }
    }

    public void OnDockReached(GameObject docker)
    {
        foreach (var obj in new[] { Owner, docker })
        {
            obj.SetCondition("DOCKING_BEGINNING", false);
            obj.SetCondition("DOCKING_ACTIVE");
        }
    }

    public void OnExitReached(GameObject docker)
    {
        foreach (var obj in new[] { Owner, docker })
        {
            obj.SetCondition("DOCKING_ACTIVE", false);
            obj.SetCondition("DOCKING", false);
            obj.SetCondition("DOCKING_ENDING");
        }
        IsDockerInside = false;
        if (docker.Id == activeDocker)
            activeDocker = 0;
    }

    static readonly string[] dockingConditions = ["DOCKING_ENDING", "DOCKING_BEGINNING", "DOCKING_ACTIVE", "DOCKING"];

    public void CancelDock(GameObject docker)
    {
        for (int i = 0; i < approachPositionOwners.Count; i++)
        {
            if (approachPositionOwners[i] == docker.Id)
            {
                approachPositionOwners[i] = 0;
                approachPositionReached[i] = false;
            }
        }
        if (activeDocker == docker.Id)
        {
            var dockingObject = Game.FindObject(activeDocker);
            activeDocker = 0;
            IsDockerInside = false;
            foreach (var condition in dockingConditions)
            {
                Owner.SetCondition(condition, false);
                dockingObject?.SetCondition(condition, false);
            }
        }
    }

    public virtual int Update()
    {
        if (activeDocker == 0 && !IsDockCrippled)
        {
            for (int i = 0; i < approachPositionReached.Count; i++)
            {
                if (approachPositionReached[i])
                {
                    activeDocker = approachPositionOwners[i];
                    return 0;
                }
            }
        }
        else if (Owner.IsKindOf("SUPPLY_SOURCE") && Game.FindObject(activeDocker) is { } docker
            && docker.IsKindOf(Kind.Dozer) && docker.IsKindOf("HARVESTER") && docker.ModelConditions.Has(Flags.ModelCondition.IndexOf("DOCKING_BEGINNING")))
        {
            docker.SetCondition("MOVING", false); // the GLA worker stops walking while he loads
        }
        return 0;
    }

    Vector3 ComputeApproachPosition(int index, GameObject forWhom)
    {
        if (!positionsLoaded)
            LoadDockPositions();
        var us = Owner;
        var working = Art.ModelBones.ToWorld(us, approachPositions[index]);
        if (numberApproachPositionBones == 0)
        {
            // No waiting bones: wait halfway out from the center, on the docker's side.
            var offset = forWhom.Position - us.Position;
            if (offset.LengthSquared() > 0)
                offset = Vector3.Normalize(offset);
            working += offset * (us.Template.GeometryMajorRadius / 2);
        }
        return Spatial.PositionFinder.FindPositionAround(Game, working, 0, 100) ?? working;
    }

    void LoadDockPositions()
    {
        var obj = Owner;
        if (!obj.IsKindOf("IGNORE_DOCKING_BONES") && Game.Bones.HasArt)
        {
            enterPosition = Game.Bones.TryGetLocalBone(obj, "DockStart", out var start) ? start : Vector3.Zero;
            dockPosition = Game.Bones.TryGetLocalBone(obj, "DockAction", out var action) ? action : Vector3.Zero;
            exitPosition = Game.Bones.TryGetLocalBone(obj, "DockEnd", out var end) ? end : Vector3.Zero;
            if (numberApproachPositions != DynamicApproachVectorFlag)
            {
                var bones = new List<Vector3>();
                for (int i = 1; i <= numberApproachPositions; i++)
                {
                    if (!Game.Bones.TryGetLocalBone(obj, $"DockWaiting{i:00}", out var bone))
                        break;
                    bones.Add(bone);
                }
                numberApproachPositionBones = bones.Count;
                // The original copies the bones only when there is one per slot.
                if (bones.Count == approachPositions.Count)
                    for (int i = 0; i < bones.Count; i++)
                        approachPositions[i] = bones[i];
            }
            else
                numberApproachPositionBones = 0;
        }
        else
            numberApproachPositionBones = 0;

        // OpenGZH addition for objects without model bones (Tiberian Dawn sprites): the dock points come
        // from DockActionOffset (and DockStartOffset / DockEndOffset, which default to it) in object space.
        if (enterPosition == Vector3.Zero && Setting("DockActionOffset") is { Length: > 0 } actionOffset)
        {
            dockPosition = RuleValue.Coord(actionOffset);
            enterPosition = Setting("DockStartOffset") is { Length: > 0 } start ? RuleValue.Coord(start) : dockPosition;
            exitPosition = Setting("DockEndOffset") is { Length: > 0 } end ? RuleValue.Coord(end) : dockPosition;
            if (enterPosition == Vector3.Zero)
                enterPosition = new Vector3(0.01f, 0, 0); // zero means "no dock point"
        }
        positionsLoaded = true;
    }
}

/// <summary>
/// A war factory or arms dealer repairing a docked vehicle (the original's RepairDockUpdate): heals it
/// from where it started to full in TimeForFullHeal, and fully heals its drone; then it leaves for the
/// dock's rally point.
/// </summary>
public sealed class RepairDockUpdate : DockUpdate
{
    uint lastRepair;
    float healthToAddPerFrame;

    public override bool IsRallyPointAfterDockType => true;

    public override bool Action(GameObject docker, GameObject? drone)
    {
        if (docker.Body is not { } body)
            return false;
        // (The original only works the rate out while lastRepair is 0, i.e. once per visit.)
        if (lastRepair == 0)
        {
            lastRepair = docker.Id;
            float framesForFullHeal = Definition.Settings.Has("TimeForFullHeal") ? FloatSetting("TimeForFullHeal") / LogicTime.MillisecondsPerFrame : 1;
            healthToAddPerFrame = (body.MaxHealth - body.Health) / Math.Max(1e-3f, framesForFullHeal);
        }
        if (body.Health >= body.MaxHealth)
        {
            lastRepair = 0;
            return false;
        }
        body.AttemptHealing(healthToAddPerFrame, Owner.Id);
        if (drone?.Body is { } droneBody)
            droneBody.AttemptHealing(droneBody.MaxHealth, Owner.Id);
        return true;
    }
}

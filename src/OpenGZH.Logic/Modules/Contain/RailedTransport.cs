using System.Numerics;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.AI;
using OpenGZH.Logic.Modules.Dock;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Contain;

/// <summary>
/// The ferry's hold (the original's RailedTransportContain): riders only get out while it is docked
/// (its dock is open), and they are pushed out down the ramp by its RailedTransportDockUpdate.
/// </summary>
public sealed class RailedTransportContain : TransportContain
{
    protected override void OnRemoving(GameObject rider)
    {
        base.OnRemoving(rider);
        if (ContainCount == 0 && Owner.FindModule<DockUpdate>() is { } dock)
            dock.IsDockOpen = true;
    }

    protected override bool IsSpecificRiderFreeToExit(GameObject rider) =>
        Owner.FindModule<DockUpdate>() is not { IsDockOpen: false };

    public override void ExitObjectViaDoor(GameObject rider) =>
        Owner.FindModule<RailedTransportDockUpdate>()?.UnloadSingleObject(rider);
}

/// <summary>
/// The ferry's loading ramp (the original's RailedTransportDockUpdate): a unit that docks within
/// ToleranceDistance is pulled inside over PullInsideDuration; unloading pushes riders one at a time
/// out to the exit over PushOutsideDuration, then sends them up to the DOCKWAITING07 spot.
/// </summary>
public sealed class RailedTransportDockUpdate : DockUpdate
{
    const int UnloadAll = -1;

    uint dockingObjectId, unloadingObjectId;
    float pullInsideDistancePerFrame, pushOutsideDistancePerFrame;
    int unloadCount = UnloadAll;

    public override int Update()
    {
        base.Update();
        DoPullInDocking();
        DoPushOutDocking();
        return 0;
    }

    public override bool Action(GameObject docker, GameObject? drone)
    {
        var us = Owner;
        if (dockingObjectId != docker.Id)
        {
            var v = us.Position - docker.Position;
            float mag = v.Length();
            if (mag <= FloatSetting("ToleranceDistance", 50))
            {
                dockingObjectId = docker.Id;
                Game.Events.Deselect(docker);
                docker.SetStatus(Status.Unselectable);
                docker.SetDisabled("DISABLED_HELD");
                pullInsideDistancePerFrame = mag / Math.Max(1, FramesSetting("PullInsideDuration"));
                docker.SetOrientation(MathF.Atan2(v.Y, v.X));
            }
        }
        return true;
    }

    public override bool IsClearToEnter(GameObject docker) =>
        base.IsClearToEnter(docker) && (Owner.Contain?.IsValidContainerFor(docker, true) ?? true);

    public bool IsLoadingOrUnloading => unloadingObjectId != 0 || dockingObjectId != 0;

    public void UnloadAllRiders()
    {
        if (unloadingObjectId != 0)
            return;
        unloadCount = UnloadAll;
        UnloadNext();
    }

    public void UnloadSingleObject(GameObject obj)
    {
        unloadCount = 1;
        UnloadNext();
    }

    void DoPullInDocking()
    {
        if (dockingObjectId == 0)
            return;
        var us = Owner;
        if (Game.FindObject(dockingObjectId) is not { } docker)
        {
            dockingObjectId = 0;
            return;
        }
        var v = us.Position - docker.Position;
        if (v.LengthSquared() > 0)
            v = Vector3.Normalize(v);
        docker.SetPosition(new Vector3(docker.Position.X + v.X * pullInsideDistancePerFrame, docker.Position.Y + v.Y * pullInsideDistancePerFrame, docker.Position.Z));
        docker.SetCondition("MOVING");
        const float closeEnough = 6;
        if (MathUtil.DistanceSquared2D(docker.Position, us.Position) <= closeEnough * closeEnough)
        {
            docker.SetCondition("MOVING", false);
            CancelDock(docker);
            docker.AI?.Stop(CommandSource.AI);
            us.Contain?.AddToContain(docker);
            dockingObjectId = 0;
        }
    }

    void DoPushOutDocking()
    {
        if (unloadingObjectId == 0)
            return;
        if (Game.FindObject(unloadingObjectId) is not { } unloader)
        {
            UnloadNext();
            return;
        }
        var destPos = GetExitPosition(unloader);
        destPos.Z = Game.Terrain.GroundHeight(destPos);
        var v = destPos - unloader.Position;
        if (v.LengthSquared() > 0)
            v = Vector3.Normalize(v);
        var newPos = new Vector3(unloader.Position.X + v.X * pushOutsideDistancePerFrame, unloader.Position.Y + v.Y * pushOutsideDistancePerFrame, destPos.Z);
        unloader.SetPosition(newPos);
        unloader.SetCondition("MOVING");
        const float closeEnough = 3;
        if (Vector3.DistanceSquared(destPos, newPos) <= closeEnough * closeEnough)
        {
            unloader.SetCondition("MOVING", false);
            unloader.AI?.Stop(CommandSource.AI);
            unloader.SetDisabled("DISABLED_HELD", false);
            unloader.SetStatus(Status.Unselectable, false);
            if (unloader.AI is { } ai && Game.Bones.HasArt)
                ai.MoveTo(Game.Bones.BonePosition(Owner, "DOCKWAITING07"), CommandSource.AI);
            UnloadNext();
        }
    }

    void UnloadNext()
    {
        var us = Owner;
        unloadingObjectId = 0;
        if (unloadCount == 0)
            return;
        if (us.Contain is not { } contain || contain.Passengers.FirstOrDefault() is not { } unloader)
            return;
        contain.RemoveFromContain(unloader);
        unloader.SetPosition(us.Position);
        unloader.SetOrientation(us.Orientation);
        unloader.SetDisabled("DISABLED_HELD");
        var dockPosition = GetExitPosition(unloader);
        pushOutsideDistancePerFrame = (dockPosition - unloader.Position).Length() / Math.Max(1, FramesSetting("PushOutsideDuration"));
        unloadingObjectId = unloader.Id;
        if (unloadCount != UnloadAll)
            --unloadCount;
    }
}

/// <summary>
/// The ferry's pilot (the original's RailedTransportAIUpdate): it shuttles along the PathPrefixName
/// StartNN-to-EndNN waypoint paths, ignoring player orders other than Execute and Evacuate. Its dock is
/// closed while it travels; Execute takes it along the next path, Evacuate unloads it while docked.
/// </summary>
public sealed class RailedTransportAIUpdate : AIUpdateInterface
{
    const int MaxWaypointPaths = 32;
    readonly List<(int Start, int End)> paths = new();
    int currentPath = -1;
    bool inTransit, waypointDataLoaded;

    void LoadWaypointData()
    {
        waypointDataLoaded = true;
        string prefix = Setting("PathPrefixName");
        for (int i = 0; i < MaxWaypointPaths; i++)
        {
            var start = Game.Waypoints?.Find($"{prefix}Start{i + 1:00}");
            var end = Game.Waypoints?.Find($"{prefix}End{i + 1:00}");
            if (start != null && end != null)
                paths.Add((start.Id, end.Id));
        }
    }

    Terrain.Waypoint? WaypointById(int id) => Game.Waypoints?.All.FirstOrDefault(w => w.Id == id);

    void PickAndMoveToInitialLocation()
    {
        var ourPos = Owner.Position;
        Terrain.Waypoint? closestEnd = null;
        int closestPath = -1;
        float closestDist = 99999999.9f;
        for (int i = 0; i < paths.Count; i++)
        {
            if (WaypointById(paths[i].End) is not { } waypoint)
                continue;
            float dist = Vector3.Distance(waypoint.Position, ourPos);
            if (dist < closestDist)
            {
                closestDist = dist;
                closestPath = i;
                closestEnd = waypoint;
            }
        }
        if (closestEnd == null)
            return;
        FollowWaypointPath(closestEnd, CommandSource.AI);
        currentPath = closestPath;
        SetInTransit(true);
    }

    public override int Update()
    {
        if (!waypointDataLoaded)
            LoadWaypointData();
        base.Update();
        if (CurrentLocomotor is { } locomotor)
            locomotor.UltraAccurate = true;
        if (currentPath == -1 && paths.Count > 0)
            PickAndMoveToInitialLocation();
        if (inTransit && currentPath >= 0 && WaypointById(paths[currentPath].End) is { } waypoint)
        {
            if (Vector3.Distance(waypoint.Position, Owner.Position) <= 5 || IsIdle)
                SetInTransit(false);
        }
        return 0;
    }

    protected override bool OnOrder(AIOrder order, Action replay)
    {
        if (order.Source != CommandSource.Player)
            return false;
        if (order.Type == AIOrderType.Evacuate)
            EvacuateFerry();
        return true;    // players can only Execute and Evacuate
    }

    void SetInTransit(bool on)
    {
        if (Owner.FindModule<DockUpdate>() is { } dock)
            dock.IsDockOpen = !on;
        inTransit = on;
    }

    /// <summary>Sails along the next path (the original's privateExecuteRailedTransport).</summary>
    public void ExecuteRailedTransport()
    {
        if (Owner.FindModule<RailedTransportDockUpdate>() is not { IsLoadingOrUnloading: false } || paths.Count == 0)
            return;
        if (++currentPath >= paths.Count)
            currentPath = 0;
        if (WaypointById(paths[currentPath].Start) is not { } start)
            return;
        FollowWaypointPath(start, CommandSource.AI);
        SetInTransit(true);
    }

    void EvacuateFerry()
    {
        if (Owner.FindModule<RailedTransportDockUpdate>() is not { } dock || inTransit || dock.IsLoadingOrUnloading)
            return;
        dock.UnloadAllRiders();
    }
}

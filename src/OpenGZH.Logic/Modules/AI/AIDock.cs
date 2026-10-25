using System.Numerics;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Dock;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// Docking (the original's AIDockState and its AIDockMachine): queue at the dock's approach
/// position, wait for clearance (moving up the queue when the spot ahead frees), drive to the
/// dock's enter, action and exit points, let the dock act, and finally go to the dock's rally point
/// for docks that want that. Any new order cancels the dock.
/// </summary>
public partial class AIUpdateInterface
{
    enum DockStep { Approach, WaitForClearance, AdvancePosition, MoveToEntry, MoveToDock, ProcessDock, MoveToExit, MoveToRally }

    DockStep dockStep;
    uint dockId;
    int dockApproachIndex = -1;
    int dockWaitEnterFrame, nextDockActionFrame;
    uint dockDroneId;
    bool dockMoving;

    public bool IsDocking => State == AIState.Docking;
    public uint DockTargetId => State == AIState.Docking ? dockId : 0;

    /// <summary>Where in the docking sequence it is (for diagnostics).</summary>
    public string DockStepName => State == AIState.Docking ? $"{dockStep} approach {dockApproachIndex} moving {dockMoving} goal {goalType} path {(path == null ? "none" : "yes")} direct {directPath} dest {Destination} blocked {blockedFrames}" : "";

    /// <summary>Dock with a building (the original's aiDock).</summary>
    public void Dock(GameObject dockObject, CommandSource source = CommandSource.Player)
    {
        if (OnOrder(new AIOrder(AIOrderType.Dock, source, dockObject.Position, dockObject.Id), () => Dock(dockObject, source)))
            return;
        if (!CanMove)
            return;
        NoteOrder();
        ClearPath();
        TargetId = 0;
        LastCommandSource = source;
        if (dockObject.FindModule<DockUpdate>() == null)
        {
            State = AIState.Idle;
            return;
        }
        // The dock itself must not block the way in.
        IgnoreObstacle(dockObject);
        dockId = dockObject.Id;
        dockApproachIndex = -1;
        dockDroneId = 0;
        State = AIState.Docking;
        EnterDockStep(DockStep.Approach);
        Wake();
    }

    /// <summary>Get a vehicle fixed at a repair bay (the original's aiGetRepaired).</summary>
    public void GetRepairedAt(GameObject repairDepot, CommandSource source = CommandSource.Player)
    {
        if (CanGetRepairedAt(Owner, repairDepot))
            Dock(repairDepot, source);
    }

    /// <summary>The original's ActionManager::canGetRepairedAt.</summary>
    public static bool CanGetRepairedAt(GameObject obj, GameObject repairDest)
    {
        if (obj.RelationshipTo(repairDest) != Players.Relationship.Allies || obj.IsEffectivelyDead || obj.AI is not { CanMove: true })
            return false;
        if (obj.IsUnderConstruction || repairDest.IsUnderConstruction || repairDest.HasStatus(Core.Flags.ObjectStatus.IndexOf("SOLD")))
            return false;
        if (!obj.IsKindOf("VEHICLE"))
            return false;
        if (obj.IsKindOf("AIRCRAFT"))
        {
            if (!obj.IsAboveTerrain || !repairDest.IsKindOf("FS_AIRFIELD"))
                return false;
        }
        else if (!repairDest.IsKindOf("REPAIR_PAD"))
            return false;
        return !(obj.Body is { } body && body.Health == body.MaxHealth);
    }

    /// <summary>Leaving the dock state for any reason: let the dock forget us (the dock machine's halt).</summary>
    void HaltDock()
    {
        if (Game.FindObject(dockId)?.FindModule<DockUpdate>() is { } dock)
            dock.CancelDock(Owner);
        dockId = 0;
        dockMoving = false;
        IgnoreObstacle(null);
    }

    void UpdateDock()
    {
        var goal = Game.FindObject(dockId);
        var dock = goal?.FindModule<DockUpdate>();
        if (goal == null || dock == null)
        {
            FinishDock();
            return;
        }
        switch (dockStep)
        {
            case DockStep.Approach:
            case DockStep.AdvancePosition:
                if (!DockMoveUpdate(out bool failed))
                    return;
                if (failed || !dock.IsDockOpen)
                {
                    dock.CancelDock(Owner);
                    FinishDock();
                    return;
                }
                dock.OnApproachReached(Owner);
                EnterDockStep(DockStep.WaitForClearance);
                return;

            case DockStep.WaitForClearance:
                if (dock.IsClearToAdvance(Owner, dockApproachIndex))
                {
                    EnterDockStep(DockStep.AdvancePosition);
                    return;
                }
                if (!dock.IsDockOpen)
                {
                    dock.CancelDock(Owner);
                    FinishDock();
                    return;
                }
                if (dock.IsClearToEnter(Owner))
                {
                    EnterDockStep(DockStep.MoveToEntry);
                    return;
                }
                if (dockWaitEnterFrame + 30 * LogicTime.FramesPerSecond < Frame)
                    FinishDock(); // waited too long
                else
                    Decelerate();
                return;

            case DockStep.MoveToEntry:
                if (!DockMoveUpdate(out failed))
                    return;
                if (!dock.IsDockOpen)
                {
                    dock.CancelDock(Owner);
                    EnterDockStep(DockStep.MoveToExit);
                    return;
                }
                dock.OnEnterReached(Owner);
                EnterDockStep(failed ? DockStep.MoveToExit : DockStep.MoveToDock);
                return;

            case DockStep.MoveToDock:
                if (!dock.IsDockOpen)
                {
                    dock.CancelDock(Owner);
                    EnterDockStep(DockStep.MoveToExit);
                    return;
                }
                if (!DockMoveUpdate(out failed))
                    return;
                dock.OnDockReached(Owner);
                EnterDockStep(failed ? DockStep.MoveToExit : DockStep.ProcessDock);
                return;

            case DockStep.ProcessDock:
                Decelerate();
                if (Frame < nextDockActionFrame)
                    return;
                nextDockActionFrame = Frame + dock.ActionDelayFor(Owner);
                if (!dock.IsDockOpen || !dock.Action(Owner, FindMyDrone()))
                    EnterDockStep(DockStep.MoveToExit);
                return;

            case DockStep.MoveToExit:
                if (!DockMoveUpdate(out failed))
                    return;
                dock.OnExitReached(Owner);
                if (failed)
                {
                    FinishDock();
                    return;
                }
                EnterDockStep(DockStep.MoveToRally);
                return;

            case DockStep.MoveToRally:
                if (DockMoveUpdate(out _))
                    FinishDock();
                return;
        }
    }

    void EnterDockStep(DockStep step)
    {
        dockStep = step;
        dockMoving = false;
        var goal = Game.FindObject(dockId);
        var dock = goal?.FindModule<DockUpdate>();
        if (goal == null || dock == null)
        {
            FinishDock();
            return;
        }
        switch (step)
        {
            case DockStep.Approach:
                if (!dock.IsDockOpen)
                {
                    dock.CancelDock(Owner);
                    FinishDock();
                    return;
                }
                if (!dock.ReserveApproachPosition(Owner, out var approach, ref dockApproachIndex))
                {
                    FinishDock();
                    return;
                }
                IgnoreObstacle(null);
                StartDockMove(approach);
                break;

            case DockStep.WaitForClearance:
                dockWaitEnterFrame = Frame;
                ClearPath();
                break;

            case DockStep.AdvancePosition:
                if (!dock.IsDockOpen)
                {
                    dock.CancelDock(Owner);
                    FinishDock();
                    return;
                }
                if (!dock.AdvanceApproachPosition(Owner, out var advance, ref dockApproachIndex))
                {
                    FinishDock();
                    return;
                }
                IgnoreObstacle(null);
                StartDockMove(advance);
                break;

            case DockStep.MoveToEntry:
                if (!dock.IsDockOpen)
                {
                    dock.CancelDock(Owner);
                    FinishDock();
                    return;
                }
                if (dock.IsAllowPassthroughType)
                    IgnoreObstacle(goal);
                dockApproachIndex = -1;
                StartDockMove(dock.GetEnterPosition(Owner));
                break;

            case DockStep.MoveToDock:
                if (dock.IsAllowPassthroughType)
                    IgnoreObstacle(goal);
                StartDockMove(dock.GetDockPosition(Owner));
                break;

            case DockStep.ProcessDock:
                nextDockActionFrame = Frame + dock.ActionDelayFor(Owner);
                ClearPath();
                break;

            case DockStep.MoveToExit:
                if (dock.IsAllowPassthroughType)
                    IgnoreObstacle(goal);
                StartDockMove(dock.GetExitPosition(Owner));
                break;

            case DockStep.MoveToRally:
                var rally = goal.FindModule<Production.IProductionExit>()?.RallyPoint;
                if (!dock.IsRallyPointAfterDockType || rally == null)
                {
                    FinishDock();
                    return;
                }
                StartDockMove(rally.Value);
                break;
        }
    }

    void StartDockMove(Vector3 destination)
    {
        dockMoving = true;
        BeginMove(destination, keepState: true);
    }

    /// <summary>Steps the move toward the current dock goal; true when it is over (failed: there was no way).</summary>
    bool DockMoveUpdate(out bool failed)
    {
        failed = false;
        if (!dockMoving)
            return true;
        if (path == null)
        {
            // No route was found (or it ran out): over, and failed unless already there.
            failed = MathUtil.Distance2D(Owner.Position, Destination) > Formats.Maps.MapFile.CellSize * 2;
            dockMoving = false;
            return true;
        }
        if (!FollowPath())
            return false;
        dockMoving = false;
        return true;
    }

    GameObject? FindMyDrone()
    {
        if (Game.FindObject(dockDroneId) is { } drone)
            return drone;
        foreach (var obj in Game.ObjectsOwnedBy(Owner.Owner))
        {
            if (obj.IsKindOf("DRONE") && obj.ProducerId == Owner.Id)
            {
                dockDroneId = obj.Id;
                return obj;
            }
        }
        return null;
    }

    /// <summary>The dock state is over (success or failure both go to idle, as in the original).</summary>
    void FinishDock()
    {
        HaltDock();
        ClearPath();
        State = AIState.Idle;
    }
}

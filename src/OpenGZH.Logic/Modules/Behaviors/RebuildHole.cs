using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// A GLA building leaves a rebuild hole when destroyed (the original's RebuildHoleExposeDie): the
/// hole takes its place, its script name and its attackers, and starts rebuilding it.
/// </summary>
public sealed class RebuildHoleExposeDie : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        var us = Owner;
        if (us.Owner.IsNeutral || us.Owner.IsPlayerDead || us.IsUnderConstruction || us.Team == null)
            return;
        if (Game.Templates.Find(Setting("HoleName")) is not { } holeTemplate)
            return;
        var hole = Game.CreateObject(holeTemplate, us.Owner, us.Position, us.Orientation, us.Team);
        Game.Scripts?.TransferObjectName(us, hole);
        hole.Body?.SetMaxHealth(FloatSetting("HoleMaxHealth", 0), Bodies.MaxHealthChange.SameCurrentHealth);
        hole.FindModule<RebuildHoleBehavior>()?.StartRebuildProcess(us.Template, us.Id);
        if (BoolSetting("TransferAttackers", true))
            foreach (var obj in Game.Objects)
                obj.AI?.TransferAttack(us.Id, hole.Id);
    }
}

/// <summary>
/// The hole itself (the original's RebuildHoleBehavior): after WorkerRespawnDelay a worker appears and
/// rebuilds the building for free; the hole heals meanwhile, and goes away when the building is up.
/// Killing the worker starts the wait again.
/// </summary>
public sealed class RebuildHoleBehavior : Module, IUpdateModule, IDieModule, IOwnershipModule
{
    uint workerId, reconstructingId;
    int workerWaitCounter;
    Templates.ObjectTemplate? rebuildTemplate;

    /// <summary>The building that made this hole (the computer player's build list matches on it).</summary>
    public uint SpawnerId { get; private set; }
    public uint ReconstructedBuildingId => reconstructingId;

    void NewWorkerRespawnProcess(GameObject? existingWorker)
    {
        if (existingWorker != null)
            Game.DestroyObject(existingWorker);
        workerId = 0;
        workerWaitCounter = FramesSetting("WorkerRespawnDelay");
        Owner.SetStatus("MASKED", false);
    }

    public void StartRebuildProcess(Templates.ObjectTemplate rebuild, uint spawnerId)
    {
        rebuildTemplate = rebuild;
        SpawnerId = spawnerId;
        NewWorkerRespawnProcess(Game.FindObject(workerId));
    }

    public int Update()
    {
        var hole = Owner;
        GameObject? reconstructing = null, worker = null;
        if (workerId != 0)
        {
            worker = Game.FindObject(workerId);
            if (worker == null)
                NewWorkerRespawnProcess(null);
        }
        if (reconstructingId != 0)
        {
            reconstructing = Game.FindObject(reconstructingId);
            if (reconstructing == null)
            {
                NewWorkerRespawnProcess(worker);
                reconstructingId = 0;
            }
        }
        if (worker == null && workerWaitCounter > 0 && --workerWaitCounter == 0 && rebuildTemplate != null
            && Game.Templates.Find(Setting("WorkerObjectName")) is { } workerTemplate && hole.Team != null)
        {
            worker = Game.CreateObject(workerTemplate, hole.Owner, hole.Position, 0, hole.Team);
            workerId = worker.Id;
            if (worker.AI is AI.DozerAIUpdate dozer)
            {
                if (reconstructing == null)
                {
                    // A rebuild is free (the original's construct with isRebuild).
                    reconstructing = Game.CreateObject(rebuildTemplate, hole.Owner, hole.Position, hole.Orientation, hole.Team, underConstruction: true);
                    if (reconstructing != null)
                        reconstructing.IsRebuild = true;
                    reconstructing.SetStatus("RECONSTRUCTING");
                }
                dozer.Construct(reconstructing);
                foreach (var obj in Game.Objects)
                    obj.AI?.TransferAttack(hole.Id, reconstructing.Id);
                reconstructingId = reconstructing.Id;
                reconstructing.ProducerId = hole.Id;
                worker.SetStatus(Status.Unselectable);
                hole.SetStatus("MASKED");
                TransferBombs(reconstructing);
            }
        }
        if (hole.Body is { } body && body.Health < body.MaxHealth)
            body.AttemptHealing(FloatSetting("HoleHealthRegen%PerSecond", 0.1f) / LogicTime.FramesPerSecond * body.MaxHealth, hole.Id);
        if (reconstructing != null && !reconstructing.IsUnderConstruction)
        {
            reconstructing.SetStatus("RECONSTRUCTING", false);
            Game.Scripts?.TransferObjectName(hole, reconstructing);
            if (worker != null)
                Game.DestroyObject(worker);
            Game.DestroyObject(hole);
        }
        return 0;
    }

    /// <summary>Sticky bombs on the hole move to the new building.</summary>
    void TransferBombs(GameObject reconstruction)
    {
        foreach (var obj in Game.Objects)
            if (obj.IsKindOf("MINE") && obj.FindModule<SpecialPowers.StickyBombUpdate>() is { } bomb && bomb.Target == Owner)
                bomb.Init(reconstruction, null);
    }

    public void OnDie(DamageInfo killingBlow)
    {
        if (workerId != 0 && Game.FindObject(workerId) is { } worker)
            Game.DestroyObject(worker);
        workerId = 0;
        Game.DestroyObject(Owner);
    }

    public void OnDelete()
    {
        if (workerId != 0 && Game.FindObject(workerId) is { } worker)
            Game.DestroyObject(worker);
    }
}

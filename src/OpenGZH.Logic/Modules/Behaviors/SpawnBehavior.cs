using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Production;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>A spawned unit's side of the bond (the original's SlavedUpdateInterface).</summary>
public interface ISlavedUpdate
{
    void OnEnslave(GameObject slaver);
    void OnSlaverDie(DamageInfo damage) { }
    void OnSlaverDamage(DamageInfo damage) { }
    bool IsSelfTasking => false;
}

/// <summary>
/// Makes and keeps up a set of units (the original's SpawnBehavior): a supply center's free truck,
/// a Stinger Site's soldiers, an Angry Mob's members. Dead spawn are replaced after SpawnReplaceDelay
/// (OneShot makes only the first batch). Spawn leave through the object's exit, or by "budding"
/// next to an existing member (mobs). With SpawnedRequireSpawner they die with it.
/// </summary>
public sealed class SpawnBehavior : Module, IUpdateModule, IDieModule, IDamageModule, IOwnershipModule, Stealth.ISpawnBehavior
{
    const int SpawnDelayMinFrames = 16;
    const int SpawnUpdateRate = LogicTime.FramesPerSecond / 2;

    readonly List<uint> spawnIds = new();
    readonly List<int> replacementTimes = new();
    List<string> templateNames = new();
    int templateNameIndex;
    int framesToWait;
    int oneShotCountdown;
    bool active = true;
    int initialBurstCountdown;
    bool initialBurstTimesInited;
    bool aggregateHealth;
    int spawnCount = -1; // the original's NONE_SPAWNED_YET
    int selfTaskingSpawnCount;

    int SpawnNumber => IntSetting("SpawnNumber", 0);
    int SpawnReplaceDelay => FramesSetting("SpawnReplaceDelay");
    bool IsOneShot => BoolSetting("OneShot");
    bool CanReclaimOrphans => BoolSetting("CanReclaimOrphans");
    bool ExitByBudding => BoolSetting("ExitByBudding");
    bool SpawnedRequireSpawner => BoolSetting("SpawnedRequireSpawner");
    public bool SlavesHaveFreeWill => BoolSetting("SlavesHaveFreeWill");

    /// <summary>The living spawn.</summary>
    public IEnumerable<GameObject> Slaves => spawnIds.Select(Game.FindObject).Where(o => o != null)!;

    protected override void Initialize()
    {
        templateNames = ListSetting("SpawnTemplateName").SelectMany(v => RuleValue.Words(v)).ToList();
        oneShotCountdown = IsOneShot ? SpawnNumber : -1;
        initialBurstCountdown = IntSetting("InitialBurst", 0);
        aggregateHealth = BoolSetting("AggregateHealth");
    }

    public int Update()
    {
        if (aggregateHealth)
            ComputeAggregateStates();
        if (!initialBurstTimesInited)
        {
            initialBurstTimesInited = true;
            bool runtimeProduced = Owner.ProducerId != 0;
            int burst = IntSetting("InitialBurst", 0);
            for (int i = 0; i < SpawnNumber; i++)
                // (The original pushes "was produced" (0 or 1) as the time for an initial burst: all spawn at once.)
                replacementTimes.Add(burst > 0 ? (runtimeProduced ? 1 : 0) : i);
        }
        if (--framesToWait > 0)
            return 0;
        framesToWait = SpawnUpdateRate;
        if (ShouldTryToSpawn())
        {
            for (int i = 0; i < replacementTimes.Count;)
            {
                if (Frame > replacementTimes[i] && CreateSpawn())
                    replacementTimes.RemoveAt(i);
                else
                    i++;
            }
            if (IsOneShot && oneShotCountdown <= 0)
                active = false;
        }
        return 0;
    }

    bool ShouldTryToSpawn()
    {
        if (!active)
            return false;
        if (Owner.HasStatus("RECONSTRUCTING") && IsOneShot)
        {
            active = false;
            return false;
        }
        if (Owner.IsUnderConstruction || Owner.HasStatus(Status.Sold))
            return false;
        return !Owner.Owner.IsNeutral;
    }

    GameObject? ReclaimOrphanSpawn()
    {
        GameObject? closest = null;
        float closestDist = float.MaxValue;
        string prev = "";
        foreach (string name in templateNames)
        {
            // (The original skips every name that differs from the previous one, so only the first is searched.)
            if (prev != name && prev.Length > 0)
                continue;
            prev = name;
            if (Game.Templates.Find(name) is not { } template)
                continue;
            closest = null;
            closestDist = float.MaxValue;
            foreach (var obj in Game.ObjectsOwnedBy(Owner.Owner))
            {
                if (!obj.Template.IsEquivalentTo(template) || obj.ProducerId != 0)
                    continue;
                float d = MathUtil.DistanceSquared2D(obj.Position, Owner.Position);
                if (d < closestDist)
                {
                    closest = obj;
                    closestDist = d;
                }
            }
        }
        return closest;
    }

    bool CreateSpawn()
    {
        var parent = Owner;
        var exit = parent.FindModule<IProductionExit>();
        if (exit == null || templateNames.Count == 0)
            return false;
        GameObject? newSpawn = null;
        bool reclaimed = false;
        if (CanReclaimOrphans && !IsOneShot)
        {
            newSpawn = ReclaimOrphanSpawn();
            reclaimed = newSpawn != null;
        }
        if (newSpawn == null)
        {
            if (Game.Templates.Find(templateNames[templateNameIndex]) is not { } template)
                return false;
            newSpawn = Game.CreateObject(template, parent.Owner, parent.Position, parent.Orientation, parent.Team);
            game_OnUnitCreated(parent, newSpawn);
            templateNameIndex = (templateNameIndex + 1) % templateNames.Count;
        }
        newSpawn.ProducerId = parent.Id;
        newSpawn.FindModule<ISlavedUpdate>()?.OnEnslave(parent);
        spawnIds.Add(newSpawn.Id);
        if (!reclaimed)
        {
            if (ExitByBudding)
            {
                bool barracksExit = false;
                if (initialBurstCountdown > 0 && Game.FindObject(parent.ProducerId) is { } barracks && barracks.IsKindOf(Kind.Structure)
                    && barracks.FindModule<IProductionExit>() is { } barracksExitInterface)
                {
                    barracksExitInterface.Exit(newSpawn);
                    newSpawn.ProducerId = parent.Id;
                    initialBurstCountdown--;
                    barracksExit = true;
                }
                if (!barracksExit)
                {
                    var budHost = Slaves.Where(o => o != newSpawn).MinBy(o => MathUtil.DistanceSquared2D(o.Position, parent.Position));
                    ExitByBud(newSpawn, budHost ?? parent);
                }
            }
            else
                exit.Exit(newSpawn);
        }
        if (IsOneShot)
            oneShotCountdown--;
        spawnCount = spawnCount == -1 ? 1 : spawnCount + 1;
        return true;
    }

    void game_OnUnitCreated(GameObject parent, GameObject spawn)
    {
        spawn.Owner.OnUnitCreated(parent, spawn);
    }

    /// <summary>The original's exitObjectByBudding: the new member appears right next to an existing one.</summary>
    void ExitByBud(GameObject spawn, GameObject host)
    {
        float angle = Game.Random.Float(0, MathF.Tau);
        var at = host.Position + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * (host.BoundingCircleRadius + spawn.BoundingCircleRadius);
        at.Z = Game.Terrain.GroundHeight(at);
        if (spawn.AI != null)
            spawn.AI.Teleport(at);
        else
            spawn.SetPosition(at);
    }

    /// <summary>A spawn died: it is replaced after the delay. A mob with aggregate health dies with its last member.</summary>
    public void OnSpawnDeath(uint deadSpawn, DamageInfo damage)
    {
        int i = spawnIds.IndexOf(deadSpawn);
        if (i < 0)
            return;
        replacementTimes.Add(SpawnReplaceDelay + Frame);
        spawnIds.RemoveAt(i);
        spawnCount--;
        if (spawnCount == 0 && aggregateHealth)
        {
            if (Game.FindObject(damage.SourceId) is { } killer)
                killer.ScoreTheKill(Owner);
            Game.DestroyObject(Owner);
        }
    }

    public void OnDie(DamageInfo killingBlow)
    {
        foreach (var spawn in Slaves.ToList())
        {
            spawn.FindModule<ISlavedUpdate>()?.OnSlaverDie(killingBlow);
            spawn.ProducerId = 0;
        }
        if (SpawnedRequireSpawner)
            foreach (var spawn in spawnIds.Select(Game.FindObject).ToList())
                if (spawn != null && !spawn.IsEffectivelyDead)
                    spawn.Kill();
    }

    public void OnDelete()
    {
        if (!SpawnedRequireSpawner)
            return;
        foreach (var spawn in spawnIds.Select(Game.FindObject).ToList())
            if (spawn != null && !spawn.IsEffectivelyDead)
                Game.DestroyObject(spawn);
    }

    public void OnDamage(DamageInfo damage)
    {
        foreach (var spawn in Slaves.ToList())
            spawn.FindModule<ISlavedUpdate>()?.OnSlaverDamage(damage);
    }

    public void StopSpawning() => active = false;
    public void StartSpawning() => active = true;

    public void OrderSlavesToAttackTarget(GameObject target, int maxShots, CommandSource source)
    {
        foreach (var spawn in Slaves.ToList())
            spawn.AI?.AttackObject(target, source, maxShots);
    }

    public void OrderSlavesToAttackPosition(Vector3 pos, int maxShots, CommandSource source)
    {
        foreach (var spawn in Slaves.ToList())
            spawn.AI?.AttackPosition(pos, source, maxShots);
    }

    public void OrderSlavesToGoIdle(CommandSource source)
    {
        foreach (var spawn in Slaves.ToList())
            spawn.AI?.Stop(source);
    }

    public bool CanAnySlavesAttack() => Slaves.Any(s => s.Weapons.HasAnyWeapon);

    public void GiveSlavesStealthUpgrade(bool grant)
    {
        foreach (var spawn in Slaves)
            spawn.SetStatus("CAN_STEALTH", grant);
    }

    /// <summary>The original's maySpawnSelfTaskAI: a computer-led mob lets some members pick their own fights.</summary>
    public bool MaySpawnSelfTaskAI(float maxSelfTaskersRatio)
    {
        if (spawnCount == 0 || maxSelfTaskersRatio == 0 || Owner.AI is not { } ai || ai.LastCommandSource != CommandSource.AI)
            return false;
        return (float)selfTaskingSpawnCount / spawnCount < maxSelfTaskersRatio;
    }

    public GameObject? ClosestSlave(Vector3 pos) => Slaves.MinBy(o => MathUtil.DistanceSquared2D(o.Position, pos));

    public bool AreAllSlavesStealthed() => Slaves.All(s => s.FindModule<Stealth.StealthUpdate>() != null && s.HasStatus("CAN_STEALTH"));

    public void RevealSlaves()
    {
        foreach (var spawn in Slaves)
            spawn.FindModule<Stealth.StealthUpdate>()?.MarkAsDetected();
    }

    /// <summary>
    /// AggregateHealth (Angry Mob): the leader's health shows the members' total, veterancy is shared
    /// both ways, and the leader itself can't be targeted.
    /// </summary>
    void ComputeAggregateStates()
    {
        var obj = Owner;
        int count = 0;
        float health = 0, healthMax = 0;
        selfTaskingSpawnCount = 0;
        foreach (var spawn in Slaves.ToList())
        {
            if (spawn.FindModule<ISlavedUpdate>() is { IsSelfTasking: true })
                selfTaskingSpawnCount++;
            if (spawn.Veterancy > obj.Veterancy)
                obj.SetVeterancy(spawn.Veterancy);
            else if (spawn.Veterancy < obj.Veterancy)
                spawn.SetVeterancy(obj.Veterancy);
            health += spawn.Health;
            healthMax += spawn.MaxHealth;
            count++;
        }
        if (obj.Body != null)
        {
            if (count > 0)
            {
                healthMax /= count;
                float perfect = healthMax * SpawnNumber;
                obj.Body.SetHealth(obj.MaxHealth * (perfect > 0 ? health / perfect : 0));
            }
            else
                obj.Body.SetHealth(0);
        }
        obj.SetStatus("MASKED");
    }
}

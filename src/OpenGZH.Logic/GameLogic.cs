using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Locomotion;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Scripting;
using OpenGZH.Logic.Spatial;
using OpenGZH.Logic.Templates;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic;

/// <summary>
/// The whole simulation of one game. Deterministic: given the same map, settings and player
/// commands, every machine computes exactly the same result (this is what makes multiplayer work).
///
/// Each call to <see cref="Update"/> advances one logic frame (1/30 s):
///   1. execute queued player commands,
///   2. run the mission scripts,
///   3. run object update modules,
///   4. remove destroyed objects.
/// </summary>
public sealed class GameLogic
{
    public RuleSet Rules { get; }
    public TemplateStore Templates { get; }
    public RulesCatalog Catalog { get; }
    public GameRandom Random { get; }
    public TerrainLogic Terrain { get; }
    public PartitionManager Partition { get; }
    public PlayerList Players { get; }
    public GameEvents Events { get; } = new();
    public RankSystem Ranks { get; }
    public ScriptEngine? Scripts { get; set; }
    public Commands.CommandQueue Commands { get; }
    public Pathfinder Pathfinder { get; }
    public DamageSystem Damage { get; }
    public ObjectCreationSystem ObjectCreation { get; }
    public PayloadDelivery PayloadDelivery { get; }
    public CrateSystem Crates { get; }

    /// <summary>Fog of war and explored areas per player.</summary>
    public Vision.ShroudManager Shroud { get; }

    /// <summary>Team definitions and their instances.</summary>
    public TeamFactory Teams { get; }

    /// <summary>Lasting effects modules keep (beams, particle systems, sound loops) for the client.</summary>
    public Effects.EffectRegistry Effects { get; }
    public HistoricDamageTracker HistoricDamage { get; }
    public CostCalculator Costs { get; } = new();

    /// <summary>Bone positions from the models (exits, fire points); without art, objects use their own position.</summary>
    public Art.ModelBones Bones { get; init; } = new(null);
    public Construction.BuildAssistant Building { get; }
    public WaypointList? Waypoints { get; set; }

    /// <summary>The global weapon bonus table from GameData ("VETERAN DAMAGE 110%" ...).</summary>
    public WeaponBonusTable WeaponBonuses { get; }

    /// <summary>Current logic frame (30 per second).</summary>
    public int Frame { get; private set; }

    public bool IsGameOver { get; set; }

    /// <summary>The skirmish "limit superweapons" option: one of each superweapon per player (0 = no limit).</summary>
    public int SuperweaponRestriction { get; set; }

    /// <summary>
    /// MaxSimultaneousOfType for a template (0 = no limit); "DeterminedBySuperweaponRestriction" follows
    /// <see cref="SuperweaponRestriction"/> (the original's ThingTemplate::getMaxSimultaneousOfType).
    /// </summary>
    public int MaxSimultaneousOfType(Templates.ObjectTemplate template)
    {
        string text = template.Rules.GetString("MaxSimultaneousOfType");
        if (text.Equals("DeterminedBySuperweaponRestriction", StringComparison.OrdinalIgnoreCase))
            return SuperweaponRestriction;
        return OpenGZH.Core.Rules.RuleValue.Int(text, 0);
    }

    /// <summary>The scripted battle behind the main menu (the original's GAME_SHELL mode).</summary>
    public bool IsShellGame { get; set; }

    /// <summary>Command buttons scripts swapped into command sets (COMMANDBAR_ADD/REMOVE_BUTTON): (set, 0-based slot) -> button, null = removed.</summary>
    public Dictionary<(string Set, int Slot), CommandButton?> ControlBarOverrides { get; } = new();

    /// <summary>Script overrides of what may be built (TECHTREE_MODIFY_BUILDABILITY_OBJECT): template -> BuildableStatus name.</summary>
    public Dictionary<string, string> BuildableStatusOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The highest general's rank scripts allow (PLAYER_SET_RANKLEVELLIMIT).</summary>
    public int RankLevelLimit { get; set; } = 1000;

    /// <summary>Frames a hulk lasts, when a script overrides it (-1 = the object's own lifetime).</summary>
    public int HulkMaxLifetimeOverride { get; set; } = -1;

    public bool IsScoringEnabled { get; set; } = true;

    /// <summary>A campaign or challenge mission (one human against map-scripted computer players).</summary>
    public bool IsSinglePlayer { get; set; }

    /// <summary>Multiplayer and skirmish defeat checks.</summary>
    public VictoryConditions Victory { get; }

    /// <summary>The client tells the logic what is selected and whether the camera is still moving (script conditions ask).</summary>
    public Func<string, bool> IsNamedObjectSelected { get; set; } = _ => false;
    public Func<bool> IsCameraMovementFinished { get; set; } = () => true;

    AI.AIData? aiData;
    public AI.AIData AIData => aiData ??= new AI.AIData(Rules);

    readonly Dictionary<uint, GameObject> objects = new();
    readonly List<GameObject> objectList = new();
    readonly List<GameObject> pendingDestroy = new();
    readonly Dictionary<string, GameObject> objectsByName = new(StringComparer.OrdinalIgnoreCase);
    uint nextObjectId = 1;

    public GameLogic(RuleSet rules, TerrainLogic terrain, ulong randomSeed)
    {
        Rules = rules;
        Templates = new TemplateStore(rules);
        Catalog = new RulesCatalog(rules);
        Random = new GameRandom(randomSeed);
        Terrain = terrain;
        Partition = new PartitionManager(terrain.WorldWidth, terrain.WorldHeight);
        Teams = new TeamFactory(this);
        Players = new PlayerList(this);
        Ranks = new RankSystem(this);
        Commands = new Commands.CommandQueue(this);
        Pathfinder = new Pathfinder(terrain);
        Damage = new DamageSystem(this);
        ObjectCreation = new ObjectCreationSystem(this);
        Shroud = new Vision.ShroudManager(this);
        Effects = new Effects.EffectRegistry(this);
        PayloadDelivery = new PayloadDelivery(this);
        Crates = new CrateSystem(this);
        Building = new Construction.BuildAssistant(this);
        HistoricDamage = new HistoricDamageTracker(this);
        Victory = new VictoryConditions(this);
        Pathfinder.ObjectSource = () => objectList;
        terrain.WaterDamage += (water, amount) =>
        {
            foreach (var obj in objectList.ToList())
                if (!obj.IsDestroyed && water.Contains(obj.Position.X, obj.Position.Y) && obj.Position.Z < water.Height)
                    obj.Damage(new DamageInfo { Amount = amount, DamageType = "WATER", DeathType = "NORMAL" });
        };
        var bonusLines = rules.Section("GameData")["WeaponBonus"]?.AsList().Select(n => n.Value ?? "") ?? [];
        WeaponBonuses = new WeaponBonusTable(bonusLines);
    }

    public IReadOnlyList<GameObject> Objects => objectList;
    public GameObject? FindObject(uint id) => id != 0 && objects.TryGetValue(id, out var obj) ? obj : null;
    public GameObject? FindObjectByName(string name) => objectsByName.GetValueOrDefault(name);
    public IEnumerable<GameObject> ObjectsOwnedBy(Player player) => objectList.Where(o => o.Owner == player && !o.IsDestroyed);

    // ---- Creating and destroying objects ----

    /// <summary>Creates an object from a template name. Returns null (and logs) if the template is unknown.</summary>
    public GameObject? CreateObject(string templateName, Player owner, Vector3 position, float orientation = 0, Team? team = null)
    {
        var template = Templates.Find(templateName);
        if (template == null)
        {
            Log.Warning($"Unknown object template '{templateName}'.");
            return null;
        }
        return CreateObject(template, owner, position, orientation, team);
    }

    public GameObject CreateObject(ObjectTemplate template, Player owner, Vector3 position, float orientation = 0, Team? team = null, bool underConstruction = false)
    {
        var obj = new GameObject(this, nextObjectId++, template, owner, position, orientation);
        obj.SetTeam(team ?? owner.DefaultTeam);
        obj.InitTriggerAreas();
        if (underConstruction)
        {
            obj.SetStatus(Core.Status.UnderConstruction);
            obj.ConstructionPercent = 0;
            obj.SetCondition(Condition.AwaitingConstruction);
        }
        objects[obj.Id] = obj;
        objectList.Add(obj);
        Partition.Add(obj);

        if (BlocksPathing(obj))
            Pathfinder.AddObstacle(obj);

        obj.CreateModules();
        foreach (var create in obj.Modules.OfType<Modules.ICreateModule>().ToList())
            create.OnCreate();

        if (underConstruction)
            obj.Body?.SetHealth(1);
        else
            owner.Energy.Adjust(template.EnergyProduction, adding: true);
        obj.CheckUpgradeModules();
        owner.OnObjectJoined(obj, joining: true);
        Events.ObjectCreated(obj);
        return obj;
    }

    /// <summary>
    /// An object that appears ready-made (starting command center, map objects) gets its
    /// build-complete hooks, as the original does for "magically created" things.
    /// </summary>
    public void TreatAsBuilt(GameObject? obj)
    {
        if (obj == null || obj.IsUnderConstruction)
            return;
        foreach (var create in obj.Modules.OfType<Modules.ICreateModule>().ToList())
            create.OnBuildComplete();
        obj.CheckUpgradeModules();
    }

    /// <summary>Finishes a building: it starts working (power, production, abilities).</summary>
    public void CompleteConstruction(GameObject structure)
    {
        if (!structure.IsUnderConstruction)
            return;
        structure.SetStatus(Core.Status.UnderConstruction, false);
        structure.ConstructionPercent = 100;
        structure.SetCondition(Condition.PartiallyConstructed, false);
        structure.SetCondition(Condition.ActivelyBeingConstructed, false);
        structure.SetCondition(Condition.AwaitingConstruction, false);
        structure.SetCondition(Condition.ConstructionComplete);
        structure.Owner.Energy.Adjust(structure.Template.EnergyProduction, adding: true);
        // A free rebuild (a GLA rebuild hole) does not score again.
        if (!structure.IsRebuild)
        {
            structure.Owner.Score.AddObjectBuilt(structure);
            structure.Owner.Score.AddMoneySpent(Costs.BuildCost(structure.Owner, structure.Template));
        }
        foreach (var create in structure.Modules.OfType<Modules.ICreateModule>().ToList())
            create.OnBuildComplete();
        structure.CheckUpgradeModules();
        Events.Eva("BuildingComplete", structure.Owner);
        structure.Owner.AI?.OnStructureProduced(FindObject(structure.BuilderId), structure);
        AnnounceSuperweapon(structure);
        Events.PlaySound(structure.Template.Rules.GetString("SoundOnConstructionComplete"), structure.Position, structure.Owner, structure);
    }

    /// <summary>EVA tells everyone a superweapon was built ("Enemy Particle Cannon detected").</summary>
    void AnnounceSuperweapon(GameObject structure)
    {
        bool Has(params string[] types) => types.Any(t => Modules.SpecialPowers.SpecialPowerRules.ModuleOfType(structure, t) != null);
        string? kind = Has("SPECIAL_PARTICLE_UPLINK_CANNON", "SUPW_SPECIAL_PARTICLE_UPLINK_CANNON", "LAZR_SPECIAL_PARTICLE_UPLINK_CANNON") ? "ParticleCannon"
            : Has("SPECIAL_NEUTRON_MISSILE", "NUKE_SPECIAL_NEUTRON_MISSILE", "SUPW_SPECIAL_NEUTRON_MISSILE") ? "Nuke"
            : Has("SPECIAL_SCUD_STORM") ? "ScudStorm" : null;
        if (kind == null || Players.Local is not { } local)
            return;
        string whose = local == structure.Owner ? "Own" : local.RelationshipTo(structure.Team) != Relationship.Enemies ? "Ally" : "Enemy";
        Events.Eva($"SuperweaponDetected_{whose}_{kind}", local);
    }

    /// <summary>Gives an object a script name so mission scripts can refer to it.</summary>
    public void SetObjectName(GameObject obj, string name)
    {
        if (obj.Name.Length > 0)
            objectsByName.Remove(obj.Name);
        obj.Name = name;
        if (name.Length > 0)
        {
            objectsByName[name] = obj;
            Scripts?.AddObjectToCache(obj);
        }
    }

    /// <summary>Removes an object at the end of the current frame (no death effects; see <see cref="GameObject.Kill"/>).</summary>
    public void DestroyObject(GameObject obj)
    {
        if (obj.IsDestroyed)
            return;
        obj.MarkDestroyed();
        pendingDestroy.Add(obj);
    }

    void RemoveDestroyedObjects()
    {
        foreach (var obj in pendingDestroy)
        {
            objects.Remove(obj.Id);
            objectList.Remove(obj);
            Partition.Remove(obj);
            if (BlocksPathing(obj))
                Pathfinder.RemoveObstacle(obj);
            if (obj.Name.Length > 0 && objectsByName.GetValueOrDefault(obj.Name) == obj)
                objectsByName.Remove(obj.Name);
            if (!obj.IsUnderConstruction)
                obj.Owner.Energy.Adjust(obj.Template.EnergyProduction, adding: false);
            foreach (var upgrade in obj.FindModules<Modules.UpgradeModule>())
                upgrade.OnRemoved();
            foreach (var module in obj.FindModules<Modules.IOwnershipModule>().ToList())
                module.OnDelete();
            Scripts?.NotifyOfObjectDestruction(obj);
            obj.ClearTeam();
            Events.ObjectDestroyed(obj);
        }
        pendingDestroy.Clear();
    }

    internal void OnOwnerChanged(GameObject obj, Player oldOwner)
    {
        if (!obj.IsUnderConstruction)
        {
            oldOwner.Energy.Adjust(obj.Template.EnergyProduction, adding: false);
            obj.Owner.Energy.Adjust(obj.Template.EnergyProduction, adding: true);
        }
        oldOwner.OnObjectJoined(obj, joining: false);
        obj.Owner.OnObjectJoined(obj, joining: true);
        foreach (var upgrade in obj.FindModules<Modules.UpgradeModule>())
            upgrade.OnOwnerChanged(oldOwner);
        foreach (var module in obj.FindModules<Modules.IOwnershipModule>().ToList())
            module.OnCapture(oldOwner, obj.Owner);
        obj.CheckUpgradeModules();
        Events.OwnerChanged(obj, oldOwner);
    }

    /// <summary>Buildings block movement (bridges and walls-you-can-walk-on excepted).</summary>
    static bool BlocksPathing(GameObject obj) =>
        obj.IsKindOf(Kind.Structure) && !obj.IsKindOf(Kind.Bridge) && !obj.IsKindOf("WALK_ON_TOP_OF_WALL") && !obj.IsKindOf("LANDMARK_BRIDGE");

    // ---- The frame loop ----

    /// <summary>Advances the simulation by one logic frame.</summary>
    static readonly Modules.UpdatePhase[] UpdatePhases =
        [Modules.UpdatePhase.Initial, Modules.UpdatePhase.Physics, Modules.UpdatePhase.Normal, Modules.UpdatePhase.Final];

    /// <summary>The ground height, or the top of a building standing there (helicopters fly over buildings).</summary>
    public float GroundOrStructureHeight(float x, float y)
    {
        float terrain = Terrain.GroundHeight(x, y);
        float tallest = 0;
        foreach (var obj in Partition.Query(new Vector3(x, y, terrain), 1))
            if (obj.IsKindOf(Kind.Structure) && MathUtil.Distance2D(obj.Position, new Vector3(x, y, 0)) <= 1 + obj.BoundingCircleRadius)
                tallest = Math.Max(tallest, obj.Geometry.MaxHeightAbovePosition);
        return terrain + tallest;
    }

    public void Update()
    {
        if (IsGameOver)
            return;

        Commands.ExecutePending();
        long t0 = GameProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Scripts?.Update();
        if (GameProfiler.Enabled) GameProfiler.Add("~Scripts", t0);
        // A script froze time (cut scenes): only the scripts run.
        if (Scripts is { IsTimeFrozenByScript: true })
            return;
        t0 = GameProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Players.Update();
        if (GameProfiler.Enabled) GameProfiler.Add("~Players (AI, team scripts)", t0);

        // Each phase runs for every object before the next (AIs steer, physics moves, then the rest).
        // Copy: modules may create objects while updating.
        var snapshot = objectList.ToArray();
        foreach (var phase in UpdatePhases)
            foreach (var obj in snapshot)
                if (!obj.IsDestroyed)
                    obj.RunUpdates(phase);

        Building.Update();
        Terrain.Update(Frame);
        foreach (var player in Players.All)
            player.UpdatePowerBrownOut(force: Frame % LogicTime.FramesPerSecond == 0);
        t0 = GameProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Shroud.Update();
        if (GameProfiler.Enabled) GameProfiler.Add("~Shroud", t0);
        // Beams follow their ends and grow after the objects moved (the original's client update).
        Effects.Update();
        t0 = GameProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Partition.ProcessCollisions();
        if (GameProfiler.Enabled) GameProfiler.Add("~Collisions", t0);
        RemoveDestroyedObjects();
        Victory.Update();
        Frame++;
    }

    // ---- Queries used by many modules ----

    /// <summary>Objects whose center is within <paramref name="radius"/> of a point (2D distance).</summary>
    public IEnumerable<GameObject> ObjectsInRadius(Vector3 center, float radius) =>
        Partition.Query(center, radius).Where(o => !o.IsDestroyed);

    public static float Distance2D(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
}

/// <summary>Logging for the simulation (goes to the console; the client can redirect it).</summary>
public static class Log
{
    public static Action<string> Output { get; set; } = Console.WriteLine;
    static readonly HashSet<string> warned = new();

    public static void Info(string message) => Output(message);

    /// <summary>Logs a warning once per distinct message, so a missing asset doesn't flood the log.</summary>
    public static void Warning(string message)
    {
        lock (warned)
            if (!warned.Add(message))
                return;
        Output("WARNING: " + message);
    }
}

/// <summary>Optional timing of the logic's parts (for finding slow modules); off by default.</summary>
public static class GameProfiler
{
    public static bool Enabled { get; set; }
    static readonly Dictionary<string, long> ticks = new();

    public static void Add(string name, long startedTimestamp)
    {
        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp;
        lock (ticks)
            ticks[name] = ticks.GetValueOrDefault(name) + elapsed;
    }

    public static IEnumerable<(string Name, double Milliseconds)> Report()
    {
        lock (ticks)
            return ticks.Select(kv => (kv.Key, kv.Value * 1000.0 / System.Diagnostics.Stopwatch.Frequency)).OrderByDescending(e => e.Item2).ToList();
    }

    public static void Reset()
    {
        lock (ticks)
            ticks.Clear();
    }
}

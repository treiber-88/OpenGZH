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
        Players = new PlayerList(this);
        Ranks = new RankSystem(this);
        Commands = new Commands.CommandQueue(this);
        Pathfinder = new Pathfinder(terrain);
        Damage = new DamageSystem(this);
        ObjectCreation = new ObjectCreationSystem(this);
        PayloadDelivery = new PayloadDelivery(this);
        Crates = new CrateSystem(this);
        Building = new Construction.BuildAssistant(this);
        HistoricDamage = new HistoricDamageTracker(this);
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
        Events.ObjectCreated(obj);
        return obj;
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
        structure.Owner.BuildingsBuilt++;
        foreach (var create in structure.Modules.OfType<Modules.ICreateModule>().ToList())
            create.OnBuildComplete();
        structure.CheckUpgradeModules();
        Events.Eva("BuildingComplete", structure.Owner);
        Events.PlaySound(structure.Template.Rules.GetString("SoundOnConstructionComplete"), structure.Position, structure.Owner);
    }

    /// <summary>Gives an object a script name so mission scripts can refer to it.</summary>
    public void SetObjectName(GameObject obj, string name)
    {
        if (obj.Name.Length > 0)
            objectsByName.Remove(obj.Name);
        obj.Name = name;
        if (name.Length > 0)
            objectsByName[name] = obj;
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
        Scripts?.Update();

        // Each phase runs for every object before the next (AIs steer, physics moves, then the rest).
        // Copy: modules may create objects while updating.
        var snapshot = objectList.ToArray();
        foreach (var phase in UpdatePhases)
            foreach (var obj in snapshot)
                if (!obj.IsDestroyed)
                    obj.RunUpdates(phase);

        Building.Update();
        Partition.ProcessCollisions();
        RemoveDestroyedObjects();
        Players.CheckVictoryConditions();
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
        if (warned.Add(message))
            Output("WARNING: " + message);
    }
}

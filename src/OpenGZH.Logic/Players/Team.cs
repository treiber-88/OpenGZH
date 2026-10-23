using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic.Players;

/// <summary>AI attitudes a team can have (the original's AttitudeType).</summary>
public enum Attitude { Sleep = -2, Passive = -1, Normal = 0, Alert = 1, Aggressive = 2, Invalid = 3 }

/// <summary>Up to seven unit types a team is made of, with how many of each.</summary>
public readonly record struct TeamUnitInfo(int MinUnits, int MaxUnits, string UnitThingName);

/// <summary>
/// A team definition from the map or the skirmish scripts (the original's TeamTemplateInfo): what the
/// team is made of, where it lives, what AI production thinks of it, and which scripts run on it.
/// </summary>
public sealed class TeamTemplateInfo
{
    public const int MaxUnitTypes = 7;
    public const int MaxGenericScripts = 16;

    public List<TeamUnitInfo> Units { get; } = new();
    public Vector3 HomeLocation { get; }
    public bool HasHomeLocation { get; }
    public string ScriptOnCreate { get; } = "";
    public string ScriptOnIdle { get; } = "";
    public int InitialIdleFrames { get; }
    public string ScriptOnEnemySighted { get; } = "";
    public string ScriptOnAllClear { get; } = "";
    public string ScriptOnUnitDestroyed { get; } = "";
    public string ScriptOnDestroyed { get; } = "";
    public float DestroyedThreshold { get; }
    public bool IsAIRecruitable { get; }
    public bool IsBaseDefense { get; }
    public bool IsPerimeterDefense { get; }
    public bool AutomaticallyReinforce { get; }
    public bool TransportsReturn { get; }
    public bool AvoidThreats { get; }
    public bool AttackCommonTarget { get; }
    public int MaxInstances { get; }
    /// <summary>Changes as the AI succeeds or fails at building the team.</summary>
    public int ProductionPriority { get; set; }
    public int ProductionPrioritySuccessIncrease { get; }
    public int ProductionPriorityFailureDecrease { get; }
    public Attitude InitialTeamAttitude { get; } = Attitude.Normal;
    public string TransportUnitType { get; } = "";
    public string StartReinforceWaypoint { get; } = "";
    public bool TeamStartsFull { get; }
    public bool TransportsExit { get; }
    public VeterancyLevel Veterancy { get; }
    public string ProductionCondition { get; } = "";
    public bool ExecuteActions { get; }
    public string[] GenericScripts { get; } = new string[MaxGenericScripts];

    public TeamTemplateInfo(Dictionary<string, object> d, WaypointList? waypoints)
    {
        static string S(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
        static int I(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v switch { int i => i, float f => (int)f, bool b => b ? 1 : 0, _ => 0 } : 0;
        static float R(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v switch { float f => f, int i => i, _ => 0 } : 0;
        static bool B(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) && v switch { bool b => b, int i => i != 0, _ => false };

        for (int n = 1; n <= MaxUnitTypes; n++)
        {
            int max = I(d, $"teamUnitMaxCount{n}");
            if (max > 0 && d.ContainsKey($"teamUnitType{n}"))
                Units.Add(new TeamUnitInfo(I(d, $"teamUnitMinCount{n}"), max, S(d, $"teamUnitType{n}")));
        }
        if (d.ContainsKey("teamHome") && waypoints?.Find(S(d, "teamHome")) is { } home)
        {
            HomeLocation = home.Position;
            HasHomeLocation = true;
        }
        ScriptOnCreate = S(d, "teamOnCreateScript");
        IsAIRecruitable = B(d, "teamIsAIRecruitable");
        IsBaseDefense = B(d, "teamIsBaseDefense");
        IsPerimeterDefense = B(d, "teamIsPerimeterDefense");
        AutomaticallyReinforce = B(d, "teamAutoReinforce");
        if (d.ContainsKey("teamAggressiveness"))
            InitialTeamAttitude = (Attitude)I(d, "teamAggressiveness");
        TransportsReturn = B(d, "teamTransportsReturn");
        AvoidThreats = B(d, "teamAvoidThreats");
        AttackCommonTarget = B(d, "teamAttackCommonTarget");
        MaxInstances = I(d, "teamMaxInstances");
        ScriptOnIdle = S(d, "teamOnIdleScript");
        InitialIdleFrames = I(d, "teamInitialIdleFrames");
        ScriptOnEnemySighted = S(d, "teamEnemySightedScript");
        ScriptOnAllClear = S(d, "teamAllClearScript");
        ScriptOnDestroyed = S(d, "teamOnDestroyedScript");
        DestroyedThreshold = R(d, "teamDestroyedThreshold");
        ScriptOnUnitDestroyed = S(d, "teamOnUnitDestroyedScript");
        ProductionPriority = I(d, "teamProductionPriority");
        ProductionPrioritySuccessIncrease = I(d, "teamProductionPrioritySuccessIncrease");
        ProductionPriorityFailureDecrease = I(d, "teamProductionPriorityFailureDecrease");
        ProductionCondition = S(d, "teamProductionCondition");
        ExecuteActions = B(d, "teamExecutesActionsOnCreate");
        for (int i = 0; i < MaxGenericScripts; i++)
            GenericScripts[i] = S(d, $"teamGenericScriptHook{i}");
        TransportUnitType = S(d, "teamTransport");
        TransportsExit = B(d, "teamTransportsExit");
        TeamStartsFull = B(d, "teamStartsFull");
        StartReinforceWaypoint = S(d, "teamReinforcementOrigin");
        Veterancy = (VeterancyLevel)I(d, "teamVeterancy");
    }
}

/// <summary>
/// A named team definition (the original's TeamPrototype). Scripts and the AI make instances of it
/// (<see cref="Team"/>); a singleton has at most one instance.
/// </summary>
public sealed class TeamPrototype
{
    readonly TeamFactory factory;
    internal readonly List<Team> instances = new();
    bool productionConditionAlwaysFalse;
    Scripting.Script? productionConditionScript;
    bool retrievedGenericScripts;
    readonly Scripting.Script?[] genericScriptsToRun = new Scripting.Script?[TeamTemplateInfo.MaxGenericScripts];

    public int Id { get; }
    public string Name { get; }
    public bool IsSingleton { get; private set; }
    public TeamTemplateInfo TemplateInfo { get; private set; }
    public Player ControllingPlayer { get; private set; }
    public string AttackPriorityName { get; set; } = "";

    /// <summary>Settings from the map's team definition.</summary>
    public Dictionary<string, object> Properties { get; private set; }

    /// <summary>Instances, newest first (the original prepends).</summary>
    public IReadOnlyList<Team> Instances => instances;

    internal TeamPrototype(TeamFactory factory, string name, Player owner, bool isSingleton, Dictionary<string, object> properties, int id)
    {
        this.factory = factory;
        Name = name;
        ControllingPlayer = owner;
        IsSingleton = isSingleton;
        Properties = properties;
        TemplateInfo = new TeamTemplateInfo(properties, owner.Game.Waypoints);
        Id = id;
        owner.TeamPrototypes.Add(this);
    }

    public Team? FirstInstance => instances.Count > 0 ? instances[0] : null;

    /// <summary>The map defines a team that already exists (a player's default team): take its settings.</summary>
    internal void Reload(Dictionary<string, object> properties, Player owner, bool isSingleton)
    {
        Properties = properties;
        IsSingleton = isSingleton;
        TemplateInfo = new TeamTemplateInfo(properties, owner.Game.Waypoints);
        retrievedGenericScripts = false;
        productionConditionAlwaysFalse = false;
        productionConditionScript = null;
        if (owner != ControllingPlayer)
            SetControllingPlayer(owner);
    }

    public Team? FindTeamById(int id) => instances.FirstOrDefault(t => t.Id == id);

    public void SetControllingPlayer(Player newController)
    {
        ControllingPlayer.TeamPrototypes.Remove(this);
        ControllingPlayer = newController;
        newController.TeamPrototypes.Add(this);
    }

    public int CountTeamInstances() => instances.Count;

    public bool HasAnyBuildings() => instances.Any(t => t.HasAnyBuildings());
    public bool HasAnyUnits() => instances.Any(t => t.HasAnyUnits());
    public bool HasAnyObjects() => instances.Any(t => t.HasAnyObjects());
    public bool HasAnyBuildFacility() => instances.Any(t => t.HasAnyBuildFacility());
    public int CountBuildings() => instances.Sum(t => t.CountBuildings());

    public void HealAllObjects()
    {
        foreach (var t in instances.ToList())
            t.HealAllObjects();
    }

    public void DamageTeamMembers(float amount)
    {
        foreach (var t in instances.ToList())
            t.DamageTeamMembers(amount);
    }

    public void IncreaseAIPriorityForSuccess() => TemplateInfo.ProductionPriority += TemplateInfo.ProductionPrioritySuccessIncrease;
    public void DecreaseAIPriorityForFailure() => TemplateInfo.ProductionPriority -= TemplateInfo.ProductionPriorityFailureDecrease;

    internal void TeamAboutToBeDeleted(Team team)
    {
        foreach (var t in instances)
            t.RemoveOverrideTeamRelationship(team.Id);
    }

    /// <summary>Runs every instance's state checks, then deletes empty non-default instances.</summary>
    public void UpdateState()
    {
        foreach (var t in instances.ToList())
            t.UpdateState();
        bool done = false;
        while (!done)
        {
            done = true;
            foreach (var t in instances)
            {
                if (t.MemberCount > 0 || IsSingleton || t.ControllingPlayer.DefaultTeam == t || !t.IsActive)
                    continue;
                factory.DeleteTeam(t);
                done = false;
                break;
            }
        }
    }

    /// <summary>The team's GenericScriptHook scripts (copies, run on each instance).</summary>
    public Scripting.Script? GetGenericScript(int index)
    {
        if (!retrievedGenericScripts)
        {
            retrievedGenericScripts = true;
            for (int i = 0; i < TeamTemplateInfo.MaxGenericScripts; i++)
            {
                string name = TemplateInfo.GenericScripts[i];
                genericScriptsToRun[i] = name.Length > 0 ? ControllingPlayer.Game.Scripts?.FindScript(name)?.Duplicate() : null;
            }
        }
        return genericScriptsToRun[index];
    }

    /// <summary>Should the AI build this team now? (its teamProductionCondition script, on the owner's difficulty)</summary>
    public bool EvaluateProductionCondition()
    {
        if (productionConditionAlwaysFalse)
            return false;
        var engine = ControllingPlayer.Game.Scripts;
        if (engine == null)
            return false;
        int frame = ControllingPlayer.Game.Frame;
        if (productionConditionScript != null)
        {
            if (frame < productionConditionScript.FrameToEvaluate)
                return false;
            int delay = productionConditionScript.DelayEvaluationSeconds;
            if (delay > 0)
                productionConditionScript.FrameToEvaluate = frame + delay * LogicTime.FramesPerSecond;
            return engine.EvaluateConditions(productionConditionScript, null, ControllingPlayer);
        }
        if (TemplateInfo.ProductionCondition.Length == 0 || engine.FindScript(TemplateInfo.ProductionCondition) is not { } script)
        {
            productionConditionAlwaysFalse = true;
            return false;
        }
        bool allowed = ControllingPlayer.Difficulty switch
        {
            Scripting.Difficulty.Easy => script.Easy,
            Scripting.Difficulty.Hard => script.Hard,
            _ => script.Normal,
        };
        if (!allowed)
        {
            productionConditionAlwaysFalse = true;
            return false;
        }
        productionConditionScript = script.Duplicate();
        return engine.EvaluateConditions(productionConditionScript, null, ControllingPlayer);
    }

    public override string ToString() => $"{Name} ({ControllingPlayer})";
}

/// <summary>
/// One team of objects (the original's Team): an instance of a <see cref="TeamPrototype"/>. Every
/// object is on a team; each player has a default team. Teams run their prototype's scripts when
/// created, idle, seeing or losing sight of enemies, losing units and being destroyed.
/// </summary>
public sealed class Team
{
    readonly List<GameObject> members = new();
    readonly Dictionary<int, Relationship> teamRelations = new();
    readonly Dictionary<int, Relationship> playerRelations = new();
    bool enteredOrExited, created, checkEnemySighted, seeEnemy, prevSeeEnemy, wasIdle;
    int destroyThreshold, curUnits;
    bool isRecruitabilitySet, isRecruitable;
    uint commonAttackTarget;
    readonly bool[] shouldAttemptGenericScript = new bool[TeamTemplateInfo.MaxGenericScripts];

    public int Id { get; internal set; }
    public TeamPrototype Prototype { get; }
    public string Name => Prototype.Name;
    public Player ControllingPlayer => Prototype.ControllingPlayer;
    /// <summary>The same as <see cref="ControllingPlayer"/>.</summary>
    public Player Owner => Prototype.ControllingPlayer;
    public bool IsDefault => ControllingPlayer.DefaultTeam == this;
    public bool IsSingleton => Prototype.IsSingleton;
    public Dictionary<string, object> Properties => Prototype.Properties;

    /// <summary>Script-visible state name (e.g. "Attacking").</summary>
    public string State { get; set; } = "";
    public bool IsActive { get; private set; }
    public bool IsCreated => created;
    public Waypoint? CurrentWaypoint { get; set; }

    internal Team(TeamPrototype prototype, int id)
    {
        Prototype = prototype;
        Id = id;
        prototype.instances.Insert(0, this);
        var info = prototype.TemplateInfo;
        if (info.ScriptOnAllClear.Length > 0 || info.ScriptOnEnemySighted.Length > 0)
            checkEnemySighted = true;
        Array.Fill(shouldAttemptGenericScript, true);
    }

    /// <summary>All members, including dead ones not yet removed (the original's team member list).</summary>
    public IReadOnlyList<GameObject> AllMembers => members;

    /// <summary>The living members.</summary>
    public IEnumerable<GameObject> Members => members.Where(o => !o.IsEffectivelyDead);

    public int MemberCount => members.Count;
    public bool HasAnyMembers => members.Any(o => !o.IsEffectivelyDead);

    internal void AddMember(GameObject obj)
    {
        if (!members.Contains(obj))
            members.Insert(0, obj);
    }

    internal void RemoveMember(GameObject obj) => members.Remove(obj);

    public void SetActive()
    {
        if (!IsActive)
        {
            created = true;
            IsActive = true;
        }
    }

    public void SetEnteredExited() => enteredOrExited = true;
    public bool DidEnterOrExit => enteredOrExited;

    public void SetRecruitable(bool recruitable)
    {
        isRecruitabilitySet = true;
        isRecruitable = recruitable;
    }

    public void SetControllingPlayer(Player newController)
    {
        Prototype.SetControllingPlayer(newController);
        foreach (var obj in members.ToList())
            obj.SetOwnerFromTeam();
    }

    public void SetAttackPriorityName(string name) => Prototype.AttackPriorityName = name;

    // ---- Relationships ----

    public Relationship RelationshipTo(Team? that)
    {
        if (that != null && teamRelations.Count > 0 && teamRelations.TryGetValue(that.Id, out var r))
            return r;
        if (that != null && playerRelations.Count > 0 && playerRelations.TryGetValue(that.ControllingPlayer.Index, out var pr))
            return pr;
        return ControllingPlayer.RelationshipTo(that);
    }

    public void SetOverrideTeamRelationship(int teamId, Relationship r)
    {
        if (teamId != 0)
            teamRelations[teamId] = r;
    }

    public bool RemoveOverrideTeamRelationship(int teamId)
    {
        if (teamRelations.Count == 0)
            return false;
        if (teamId == 0)
        {
            teamRelations.Clear();
            return true;
        }
        return teamRelations.Remove(teamId);
    }

    public void SetOverridePlayerRelationship(int playerIndex, Relationship r)
    {
        if (playerIndex >= 0)
            playerRelations[playerIndex] = r;
    }

    public bool RemoveOverridePlayerRelationship(int playerIndex)
    {
        if (playerRelations.Count == 0)
            return false;
        if (playerIndex < 0)
        {
            playerRelations.Clear();
            return true;
        }
        return playerRelations.Remove(playerIndex);
    }

    /// <summary>Kept for existing callers: a team-to-team override.</summary>
    public void SetRelationship(Team other, Relationship relation) => SetOverrideTeamRelationship(other.Id, relation);

    // ---- Common target (AttackCommonTarget teams) ----

    public void SetTeamTargetObject(GameObject? target)
    {
        if (target == null)
        {
            commonAttackTarget = 0;
            return;
        }
        if (!ControllingPlayer.IsHuman)
        {
            if (ControllingPlayer.Difficulty == Scripting.Difficulty.Easy)
                return;
            commonAttackTarget = target.Id;
        }
    }

    public GameObject? TeamTargetObject
    {
        get
        {
            if (commonAttackTarget == 0)
                return null;
            var target = ControllingPlayer.Game.FindObject(commonAttackTarget);
            if (target != null && target.HasStatus(Status.Stealthed) && !target.HasStatus(Status.Detected) && !target.HasStatus("DISGUISED"))
                target = null;
            if (target != null && (target.IsEffectivelyDead || target.IsContained || target.IsKindOf("AIRCRAFT")))
                target = null;
            if (target == null)
                commonAttackTarget = 0;
            return target;
        }
    }

    // ---- Counting ----

    public int TargetableCount => members.Count(o => !o.IsEffectivelyDead && (o.AI != null || o.IsKindOf(Kind.Structure)));

    public int CountBuildings() => members.Count(o => o.IsKindOf(Kind.Structure));

    public int CountObjectsByTemplate(Templates.ObjectTemplate template, bool ignoreDead = true, bool ignoreUnderConstruction = true) =>
        members.Count(o => o.Template.IsEquivalentTo(template) && !(ignoreDead && o.IsEffectivelyDead) && !(ignoreUnderConstruction && o.IsUnderConstruction));

    public bool HasAnyBuildings() => members.Any(o => !o.IsEffectivelyDead && !o.IsDestroyed && o.IsKindOf(Kind.Structure));

    public bool HasAnyBuildings(string kindOf) => members.Any(o => !o.IsEffectivelyDead && !o.IsDestroyed && o.IsKindOf(Kind.Structure) && o.IsKindOf(kindOf));

    public bool HasAnyUnits() => members.Any(o => !o.IsEffectivelyDead && !o.IsDestroyed
        && !o.IsKindOf(Kind.Structure) && !o.IsKindOf(Kind.Projectile) && !o.IsKindOf("MINE"));

    public bool HasAnyObjects() => members.Any(o => !o.IsEffectivelyDead && !o.IsDestroyed
        && !o.IsKindOf(Kind.Projectile) && !o.IsKindOf("INERT") && !o.IsKindOf("MINE"));

    public bool HasAnyBuildFacility() => members.Any(o => o.Game.Templates.IsBuildFacility(o.Template));

    public bool IsIdle => members.All(o => o.AI == null || o.IsEffectivelyDead || o.AI.IsIdle);

    /// <summary>Somewhere near the team (the first member's position, as the original).</summary>
    public Vector3? EstimateTeamPosition => members.Count > 0 ? members[0].Position : null;

    // ---- Script hooks ----

    /// <summary>Once per frame (the original's Team::updateState): created / enemy sighted / all clear / destroyed / idle scripts.</summary>
    public void UpdateState()
    {
        enteredOrExited = false;
        if (!IsActive)
            return;
        var info = Prototype.TemplateInfo;
        var engine = ControllingPlayer.Game.Scripts;
        if (created)
        {
            created = false;
            if (info.ScriptOnCreate.Length > 0)
                engine?.RunScript(info.ScriptOnCreate, this);
            if (info.ScriptOnDestroyed.Length > 0)
            {
                curUnits += members.Count;
                destroyThreshold = (int)(curUnits - curUnits * info.DestroyedThreshold);
                if (destroyThreshold > curUnits - 1) destroyThreshold = curUnits - 1;
                if (destroyThreshold < 0) destroyThreshold = 0;
            }
        }
        if (checkEnemySighted)
        {
            prevSeeEnemy = seeEnemy;
            seeEnemy = false;
            bool anyAlive = false;
            foreach (var obj in members)
            {
                if (obj.IsEffectivelyDead)
                    continue;
                anyAlive = true;
                float range = obj.VisionRange;
                if (obj.Game.Partition.Query(obj.Position, range).Any(o => !o.IsEffectivelyDead && o != obj && obj.IsEnemyOf(o)
                    && o.IsOffMap == obj.IsOffMap && MathUtil.Distance2D(o.Position, obj.Position) <= range))
                {
                    seeEnemy = true;
                    break;
                }
            }
            if (anyAlive && prevSeeEnemy != seeEnemy)
                engine?.RunScript(seeEnemy ? info.ScriptOnEnemySighted : info.ScriptOnAllClear, this);
        }
        if (info.ScriptOnDestroyed.Length > 0)
        {
            int prevUnits = curUnits;
            curUnits = members.Count(o => !o.IsEffectivelyDead);
            if (curUnits != prevUnits && curUnits <= destroyThreshold)
            {
                engine?.RunScript(info.ScriptOnDestroyed, this);
                destroyThreshold = -1;
            }
        }
        if (info.ScriptOnIdle.Length > 0)
        {
            bool idle = true, anyAlive = false;
            foreach (var obj in members)
            {
                if (obj.IsEffectivelyDead || obj.AI == null)
                    continue;
                anyAlive = true;
                if (!obj.AI.IsIdle)
                    idle = false;
            }
            if (anyAlive && idle && wasIdle)
                engine?.RunScript(info.ScriptOnIdle, this);
            wasIdle = idle;
        }
    }

    public void NotifyTeamOfObjectDeath()
    {
        if (Prototype.TemplateInfo.ScriptOnUnitDestroyed.Length > 0)
            ControllingPlayer.Game.Scripts?.RunScript(Prototype.TemplateInfo.ScriptOnUnitDestroyed, this);
    }

    /// <summary>Generic script hooks: each is tried every frame on the team until it runs (if one-shot).</summary>
    public void UpdateGenericScripts()
    {
        var engine = ControllingPlayer.Game.Scripts;
        if (engine == null)
            return;
        for (int i = 0; i < TeamTemplateInfo.MaxGenericScripts; i++)
        {
            if (!shouldAttemptGenericScript[i])
                continue;
            if (Prototype.GetGenericScript(i) is not { } script)
            {
                shouldAttemptGenericScript[i] = false;
                continue;
            }
            if (engine.EvaluateConditions(script, this))
            {
                if (script.IsOneShot)
                    shouldAttemptGenericScript[i] = false;
                engine.ExecuteActionsFor(script.Actions, this);
            }
        }
    }

    // ---- Trigger areas ----

    static bool SurfacesMatch(Locomotion.Surfaces surfaces, int whichToConsider)
    {
        // The script's surfaces bits: 1 = ground, 2 = air (the original remaps air to the locomotor's AIR bit).
        bool ground = (whichToConsider & 1) != 0, air = (whichToConsider & 2) != 0;
        return (ground && (surfaces & Locomotion.Surfaces.Ground) != 0) || (air && (surfaces & Locomotion.Surfaces.Air) != 0);
    }

    IEnumerable<GameObject> Considered(int whichToConsider) => members.Where(o =>
        SurfacesMatch(o.AI?.ValidSurfaces ?? Locomotion.Surfaces.Ground, whichToConsider) && !o.IsEffectivelyDead && !o.IsKindOf("INERT"));

    public bool DidAllEnter(TriggerArea area, int which)
    {
        if (!enteredOrExited)
            return false;
        bool entered = false, outside = false;
        foreach (var o in Considered(which))
        {
            if (o.DidEnter(area)) entered = true;
            else if (!o.IsInside(area)) outside = true;
        }
        return entered && !outside;
    }

    public bool DidPartialEnter(TriggerArea area, int which) => enteredOrExited && Considered(which).Any(o => o.DidEnter(area));

    public bool DidPartialExit(TriggerArea area, int which) => enteredOrExited && Considered(which).Any(o => o.DidExit(area));

    public bool DidAllExit(TriggerArea area, int which)
    {
        if (!enteredOrExited)
            return false;
        bool any = false, exited = false, inside = false;
        foreach (var o in Considered(which))
        {
            if (o.DidExit(area)) exited = true;
            else if (o.IsInside(area)) inside = true;
            any = true;
        }
        return any && exited && !inside;
    }

    public bool AllInside(TriggerArea area, int which)
    {
        if (!HasAnyObjects())
            return false;
        bool any = false, outside = false;
        foreach (var o in Considered(which))
        {
            if (!o.IsInside(area)) outside = true;
            any = true;
        }
        return any && !outside;
    }

    public bool NoneInside(TriggerArea area, int which)
    {
        bool any = false, inside = false;
        foreach (var o in Considered(which))
        {
            if (o.IsInside(area)) inside = true;
            any = true;
        }
        return any && !inside;
    }

    public bool SomeInsideSomeOutside(TriggerArea area, int which)
    {
        bool any = false, inside = false, outside = false;
        foreach (var o in Considered(which))
        {
            if (o.IsInside(area)) inside = true; else outside = true;
            any = true;
        }
        return any && inside && outside;
    }

    // ---- Orders ----

    /// <summary>The nearest recruitable unit of this type for this team (the original's tryToRecruit).</summary>
    public GameObject? TryToRecruit(Templates.ObjectTemplate template, Vector3 teamHome, float maxDistance)
    {
        var player = ControllingPlayer;
        float distSqr = maxDistance * maxDistance;
        GameObject? recruit = null;
        foreach (var obj in player.Game.Objects)
        {
            if (!obj.Template.IsEquivalentTo(template) && !template.BuildVariations.Contains(obj.Template.Name, StringComparer.OrdinalIgnoreCase))
                continue;
            if (obj.Owner != player || obj.Team is not { } team)
                continue;
            bool isDefault = team == player.DefaultTeam;
            if (!team.IsActive)
                continue;
            if (team.Prototype.TemplateInfo.ProductionPriority >= Prototype.TemplateInfo.ProductionPriority)
                continue;
            bool recruitable = isDefault || team.Prototype.TemplateInfo.IsAIRecruitable;
            if (team.isRecruitabilitySet)
                recruitable = team.isRecruitable;
            if (!recruitable)
                continue;
            if (obj.AI != null && !obj.AI.IsRecruitable)
                continue;
            if (obj.IsDisabledBy("DISABLED_HELD"))
                continue;
            float dx = teamHome.X - obj.Position.X, dy = teamHome.Y - obj.Position.Y;
            if (isDefault && recruit == null)
            {
                recruit = obj;
                distSqr = dx * dx + dy * dy;
            }
            if (dx * dx + dy * dy > distSqr)
                continue;
            distSqr = dx * dx + dy * dy;
            recruit = obj;
        }
        return recruit;
    }

    public void HealAllObjects()
    {
        foreach (var o in members.ToList())
            o.HealCompletely();
    }

    public void DamageTeamMembers(float amount)
    {
        foreach (var o in members.ToList())
        {
            if (o.IsEffectivelyDead || o.IsDestroyed)
                continue;
            if (amount < 0)
                o.Kill();
            else
                o.Damage(new Combat.DamageInfo { Amount = amount, DamageType = "UNRESISTABLE", DeathType = "NORMAL" });
        }
    }

    /// <summary>Deletes every member (default teams first empty their containers).</summary>
    public void DeleteTeam(bool ignoreDead = false)
    {
        if (IsDefault)
            foreach (var o in members.Where(o => o.Contain is { Count: > 0 }).ToList())
                o.Contain!.RemoveAllContained();
        foreach (var o in members.ToList())
        {
            if (ignoreDead && o.IsEffectivelyDead)
                continue;
            o.Game.DestroyObject(o);
        }
    }

    public void TransferUnitsTo(Team? newTeam)
    {
        if (newTeam == null || newTeam == this)
            return;
        while (members.Count > 0)
            members[0].SetTeam(newTeam);
    }

    public void EvacuateTeam()
    {
        foreach (var o in members.Where(o => !o.IsDestroyed && !o.IsEffectivelyDead && o.Contain is { Count: > 0 }).ToList())
            o.Contain!.RemoveAllContained();
    }

    public void KillTeam()
    {
        EvacuateTeam();
        string beacon = ControllingPlayer.Faction?.GetString("BeaconName") ?? "";
        foreach (var o in members.Where(o => !o.IsDestroyed && (!o.IsEffectivelyDead || o.Template.Name.Equals(beacon, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            if (o.IsKindOf("TECH_BUILDING"))
                o.SetTeam(o.Game.Players.Neutral.DefaultTeam);
            else
                o.Kill();
        }
    }

    public override string ToString() => $"{Name}#{Id} ({ControllingPlayer})";
}

/// <summary>
/// All team prototypes and their instances (the original's TeamFactory). Map and skirmish teams are
/// set up from the sides list; scripts and the AI create instances by name.
/// </summary>
public sealed class TeamFactory
{
    readonly GameLogic game;
    readonly Dictionary<string, TeamPrototype> prototypes = new(StringComparer.OrdinalIgnoreCase);
    int uniquePrototypeId, uniqueTeamId;

    public TeamFactory(GameLogic game) => this.game = game;

    public IEnumerable<TeamPrototype> Prototypes => prototypes.Values;

    public void Clear() => prototypes.Clear();

    public void InitFromSides(IEnumerable<Dictionary<string, object>> teams)
    {
        foreach (var d in teams)
        {
            string name = d.TryGetValue("teamName", out var n) ? n?.ToString() ?? "" : "";
            string owner = d.TryGetValue("teamOwner", out var o) ? o?.ToString() ?? "" : "";
            bool singleton = d.TryGetValue("teamIsSingleton", out var s) && s is true;
            if (name.Length > 0)
                InitTeam(name, owner, singleton, d);
        }
    }

    public void InitTeam(string name, string owner, bool isSingleton, Dictionary<string, object> d)
    {
        var player = game.Players.FindByNameKey(owner) ?? game.Players.Neutral;
        if (prototypes.TryGetValue(name, out var existing))
        {
            existing.Reload(d, player, isSingleton);
            if (isSingleton && existing.FirstInstance == null)
                CreateInactiveTeam(name);
            return;
        }
        var prototype = new TeamPrototype(this, name, player, isSingleton, d, ++uniquePrototypeId);
        prototypes[name] = prototype;
        if (isSingleton)
            CreateInactiveTeam(name);
    }

    public TeamPrototype? FindTeamPrototype(string name) => prototypes.GetValueOrDefault(name);

    public TeamPrototype? FindTeamPrototypeById(int id) => prototypes.Values.FirstOrDefault(p => p.Id == id);

    public Team? FindTeamById(int id)
    {
        if (id == 0)
            return null;
        foreach (var p in prototypes.Values)
            if (p.FindTeamById(id) is { } t)
                return t;
        return null;
    }

    public Team CreateInactiveTeam(string name)
    {
        var tp = FindTeamPrototype(name) ?? throw new ArgumentException($"No team '{name}'.");
        if (tp.IsSingleton && tp.FirstInstance is { } existing)
        {
            RunExecuteActions(tp);
            return existing;
        }
        var team = new Team(tp, ++uniqueTeamId);
        RunExecuteActions(tp);
        return team;
    }

    void RunExecuteActions(TeamPrototype tp)
    {
        if (tp.TemplateInfo.ExecuteActions && game.Scripts?.FindScript(tp.TemplateInfo.ProductionCondition) is { } script)
            game.Scripts.ExecuteActionsFor(script.Actions, null);
    }

    public Team CreateTeam(string name)
    {
        var team = CreateInactiveTeam(name);
        team.SetActive();
        return team;
    }

    public Team CreateTeamOnPrototype(TeamPrototype prototype)
    {
        if (prototype.IsSingleton && prototype.FirstInstance is { } existing)
            return existing;
        var team = new Team(prototype, ++uniqueTeamId);
        team.SetActive();
        return team;
    }

    /// <summary>The first instance of a team (creating an inactive one for a non-singleton that has none).</summary>
    public Team? FindTeam(string name)
    {
        if (FindTeamPrototype(name) is not { } tp)
            return null;
        var t = tp.FirstInstance;
        if (t == null && !tp.IsSingleton)
            t = CreateInactiveTeam(name);
        return t;
    }

    /// <summary>A brand-new prototype (a player's default team when no map defines one).</summary>
    public TeamPrototype AddPrototype(string name, Player owner, bool isSingleton, Dictionary<string, object>? d = null)
    {
        d ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["teamName"] = name, ["teamOwner"] = owner.Name, ["teamIsSingleton"] = isSingleton };
        var prototype = new TeamPrototype(this, name, owner, isSingleton, d, ++uniquePrototypeId);
        prototypes[name] = prototype;
        return prototype;
    }

    internal void DeleteTeam(Team team)
    {
        foreach (var p in prototypes.Values)
            p.TeamAboutToBeDeleted(team);
        game.Scripts?.NotifyOfTeamDestruction(team);
        foreach (var o in team.AllMembers.ToList())
            o.ClearTeam();
        team.Prototype.instances.Remove(team);
    }

    /// <summary>Every prototype's teams update their state and drop empty instances (the original's PlayerList::updateTeamStates).</summary>
    public void UpdateTeamStates()
    {
        foreach (var p in prototypes.Values.ToList())
            p.UpdateState();
    }
}

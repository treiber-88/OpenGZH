using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic.Scripting;

/// <summary>
/// Runs every side's scripts: the mission logic of the campaign maps and the skirmish AI's scripts
/// (the original's ScriptEngine). Each frame each side's active scripts are evaluated in order, as
/// that side's player; a script whose conditions are about a non-singleton team runs once per
/// instance of the team. Counters, flags and timers are shared by all scripts.
/// </summary>
public sealed partial class ScriptEngine
{
    public const string ThisTeam = "<This Team>";
    public const string AnyTeam = "<Any Team>";
    public const string ThisObject = "<This Object>";
    public const string ThisPlayer = "<This Player>";
    public const string LocalPlayer = "<Local Player>";
    public const string ThePlayer = "ThePlayer";
    public const string TeamThePlayer = "teamThePlayer";
    public const string ThisPlayerEnemy = "<This Player's Enemy>";
    public const string NoneString = "<none>";
    public const string MyInnerPerimeter = "[Skirmish]MyInnerPerimeter";
    public const string MyOuterPerimeter = "[Skirmish]MyOuterPerimeter";
    public const string EnemyOuterPerimeter = "[Skirmish]EnemyOuterPerimeter";
    public const string EnemyInnerPerimeter = "[Skirmish]EnemyInnerPerimeter";
    public const string InnerPerimeter = "InnerPerimeter";
    public const string OuterPerimeter = "OuterPerimeter";
    public const string SkirmishAreaHomeBase = "Home Base";
    public const string SkirmishAreaAnySupplyDepot = "Any Supply Depot";
    const int MaxSpinCount = 20;
    const int FramesToShowWinLoseMessage = 120;
    const int FramesToFadeInAtStart = 33;

    readonly GameLogic game;

    /// <summary>Each side's scripts, by player index (side 0 is the neutral player).</summary>
    readonly List<ScriptList?> sides = new();

    sealed class Counter
    {
        public int Value;
        public bool IsCountdownTimer;
    }

    readonly Dictionary<string, Counter> counters = new(StringComparer.Ordinal);
    readonly Dictionary<string, bool> flags = new(StringComparer.Ordinal);

    /// <summary>Script-named objects; a name stays with a null object once it died (didUnitExist).</summary>
    readonly List<(string Name, GameObject? Object)> namedObjects = new();

    readonly List<AttackPriorityInfo> attackPriorityInfo = new() { new AttackPriorityInfo("") };
    readonly List<ObjectTypes> objectTypeLists = new();
    readonly List<SequentialScript> sequentialScripts = new();
    readonly LinkedList<string> uiInteractions = new();

    Player? currentPlayer;
    Team? callingTeam, conditionTeam;
    GameObject? callingObject, conditionObject;
    bool firstUpdate = true;
    int endGameTimer = -1, closeWindowTimer = -1;

    /// <summary>The difficulty for the mission (scripts can be easy/normal/hard only).</summary>
    public Difficulty Difficulty { get; set; } = Difficulty.Normal;

    /// <summary>When a map has scripts that decide victory (campaign), the default "destroy everything" rule is off.</summary>
    public bool HandlesVictory { get; set; }

    /// <summary>The music track scripts last asked for (MUSIC_SET_TRACK).</summary>
    public string CurrentTrackName { get; set; } = "";

    /// <summary>Scripts stopped game time (FREEZE_TIME).</summary>
    public bool IsTimeFrozenByScript { get; set; }

    public ScriptEngine(GameLogic game) => this.game = game;

    public GameLogic Game => game;

    public int ScriptCount => sides.Sum(s => s?.AllScripts.Count() ?? 0);

    public IReadOnlyList<ScriptList?> Sides => sides;

    // ---- Setup ----

    /// <summary>Gives a side (player index) its scripts.</summary>
    public void SetSideScripts(int playerIndex, ScriptList? scripts)
    {
        while (sides.Count <= playerIndex)
            sides.Add(null);
        sides[playerIndex] = scripts;
    }

    /// <summary>Loads every side's scripts from the map, by matching player names.</summary>
    public void Load(Formats.Maps.SidesList mapSides)
    {
        foreach (var side in mapSides.Sides)
        {
            var owner = game.Players.FindByNameKey(side.Name) ?? game.Players.Neutral;
            if (side.Scripts.Scripts.Count > 0 || side.Scripts.Groups.Count > 0)
                SetSideScripts(owner.Index, ScriptList.From(side.Scripts));
        }
        NewMap();
    }

    /// <summary>The original's newMap: script evaluation delays and the team each script is about.</summary>
    public void NewMap()
    {
        foreach (var list in sides)
            if (list != null)
                foreach (var script in list.AllScripts)
                    CheckConditionsForTeamNames(script);
        HandlesVictory = sides.Any(l => l != null && l.AllScripts.Any(s => s.Actions.Concat(s.FalseActions).Any(a => a.Name is "VICTORY" or "DEFEAT" or "QUICKVICTORY")));
        firstUpdate = true;
        Fade = FadeType.Multiply;
        CurrentFadeFrame = 0;
        MinFade = 1;
        MaxFade = 0;
        fadeFramesIncrease = 0;
        fadeFramesHold = 0;
        fadeFramesDecrease = FramesToFadeInAtStart;
        CurrentFadeValue = 0;
    }

    void CheckConditionsForTeamNames(Script script)
    {
        script.FrameToEvaluate = script.DelayEvaluationSeconds > 0 ? game.Random.Int(0, 2 * LogicTime.FramesPerSecond) : 0;
        string singleton = "", multi = "";
        foreach (var and in script.OrConditions)
            foreach (var condition in and)
                foreach (var p in condition.Parameters)
                {
                    if (p.Type != ParamType.Team || game.Teams.FindTeamPrototype(p.String) is not { } proto)
                        continue;
                    bool isSingleton = proto.IsSingleton || proto.TemplateInfo.MaxInstances < 2;
                    if (isSingleton)
                        singleton = p.String;
                    else if (multi.Length == 0)
                        multi = p.String;
                }
        if (multi.Length == 0)
        {
            if (singleton.Length > 0)
                script.ConditionTeamName = singleton;
        }
        else
            script.ConditionTeamName = multi;
    }

    // ---- The frame ----

    public void Update()
    {
        if (firstUpdate)
        {
            CreateNamedCache();
            firstUpdate = false;
        }
        if (closeWindowTimer > 0 && --closeWindowTimer < 1)
        {
            game.Events.ScriptClientAction(new ScriptAction { Name = "CLOSE_WINDOWS" }); // victory / defeat windows
            OnCloseWindows?.Invoke();
        }
        if (endGameTimer > 0 && --endGameTimer < 1)
        {
            // The original sends MSG_CLEAR_GAME_DATA here: the game is over and the shell takes over.
            game.IsGameOver = true;
            OnEndGameTimerExpired?.Invoke();
        }
        if (Fade != FadeType.None)
            UpdateFades();
        if (endGameTimer >= 0)
            return;

        Actions.Update();
        Conditions.Update();

        foreach (var counter in counters.Values)
            if (counter.IsCountdownTimer && counter.Value >= 0)
                counter.Value--;

        for (int i = 0; i < sides.Count; i++)
        {
            if (sides[i] is not { } list || i >= game.Players.All.Count)
                continue;
            currentPlayer = game.Players.All[i];
            ExecuteScripts(list.Scripts);
            foreach (var group in list.Groups)
                if (group.IsActive && !group.IsSubroutine)
                    ExecuteScripts(group.Scripts);
            currentPlayer = null;
        }

        game.Teams.UpdateTeamStates();
        uiInteractions.Clear();
        EvaluateAndProgressAllSequentialScripts();
    }

    void ExecuteScripts(List<Script> scripts)
    {
        foreach (var script in scripts.ToList())
            if (!script.IsSubroutine)
                ExecuteScript(script);
    }

    /// <summary>The original's executeScript: once, or once per instance of the team its conditions are about.</summary>
    void ExecuteScript(Script script)
    {
        if (!script.IsActive)
            return;
        var difficulty = currentPlayer?.Difficulty ?? Difficulty;
        if (!script.AllowedOn(difficulty))
            return;
        if (game.Frame < script.FrameToEvaluate)
            return;
        if (script.DelayEvaluationSeconds > 0)
            script.FrameToEvaluate = game.Frame + script.DelayEvaluationSeconds * LogicTime.FramesPerSecond;

        var savedConditionTeam = conditionTeam;
        var proto = script.ConditionTeamName.Length > 0 ? game.Teams.FindTeamPrototype(script.ConditionTeamName) : null;
        if (proto != null && proto.CountTeamInstances() > 0)
        {
            foreach (var team in proto.Instances.ToList())
            {
                conditionTeam = team;
                if (EvaluateConditions(script))
                {
                    ExecuteActions(script.Actions);
                    if (script.IsOneShot)
                        script.IsActive = false;
                }
                else if (script.FalseActions.Count > 0)
                    ExecuteActions(script.FalseActions);
            }
        }
        else
        {
            conditionTeam = null;
            if (EvaluateConditions(script))
            {
                ExecuteActions(script.Actions);
                if (script.IsOneShot)
                    script.IsActive = false;
            }
            else if (script.FalseActions.Count > 0)
            {
                ExecuteActions(script.FalseActions);
                if (script.IsOneShot)
                    script.IsActive = false;
            }
        }
        conditionTeam = savedConditionTeam;
    }

    /// <summary>Are the script's conditions true (as <paramref name="thisTeam"/> / <paramref name="player"/>, if given)?</summary>
    public bool EvaluateConditions(Script script, Team? thisTeam = null, Player? player = null)
    {
        var savedCallingTeam = callingTeam;
        var savedPlayer = currentPlayer;
        callingTeam = thisTeam;
        if (thisTeam != null)
            player = thisTeam.ControllingPlayer;
        currentPlayer = player ?? currentPlayer;
        try
        {
            foreach (var and in script.OrConditions)
            {
                if (and.Count == 0)
                    continue;
                bool all = true;
                foreach (var condition in and)
                    if (!EvaluateCondition(condition))
                    {
                        all = false;
                        break;
                    }
                if (all)
                    return true;
            }
            return false;
        }
        finally
        {
            callingTeam = savedCallingTeam;
            currentPlayer = savedPlayer;
        }
    }

    bool EvaluateCondition(ScriptCondition c) => c.Name switch
    {
        "CONDITION_FALSE" => false,
        "CONDITION_TRUE" => true,
        "COUNTER" => EvaluateCounter(c),
        "FLAG" => EvaluateFlag(c),
        "TIMER_EXPIRED" => EvaluateTimer(c),
        _ => Conditions.Evaluate(c),
    };

    /// <summary>Debugging: every script whose actions run is logged (with <see cref="ShowDebugMessages"/>).</summary>
    public static bool TraceScripts { get; set; }

    public void ExecuteActions(IEnumerable<ScriptAction> actions)
    {
        foreach (var action in actions)
        {
            if (TraceScripts)
                Log.Info($"[action] {game.Frame} {currentPlayer?.ScriptName}: {action}");
            switch (action.Name)
            {
                case "SET_COUNTER": GetCounter(action[0].String).Value = action[1].Int; break;
                case "INCREMENT_COUNTER": GetCounter(action[1].String).Value += action[0].Int; break;
                case "DECREMENT_COUNTER": GetCounter(action[1].String).Value -= action[0].Int; break;
                case "SET_FLAG": flags[action[0].String] = action[1].Int != 0; break;
                case "STOP_TIMER": GetCounter(action[0].String).IsCountdownTimer = false; break;
                case "RESTART_TIMER":
                {
                    var counter = GetCounter(action[0].String);
                    if (counter.Value > 0)
                        counter.IsCountdownTimer = true;
                    break;
                }
                case "SET_TIMER": SetTimer(action, false, false); break;
                case "SET_MILLISECOND_TIMER": SetTimer(action, true, false); break;
                case "SET_RANDOM_TIMER": SetTimer(action, false, true); break;
                case "SET_RANDOM_MSEC_TIMER": SetTimer(action, true, true); break;
                case "ADD_TO_MSEC_TIMER": AdjustTimer(action, true, true); break;
                case "SUB_FROM_MSEC_TIMER": AdjustTimer(action, true, false); break;
                case "ENABLE_SCRIPT": SetScriptEnabled(action[0].String, true); break;
                case "DISABLE_SCRIPT": SetScriptEnabled(action[0].String, false); break;
                case "CALL_SUBROUTINE": CallSubroutine(action[0].String); break;
                case "CAMERA_FADE_ADD": SetFade(action, FadeType.Add); break;
                case "CAMERA_FADE_SUBTRACT": SetFade(action, FadeType.Subtract); break;
                case "CAMERA_FADE_SATURATE": SetFade(action, FadeType.Saturate); break;
                case "CAMERA_FADE_MULTIPLY": SetFade(action, FadeType.Multiply); break;
                case "SET_ATTACK_PRIORITY_THING": SetPriorityThing(action); break;
                case "SET_ATTACK_PRIORITY_KIND_OF": SetPriorityKind(action); break;
                case "SET_DEFAULT_ATTACK_PRIORITY": FindAttackInfo(action[0].String, true)!.DefaultPriority = action[1].Int; break;
                case "NO_OP": break;
                default: Actions.Execute(action); break;
            }
        }
    }

    /// <summary>Runs actions as a team's (the original's friend_executeAction).</summary>
    public void ExecuteActionsFor(IEnumerable<ScriptAction> actions, Team? team)
    {
        var savedTeam = callingTeam;
        var savedPlayer = currentPlayer;
        callingTeam = team;
        currentPlayer = team?.ControllingPlayer;
        try { ExecuteActions(actions); }
        finally
        {
            callingTeam = savedTeam;
            currentPlayer = savedPlayer;
        }
    }

    // ---- Finding scripts ----

    public ScriptGroup? FindGroup(string name)
    {
        foreach (var list in sides)
            if (list != null)
                foreach (var g in list.Groups)
                    if (g.Name == name)
                        return g;
        return null;
    }

    public Script? FindScript(string name)
    {
        foreach (var list in sides)
        {
            if (list == null)
                continue;
            foreach (var s in list.Scripts)
                if (s.Name == name)
                    return s;
            foreach (var g in list.Groups)
                foreach (var s in g.Scripts)
                    if (s.Name == name)
                        return s;
        }
        return null;
    }

    void SetScriptEnabled(string name, bool enabled)
    {
        if (FindGroup(name) is { } group)
            group.IsActive = enabled;
        if (FindScript(name) is { } script)
            script.IsActive = enabled;
    }

    public void SetScriptActive(string name, bool active) => SetScriptEnabled(name, active);
    public void SetGroupActive(string name, bool active)
    {
        if (FindGroup(name) is { } group)
            group.IsActive = active;
    }

    /// <summary>CALL_SUBROUTINE: a subroutine group (if active) or script runs now.</summary>
    public void CallSubroutine(string name)
    {
        if (FindGroup(name) is { } group)
        {
            if (group.IsSubroutine && group.IsActive)
                foreach (var s in group.Scripts.ToList())
                    ExecuteScript(s);
            return;
        }
        if (FindScript(name) is { IsSubroutine: true } script)
            ExecuteScript(script);
    }

    /// <summary>Runs a subroutine script for a team (team created / idle / ... hooks).</summary>
    public void RunScript(string name, Team? thisTeam)
    {
        if (string.IsNullOrEmpty(name) || name == NoneString)
            return;
        var savedPlayer = currentPlayer;
        var savedCalling = callingTeam;
        callingTeam = thisTeam;
        conditionTeam = null;
        currentPlayer = thisTeam?.ControllingPlayer;
        try { CallSubroutine(name); }
        finally
        {
            callingTeam = savedCalling;
            currentPlayer = savedPlayer;
        }
    }

    /// <summary>Runs a subroutine script for an object (an object's script hooks).</summary>
    public void RunObjectScript(string name, GameObject obj)
    {
        if (string.IsNullOrEmpty(name) || name == NoneString)
            return;
        var saved = callingObject;
        callingObject = obj;
        try { CallSubroutine(name); }
        finally { callingObject = saved; }
    }

    // ---- Context ----

    public Player? CurrentPlayer => currentPlayer;
    public Team? CallingTeam => callingTeam;
    public Team? ConditionTeam => conditionTeam;
    public GameObject? CallingObject => callingObject;

    /// <summary>The current player's enemy for skirmish scripts (its chosen enemy, or the first human).</summary>
    public Player? GetSkirmishEnemyPlayer()
    {
        if (currentPlayer == null)
            return null;
        if (currentPlayer.CurrentEnemy is { } enemy)
            return enemy;
        return game.Players.All.FirstOrDefault(p => p.IsHuman && !p.IsNeutral);
    }

    /// <summary>A player by script name: special names first, then the side name.</summary>
    public Player? GetPlayer(string name)
    {
        if (name == LocalPlayer)
            return game.Players.Local;
        if (name == ThisPlayer)
            return currentPlayer;
        if (name == ThisPlayerEnemy)
            return GetSkirmishEnemyPlayer();
        return game.Players.FindByNameKey(name);
    }

    public Team? GetTeamNamed(string name)
    {
        if (name == ThisTeam)
            return callingTeam ?? conditionTeam;
        if (callingTeam != null && callingTeam.Name == name)
            return callingTeam;
        if (conditionTeam != null && conditionTeam.Name == name)
            return conditionTeam;
        if (game.Teams.FindTeamPrototype(name) is not { } proto)
            return null;
        if (proto.IsSingleton)
            return proto.FirstInstance is { IsActive: true } t ? t : null;
        return proto.FirstInstance;
    }

    public GameObject? GetUnitNamed(string name)
    {
        if (name == ThisObject)
            return callingObject ?? conditionObject;
        foreach (var (n, obj) in namedObjects)
            if (n == name)
                return obj;
        return null;
    }

    public bool DidUnitExist(string name)
    {
        foreach (var (n, obj) in namedObjects)
            if (n == name)
                return obj == null;
        return false;
    }

    /// <summary>A trigger area by name; "[Skirmish]MyInnerPerimeter" and friends become the start position's perimeter.</summary>
    public TriggerArea? GetQualifiedTriggerArea(string name)
    {
        if (name is MyInnerPerimeter or MyOuterPerimeter)
        {
            if (currentPlayer == null)
                return null;
            int index = currentPlayer.MpStartIndex + 1;
            name = (name == MyInnerPerimeter ? InnerPerimeter : OuterPerimeter) + index;
        }
        else if (name is EnemyInnerPerimeter or EnemyOuterPerimeter)
        {
            int index = -1;
            if (currentPlayer?.CurrentEnemy is { } enemy)
                index = enemy.MpStartIndex + 1;
            name = (name == EnemyInnerPerimeter ? InnerPerimeter : OuterPerimeter) + index;
        }
        return game.Terrain.FindTriggerArea(name);
    }

    public ObjectTypes? GetObjectTypes(string listName) => objectTypeLists.FirstOrDefault(l => l.ListName == listName);

    /// <summary>OBJECTLIST_ADDOBJECTTYPE / OBJECTLIST_REMOVEOBJECTTYPE.</summary>
    public void DoObjectTypeListMaintenance(string listName, string type, bool add)
    {
        var list = GetObjectTypes(listName);
        if (list == null)
        {
            list = new ObjectTypes(listName);
            objectTypeLists.Add(list);
        }
        if (add)
            list.Add(type);
        else
            list.Remove(type);
        if (list.Types.Count == 0)
            objectTypeLists.Remove(list);
    }

    /// <summary>An object type parameter: one template name, or a script-made list of them.</summary>
    public IEnumerable<string> ObjectTypeNames(string typeOrList) =>
        GetObjectTypes(typeOrList) is { } list ? list.Types : [typeOrList];

    // ---- Counters, flags, timers ----

    Counter GetCounter(string name)
    {
        if (!counters.TryGetValue(name, out var counter))
            counters[name] = counter = new Counter();
        return counter;
    }

    public int CounterValue(string name) => counters.TryGetValue(name, out var c) ? c.Value : 0;
    public bool FlagValue(string name) => flags.TryGetValue(name, out var f) && f;
    public void SetFlagValue(string name, bool value) => flags[name] = value;
    public void SetCounterValue(string name, int value) => GetCounter(name).Value = value;

    /// <summary>The original's clearFlag: clears "name0" .. "name15" (per-player flags).</summary>
    public void ClearFlag(string name)
    {
        for (int j = 0; j < 16; j++)
            if (flags.ContainsKey(name + j))
                flags[name + j] = false;
    }

    bool EvaluateCounter(ScriptCondition c) => Compare(CounterValue(c[0].String), (Comparison)c[1].Int, c[2].Int);

    bool EvaluateFlag(ScriptCondition c)
    {
        if (FlagValue(c[0].String) == (c[1].Int != 0))
            return true;
        return uiInteractions.Contains(c[0].String);
    }

    bool EvaluateTimer(ScriptCondition c) =>
        counters.TryGetValue(c[0].String, out var counter) && counter.IsCountdownTimer && counter.Value < 1;

    void SetTimer(ScriptAction a, bool milliseconds, bool random)
    {
        var counter = GetCounter(a[0].String);
        if (milliseconds)
        {
            float value = a[1].Real;
            if (random)
                value = game.Random.Float(value, a[2].Real);
            counter.Value = (int)MathF.Ceiling(value * 1000 * LogicTime.FramesPerSecond / 1000f);
        }
        else
        {
            int value = a[1].Int;
            if (random)
                value = game.Random.Int(value, a[2].Int);
            counter.Value = value;
        }
        counter.IsCountdownTimer = true;
    }

    void AdjustTimer(ScriptAction a, bool milliseconds, bool add)
    {
        var counter = GetCounter(a[1].String);
        if (milliseconds)
        {
            float value = add ? a[0].Real : -a[0].Real;
            counter.Value += (int)MathF.Ceiling(value * 1000 * LogicTime.FramesPerSecond / 1000f);
        }
        else
            counter.Value += add ? a[0].Int : -a[0].Int;
    }

    /// <summary>Frames left on a countdown timer (for the on-screen timer).</summary>
    public int TimerRemaining(string name) => counters.TryGetValue(name, out var c) && c.IsCountdownTimer ? Math.Max(0, c.Value) : 0;

    public static bool Compare(int value, Comparison comparison, int against) => comparison switch
    {
        Comparison.LessThan => value < against,
        Comparison.LessEqual => value <= against,
        Comparison.Equal => value == against,
        Comparison.GreaterEqual => value >= against,
        Comparison.Greater => value > against,
        Comparison.NotEqual => value != against,
        _ => false,
    };

    public static bool Compare(float value, Comparison comparison, float against) => comparison switch
    {
        Comparison.LessThan => value < against,
        Comparison.LessEqual => value <= against,
        Comparison.Equal => value == against,
        Comparison.GreaterEqual => value >= against,
        Comparison.Greater => value > against,
        Comparison.NotEqual => value != against,
        _ => false,
    };

    /// <summary>A UI hook fired (a button the mission waits for); matching FLAG conditions are true this frame.</summary>
    public void SignalUIInteract(string hook) => uiInteractions.AddFirst(hook);

    // ---- Attack priorities ----

    public AttackPriorityInfo? FindAttackInfo(string name, bool addIfNotFound)
    {
        for (int i = 1; i < attackPriorityInfo.Count; i++)
            if (attackPriorityInfo[i].Name == name)
                return attackPriorityInfo[i];
        if (!addIfNotFound)
            return null;
        var info = new AttackPriorityInfo(name);
        attackPriorityInfo.Add(info);
        return info;
    }

    public AttackPriorityInfo DefaultAttackInfo => attackPriorityInfo[0];

    public AttackPriorityInfo GetAttackInfo(string name) => FindAttackInfo(name, false) ?? attackPriorityInfo[0];

    void SetPriorityThing(ScriptAction a)
    {
        var info = FindAttackInfo(a[0].String, true)!;
        foreach (string typeName in ObjectTypeNames(a[1].String))
        {
            if (game.Templates.Find(typeName) is not { } template)
                return;
            info.SetPriority(template, a[2].Int);
        }
    }

    void SetPriorityKind(ScriptAction a)
    {
        var info = FindAttackInfo(a[0].String, true)!;
        int kind = a[1].Int;
        foreach (var name in game.Templates.ObjectNames)
            if (game.Templates.Find(name) is { } template && kind >= 0 && kind < Core.Flags.KindOf.Count && template.KindOf.Contains(Core.Flags.KindOf[kind]))
                info.SetPriority(template, a[2].Int);
    }

    // ---- Named objects ----

    void CreateNamedCache()
    {
        namedObjects.Clear();
        foreach (var obj in game.Objects)
            if (obj.Name.Length > 0)
                namedObjects.Add((obj.Name, obj));
    }

    /// <summary>An object was given a script name (map objects, NAMED_ actions that name created units).</summary>
    public void AddObjectToCache(GameObject obj)
    {
        if (obj.Name.Length == 0)
            return;
        for (int i = 0; i < namedObjects.Count; i++)
        {
            if (namedObjects[i].Name == obj.Name)
            {
                if (namedObjects[i].Object == null)
                    namedObjects[i] = (obj.Name, obj);
                return;
            }
            if (namedObjects[i].Object == obj)
            {
                namedObjects[i] = (obj.Name, obj);
                return;
            }
        }
        namedObjects.Add((obj.Name, obj));
    }

    void RemoveObjectFromCache(GameObject obj)
    {
        for (int i = 0; i < namedObjects.Count; i++)
            if (namedObjects[i].Object == obj)
            {
                namedObjects[i] = (namedObjects[i].Name, null);
                break;
            }
    }

    /// <summary>A script-named object became another (a pilot climbing into a vehicle): the name moves with it.</summary>
    public void TransferObjectName(GameObject from, GameObject to)
    {
        string name = from.Name;
        if (name.Length == 0)
            return;
        if (to.Name.Length > 0)
            RemoveObjectFromCache(to);
        game.SetObjectName(from, "");
        game.SetObjectName(to, name);
        for (int i = 0; i < namedObjects.Count; i++)
            if (namedObjects[i].Name == name)
            {
                namedObjects[i] = (name, to);
                return;
            }
        namedObjects.Add((name, to));
    }

    /// <summary>A script made a new object under a name an old (dead) one had: the name now means the new one.</summary>
    public void ReassignName(string name, GameObject obj)
    {
        for (int i = 0; i < namedObjects.Count; i++)
            if (namedObjects[i].Name == name)
            {
                namedObjects[i] = (name, obj);
                return;
            }
        namedObjects.Add((name, obj));
    }

    /// <summary>An object was destroyed: its name now refers to nothing (but "did exist").</summary>
    public void NotifyOfObjectDestruction(GameObject obj)
    {
        if (obj.Name.Length > 0)
            RemoveObjectFromCache(obj);
        if (conditionObject == obj)
            conditionObject = null;
        if (callingObject == obj)
            callingObject = null;
        RemoveAllSequentialScripts(obj);
    }

    /// <summary>Kept for existing callers (an object died; destruction cleans up later).</summary>
    public void OnObjectDied(GameObject obj) { }

    public bool WasNamedObjectDestroyed(string name) => DidUnitExist(name);

    // ---- Sequential scripts ----

    public void AppendSequentialScript(SequentialScript toSequence)
    {
        var copy = toSequence.Copy();
        copy.Next = null;
        copy.CurrentInstruction = -1;
        foreach (var seq in sequentialScripts)
        {
            if ((toSequence.ObjectId != 0 && toSequence.ObjectId == seq.ObjectId) || (toSequence.Team != null && toSequence.Team == seq.Team))
            {
                var last = seq;
                while (last.Next != null)
                    last = last.Next;
                last.Next = copy;
                return;
            }
        }
        sequentialScripts.Add(copy);
    }

    public void RemoveAllSequentialScripts(GameObject obj)
    {
        for (int i = 0; i < sequentialScripts.Count; i++)
            if (sequentialScripts[i].ObjectId == obj.Id)
            {
                sequentialScripts.RemoveAt(i);
                i--;
            }
    }

    public void RemoveAllSequentialScripts(Team team) => NotifyOfTeamDestruction(team);

    public void NotifyOfTeamDestruction(Team team)
    {
        sequentialScripts.RemoveAll(s => s.Team == team);
        if (callingTeam == team)
            callingTeam = null;
        if (conditionTeam == team)
            conditionTeam = null;
    }

    public void SetSequentialTimer(GameObject obj, int frames)
    {
        if (sequentialScripts.FirstOrDefault(s => s.ObjectId == obj.Id) is { } seq)
            seq.FramesToWait = frames;
    }

    public void SetSequentialTimer(Team team, int frames)
    {
        if (sequentialScripts.FirstOrDefault(s => s.Team == team) is { } seq)
            seq.FramesToWait = frames;
    }

    /// <summary>Removes the current entry, moving on to the next script queued for the same unit or team.</summary>
    int CleanupSequentialScript(int index, bool cleanDanglers)
    {
        var seq = sequentialScripts[index];
        if (cleanDanglers || seq.Next == null)
        {
            sequentialScripts.RemoveAt(index);
            return index;
        }
        sequentialScripts[index] = seq.Next;
        return index;
    }

    static bool IsGroupIdle(Team team) => team.Members.All(o => o.AI == null || o.AI.IsIdle);

    void EvaluateAndProgressAllSequentialScripts()
    {
        int i = 0, lastIndex = -1, spinCount = 0;
        while (i < sequentialScripts.Count)
        {
            if (i == lastIndex) spinCount++; else spinCount = 0;
            if (spinCount > MaxSpinCount)
            {
                i++;
                continue;
            }
            lastIndex = i;
            var seq = sequentialScripts[i];
            var team = seq.Team;
            var obj = seq.ObjectId != 0 ? game.FindObject(seq.ObjectId) : null;
            if (obj == null && team == null)
            {
                i = CleanupSequentialScript(i, false);
                continue;
            }
            currentPlayer = obj?.Owner ?? team?.ControllingPlayer;
            if (currentPlayer != null && !currentPlayer.IsSkirmishAIPlayer)
                currentPlayer = null;
            var ai = obj?.AI;
            bool advanced = false;
            if (ai != null || team != null)
            {
                bool idle = (ai != null && ai.IsIdle) || (team != null && IsGroupIdle(team));
                if ((idle && seq.FramesToWait < 1) || seq.FramesToWait == 0)
                {
                    if (seq.DontAdvanceInstruction)
                        seq.DontAdvanceInstruction = false;
                    else
                        seq.CurrentInstruction++;
                    if (seq.CurrentInstruction < seq.Script.Actions.Count)
                    {
                        var action = seq.Script.Actions[seq.CurrentInstruction];
                        conditionTeam = team;
                        conditionObject = obj;
                        seq.FramesToWait = -1;
                        switch (action.Name)
                        {
                            case "SKIRMISH_WAIT_FOR_COMMANDBUTTON_AVAILABLE_ALL":
                                if (!Conditions.EvaluateSkirmishCommandButtonIsReady(null, action[1], action[2], true))
                                    seq.DontAdvanceInstruction = true;
                                break;
                            case "SKIRMISH_WAIT_FOR_COMMANDBUTTON_AVAILABLE_PARTIAL":
                                if (!Conditions.EvaluateSkirmishCommandButtonIsReady(null, action[1], action[2], false))
                                    seq.DontAdvanceInstruction = true;
                                break;
                            case "TEAM_WAIT_FOR_NOT_CONTAINED_ALL":
                                if (Conditions.EvaluateTeamIsContained(action[0], true))
                                    seq.DontAdvanceInstruction = true;
                                break;
                            case "TEAM_WAIT_FOR_NOT_CONTAINED_PARTIAL":
                                if (Conditions.EvaluateTeamIsContained(action[0], false))
                                    seq.DontAdvanceInstruction = true;
                                break;
                            default:
                                ExecuteActions([action]);
                                break;
                        }
                        if (seq.DontAdvanceInstruction)
                        {
                            i++;
                            continue;
                        }
                        if ((ai != null && ai.IsIdle) || (team != null && IsGroupIdle(team)))
                            advanced = true;
                        if (advanced)
                        {
                            if (obj != null && obj.IsEffectivelyDead)
                            {
                                i = CleanupSequentialScript(i, true);
                                continue;
                            }
                            if (team != null && !team.HasAnyMembers)
                            {
                                i = CleanupSequentialScript(i, true);
                                continue;
                            }
                        }
                    }
                    else
                    {
                        if (seq.TimesToLoop != 0)
                        {
                            if (seq.TimesToLoop != -1)
                                seq.TimesToLoop--;
                            seq.FramesToWait = -1;
                            AppendSequentialScript(seq);
                        }
                        i = CleanupSequentialScript(i, false);
                        continue;
                    }
                }
                else if (seq.FramesToWait > 0)
                    seq.FramesToWait--;
            }
            if (!advanced)
                i++;
        }
        currentPlayer = null;
        conditionTeam = null;
        conditionObject = null;
    }

    // ---- Fades and the end of the game ----

    public enum FadeType { None, Subtract, Add, Saturate, Multiply }

    public FadeType Fade { get; private set; } = FadeType.None;
    public int CurrentFadeFrame { get; private set; }
    public float MinFade { get; private set; } = 1;
    public float MaxFade { get; private set; }
    public float CurrentFadeValue { get; private set; }
    int fadeFramesIncrease, fadeFramesHold, fadeFramesDecrease;

    void SetFade(ScriptAction a, FadeType type)
    {
        Fade = type;
        CurrentFadeFrame = 0;
        MinFade = a[0].Real;
        MaxFade = a[1].Real;
        fadeFramesIncrease = a[2].Int;
        fadeFramesHold = a[3].Int;
        fadeFramesDecrease = a[4].Int;
        CurrentFadeValue = MinFade;
        if (fadeFramesIncrease == 0)
            UpdateFades();
    }

    void UpdateFades()
    {
        CurrentFadeFrame++;
        int fade = CurrentFadeFrame;
        if (fade <= fadeFramesIncrease)
        {
            CurrentFadeValue = MinFade + (float)CurrentFadeFrame / Math.Max(1, fadeFramesIncrease) * (MaxFade - MinFade);
            return;
        }
        fade -= fadeFramesIncrease;
        if (fade <= fadeFramesHold)
        {
            CurrentFadeValue = MaxFade;
            return;
        }
        fade -= fadeFramesHold;
        if (fade <= fadeFramesDecrease)
        {
            CurrentFadeValue = MaxFade + (float)fade / (fadeFramesDecrease + 1) * (MinFade - MaxFade);
            return;
        }
        Fade = FadeType.None;
    }

    /// <summary>The win/lose message shows for a while, then the game ends (MSG_CLEAR_GAME_DATA).</summary>
    public event Action? OnEndGameTimerExpired;
    public event Action? OnCloseWindows;

    public void StartQuickEndGameTimer() => endGameTimer = 1;
    public void StartEndGameTimer() => endGameTimer = FramesToShowWinLoseMessage;
    public void StartCloseWindowTimer() => closeWindowTimer = FramesToShowWinLoseMessage;

    ScriptConditions? conditions;
    ScriptActions? actions;
    internal ScriptConditions Conditions => conditions ??= new ScriptConditions(game, this);
    internal ScriptActions Actions => actions ??= new ScriptActions(game, this);
}

public enum Difficulty { Easy, Normal, Hard }

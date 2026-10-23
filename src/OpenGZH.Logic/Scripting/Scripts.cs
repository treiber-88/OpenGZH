using System.Numerics;
using Maps = OpenGZH.Formats.Maps;

namespace OpenGZH.Logic.Scripting;

/// <summary>The original's script parameter types (Scripts.h ParameterType).</summary>
public enum ParamType
{
    Int = 0, Real, Script, Team, Counter, Flag, Comparison, Waypoint, Boolean, TriggerArea, TextString, Side, Sound,
    ScriptSubroutine, Unit, ObjectType, Coord3D, Angle, TeamState, Relation, AiMood, Dialog, Music, Movie, WaypointPath,
    LocalizedText, Bridge, KindOf, AttackPrioritySet, RadarEventType, SpecialPower, Science, Upgrade, CommandButtonAbility,
    Boundary, Buildable, SurfacesAllowed, ShakeIntensity, CommandButton, FontName, ObjectStatus, CommandButtonAllAbilities,
    SkirmishWaypointPath, Color, Emoticon, ObjectPanelFlag, FactionName, ObjectTypeList, RevealName, ScienceAvailability,
    LeftOrRight, Percent,
}

/// <summary>Comparison codes in COMPARISON parameters.</summary>
public enum Comparison { LessThan = 0, LessEqual, Equal, GreaterEqual, Greater, NotEqual }

/// <summary>One script parameter (the original's Parameter): which field matters depends on <see cref="Type"/>.</summary>
public sealed class ScriptParam
{
    public ParamType Type;
    public int Int;
    public float Real;
    public string String = "";
    public Vector3 Coord;

    public ScriptParam Clone() => (ScriptParam)MemberwiseClone();

    /// <summary>
    /// Skirmish scripts are copied per computer player with their start index appended to script,
    /// team, counter and flag names; the template side ("SkirmishAmerica" + index) becomes the player.
    /// </summary>
    public void Qualify(string qualifier, string playerTemplateName, string newPlayerName)
    {
        switch (Type)
        {
            case ParamType.Side:
                if (String + qualifier == playerTemplateName)
                    String = newPlayerName;
                break;
            case ParamType.Team:
                if (String == ScriptEngine.ThisTeam)
                    break;
                String += qualifier;
                break;
            case ParamType.Script:
            case ParamType.Counter:
            case ParamType.Flag:
            case ParamType.ScriptSubroutine:
                String += qualifier;
                break;
        }
    }

    public bool Bool => Int != 0;

    public static ScriptParam From(Maps.ScriptParameter p) => new() { Type = (ParamType)p.Type, Int = p.Int, Real = p.Real, String = p.String, Coord = p.Coord };

    public override string ToString() => Type == ParamType.Coord3D ? Coord.ToString() : String.Length > 0 ? String : Int != 0 ? Int.ToString() : Real.ToString();
}

/// <summary>A script condition by its internal name (e.g. "TEAM_DESTROYED").</summary>
public sealed class ScriptCondition
{
    public required string Name;
    public List<ScriptParam> Parameters = new();

    public ScriptParam this[int i] => i < Parameters.Count ? Parameters[i] : Empty;
    static readonly ScriptParam Empty = new();

    public ScriptCondition Clone() => new() { Name = Name, Parameters = Parameters.Select(p => p.Clone()).ToList() };

    public override string ToString() => $"{Name}({string.Join(", ", Parameters)})";
}

/// <summary>A script action by its internal name (e.g. "TEAM_ATTACK_TEAM").</summary>
public sealed class ScriptAction
{
    public required string Name;
    public List<ScriptParam> Parameters = new();

    public ScriptParam this[int i] => i < Parameters.Count ? Parameters[i] : Empty;
    static readonly ScriptParam Empty = new();

    public ScriptAction Clone() => new() { Name = Name, Parameters = Parameters.Select(p => p.Clone()).ToList() };

    public override string ToString() => $"{Name}({string.Join(", ", Parameters)})";
}

/// <summary>
/// One script as the engine runs it (the original's Script): "if any OR-clause (each an AND of
/// conditions) is true then do the actions, else the false-actions". Copies are made for skirmish
/// players and team scripts, so this is mutable runtime state, unlike the map data it comes from.
/// </summary>
public sealed class Script
{
    public string Name = "";
    public string Comment = "";
    public bool IsActive, IsOneShot, IsSubroutine, Easy, Normal, Hard;
    public int DelayEvaluationSeconds;
    public List<List<ScriptCondition>> OrConditions = new();
    public List<ScriptAction> Actions = new();
    public List<ScriptAction> FalseActions = new();

    /// <summary>Not evaluated before this frame (DelayEvaluationSeconds).</summary>
    public int FrameToEvaluate;
    /// <summary>The team the conditions are about; the script runs once per instance of it.</summary>
    public string ConditionTeamName = "";

    public bool AllowedOn(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => Easy,
        Difficulty.Hard => Hard,
        _ => Normal,
    };

    public Script Duplicate() => DuplicateAndQualify("", "", "", qualify: false);

    public Script DuplicateAndQualify(string qualifier, string playerTemplateName, string newPlayerName) =>
        DuplicateAndQualify(qualifier, playerTemplateName, newPlayerName, qualify: true);

    Script DuplicateAndQualify(string qualifier, string playerTemplateName, string newPlayerName, bool qualify)
    {
        T Q<T>(T item, List<ScriptParam> parameters)
        {
            if (qualify)
                foreach (var p in parameters)
                    p.Qualify(qualifier, playerTemplateName, newPlayerName);
            return item;
        }
        return new Script
        {
            Name = qualify ? Name + qualifier : Name,
            Comment = Comment,
            IsActive = IsActive,
            IsOneShot = IsOneShot,
            IsSubroutine = IsSubroutine,
            Easy = Easy,
            Normal = Normal,
            Hard = Hard,
            DelayEvaluationSeconds = DelayEvaluationSeconds,
            OrConditions = OrConditions.Select(and => and.Select(c => { var copy = c.Clone(); return Q(copy, copy.Parameters); }).ToList()).ToList(),
            Actions = Actions.Select(a => { var copy = a.Clone(); return Q(copy, copy.Parameters); }).ToList(),
            FalseActions = FalseActions.Select(a => { var copy = a.Clone(); return Q(copy, copy.Parameters); }).ToList(),
        };
    }

    public static Script From(Maps.MapScript s) => new()
    {
        Name = s.Name,
        Comment = s.Comment,
        IsActive = s.IsActive,
        IsOneShot = s.IsOneShot,
        IsSubroutine = s.IsSubroutine,
        Easy = s.Easy,
        Normal = s.Normal,
        Hard = s.Hard,
        DelayEvaluationSeconds = s.DelayEvaluationSeconds,
        OrConditions = s.OrConditions.Select(and => and.Select(c => new ScriptCondition { Name = c.Name, Parameters = c.Parameters.Select(ScriptParam.From).ToList() }).ToList()).ToList(),
        Actions = s.Actions.Select(a => new ScriptAction { Name = a.Name, Parameters = a.Parameters.Select(ScriptParam.From).ToList() }).ToList(),
        FalseActions = s.FalseActions.Select(a => new ScriptAction { Name = a.Name, Parameters = a.Parameters.Select(ScriptParam.From).ToList() }).ToList(),
    };

    public override string ToString() => Name;
}

/// <summary>A named group of scripts that is switched on and off together, or called as a subroutine.</summary>
public sealed class ScriptGroup
{
    public string Name = "";
    public bool IsActive = true, IsSubroutine;
    public List<Script> Scripts = new();

    public ScriptGroup DuplicateAndQualify(string qualifier, string playerTemplateName, string newPlayerName) => new()
    {
        Name = Name + qualifier,
        IsActive = IsActive,
        IsSubroutine = IsSubroutine,
        Scripts = Scripts.Select(s => s.DuplicateAndQualify(qualifier, playerTemplateName, newPlayerName)).ToList(),
    };
}

/// <summary>One side's scripts: loose scripts and groups.</summary>
public sealed class ScriptList
{
    public List<Script> Scripts = new();
    public List<ScriptGroup> Groups = new();

    public IEnumerable<Script> AllScripts => Scripts.Concat(Groups.SelectMany(g => g.Scripts));

    public bool IsEmpty => Scripts.Count == 0 && Groups.Count == 0;

    public ScriptList DuplicateAndQualify(string qualifier, string playerTemplateName, string newPlayerName) => new()
    {
        Scripts = Scripts.Select(s => s.DuplicateAndQualify(qualifier, playerTemplateName, newPlayerName)).ToList(),
        Groups = Groups.Select(g => g.DuplicateAndQualify(qualifier, playerTemplateName, newPlayerName)).ToList(),
    };

    public static ScriptList From(Maps.ScriptList list) => new()
    {
        Scripts = list.Scripts.Select(Script.From).ToList(),
        Groups = list.Groups.Select(g => new ScriptGroup
        {
            Name = g.Name,
            IsActive = g.IsActive,
            IsSubroutine = g.IsSubroutine,
            Scripts = g.Scripts.Select(Script.From).ToList(),
        }).ToList(),
    };
}

/// <summary>
/// A named set of attack priorities (the original's AttackPriorityInfo): AI units prefer targets
/// with a higher number. Unlisted object types use the set's default.
/// </summary>
public sealed class AttackPriorityInfo
{
    readonly Dictionary<string, int> priorities = new(StringComparer.OrdinalIgnoreCase);
    public string Name { get; }
    public int DefaultPriority { get; set; } = 1;

    public AttackPriorityInfo(string name) => Name = name;

    public void SetPriority(Templates.ObjectTemplate template, int priority) => priorities[template.Name] = priority;

    public int GetPriority(Templates.ObjectTemplate? template) =>
        template != null && priorities.TryGetValue(template.Name, out int p) ? p : DefaultPriority;
}

/// <summary>A script-built list of object type names (OBJECTLIST_ADDOBJECTTYPE), used wherever an object type goes.</summary>
public sealed class ObjectTypes
{
    readonly List<string> types = new();
    public string ListName { get; }

    public ObjectTypes(string name) => ListName = name;

    public IReadOnlyList<string> Types => types;

    public void Add(string type)
    {
        if (!types.Contains(type, StringComparer.OrdinalIgnoreCase))
            types.Add(type);
    }

    public void Remove(string type) => types.RemoveAll(t => t.Equals(type, StringComparison.OrdinalIgnoreCase));

    public bool Contains(string type) => types.Contains(type, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A script being run step by step on a team or unit (TEAM_EXECUTE_SEQUENTIAL_SCRIPT).</summary>
public sealed class SequentialScript
{
    public Players.Team? Team;
    public uint ObjectId;
    public required Script Script;
    public int CurrentInstruction = -1;
    public int TimesToLoop;
    public int FramesToWait = -1;
    public bool DontAdvanceInstruction;
    public SequentialScript? Next;

    public SequentialScript Copy() => new()
    {
        Team = Team, ObjectId = ObjectId, Script = Script, CurrentInstruction = CurrentInstruction, TimesToLoop = TimesToLoop,
        FramesToWait = FramesToWait, DontAdvanceInstruction = DontAdvanceInstruction,
    };
}

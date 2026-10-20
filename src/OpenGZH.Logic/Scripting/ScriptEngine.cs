using OpenGZH.Formats.Maps;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Scripting;

/// <summary>
/// Runs a map's scripts (the mission logic of every campaign map, and the skirmish AI scripts).
/// Each script: when its conditions are true, run its actions (once, if one-shot).
/// Conditions and actions are looked up by their original internal names; see
/// <see cref="ScriptConditions"/> and <see cref="ScriptActions"/>.
/// </summary>
public sealed partial class ScriptEngine
{
    readonly GameLogic game;
    readonly List<RunningScript> scripts = new();
    readonly Dictionary<string, RunningScript> byName = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, bool> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Counters { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When a map has scripts that decide victory (campaign), the default "destroy everything" rule is off.</summary>
    public bool HandlesVictory { get; set; }

    /// <summary>Difficulty for the mission (scripts can be easy/normal/hard only).</summary>
    public Difficulty Difficulty { get; set; } = Difficulty.Normal;

    /// <summary>Scripts are evaluated every this many frames (the original evaluates about twice a second for most).</summary>
    public int EvaluationInterval { get; set; } = 1;

    sealed class RunningScript
    {
        public required MapScript Script;
        public required Players.Player Owner;
        public bool Active;
        public int NextEvaluationFrame;
        public string Group = "";
    }

    public ScriptEngine(GameLogic game) => this.game = game;

    public int ScriptCount => scripts.Count;

    /// <summary>Loads every script from the map's sides.</summary>
    public void Load(SidesList sides)
    {
        foreach (var side in sides.Sides)
        {
            var owner = game.Players.Find(side.Name) ?? game.Players.Neutral;
            foreach (var script in side.Scripts.Scripts)
                Add(script, owner, "", true);
            foreach (var group in side.Scripts.Groups)
                foreach (var script in group.Scripts)
                    Add(script, owner, group.Name, group.IsActive);
        }
        HandlesVictory = scripts.Any(s => s.Script.Actions.Concat(s.Script.FalseActions).Any(a => a.Name is "VICTORY" or "DEFEAT" or "QUICKVICTORY"));
    }

    void Add(MapScript script, Players.Player owner, string group, bool groupActive)
    {
        var running = new RunningScript { Script = script, Owner = owner, Active = script.IsActive && groupActive, Group = group };
        scripts.Add(running);
        byName.TryAdd(script.Name, running);
    }

    public void Update()
    {
        if (scripts.Count == 0)
            return;
        int frame = game.Frame;
        foreach (var running in scripts.ToList())
        {
            if (!running.Active || running.Script.IsSubroutine || frame < running.NextEvaluationFrame)
                continue;
            if (!AllowedOnDifficulty(running.Script))
                continue;

            running.NextEvaluationFrame = frame + Math.Max(EvaluationInterval, running.Script.DelayEvaluationSeconds * Core.LogicTime.FramesPerSecond);
            Evaluate(running);
        }
    }

    bool AllowedOnDifficulty(MapScript script) => Difficulty switch
    {
        Difficulty.Easy => script.Easy,
        Difficulty.Hard => script.Hard,
        _ => script.Normal,
    };

    void Evaluate(RunningScript running)
    {
        var script = running.Script;
        bool result = script.OrConditions.Count == 0 || script.OrConditions.Any(and => and.All(c => Conditions.Evaluate(c, running.Owner)));
        if (result)
        {
            foreach (var action in script.Actions)
                Actions.Execute(action, running.Owner);
            if (script.IsOneShot)
                running.Active = false;
        }
        else if (script.FalseActions.Count > 0)
        {
            foreach (var action in script.FalseActions)
                Actions.Execute(action, running.Owner);
        }
    }

    // ---- Used by actions ----

    public void SetScriptActive(string name, bool active)
    {
        if (byName.TryGetValue(name, out var running))
            running.Active = active;
    }

    public void SetGroupActive(string group, bool active)
    {
        foreach (var running in scripts.Where(s => s.Group.Equals(group, StringComparison.OrdinalIgnoreCase)))
            running.Active = active;
    }

    /// <summary>Runs a subroutine script immediately (CALL_SUBROUTINE).</summary>
    public void CallSubroutine(string name)
    {
        if (byName.TryGetValue(name, out var running))
            Evaluate(running);
    }

    // ---- Notifications from the game ----

    readonly HashSet<string> destroyedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A script-named object became another (a pilot climbing into a vehicle): the name moves with it.</summary>
    public void TransferObjectName(GameObject from, GameObject to)
    {
        if (from.Name.Length == 0)
            return;
        string name = from.Name;
        game.SetObjectName(from, "");
        game.SetObjectName(to, name);
    }

    public void OnObjectDied(GameObject obj)
    {
        if (obj.Name.Length > 0)
            destroyedNames.Add(obj.Name);
    }

    public bool WasNamedObjectDestroyed(string name) => destroyedNames.Contains(name);

    ScriptConditions? conditions;
    ScriptActions? actions;
    ScriptConditions Conditions => conditions ??= new ScriptConditions(game, this);
    ScriptActions Actions => actions ??= new ScriptActions(game, this);
}

public enum Difficulty { Easy, Normal, Hard }

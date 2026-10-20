using OpenGZH.Formats.Maps;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Scripting;

/// <summary>
/// Script conditions, by their original internal names (e.g. "TIMER_EXPIRED", "NAMED_DESTROYED").
/// Each handler receives the condition (with its parameters) and the player that owns the script.
/// Unimplemented conditions are reported once and evaluate to false.
/// </summary>
public sealed class ScriptConditions
{
    readonly GameLogic game;
    readonly ScriptEngine engine;
    readonly Dictionary<string, Func<ScriptCondition, Player, bool>> handlers = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> reported = new();

    public ScriptConditions(GameLogic game, ScriptEngine engine)
    {
        this.game = game;
        this.engine = engine;
        Register();
    }

    public static IEnumerable<string> Missing => reported;

    public bool Evaluate(ScriptCondition condition, Player owner)
    {
        if (handlers.TryGetValue(condition.Name, out var handler))
            return handler(condition, owner);
        if (reported.Add(condition.Name))
            Log.Warning($"Script condition '{condition.Name}' is not implemented yet.");
        return false;
    }

    static string Text(ScriptCondition c, int index) => index < c.Parameters.Count ? c.Parameters[index].String : "";
    static int Int(ScriptCondition c, int index) => index < c.Parameters.Count ? c.Parameters[index].Int : 0;

    /// <summary>Comparison parameter: 0 &lt;, 1 &lt;=, 2 ==, 3 &gt;=, 4 &gt;, 5 !=.</summary>
    public static bool Compare(int comparison, float left, float right) => comparison switch
    {
        0 => left < right,
        1 => left <= right,
        2 => Math.Abs(left - right) < 0.0001f,
        3 => left >= right,
        4 => left > right,
        _ => Math.Abs(left - right) >= 0.0001f,
    };

    void Register()
    {
        handlers["CONDITION_TRUE"] = (_, _) => true;
        handlers["CONDITION_FALSE"] = (_, _) => false;

        // FLAG(flag, value)
        handlers["FLAG"] = (c, _) => engine.Flags.GetValueOrDefault(Text(c, 0)) == (Int(c, 1) != 0);

        // COUNTER(counter, comparison, value)
        handlers["COUNTER"] = (c, _) => Compare(Int(c, 1), engine.Counters.GetValueOrDefault(Text(c, 0)), Int(c, 2));

        // TIMER_EXPIRED(timer): timers count down in frames and "expire" at zero.
        handlers["TIMER_EXPIRED"] = (c, _) => engine.IsTimerExpired(Text(c, 0));

        handlers["NAMED_DESTROYED"] = (c, _) =>
            game.FindObjectByName(Text(c, 0)) is not { IsEffectivelyDead: false } && engine.WasNamedObjectDestroyed(Text(c, 0));
        handlers["NAMED_NOT_DESTROYED"] = (c, _) =>
            game.FindObjectByName(Text(c, 0)) is { IsEffectivelyDead: false };
        handlers["NAMED_TOTALLY_DEAD"] = (c, _) => game.FindObjectByName(Text(c, 0)) == null;
        handlers["NAMED_DYING"] = (c, _) => game.FindObjectByName(Text(c, 0)) is { IsEffectivelyDead: true };

        handlers["TEAM_DESTROYED"] = (c, _) => game.Players.FindTeam(Text(c, 0)) is not { HasAnyMembers: true };
        handlers["TEAM_HAS_UNITS"] = (c, _) => game.Players.FindTeam(Text(c, 0)) is { HasAnyMembers: true };

        handlers["PLAYER_HAS_CREDITS"] = (c, owner) =>
            ResolvePlayer(Text(c, 2), owner) is { } player && Compare(Int(c, 1), player.Money.Amount, Int(c, 0));

        handlers["PLAYER_ALL_DESTROYED"] = (c, owner) =>
            ResolvePlayer(Text(c, 0), owner) is { } player && !game.ObjectsOwnedBy(player).Any(o => !o.IsEffectivelyDead && o.IsKindOf(Core.Kind.Selectable));

        handlers["PLAYER_ALL_BUILDFACILITIES_DESTROYED"] = (c, owner) =>
            ResolvePlayer(Text(c, 0), owner) is { } player && !game.ObjectsOwnedBy(player).Any(o => !o.IsEffectivelyDead && o.FindModule<Modules.Production.ProductionUpdate>() != null);

        handlers["PLAYER_HAS_POWER"] = (c, owner) => ResolvePlayer(Text(c, 0), owner)?.Energy.HasSufficientPower ?? false;
        handlers["PLAYER_HAS_NO_POWER"] = (c, owner) => !(ResolvePlayer(Text(c, 0), owner)?.Energy.HasSufficientPower ?? true);
        handlers["PLAYER_ACQUIRED_SCIENCE"] = (c, owner) => ResolvePlayer(Text(c, 0), owner)?.HasScience(Text(c, 1)) ?? false;
        handlers["PLAYER_BUILT_UPGRADE"] = (c, owner) => ResolvePlayer(Text(c, 0), owner)?.HasUpgrade(Text(c, 1)) ?? false;
    }

    /// <summary>Script player parameters may be a player name, or "&lt;This Player&gt;" style placeholders.</summary>
    public Player? ResolvePlayer(string name, Player scriptOwner)
    {
        if (name.Length == 0 || name.StartsWith("<This Player", StringComparison.OrdinalIgnoreCase))
            return scriptOwner;
        if (name.StartsWith("<Local Player", StringComparison.OrdinalIgnoreCase))
            return game.Players.Local ?? scriptOwner;
        return game.Players.Find(name);
    }
}

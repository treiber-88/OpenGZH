using OpenGZH.Formats.Maps;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Scripting;

/// <summary>
/// Script actions, by their original internal names (e.g. "SET_FLAG", "TEAM_FOLLOW_WAYPOINTS").
/// Unimplemented actions are reported once and do nothing.
/// </summary>
public sealed class ScriptActions
{
    readonly GameLogic game;
    readonly ScriptEngine engine;
    readonly Dictionary<string, Action<ScriptAction, Player>> handlers = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> reported = new();

    public ScriptActions(GameLogic game, ScriptEngine engine)
    {
        this.game = game;
        this.engine = engine;
        Register();
    }

    public static IEnumerable<string> Missing => reported;

    public void Execute(ScriptAction action, Player owner)
    {
        if (handlers.TryGetValue(action.Name, out var handler))
            handler(action, owner);
        else if (reported.Add(action.Name))
            Log.Warning($"Script action '{action.Name}' is not implemented yet.");
    }

    static string Text(ScriptAction a, int index) => index < a.Parameters.Count ? a.Parameters[index].String : "";
    static int Int(ScriptAction a, int index) => index < a.Parameters.Count ? a.Parameters[index].Int : 0;
    static float Real(ScriptAction a, int index) => index < a.Parameters.Count ? a.Parameters[index].Real : 0;

    void Register()
    {
        handlers["NO_OP"] = (_, _) => { };
        handlers["DEBUG_STRING"] = (a, _) => Log.Info("[script] " + Text(a, 0));

        handlers["SET_FLAG"] = (a, _) => engine.Flags[Text(a, 0)] = Int(a, 1) != 0;
        handlers["SET_COUNTER"] = (a, _) => engine.Counters[Text(a, 0)] = Int(a, 1);
        handlers["INCREMENT_COUNTER"] = (a, _) => engine.Counters[Text(a, 1)] = engine.Counters.GetValueOrDefault(Text(a, 1)) + Int(a, 0);
        handlers["DECREMENT_COUNTER"] = (a, _) => engine.Counters[Text(a, 1)] = engine.Counters.GetValueOrDefault(Text(a, 1)) - Int(a, 0);

        // Timers: SET_TIMER uses frames, SET_MILLISECOND_TIMER seconds (as a real).
        handlers["SET_TIMER"] = (a, _) => engine.StartTimer(Text(a, 0), Int(a, 1));
        handlers["SET_MILLISECOND_TIMER"] = (a, _) => engine.StartTimer(Text(a, 0), LogicTime.FramesFromSeconds(Real(a, 1)));
        handlers["SET_RANDOM_TIMER"] = (a, _) => engine.StartTimer(Text(a, 0), game.Random.Int(Int(a, 1), Int(a, 2)));
        handlers["SET_RANDOM_MSEC_TIMER"] = (a, _) =>
            engine.StartTimer(Text(a, 0), LogicTime.FramesFromSeconds(game.Random.Float(Real(a, 1), Real(a, 2))));
        handlers["STOP_TIMER"] = (a, _) => engine.PauseTimer(Text(a, 0));
        handlers["RESTART_TIMER"] = (a, _) => engine.ResumeTimer(Text(a, 0));
        handlers["ADD_TO_MSEC_TIMER"] = (a, _) => engine.AddToTimer(Text(a, 1), LogicTime.FramesFromSeconds(Real(a, 0)));

        handlers["ENABLE_SCRIPT"] = (a, _) => { engine.SetScriptActive(Text(a, 0), true); engine.SetGroupActive(Text(a, 0), true); };
        handlers["DISABLE_SCRIPT"] = (a, _) => { engine.SetScriptActive(Text(a, 0), false); engine.SetGroupActive(Text(a, 0), false); };
        handlers["CALL_SUBROUTINE"] = (a, _) => engine.CallSubroutine(Text(a, 0));

        handlers["VICTORY"] = (_, owner) => engine.EndGame(victory: true, owner);
        handlers["QUICKVICTORY"] = (_, owner) => engine.EndGame(victory: true, owner);
        handlers["DEFEAT"] = (_, owner) => engine.EndGame(victory: false, owner);

        handlers["PLAYER_GIVE_MONEY"] = (a, owner) => ResolvePlayer(Text(a, 0), owner)?.Money.Deposit(Int(a, 1));
        handlers["PLAYER_SET_MONEY"] = (a, owner) =>
        {
            if (ResolvePlayer(Text(a, 0), owner) is { } player)
            {
                player.Money.WithdrawUpTo(player.Money.Amount);
                player.Money.Deposit(Int(a, 1));
            }
        };
        handlers["PLAYER_GRANT_SCIENCE"] = (a, owner) => ResolvePlayer(Text(a, 0), owner)?.GrantScience(Text(a, 1));

        handlers["NAMED_KILL"] = (a, _) => game.FindObjectByName(Text(a, 0))?.Kill();
        handlers["NAMED_DELETE"] = (a, _) => { if (game.FindObjectByName(Text(a, 0)) is { } obj) game.DestroyObject(obj); };
        handlers["TEAM_KILL"] = (a, _) => { foreach (var member in game.Players.FindTeam(Text(a, 0))?.Members.ToList() ?? []) member.Kill(); };
        handlers["TEAM_DELETE"] = (a, _) => { foreach (var member in game.Players.FindTeam(Text(a, 0))?.Members.ToList() ?? []) game.DestroyObject(member); };

        handlers["SPEECH_PLAY"] = (a, _) => game.Events.PlaySound(Text(a, 0));
        handlers["PLAY_SOUND_EFFECT"] = (a, _) => game.Events.PlaySound(Text(a, 0));
        handlers["SHOW_MILITARY_CAPTION"] = (a, _) => game.Events.Message(Text(a, 0));
    }

    Player? ResolvePlayer(string name, Player scriptOwner)
    {
        if (name.Length == 0 || name.StartsWith("<This Player", StringComparison.OrdinalIgnoreCase))
            return scriptOwner;
        if (name.StartsWith("<Local Player", StringComparison.OrdinalIgnoreCase))
            return game.Players.Local ?? scriptOwner;
        return game.Players.Find(name);
    }
}

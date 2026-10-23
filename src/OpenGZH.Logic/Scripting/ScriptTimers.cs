using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Scripting;

/// <summary>How the mission ended, for the shell (victory / defeat screens).</summary>
public sealed partial class ScriptEngine
{
    public bool? MissionResult { get; private set; }

    public event Action<bool>? OnMissionEnded;

    /// <summary>
    /// The mission is over for the local player (VICTORY / DEFEAT). The game plays on while the
    /// victory or defeat message shows; the end game timer then closes it (<see cref="GameLogic.IsGameOver"/>).
    /// </summary>
    public void EndGame(bool victory, Player? scriptOwner)
    {
        if (MissionResult != null)
            return;
        MissionResult = victory;
        OnMissionEnded?.Invoke(victory);
    }
}

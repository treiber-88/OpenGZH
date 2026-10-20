using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Scripting;

/// <summary>Script timers and game-ending, kept apart from the evaluation loop for readability.</summary>
public sealed partial class ScriptEngine
{
    /// <summary>A countdown: expires at <see cref="EndFrame"/>; a paused timer remembers how much was left.</summary>
    sealed class Timer
    {
        public int EndFrame;
        public int? PausedRemaining;
    }

    readonly Dictionary<string, Timer> timers = new(StringComparer.OrdinalIgnoreCase);

    public void StartTimer(string name, int frames) => timers[name] = new Timer { EndFrame = game.Frame + frames };

    public bool IsTimerExpired(string name) =>
        timers.TryGetValue(name, out var timer) && timer.PausedRemaining == null && game.Frame >= timer.EndFrame;

    public void PauseTimer(string name)
    {
        if (timers.TryGetValue(name, out var timer) && timer.PausedRemaining == null)
            timer.PausedRemaining = Math.Max(0, timer.EndFrame - game.Frame);
    }

    public void ResumeTimer(string name)
    {
        if (timers.TryGetValue(name, out var timer) && timer.PausedRemaining is { } remaining)
        {
            timer.EndFrame = game.Frame + remaining;
            timer.PausedRemaining = null;
        }
    }

    public void AddToTimer(string name, int frames)
    {
        if (timers.TryGetValue(name, out var timer))
            timer.EndFrame += frames;
    }

    /// <summary>Frames left on a timer (for the on-screen countdown).</summary>
    public int TimerRemaining(string name) =>
        timers.TryGetValue(name, out var timer) ? timer.PausedRemaining ?? Math.Max(0, timer.EndFrame - game.Frame) : 0;

    public bool? MissionResult { get; private set; }

    public event Action<bool>? OnMissionEnded;

    public void EndGame(bool victory, Player scriptOwner)
    {
        if (MissionResult != null)
            return;
        MissionResult = victory;
        game.IsGameOver = true;
        OnMissionEnded?.Invoke(victory);
    }
}

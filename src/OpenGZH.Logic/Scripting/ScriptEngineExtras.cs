using System.Numerics;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Scripting;

/// <summary>Smaller pieces of the original ScriptEngine that actions and conditions use.</summary>
public sealed partial class ScriptEngine
{
    /// <summary>Script debug output (the original sends it to the debug window DLL; here, to the log when enabled).</summary>
    public static bool ShowDebugMessages { get; set; }

    public void AppendDebugMessage(string message, bool pause)
    {
        if (ShowDebugMessages)
            Log.Info($"[script] {game.Frame} {message}");
    }

    /// <summary>The skirmish scripts' "team is building" flags, cleared when a team is released or disbanded.</summary>
    public void ClearTeamFlags()
    {
        ClearFlag("USA Team is Building");
        ClearFlag("USA Air Team Is Building");
        ClearFlag("USA Inf Team Is Building");
        ClearFlag("China Team is Building");
        ClearFlag("China Air Team Is Building");
        ClearFlag("China Inf Team Is Building");
        ClearFlag("GLA Team is Building");
        ClearFlag("GLA Inf Team is Building");
    }

    // ---- Named permanent reveals (MAP_REVEAL_PERMANENTLY_AT_WAYPOINT) ----

    sealed record NamedReveal(string Name, string Waypoint, float Radius, string Player);
    readonly List<NamedReveal> namedReveals = new();

    public void CreateNamedMapReveal(string revealName, string waypointName, float radius, string playerName)
    {
        if (namedReveals.Any(r => r.Name == revealName))
            return; // (the original refuses to redefine one)
        namedReveals.Add(new NamedReveal(revealName, waypointName, radius, playerName));
    }

    bool ResolveReveal(string revealName, out Vector2 where, out float radius, out uint mask)
    {
        where = default;
        radius = 0;
        mask = 0;
        if (namedReveals.FirstOrDefault(r => r.Name == revealName) is not { } reveal)
            return false;
        if (game.Waypoints?.Find(reveal.Waypoint) is not { } way || GetPlayer(reveal.Player) is not { } player)
            return false;
        where = new Vector2(way.Position.X, way.Position.Y);
        radius = reveal.Radius;
        mask = player.PlayerMask;
        return true;
    }

    public void DoNamedMapReveal(string revealName)
    {
        if (ResolveReveal(revealName, out var where, out float radius, out uint mask))
            game.Shroud.DoShroudReveal(where, radius, mask);
    }

    public void UndoNamedMapReveal(string revealName)
    {
        if (ResolveReveal(revealName, out var where, out float radius, out uint mask))
            game.Shroud.UndoShroudReveal(where, radius, mask);
    }

    public void RemoveNamedMapReveal(string revealName) => namedReveals.RemoveAll(r => r.Name == revealName);

    // ---- Time ----

    public void DoFreezeTime() => IsTimeFrozenByScript = true;

    /// <summary>The wind trees sway in (the original's BreezeInfo); Version goes up when a script changes it.</summary>
    public BreezeInfo Breeze { get; private set; } = BreezeInfo.Default;

    /// <summary>SET_TREE_SWAY: direction (radians), intensity, lean, period (frames), randomness.</summary>
    public void DoSetSway(float direction, float intensity, float lean, int period, float randomness) =>
        Breeze = new BreezeInfo(direction, intensity, lean, Math.Max(1, period), randomness, Breeze.Version + 1);
    public void DoUnfreezeTime() => IsTimeFrozenByScript = false;

    // ---- Topple directions (NAMED_SET_TOPPLE_DIRECTION) ----

    readonly Dictionary<string, Vector3> toppleDirections = new(StringComparer.Ordinal);

    public void SetToppleDirection(string objectName, Vector3? direction)
    {
        if (objectName.Length == 0)
            return;
        if (direction is { } d)
            toppleDirections[objectName] = d;
        else
            toppleDirections.Remove(objectName);
    }

    /// <summary>A script-chosen fall direction for a named tree or pole, if any.</summary>
    public Vector3? ToppleDirectionFor(GameObject obj) =>
        obj.Name.Length > 0 && toppleDirections.TryGetValue(obj.Name, out var d) ? d : null;

    // ---- Switches ----

    public bool ObjectsShouldReceiveDifficultyBonus { get; set; } = true;
    public bool ChooseVictimAlwaysUsesNormal { get; set; }
    public bool HasShownMPLocalDefeatWindow { get; private set; }
    public void MarkMPLocalDefeatWindowShown() => HasShownMPLocalDefeatWindow = true;

    // ---- Things the client reports as finished (videos, speech, sounds) and sciences gained ----

    readonly HashSet<string> completedVideos = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> completedSpeech = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> completedAudio = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, List<string>> acquiredSciences = new();

    public void NotifyOfCompletedVideo(string name) => completedVideos.Add(name);
    public void NotifyOfCompletedSpeech(string name) => completedSpeech.Add(name);
    public void NotifyOfCompletedAudio(string name) => completedAudio.Add(name);

    /// <summary>The original's isVideoComplete: true once the video ended; with removeIfSo it is forgotten again.</summary>
    public bool IsVideoComplete(string name, bool removeIfSo) => Consume(completedVideos, name, removeIfSo);
    public bool IsSpeechComplete(string name, bool removeIfSo) => Consume(completedSpeech, name, removeIfSo);
    public bool IsAudioComplete(string name, bool removeIfSo) => Consume(completedAudio, name, removeIfSo);

    static bool Consume(HashSet<string> set, string name, bool remove)
    {
        if (!set.Contains(name))
            return false;
        if (remove)
            set.Remove(name);
        return true;
    }

    public void NotifyOfAcquiredScience(int playerIndex, string science)
    {
        if (!acquiredSciences.TryGetValue(playerIndex, out var list))
            acquiredSciences[playerIndex] = list = new();
        list.Add(science);
    }

    public bool IsScienceAcquired(int playerIndex, string science, bool removeFromList = false)
    {
        if (!acquiredSciences.TryGetValue(playerIndex, out var list))
            return false;
        int i = list.FindIndex(s => s.Equals(science, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
            return false;
        if (removeFromList)
            list.RemoveAt(i);
        return true;
    }
}

/// <summary>How the wind blows trees about (the original's BreezeInfo).</summary>
public sealed record BreezeInfo(float Direction, float Intensity, float Lean, int BreezePeriod, float Randomness, int Version)
{
    public static readonly BreezeInfo Default = new(MathF.PI / 3, 0.07f * MathF.PI / 4, 0.07f * MathF.PI / 4, Core.LogicTime.FramesPerSecond * 5, 0.2f, 0);

    /// <summary>(sin, cos) of the direction, as the original stores it.</summary>
    public System.Numerics.Vector2 DirectionVec => new(MathF.Sin(Direction), MathF.Cos(Direction));
}

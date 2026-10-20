namespace OpenGZH.Logic.Core;

/// <summary>
/// The simulation runs at a fixed 30 frames per second, exactly like the original game.
/// Rules give durations in milliseconds and speeds per second; these helpers convert them.
/// </summary>
public static class LogicTime
{
    public const int FramesPerSecond = 30;
    public const float SecondsPerFrame = 1f / FramesPerSecond;
    public const float MillisecondsPerFrame = 1000f / FramesPerSecond;

    /// <summary>What an update module returns to sleep until something wakes it (about 100 days of game time).</summary>
    public const int SleepForever = 1 << 28;

    /// <summary>Milliseconds to frames, rounding up (the original's conversion).</summary>
    public static int Frames(float milliseconds) =>
        milliseconds <= 0 ? 0 : (int)MathF.Ceiling(milliseconds / MillisecondsPerFrame - 0.0001f);

    public static int FramesFromSeconds(float seconds) => Frames(seconds * 1000f);

    /// <summary>A per-second rate (speed, turn rate, ...) to a per-frame amount.</summary>
    public static float PerFrame(float perSecond) => perSecond * SecondsPerFrame;
}

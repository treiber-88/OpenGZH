using OpenGZH.Core.Rules;

namespace OpenGZH.Logic.Core;

/// <summary>
/// Converts rule values into simulation units, matching the original INI parsers:
///   angles in degrees            → radians
///   speeds per second            → units per frame
///   accelerations per second²    → units per frame²
///   turn rates in degrees/second → radians per frame
///   durations in milliseconds    → frames
/// </summary>
public static class RuleUnits
{
    public static float Angle(string? text, float fallbackDegrees = 0) =>
        RuleValue.Float(text, fallbackDegrees) * MathF.PI / 180f;

    public static float Velocity(string? text, float fallbackPerSecond = 0) =>
        RuleValue.Float(text, fallbackPerSecond) / LogicTime.FramesPerSecond;

    public static float Acceleration(string? text, float fallbackPerSecondSquared = 0) =>
        RuleValue.Float(text, fallbackPerSecondSquared) / (LogicTime.FramesPerSecond * LogicTime.FramesPerSecond);

    public static float AngularVelocity(string? text, float fallbackDegreesPerSecond = 0) =>
        Angle(text, fallbackDegreesPerSecond) / LogicTime.FramesPerSecond;

    public static int Duration(string? text, float fallbackMs = 0) =>
        LogicTime.Frames(RuleValue.Float(text, fallbackMs));
}

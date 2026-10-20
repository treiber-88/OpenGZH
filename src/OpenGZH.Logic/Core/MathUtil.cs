using System.Numerics;

namespace OpenGZH.Logic.Core;

public static class MathUtil
{
    /// <summary>Wraps an angle into (-π, π].</summary>
    public static float NormalizeAngle(float radians)
    {
        while (radians > MathF.PI) radians -= MathF.Tau;
        while (radians <= -MathF.PI) radians += MathF.Tau;
        return radians;
    }

    /// <summary>The signed smallest rotation from one angle to another.</summary>
    public static float AngleDifference(float from, float to) => NormalizeAngle(to - from);

    /// <summary>Turns <paramref name="current"/> towards <paramref name="target"/> by at most <paramref name="maxStep"/>.</summary>
    public static float TurnTowards(float current, float target, float maxStep)
    {
        float difference = AngleDifference(current, target);
        if (MathF.Abs(difference) <= maxStep)
            return NormalizeAngle(target);
        return NormalizeAngle(current + MathF.Sign(difference) * maxStep);
    }

    /// <summary>Angle (radians, 0 = +X) of the direction from one point to another, ignoring height.</summary>
    public static float AngleTo(Vector3 from, Vector3 to) => MathF.Atan2(to.Y - from.Y, to.X - from.X);

    public static Vector3 Forward(float angle) => new(MathF.Cos(angle), MathF.Sin(angle), 0);

    public static float Distance2D(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

    public static float DistanceSquared2D(Vector3 a, Vector3 b) => Vector2.DistanceSquared(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
}

/// <summary>Small helpers for space-separated rule values such as "SET_NORMAL CrusaderLocomotor".</summary>
public static class RuleWords
{
    public static string First(string? text) => Split(text).ElementAtOrDefault(0) ?? "";
    public static string Second(string? text) => Split(text).ElementAtOrDefault(1) ?? "";
    static string[] Split(string? text) => string.IsNullOrWhiteSpace(text) ? [] : text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace OpenGZH.Core.Rules;

/// <summary>
/// Parses rule values written in the original Zero Hour style, so the converted data needs no rewriting:
///   numbers "12.5", percentages "50%", booleans "Yes"/"No", colors "R:200 G:150 B:100 A:255",
///   coordinates "X:10 Y:-5 Z:3", and durations in milliseconds.
/// </summary>
public static partial class RuleValue
{
    /// <summary>Matches "X:10", "Y: -5.5" (a space after the colon is allowed, as in the original data).</summary>
    [GeneratedRegex(@"([A-Za-z]+)\s*:\s*(-?[0-9]*\.?[0-9]+)")]
    internal static partial Regex NamedNumber();

    /// <summary>The number at the start of a token ("100.0f" -> "100.0").</summary>
    [GeneratedRegex(@"^[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?")]
    private static partial Regex LeadingNumber();

    /// <summary>
    /// Reads a number the way the original's sscanf("%f") does: the leading number counts and any
    /// trailing letters are ignored (the data has values like "100.0f").
    /// </summary>
    static bool TryParseLeading(string token, out float value)
    {
        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;
        var match = LeadingNumber().Match(token);
        return match.Success && float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static float Float(string? text, float fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        string first = FirstToken(text);
        bool percent = first.EndsWith('%');
        if (percent)
            first = first[..^1];
        if (!TryParseLeading(first, out float value))
            return fallback;
        return percent ? value / 100f : value;
    }

    public static int Int(string? text, int fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        string first = FirstToken(text);
        if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return value;
        return TryParseLeading(first, out float f) ? (int)f : fallback;
    }

    public static bool Bool(string? text, bool fallback = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        return FirstToken(text).ToUpperInvariant() switch
        {
            "YES" or "TRUE" or "1" => true,
            "NO" or "FALSE" or "0" => false,
            _ => fallback,
        };
    }

    /// <summary>Reads "X:1 Y:2 Z:3" style values (any subset of named components).</summary>
    public static Vector3 Coord(string? text, Vector3 fallback = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        var result = fallback;
        foreach (Match match in NamedNumber().Matches(text))
        {
            float value = Float(match.Groups[2].Value);
            switch (match.Groups[1].Value.ToUpperInvariant())
            {
                case "X": result.X = value; break;
                case "Y": result.Y = value; break;
                case "Z": result.Z = value; break;
            }
        }
        return result;
    }

    /// <summary>Splits a space-separated list ("KindOf: A B C").</summary>
    public static string[] Words(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    static string FirstToken(string text)
    {
        text = text.Trim();
        int space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
}

/// <summary>Colors written as "R:200 G:150 B:100 A:255" (0-255 each), returned as 0-1 floats.</summary>
public static class RuleColor
{
    /// <summary>A 0xRRGGBB colour as RGBA 0..1 (opaque).</summary>
    public static Vector4 FromRgb(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1);

    public static Vector4 Parse(string? text, Vector4 fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        var result = new Vector4(fallback.X, fallback.Y, fallback.Z, 1);
        foreach (Match match in RuleValue.NamedNumber().Matches(text))
        {
            float value = RuleValue.Float(match.Groups[2].Value) / 255f;
            switch (match.Groups[1].Value.ToUpperInvariant())
            {
                case "R": result.X = value; break;
                case "G": result.Y = value; break;
                case "B": result.Z = value; break;
                case "A": result.W = value; break;
            }
        }
        return result;
    }
}

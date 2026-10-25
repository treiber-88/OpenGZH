using System.Reflection;
using OpenGZH.Core.Rules;

namespace OpenGZH.TiberianDawn.Logic.Types;

/// <summary>
/// Fills a type class's public fields from its rules entry, by name: numbers, yes/no values, the
/// original enum names (ARMOR_STEEL), flag sets of #define names ("HOUSEF_GOOD HOUSEF_BAD"),
/// lists, pixel pairs ("22,39") and named rows (an infantry's DoTable).
/// </summary>
public static class RuleBinder
{
    public static void Bind(object target, RuleNode node)
    {
        foreach (var field in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!node.TryGet(field.Name, out var value) || field.IsInitOnly)
                continue;
            if (ConvertValue(value, field.FieldType) is { } converted)
                field.SetValue(target, converted);
        }
    }

    static object? ConvertValue(RuleNode value, Type type)
    {
        if (type == typeof(string))
            return value.IsScalar ? value.Value ?? "" : null;
        if (type == typeof(bool))
            return value.IsScalar && IsTrue(value.Value);
        if (type.IsEnum)
            return value.IsScalar && Enum.TryParse(type, value.Value, ignoreCase: false, out var e) ? e : null;
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(uint) || type == typeof(sbyte) || type == typeof(ushort))
        {
            if (!value.IsScalar || Number(value.Value) is not long n)
                return null;
            return System.Convert.ChangeType(type == typeof(uint) ? (uint)n : type == typeof(byte) ? (byte)n : type == typeof(ushort) ? (ushort)n : n, type);
        }
        if (type == typeof(float))
            return value.IsScalar && float.TryParse(value.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : null;
        if (type.IsArray && value.IsSequence)
        {
            var element = type.GetElementType()!;
            var items = value.Items.Select(i => ConvertValue(i, element)).ToList();
            var array = Array.CreateInstance(element, items.Count);
            for (int i = 0; i < items.Count; i++)
                array.SetValue(items[i] ?? Activator.CreateInstance(element), i);
            return array;
        }
        if (type == typeof(Dictionary<string, int[]>) && value.IsMapping)
            return value.Entries.ToDictionary(e => e.Key, e => e.Value.AsList().Select(i => (int)(Number(i.Value) ?? 0)).ToArray());
        return null;
    }

    public static bool IsTrue(string? text) => text is "1" or "true" or "yes" or "True" or "Yes";

    /// <summary>A number, an enum name, a #define name, or a space-separated set of them (OR-ed together).</summary>
    public static long? Number(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        long result = 0;
        foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part, out long n))
                result |= n;
            else if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(part[2..], System.Globalization.NumberStyles.HexNumber, null, out long h))
                result |= h;
            else if (Defines.TryGetValue(part, out long d))
                result |= d;
            else if (EnumValues.Value.TryGetValue(part, out long ev))
                result |= ev;
            else
                return null;
        }
        return result;
    }

    static readonly Dictionary<string, long> Defines = typeof(TdDefines).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral).ToDictionary(f => f.Name, f => Convert.ToInt64(f.GetRawConstantValue()!));

    static readonly Lazy<Dictionary<string, long>> EnumValues = new(() =>
    {
        var values = new Dictionary<string, long>();
        foreach (var type in typeof(TdDefines).Assembly.GetTypes().Where(t => t.IsEnum && t.Namespace == typeof(TdDefines).Namespace))
            foreach (var name in Enum.GetNames(type))
                values.TryAdd(name, System.Convert.ToInt64(Enum.Parse(type, name)));
        return values;
    });
}

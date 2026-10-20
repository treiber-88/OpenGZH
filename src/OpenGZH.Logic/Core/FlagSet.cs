using System.Numerics;

namespace OpenGZH.Logic.Core;

/// <summary>
/// A list of flag names (e.g. every KindOf), giving each a bit number.
/// The names are exactly the ones used in the rules files.
/// </summary>
public sealed class FlagNames
{
    readonly string[] names;
    readonly Dictionary<string, int> indices = new(StringComparer.OrdinalIgnoreCase);

    public FlagNames(string category, IEnumerable<string> names)
    {
        Category = category;
        this.names = names.ToArray();
        if (this.names.Length > FlagSet.Capacity)
            throw new ArgumentException($"{category} has more than {FlagSet.Capacity} flags.");
        for (int i = 0; i < this.names.Length; i++)
            indices[this.names[i]] = i;
    }

    public string Category { get; }
    public int Count => names.Length;
    public string this[int index] => names[index];

    public int IndexOf(string name) => indices.TryGetValue(name, out int index) ? index : -1;

    /// <summary>
    /// Parses a space-separated list like "PRELOAD SELECTABLE VEHICLE". Unknown names are reported
    /// through <paramref name="unknown"/> (if given) and skipped.
    /// "ALL" sets every flag; a leading "-" or "+" removes or adds a flag (as in DeathTypes).
    /// </summary>
    public FlagSet Parse(string? text, Action<string>? unknown = null)
    {
        var set = new FlagSet();
        if (string.IsNullOrWhiteSpace(text))
            return set;

        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals("NONE", StringComparison.OrdinalIgnoreCase) && IndexOf("NONE") < 0)
                continue;
            if (word.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < names.Length; i++)
                    set = set.With(i);
                continue;
            }

            bool remove = word[0] == '-';
            string name = word[0] is '-' or '+' ? word[1..] : word;
            int index = IndexOf(name);
            if (index < 0)
            {
                unknown?.Invoke(name);
                continue;
            }
            set = remove ? set.Without(index) : set.With(index);
        }
        return set;
    }

    public string Format(FlagSet set) => string.Join(' ', Enumerable.Range(0, names.Length).Where(set.Has).Select(i => names[i]));
}

/// <summary>An immutable set of up to 256 flags (bit numbers come from a <see cref="FlagNames"/>).</summary>
public readonly struct FlagSet : IEquatable<FlagSet>
{
    public const int Capacity = 256;

    readonly ulong a, b, c, d;

    FlagSet(ulong a, ulong b, ulong c, ulong d)
    {
        this.a = a; this.b = b; this.c = c; this.d = d;
    }

    public static FlagSet Empty => default;

    public bool Has(int bit) => bit switch
    {
        < 0 => false,
        < 64 => (a >> bit & 1) != 0,
        < 128 => (b >> (bit - 64) & 1) != 0,
        < 192 => (c >> (bit - 128) & 1) != 0,
        < 256 => (d >> (bit - 192) & 1) != 0,
        _ => false,
    };

    public FlagSet With(int bit) => bit switch
    {
        < 0 => this,
        < 64 => new(a | 1UL << bit, b, c, d),
        < 128 => new(a, b | 1UL << (bit - 64), c, d),
        < 192 => new(a, b, c | 1UL << (bit - 128), d),
        _ => new(a, b, c, d | 1UL << (bit - 192)),
    };

    public FlagSet Without(int bit) => bit switch
    {
        < 0 => this,
        < 64 => new(a & ~(1UL << bit), b, c, d),
        < 128 => new(a, b & ~(1UL << (bit - 64)), c, d),
        < 192 => new(a, b, c & ~(1UL << (bit - 128)), d),
        _ => new(a, b, c, d & ~(1UL << (bit - 192))),
    };

    public FlagSet With(int bit, bool value) => value ? With(bit) : Without(bit);

    public bool IsEmpty => (a | b | c | d) == 0;
    public bool HasAll(FlagSet other) => (a & other.a) == other.a && (b & other.b) == other.b && (c & other.c) == other.c && (d & other.d) == other.d;
    public bool HasAny(FlagSet other) => ((a & other.a) | (b & other.b) | (c & other.c) | (d & other.d)) != 0;
    public int Count => BitOperations.PopCount(a) + BitOperations.PopCount(b) + BitOperations.PopCount(c) + BitOperations.PopCount(d);

    /// <summary>Number of flags set in both sets (used to pick the best-matching model condition state).</summary>
    public int CountInCommon(FlagSet other) =>
        BitOperations.PopCount(a & other.a) + BitOperations.PopCount(b & other.b) + BitOperations.PopCount(c & other.c) + BitOperations.PopCount(d & other.d);

    public static FlagSet operator |(FlagSet x, FlagSet y) => new(x.a | y.a, x.b | y.b, x.c | y.c, x.d | y.d);
    public static FlagSet operator &(FlagSet x, FlagSet y) => new(x.a & y.a, x.b & y.b, x.c & y.c, x.d & y.d);
    public static FlagSet operator ~(FlagSet x) => new(~x.a, ~x.b, ~x.c, ~x.d);
    public static bool operator ==(FlagSet x, FlagSet y) => x.Equals(y);
    public static bool operator !=(FlagSet x, FlagSet y) => !x.Equals(y);

    public bool Equals(FlagSet other) => a == other.a && b == other.b && c == other.c && d == other.d;
    public override bool Equals(object? obj) => obj is FlagSet other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(a, b, c, d);
}

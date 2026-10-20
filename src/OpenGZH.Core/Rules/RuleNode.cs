namespace OpenGZH.Core.Rules;

/// <summary>
/// One node of a rules tree, mirroring YAML: a scalar string, an ordered mapping, or a sequence.
///
/// All game rules (units, weapons, locomotors, ...) are loaded into RuleNodes first and then
/// read by typed definitions. Keeping the raw tree around lets mods override any single field.
/// </summary>
public sealed class RuleNode
{
    public enum NodeKind { Scalar, Mapping, Sequence }

    public NodeKind Kind { get; private set; }

    /// <summary>Scalar text. Null for an explicit YAML null ("~"), which means "remove" when merging.</summary>
    public string? Value { get; private set; }

    readonly List<KeyValuePair<string, RuleNode>> entries = new();
    readonly List<RuleNode> items = new();

    /// <summary>Where this node came from, for error messages ("rules/usa/vehicles.yaml:120").</summary>
    public string Source { get; set; } = "";

    RuleNode(NodeKind kind) => Kind = kind;

    public static RuleNode Scalar(string? value) => new(NodeKind.Scalar) { Value = value };
    public static RuleNode Mapping() => new(NodeKind.Mapping);
    public static RuleNode Sequence() => new(NodeKind.Sequence);

    public bool IsScalar => Kind == NodeKind.Scalar;
    public bool IsMapping => Kind == NodeKind.Mapping;
    public bool IsSequence => Kind == NodeKind.Sequence;
    public bool IsNull => Kind == NodeKind.Scalar && Value == null;

    // ---- Mapping access ----

    public IReadOnlyList<KeyValuePair<string, RuleNode>> Entries => entries;
    public IEnumerable<string> Keys => entries.Select(e => e.Key);

    public RuleNode? this[string key] => TryGet(key, out var node) ? node : null;

    public bool TryGet(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RuleNode? node)
    {
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                node = entry.Value;
                return true;
            }
        }
        node = null;
        return false;
    }

    public bool Has(string key) => TryGet(key, out _);

    /// <summary>Adds or replaces a key in a mapping, keeping the original position when replacing.</summary>
    public RuleNode Set(string key, RuleNode value)
    {
        RequireKind(NodeKind.Mapping);
        for (int i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                entries[i] = new(entries[i].Key, value);
                return value;
            }
        }
        entries.Add(new(key, value));
        return value;
    }

    public RuleNode Set(string key, string? value) => Set(key, Scalar(value));

    public bool Remove(string key)
    {
        RequireKind(NodeKind.Mapping);
        return entries.RemoveAll(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    // ---- Sequence access ----

    public IReadOnlyList<RuleNode> Items => items;

    public RuleNode Add(RuleNode item)
    {
        RequireKind(NodeKind.Sequence);
        items.Add(item);
        return item;
    }

    /// <summary>
    /// Treats this node as a list: a sequence returns its items, anything else returns itself.
    /// Rules use this for fields that may appear once or many times (e.g. WeaponSet).
    /// </summary>
    public IEnumerable<RuleNode> AsList() => IsSequence ? items : [this];

    // ---- Scalar helpers ----

    public string GetString(string key, string fallback = "") =>
        TryGet(key, out var node) && node.IsScalar && node.Value != null ? node.Value : fallback;

    // ---- Merging ----

    /// <summary>
    /// Merges <paramref name="overlay"/> into this node, the way mods override rules:
    /// mappings merge key by key (recursively), "~" removes a key, scalars and sequences replace.
    /// </summary>
    public void MergeFrom(RuleNode overlay)
    {
        if (!IsMapping || !overlay.IsMapping)
            throw new InvalidOperationException("Only mappings can be merged.");

        foreach (var (key, value) in overlay.entries)
        {
            if (value.IsNull)
            {
                Remove(key);
                continue;
            }

            if (value.IsMapping && TryGet(key, out var existing) && existing.IsMapping)
                existing.MergeFrom(value);
            else
                Set(key, value.DeepClone());
        }
    }

    public RuleNode DeepClone()
    {
        var copy = new RuleNode(Kind) { Value = Value, Source = Source };
        foreach (var (key, value) in entries)
            copy.entries.Add(new(key, value.DeepClone()));
        foreach (var item in items)
            copy.items.Add(item.DeepClone());
        return copy;
    }

    void RequireKind(NodeKind kind)
    {
        if (Kind != kind)
            throw new InvalidOperationException($"Expected a {kind} node but this is a {Kind} ({Source}).");
    }

    public override string ToString() => Kind switch
    {
        NodeKind.Scalar => Value ?? "~",
        NodeKind.Mapping => $"{{{entries.Count} keys}}",
        _ => $"[{items.Count} items]",
    };
}

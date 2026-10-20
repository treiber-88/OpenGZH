namespace OpenGZH.Formats.Ini;

/// <summary>One "Key = Value" line inside an INI block.</summary>
public sealed record IniProperty(string Key, string Value, int Line);

/// <summary>
/// A block such as "Object AmericaTankCrusader ... End" or "Draw = W3DModelDraw ModuleTag_01 ... End".
/// Children keep their original order, mixing properties and nested blocks.
/// </summary>
public sealed class IniBlock
{
    /// <summary>The block keyword: "Object", "Weapon", "Draw", "ConditionState", ...</summary>
    public string Type { get; }

    /// <summary>Everything after the keyword (and after '=' when present), e.g. "W3DModelDraw ModuleTag_01".</summary>
    public string Name { get; }

    public int Line { get; }

    /// <summary>Properties and nested blocks in file order. Each item is an <see cref="IniProperty"/> or <see cref="IniBlock"/>.</summary>
    public List<object> Children { get; } = new();

    public IniBlock(string type, string name, int line)
    {
        Type = type;
        Name = name;
        Line = line;
    }

    public IEnumerable<IniProperty> Properties => Children.OfType<IniProperty>();
    public IEnumerable<IniBlock> Blocks => Children.OfType<IniBlock>();

    /// <summary>The value of the last property with this key (the original engine lets later lines overwrite earlier ones).</summary>
    public string? Get(string key)
    {
        string? value = null;
        foreach (var property in Properties)
            if (property.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                value = property.Value;
        return value;
    }

    public override string ToString() => $"{Type} {Name}";
}

public sealed class IniDocument
{
    public string SourcePath { get; }
    public List<IniBlock> Blocks { get; } = new();

    public IniDocument(string sourcePath) => SourcePath = sourcePath;
}

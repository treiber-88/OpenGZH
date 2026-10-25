namespace OpenGZH.TiberianDawn.Data;

/// <summary>
/// A Westwood-style INI file (the original's INIClass / WWGetPrivateProfileString): [Sections] of
/// key=value lines, names compared case-insensitively, ';' starts a comment. Keys keep file order.
/// </summary>
public sealed class TdIni
{
    readonly Dictionary<string, List<KeyValuePair<string, string>>> sections = new(StringComparer.OrdinalIgnoreCase);

    public TdIni(string text)
    {
        List<KeyValuePair<string, string>>? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            int comment = line.IndexOf(';');
            if (comment >= 0)
                line = line[..comment].Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('[') && line.Contains(']'))
            {
                string name = line[1..line.IndexOf(']')].Trim();
                if (!sections.TryGetValue(name, out current))
                    sections[name] = current = new();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || current == null)
                continue;
            current.Add(new(line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
    }

    public IEnumerable<string> SectionNames => sections.Keys;

    public bool HasSection(string section) => sections.ContainsKey(section);

    /// <summary>The entries of a section in file order (empty if the section is missing).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Section(string section) =>
        sections.TryGetValue(section, out var list) ? list : [];

    public string Get(string section, string key, string fallback = "")
    {
        foreach (var (k, v) in Section(section))
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return v;
        return fallback;
    }

    public int GetInt(string section, string key, int fallback = 0) =>
        int.TryParse(Get(section, key), out int value) ? value : fallback;

    /// <summary>The original's yes/no reading: true for "yes", "true", "1" (first letter y, t or 1).</summary>
    public bool GetBool(string section, string key, bool fallback = false)
    {
        string value = Get(section, key);
        return value.Length == 0 ? fallback : value[0] is 'y' or 'Y' or 't' or 'T' or '1';
    }
}

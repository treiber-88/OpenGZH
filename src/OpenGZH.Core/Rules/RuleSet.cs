using System.Text.RegularExpressions;
using OpenGZH.Core.Mods;

namespace OpenGZH.Core.Rules;

/// <summary>
/// All rules of a mod merged together: one mapping per section ("Objects", "Weapons", ...),
/// each holding named definitions.
///
/// Files load in the order the manifest lists them (base mods first). A definition that appears
/// again in a later file is merged into the earlier one, so a mod only needs to write the fields it changes:
///
///   Objects:
///     AmericaTankCrusader:
///       BuildCost: 800          # change one value
///       Modules:
///         ModuleTag_09: ~       # remove a module
/// </summary>
public sealed class RuleSet
{
    readonly RuleNode root = RuleNode.Mapping();

    public List<string> LoadedFiles { get; } = new();

    /// <summary>Returns a section such as "Objects" (empty if no file defines it).</summary>
    public RuleNode Section(string name) => root[name] ?? RuleNode.Mapping();

    public IEnumerable<string> SectionNames => root.Keys;

    /// <summary>Looks up one definition, e.g. Get("Weapons", "CrusaderTankGun").</summary>
    public RuleNode? Get(string section, string name) => Section(section)[name];

    /// <summary>Loads every rules file of a mod, its inherited mods first.</summary>
    public static RuleSet Load(ModManifest manifest, Func<string, ModManifest>? findMod = null)
    {
        var rules = new RuleSet();
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in manifest.InheritanceChain(findMod).Reverse())
            foreach (string pattern in mod.Rules)
                foreach (string file in ExpandPattern(mod.Folder, pattern))
                    if (loaded.Add(Path.GetFullPath(file)))
                        rules.LoadFile(file, $"{mod.Id}/{Path.GetRelativePath(mod.Folder, file).Replace('\\', '/')}");
        return rules;
    }

    public void LoadFile(string fullPath, string displayName)
    {
        var document = RuleYaml.Parse(File.ReadAllText(fullPath), displayName);
        Merge(document);
        LoadedFiles.Add(displayName);
    }

    /// <summary>Merges a parsed rules document (sections of named definitions) into this set.</summary>
    public void Merge(RuleNode document)
    {
        if (!document.IsMapping)
            throw new InvalidDataException($"{document.Source}: a rules file must be a mapping of sections.");

        foreach (var (sectionName, definitions) in document.Entries)
        {
            if (!root.TryGet(sectionName, out var section))
                section = root.Set(sectionName, RuleNode.Mapping());

            if (definitions.IsMapping)
                section.MergeFrom(definitions);
            else
                root.Set(sectionName, definitions.DeepClone()); // lists of plain values replace wholesale
        }
    }

    /// <summary>
    /// Expands a rules pattern relative to the mod folder. Supports "*" within a folder and "**/" for
    /// any depth. Files from one pattern load in sorted order, so results are the same on every platform.
    /// Files already matched by an earlier pattern are skipped.
    /// </summary>
    static IEnumerable<string> ExpandPattern(string modFolder, string pattern)
    {
        string normalized = pattern.Replace('\\', '/');
        int firstWildcard = normalized.IndexOfAny(['*', '?']);
        if (firstWildcard < 0)
        {
            string single = Path.Combine(modFolder, normalized);
            return File.Exists(single) ? [single] : [];
        }

        string baseDir = normalized[..firstWildcard];
        baseDir = baseDir.Contains('/') ? baseDir[..baseDir.LastIndexOf('/')] : "";
        string searchRoot = Path.Combine(modFolder, baseDir);
        if (!Directory.Exists(searchRoot))
            return [];

        var regex = new Regex("^" + Regex.Escape(normalized)
            .Replace(@"\*\*/", "(.+/)?")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]") + "$", RegexOptions.IgnoreCase);

        return Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Relative: Path.GetRelativePath(modFolder, f).Replace('\\', '/')))
            .Where(f => regex.IsMatch(f.Relative))
            .OrderBy(f => f.Relative.Count(c => c == '/'))
            .ThenBy(f => f.Relative, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.Full);
    }
}

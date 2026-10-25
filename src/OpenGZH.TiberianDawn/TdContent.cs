using OpenGZH.Core.FileSystem;
using OpenGZH.Core.Mods;
using OpenGZH.Core.Rules;

namespace OpenGZH.TiberianDawn;

/// <summary>The Tiberian Dawn mod: its mounted .mix archives and its rules.</summary>
public sealed class TdContent : IDisposable
{
    public ModManifest Manifest { get; }
    public VirtualFileSystem Files { get; }
    public RuleSet Rules { get; }

    TdContent(ModManifest manifest, VirtualFileSystem files, RuleSet rules)
    {
        Manifest = manifest;
        Files = files;
        Rules = rules;
    }

    /// <summary>Loads mods/&lt;id&gt; (default "tiberiandawn") from the OpenGZH root folder.</summary>
    public static TdContent Load(string root, string modId = "tiberiandawn")
    {
        var manifest = ModManifest.Load(Path.Combine(root, "mods", modId, "mod.yaml"));
        var files = manifest.MountPackages(root, id => ModManifest.Load(Path.Combine(root, "mods", id, "mod.yaml")));
        return new TdContent(manifest, files, RuleSet.Load(manifest));
    }

    /// <summary>A definition by its INI name (e.g. "MTNK") from a rules section such as "Vehicles".</summary>
    public RuleNode? Find(string section, string iniName) => Rules.Section(section).TryGet(iniName, out var node) ? node : null;

    /// <summary>The template with this TemplateType number (the value stored in a map's .BIN file).</summary>
    public RuleNode? TemplateById(int id)
    {
        templatesById ??= Rules.Section("Templates").Entries
            .Where(e => int.TryParse(e.Value.GetString("Id"), out _))
            .GroupBy(e => int.Parse(e.Value.GetString("Id")))
            .ToDictionary(g => g.Key, g => g.First().Value);
        return templatesById.GetValueOrDefault(id);
    }

    Dictionary<int, RuleNode>? templatesById;

    public void Dispose() => Files.Dispose();
}

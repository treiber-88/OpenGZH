using OpenGZH.Core.FileSystem;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OpenGZH.Core.Mods;

/// <summary>
/// Describes a mod: which packages to mount (in priority order) and which rules files to load.
/// Loaded from "mods/&lt;id&gt;/mod.yaml". The base game itself is the mod "zh".
/// </summary>
public sealed class ModManifest
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>Another mod this one builds on. Its packages and rules load after (i.e. below) this mod's.</summary>
    public string? Inherits { get; set; }

    /// <summary>
    /// Packages in priority order: the FIRST entry wins when two packages contain the same file.
    /// Paths are relative to the OpenGZH root folder. Entries ending in ".big" are archives;
    /// anything else is a folder. "folder@Prefix" mounts a folder under a virtual directory.
    /// </summary>
    public List<string> Packages { get; set; } = new();

    /// <summary>Rules files (YAML), loaded in order. Later files may override or extend earlier ones.</summary>
    public List<string> Rules { get; set; } = new();

    /// <summary>The folder this manifest was loaded from. Set by the loader.</summary>
    [YamlIgnore] public string Folder { get; set; } = "";

    public static ModManifest Load(string manifestPath)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var manifest = deserializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath));
        manifest.Folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        return manifest;
    }

    /// <summary>Opens every package listed by this manifest (and the mods it inherits) into a file system.</summary>
    public VirtualFileSystem MountPackages(string gameRoot, Func<string, ModManifest>? findMod = null)
    {
        var fileSystem = new VirtualFileSystem();
        foreach (var mod in InheritanceChain(findMod))
            foreach (string entry in mod.Packages)
                MountEntry(fileSystem, gameRoot, mod, entry);
        return fileSystem;
    }

    /// <summary>This mod first, then its parent, then the parent's parent...</summary>
    public IEnumerable<ModManifest> InheritanceChain(Func<string, ModManifest>? findMod)
    {
        var current = this;
        var seen = new HashSet<string>();
        while (true)
        {
            if (!seen.Add(current.Id))
                throw new InvalidDataException($"Mod inheritance loop at '{current.Id}'.");
            yield return current;
            if (current.Inherits == null || findMod == null)
                yield break;
            current = findMod(current.Inherits);
        }
    }

    static void MountEntry(VirtualFileSystem fileSystem, string gameRoot, ModManifest mod, string entry)
    {
        string spec = entry.Replace("{mod}", mod.Folder);
        string prefix = "";
        int at = spec.IndexOf('@');
        if (at >= 0)
        {
            prefix = spec[(at + 1)..];
            spec = spec[..at];
        }

        string fullPath = Path.IsPathRooted(spec) ? spec : Path.Combine(gameRoot, spec);
        bool optional = false;
        if (fullPath.EndsWith('?'))
        {
            optional = true;
            fullPath = fullPath[..^1];
        }

        if (fullPath.EndsWith(".big", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(fullPath))
                fileSystem.Mount(new BigArchive(fullPath));
            else if (!optional)
                throw new FileNotFoundException($"Mod '{mod.Id}' needs package '{entry}', which is missing. Run the content importer first.", fullPath);
        }
        else
        {
            if (Directory.Exists(fullPath))
                fileSystem.Mount(new FolderPackage(fullPath, prefix));
            else if (!optional)
                throw new DirectoryNotFoundException($"Mod '{mod.Id}' needs folder '{entry}', which is missing.");
        }
    }
}

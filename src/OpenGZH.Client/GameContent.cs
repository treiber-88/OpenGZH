using OpenGZH.Core.FileSystem;
using OpenGZH.Core.Mods;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.W3d;

namespace OpenGZH.Client;

/// <summary>
/// Everything loaded from a mod before any window exists: the file system, the rules and the
/// W3D asset library. Shared by the game, the map editor and the tools.
/// </summary>
public sealed class GameContent : IDisposable
{
    public string RootFolder { get; }
    public ModManifest Mod { get; }
    public VirtualFileSystem FileSystem { get; }
    public RuleSet Rules { get; }
    public W3dLibrary Models { get; }

    GameContent(string root, ModManifest mod, VirtualFileSystem fileSystem, RuleSet rules)
    {
        RootFolder = root;
        Mod = mod;
        FileSystem = fileSystem;
        Rules = rules;
        Models = new W3dLibrary(fileSystem);
    }

    /// <summary>Loads a mod by id (default "zh", the base game) from the "mods" folder.</summary>
    public static GameContent Load(string? modId = null, string? root = null)
    {
        root ??= FindRoot();
        ModManifest FindMod(string id) => ModManifest.Load(Path.Combine(root, "mods", id, "mod.yaml"));

        var mod = FindMod(modId ?? "zh");
        var fileSystem = mod.MountPackages(root, FindMod);
        var rules = RuleSet.Load(mod, FindMod);
        return new GameContent(root, mod, fileSystem, rules);
    }

    /// <summary>The OpenGZH folder: the nearest parent of the executable that contains "mods".</summary>
    public static string FindRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            string? dir = start;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "mods", "zh", "mod.yaml")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
        }
        throw new DirectoryNotFoundException("Could not find the OpenGZH folder (looked for mods/zh/mod.yaml above the program).");
    }

    public void Dispose() => FileSystem.Dispose();
}

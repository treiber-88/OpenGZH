using OpenGZH.Core.Mods;
using OpenGZH.Tools;

// OpenGZH developer tools. Run with no arguments for help.
//
//   OpenGZH.Tools import-rules [--force] Convert the original INI data into YAML rules files.
//   OpenGZH.Tools ini-check              Parse every original INI file and report problems.
//   OpenGZH.Tools list <dir> [ext]       List files in the virtual file system.
//   OpenGZH.Tools extract <path> <out>   Copy one file out of the virtual file system.

string root = GameRoot.Find();
string command = args.Length > 0 ? args[0] : "help";
string[] rest = args.Skip(1).ToArray();

var manifest = ModManifest.Load(Path.Combine(root, "mods", "zh", "mod.yaml"));
using var fileSystem = manifest.MountPackages(root);

return command switch
{
    "ini-check" => IniCheck.Run(fileSystem),
    "import-rules" => RulesImporter.Run(fileSystem, Path.Combine(root, "mods", "zh", "rules"), rest.Contains("--force")),
    "map-check" => MapCheck.Run(fileSystem, rest),
    "script-stats" => ScriptStats.Run(fileSystem, rest),
    "terrain-view" => TerrainView.Run(fileSystem, rest),
    "wnd-dump" => WndDump.Run(fileSystem, rest),
    "list" => FileTools.List(fileSystem, rest),
    "extract" => FileTools.Extract(fileSystem, rest),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        OpenGZH.Tools <command>
          import-rules           Convert original INI data into mods/zh/rules/*.yaml
          ini-check              Parse every original INI file and report problems
          map-check [filter]     Parse every map and report problems
          list <dir> [ext]       List files in the virtual file system
          extract <path> <out>   Copy a file out of the virtual file system
        """);
    return 1;
}

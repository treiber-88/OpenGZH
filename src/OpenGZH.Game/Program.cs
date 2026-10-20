using OpenGZH.Client;
using OpenGZH.Client.Platform;
using OpenGZH.Client.Screens;

// OpenGZH - an open, moddable engine for Command & Conquer Generals Zero Hour.
//
// Usage:
//   OpenGZH                                   start the game
//   OpenGZH --mod <id>                        start with a mod from mods/<id>
//   OpenGZH --view-model <name> [--anim <name>] [--house RRGGBB]
//                                             preview a W3D model
//   OpenGZH --view-map "Maps/<name>/<name>.map" preview a map
//   OpenGZH --skirmish "Maps/<name>/<name>.map" [--faction <PlayerTemplate>] [--enemy <PlayerTemplate>]
//   --screenshot <file.png>                   render a few frames, save a screenshot and quit

var options = CommandLine.Parse(args);
using var content = GameContent.Load(options.Get("mod"));

var window = new GameWindow("OpenGZH", visible: !options.Has("hidden"))
{
    ScreenshotPath = options.Get("screenshot"),
};

window.Run((gl, input, osWindow) =>
{
    if (options.Get("view-model") is { } modelName)
    {
        uint house = Convert.ToUInt32(options.Get("house") ?? "0", 16);
        return new ModelViewerScreen(gl, input, content, modelName, options.Get("anim"), house);
    }

    if (options.Get("view-map") is { } mapPath)
        return new MapViewerScreen(gl, input, content, mapPath, options.Has("topdown"));

    // A quick skirmish against the computer: --skirmish "Maps/<name>/<name>.map" [--faction FactionAmerica] [--enemy FactionChina]
    string skirmishMap = options.Get("skirmish") ?? "Maps/Tournament Desert/Tournament Desert.map";
    var map = OpenGZH.Formats.Maps.MapFile.Read(content.FileSystem.ReadAllBytes(skirmishMap));
    var game = OpenGZH.Logic.GameSetup.FromMap(map, content.Rules, (ulong)Environment.TickCount64,
    [
        new OpenGZH.Logic.PlayerSlot("Player", options.Get("faction") ?? "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x2050FF),
        new OpenGZH.Logic.PlayerSlot("Enemy", options.Get("enemy") ?? "FactionChina", IsHuman: false, StartPosition: 2, Color: 0xE02020),
    ], models: content.Models);
    var ui = new OpenGZH.Client.UI.UiResources(gl, content);
    var screen = new OpenGZH.Client.InGame.GameScreen(gl, input, ui, game, map);
    if (options.Get("select") is { } selectTemplate)
        screen.SelectFirst(selectTemplate);
    return screen;
});

/// <summary>Minimal "--key value" / "--flag" command line parser.</summary>
sealed class CommandLine
{
    readonly Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
                continue;
            string key = args[i][2..];
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            result.values[key] = value;
        }
        return result;
    }

    public bool Has(string key) => values.ContainsKey(key);
    public string? Get(string key) => values.GetValueOrDefault(key);
}

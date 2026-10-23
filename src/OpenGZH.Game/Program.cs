using OpenGZH.Client;
using OpenGZH.Client.Platform;
using OpenGZH.Client.Screens;

// OpenGZH - an open, moddable engine for Command & Conquer Generals Zero Hour.
//
// Usage:
//   OpenGZH                                   start the game at the main menu
//   OpenGZH --mod <id>                        start with a mod from mods/<id>
//   OpenGZH --view-model <name> [--anim <name>] [--house RRGGBB]
//                                             preview a W3D model
//   OpenGZH --view-map "Maps/<name>/<name>.map" preview a map
//   OpenGZH --skirmish "Maps/<name>/<name>.map" [--faction <PlayerTemplate>] [--enemy <PlayerTemplate>]
//   --open-menu <Menus/X.wnd>                 start at this shell menu
//   --click "<Menus/X.wnd:Button>@<seconds>;..." press shell buttons at those times
//   --end-game <VICTORY|DEFEAT>@<seconds>     end the game that way (to see the score screen)
//   --screenshot <file.png>                   render a few frames, save a screenshot and quit
//   --screenshot-frames <n>                   ... after n rendered frames instead of 5
//   --reveal                                  (skirmish) no shroud
//   --look-at <x,y>                           (skirmish) start the camera over this map point
//   --demo-fx <FXList>                        (skirmish) keep playing an FX list at the screen centre

var options = CommandLine.Parse(args);
using var content = GameContent.Load(options.Get("mod"));

var window = new GameWindow("OpenGZH", visible: !options.Has("hidden"))
{
    ScreenshotPath = options.Get("screenshot"),
    ScreenshotAfterFrames = int.TryParse(options.Get("screenshot-frames"), out int shotFrames) ? shotFrames : 5,
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

    // Without a skirmish (or one of its debug options) the game starts at the main menu.
    if (!options.Has("skirmish") && !options.Has("demo-fx") && !options.Has("demo-drive") && !options.Has("select") && !options.Has("look-at"))
    {
        OpenGZH.Client.Shell.MainMenu.RevealAtStart = options.Has("menu-shown");
        OpenGZH.Client.Shell.ShellScreen.DebugStartMenu = options.Get("open-menu");
        // Debug: --end-game "VICTORY@5" ends the next game that way after 5 seconds.
        if (options.Get("end-game") is { } endGame && endGame.Split('@') is [var endAction, var endAt]
            && float.TryParse(endAt, System.Globalization.CultureInfo.InvariantCulture, out float endSeconds))
            OpenGZH.Client.InGame.GameScreen.DebugEndGame = (endAction.ToUpperInvariant(), endSeconds);
        // Debug: --click "Menus/X.wnd:ButtonName@2.5;..." presses buttons at those times (seconds).
        foreach (var click in (options.Get("click") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (click.LastIndexOf('@') is int at and > 0 && float.TryParse(click[(at + 1)..], System.Globalization.CultureInfo.InvariantCulture, out float when))
                OpenGZH.Client.Shell.ShellScreen.DebugClicks.Enqueue((click[..at], when));
        return new OpenGZH.Client.Shell.ShellScreen(gl, input, osWindow, new OpenGZH.Client.UI.UiResources(gl, content));
    }

    // A quick skirmish against the computer: --skirmish "Maps/<name>/<name>.map" [--faction FactionAmerica] [--enemy FactionChina]
    string skirmishMap = options.Get("skirmish") ?? "Maps/Tournament Desert/Tournament Desert.map";
    var map = OpenGZH.Formats.Maps.MapFile.Read(content.FileSystem.ReadAllBytes(skirmishMap));
    var game = OpenGZH.Logic.GameSetup.FromMap(map, content.Rules, (ulong)Environment.TickCount64,
    [
        new OpenGZH.Logic.PlayerSlot("Player", options.Get("faction") ?? "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x2050FF),
        new OpenGZH.Logic.PlayerSlot("Enemy", options.Get("enemy") ?? "FactionChina", IsHuman: false, StartPosition: 2, Color: 0xE02020),
    ], models: content.Models, readFile: p => content.FileSystem.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    if (options.Has("reveal"))
        foreach (var player in game.Players.All)
            game.Shroud.RevealMapForPlayerPermanently(player);
    var ui = new OpenGZH.Client.UI.UiResources(gl, content);
    var screen = new OpenGZH.Client.InGame.GameScreen(gl, input, ui, game, map);
    if (options.Get("select") is { } selectTemplate)
        screen.SelectFirst(selectTemplate);
    if (options.Get("look-at") is { } lookAt && lookAt.Split(',') is [var lx, var ly]
        && float.TryParse(lx, System.Globalization.CultureInfo.InvariantCulture, out float x) && float.TryParse(ly, System.Globalization.CultureInfo.InvariantCulture, out float y))
        screen.LookAt(new System.Numerics.Vector3(x, y, 0));
    if (options.Get("demo-fx") is { } demoFx)
        screen.DemoFx(demoFx);
    // Debug: --demo-drive "AmericaTankCrusader,AmericaVehicleHumvee" spawns the vehicles next to the
    // player's command center and drives them off together (for checking treads, wheels and body motion).
    if (options.Get("demo-drive") is { } demoDrive)
    {
        var player = game.Players.Find("Player")!;
        var home = game.Objects.First(o => o.Owner == player && o.IsKindOf("COMMANDCENTER")).Position;
        var units = demoDrive.Split(',').Select((name, i) =>
            game.CreateObject(name.Trim(), player, home + new System.Numerics.Vector3(-120 + i * 45, -140, 0))).OfType<OpenGZH.Logic.Objects.GameObject>().ToList();
        foreach (var unit in units)
            unit.AI?.MoveTo(unit.Position + new System.Numerics.Vector3(0, -900, 0));
        screen.LookAt(home + new System.Numerics.Vector3(0, -260, 0));
    }
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

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
//   --perf                                    print frame timings and the slowest logic parts every 5 s
//   --td-map <SCG01EA>                        show a Tiberian Dawn scenario (PageUp/PageDown: next ones)
//   --td-skirmish [SCM01EA] [--side gdi|nod] [--enemy gdi|nod] [--opponents n]
//                                             Tiberian Dawn skirmish (setup screen, or straight into a map)
//   --screenshot <file.png>                   render a few frames, save a screenshot and quit
//   --screenshot-frames <n>                   ... after n rendered frames instead of 5
//   --reveal                                  (skirmish) no shroud
//   --look-at <x,y>                           (skirmish) start the camera over this map point
//   --demo-fx <FXList>                        (skirmish) keep playing an FX list at the screen centre

var options = CommandLine.Parse(args);

// Tiberian Dawn: --td-map SCG01EA shows one of its original scenarios (PageUp/PageDown for the others).
if (options.Get("td-map") is { } tdMap)
{
    using var td = OpenGZH.TiberianDawn.TdContent.Load(GameContent.FindRoot());
    var tdWindow = new GameWindow("OpenGZH - Tiberian Dawn", visible: !options.Has("hidden"))
    {
        ScreenshotPath = options.Get("screenshot"),
        ScreenshotAfterFrames = int.TryParse(options.Get("screenshot-frames"), out int tdFrames) ? tdFrames : 5,
    };
    tdWindow.Run((gl, input, _) => new OpenGZH.Client.TiberianDawn.TdMapScreen(gl, input, td, tdMap));
    return;
}

// Tiberian Dawn skirmish: --td-skirmish opens its setup; --td-skirmish SCM01EA [--side gdi|nod] [--enemy gdi|nod]
// [--opponents n] starts that map at once.
if (options.Has("td-skirmish"))
{
    var td = TiberianDawnContent.Get() ?? throw new InvalidOperationException("Tiberian Dawn's files are not in content/tiberiandawn.");
    var tdWindow = new GameWindow("OpenGZH - Tiberian Dawn", visible: !options.Has("hidden"))
    {
        ScreenshotPath = options.Get("screenshot"),
        ScreenshotAfterFrames = int.TryParse(options.Get("screenshot-frames"), out int tdFrames) ? tdFrames : 5,
    };
    tdWindow.Run((gl, input, _) =>
    {
        if (options.Get("td-skirmish") is not { } tdMap)
            return new OpenGZH.Client.TiberianDawn.TdSkirmishSetupScreen(gl, input, td);
        static OpenGZH.TiberianDawn.Logic.HousesType Side(string? s, OpenGZH.TiberianDawn.Logic.HousesType fallback) =>
            s?.ToLowerInvariant() switch { "gdi" => OpenGZH.TiberianDawn.Logic.HousesType.HOUSE_GOOD, "nod" => OpenGZH.TiberianDawn.Logic.HousesType.HOUSE_BAD, _ => fallback };
        var side = Side(options.Get("side"), OpenGZH.TiberianDawn.Logic.HousesType.HOUSE_GOOD);
        var enemy = Side(options.Get("enemy"), side == OpenGZH.TiberianDawn.Logic.HousesType.HOUSE_GOOD ? OpenGZH.TiberianDawn.Logic.HousesType.HOUSE_BAD : OpenGZH.TiberianDawn.Logic.HousesType.HOUSE_GOOD);
        int opponents = int.TryParse(options.Get("opponents"), out int n) ? Math.Clamp(n, 1, 5) : 1;
        var game = new OpenGZH.TiberianDawn.Logic.TdGame(new OpenGZH.TiberianDawn.Logic.Types.TdTypes(td.Rules));
        var players = new List<OpenGZH.TiberianDawn.Logic.TdSkirmishPlayer> { new("Player", side, OpenGZH.TiberianDawn.Logic.PlayerColorType.REMAP_GOLD, false, 0) };
        for (int i = 0; i < opponents; i++)
            players.Add(new($"Computer {i + 1}", enemy, (OpenGZH.TiberianDawn.Logic.PlayerColorType)(1 + i), true, i + 1));
        game.Set_Multiplayer_Data(new OpenGZH.TiberianDawn.Logic.TdSkirmishOptions(), players);
        string ini = System.Text.Encoding.Latin1.GetString(td.Files.ReadAllBytes(tdMap + ".INI"));
        byte[]? bin = td.Files.Exists(tdMap + ".BIN") ? td.Files.ReadAllBytes(tdMap + ".BIN") : null;
        game.Start_Skirmish(tdMap.ToUpperInvariant(), ini, bin, uint.TryParse(options.Get("seed"), out uint seed) ? seed : (uint)Environment.TickCount);
        // Debug: --reveal shows the whole map; --deploy deploys the player's MCV at once;
        // --fast <seconds> runs the logic that long before showing the game.
        game.DebugUnshroud = options.Has("reveal");
        if (options.Has("deploy"))
            foreach (var mcv in game.Units.Active.Where(u => u.House == game.PlayerPtr && u.Class.Type == OpenGZH.TiberianDawn.Logic.UnitType.UNIT_MCV).ToList())
                game.Queue_Mission(mcv.As_Target(), OpenGZH.TiberianDawn.Logic.MissionType.MISSION_UNLOAD, 0, 0);
        if (int.TryParse(options.Get("fast"), out int fastSeconds))
            for (int f = 0; f < fastSeconds * 15; f++)
                game.Main_Loop();
        var tdScreen = new OpenGZH.Client.TiberianDawn.TdGameScreen(gl, input, td, game,
            () => new OpenGZH.Client.TiberianDawn.TdSkirmishSetupScreen(gl, input, td, null, tdMap));
        if (options.Get("look-at") is { } who && who == "enemy")
            tdScreen.LookAtEnemy();
        return tdScreen;
    });
    return;
}

using var content = GameContent.Load(options.Get("mod"));

var window = new GameWindow("OpenGZH", visible: !options.Has("hidden"))
{
    ScreenshotPath = options.Get("screenshot"),
    ScreenshotAfterFrames = int.TryParse(options.Get("screenshot-frames"), out int shotFrames) ? shotFrames : 5,
    PerfReport = options.Has("perf"),
};
OpenGZH.Logic.GameProfiler.Enabled = options.Has("perf");

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
    // Debug: --deploy unfolds a Tiberian Dawn player's MCV at once; --fast N plays N seconds first.
    if (options.Has("deploy"))
        foreach (var mcv in game.Objects.Where(o => o.Owner == game.Players.Find("Player")).ToList())
            mcv.FindModule<OpenGZH.Logic.Modules.Behaviors.TdDeployToStructure>()?.Deploy();
    // Debug: --fight "TDGDI_MTNK,ChinaTankBattleMaster" puts the player's and the enemy's units face to face
    // in the middle of the map (a few of each) and the camera on them.
    System.Numerics.Vector3? fightAt = null;
    if (options.Get("fight") is { } fight && fight.Split(',') is [var mineName, var theirsName])
    {
        var middle = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        for (int i = 0; i < 3; i++)
        {
            var a = middle + new System.Numerics.Vector3(-70, (i - 1) * 40, 0);
            var b = middle + new System.Numerics.Vector3(70, (i - 1) * 40, 0);
            game.CreateObject(mineName.Trim(), game.Players.Find("Player")!, a with { Z = game.Terrain.GroundHeight(a) });
            game.CreateObject(theirsName.Trim(), game.Players.Find("Enemy")!, b with { Z = game.Terrain.GroundHeight(b) });
        }
        fightAt = middle;
    }
    if (int.TryParse(options.Get("fast"), out int fastSeconds))
        for (int i = 0; i < fastSeconds * OpenGZH.Logic.Core.LogicTime.FramesPerSecond; i++)
            game.Update();
    var ui = new OpenGZH.Client.UI.UiResources(gl, content);
    var screen = new OpenGZH.Client.InGame.GameScreen(gl, input, ui, game, map);
    if (fightAt is { } fightPoint)
        screen.LookAt(fightPoint);
    if (options.Get("look-at") is "player" or "enemy")
    {
        var who = game.Players.Find(options.Get("look-at") == "player" ? "Player" : "Enemy");
        if (who?.StartPosition is { } start)
            screen.LookAt(start);
    }
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

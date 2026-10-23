using OpenGZH.Client.InGame;
using OpenGZH.Client.UI;
using OpenGZH.Formats.Maps;
using OpenGZH.Logic;
using OpenGZH.Logic.Scripting;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace OpenGZH.Client.Shell;

/// <summary>Builds games from maps and the game screens to play them in (missions and skirmishes).</summary>
public sealed class GameLauncher(GL gl, IInputContext input, IWindow window, UiResources ui)
{
    MapFile ReadMap(string map) => MapFile.Read(ui.Content.FileSystem.ReadAllBytes(map.Replace('\\', '/')));

    byte[]? ReadFile(string path) => ui.Content.FileSystem.Exists(path) ? ui.Content.FileSystem.ReadAllBytes(path) : null;

    /// <summary>A campaign or challenge mission: the map's own players and scripts.</summary>
    public GameScreen StartMission(string map, Difficulty difficulty)
    {
        var mapFile = ReadMap(map);
        var game = GameSetup.FromMap(mapFile, ui.Content.Rules, (ulong)Environment.TickCount64, difficulty: difficulty,
            models: ui.Content.Models, readFile: ReadFile);
        return new GameScreen(gl, input, ui, game, mapFile);
    }

    /// <summary>A skirmish (or LAN) game: one side per slot plus the standard skirmish scripts.</summary>
    public GameScreen StartSkirmish(string map, IReadOnlyList<PlayerSlot> slots, int startingCash, ulong? seed = null)
    {
        var mapFile = ReadMap(map);
        var game = GameSetup.FromMap(mapFile, ui.Content.Rules, seed ?? (ulong)Environment.TickCount64, slots, startingCash,
            models: ui.Content.Models, readFile: ReadFile);
        return new GameScreen(gl, input, ui, game, mapFile);
    }

    /// <summary>A skirmish set up in the menus: random choices are settled as the game starts.</summary>
    public GameScreen StartSkirmish(Logic.Multiplayer.GameInfo info, Logic.Multiplayer.MapCache mapCache)
    {
        var mapFile = ReadMap(info.Map);
        var game = GameSetup.FromGameInfo(mapFile, ui.Content.Rules, info, mapCache, ui.Content.Models, ReadFile);
        return new GameScreen(gl, input, ui, game, mapFile);
    }

    public IWindow Window => window;
}

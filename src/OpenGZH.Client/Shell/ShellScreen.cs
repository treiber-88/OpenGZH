using System.Numerics;
using OpenGZH.Client.InGame;
using OpenGZH.Client.Platform;
using OpenGZH.Client.UI;
using OpenGZH.Client.UI.Gui;
using OpenGZH.Client.World;
using OpenGZH.Logic;
using OpenGZH.Logic.Scripting;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace OpenGZH.Client.Shell;

/// <summary>
/// The front end: the menus (<see cref="Shell"/>) over the shell map, a scripted battle playing
/// behind them (GameData ShellMapName, run in the original's GAME_SHELL mode).
/// </summary>
public sealed class ShellScreen : IScreen
{
    readonly GL gl;
    readonly IInputContext input;
    readonly IWindow window;
    readonly List<Action> unsubscribe = new();
    int width = 1280, height = 720;
    Vector2 mouse;

    public UiResources Ui { get; }
    public GuiManager Gui { get; } = new();
    public TransitionHandler Transitions { get; }
    public Shell Shell { get; }
    public CampaignManager Campaigns { get; }
    public GameLauncher Launcher { get; }

    // The shell map.
    GameLogic? shellGame;
    WorldView? shellWorld;
    ScriptDirector? director;
    Audio.GameAudio? shellAudio;

    /// <summary>The game that just ended, for the score screen (null when the shell starts fresh).</summary>
    public FinishedGame? LastGame { get; }

    public ShellScreen(GL gl, IInputContext input, IWindow window, UiResources ui, CampaignManager? campaigns = null,
        FinishedGame? finishedGame = null)
    {
        LastGame = finishedGame;
        this.gl = gl;
        this.input = input;
        this.window = window;
        Ui = ui;
        Launcher = new GameLauncher(gl, input, window, ui);
        Campaigns = campaigns ?? new CampaignManager(ui.Content.Rules.Section("Campaigns"));
        Transitions = new TransitionHandler(ui.Content.Rules.Section("WindowTransitions"))
        {
            FindWindow = Gui.Find,
            PlaySound = name => ui.Audio.AddAudioEvent(name),
            GetText = w => w.Text ?? ui.Gui.Text(w.Definition.Text),
        };
        Shell = new Shell(this);
        Shell.Register("Menus/MainMenu.wnd", (s, l) => new MainMenu(s, l));
        Shell.Register("Menus/SkirmishGameOptionsMenu.wnd", (s, l) => new SkirmishGameOptionsMenu(s, l));
        Shell.Register("Menus/ScoreScreen.wnd", (s, l) => new ScoreScreen(s, l));

        foreach (var m in input.Mice)
        {
            Action<IMouse, Vector2> move = (_, p) => OnMouseMove(p);
            Action<IMouse, MouseButton> down = (_, b) => { if (b == MouseButton.Left) Gui.MouseDown(mouse); };
            Action<IMouse, MouseButton> up = (_, b) => { if (b == MouseButton.Left) Gui.MouseUp(mouse); };
            Action<IMouse, ScrollWheel> scroll = (_, w) => Gui.Scroll(mouse, w.Y);
            m.MouseMove += move;
            m.MouseDown += down;
            m.MouseUp += up;
            m.Scroll += scroll;
            unsubscribe.Add(() => { m.MouseMove -= move; m.MouseDown -= down; m.MouseUp -= up; m.Scroll -= scroll; });
        }
        foreach (var k in input.Keyboards)
        {
            Action<IKeyboard, char> ch = (_, c) => OnChar(c);
            k.KeyChar += ch;
            unsubscribe.Add(() => k.KeyChar -= ch);
        }

        if (finishedGame != null)
        {
            // The original's clearGameData: the score screen goes on top of the menus the game was started from.
            Shell.Restore(finishedGame.ShellStack);
            Transitions.SetGroup("FadeWholeScreen");
            Shell.Push("Menus/ScoreScreen.wnd");
            Transitions.Reverse("FadeWholeScreen");
        }
        else
            Shell.Push(DebugStartMenu ?? "Menus/MainMenu.wnd");
    }

    /// <summary>Debug: open this menu instead of the main menu (e.g. "Menus/SkirmishGameOptionsMenu.wnd").</summary>
    public static string? DebugStartMenu { get; set; }

    /// <summary>Debug: "Window.wnd:Name@seconds" entries clicked in order once each time is reached.</summary>
    public static Queue<(string Window, float At)> DebugClicks { get; } = new();
    float debugClock;

    void OnMouseMove(Vector2 point)
    {
        mouse = point;
        if (Shell.Top?.OnMouseMove(point) == true)
            return;
        Gui.MouseMove(point);
    }

    void OnChar(char c)
    {
        if (Shell.Top?.OnKey(c) == true)
            return;
        Gui.KeyChar(c);
    }

    /// <summary>Starts (or ends) the shell map behind the menus (the original's showShellMap).</summary>
    public void ShowShellMap(bool on)
    {
        if (!on)
        {
            shellWorld?.Dispose();
            shellWorld = null;
            shellGame = null;
            director = null;
            shellAudio = null;
            return;
        }
        if (shellGame != null)
            return;
        string map = ui.Content.Rules.Section("GameData").GetString("ShellMapName").Replace('\\', '/');
        if (map.Length == 0 || !Ui.Content.FileSystem.Exists(map))
            return;
        try
        {
            var mapFile = Formats.Maps.MapFile.Read(Ui.Content.FileSystem.ReadAllBytes(map));
            shellGame = GameSetup.FromMap(mapFile, Ui.Content.Rules, seed: 0, models: Ui.Content.Models,
                readFile: p => Ui.Content.FileSystem.Exists(p) ? Ui.Content.FileSystem.ReadAllBytes(p) : null);
            shellGame.IsShellGame = true;
            var viewer = shellGame.Players.Local ?? shellGame.Players.All.First(p => !p.IsNeutral);
            shellWorld = new WorldView(gl, Ui.Content, shellGame, mapFile, Ui.Textures) { Viewer = viewer };
            shellGame.Shroud.RevealMapForPlayerPermanently(viewer);
            director = new ScriptDirector(shellGame, shellWorld, Ui, viewer);
            shellAudio = new Audio.GameAudio(Ui.Audio, shellGame, shellWorld, viewer);
            shellWorld.LookAt(new Vector3(shellGame.Terrain.WorldWidth / 2, shellGame.Terrain.WorldHeight / 2, 0));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Shell map failed to start: {e.Message}");
            shellGame = null;
            shellWorld = null;
        }
    }

    UiResources ui => Ui;

    /// <summary>Leaves the shell for a game screen.</summary>
    public void SwitchTo(IScreen next)
    {
        Shell.Reset();
        GameWindow.Current?.Switch(next);
    }

    public void Quit() => window.Close();

    /// <summary>The difficulty picked for the Generals Challenge.</summary>
    public Difficulty ChallengeDifficulty { get; set; } = Difficulty.Normal;

    GuiLayout? optionsLayout;

    /// <summary>The options popup (the original's OptionsMenu.wnd layout, kept outside the menu stack).</summary>
    public void ShowOptions()
    {
        if (optionsLayout == null)
        {
            optionsLayout = GuiLayout.Load(Ui.Content.FileSystem, "Window/Menus/OptionsMenu.wnd");
            foreach (string close in new[] { "ButtonBack", "ButtonAccept" })
                if (optionsLayout.Find<GuiPushButton>(close) is { } button)
                    button.Clicked += _ =>
                    {
                        if (optionsLayout != null)
                            Gui.Remove(optionsLayout);
                    };
        }
        Gui.Remove(optionsLayout);
        Gui.Push(optionsLayout);
        optionsLayout.Layout(Ui.Gui.Scale);
        optionsLayout.Hidden = false;
    }

    // ---- Skirmish ----

    /// <summary>The skirmish being set up (the original's TheSkirmishGameInfo).</summary>
    public Logic.Multiplayer.GameInfo? SkirmishGameInfo { get; set; }

    Logic.Multiplayer.MapCache? mapCache;
    public Logic.Multiplayer.MapCache MapCache => mapCache ??= Logic.Multiplayer.MapCache.Load(Ui.Content.FileSystem);

    readonly Dictionary<string, Graphics.Texture?> previews = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The map's picture: the .tga beside the .map (the original's getMapPreviewImage).</summary>
    public Graphics.Texture? MapPreview(string map)
    {
        if (map.Length == 0)
            return null;
        if (!previews.TryGetValue(map, out var texture))
        {
            string tga = Path.ChangeExtension(map.Replace('\\', '/'), ".tga");
            texture = Ui.Content.FileSystem.Exists(tga) ? Ui.Textures.Get(tga, repeat: false) : null;
            previews[map] = texture;
        }
        return texture;
    }

    /// <summary>A message box with an OK button (the original's MessageBoxOk).</summary>
    public void ShowMessageBox(string title, string message)
    {
        var box = GuiLayout.Load(Ui.Content.FileSystem, "Window/Menus/MessageBox.wnd");
        if (box.Find("MessageBox.wnd:StaticTextTitle") is { } t)
            t.Text = title;
        if (box.Find("MessageBox.wnd:StaticTextMessage") is { } m)
            m.Text = message;
        foreach (string hide in new[] { "ButtonNo", "ButtonYes", "ButtonCancel" })
            if (box.Find($"MessageBox.wnd:{hide}") is { } b)
                b.Hidden = true;
        if (box.Find<GuiPushButton>("MessageBox.wnd:ButtonOk") is { } ok)
            ok.Clicked += _ => Gui.Remove(box);
        Gui.Push(box);
        box.Layout(Ui.Gui.Scale);
    }

    /// <summary>
    /// The map list popup (the original's SkirmishMapSelectMenu): multiplayer maps by player count and
    /// name, with a preview; OK picks the highlighted map.
    /// </summary>
    public GuiLayout OpenSkirmishMapSelect(string currentMap, Action<string> onSelect, Action onCancel)
    {
        const string L = "SkirmishMapSelectMenu.wnd";
        var popup = GuiLayout.Load(Ui.Content.FileSystem, $"Window/Menus/{L}");
        // Skipped like the original: two Generals maps that Zero Hour does not list.
        string[] excluded = ["maps/armored fury/armored fury.map", "maps/scorched earth/scorched earth.map"];
        var maps = MapCache.Maps
            .Where(m => m.IsMultiplayer && m.NameLookupTag.Length > 0 && !excluded.Contains(m.FileName))
            .OrderBy(m => m.NumPlayers).ThenBy(m => Ui.Strings.Get(m.NameLookupTag), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var list = popup.Find<GuiListBox>($"{L}:ListboxMap");
        var preview = popup.Find($"{L}:WinMapPreview");
        var startButtons = Enumerable.Range(0, Logic.Multiplayer.GameInfo.MaxSlots).Select(i => popup.Find($"{L}:ButtonMapStartPosition{i}")).ToList();
        string selected = currentMap;
        if (list != null)
        {
            list.Items.Clear();
            foreach (var map in maps)
                list.Items.Add(Ui.Strings.Get(map.NameLookupTag));
            list.SelectedIndex = Math.Max(0, maps.FindIndex(m => m.FileName.Equals(Logic.Multiplayer.MapCache.Key(currentMap), StringComparison.OrdinalIgnoreCase)));
            if (maps.Count > 0)
                selected = maps[list.SelectedIndex].FileName;
            list.Clicked += _ =>
            {
                if (list.SelectedIndex >= 0 && list.SelectedIndex < maps.Count)
                    selected = maps[list.SelectedIndex].FileName;
            };
        }
        if (preview != null)
            preview.CustomDraw = (w, context) =>
            {
                var meta = MapCache.Find(selected);
                MapPreviewDrawer.Draw(w, context, meta, MapPreview(selected));
                for (int i = 0; i < startButtons.Count; i++)
                    if (startButtons[i] is { } b)
                    {
                        b.Hidden = meta == null || i >= meta.NumPlayers;
                        b.Enabled = false;
                    }
                if (meta != null)
                    MapPreviewDrawer.PositionStartButtons(w, meta, startButtons);
            };
        if (popup.Find<GuiPushButton>($"{L}:ButtonOK") is { } okButton)
            okButton.Clicked += _ => onSelect(selected);
        if (popup.Find<GuiPushButton>($"{L}:ButtonBack") is { } backButton)
            backButton.Clicked += _ => onCancel();
        Gui.Push(popup);
        popup.Layout(Ui.Gui.Scale);
        return popup;
    }

    /// <summary>Starts the skirmish set up in the menu (the original's reallyDoStart).</summary>
    public void StartSkirmish(Logic.Multiplayer.GameInfo info)
    {
        var screen = Launcher.StartSkirmish(info, MapCache);
        SkirmishGameInfo = null;
        Play(screen, info);
    }

    /// <summary>Starts the campaign's current mission (the original's doGameStart for GAME_SINGLE_PLAYER).</summary>
    public void StartCampaignMission(string map, Difficulty difficulty)
    {
        if (map.Length == 0)
            return;
        Play(Launcher.StartMission(map, difficulty), null);
    }

    /// <summary>Leaves for the game; when it ends, a new shell comes back with the score screen.</summary>
    void Play(GameScreen screen, Logic.Multiplayer.GameInfo? skirmish)
    {
        var stack = Shell.StackFiles;
        var campaigns = Campaigns;
        var game = screen.Game;
        if (game.Scripts is { } scripts)
            scripts.OnMissionEnded += victory => campaigns.IsVictorious = victory;
        screen.GameEnded = () =>
            GameWindow.Current?.Switch(new ShellScreen(gl, input, window, Ui, campaigns,
                new FinishedGame(game, game.Players.Local, skirmish, stack)));
        SwitchTo(screen);
    }

    public void Resize(int w, int h)
    {
        width = Math.Max(1, w);
        height = Math.Max(1, h);
        Ui.Resize(width, height);
        Gui.Layout(Ui.Gui.Scale);
        Transitions.ScreenSize = new Vector2(width, height);
    }

    public void Update(float seconds)
    {
        if (shellWorld != null)
        {
            shellWorld.ViewportSize = new Vector2(width, height);
            shellWorld.Update(seconds);
            director?.Update(seconds);
            shellAudio?.Update();
        }
        else
            Ui.Audio.Update();
        Shell.Update(seconds);
        Transitions.Update(seconds);
        debugClock += seconds;
        while (DebugClicks.Count > 0 && DebugClicks.Peek().At <= debugClock)
        {
            var (name, _) = DebugClicks.Dequeue();
            if (Gui.Find(name) is GuiPushButton button)
                button.SimulateClick();
            else
                Console.WriteLine($"--click: no button {name}");
        }
    }

    public void Render(float seconds)
    {
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        shellWorld?.Render(seconds, width / (float)height);
        var batch = Ui.Batch;
        batch.Begin(width, height);
        Gui.Draw(Ui.Gui);
        Transitions.Draw(Ui.Gui);
        batch.End();
    }

    public void Dispose()
    {
        foreach (var action in unsubscribe)
            action();
        shellWorld?.Dispose();
    }
}

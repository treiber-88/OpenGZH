using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.Platform;
using OpenGZH.Client.UI;
using OpenGZH.TiberianDawn;
using OpenGZH.TiberianDawn.Art;
using OpenGZH.TiberianDawn.Data;
using OpenGZH.TiberianDawn.Logic;
using OpenGZH.TiberianDawn.Logic.Types;
using Silk.NET.Input;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.TiberianDawn;

/// <summary>
/// Tiberian Dawn skirmish setup: pick one of the original multiplayer maps (with a preview), play as
/// GDI or Nod, choose how many computer opponents and their side, and the starting credits.
/// </summary>
public sealed class TdSkirmishSetupScreen : IScreen
{
    readonly GL gl;
    readonly IInputContext input;
    readonly TdContent content;
    readonly SpriteBatch batch;
    readonly FontLibrary fonts;
    readonly GameFont font, title;
    readonly Func<IScreen>? exitTo;
    readonly List<(string Root, string Name, int Starts)> maps = new();

    int mapIndex;
    HousesType side = HousesType.HOUSE_GOOD;
    int opponents = 1;
    int enemySide;                 // 0 = random, 1 = GDI, 2 = Nod
    int creditsIndex = 1;
    bool crates;
    static readonly int[] CreditChoices = [2500, 5000, 7500, 10000];
    Texture? preview;
    int width = 1280, height = 720;
    Vector2 mouse;
    readonly List<(Rect Area, Action Click)> buttons = new();

    public TdSkirmishSetupScreen(GL gl, IInputContext input, TdContent content, Func<IScreen>? exitTo = null, string? initialMap = null)
    {
        this.gl = gl;
        this.input = input;
        this.content = content;
        this.exitTo = exitTo;
        batch = new SpriteBatch(gl);
        fonts = new FontLibrary(gl, GameContent.FindRoot());
        font = fonts.Get("Arial", 12);
        title = fonts.Get("Arial", 24, bold: true);

        foreach (string root in TdScenario.List(content.Files).Where(n => char.ToUpperInvariant(n[2]) == 'M'))
        {
            var ini = new WwProfile(System.Text.Encoding.Latin1.GetString(content.Files.ReadAllBytes(root + ".INI")));
            string name = ini.Get_String("Basic", "Name", root, 64);
            int starts = 0;
            for (int i = 0; i < 6; i++)
                if (ini.Get_Int("Waypoints", i.ToString(), -1) != -1)
                    starts++;
            maps.Add((root, name, Math.Max(starts, 2)));
        }
        mapIndex = Math.Max(0, maps.FindIndex(m => m.Root.Equals(initialMap, StringComparison.OrdinalIgnoreCase)));
        LoadPreview();

        onMove = (_, p) => mouse = p;
        onDown = (_, b) =>
        {
            if (b != MouseButton.Left)
                return;
            foreach (var (area, click) in buttons.ToList())
                if (area.Contains(mouse))
                {
                    click();
                    return;
                }
        };
        onKey = (_, key, _) =>
        {
            if (key == Key.Escape && exitTo != null)
                GameWindow.Current?.Switch(exitTo());
            if (key == Key.Enter)
                Start();
        };
        foreach (var m in input.Mice)
        {
            m.MouseMove += onMove;
            m.MouseDown += onDown;
        }
        foreach (var k in input.Keyboards)
            k.KeyDown += onKey;
    }

    readonly Action<IMouse, Vector2> onMove;
    readonly Action<IMouse, MouseButton> onDown;
    readonly Action<IKeyboard, Key, int> onKey;

    void LoadPreview()
    {
        preview?.Dispose();
        preview = null;
        if (maps.Count == 0)
            return;
        var scenario = TdScenario.Load(content.Files, maps[mapIndex].Root);
        if (scenario != null)
            preview = new Texture(gl, new TdMapRenderer(content).Render(scenario), repeat: false, mipmaps: false, nearest: true);
        opponents = Math.Clamp(opponents, 1, maps[mapIndex].Starts - 1);
    }

    void Start()
    {
        if (maps.Count == 0)
            return;
        string root = maps[mapIndex].Root;
        var game = new TdGame(new TdTypes(content.Rules));
        var random = new Random();
        var players = new List<TdSkirmishPlayer>
        {
            new("Player", side, PlayerColorType.REMAP_GOLD, IsAI: false, Team: 0),
        };
        PlayerColorType[] colors = [PlayerColorType.REMAP_RED, PlayerColorType.REMAP_LTBLUE, PlayerColorType.REMAP_GREEN, PlayerColorType.REMAP_ORANGE, PlayerColorType.REMAP_BLUE];
        for (int i = 0; i < opponents; i++)
        {
            var enemy = enemySide switch
            {
                1 => HousesType.HOUSE_GOOD,
                2 => HousesType.HOUSE_BAD,
                _ => random.Next(2) == 0 ? HousesType.HOUSE_GOOD : HousesType.HOUSE_BAD,
            };
            players.Add(new TdSkirmishPlayer($"Computer {i + 1}", enemy, colors[i % colors.Length], IsAI: true, Team: i + 1));
        }
        game.Set_Multiplayer_Data(new TdSkirmishOptions { Credits = CreditChoices[creditsIndex], Crates = crates }, players);
        string ini = System.Text.Encoding.Latin1.GetString(content.Files.ReadAllBytes(root + ".INI"));
        byte[]? bin = content.Files.Exists(root + ".BIN") ? content.Files.ReadAllBytes(root + ".BIN") : null;
        if (!game.Start_Skirmish(root, ini, bin, (uint)Environment.TickCount))
            return;
        var exit = exitTo;
        var self = (gl, input, content);
        GameWindow.Current?.Switch(new TdGameScreen(gl, input, content, game,
            () => new TdSkirmishSetupScreen(self.gl, self.input, self.content, exit, root)));
    }

    public void Resize(int w, int h)
    {
        width = Math.Max(1, w);
        height = Math.Max(1, h);
        gl.Viewport(0, 0, (uint)width, (uint)height);
    }

    public void Update(float seconds) { }

    bool Button(Rect r, string text, bool selected = false, Action? click = null)
    {
        bool hover = r.Contains(mouse);
        batch.FillRect(r, selected ? new Vector4(0.45f, 0.35f, 0.1f, 1) : hover ? new Vector4(0.3f, 0.3f, 0.25f, 1) : new Vector4(0.18f, 0.18f, 0.16f, 1));
        batch.DrawRect(r, new Vector4(0.7f, 0.6f, 0.3f, 1));
        font.Draw(batch, text, new Vector2(r.X + (r.Width - font.Measure(text)) / 2, r.Y + (r.Height - font.LineHeight) / 2), new Vector4(1, 1, 1, 1));
        if (click != null)
            buttons.Add((r, click));
        return hover;
    }

    public void Render(float seconds)
    {
        buttons.Clear();
        gl.ClearColor(0.05f, 0.05f, 0.04f, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        batch.Begin(width, height);
        var gold = new Vector4(0.95f, 0.8f, 0.3f, 1);
        var text = new Vector4(0.9f, 0.9f, 0.85f, 1);
        title.Draw(batch, "TIBERIAN DAWN SKIRMISH", new Vector2(40, 24), gold);

        // The maps.
        float y = 80;
        font.Draw(batch, "Map", new Vector2(40, y), gold);
        y += 24;
        for (int i = 0; i < maps.Count; i++)
        {
            int index = i;
            Button(new Rect(40, y, 300, 24), $"{maps[i].Name}  ({maps[i].Starts} players)", i == mapIndex, () => { mapIndex = index; LoadPreview(); });
            y += 27;
        }

        // The preview.
        if (preview != null)
        {
            float s = Math.Min(420f / preview.Width, 420f / preview.Height);
            batch.DrawTexture(preview, new Rect(380, 104, preview.Width * s, preview.Height * s));
        }

        // The options.
        float x = 840;
        y = 104;
        font.Draw(batch, "Your side", new Vector2(x, y), gold);
        Button(new Rect(x, y + 24, 120, 30), "GDI", side == HousesType.HOUSE_GOOD, () => side = HousesType.HOUSE_GOOD);
        Button(new Rect(x + 130, y + 24, 120, 30), "Nod", side == HousesType.HOUSE_BAD, () => side = HousesType.HOUSE_BAD);

        y += 80;
        int maxOpponents = maps.Count > 0 ? maps[mapIndex].Starts - 1 : 1;
        font.Draw(batch, "Computer opponents", new Vector2(x, y), gold);
        for (int n = 1; n <= Math.Min(maxOpponents, 5); n++)
        {
            int count = n;
            Button(new Rect(x + (n - 1) * 50, y + 24, 44, 30), n.ToString(), opponents == n, () => opponents = count);
        }

        y += 80;
        font.Draw(batch, "Opponent side", new Vector2(x, y), gold);
        string[] sides = ["Random", "GDI", "Nod"];
        for (int i = 0; i < sides.Length; i++)
        {
            int choice = i;
            Button(new Rect(x + i * 84, y + 24, 78, 30), sides[i], enemySide == i, () => enemySide = choice);
        }

        y += 80;
        font.Draw(batch, "Starting credits", new Vector2(x, y), gold);
        for (int i = 0; i < CreditChoices.Length; i++)
        {
            int choice = i;
            Button(new Rect(x + i * 64, y + 24, 58, 30), CreditChoices[i].ToString(), creditsIndex == i, () => creditsIndex = choice);
        }

        y += 80;
        Button(new Rect(x, y, 160, 30), crates ? "Crates: on" : "Crates: off", crates, () => crates = !crates);

        Button(new Rect(x, height - 90, 250, 44), "START", false, Start);
        if (exitTo != null)
            Button(new Rect(40, height - 90, 160, 44), "BACK", false, () => GameWindow.Current?.Switch(exitTo()));

        font.Draw(batch, "In game: left-click selects (drag for a box), right-click gives orders, the sidebar builds.",
            new Vector2(380, 540), text);
        font.Draw(batch, "Arrow keys or the screen edge scroll, the mouse wheel zooms, Escape cancels.", new Vector2(380, 562), text);
        batch.End();
    }

    public void Dispose()
    {
        foreach (var m in input.Mice)
        {
            m.MouseMove -= onMove;
            m.MouseDown -= onDown;
        }
        foreach (var k in input.Keyboards)
            k.KeyDown -= onKey;
        preview?.Dispose();
        fonts.Dispose();
        batch.Dispose();
    }
}

using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.Platform;
using OpenGZH.Client.UI;
using OpenGZH.TiberianDawn;
using OpenGZH.TiberianDawn.Art;
using OpenGZH.TiberianDawn.Data;
using Silk.NET.Input;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.TiberianDawn;

/// <summary>
/// Shows a Tiberian Dawn scenario as it starts, in its original 2D art (24-pixel cells), scaled up
/// by whole pixels. Arrow keys or the screen edges scroll; the mouse wheel changes the scale;
/// PageUp/PageDown step through the scenarios.
/// </summary>
public sealed class TdMapScreen : IScreen
{
    readonly GL gl;
    readonly IInputContext input;
    readonly TdContent content;
    readonly SpriteBatch batch;
    readonly List<string> scenarios;
    Texture? mapTexture;
    TdScenario? scenario;
    int index;
    int width = 1280, height = 720;
    int scale = 2;
    Vector2 scroll;
    Vector2 mouse;
    bool mouseSeen;

    public TdMapScreen(GL gl, IInputContext input, TdContent content, string scenarioName)
    {
        this.gl = gl;
        this.input = input;
        this.content = content;
        batch = new SpriteBatch(gl);
        scenarios = TdScenario.List(content.Files);
        index = Math.Max(0, scenarios.FindIndex(s => s.Equals(scenarioName, StringComparison.OrdinalIgnoreCase)));
        foreach (var mouseDevice in input.Mice)
        {
            mouseDevice.MouseMove += (_, p) => { mouse = p; mouseSeen = true; };
            mouseDevice.Scroll += (_, w) => scale = Math.Clamp(scale + Math.Sign(w.Y), 1, 4);
        }
        foreach (var keyboard in input.Keyboards)
            keyboard.KeyDown += (_, key, _) =>
            {
                if (key == Key.PageDown) Load(index + 1);
                if (key == Key.PageUp) Load(index - 1);
            };
        Load(index);
    }

    void Load(int i)
    {
        if (scenarios.Count == 0)
            return;
        index = (i % scenarios.Count + scenarios.Count) % scenarios.Count;
        scenario = TdScenario.Load(content.Files, scenarios[index]);
        mapTexture?.Dispose();
        mapTexture = null;
        if (scenario == null)
            return;
        var image = new TdMapRenderer(content).Render(scenario);
        mapTexture = new Texture(gl, image, repeat: false, mipmaps: false, nearest: true);
        // Start where the scenario's [MAP] TacticalPos says the view begins.
        int tactical = scenario.Ini.GetInt("MAP", "TacticalPos", -1);
        scroll = tactical >= 0
            ? new Vector2((tactical % 64 - scenario.X) * TdMapRenderer.CellPixels, (tactical / 64 - scenario.Y) * TdMapRenderer.CellPixels)
            : Vector2.Zero;
        Console.WriteLine($"{scenarios[index]} ({scenario.Theater}, {scenario.Width}x{scenario.Height} cells): {scenario.Ini.Get("BASIC", "Name", scenario.Ini.Get("BASIC", "Brief"))}");
    }

    public void Resize(int w, int h)
    {
        width = Math.Max(1, w);
        height = Math.Max(1, h);
        gl.Viewport(0, 0, (uint)width, (uint)height);
    }

    public void Update(float seconds)
    {
        var move = Vector2.Zero;
        var keyboard = input.Keyboards.Count > 0 ? input.Keyboards[0] : null;
        if (keyboard?.IsKeyPressed(Key.Left) == true || mouseSeen && mouse.X <= 2) move.X -= 1;
        if (keyboard?.IsKeyPressed(Key.Right) == true || mouseSeen && mouse.X >= width - 3) move.X += 1;
        if (keyboard?.IsKeyPressed(Key.Up) == true || mouseSeen && mouse.Y <= 2) move.Y -= 1;
        if (keyboard?.IsKeyPressed(Key.Down) == true || mouseSeen && mouse.Y >= height - 3) move.Y += 1;
        scroll += move * 600 * seconds / scale;
        if (mapTexture != null)
        {
            scroll.X = Math.Clamp(scroll.X, 0, Math.Max(0, mapTexture.Width - width / (float)scale));
            scroll.Y = Math.Clamp(scroll.Y, 0, Math.Max(0, mapTexture.Height - height / (float)scale));
        }
    }

    public void Render(float seconds)
    {
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        batch.Begin(width, height);
        if (mapTexture != null)
        {
            // Snap to whole map pixels so the art stays crisp.
            float sx = MathF.Floor(scroll.X), sy = MathF.Floor(scroll.Y);
            batch.DrawTexture(mapTexture, new Rect(-sx * scale, -sy * scale, mapTexture.Width * scale, mapTexture.Height * scale));
        }
        batch.End();
    }

    public void Dispose()
    {
        mapTexture?.Dispose();
        batch.Dispose();
    }
}

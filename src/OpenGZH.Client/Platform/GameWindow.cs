using OpenGZH.Formats.Images;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace OpenGZH.Client.Platform;

/// <summary>
/// Creates the OS window and OpenGL 3.3 core context (works on Windows, macOS and Linux)
/// and drives a <see cref="IScreen"/>: load, update, render, input.
/// </summary>
public sealed class GameWindow
{
    readonly IWindow window;
    IScreen? screen;
    Func<GL, IInputContext, IWindow, IScreen>? createScreen;

    /// <summary>The game's window (screens use it to hand over to the next screen).</summary>
    public static GameWindow? Current { get; private set; }

    public GL? GL { get; private set; }
    public IInputContext? Input { get; private set; }

    /// <summary>When set, the window renders a few frames, saves a screenshot here and closes.</summary>
    public string? ScreenshotPath { get; set; }
    public int ScreenshotAfterFrames { get; set; } = 5;
    int renderedFrames;

    public GameWindow(string title, int width = 1280, int height = 720, bool visible = true)
    {
        var options = WindowOptions.Default with
        {
            Title = title,
            Size = new Vector2D<int>(width, height),
            IsVisible = visible,
            VSync = true,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            PreferredDepthBufferBits = 24,
            PreferredStencilBufferBits = 8,
            Samples = 4, // anti-aliased edges
        };
        // Inside a single-file executable the native libraries are unpacked where only .NET's own
        // resolver looks: load them that way first, then Silk.NET finds them already loaded.
        foreach (string native in new[] { "glfw3", "glfw", "soft_oal", "openal" })
            System.Runtime.InteropServices.NativeLibrary.TryLoad(native, typeof(GameWindow).Assembly, null, out _);
        // Register GLFW explicitly: Silk.NET's automatic discovery doesn't work inside a single-file executable.
        Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
        Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();
        window = Window.Create(options);
        window.Load += () => NativeWindow = window.Native?.Glfw;
        window.Load += OnLoad;
        window.Update += dt =>
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            screen?.Update((float)dt);
            if (PerfReport)
                updateTicks.Add(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        };
        window.Render += OnRender;
        window.FramebufferResize += size =>
        {
            GL?.Viewport(size);
            screen?.Resize(size.X, size.Y);
        };
        window.Closing += () => screen?.Dispose();
    }

    /// <summary>The GLFW window handle (the cursors are set on it directly), once the window exists.</summary>
    public static nint? NativeWindow { get; private set; }

    /// <summary>Replaces the current screen (menus to a game and back).</summary>
    public void Switch(IScreen next)
    {
        var old = screen;
        screen = next;
        old?.Dispose();
        var size = window.FramebufferSize;
        next.Resize(size.X, size.Y);
    }

    public void Run(Func<GL, IInputContext, IWindow, IScreen> create)
    {
        Current = this;
        createScreen = create;
        window.Run();
    }

    void OnLoad()
    {
        GL = window.CreateOpenGL();
        Input = window.CreateInput();
        screen = createScreen!(GL, Input, window);
        var size = window.FramebufferSize;
        GL.Viewport(size);
        screen.Resize(size.X, size.Y);
    }

    /// <summary>Debug (--perf): every few seconds, print frame rate, update/render times and the slowest logic parts.</summary>
    public bool PerfReport { get; set; }
    readonly List<long> updateTicks = new(), renderTicks = new();
    double perfClock;

    void ReportPerf(double dt)
    {
        perfClock += dt;
        if (perfClock < 5)
            return;
        static string Stats(List<long> ticks)
        {
            if (ticks.Count == 0)
                return "-";
            var ms = ticks.Select(t => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency).OrderBy(v => v).ToList();
            return $"avg {ms.Average():F1} ms, 95% {ms[(int)(ms.Count * 0.95)]:F1}, worst {ms[^1]:F1}";
        }
        Console.WriteLine($"[perf] {renderTicks.Count / perfClock:F0} fps | update {Stats(updateTicks)} | render {Stats(renderTicks)}");
        foreach (var (name, ms) in Logic.GameProfiler.Report().Take(12))
            Console.WriteLine($"[perf]   {name,-45} {ms / perfClock:F1} ms/s");
        Logic.GameProfiler.Reset();
        updateTicks.Clear();
        renderTicks.Clear();
        perfClock = 0;
    }

    void OnRender(double dt)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        screen?.Render((float)dt);
        if (PerfReport)
        {
            renderTicks.Add(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            ReportPerf(dt);
        }
        renderedFrames++;
        if (ScreenshotPath != null && renderedFrames == ScreenshotAfterFrames)
        {
            SaveScreenshot(ScreenshotPath);
            window.Close();
        }
    }

    public unsafe void SaveScreenshot(string path)
    {
        var size = window.FramebufferSize;
        var image = new Image(size.X, size.Y);
        fixed (byte* pixels = image.Pixels)
            GL!.ReadPixels(0, 0, (uint)size.X, (uint)size.Y, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        // OpenGL reads bottom row first; images are stored top row first.
        int stride = size.X * 4;
        var row = new byte[stride];
        for (int y = 0; y < size.Y / 2; y++)
        {
            int top = y * stride, bottom = (size.Y - 1 - y) * stride;
            System.Buffer.BlockCopy(image.Pixels, top, row, 0, stride);
            System.Buffer.BlockCopy(image.Pixels, bottom, image.Pixels, top, stride);
            System.Buffer.BlockCopy(row, 0, image.Pixels, bottom, stride);
        }
        for (int i = 3; i < image.Pixels.Length; i += 4)
            image.Pixels[i] = 255;

        PngWriter.Save(image, path);
        Console.WriteLine($"Screenshot saved to {path}");
    }
}

/// <summary>Something that fills the window: the shell menus, a game in progress, the model viewer...</summary>
public interface IScreen : IDisposable
{
    void Resize(int width, int height);
    void Update(float seconds);
    void Render(float seconds);
}

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
        };
        window = Window.Create(options);
        window.Load += OnLoad;
        window.Update += dt => screen?.Update((float)dt);
        window.Render += OnRender;
        window.FramebufferResize += size =>
        {
            GL?.Viewport(size);
            screen?.Resize(size.X, size.Y);
        };
        window.Closing += () => screen?.Dispose();
    }

    public void Run(Func<GL, IInputContext, IWindow, IScreen> create)
    {
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

    void OnRender(double dt)
    {
        screen?.Render((float)dt);
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

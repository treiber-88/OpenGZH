using OpenGZH.Core.FileSystem;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Images;
using Silk.NET.Core;
using Silk.NET.Input;

namespace OpenGZH.Client.UI;

/// <summary>
/// The game's mouse cursors (the original's Win32Mouse): each MouseCursors entry in rules/mouse.yaml
/// names an animated cursor, Data/Cursors/&lt;Texture&gt;.ani (or &lt;Texture&gt;0..7.ani when it has
/// several Directions, like Scroll). The chosen cursor is set as the system cursor, animated.
/// </summary>
public sealed class Cursors
{
    readonly VirtualFileSystem files;
    readonly Dictionary<string, (string Texture, int Directions)> infos = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AniCursor?> loaded = new(StringComparer.OrdinalIgnoreCase);
    readonly IInputContext input;

    string current = "";
    int currentDirection;
    AniCursor? currentCursor;
    int shownStep = -1;
    double clock;

    public Cursors(RuleSet rules, VirtualFileSystem files, IInputContext input)
    {
        this.files = files;
        this.input = input;
        foreach (var (name, node) in rules.Section("MouseCursors").Entries)
            infos[name] = (node.GetString("Texture", node.GetString("Image")), Math.Max(1, RuleValue.Int(node.GetString("Directions"), 1)));
    }

    /// <summary>The cursor now showing (a MouseCursors name such as "Move" or "AttackObj").</summary>
    public string Current => current;

    /// <summary>
    /// Shows a cursor. <paramref name="direction"/> (0..7, as the original's scroll direction frame)
    /// picks one of a several-directions cursor. Unknown names fall back to Normal.
    /// </summary>
    public void Set(string name, int direction = 0)
    {
        if (!infos.ContainsKey(name))
            name = "Normal";
        var (_, directions) = infos.GetValueOrDefault(name);
        direction = directions > 1 ? ((direction % directions) + directions) % directions : 0;
        if (name.Equals(current, StringComparison.OrdinalIgnoreCase) && direction == currentDirection)
            return;
        current = name;
        currentDirection = direction;
        currentCursor = Load(name, direction);
        clock = 0;
        shownStep = -1;
        Apply();
    }

    AniCursor? Load(string name, int direction)
    {
        var (texture, directions) = infos[name];
        string file = directions > 1 ? $"Data/Cursors/{texture}{direction}.ani" : $"Data/Cursors/{texture}.ani";
        if (loaded.TryGetValue(file, out var cached))
            return cached;
        AniCursor? cursor = null;
        try
        {
            if (files.Exists(file))
                cursor = AniCursor.Decode(files.ReadAllBytes(file));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Cursor {file}: {e.Message}");
        }
        loaded[file] = cursor;
        return cursor;
    }

    // Each animation frame becomes one OS cursor, made once and kept. (Setting Silk.NET's cursor image
    // makes a new OS cursor every time without freeing the old one; animated cursors used up Windows'
    // cursor resources within an hour or so and the game crashed.)
    static readonly Dictionary<(AniCursor Cursor, Image Frame), nint> osCursors = new();
    static Silk.NET.GLFW.Glfw? glfw;

    static unsafe bool SetGlfwCursor(nint window, AniCursor cursor, Image frame)
    {
        glfw ??= Silk.NET.GLFW.Glfw.GetApi();
        if (!osCursors.TryGetValue((cursor, frame), out nint made))
        {
            fixed (byte* pixels = frame.Pixels)
            {
                var image = new Silk.NET.GLFW.Image { Width = frame.Width, Height = frame.Height, Pixels = pixels };
                made = (nint)glfw.CreateCursor(&image, cursor.HotspotX, cursor.HotspotY);
            }
            if (made == 0)
                return false;
            osCursors[(cursor, frame)] = made;
        }
        glfw.SetCursor((Silk.NET.GLFW.WindowHandle*)window, (Silk.NET.GLFW.Cursor*)made);
        return true;
    }

    /// <summary>Steps the animation (call once a frame).</summary>
    public void Update(float seconds)
    {
        clock += seconds;
        Apply();
    }

    void Apply()
    {
        if (input.Mice.Count == 0)
            return;
        var mouseCursor = input.Mice[0].Cursor;
        if (currentCursor == null)
        {
            if (shownStep != -2)
            {
                if (Platform.GameWindow.NativeWindow is { } arrowWindow)
                    unsafe { (glfw ??= Silk.NET.GLFW.Glfw.GetApi()).SetCursor((Silk.NET.GLFW.WindowHandle*)arrowWindow, null); }
                mouseCursor.Type = CursorType.Standard;
                mouseCursor.StandardCursor = StandardCursor.Arrow;
                shownStep = -2;
            }
            return;
        }
        int step = currentCursor.StepAt(clock);
        if (step == shownStep)
            return;
        shownStep = step;
        var image = currentCursor.Frames[currentCursor.Steps.Count > 0 ? currentCursor.Steps[step].Frame : 0];
        if (Platform.GameWindow.NativeWindow is { } handle && SetGlfwCursor(handle, currentCursor, image))
            return;
        mouseCursor.Type = CursorType.Custom;
        mouseCursor.HotspotX = currentCursor.HotspotX;
        mouseCursor.HotspotY = currentCursor.HotspotY;
        mouseCursor.Image = new RawImage(image.Width, image.Height, image.Pixels);
    }
}

using System.Numerics;
using OpenGZH.Core.FileSystem;
using OpenGZH.Formats.Wnd;

namespace OpenGZH.Client.UI.Gui;

/// <summary>
/// A loaded .wnd layout (a menu, the command bar, a dialog): its window tree and lookups by name.
/// Behaviour is attached in code by window name, e.g. layout.Button("ButtonSkirmish").Clicked += ...
/// </summary>
public sealed class GuiLayout
{
    public string FileName { get; }
    public List<GuiWindow> Roots { get; } = new();
    public bool Hidden { get; set; }

    public GuiLayout(string fileName, IEnumerable<WndWindow> definitions)
    {
        FileName = fileName;
        foreach (var definition in definitions)
            Roots.Add(Build(definition));
    }

    public static GuiLayout Load(VirtualFileSystem fileSystem, string path) =>
        new(PathUtil.FileName(path), WndFile.Parse(fileSystem.ReadAllText(path)));

    static GuiWindow Build(WndWindow definition)
    {
        GuiWindow window = definition.Type.ToUpperInvariant() switch
        {
            "PUSHBUTTON" => new GuiPushButton(definition),
            "STATICTEXT" => new GuiStaticText(definition),
            "CHECKBOX" => new GuiCheckBox(definition),
            "RADIOBUTTON" => new GuiRadioButton(definition),
            "PROGRESSBAR" => new GuiProgressBar(definition),
            "LISTBOX" or "SCROLLLISTBOX" => new GuiListBox(definition),
            "COMBOBOX" => new GuiComboBox(definition),
            "ENTRYFIELD" => new GuiTextEntry(definition),
            "HORZSLIDER" or "VERTSLIDER" => new GuiSlider(definition),
            _ => new GuiWindow(definition),
        };
        foreach (var child in definition.Children)
            window.AddChild(Build(child));
        return window;
    }

    public GuiWindow? Find(string name)
    {
        foreach (var root in Roots)
            if (root.Find(name) is { } found)
                return found;
        return null;
    }

    public T? Find<T>(string name) where T : GuiWindow => Find(name) as T;

    /// <summary>A button by name; throws with a clear message if the layout doesn't have it.</summary>
    public GuiPushButton Button(string name) =>
        Find<GuiPushButton>(name) ?? throw new KeyNotFoundException($"{FileName} has no button '{name}'.");

    public void Layout(Vector2 scale)
    {
        foreach (var root in Roots)
            root.Layout(scale);
    }

    public void Draw(GuiContext context)
    {
        if (Hidden)
            return;
        foreach (var root in Roots)
            root.Draw(context);
    }

    public GuiWindow? HitTest(Vector2 point)
    {
        if (Hidden)
            return null;
        for (int i = Roots.Count - 1; i >= 0; i--)
            if (Roots[i].HitTest(point) is { } hit)
                return hit;
        return null;
    }
}

/// <summary>
/// The visible layouts, top one last. Routes mouse and keyboard to the window under the mouse,
/// handling hover, press and click.
/// </summary>
public sealed class GuiManager
{
    readonly List<GuiLayout> layouts = new();
    GuiWindow? hovered, pressed, focused;

    public IReadOnlyList<GuiLayout> Layouts => layouts;

    public void Push(GuiLayout layout) => layouts.Add(layout);

    /// <summary>A window by name in any loaded layout (the original's winGetWindowFromId(NULL, ...)).</summary>
    public GuiWindow? Find(string name)
    {
        for (int i = layouts.Count - 1; i >= 0; i--)
            if (layouts[i].Find(name) is { } found)
                return found;
        return null;
    }

    /// <summary>Puts a layout on top of the others (the original's bringForward).</summary>
    public void BringForward(GuiLayout layout)
    {
        if (layouts.Remove(layout))
            layouts.Add(layout);
    }
    public void Remove(GuiLayout layout) => layouts.Remove(layout);
    public void Clear() => layouts.Clear();

    public void Layout(Vector2 scale)
    {
        foreach (var layout in layouts)
            layout.Layout(scale);
    }

    public void Draw(GuiContext context)
    {
        foreach (var layout in layouts)
            layout.Draw(context);
        // An open drop-down list sits above every layout.
        GuiComboBox.Open?.DrawList(context);
    }

    public GuiWindow? HitTest(Vector2 point)
    {
        for (int i = layouts.Count - 1; i >= 0; i--)
            if (layouts[i].HitTest(point) is { } hit)
                return hit;
        return null;
    }

    /// <summary>True if the interface (not the game world) is under the mouse.</summary>
    public bool IsOverInterface(Vector2 point) => HitTest(point) != null;

    public void MouseMove(Vector2 point)
    {
        if (GuiComboBox.Open is { } open && open.ListContains(point))
        {
            open.ListMouseMove(point);
            return;
        }
        var hit = HitTest(point);
        if (hit != hovered)
        {
            hovered?.OnMouseLeave();
            hovered?.RaiseMouseLeft();
            hovered = hit;
            hovered?.OnMouseEnter();
            hovered?.RaiseMouseEntered();
        }
        (pressed as GuiSlider)?.Drag(point);
    }

    public bool MouseDown(Vector2 point)
    {
        if (GuiComboBox.Open is { } open)
        {
            if (open.ListContains(point))
            {
                pressed = null;
                return true;
            }
            // A click anywhere else closes the list (and is not passed on, except to the combo box itself).
            if (HitTest(point) != open)
            {
                GuiComboBox.Close();
                pressed = null;
                return true;
            }
        }
        var hit = HitTest(point);
        pressed = hit;
        focused = hit as GuiTextEntry ?? focused;
        hit?.OnMouseDown(point);
        return hit != null;
    }

    public bool MouseUp(Vector2 point)
    {
        if (GuiComboBox.Open is { } open && open.ListContains(point))
        {
            open.ListClick(point);
            return true;
        }
        var target = pressed;
        pressed = null;
        if (target == null)
            return HitTest(point) != null;
        target.OnMouseUp(point, target.ScreenRect.Contains(point));
        return true;
    }

    public bool Scroll(Vector2 point, float amount)
    {
        if (GuiComboBox.Open is { } open && open.ListContains(point))
        {
            open.ListScroll(amount);
            return true;
        }
        var hit = HitTest(point);
        for (var window = hit; window != null; window = window.Parent)
        {
            if (window is GuiListBox list)
            {
                list.OnScroll(amount);
                return true;
            }
        }
        return hit != null;
    }

    public bool KeyChar(char character)
    {
        if (focused is not { IsVisible: true })
            return false;
        focused.OnKey(character);
        return true;
    }
}

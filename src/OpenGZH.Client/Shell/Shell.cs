using OpenGZH.Client.UI.Gui;

namespace OpenGZH.Client.Shell;

/// <summary>
/// The front-end menu stack (the original's Shell): menus are pushed and popped by their .wnd file
/// name. A push or pop first lets the current menu play its closing transitions; the menu reports
/// back with <see cref="ShutdownComplete"/>, and only then is the next one shown.
/// </summary>
public sealed class Shell
{
    const int MaxShellStack = 16;

    readonly ShellScreen screen;
    readonly List<ShellMenu> stack = new();
    readonly Dictionary<string, Func<ShellScreen, GuiLayout, ShellMenu>> factories = new(StringComparer.OrdinalIgnoreCase);
    bool pendingPush, pendingPop;
    string pendingPushName = "";
    float updateTime;

    public Shell(ShellScreen screen) => this.screen = screen;

    /// <summary>Gives a menu's behaviour by its .wnd file (e.g. "Menus/MainMenu.wnd").</summary>
    public void Register(string file, Func<ShellScreen, GuiLayout, ShellMenu> factory) => factories[file] = factory;

    public ShellMenu? Top => stack.Count == 0 ? null : stack[^1];
    public int Count => stack.Count;

    /// <summary>The transitions have finished (the original also waited for its window animator here).</summary>
    public bool IsAnimFinished => screen.Transitions.IsFinished;

    public void Push(string file, bool shutdownImmediate = false)
    {
        if (file.Length == 0 || stack.Count >= MaxShellStack)
            return;
        pendingPush = true;
        pendingPushName = file;
        var currentTop = Top;
        if (currentTop != null && !currentTop.Layout.Hidden)
            currentTop.Shutdown(shutdownImmediate);
        else
            ShutdownComplete(null);
    }

    public void Pop()
    {
        var top = Top;
        if (top == null)
            return;
        pendingPop = true;
        top.Shutdown(immediate: false);
    }

    public void PopImmediate()
    {
        var top = Top;
        if (top == null)
            return;
        pendingPop = false;
        top.Shutdown(immediate: true);
        DoPop(impendingPush: false);
    }

    /// <summary>Called by a menu when its closing animation is done.</summary>
    public void ShutdownComplete(ShellMenu? menu, bool impendingPush = false)
    {
        if (pendingPush)
        {
            pendingPush = false;
            string name = pendingPushName;
            pendingPushName = "";
            DoPush(name);
        }
        else if (pendingPop)
        {
            pendingPop = false;
            DoPop(impendingPush);
        }
    }

    void DoPush(string file)
    {
        var menu = Create(file);
        menu.Init();
        screen.Gui.BringForward(menu.Layout);
    }

    ShellMenu Create(string file)
    {
        var layout = GuiLayout.Load(screen.Ui.Content.FileSystem, "Window/" + file);
        screen.Gui.Push(layout);
        layout.Layout(screen.Ui.Gui.Scale);
        var menu = factories.TryGetValue(file, out var make) ? make(screen, layout) : new GenericMenu(screen, layout);
        menu.File = file;
        stack.Add(menu);
        return menu;
    }

    /// <summary>The menus on the stack, bottom first (kept while a game is played, to come back to).</summary>
    public IReadOnlyList<string> StackFiles => stack.Select(m => m.File).ToList();

    /// <summary>
    /// Puts back the menus that were open when a game started, hidden and not yet initialised (the
    /// original keeps its stack under a hidden shell; the top one initialises when it is shown again).
    /// </summary>
    public void Restore(IEnumerable<string> files)
    {
        foreach (string file in files)
            Create(file).Layout.Hidden = true;
    }

    void DoPop(bool impendingPush)
    {
        var top = Top;
        if (top == null)
            return;
        stack.RemoveAt(stack.Count - 1);
        screen.Gui.Remove(top.Layout);
        top.Destroyed();
        if (Top is { } newTop && !impendingPush)
        {
            newTop.Layout.Hidden = false;
            newTop.Init();
        }
    }

    /// <summary>Hides or shows every menu (a game is starting, or the options popup).</summary>
    public void Hide(bool hide)
    {
        foreach (var menu in stack)
            menu.Layout.Hidden = hide;
    }

    /// <summary>Runs each menu's update about 30 times a second, top first (as the original).</summary>
    public void Update(float seconds)
    {
        updateTime += seconds;
        if (updateTime < 1f / 30)
            return;
        updateTime = 0;
        for (int i = stack.Count - 1; i >= 0 && i < stack.Count; i--)
            stack[i].Update();
    }

    /// <summary>Takes every menu down (leaving the shell for a game).</summary>
    public void Reset()
    {
        while (stack.Count > 0)
            PopImmediate();
        screen.Transitions.Reset();
    }
}

/// <summary>
/// A menu's behaviour (the original's layout callbacks: init, update, shutdown, and the system
/// callback that handles its buttons).
/// </summary>
public abstract class ShellMenu(ShellScreen screen, GuiLayout layout)
{
    public GuiLayout Layout => layout;

    /// <summary>The .wnd file this menu was opened from (e.g. "Menus/MainMenu.wnd").</summary>
    public string File { get; internal set; } = "";
    protected ShellScreen Screen => screen;
    protected Shell Shell => screen.Shell;
    protected TransitionHandler Transitions => screen.Transitions;

    protected GuiWindow? Window(string name) => layout.Find(name);

    protected GuiPushButton? Button(string name) => layout.Find<GuiPushButton>(name);

    /// <summary>Wires a button by name; quietly does nothing if the layout lacks it.</summary>
    protected void OnClick(string name, Action action)
    {
        if (Button(name) is { } button)
            button.Clicked += _ => action();
    }

    public virtual void Init() => layout.Hidden = false;

    public virtual void Update() { }

    /// <summary>The menu is closing: by default it just hides and tells the shell.</summary>
    public virtual void Shutdown(bool immediate)
    {
        layout.Hidden = true;
        Shell.ShutdownComplete(this);
    }

    /// <summary>The menu's windows are gone.</summary>
    public virtual void Destroyed() { }

    /// <summary>Raw input the menu wants first (the original's layout input callback).</summary>
    public virtual bool OnMouseMove(System.Numerics.Vector2 point) => false;
    public virtual bool OnKey(char character) => false;
}

/// <summary>
/// A menu whose own behaviour is not written yet: shows its layout, and its back/cancel buttons go back.
/// </summary>
public sealed class GenericMenu : ShellMenu
{
    public GenericMenu(ShellScreen screen, GuiLayout layout) : base(screen, layout)
    {
        foreach (var root in layout.Roots)
            Wire(root);
    }

    void Wire(GuiWindow window)
    {
        if (window is GuiPushButton button)
        {
            string name = button.Name[(button.Name.IndexOf(':') + 1)..];
            if (name.Contains("Back", StringComparison.OrdinalIgnoreCase) || name.Contains("Cancel", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Exit", StringComparison.OrdinalIgnoreCase) || name.Equals("ButtonOk", StringComparison.OrdinalIgnoreCase))
                button.Clicked += _ => Shell.Pop();
        }
        foreach (var child in window.Children)
            Wire(child);
    }
}

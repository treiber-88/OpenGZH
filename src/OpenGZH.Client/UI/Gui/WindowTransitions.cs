using System.Numerics;
using OpenGZH.Core.Rules;

namespace OpenGZH.Client.UI.Gui;

/// <summary>
/// Menu window transitions (the original's GameWindowTransitions): named groups from WindowTransitions
/// (window-transitions.yaml), each a list of windows with a style and a frame delay. Playing a group
/// animates its windows in (flashing borders, buttons fading in, logos growing...); reversing plays it
/// back out. One group plays at a time, with one more pending. Stepped 30 times a second.
/// </summary>
public sealed class TransitionHandler
{
    readonly List<TransitionGroup> groups = new();
    TransitionGroup? currentGroup, pendingGroup, drawGroup, secondaryDrawGroup;
    float stepTime;

    /// <summary>Finds windows by name across the loaded layouts.</summary>
    public Func<string, GuiWindow?> FindWindow { get; set; } = _ => null;

    /// <summary>Plays a UI sound (audio event name).</summary>
    public Action<string>? PlaySound { get; set; }

    /// <summary>The screen size for whole-screen fades.</summary>
    public Vector2 ScreenSize { get; set; } = new(800, 600);

    /// <summary>The text of a static text window, and setting it (score screen count-ups).</summary>
    public Func<GuiWindow, string> GetText { get; set; } = w => w.Text ?? "";

    public TransitionHandler(RuleNode? transitions)
    {
        if (transitions == null)
            return;
        foreach (var (name, node) in transitions.Entries)
        {
            var group = new TransitionGroup(this, name) { FireOnce = RuleValue.Bool(node.GetString("FireOnce")) };
            if (node.TryGet("Window", out var windows))
            {
                foreach (var w in windows.AsList())
                    group.Windows.Add(new TransitionWindow(this, w.GetString("WinName"), StyleFor(w.GetString("Style")), RuleValue.Int(w.GetString("FrameDelay"))));
            }
            groups.Add(group);
        }
    }

    static int StyleFor(string name) => name.ToUpperInvariant() switch
    {
        "FLASH" => Transition.Flash,
        "BUTTONFLASH" => Transition.ButtonFlash,
        "WINFADE" => Transition.WinFade,
        "WINSCALEUP" => Transition.WinScaleUp,
        "MAINMENUSCALEUP" => Transition.MainMenuScaleUp,
        "TYPETEXT" => Transition.TextType,
        "SCREENFADE" => Transition.ScreenFade,
        "COUNTUP" => Transition.CountUp,
        "FULLFADE" => Transition.FullFade,
        "TEXTONFRAME" => Transition.TextOnFrame,
        "MAINMENUMEDIUMSCALEUP" => Transition.MainMenuMediumScaleUp,
        "MAINMENUSMALLSCALEDOWN" => Transition.MainMenuSmallScaleDown,
        "CONTROLBARARROW" => Transition.ControlBarArrow,
        "SCORESCALEUP" => Transition.ScoreScaleUp,
        "REVERSESOUND" => Transition.ReverseSound,
        _ => -1,
    };

    TransitionGroup? FindGroup(string name) =>
        name.Length == 0 ? null : groups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Reset() => currentGroup = pendingGroup = drawGroup = secondaryDrawGroup = null;

    /// <summary>Real time in; steps the transitions at 30 a second.</summary>
    public void Update(float seconds)
    {
        stepTime += seconds;
        for (int i = 0; stepTime >= 1f / 30 && i < 4; i++)
        {
            stepTime -= 1f / 30;
            Step();
        }
        if (stepTime > 1f / 30)
            stepTime = 0;
    }

    void Step()
    {
        secondaryDrawGroup = drawGroup != currentGroup ? drawGroup : null;
        drawGroup = currentGroup;
        if (currentGroup != null && !currentGroup.IsFinished)
            currentGroup.Update();
        if (currentGroup != null && currentGroup.IsFinished && currentGroup.FireOnce)
            currentGroup = null;
        if (currentGroup != null && pendingGroup != null && currentGroup.IsFinished)
        {
            currentGroup = pendingGroup;
            pendingGroup = null;
        }
        if (currentGroup == null && pendingGroup != null)
        {
            currentGroup = pendingGroup;
            pendingGroup = null;
        }
        if (currentGroup != null && currentGroup.IsFinished && currentGroup.IsReversed)
            currentGroup = null;
    }

    public void Draw(GuiContext context)
    {
        drawGroup?.Draw(context);
        secondaryDrawGroup?.Draw(context);
    }

    /// <summary>Plays a group (after the current one, which is reversed first unless it fires once).</summary>
    public void SetGroup(string groupName, bool immediate = false)
    {
        if (groupName.Length == 0 && immediate)
            currentGroup = null;
        if (immediate && currentGroup != null)
        {
            currentGroup.Skip();
            currentGroup = FindGroup(groupName);
            currentGroup?.Init();
            return;
        }
        if (currentGroup != null)
        {
            if (!currentGroup.FireOnce && !currentGroup.IsReversed)
                currentGroup.Reverse();
            pendingGroup = FindGroup(groupName);
            pendingGroup?.Init();
            return;
        }
        currentGroup = FindGroup(groupName);
        currentGroup?.Init();
    }

    /// <summary>Plays a group backwards (from its end).</summary>
    public void Reverse(string groupName)
    {
        var g = FindGroup(groupName);
        if (currentGroup == g && g != null)
        {
            currentGroup.Reverse();
            return;
        }
        if (pendingGroup == g && g != null)
        {
            pendingGroup = null;
            return;
        }
        currentGroup?.Skip();
        pendingGroup?.Skip();
        currentGroup = g;
        if (currentGroup != null)
        {
            currentGroup.Init();
            currentGroup.Skip();
            currentGroup.Reverse();
        }
        pendingGroup = null;
    }

    public void Remove(string groupName, bool skipPending = false)
    {
        var g = FindGroup(groupName);
        if (g == null)
            return;
        if (pendingGroup == g)
        {
            if (skipPending)
                pendingGroup.Skip();
            pendingGroup = null;
        }
        if (currentGroup == g)
        {
            currentGroup.Skip();
            currentGroup = null;
            if (pendingGroup != null)
                currentGroup = pendingGroup;
        }
    }

    public bool IsFinished => currentGroup?.IsFinished ?? true;
}

sealed class TransitionGroup(TransitionHandler handler, string name)
{
    public string Name => name;
    public bool FireOnce { get; init; }
    public List<TransitionWindow> Windows { get; } = new();
    int directionMultiplier = 1, currentFrame;

    public void Init()
    {
        currentFrame = 0;
        directionMultiplier = 1;
        foreach (var w in Windows)
            w.Init();
    }

    public void Update()
    {
        currentFrame += directionMultiplier;
        foreach (var w in Windows)
            w.Update(currentFrame);
    }

    public bool IsFinished => Windows.All(w => w.IsFinished);
    public bool IsReversed => directionMultiplier < 0;

    public void Reverse()
    {
        directionMultiplier = -1;
        int totalFrames = Windows.Count == 0 ? 0 : Windows.Max(w => w.TotalFrames);
        foreach (var w in Windows)
            w.Reverse();
        currentFrame = totalFrames;
    }

    public void Skip()
    {
        foreach (var w in Windows)
            w.Skip();
    }

    public void Draw(GuiContext context)
    {
        foreach (var w in Windows)
            w.Draw(context);
    }
}

sealed class TransitionWindow(TransitionHandler handler, string winName, int style, int frameDelay)
{
    Transition? transition;

    public void Init()
    {
        var win = handler.FindWindow(winName);
        transition = Transition.ForStyle(style, handler);
        transition?.Init(win);
    }

    public void Update(int frame)
    {
        if (transition == null || frame < frameDelay || frame > frameDelay + transition.FrameLength)
            return;
        transition.Update(frame - frameDelay);
    }

    public bool IsFinished => transition?.IsFinished ?? true;
    public int TotalFrames => frameDelay + (transition?.FrameLength ?? 0);
    public void Reverse() => transition?.Reverse();
    public void Skip() => transition?.Skip();
    public void Draw(GuiContext context) => transition?.Draw(context);
}

/// <summary>One window's animation in a transition group (the original's Transition subclasses).</summary>
abstract class Transition(TransitionHandler handler)
{
    public const int Flash = 0, ButtonFlash = 1, WinFade = 2, WinScaleUp = 3, MainMenuScaleUp = 4, TextType = 5, ScreenFade = 6,
        CountUp = 7, FullFade = 8, TextOnFrame = 9, MainMenuMediumScaleUp = 10, MainMenuSmallScaleDown = 11, ControlBarArrow = 12,
        ScoreScaleUp = 13, ReverseSound = 14;

    public int FrameLength { get; protected set; }
    public bool IsFinished { get; protected set; }
    protected bool IsForward = true;
    protected GuiWindow? Win;
    protected Rect WinRect;
    protected int DrawState = -1;
    protected TransitionHandler Handler => handler;

    public static Transition? ForStyle(int style, TransitionHandler handler) => style switch
    {
        Flash => new FlashTransition(handler),
        ButtonFlash => new ButtonFlashTransition(handler),
        WinFade => new FadeTransition(handler),
        WinScaleUp => new ScaleUpTransition(handler, "GUILogoMouseOver"),
        MainMenuScaleUp => new MainMenuScaleUpTransition(handler),
        TextType => new TextTypeTransition(handler),
        ScreenFade => new ScreenFadeTransition(handler),
        CountUp => new CountUpTransition(handler),
        FullFade => new FullFadeTransition(handler),
        TextOnFrame => new TextOnFrameTransition(handler),
        MainMenuMediumScaleUp => new MainMenuGrowTransition(handler, "Medium", 3, medium: true),
        MainMenuSmallScaleDown => new MainMenuGrowTransition(handler, "Small", 6, medium: false),
        ControlBarArrow => new ControlBarArrowTransition(handler),
        ScoreScaleUp => new ScaleUpTransition(handler, "GUIScoreScreenPictures"),
        ReverseSound => new ReverseSoundTransition(handler),
        _ => null,
    };

    protected static Vector4 Color(int r, int g, int b, int a) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    protected void Hide(bool hide)
    {
        if (Win != null)
            Win.Hidden = hide;
    }

    protected void Sound(string name) => Handler.PlaySound?.Invoke(name);

    public virtual void Init(GuiWindow? win)
    {
        if (win != null)
        {
            Win = win;
            WinRect = win.ScreenRect;
        }
        IsForward = false;
        Update(0);
        IsFinished = false;
        IsForward = true;
    }

    public abstract void Update(int frame);

    public virtual void Reverse()
    {
        IsFinished = false;
        IsForward = false;
    }

    public virtual void Skip() => Update(FrameLength);
    public abstract void Draw(GuiContext context);

    /// <summary>The original's drawOpenRect/drawFillRect pair at the window, inset by <paramref name="inset"/>.</summary>
    protected void Box(GuiContext context, Rect r, int borderAlpha, int fillAlpha)
    {
        context.Batch.DrawRect(r, Color(255, 255, 255, borderAlpha));
        context.Batch.FillRect(r, Color(255, 255, 255, fillAlpha));
    }

    /// <summary>The original's PushButtonImageDrawThree: a three-part button's enabled images at an alpha.</summary>
    protected static void DrawButtonImages(GuiContext context, GuiWindow? window, int alpha)
    {
        if (window == null)
            return;
        var d = window.Definition;
        string? left = d.EnabledData(0) is { HasImage: true } l ? l.Image : null;
        string? middle = d.EnabledData(5) is { HasImage: true } m ? m.Image : null;
        string? right = d.EnabledData(6) is { HasImage: true } rr ? rr.Image : null;
        if (context.Images.Find(left) is not { } leftImage || context.Images.Find(right) is not { } rightImage || middle == null)
            return;
        var color = Color(255, 255, 255, alpha);
        var r = window.ScreenRect;
        float leftWidth = leftImage.PixelWidth * r.Height / Math.Max(1, leftImage.PixelHeight);
        float rightWidth = rightImage.PixelWidth * r.Height / Math.Max(1, rightImage.PixelHeight);
        context.Images.Draw(context.Batch, leftImage, new Rect(r.X, r.Y, leftWidth, r.Height), color);
        context.Images.Draw(context.Batch, middle, new Rect(r.X + leftWidth, r.Y, Math.Max(0, r.Width - leftWidth - rightWidth), r.Height), color);
        context.Images.Draw(context.Batch, rightImage, new Rect(r.Right - rightWidth, r.Y, rightWidth, r.Height), color);
    }
}

/// <summary>FLASH: a window border flashes white and fades into the window.</summary>
sealed class FlashTransition : Transition
{
    const int Start = 0, FadeIn1 = 1, FadeIn3 = 3, Back1 = 4, Back4 = 7, End = 8;
    public FlashTransition(TransitionHandler h) : base(h) => FrameLength = End;

    public override void Update(int frame)
    {
        DrawState = -1;
        if (frame < Start || frame > End)
            return;
        if (frame == Start)
        {
            if (!IsForward && Win != null)
            {
                Hide(true);
                IsFinished = true;
            }
        }
        else if (frame <= FadeIn3)
        {
            if (frame == FadeIn1 && IsForward)
                Sound("GUIBoarderFadeIn");
            if (Win != null)
            {
                Hide(true);
                DrawState = frame;
            }
        }
        else if (frame <= Back4)
        {
            if (Win != null)
            {
                Hide(false);
                DrawState = frame;
            }
        }
        else if (IsForward && Win != null)
        {
            Hide(false);
            IsFinished = true;
        }
    }

    public override void Draw(GuiContext context)
    {
        var r = new Rect(WinRect.X + 1, WinRect.Y + 1, WinRect.Width - 2, WinRect.Height);
        switch (DrawState)
        {
            case 1: Box(context, r, 100, 33); break;
            case 2: Box(context, r, 150, 66); break;
            case 3: Box(context, r, 200, 99); break;
            case 4: Box(context, r, 250, 75); break;
            case 5: Box(context, r, 250, 50); break;
            case 6: Box(context, r, 250, 25); break;
            case 7: Box(context, r, 250, 10); break;
        }
    }
}

/// <summary>BUTTONFLASH: a button flashes in, shows, then a gradient sweeps across it.</summary>
sealed class ButtonFlashTransition : Transition
{
    const int Start = 0, FadeIn1 = 1, FadeIn2 = 2, FadeIn3 = 3, Back1 = 4, Back2 = 5, Back3 = 6, Back4 = 7,
        GradeIn1 = 11, GradeIn2 = 12, GradeOut1 = 13, GradeOut2 = 14, GradeOut3 = 15, GradeOut4 = 16, End = 17, ShowBackground = 18;
    public ButtonFlashTransition(TransitionHandler h) : base(h) => FrameLength = End;

    public override void Update(int frame)
    {
        DrawState = -1;
        if (frame < Start || frame > End)
            return;
        if (Win == null && frame != Start && frame != End)
            return;
        switch (frame)
        {
            case Start:
                if (!IsForward && Win != null)
                {
                    Hide(true);
                    IsFinished = true;
                }
                break;
            case FadeIn1:
                Hide(true);
                if (IsForward)
                {
                    Sound("GUIButtonsFadeIn");
                    DrawState = frame;
                }
                else
                    DrawState = Back4;
                break;
            case FadeIn2: Hide(true); DrawState = IsForward ? frame : Back3; break;
            case FadeIn3: Hide(true); DrawState = IsForward ? frame : Back2; break;
            case Back1: Hide(true); DrawState = IsForward ? frame : Back1; break;
            case Back2: Hide(true); DrawState = IsForward ? frame : FadeIn3; break;
            case Back3: Hide(true); DrawState = IsForward ? frame : FadeIn2; break;
            case Back4: Hide(true); DrawState = IsForward ? frame : FadeIn1; break;
            case GradeIn1: Hide(!IsForward); DrawState = IsForward ? frame : GradeOut4; break;
            case GradeIn2: Hide(!IsForward); DrawState = IsForward ? frame : GradeOut3; break;
            case GradeOut1: Hide(!IsForward); DrawState = IsForward ? frame : GradeOut2; break;
            case GradeOut2: Hide(!IsForward); DrawState = IsForward ? frame : GradeOut1; break;
            case GradeOut3: Hide(false); DrawState = IsForward ? frame : GradeIn2; break;
            case GradeOut4: Hide(false); DrawState = IsForward ? frame : GradeIn1; break;
            case End:
                if (IsForward && Win != null)
                {
                    Hide(false);
                    IsFinished = true;
                }
                break;
        }
        if (frame > Back4 && frame < GradeIn1)
            DrawState = ShowBackground;
    }

    void Gradient(GuiContext context, int alpha) =>
        context.Images.Draw(context.Batch, "Gradient", WinRect, Color(255, 255, 255, alpha));

    public override void Draw(GuiContext context)
    {
        switch (DrawState)
        {
            case FadeIn1: Box(context, WinRect, 100, 75); break;
            case FadeIn2: Box(context, WinRect, 150, 150); break;
            case FadeIn3: Box(context, WinRect, 200, 200); break;
            case Back1: DrawButtonImages(context, Win, 255); Box(context, WinRect, 250, 150); break;
            case Back2: DrawButtonImages(context, Win, 255); Box(context, WinRect, 250, 100); break;
            case Back3: DrawButtonImages(context, Win, 255); Box(context, WinRect, 250, 50); break;
            case Back4: DrawButtonImages(context, Win, 255); Box(context, WinRect, 250, 15); break;
            case GradeIn1:
                if (IsForward)
                    DrawButtonImages(context, Win, 255);
                Gradient(context, 100);
                break;
            case GradeIn2: Gradient(context, 200); break;
            case GradeOut1:
                if (!IsForward)
                    DrawButtonImages(context, Win, 255);
                Gradient(context, 150);
                break;
            case GradeOut2:
                if (!IsForward)
                    DrawButtonImages(context, Win, 255);
                Gradient(context, 100);
                break;
            case GradeOut3:
                if (!IsForward)
                    DrawButtonImages(context, Win, 255);
                Gradient(context, 50);
                break;
            case GradeOut4:
                if (!IsForward)
                    DrawButtonImages(context, Win, 255);
                Gradient(context, 17);
                break;
            case ShowBackground:
                DrawButtonImages(context, Win, 255);
                break;
        }
    }
}

/// <summary>WINFADE: the window's image fades in over ten frames.</summary>
sealed class FadeTransition : Transition
{
    const int End = 10;
    public FadeTransition(TransitionHandler h) : base(h) => FrameLength = End;

    public override void Update(int frame)
    {
        DrawState = -1;
        if (frame < 0 || frame > End)
            return;
        if (frame == 0)
        {
            if (!IsForward && Win != null)
            {
                Hide(true);
                IsFinished = true;
            }
        }
        else if (frame < End)
        {
            Hide(true);
            DrawState = frame;
        }
        else if (IsForward && Win != null)
        {
            Hide(false);
            IsFinished = true;
        }
    }

    public override void Draw(GuiContext context)
    {
        if (Win == null || DrawState < 1 || DrawState >= End)
            return;
        context.Images.Draw(context.Batch, Win.EnabledImage, WinRect, Color(255, 255, 255, 25 * DrawState));
    }
}

/// <summary>WINSCALEUP / SCORESCALEUP: the window's image grows from its centre.</summary>
sealed class ScaleUpTransition : Transition
{
    const int End = 6;
    readonly string sound;
    Vector2 center, increment;
    public ScaleUpTransition(TransitionHandler h, string sound) : base(h)
    {
        FrameLength = End;
        this.sound = sound;
    }

    public override void Init(GuiWindow? win)
    {
        base.Init(win);
        center = new Vector2(WinRect.X + WinRect.Width / 2, WinRect.Y + WinRect.Height / 2);
        increment = new Vector2((int)(WinRect.Width / End), (int)(WinRect.Height / End));
    }

    public override void Update(int frame)
    {
        DrawState = -1;
        if (frame < 0 || frame > End)
            return;
        if (frame == 0)
        {
            if (!IsForward && Win != null)
            {
                Hide(true);
                IsFinished = true;
            }
        }
        else if (frame < End)
        {
            if (frame == 1 && IsForward)
                Sound(sound);
            Hide(true);
            DrawState = frame;
        }
        else if (IsForward && Win != null)
        {
            Hide(false);
            IsFinished = true;
        }
    }

    public override void Draw(GuiContext context)
    {
        if (Win == null || DrawState <= 0 || DrawState >= End)
            return;
        float w = increment.X * DrawState, h = increment.Y * DrawState;
        context.Images.Draw(context.Batch, Win.EnabledImage, new Rect(center.X - w / 2, center.Y - h / 2, w, h));
    }
}

/// <summary>MAINMENUSCALEUP: a faction logo flies from its button to the big WinGrowMarker spot.</summary>
sealed class MainMenuScaleUpTransition : Transition
{
    const int End = 5;
    GuiWindow? growWin;
    Rect growRect;
    Vector2 incrementPos, incrementSize;
    public MainMenuScaleUpTransition(TransitionHandler h) : base(h) => FrameLength = End;

    public override void Init(GuiWindow? win)
    {
        if (win != null)
        {
            Win = win;
            WinRect = win.ScreenRect;
        }
        growWin = Handler.FindWindow("MainMenu.wnd:WinGrowMarker");
        if (growWin == null)
            return;
        growRect = growWin.ScreenRect;
        IsForward = false;
        Update(0);
        IsFinished = false;
        IsForward = true;
        incrementPos = new Vector2((int)((growRect.X - WinRect.X) / End), (int)((growRect.Y - WinRect.Y) / End));
        incrementSize = new Vector2((int)((growRect.Width - WinRect.Width) / End), (int)((growRect.Height - WinRect.Height) / End));
        if (Win != null)
            growWin.EnabledImageOverride = Win.DisabledImage;
    }

    public override void Update(int frame)
    {
        DrawState = -1;
        if (frame < 0 || frame > End)
            return;
        if (frame == 0 && !IsForward && Win != null && growWin != null)
        {
            growWin.Hidden = true;
            IsFinished = true;
        }
        if (frame == End && IsForward && Win != null && growWin != null)
        {
            Win.Hidden = true;
            growWin.Hidden = false;
            IsFinished = true;
        }
        if (frame == 1)
            Sound("GUILogoSelect");
        if (frame > 0 && frame < End)
        {
            Hide(true);
            if (growWin != null)
                growWin.Hidden = true;
            DrawState = frame;
        }
    }

    public override void Draw(GuiContext context)
    {
        if (Win == null || growWin == null || DrawState <= 0 || DrawState >= End)
            return;
        float x = WinRect.X + incrementPos.X * DrawState, y = WinRect.Y + incrementPos.Y * DrawState;
        float w = WinRect.Width + incrementSize.X * DrawState, h = WinRect.Height + incrementSize.Y * DrawState;
        context.Images.Draw(context.Batch, growWin.EnabledImage, new Rect(x, y, w, h));
    }
}

/// <summary>
/// MAINMENUMEDIUMSCALEUP / MAINMENUSMALLSCALEDOWN: a faction logo grows (or shrinks) in place into
/// its "...Medium" / "...Small" window.
/// </summary>
sealed class MainMenuGrowTransition : Transition
{
    readonly string suffix;
    readonly bool medium;
    GuiWindow? growWin;
    Vector2 incrementSize;
    public MainMenuGrowTransition(TransitionHandler h, string suffix, int end, bool medium) : base(h)
    {
        FrameLength = end;
        this.suffix = suffix;
        this.medium = medium;
    }

    public override void Init(GuiWindow? win)
    {
        if (win != null)
        {
            Win = win;
            WinRect = win.ScreenRect;
        }
        if (Win == null)
            return;
        growWin = Handler.FindWindow(Win.Name + suffix);
        if (growWin == null)
            return;
        var growRect = growWin.ScreenRect;
        IsForward = false;
        Update(0);
        IsFinished = false;
        IsForward = true;
        incrementSize = new Vector2((int)((growRect.Width - WinRect.Width) / FrameLength), (int)((growRect.Height - WinRect.Height) / FrameLength));
        if (!medium)
            growWin.EnabledImageOverride = Win.EnabledImage;
    }

    public override void Update(int frame)
    {
        DrawState = -1;
        if (frame < 0 || frame > FrameLength)
            return;
        if (frame == 0)
        {
            if (!IsForward && Win != null && growWin != null)
            {
                Win.Hidden = false;
                growWin.Hidden = true;
                IsFinished = true;
            }
        }
        else if (frame == FrameLength)
        {
            if (IsForward && Win != null && growWin != null)
            {
                Win.Hidden = true;
                growWin.Hidden = false;
                IsFinished = true;
            }
        }
        else
        {
            if (medium && frame == 1 && IsForward)
                Sound("GUILogoMouseOver");
            Hide(true);
            if (growWin != null)
                growWin.Hidden = true;
            DrawState = frame;
        }
    }

    public override void Reverse()
    {
        base.Reverse();
        if (medium)
        {
            Hide(true);
            if (growWin != null)
                growWin.Hidden = true;
        }
    }

    public override void Draw(GuiContext context)
    {
        if (Win == null || DrawState <= 0 || DrawState >= FrameLength)
            return;
        float w = incrementSize.X * DrawState, h = incrementSize.Y * DrawState;
        context.Images.Draw(context.Batch, Win.EnabledImage, new Rect(WinRect.X - w / 2, WinRect.Y - h / 2, WinRect.Width + w, WinRect.Height + h));
    }
}

/// <summary>TYPETEXT: a static text types itself out, a letter a frame.</summary>
sealed class TextTypeTransition : Transition
{
    const int End = 30;
    string fullText = "", partialText = "";
    public TextTypeTransition(TransitionHandler h) : base(h) => FrameLength = End;

    public override void Init(GuiWindow? win)
    {
        base.Init(win);
        fullText = Win != null ? Handler.GetText(Win) : "";
        partialText = "";
        FrameLength = Math.Min(fullText.Length, End);
    }

    public override void Update(int frame)
    {
        DrawState = -1;
        if (frame < 0 || frame > End || Win == null)
            return;
        if (frame == 0 && !IsForward)
        {
            Hide(true);
            IsFinished = true;
        }
        if (frame == End && IsForward)
        {
            Hide(false);
            IsFinished = true;
        }
        if (frame >= FrameLength)
            Hide(false);
        if (frame > 0 && frame < FrameLength)
        {
            Hide(true);
            DrawState = frame;
            Sound("GUITypeText");
            if (IsForward)
                partialText += fullText[frame - 1];
            else if (partialText.Length > 0)
                partialText = partialText[..^1];
        }
    }

    public override void Reverse()
    {
        base.Reverse();
        partialText = fullText;
    }

    public override void Skip() => Update(End);

    public override void Draw(GuiContext context)
    {
        if (Win == null || DrawState <= 0 || DrawState >= FrameLength)
            return;
        var font = context.Font(Win.Definition);
        var color = Win.Definition.TextEnabled;
        var shadow = Win.Definition.TextEnabledBorder;
        bool centered = Win.Definition.Data("STATICTEXTDATA", "CENTERED") == "1";
        float width = font.Measure(partialText);
        float x = centered ? WinRect.X + (WinRect.Width - width) / 2 : WinRect.X + 7;
        float y = WinRect.Y + (WinRect.Height - font.LineHeight) / 2;
        font.DrawWithShadow(context.Batch, partialText, new Vector2(MathF.Round(x), MathF.Round(y)), color, shadow);
    }
}

/// <summary>COUNTUP: a number counts up from zero (score screen).</summary>
sealed class CountUpTransition : Transition
{
    const int End = 30;
    string fullText = "";
    int intValue, currentValue, countStep;
    public CountUpTransition(TransitionHandler h) : base(h) => FrameLength = End;

    public override void Init(GuiWindow? win)
    {
        if (win != null)
        {
            Win = win;
            WinRect = win.ScreenRect;
            if (win.Hidden)
            {
                IsForward = true;
                IsFinished = true;
                FrameLength = 0;
                return;
            }
        }
        if (Win == null)
            return;
        fullText = Handler.GetText(Win);
        IsForward = false;
        Update(0);
        IsFinished = false;
        IsForward = true;
        int.TryParse(new string(fullText.TakeWhile(c => char.IsDigit(c) || c == '-').ToArray()), out intValue);
        if (intValue < End)
        {
            countStep = 1;
            FrameLength = Math.Min(intValue, End);
        }
        else if (intValue / 100 < End)
        {
            countStep = 100;
            FrameLength = Math.Min(intValue / 100, End);
        }
        else
        {
            countStep = 1000;
            FrameLength = Math.Min(intValue / 1000, End);
        }
        currentValue = 0;
        Win.Text = "0";
    }

    public override void Update(int frame)
    {
        DrawState = -1;
        if (frame < 0 || frame > End || Win == null)
            return;
        if (frame == 0 && !IsForward)
        {
            currentValue = 0;
            Win.Text = "0";
            Hide(true);
            IsFinished = true;
        }
        if (frame == End && IsForward)
        {
            Hide(false);
            IsFinished = true;
        }
        if (frame >= FrameLength)
            Hide(false);
        if (frame > 0 && frame < FrameLength)
        {
            Hide(false);
            DrawState = frame;
            Sound("GUIScoreScreenTick");
            currentValue = Math.Min(intValue, currentValue + countStep);
            Win.Text = currentValue.ToString();
        }
        if (frame == FrameLength)
        {
            Win.Text = fullText;
            IsFinished = true;
        }
    }

    public override void Reverse()
    {
        if (Win is { Hidden: true })
        {
            IsForward = false;
            IsFinished = true;
            FrameLength = 0;
            return;
        }
        base.Reverse();
    }

    public override void Skip()
    {
        if (!IsFinished)
            Update(End);
    }

    public override void Draw(GuiContext context) { }
}

/// <summary>SCREENFADE: the whole screen fades to black over thirty frames.</summary>
sealed class ScreenFadeTransition : Transition
{
    const int End = 30;
    public ScreenFadeTransition(TransitionHandler h) : base(h) => FrameLength = End;

    public override void Init(GuiWindow? win)
    {
        IsForward = false;
        Update(0);
        IsFinished = false;
        IsForward = true;
    }

    public override void Update(int frame)
    {
        DrawState = -1;
        if (frame < 0 || frame > End)
            return;
        if (frame == 0 || frame == End)
            IsFinished = true;
        DrawState = frame;
    }

    public override void Draw(GuiContext context)
    {
        if (DrawState < 0)
            return;
        int alpha = Math.Min(255, (int)(1f / (End - 1) * 255 * DrawState));
        context.Batch.FillRect(new Rect(0, 0, Handler.ScreenSize.X, Handler.ScreenSize.Y), Color(0, 0, 0, alpha));
    }
}

/// <summary>FULLFADE: the window fades out to black and back in, with a blue frame.</summary>
sealed class FullFadeTransition : Transition
{
    const int End = 10;
    public FullFadeTransition(TransitionHandler h) : base(h) => FrameLength = End;

    public override void Update(int frame)
    {
        DrawState = -1;
        if (frame < 0 || frame > End || Win == null)
            return;
        if (frame == 0 && !IsForward)
        {
            Hide(true);
            IsFinished = true;
        }
        if (frame == End && IsForward)
        {
            Hide(false);
            IsFinished = true;
        }
        if (frame == End / 2)
            Hide(!IsForward);
        DrawState = frame;
    }

    public override void Draw(GuiContext context)
    {
        if (DrawState < 0)
            return;
        float percent = 1f / (End / 2);
        int alpha = DrawState > End / 2 ? (int)(percent * 255 * (End - DrawState)) : (int)(percent * 255 * DrawState);
        alpha = Math.Min(255, alpha);
        context.Batch.FillRect(WinRect, Color(0, 0, 0, alpha));
        context.Batch.DrawRect(WinRect, Color(60, 60, 180, alpha));
    }
}

/// <summary>TEXTONFRAME: the window simply appears at the end (or vanishes at the start when reversed).</summary>
sealed class TextOnFrameTransition : Transition
{
    const int End = 1;
    public TextOnFrameTransition(TransitionHandler h) : base(h) => FrameLength = End;

    public override void Init(GuiWindow? win)
    {
        if (win != null)
            Win = win;
        if (Win is { Hidden: true })
        {
            IsFinished = true;
            IsForward = true;
            FrameLength = 0;
            return;
        }
        IsForward = false;
        Update(0);
        IsFinished = false;
        IsForward = true;
    }

    public override void Update(int frame)
    {
        if (frame < 0 || frame > End || Win == null)
            return;
        if (frame == 0 && !IsForward)
        {
            Hide(true);
            IsFinished = true;
        }
        if (frame == End && IsForward)
        {
            Hide(false);
            IsFinished = true;
        }
    }

    public override void Reverse()
    {
        base.Reverse();
        if (Win is { Hidden: true })
        {
            IsFinished = true;
            FrameLength = 0;
        }
    }

    public override void Skip()
    {
        if (!IsFinished)
            Update(End);
    }

    public override void Draw(GuiContext context) { }
}

/// <summary>REVERSESOUND: plays GUITransitionFade when the group is played backwards.</summary>
sealed class ReverseSoundTransition : Transition
{
    const int End = 2;
    public ReverseSoundTransition(TransitionHandler h) : base(h) => FrameLength = End;

    public override void Init(GuiWindow? win)
    {
        IsFinished = true;
        IsForward = true;
    }

    public override void Update(int frame)
    {
        if (frame < 0 || frame > End)
            return;
        if (frame == 0 && !IsForward)
            IsFinished = true;
        if (frame == 1)
            Sound("GUITransitionFade");
        if ((frame == 1 || frame == End) && IsForward)
            IsFinished = true;   // (the original falls through from the sound case to the end case)
    }

    public override void Skip()
    {
        if (!IsFinished)
            Update(End);
    }

    public override void Draw(GuiContext context) { }
}

/// <summary>CONTROLBARARROW: an arrow drops onto the General's button and fades (new science point).</summary>
sealed class ControlBarArrowTransition : Transition
{
    const int BeginFade = 16, End = 22;
    Vector2 pos, size;
    float incrementY;
    string? arrowImage;
    public ControlBarArrowTransition(TransitionHandler h) : base(h) => FrameLength = End;

    /// <summary>The control bar's arrow image (set by the control bar).</summary>
    public static string? ArrowImage { get; set; }

    public override void Init(GuiWindow? win)
    {
        IsForward = false;
        Update(0);
        IsFinished = false;
        IsForward = true;
        arrowImage = ArrowImage;
        var button = Handler.FindWindow("ControlBar.wnd:ButtonGeneral");
        if (button == null || arrowImage == null)
        {
            IsFinished = true;
            return;
        }
        var r = button.ScreenRect;
        incrementY = r.Y / BeginFade;
        size = new Vector2(48, 48);
        pos = new Vector2(r.X + r.Width / 2 - size.X / 2, -size.Y + 20);
    }

    public override void Update(int frame)
    {
        DrawState = -1;
        if (frame < 0 || frame > End)
            return;
        if (frame == 0 || frame == End)
            IsFinished = true;
        DrawState = frame;
    }

    public override void Draw(GuiContext context)
    {
        if (DrawState < 0 || arrowImage == null)
            return;
        if (context.Images.Find(arrowImage) is { } image)
            size = new Vector2(image.PixelWidth, image.PixelHeight);
        if (DrawState < BeginFade)
        {
            float y = pos.Y + incrementY * DrawState;
            context.Images.Draw(context.Batch, arrowImage, new Rect(pos.X, y, size.X, size.Y));
        }
        else
        {
            float fade = 1f / (End - BeginFade);
            int alpha = Math.Min(255, (int)((1 - fade * (DrawState - BeginFade)) * 255));
            float y = pos.Y + incrementY * (BeginFade - 1);
            context.Images.Draw(context.Batch, arrowImage, new Rect(pos.X, y, size.X, size.Y), Color(255, 255, 255, alpha));
        }
    }
}

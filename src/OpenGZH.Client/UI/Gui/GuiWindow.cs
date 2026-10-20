using System.Numerics;
using OpenGZH.Formats.Strings;
using OpenGZH.Formats.Wnd;

namespace OpenGZH.Client.UI.Gui;

/// <summary>Everything windows need to draw themselves.</summary>
public sealed class GuiContext
{
    public required SpriteBatch Batch { get; init; }
    public required ImageCatalog Images { get; init; }
    public required FontLibrary Fonts { get; init; }
    public required StringTable Strings { get; init; }

    /// <summary>Scale from a layout's creation resolution (usually 800x600) to the screen.</summary>
    public Vector2 Scale { get; set; } = Vector2.One;

    public GameFont Font(WndWindow definition) => Fonts.Get(definition.FontName, definition.FontSize, definition.FontBold);

    /// <summary>Resolves a string label ("GUI:Skirmish") to text; literal text is returned unchanged.</summary>
    public string Text(string labelOrText) =>
        labelOrText.Contains(':') && !labelOrText.Contains(' ') ? Strings.Get(labelOrText) : labelOrText;
}

/// <summary>
/// A window from a .wnd layout: a panel, button, label, list... Built from its <see cref="WndWindow"/>
/// definition and drawn with the original images and colours. Subclasses handle each gadget type.
/// </summary>
public class GuiWindow
{
    public WndWindow Definition { get; }
    public GuiWindow? Parent { get; private set; }
    public List<GuiWindow> Children { get; } = new();
    public string Name => Definition.Name;

    public bool Hidden { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Hilited { get; protected set; }

    /// <summary>Text shown on the window; starts as the layout's (localized) TEXT.</summary>
    public string? Text { get; set; }

    /// <summary>Lets code draw something extra on top (e.g. the radar, a unit portrait).</summary>
    public Action<GuiWindow, GuiContext>? CustomDraw { get; set; }

    /// <summary>Raised when a button is clicked (or a list item chosen, a box ticked...).</summary>
    public event Action<GuiWindow>? Clicked;

    public Rect ScreenRect { get; private set; }

    public GuiWindow(WndWindow definition)
    {
        Definition = definition;
        Hidden = definition.Status.Contains("HIDDEN");
        Enabled = definition.Status.Contains("ENABLED");
    }

    public bool IsImageWindow => Definition.Status.Contains("IMAGE");
    public bool IsSeeThrough => Definition.Status.Contains("SEE_THRU");
    public bool AcceptsInput => !Definition.Status.Contains("NOINPUT");
    public bool IsVisible => !Hidden && (Parent?.IsVisible ?? true);
    public bool IsEnabled => Enabled && (Parent?.IsEnabled ?? true);

    internal void AddChild(GuiWindow child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public void Layout(Vector2 scale)
    {
        var d = Definition;
        float sx = scale.X * 800f / Math.Max(1, d.CreationWidth);
        float sy = scale.Y * 600f / Math.Max(1, d.CreationHeight);
        ScreenRect = new Rect(d.X * sx, d.Y * sy, d.Width * sx, d.Height * sy);
        foreach (var child in Children)
            child.Layout(scale);
    }

    public GuiWindow? Find(string name)
    {
        if (Name.Equals(name, StringComparison.OrdinalIgnoreCase) || Name.EndsWith(":" + name, StringComparison.OrdinalIgnoreCase))
            return this;
        foreach (var child in Children)
            if (child.Find(name) is { } found)
                return found;
        return null;
    }

    // ---- Drawing ----

    /// <summary>
    /// The layout's DRAWCALLBACK decides how a window draws: "[None]" and the default callbacks use the
    /// standard drawing for its type, "W3DNoDraw" draws nothing, and any other name is a hook the game
    /// code fills in through <see cref="CustomDraw"/> (radar, power bar, portraits...).
    /// </summary>
    public bool UsesStandardDraw => Definition.DrawCallback is "" or "W3DGameWinDefaultDraw" or "GameWinDefaultDraw"
        || Definition.DrawCallback.StartsWith("W3DGadget", StringComparison.OrdinalIgnoreCase);

    public void Draw(GuiContext context)
    {
        if (Hidden)
            return;
        if (UsesStandardDraw)
            DrawSelf(context);
        CustomDraw?.Invoke(this, context);
        foreach (var child in Children)
            child.Draw(context);
    }

    /// <summary>The original default draw: the state's first image stretched over the window, or a coloured box.</summary>
    protected virtual void DrawSelf(GuiContext context)
    {
        var data = StateData(0);
        if (IsImageWindow)
        {
            if (data is { HasImage: true })
                context.Images.Draw(context.Batch, data.Image, ScreenRect);
        }
        else if (data != null && !IsSeeThrough)
        {
            context.Batch.FillRect(ScreenRect, data.Color);
            if (Definition.Status.Contains("BORDER"))
                context.Batch.DrawRect(ScreenRect, data.BorderColor);
        }
        DrawText(context, centered: true);
    }

    /// <summary>The draw data for the current state (disabled / hilited / enabled).</summary>
    protected WndDrawData? StateData(int index) =>
        !IsEnabled ? Definition.DisabledData(index) ?? Definition.EnabledData(index)
        : Hilited ? Definition.HiliteData(index) ?? Definition.EnabledData(index)
        : Definition.EnabledData(index);

    protected (Vector4 Color, Vector4 Shadow) TextColors() =>
        !IsEnabled ? (Definition.TextDisabled, Definition.TextDisabledBorder)
        : Hilited ? (Definition.TextHilite, Definition.TextHiliteBorder)
        : (Definition.TextEnabled, Definition.TextEnabledBorder);

    protected string CurrentText(GuiContext context) => Text ?? context.Text(Definition.Text);

    protected void DrawText(GuiContext context, bool centered, bool centerVertically = true, float margin = 2)
    {
        string text = CurrentText(context);
        if (text.Length == 0)
            return;
        var font = context.Font(Definition);
        var (color, shadow) = TextColors();
        var lines = font.WrapLines(text, ScreenRect.Width - 4);
        float totalHeight = lines.Count * font.LineHeight;
        float y = centerVertically ? ScreenRect.Y + (ScreenRect.Height - totalHeight) / 2 : ScreenRect.Y + margin;
        foreach (string line in lines)
        {
            float width = font.Measure(line);
            float x = centered ? ScreenRect.X + (ScreenRect.Width - width) / 2 : ScreenRect.X + margin;
            font.DrawWithShadow(context.Batch, line, new Vector2(MathF.Round(x), MathF.Round(y)), color, shadow);
            y += font.LineHeight;
        }
    }

    // ---- Input ----

    /// <summary>The deepest visible window under the point that accepts input.</summary>
    public GuiWindow? HitTest(Vector2 point)
    {
        if (Hidden || !ScreenRect.Contains(point) && Children.Count == 0)
            return null;
        for (int i = Children.Count - 1; i >= 0; i--)
            if (Children[i].HitTest(point) is { } hit)
                return hit;
        return ScreenRect.Contains(point) && AcceptsInput && !IsSeeThroughForInput ? this : null;
    }

    /// <summary>Plain see-through panels don't catch the mouse; their gadgets do.</summary>
    protected virtual bool IsSeeThroughForInput => IsSeeThrough && Definition.Type == "USER";

    /// <summary>Windows with the MOUSETRACK style light up under the mouse.</summary>
    public virtual void OnMouseEnter() => Hilited = IsEnabled && Definition.Style.Contains("MOUSETRACK");
    public virtual void OnMouseLeave() => Hilited = false;
    public virtual void OnMouseDown(Vector2 point) { }
    public virtual void OnMouseUp(Vector2 point, bool inside) { }
    public virtual void OnScroll(float amount) { }
    public virtual void OnKey(char character) { }

    protected void RaiseClicked() => Clicked?.Invoke(this);

    public override string ToString() => Definition.ToString();
}

/// <summary>A push button: one stretched image, or left/middle/right images that tile across.</summary>
public class GuiPushButton : GuiWindow
{
    bool pressed;

    /// <summary>For toggle buttons (and the selected command bar slot).</summary>
    public bool Selected { get; set; }

    public GuiPushButton(WndWindow definition) : base(definition) { }

    protected override void DrawSelf(GuiContext context)
    {
        bool down = pressed && Hilited || Selected;
        var d = Definition;

        // Images by state, following the original layout of the draw data (see GadgetPushButton.h).
        string? left, middle, right;
        if (!IsEnabled)
            (left, middle, right) = Images(d.DisabledData(0), d.DisabledData(5), d.DisabledData(6));
        else if (down)
            (left, middle, right) = Images(d.HiliteData(1), d.HiliteData(3), d.HiliteData(4));
        else if (Hilited)
            (left, middle, right) = Images(d.HiliteData(0), d.HiliteData(5), d.HiliteData(6));
        else
            (left, middle, right) = Images(d.EnabledData(0), d.EnabledData(5), d.EnabledData(6));

        var r = ScreenRect;
        if (IsImageWindow && middle != null && context.Images.Find(left) is { } leftImage && context.Images.Find(right) is { } rightImage)
        {
            // Three-part button: end caps keep their aspect ratio, the middle stretches.
            float leftWidth = leftImage.PixelWidth * r.Height / Math.Max(1, leftImage.PixelHeight);
            float rightWidth = rightImage.PixelWidth * r.Height / Math.Max(1, rightImage.PixelHeight);
            context.Images.Draw(context.Batch, leftImage, new Rect(r.X, r.Y, leftWidth, r.Height));
            context.Images.Draw(context.Batch, middle, new Rect(r.X + leftWidth, r.Y, r.Width - leftWidth - rightWidth, r.Height));
            context.Images.Draw(context.Batch, rightImage, new Rect(r.Right - rightWidth, r.Y, rightWidth, r.Height));
        }
        else if (IsImageWindow)
        {
            // One image per state: enabled[0], hilite[0], pushed = hilite[1], disabled[0].
            var image = !IsEnabled ? d.DisabledData(0) : down ? d.HiliteData(1) ?? d.HiliteData(0) : Hilited ? d.HiliteData(0) : d.EnabledData(0);
            if (image is { HasImage: true })
                context.Images.Draw(context.Batch, image.Image, r);
            else if (d.EnabledData(0) is { HasImage: true } fallback)
                context.Images.Draw(context.Batch, fallback.Image, r);
        }
        else
        {
            var data = !IsEnabled ? d.DisabledData(0) : down ? d.HiliteData(1) : Hilited ? d.HiliteData(0) : d.EnabledData(0);
            if (data != null)
            {
                context.Batch.FillRect(r, data.Color);
                context.Batch.DrawRect(r, data.BorderColor);
            }
        }

        DrawText(context, centered: true);
    }

    static (string?, string?, string?) Images(WndDrawData? left, WndDrawData? middle, WndDrawData? right) =>
        (left?.HasImage == true ? left.Image : null, middle?.HasImage == true ? middle.Image : null, right?.HasImage == true ? right.Image : null);

    public override void OnMouseEnter() => Hilited = IsEnabled;
    public override void OnMouseDown(Vector2 point) => pressed = IsEnabled;

    public override void OnMouseUp(Vector2 point, bool inside)
    {
        bool wasPressed = pressed;
        pressed = false;
        if (wasPressed && inside && IsEnabled)
            RaiseClicked();
    }
}

/// <summary>A text label (STATICTEXT); CENTERED in its STATICTEXTDATA centres it horizontally.</summary>
public sealed class GuiStaticText : GuiWindow
{
    public GuiStaticText(WndWindow definition) : base(definition) { }

    protected override void DrawSelf(GuiContext context)
    {
        var data = StateData(0);
        if (IsImageWindow && data is { HasImage: true })
            context.Images.Draw(context.Batch, data.Image, ScreenRect);
        else if (data != null && data.Color.W > 0 && !IsSeeThrough)
            context.Batch.FillRect(ScreenRect, data.Color);
        bool centered = Definition.Data("STATICTEXTDATA", "CENTERED") == "1";
        DrawText(context, centered, centerVertically: true, margin: 5);
    }

    protected override bool IsSeeThroughForInput => true;
}

/// <summary>A tick box: the box image on the left, the label beside it.</summary>
public sealed class GuiCheckBox : GuiWindow
{
    public bool Checked { get; set; }

    public GuiCheckBox(WndWindow definition) : base(definition) { }

    protected override void DrawSelf(GuiContext context)
    {
        var d = Definition;
        float size = ScreenRect.Height;
        var box = new Rect(ScreenRect.X, ScreenRect.Y, size, size);
        // Checked box image is entry 2 ("CheckBoxCheckedEnabled..."), unchecked entry 1, background entry 0.
        var background = !IsEnabled ? d.DisabledData(0) : Hilited ? d.HiliteData(0) : d.EnabledData(0);
        var mark = !IsEnabled ? d.DisabledData(Checked ? 2 : 1) : Hilited ? d.HiliteData(Checked ? 2 : 1) : d.EnabledData(Checked ? 2 : 1);
        if (background is { HasImage: true })
            context.Images.Draw(context.Batch, background.Image, ScreenRect);
        if (mark is { HasImage: true })
            context.Images.Draw(context.Batch, mark.Image, box);
        else
        {
            context.Batch.DrawRect(box.Inflate(-2), TextColors().Color);
            if (Checked)
                context.Batch.FillRect(box.Inflate(-5), TextColors().Color);
        }

        string text = CurrentText(context);
        if (text.Length > 0)
        {
            var font = context.Font(d);
            var (color, shadow) = TextColors();
            font.DrawWithShadow(context.Batch, text, new Vector2(box.Right + 4, ScreenRect.Y + (ScreenRect.Height - font.LineHeight) / 2), color, shadow);
        }
    }

    public override void OnMouseUp(Vector2 point, bool inside)
    {
        if (!inside || !IsEnabled)
            return;
        Checked = !Checked;
        RaiseClicked();
    }
}

/// <summary>One of a group of exclusive choices (RADIOBUTTONDATA GROUP).</summary>
public sealed class GuiRadioButton : GuiPushButton
{
    public int Group => int.TryParse(Definition.Data("RADIOBUTTONDATA", "GROUP"), out int g) ? g : 0;

    public GuiRadioButton(WndWindow definition) : base(definition) { }

    public override void OnMouseUp(Vector2 point, bool inside)
    {
        if (!inside || !IsEnabled)
            return;
        if (Parent != null)
            foreach (var sibling in Parent.Children.OfType<GuiRadioButton>().Where(r => r.Group == Group))
                sibling.Selected = false;
        Selected = true;
        base.OnMouseUp(point, inside);
    }
}

/// <summary>A horizontal bar filled to a percentage (PROGRESSBAR); used for loading and build progress.</summary>
public sealed class GuiProgressBar : GuiWindow
{
    public float Progress { get; set; }

    public GuiProgressBar(WndWindow definition) : base(definition) { }

    protected override void DrawSelf(GuiContext context)
    {
        var d = Definition;
        var r = ScreenRect;
        if (d.EnabledData(0) is { HasImage: true } background)
            context.Images.Draw(context.Batch, background.Image, r);
        else if (d.EnabledData(0) is { } fill)
            context.Batch.FillRect(r, fill.Color);
        var bar = new Rect(r.X, r.Y, r.Width * Math.Clamp(Progress, 0, 1), r.Height);
        // The bar itself is entry 4 (middle image) in the original, or the hilite colour.
        if (d.EnabledData(4) is { HasImage: true } barImage)
            context.Images.Draw(context.Batch, barImage.Image, bar);
        else if (d.HiliteData(0) is { } hilite)
            context.Batch.FillRect(bar, hilite.Color);
    }
}

/// <summary>A scrolling list of lines (LISTBOX); click selects, wheel scrolls.</summary>
public sealed class GuiListBox : GuiWindow
{
    public List<string> Items { get; } = new();
    public int SelectedIndex { get; set; } = -1;
    public int ScrollOffset { get; private set; }
    public string? SelectedItem => SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;

    GameFont? lastFont;

    public GuiListBox(WndWindow definition) : base(definition) { }

    protected override void DrawSelf(GuiContext context)
    {
        var r = ScreenRect;
        if (Definition.EnabledData(0) is { } background)
        {
            if (background.HasImage)
                context.Images.Draw(context.Batch, background.Image, r);
            else if (!IsSeeThrough)
                context.Batch.FillRect(r, background.Color);
        }
        var font = lastFont = context.Font(Definition);
        var previousClip = context.Batch.Clip;
        context.Batch.SetClip(r);
        float y = r.Y + 2;
        for (int i = ScrollOffset; i < Items.Count && y < r.Bottom; i++)
        {
            if (i == SelectedIndex && Definition.EnabledData(1) is { } selected)
                context.Batch.FillRect(new Rect(r.X + 1, y, r.Width - 2, font.LineHeight), selected.Color with { W = Math.Max(0.35f, selected.Color.W) });
            var (color, shadow) = TextColors();
            font.DrawWithShadow(context.Batch, Items[i], new Vector2(r.X + 4, y), color, shadow);
            y += font.LineHeight;
        }
        context.Batch.SetClip(previousClip);
    }

    public override void OnMouseUp(Vector2 point, bool inside)
    {
        if (!inside || lastFont == null)
            return;
        int index = ScrollOffset + (int)((point.Y - ScreenRect.Y - 2) / lastFont.LineHeight);
        if (index >= 0 && index < Items.Count)
        {
            SelectedIndex = index;
            RaiseClicked();
        }
    }

    public override void OnScroll(float amount)
    {
        ScrollOffset = Math.Clamp(ScrollOffset - Math.Sign(amount) * 3, 0, Math.Max(0, Items.Count - 1));
    }
}

/// <summary>A one-line text field (ENTRYFIELD).</summary>
public sealed class GuiTextEntry : GuiWindow
{
    public int MaxLength => int.TryParse(Definition.Data("TEXTENTRYDATA", "MAXLEN"), out int max) ? max : 64;

    public GuiTextEntry(WndWindow definition) : base(definition) => Text = "";

    protected override void DrawSelf(GuiContext context)
    {
        var data = StateData(0);
        if (data is { HasImage: true })
            context.Images.Draw(context.Batch, data.Image, ScreenRect);
        else if (data != null)
        {
            context.Batch.FillRect(ScreenRect, data.Color);
            context.Batch.DrawRect(ScreenRect, data.BorderColor);
        }
        DrawText(context, centered: false, centerVertically: true, margin: 4);
    }

    public override void OnKey(char character)
    {
        Text ??= "";
        if (character == '\b')
            Text = Text.Length > 0 ? Text[..^1] : "";
        else if (!char.IsControl(character) && Text.Length < MaxLength)
            Text += character;
    }
}

/// <summary>A slider (HORZSLIDER / VERTSLIDER) between SLIDERDATA MINVALUE and MAXVALUE.</summary>
public sealed class GuiSlider : GuiWindow
{
    public int Minimum => int.TryParse(Definition.Data("SLIDERDATA", "MINVALUE"), out int v) ? v : 0;
    public int Maximum => int.TryParse(Definition.Data("SLIDERDATA", "MAXVALUE"), out int v) ? v : 100;
    public int Value { get; set; }
    bool Vertical => Definition.Type.Equals("VERTSLIDER", StringComparison.OrdinalIgnoreCase);
    bool dragging;

    public GuiSlider(WndWindow definition) : base(definition) { }

    protected override void DrawSelf(GuiContext context)
    {
        var r = ScreenRect;
        if (Definition.EnabledData(0) is { HasImage: true } track)
            context.Images.Draw(context.Batch, track.Image, r);
        else
            context.Batch.FillRect(r, new Vector4(0, 0, 0, 0.4f));
        float t = Maximum > Minimum ? (Value - Minimum) / (float)(Maximum - Minimum) : 0;
        var thumb = Vertical
            ? new Rect(r.X, r.Bottom - t * r.Height - 6, r.Width, 12)
            : new Rect(r.X + t * r.Width - 6, r.Y, 12, r.Height);
        context.Batch.FillRect(thumb, TextColors().Color);
    }

    public override void OnMouseDown(Vector2 point)
    {
        dragging = true;
        SetFrom(point);
    }

    public override void OnMouseUp(Vector2 point, bool inside)
    {
        if (dragging)
            RaiseClicked();
        dragging = false;
    }

    public void Drag(Vector2 point)
    {
        if (dragging)
            SetFrom(point);
    }

    void SetFrom(Vector2 point)
    {
        var r = ScreenRect;
        float t = Vertical ? 1 - (point.Y - r.Y) / r.Height : (point.X - r.X) / r.Width;
        Value = Minimum + (int)MathF.Round(Math.Clamp(t, 0, 1) * (Maximum - Minimum));
    }
}

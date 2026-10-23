using System.Numerics;
using OpenGZH.Formats.Wnd;

namespace OpenGZH.Client.UI.Gui;

/// <summary>
/// A drop-down list (COMBOBOX, the original's GadgetComboBox): the chosen entry in an edit-box frame
/// with a drop-down arrow; clicking opens a list of up to MAXDISPLAY entries over everything else.
/// Each entry has text, a colour and a value (the original's item data). Choosing one raises
/// <see cref="SelectionChanged"/> (the original's GCM_SELECTED).
/// </summary>
public sealed class GuiComboBox : GuiWindow
{
    public sealed record Entry(string Text, Vector4 Color, object? Data);

    public List<Entry> Entries { get; } = new();
    public int SelectedIndex { get; private set; } = -1;
    public Entry? Selected => SelectedIndex >= 0 && SelectedIndex < Entries.Count ? Entries[SelectedIndex] : null;
    public object? SelectedData => Selected?.Data;

    /// <summary>The selection changed through the player.</summary>
    public event Action<GuiComboBox>? SelectionChanged;

    /// <summary>The one combo box whose list is open (it draws and takes clicks above everything).</summary>
    public static GuiComboBox? Open { get; private set; }

    int scrollOffset, hoverIndex = -1;
    float lineHeight = 14;

    public GuiComboBox(WndWindow definition) : base(definition) { }

    int MaxDisplay => int.TryParse(Definition.Data("COMBOBOXDATA", "MAXDISPLAY"), out int n) && n > 0 ? n : 5;

    public void Reset()
    {
        Entries.Clear();
        SelectedIndex = -1;
        scrollOffset = 0;
    }

    public int AddEntry(string text, Vector4 color, object? data = null)
    {
        Entries.Add(new Entry(text, color, data));
        return Entries.Count - 1;
    }

    /// <summary>Sets the selection without telling anyone (the original's GadgetComboBoxSetSelectedPos with dontHide).</summary>
    public void SetSelectedPos(int index) => SelectedIndex = index >= -1 && index < Entries.Count ? index : -1;

    public int IndexOfData(object? data) => Entries.FindIndex(e => Equals(e.Data, data));

    Rect ListRect
    {
        get
        {
            int rows = Math.Min(MaxDisplay, Math.Max(1, Entries.Count));
            return new Rect(ScreenRect.X, ScreenRect.Bottom, ScreenRect.Width, rows * lineHeight + 4);
        }
    }

    Rect ButtonRect => new(ScreenRect.Right - ScreenRect.Height, ScreenRect.Y, ScreenRect.Height, ScreenRect.Height);

    protected override void DrawSelf(GuiContext context)
    {
        var d = Definition;
        string state = !IsEnabled ? "DISABLED" : Hilited ? "HILITE" : "ENABLED";
        var r = ScreenRect;
        var box = new Rect(r.X, r.Y, r.Width - ButtonRect.Width, r.Height);

        // The edit box frame: left end, right end, repeating centre (TextEntry images).
        string? left = Image(d.Extra($"COMBOBOXEDITBOX{state}DRAWDATA", 0));
        string? right = Image(d.Extra($"COMBOBOXEDITBOX{state}DRAWDATA", 1));
        string? center = Image(d.Extra($"COMBOBOXEDITBOX{state}DRAWDATA", 2));
        if (left != null && right != null && center != null && context.Images.Find(left) is { } l && context.Images.Find(right) is { } rr)
        {
            float lw = l.PixelWidth * box.Height / Math.Max(1, l.PixelHeight);
            float rw = rr.PixelWidth * box.Height / Math.Max(1, rr.PixelHeight);
            context.Images.Draw(context.Batch, l, new Rect(box.X, box.Y, lw, box.Height));
            context.Images.Draw(context.Batch, center, new Rect(box.X + lw, box.Y, Math.Max(0, box.Width - lw - rw), box.Height));
            context.Images.Draw(context.Batch, rr, new Rect(box.Right - rw, box.Y, rw, box.Height));
        }
        else
        {
            context.Batch.FillRect(box, new Vector4(0, 0, 0, 0.7f));
            context.Batch.DrawRect(box, d.TextEnabled);
        }

        // The drop-down arrow.
        var button = d.Extra($"COMBOBOXDROPDOWNBUTTON{state}DRAWDATA", Open == this ? 1 : 0) ?? d.Extra($"COMBOBOXDROPDOWNBUTTON{state}DRAWDATA", 0);
        if (Image(button) is { } arrow)
            context.Images.Draw(context.Batch, arrow, ButtonRect);

        // The chosen entry.
        var font = context.Font(d);
        lineHeight = font.LineHeight + 2;
        if (Selected is { } entry)
        {
            var (_, shadow) = TextColors();
            var color = IsEnabled ? entry.Color : d.TextDisabled;
            var previous = context.Batch.Clip;
            context.Batch.SetClip(box);
            font.DrawWithShadow(context.Batch, entry.Text, new Vector2(MathF.Round(box.X + 4), MathF.Round(box.Y + (box.Height - font.LineHeight) / 2)), color, shadow);
            context.Batch.SetClip(previous);
        }
    }

    static string? Image(WndDrawData? data) => data is { HasImage: true } ? data.Image : null;

    /// <summary>Draws the open list (called after every layout so it sits on top).</summary>
    public void DrawList(GuiContext context)
    {
        if (Open != this || !IsVisible)
            return;
        var d = Definition;
        var list = ListRect;
        var background = d.Extra("COMBOBOXLISTBOXENABLEDDRAWDATA", 0);
        if (Image(background) is { } image)
            context.Images.Draw(context.Batch, image, list);
        else
            context.Batch.FillRect(list, new Vector4(0, 0, 0, 1));
        context.Batch.DrawRect(list, background?.BorderColor ?? new Vector4(0.19f, 0.21f, 0.66f, 1));
        var font = context.Font(d);
        var previous = context.Batch.Clip;
        context.Batch.SetClip(list);
        float y = list.Y + 2;
        for (int i = scrollOffset; i < Entries.Count && i < scrollOffset + MaxDisplay; i++)
        {
            var row = new Rect(list.X + 1, y, list.Width - 2, lineHeight);
            if (i == hoverIndex || i == SelectedIndex)
            {
                var hilite = d.Extra("COMBOBOXLISTBOXHILITEDRAWDATA", 3) ?? d.Extra("COMBOBOXLISTBOXENABLEDDRAWDATA", 3);
                if (Image(hilite) is { } bar && i == hoverIndex)
                    context.Images.Draw(context.Batch, bar, row);
                else
                    context.Batch.FillRect(row, new Vector4(0.2f, 0.25f, 0.7f, i == hoverIndex ? 0.8f : 0.45f));
            }
            font.DrawWithShadow(context.Batch, Entries[i].Text, new Vector2(MathF.Round(row.X + 4), MathF.Round(y + 1)), Entries[i].Color, d.TextEnabledBorder);
            y += lineHeight;
        }
        context.Batch.SetClip(previous);
    }

    /// <summary>Whether a point is on the open list (the GUI manager asks before anything else).</summary>
    public bool ListContains(Vector2 point) => Open == this && ListRect.Contains(point);

    public void ListMouseMove(Vector2 point)
    {
        int index = scrollOffset + (int)((point.Y - ListRect.Y - 2) / lineHeight);
        hoverIndex = index >= 0 && index < Entries.Count ? index : -1;
    }

    public void ListClick(Vector2 point)
    {
        int index = scrollOffset + (int)((point.Y - ListRect.Y - 2) / lineHeight);
        Close();
        if (index < 0 || index >= Entries.Count)
            return;
        SelectedIndex = index;
        SelectionChanged?.Invoke(this);
    }

    public void ListScroll(float amount) =>
        scrollOffset = Math.Clamp(scrollOffset - Math.Sign(amount), 0, Math.Max(0, Entries.Count - MaxDisplay));

    public static void Close()
    {
        if (Open != null)
            Open.hoverIndex = -1;
        Open = null;
    }

    public override void OnMouseEnter() => Hilited = IsEnabled;

    public override void OnMouseUp(Vector2 point, bool inside)
    {
        if (!inside || !IsEnabled)
            return;
        if (Open == this)
        {
            Close();
            return;
        }
        Open = this;
        // Show the chosen entry in the list.
        scrollOffset = Math.Clamp(SelectedIndex - MaxDisplay / 2, 0, Math.Max(0, Entries.Count - MaxDisplay));
    }
}

using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.UI;
using OpenGZH.Client.UI.Gui;
using OpenGZH.Logic.Multiplayer;

namespace OpenGZH.Client.Shell;

/// <summary>
/// The map preview in the skirmish screens (the original's W3DDrawMapPreview and findDrawPositions):
/// the map's picture letterboxed to its proportions, with tech building and supply markers.
/// </summary>
public static class MapPreviewDrawer
{
    const float SupplyTechSize = 15;

    /// <summary>Where the map's extent goes inside the window (keeping its proportions).</summary>
    public static (Vector2 Ul, Vector2 Lr) FindDrawPositions(Rect r, MapMetaData meta)
    {
        float extentW = Math.Max(1, meta.ExtentMax.X - meta.ExtentMin.X), extentH = Math.Max(1, meta.ExtentMax.Y - meta.ExtentMin.Y);
        float ratioWidth = extentW / r.Width, ratioHeight = extentH / r.Height;
        Vector2 ul, lr;
        if (ratioWidth >= ratioHeight)
        {
            float h = extentH / ratioWidth;
            ul = new Vector2(0, (r.Height - h) / 2);
            lr = new Vector2(extentW / ratioWidth, r.Height - ul.Y);
        }
        else
        {
            float w = extentW / ratioHeight;
            ul = new Vector2((r.Width - w) / 2, 0);
            lr = new Vector2(r.Width - ul.X, extentH / ratioHeight);
        }
        return (ul + new Vector2(r.X, r.Y), lr + new Vector2(r.X, r.Y));
    }

    public static Vector2 MapToScreen(Rect r, MapMetaData meta, Vector3 world)
    {
        var (ul, lr) = FindDrawPositions(r, meta);
        float px = (world.X - meta.ExtentMin.X) / Math.Max(1, meta.ExtentMax.X - meta.ExtentMin.X);
        float py = (world.Y - meta.ExtentMin.Y) / Math.Max(1, meta.ExtentMax.Y - meta.ExtentMin.Y);
        return new Vector2(ul.X + px * (lr.X - ul.X), ul.Y + (1 - py) * (lr.Y - ul.Y));
    }

    public static void Draw(GuiWindow window, GuiContext context, MapMetaData? meta, Texture? picture)
    {
        var r = window.ScreenRect;
        var batch = context.Batch;
        var border = new Rect(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2);
        if (meta == null)
        {
            context.Images.Draw(batch, "UnknownMap", r);
            batch.DrawRect(border, new Vector4(0.2f, 0.2f, 0.2f, 1));
            return;
        }
        var (ul, lr) = FindDrawPositions(r, meta);
        batch.FillRect(r, new Vector4(0, 0, 0, 1));
        var inner = new Rect(ul.X, ul.Y, lr.X - ul.X, lr.Y - ul.Y);
        if (picture != null)
            batch.DrawTexture(picture, inner);
        else
            batch.FillRect(inner, new Vector4(50 / 255f, 50 / 255f, 50 / 255f, 1));
        float size = SupplyTechSize * r.Width / Math.Max(1, window.Definition.Width);
        foreach (var tech in meta.TechPositions)
        {
            var p = MapToScreen(r, meta, tech);
            context.Images.Draw(batch, "TecBuilding", new Rect(p.X - size / 2, p.Y - size / 2, size, size));
        }
        foreach (var supply in meta.SupplyPositions)
        {
            var p = MapToScreen(r, meta, supply);
            context.Images.Draw(batch, "Cash", new Rect(p.X - size / 2, p.Y - size / 2, size, size));
        }
        batch.DrawRect(border, new Vector4(0.2f, 0.2f, 0.2f, 1));
    }

    /// <summary>Keeps start position buttons over their spots, nudging overlapping ones (positionStartSpotControls).</summary>
    public static void PositionStartButtons(GuiWindow mapWindow, MapMetaData meta, IReadOnlyList<GuiWindow?> buttons)
    {
        for (int i = 0; i < meta.NumPlayers && i < buttons.Count; i++)
        {
            if (buttons[i] is not { } button || meta.StartPosition(i) is not { } pos)
                continue;
            var p = MapToScreen(mapWindow.ScreenRect, meta, pos);
            var size = new Vector2(button.ScreenRect.Width, button.ScreenRect.Height);
            var target = p - size / 2;
            for (int j = 0; j < i; j++)
            {
                if (buttons[j] is not { } other)
                    continue;
                var o = other.ScreenRect;
                if (target.X > o.X && target.X < o.X + size.X && target.Y > o.Y && target.Y < o.Y + size.Y)
                {
                    float closerRight = o.X + size.X - target.X, closerBottom = o.Y + size.Y - target.Y;
                    if (closerRight < closerBottom)
                        target.X = o.X + size.X + 1;
                    else if (closerBottom < closerRight)
                        target.Y = o.Y + size.Y + 1;
                    else
                        target = new Vector2(o.X + size.X + 1, o.Y + size.Y + 1);
                }
            }
            button.SetScreenPosition(target);
        }
    }
}

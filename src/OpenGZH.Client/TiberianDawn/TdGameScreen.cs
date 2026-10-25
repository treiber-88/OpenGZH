using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.Platform;
using OpenGZH.Client.UI;
using OpenGZH.Formats.Westwood;
using OpenGZH.TiberianDawn;
using OpenGZH.TiberianDawn.Art;
using OpenGZH.TiberianDawn.Logic;
using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Types;
using Silk.NET.Input;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.TiberianDawn;

/// <summary>
/// A running Tiberian Dawn game, drawn with its original 2D art at whole-pixel scale: the map,
/// overlays (walls, Tiberium) and smudges, every object in the logic's layers, the shroud, and a
/// sidebar with the original cameos. Controls follow the remaster: left-click selects (drag for a
/// box, shift adds), right-click orders the selection; the sidebar builds; arrow keys or the screen
/// edges scroll, the mouse wheel zooms.
/// </summary>
public sealed class TdGameScreen : IScreen, ITdHooks
{
    const int Cell = TdArt.CellPixels;
    const float LogicStep = 1f / 15f;   // TD's Normal speed: 15 frames a second
    const int SidebarWidth = 170;
    const int CameoW = 64, CameoH = 48;

    readonly GL gl;
    readonly IInputContext input;
    readonly TdContent content;
    readonly TdGame game;
    readonly TdArt art;
    readonly SpriteBatch batch;
    readonly FontLibrary fonts;
    readonly GameFont font, bigFont;
    readonly Func<IScreen>? exitTo;

    Texture? terrain;
    readonly Dictionary<(KeyframeShape Shape, int Frame, int Remap, int Mode), Texture?> sprites = new();
    readonly Dictionary<byte[], int> remapIds = new(ReferenceEqualityComparer.Instance);
    readonly List<TdShapeLayer> layers = new();

    int width = 1280, height = 720;
    int scale = 2;
    Vector2 scroll;            // top-left of the view, in game pixels
    Vector2 mouse;
    bool mouseSeen;
    float accumulator;
    Vector2? dragStart;        // screen position where a left drag began
    bool dragging;
    int sidebarScroll0, sidebarScroll1;
    readonly List<(string Text, float Time)> messages = new();
    string? endText;

    public TdGameScreen(GL gl, IInputContext input, TdContent content, TdGame game, Func<IScreen>? exitTo = null)
    {
        this.gl = gl;
        this.input = input;
        this.content = content;
        this.game = game;
        this.exitTo = exitTo;
        art = new TdArt(content, game.Map.Theater);
        game.Art = art;
        game.Hooks = this;
        batch = new SpriteBatch(gl);
        fonts = new FontLibrary(gl, GameContent.FindRoot());
        font = fonts.Get("Arial", 9);
        bigFont = fonts.Get("Arial", 20, bold: true);

        // The view starts on the player's MCV (or base).
        var home = game.Units.Active.FirstOrDefault(u => u.House == game.PlayerPtr)?.Center_Coord()
                   ?? game.Buildings.Active.FirstOrDefault(b => b.House == game.PlayerPtr)?.Center_Coord()
                   ?? TdMath.CellCoord(game.Waypoint[(int)WaypointEnum.WAYPT_HOME]);
        CenterOn(home);

        onMove = (_, p) => { mouse = p; mouseSeen = true; };
        onDown = (_, b) => MouseDown(b);
        onUp = (_, b) => MouseUp(b);
        onScroll = (_, w) => Wheel(w.Y);
        onKey = (_, key, _) => KeyDown(key);
        foreach (var m in input.Mice)
        {
            m.MouseMove += onMove;
            m.MouseDown += onDown;
            m.MouseUp += onUp;
            m.Scroll += onScroll;
        }
        foreach (var k in input.Keyboards)
            k.KeyDown += onKey;
    }

    readonly Action<IMouse, Vector2> onMove;
    readonly Action<IMouse, MouseButton> onDown, onUp;
    readonly Action<IMouse, ScrollWheel> onScroll;
    readonly Action<IKeyboard, Key, int> onKey;

    // ---- Coordinates ----

    int ViewWidth => width - SidebarWidth;
    float LeptonsPerPixel => 256f / Cell;

    Vector2 GamePixel(uint coord) => new(TdMath.CoordX(coord) / LeptonsPerPixel, TdMath.CoordY(coord) / LeptonsPerPixel);
    Vector2 ToScreen(Vector2 gamePixel) => (gamePixel - new Vector2(MathF.Floor(scroll.X), MathF.Floor(scroll.Y))) * scale;
    Vector2 ToGamePixel(Vector2 screen) => screen / scale + new Vector2(MathF.Floor(scroll.X), MathF.Floor(scroll.Y));

    uint ScreenToCoord(Vector2 screen)
    {
        if (screen.X >= ViewWidth)
            return 0;
        var p = ToGamePixel(screen);
        int lx = (int)(p.X * LeptonsPerPixel), ly = (int)(p.Y * LeptonsPerPixel);
        if (lx < 0 || ly < 0 || lx >= 64 * 256 || ly >= 64 * 256)
            return 0;
        uint coord = TdMath.XyCoord(lx, ly);
        return game.Map.In_Radar(TdMath.CoordCell(coord)) ? coord : 0;
    }

    /// <summary>Debug: look at the first computer player's base (or units).</summary>
    public void LookAtEnemy()
    {
        var enemy = game.Houses.Active.FirstOrDefault(h => h != game.PlayerPtr && !h.IsHuman && h.Class.House >= HousesType.HOUSE_MULTI1);
        if (enemy == null)
            return;
        uint? at = game.Buildings.Active.FirstOrDefault(b => b.House == enemy)?.Center_Coord()
                   ?? game.Units.Active.FirstOrDefault(u => u.House == enemy)?.Center_Coord();
        if (at is { } coord)
            CenterOn(coord);
    }

    void CenterOn(uint coord)
    {
        var p = GamePixel(coord);
        scroll = p - new Vector2(ViewWidth, height) / (2f * scale);
        ClampScroll();
    }

    void ClampScroll()
    {
        var map = game.Map;
        float minX = map.MapCellX * Cell, minY = map.MapCellY * Cell;
        float maxX = (map.MapCellX + map.MapCellWidth) * Cell - ViewWidth / (float)scale;
        float maxY = (map.MapCellY + map.MapCellHeight) * Cell - height / (float)scale;
        scroll.X = Math.Clamp(scroll.X, minX, Math.Max(minX, maxX));
        scroll.Y = Math.Clamp(scroll.Y, minY, Math.Max(minY, maxY));
    }

    // ---- Input ----

    bool Shift => input.Keyboards.Count > 0 && (input.Keyboards[0].IsKeyPressed(Key.ShiftLeft) || input.Keyboards[0].IsKeyPressed(Key.ShiftRight));

    void KeyDown(Key key)
    {
        if (key == Key.Escape)
        {
            if (game.PendingObject != null)
                game.Cancel_Placement();
            else if (endText != null || exitTo != null)
                Leave();
        }
        if (endText != null && key is Key.Enter or Key.Space)
            Leave();
    }

    void Leave()
    {
        if (exitTo != null)
            GameWindow.Current?.Switch(exitTo());
    }

    void Wheel(float y)
    {
        if (mouse.X >= ViewWidth)
        {
            // Scroll the column under the mouse.
            int column = mouse.X < ViewWidth + SidebarWidth / 2 ? 0 : 1;
            if (column == 0) sidebarScroll0 = Math.Max(0, sidebarScroll0 - Math.Sign(y));
            else sidebarScroll1 = Math.Max(0, sidebarScroll1 - Math.Sign(y));
            return;
        }
        var before = ToGamePixel(mouse);
        scale = Math.Clamp(scale + Math.Sign(y), 1, 4);
        scroll += before - ToGamePixel(mouse);
        ClampScroll();
    }

    void MouseDown(MouseButton button)
    {
        if (endText != null)
            return;
        if (mouse.X >= ViewWidth)
        {
            SidebarClick(button);
            return;
        }
        if (button == MouseButton.Left)
        {
            if (game.PendingObject != null)
            {
                int cell = PlacementCell();
                if (game.Can_Place_At(cell))
                    game.Place(cell);
                else
                    Speak(VoxType.VOX_DEPLOY, null, 0);   // "Cannot deploy here"
                return;
            }
            dragStart = mouse;
            dragging = false;
        }
        else if (button == MouseButton.Right)
        {
            if (game.PendingObject != null)
            {
                game.Cancel_Placement();
                return;
            }
            game.Command_Object(ScreenToCoord(mouse));
        }
    }

    void MouseUp(MouseButton button)
    {
        if (button != MouseButton.Left || dragStart is not { } start)
            return;
        dragStart = null;
        if (dragging)
        {
            var a = ToGamePixel(start);
            var b = ToGamePixel(mouse);
            game.Select_These(TdMath.XyCoord((int)(a.X * LeptonsPerPixel), (int)(a.Y * LeptonsPerPixel)),
                              TdMath.XyCoord((int)(b.X * LeptonsPerPixel), (int)(b.Y * LeptonsPerPixel)), Shift);
        }
        else
        {
            game.Selection_At_Mouse(ScreenToCoord(mouse), Shift);
        }
        dragging = false;
    }

    /// <summary>The top-left cell for the pending building, centred on the mouse.</summary>
    int PlacementCell()
    {
        if (game.PlacementType is not { } type)
            return -1;
        var p = ToGamePixel(mouse);
        int cx = (int)(p.X / Cell) - (type.Width() - 1) / 2, cy = (int)(p.Y / Cell) - (type.Height() - 1) / 2;
        return TdMath.XyCell(Math.Clamp(cx, 0, 63), Math.Clamp(cy, 0, 63));
    }

    // ---- The sidebar ----

    Rect CameoRect(int column, int row) =>
        new(ViewWidth + 14 + column * (CameoW + 14), 132 + row * (CameoH + 4), CameoW, CameoH);

    int VisibleRows => Math.Max(1, (height - 140) / (CameoH + 4));

    void SidebarClick(MouseButton button)
    {
        var sidebar = game.SidebarFor(game.PlayerPtr);
        for (int column = 0; column < 2; column++)
        {
            var list = sidebar.Column[column].Buildables;
            int first = column == 0 ? sidebarScroll0 : sidebarScroll1;
            for (int row = 0; row < VisibleRows && first + row < list.Count; row++)
            {
                if (!CameoRect(column, row).Contains(mouse))
                    continue;
                var b = list[first + row];
                if (b.BuildableType == RTTIType.RTTI_SPECIAL)
                {
                    game.IsTargettingMode = true;
                    targetting = (SpecialWeaponType)b.BuildableID;
                    return;
                }
                var factory = b.Factory != -1 ? game.Factories.RawPtr(b.Factory) : null;
                if (button == MouseButton.Left)
                {
                    if (factory != null && factory.Has_Completed() && b.BuildableType == RTTIType.RTTI_BUILDINGTYPE)
                        game.Start_Placement(b.BuildableType, b.BuildableID);
                    else
                        game.Start_Construction(b.BuildableType, b.BuildableID);
                }
                else if (button == MouseButton.Right && factory != null)
                {
                    if (factory.Is_Building())
                        game.Hold_Construction(b.BuildableType, b.BuildableID);
                    else
                        game.Cancel_Construction(b.BuildableType, b.BuildableID);
                }
                return;
            }
        }
    }

    SpecialWeaponType targetting = SpecialWeaponType.SPC_NONE;

    // ---- The game ----

    public void Resize(int w, int h)
    {
        width = Math.Max(1, w);
        height = Math.Max(1, h);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        ClampScroll();
    }

    public void Update(float seconds)
    {
        // Scrolling.
        var move = Vector2.Zero;
        var keyboard = input.Keyboards.Count > 0 ? input.Keyboards[0] : null;
        if (keyboard?.IsKeyPressed(Key.Left) == true || mouseSeen && mouse.X <= 2) move.X -= 1;
        if (keyboard?.IsKeyPressed(Key.Right) == true || mouseSeen && mouse.X >= width - 3) move.X += 1;
        if (keyboard?.IsKeyPressed(Key.Up) == true || mouseSeen && mouse.Y <= 2) move.Y -= 1;
        if (keyboard?.IsKeyPressed(Key.Down) == true || mouseSeen && mouse.Y >= height - 3) move.Y += 1;
        scroll += move * 700 * seconds / scale;
        ClampScroll();

        if (dragStart is { } start && Vector2.Distance(start, mouse) > 6)
            dragging = true;

        // The logic runs at the original's fixed rate.
        if (endText == null)
        {
            accumulator = Math.Min(accumulator + seconds, 0.5f);
            while (accumulator >= LogicStep)
            {
                accumulator -= LogicStep;
                game.Input.MouseX = (int)ToGamePixel(mouse).X;
                game.Input.MouseY = (int)ToGamePixel(mouse).Y;
                game.Main_Loop();
                if (game.PlayerWins)
                    endText = "MISSION ACCOMPLISHED";
                else if (game.PlayerLoses)
                    endText = "MISSION FAILED";
                if (endText != null)
                    break;
            }
        }

        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var (text, time) = messages[i];
            if (time - seconds <= 0)
                messages.RemoveAt(i);
            else
                messages[i] = (text, time - seconds);
        }
    }

    Texture? Sprite(KeyframeShape shape, int frame, byte[]? remap, int mode)
    {
        int remapId = 0;
        if (remap != null && !remapIds.TryGetValue(remap, out remapId))
            remapIds[remap] = remapId = remapIds.Count + 1;
        var key = (shape, frame, remapId, mode);
        if (sprites.TryGetValue(key, out var texture))
            return texture;
        var image = art.Frame(shape, frame, remap, shadowOnly: mode == 1, ghost: mode == 2);
        texture = image != null ? new Texture(gl, image, repeat: false, mipmaps: false, nearest: true) : null;
        return sprites[key] = texture;
    }

    /// <summary>Draws a frame centred on a game pixel (SHAPE_CENTER), or from its top-left.</summary>
    void DrawShape(KeyframeShape? shape, int frame, byte[]? remap, Vector2 at, bool centre = true, int mode = 0)
    {
        if (shape == null || frame < 0 || frame >= shape.FrameCount)
            return;
        var texture = Sprite(shape, frame, remap, mode);
        if (texture == null)
            return;
        var topLeft = centre ? at - new Vector2(shape.Width / 2, shape.Height / 2) : at;
        var s = ToScreen(topLeft);
        if (s.X > ViewWidth || s.Y > height || s.X + shape.Width * scale < 0 || s.Y + shape.Height * scale < 0)
            return;
        batch.DrawTexture(texture, new Rect(s.X, s.Y, shape.Width * scale, shape.Height * scale));
    }

    public void Render(float seconds)
    {
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        batch.Begin(width, height);
        batch.SetClip(new Rect(0, 0, ViewWidth, height));

        terrain ??= new Texture(gl, art.Terrain(game), repeat: false, mipmaps: false, nearest: true);
        var origin = ToScreen(Vector2.Zero);
        batch.DrawTexture(terrain, new Rect(origin.X, origin.Y, terrain.Width * scale, terrain.Height * scale));

        DrawCellLayers();
        DrawObjects();
        DrawShroud();
        DrawSelection();
        DrawPlacement();

        if (dragging && dragStart is { } start)
        {
            var r = new Rect(Math.Min(start.X, mouse.X), Math.Min(start.Y, mouse.Y), Math.Abs(mouse.X - start.X), Math.Abs(mouse.Y - start.Y));
            batch.DrawRect(r, new Vector4(1, 1, 1, 1));
        }

        batch.SetClip(null);
        DrawSidebar();
        DrawMessages();
        DrawCursorHint();
        if (endText != null)
        {
            float w = bigFont.Measure(endText);
            bigFont.DrawWithShadow(batch, endText, new Vector2((ViewWidth - w) / 2, height / 2f - 20), new Vector4(1, 0.9f, 0.3f, 1), new Vector4(0, 0, 0, 1));
            string sub = exitTo != null ? "Press Enter to return to the menu" : "Press Escape";
            font.Draw(batch, sub, new Vector2((ViewWidth - font.Measure(sub)) / 2, height / 2f + 20), new Vector4(1, 1, 1, 1));
        }
        batch.End();
    }

    (int X0, int Y0, int X1, int Y1) VisibleCells()
    {
        var a = ToGamePixel(Vector2.Zero);
        var b = ToGamePixel(new Vector2(ViewWidth, height));
        return (Math.Max(0, (int)(a.X / Cell) - 1), Math.Max(0, (int)(a.Y / Cell) - 1),
                Math.Min(63, (int)(b.X / Cell) + 1), Math.Min(63, (int)(b.Y / Cell) + 1));
    }

    /// <summary>CellClass::Draw_It's smudges (from the cell's corner) and overlays (centred).</summary>
    void DrawCellLayers()
    {
        var (x0, y0, x1, y1) = VisibleCells();
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                var cell = game.Map[TdMath.XyCell(x, y)];
                var corner = new Vector2(x * Cell, y * Cell);
                if (cell.Smudge != SmudgeType.SMUDGE_NONE)
                    DrawShape(art.Shape(game.Types.Smudge(cell.Smudge).IniName), cell.SmudgeData, null, corner, centre: false);
                if (cell.Overlay != OverlayType.OVERLAY_NONE)
                    DrawShape(art.Shape(game.Types.Overlay(cell.Overlay).IniName), cell.OverlayData, null, corner + new Vector2(Cell / 2, Cell / 2));
            }
        }
    }

    readonly HashSet<ObjectClass> drawnByFactory = new();

    void DrawObjects()
    {
        // A unit driving out of a war factory is drawn by the factory, under its roof.
        drawnByFactory.Clear();
        foreach (var b in game.Buildings.Active)
            if (Leaving_Unit(b) is { } contact)
                drawnByFactory.Add(contact);

        // The layers in the original's order: ground (sorted by the logic), air, then top.
        for (int layer = 0; layer < game.Map.Layer.Length; layer++)
        {
            var list = game.Map.Layer[layer];
            for (int i = 0; i < list.Count; i++)
                if (!drawnByFactory.Contains(list[i]))
                    DrawObject(list[i]);
        }
    }

    static TechnoClass? Leaving_Unit(BuildingClass b) =>
        b.Class.Type == StructType.STRUCT_WEAP && b.BState != BStateType.BSTATE_CONSTRUCTION && b.IsTethered && b.In_Radio_Contact() &&
        b.Contact_With_Whom() is { IsInLimbo: false } contact ? contact : null;

    void DrawObject(ObjectClass obj)
    {
        if (obj.IsInLimbo)
            return;
        var at = GamePixel(obj.Center_Coord());
        switch (obj)
        {
            case TerrainClass t:
                DrawShape(art.Shape(t.Class.IniName), t.Shape_Number(), null, GamePixel(t.Coord), centre: false);
                break;
            case BuildingClass b:
            {
                if (b.Visual_Character() == VisualType.VISUAL_HIDDEN)
                    break;
                var (shape, buildup) = b.Shape_Number();
                var file = art.Shape(buildup ? b.Class.IniName + "MAKE" : b.Class.IniName);
                var remap = b.House.Remap_Table(false, false);
                DrawShape(file, shape, remap, at);
                if (b.BState != BStateType.BSTATE_CONSTRUCTION)
                {
                    if (Leaving_Unit(b) is { } contact)
                        DrawObject(contact);
                    // The war factory's door and roof (WEAP2), over a unit driving out.
                    if (b.Class.Type == StructType.STRUCT_WEAP && b.Strength > 1)
                        DrawShape(art.Shape("WEAP2"), b.Door.Door_Stage() + (b.Health_Ratio() < 0x0080 ? 10 : 0), remap, at);
                    if (b.IsRepairing && b.IsWrenchVisible)
                        DrawShape(art.Shape("SELECT"), (int)SelectEnum.SELECT_WRENCH, null, at);
                }
                break;
            }
            case InfantryClass inf:
                if (inf.Visual_Character() == VisualType.VISUAL_HIDDEN)
                    break;
                DrawShape(art.Shape(inf.Class.IniName), inf.Shape_Number(false), inf.House.Remap_Table(false, true), at);
                break;
            case UnitClass u:
                if (u.Visual_Character() == VisualType.VISUAL_HIDDEN)
                    break;
                layers.Clear();
                TdDraw.Unit_Layers(u, layers);
                DrawLayers(art.Shape(u.Class.IniName), layers, u.House.Remap_Table(false, true), at);
                break;
            case AircraftClass a:
                if (a.Visual_Character() == VisualType.VISUAL_HIDDEN)
                    break;
                layers.Clear();
                if (TdDraw.Aircraft_Layers(a, layers))
                    DrawLayers(art.Shape(a.Class.IniName), layers, a.House.Remap_Table(false, true), at);
                break;
            case BulletClass bullet:
                if (!bullet.Class.IsInvisible)
                    DrawShape(art.Shape(bullet.Class.IniName), bullet.Shape_Number(), null, at, mode: bullet.Class.IsTranslucent ? 2 : 0);
                break;
            case AnimClass anim:
                DrawShape(art.Shape(anim.Class.IniName), anim.Shape_Number(false), null, at, mode: anim.Class.IsTranslucent ? 2 : 0);
                break;
        }
    }

    void DrawLayers(KeyframeShape? own, List<TdShapeLayer> list, byte[]? remap, Vector2 at)
    {
        foreach (var l in list)
        {
            var shape = l.File != null ? art.Shape(l.File) : own;
            int mode = (l.Flags & TdShapeFlags.Shadow) != 0 ? 1 : (l.Flags & TdShapeFlags.Ghost) != 0 ? 2 : 0;
            DrawShape(shape, l.Frame, (l.Flags & TdShapeFlags.Remap) != 0 ? remap : null, at + new Vector2(l.Dx, l.Dy), mode: mode);
        }
    }

    /// <summary>Cells the player hasn't seen are black (the original's shroud).</summary>
    void DrawShroud()
    {
        var (x0, y0, x1, y1) = VisibleCells();
        var black = new Vector4(0, 0, 0, 1);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int cell = TdMath.XyCell(x, y);
                if (!game.Map.In_Radar(cell) || (!game.DebugUnshroud && !game.Map[cell].Is_Mapped(game.PlayerPtr)))
                {
                    var s = ToScreen(new Vector2(x * Cell, y * Cell));
                    batch.FillRect(new Rect(s.X, s.Y, Cell * scale, Cell * scale), black);
                }
            }
        }
    }

    /// <summary>Selected objects: corner brackets and a health bar (green, yellow, red).</summary>
    void DrawSelection()
    {
        foreach (var obj in game.CurrentObject.Of(game.PlayerPtr.Class.House))
        {
            if (obj.IsInLimbo)
                continue;
            var c = GamePixel(obj.Center_Coord());
            int w = Cell, h = Cell;
            if (obj is BuildingClass b)
            {
                w = b.Class.Width() * Cell;
                h = b.Class.Height() * Cell;
            }
            else if (obj is InfantryClass)
            {
                w = h = 12;
            }
            if (obj is AircraftClass a)
                c.Y -= a.Altitude;
            var tl = ToScreen(c - new Vector2(w / 2f, h / 2f));
            var r = new Rect(tl.X, tl.Y, w * scale, h * scale);
            var white = new Vector4(1, 1, 1, 1);
            float l = 5 * scale;
            batch.DrawLine(new Vector2(r.X, r.Y), new Vector2(r.X + l, r.Y), white);
            batch.DrawLine(new Vector2(r.X, r.Y), new Vector2(r.X, r.Y + l), white);
            batch.DrawLine(new Vector2(r.Right, r.Y), new Vector2(r.Right - l, r.Y), white);
            batch.DrawLine(new Vector2(r.Right, r.Y), new Vector2(r.Right, r.Y + l), white);
            batch.DrawLine(new Vector2(r.X, r.Bottom), new Vector2(r.X + l, r.Bottom), white);
            batch.DrawLine(new Vector2(r.X, r.Bottom), new Vector2(r.X, r.Bottom - l), white);
            batch.DrawLine(new Vector2(r.Right, r.Bottom), new Vector2(r.Right - l, r.Bottom), white);
            batch.DrawLine(new Vector2(r.Right, r.Bottom), new Vector2(r.Right, r.Bottom - l), white);

            float ratio = Math.Clamp(obj.Health_Ratio() / 256f, 0, 1);
            var color = ratio > 0.5f ? new Vector4(0, 0.9f, 0, 1) : ratio > 0.25f ? new Vector4(1, 0.9f, 0, 1) : new Vector4(1, 0, 0, 1);
            var bar = new Rect(r.X, r.Y - 4 * scale, r.Width, 2 * scale);
            batch.FillRect(bar, new Vector4(0, 0, 0, 1));
            batch.FillRect(bar with { Width = bar.Width * ratio }, color);
        }
    }

    /// <summary>The pending building's footprint: green where it can go, red where not.</summary>
    void DrawPlacement()
    {
        if (game.PlacementType is not { } type || mouse.X >= ViewWidth)
            return;
        int cell = PlacementCell();
        bool ok = game.Can_Place_At(cell);
        var color = ok ? new Vector4(0.2f, 1, 0.2f, 0.45f) : new Vector4(1, 0.2f, 0.2f, 0.45f);
        foreach (short offset in type.Occupy_List(game.Types, game.Special, true))
        {
            if (offset == TdMath.RefreshEol)
                break;
            int c = cell + offset;
            var s = ToScreen(new Vector2(TdMath.CellX(c) * Cell, TdMath.CellY(c) * Cell));
            batch.FillRect(new Rect(s.X, s.Y, Cell * scale, Cell * scale), color);
        }
    }

    void DrawSidebar()
    {
        var panel = new Rect(ViewWidth, 0, SidebarWidth, height);
        batch.FillRect(panel, new Vector4(0.13f, 0.13f, 0.12f, 1));
        batch.DrawLine(new Vector2(ViewWidth, 0), new Vector2(ViewWidth, height), new Vector4(0.4f, 0.4f, 0.35f, 1), 2);

        var player = game.PlayerPtr;
        var gold = new Vector4(0.95f, 0.85f, 0.4f, 1);
        string credits = $"$ {player.Available_Money()}";
        font.Draw(batch, credits, new Vector2(ViewWidth + (SidebarWidth - font.Measure(credits)) / 2, 6), gold);

        // Power: output against drain.
        int power = player.Power, drain = player.Drain;
        var bar = new Rect(ViewWidth + 10, 26, SidebarWidth - 20, 8);
        batch.FillRect(bar, new Vector4(0, 0, 0, 1));
        float max = Math.Max(Math.Max(power, drain), 1);
        batch.FillRect(bar with { Width = bar.Width * power / max }, power >= drain ? new Vector4(0.2f, 0.8f, 0.2f, 1) : new Vector4(0.9f, 0.6f, 0.1f, 1));
        batch.FillRect(new Rect(bar.X + bar.Width * Math.Min(drain / max, 1) - 1, bar.Y - 2, 2, bar.Height + 4), new Vector4(1, 1, 1, 1));
        font.Draw(batch, $"Power {power} / {drain}", new Vector2(ViewWidth + 10, 36), new Vector4(0.85f, 0.85f, 0.85f, 1));

        DrawRadar(new Rect(ViewWidth + 10, 54, SidebarWidth - 20, 72));

        var sidebar = game.SidebarFor(player);
        for (int column = 0; column < 2; column++)
        {
            var list = sidebar.Column[column].Buildables;
            ref int first = ref (column == 0 ? ref sidebarScroll0 : ref sidebarScroll1);
            first = Math.Clamp(first, 0, Math.Max(0, list.Count - VisibleRows));
            for (int row = 0; row < VisibleRows && first + row < list.Count; row++)
                DrawCameo(list[first + row], CameoRect(column, row));
        }
    }

    void DrawCameo(TdBuildable b, Rect r)
    {
        string name;
        if (b.BuildableType == RTTIType.RTTI_SPECIAL)
        {
            name = (SpecialWeaponType)b.BuildableID switch
            {
                SpecialWeaponType.SPC_ION_CANNON => "ION",
                SpecialWeaponType.SPC_NUCLEAR_BOMB => "BOMB",
                SpecialWeaponType.SPC_AIR_STRIKE => "AIR",
                _ => "?",
            };
        }
        else
        {
            var type = game.Fetch_Techno_Type(b.BuildableType, b.BuildableID);
            name = type?.IniName ?? "?";
            // Infantry cameos use the first four letters (the source's %.4s).
            if (b.BuildableType == RTTIType.RTTI_INFANTRYTYPE && name.Length > 4)
                name = name[..4];
        }

        batch.FillRect(r, new Vector4(0, 0, 0, 1));
        if (art.Cameo(name) is { } shape && Sprite(shape, 0, null, 0) is { } tex)
            batch.DrawTexture(tex, r);
        else
            font.Draw(batch, name, new Vector2(r.X + 4, r.Y + 16), new Vector4(1, 1, 1, 1));

        var factory = b.Factory != -1 ? game.Factories.RawPtr(b.Factory) : null;
        if (factory != null)
        {
            float done = Math.Clamp(factory.Completion() / (float)FactoryClass.STEP_COUNT, 0, 1);
            batch.FillRect(new Rect(r.X, r.Y + r.Height * done, r.Width, r.Height * (1 - done)), new Vector4(0, 0, 0, 0.6f));
            string state = factory.Has_Completed() ? "READY" : !factory.Is_Building() ? "ON HOLD" : "";
            if (state.Length > 0)
                font.DrawWithShadow(batch, state, new Vector2(r.X + (r.Width - font.Measure(state)) / 2, r.Y + r.Height - 16), new Vector4(1, 1, 1, 1), new Vector4(0, 0, 0, 1));
        }
        if (r.Contains(mouse))
            batch.DrawRect(r, new Vector4(1, 1, 0.6f, 1));
    }

    /// <summary>A small radar: terrain, units as house-coloured dots, and the view box (when the
    /// player has a working Communications Center, as the original).</summary>
    void DrawRadar(Rect r)
    {
        batch.FillRect(r, new Vector4(0, 0, 0, 1));
        if (game.PlayerPtr.Radar != RadarEnum.RADAR_ON || terrain == null)
        {
            font.Draw(batch, "No radar", new Vector2(r.X + (r.Width - font.Measure("No radar")) / 2, r.Y + r.Height / 2 - 6), new Vector4(0.5f, 0.5f, 0.5f, 1));
            return;
        }
        var map = game.Map;
        float cs = Math.Min(r.Width / map.MapCellWidth, r.Height / map.MapCellHeight);
        float ox = r.X + (r.Width - cs * map.MapCellWidth) / 2, oy = r.Y + (r.Height - cs * map.MapCellHeight) / 2;
        float u0 = map.MapCellX / 64f, v0 = map.MapCellY / 64f;
        float u1 = (map.MapCellX + map.MapCellWidth) / 64f, v1 = (map.MapCellY + map.MapCellHeight) / 64f;
        batch.DrawTexture(terrain, new Rect(ox, oy, cs * map.MapCellWidth, cs * map.MapCellHeight), u0, v0, u1, v1, new Vector4(1, 1, 1, 1));
        for (int y = 0; y < map.MapCellHeight; y++)
            for (int x = 0; x < map.MapCellWidth; x++)
            {
                var cell = map[TdMath.XyCell(map.MapCellX + x, map.MapCellY + y)];
                if (!cell.Is_Mapped(game.PlayerPtr))
                    batch.FillRect(new Rect(ox + x * cs, oy + y * cs, cs + 0.5f, cs + 0.5f), new Vector4(0, 0, 0, 1));
            }
        void Dot(TechnoClass t)
        {
            if (t.IsInLimbo)
                return;
            int cell = TdMath.CoordCell(t.Center_Coord());
            if (!map[cell].Is_Mapped(game.PlayerPtr))
                return;
            var (cr, cg, cb) = art.Color(t.House.Remap_Table(false, true)?[179] ?? 179);
            batch.FillRect(new Rect(ox + (TdMath.CellX(cell) - map.MapCellX) * cs, oy + (TdMath.CellY(cell) - map.MapCellY) * cs, Math.Max(cs, 1.5f), Math.Max(cs, 1.5f)),
                new Vector4(cr / 255f, cg / 255f, cb / 255f, 1));
        }
        foreach (var o in game.Buildings.Active) Dot(o);
        foreach (var o in game.Units.Active) Dot(o);
        foreach (var o in game.Infantry.Active) Dot(o);
        var a = ToGamePixel(Vector2.Zero) / Cell;
        var b = ToGamePixel(new Vector2(ViewWidth, height)) / Cell;
        batch.DrawRect(new Rect(ox + (a.X - map.MapCellX) * cs, oy + (a.Y - map.MapCellY) * cs, (b.X - a.X) * cs, (b.Y - a.Y) * cs), new Vector4(1, 1, 1, 1));
    }

    void DrawMessages()
    {
        float y = 8;
        foreach (var (text, _) in messages)
        {
            font.DrawWithShadow(batch, text, new Vector2(10, y), new Vector4(0.6f, 1, 0.6f, 1), new Vector4(0, 0, 0, 1));
            y += 16;
        }
    }

    /// <summary>What a right-click would do here (the original's mouse shapes aren't in the
    /// freeware files, so the action is named next to the pointer).</summary>
    void DrawCursorHint()
    {
        if (mouse.X >= ViewWidth || game.PendingObject != null || endText != null)
            return;
        var action = game.Action_At(ScreenToCoord(mouse), out _);
        string? hint = action switch
        {
            ActionType.ACTION_MOVE => "Move",
            ActionType.ACTION_NOMOVE => "Can't move there",
            ActionType.ACTION_ATTACK => "Attack",
            ActionType.ACTION_ENTER => "Enter",
            ActionType.ACTION_CAPTURE => "Capture",
            ActionType.ACTION_SABOTAGE => "Sabotage",
            ActionType.ACTION_HARVEST => "Harvest",
            ActionType.ACTION_SELF => "Deploy",
            ActionType.ACTION_NO_DEPLOY => "Can't deploy here",
            ActionType.ACTION_GUARD_AREA => "Guard",
            _ => null,
        };
        if (hint != null)
            font.DrawWithShadow(batch, hint, mouse + new Vector2(16, 14), new Vector4(1, 1, 0.7f, 1), new Vector4(0, 0, 0, 1));
    }

    public void Dispose()
    {
        foreach (var m in input.Mice)
        {
            m.MouseMove -= onMove;
            m.MouseDown -= onDown;
            m.MouseUp -= onUp;
            m.Scroll -= onScroll;
        }
        foreach (var k in input.Keyboards)
            k.KeyDown -= onKey;
        foreach (var t in sprites.Values)
            t?.Dispose();
        terrain?.Dispose();
        fonts.Dispose();
        batch.Dispose();
        if (game.Hooks == this)
            game.Hooks = new NoHooks();
    }

    sealed class NoHooks : ITdHooks { }

    // ---- The front-end hooks (speech shows as text until TD audio is in) ----

    static string Words(VoxType voice)
    {
        string name = voice.ToString().Replace("VOX_", "").Replace('_', ' ').ToLowerInvariant();
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    public void Speak(VoxType voice, HouseClass? house, uint coord)
    {
        if (voice == VoxType.VOX_NONE || (house != null && house != game.PlayerPtr))
            return;
        string text = Words(voice);
        if (messages.Count == 0 || messages[^1].Text != text)
            messages.Add((text, 4));
        if (messages.Count > 6)
            messages.RemoveAt(0);
    }

    public void Add_Message(string text, PlayerColorType color, int timeout) => messages.Add((text, timeout / 15f));
    public void On_Defeated_Message(string text) => messages.Add((text, 6));
}

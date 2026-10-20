using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.Platform;
using OpenGZH.Client.UI;
using OpenGZH.Client.World;
using OpenGZH.Formats.Maps;
using OpenGZH.Logic;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Construction;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;
using Silk.NET.Input;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.InGame;

/// <summary>
/// A game in progress: the world, the command bar and the player's mouse and keyboard.
///
/// Controls (the original's default "right-click" scheme):
///   left click / drag   select units (drag draws a box)
///   right click         move, attack, repair, build at the cursor (depends on what is under it)
///   arrows / screen edge scroll, mouse wheel zoom, Ctrl+wheel or middle drag rotate, Space centre on selection
///   S stop, A attack-move (then click), Escape cancel placement
/// </summary>
public sealed class GameScreen : IScreen
{
    readonly GL gl;
    readonly IInputContext input;
    readonly UiResources ui;
    readonly WorldView world;
    readonly ControlBar controlBar;
    readonly Player player;
    readonly List<GameObject> selection = new();

    int width = 1280, height = 720;
    Vector2 mouse;
    Vector2? dragStart;
    bool middleDragging;
    Vector2 lastMiddle;

    // Building placement mode (after clicking a construct button).
    ObjectTemplate? placing;
    GameObject? placingBuilder;
    float placingAngle;
    bool attackMoveMode;
    string? message;
    float messageTime;

    public GameLogic Game => world.Game;

    public GameScreen(GL gl, IInputContext input, UiResources ui, GameLogic game, MapFile map)
    {
        this.gl = gl;
        this.input = input;
        this.ui = ui;
        player = game.Players.Local ?? game.Players.All.First(p => !p.IsNeutral);
        world = new WorldView(gl, ui.Content, game, map, ui.Textures);
        controlBar = new ControlBar(ui, game, player);
        controlBar.CommandClicked += OnCommand;
        controlBar.QueueEntryClicked += (factory, entry) => Game.Commands.Enqueue(new CancelProductionCommand(player.Index, factory.Id, entry.Id));
        game.Events.OnMessage += (text, who) => { if (who == null || who == player) ShowMessage(text); };
        game.Events.OnEva += (eva, who) => { if (who == player && eva == "InsufficientFunds") ShowMessage(ui.Strings.Get("GUI:NotEnoughMoneyToBuild")); };

        if (player.StartPosition is { } start)
            world.LookAt(start);
        else if (game.ObjectsOwnedBy(player).FirstOrDefault() is { } first)
            world.LookAt(first.Position);
        else
            world.LookAt(new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0));

        foreach (var m in input.Mice)
        {
            m.MouseMove += (_, p) => OnMouseMove(p);
            m.MouseDown += (_, b) => OnMouseDown(b);
            m.MouseUp += (_, b) => OnMouseUp(b);
            m.Scroll += (_, w) => OnScroll(w.Y);
        }
        foreach (var k in input.Keyboards)
            k.KeyDown += (_, key, _) => OnKey(key);
    }

    Vector2 Viewport => new(width, height);
    IKeyboard? Keyboard => input.Keyboards.FirstOrDefault();
    bool Held(Key key) => Keyboard?.IsKeyPressed(key) == true;

    void ShowMessage(string text)
    {
        message = text;
        messageTime = 4;
    }

    public void Resize(int w, int h)
    {
        width = Math.Max(1, w);
        height = Math.Max(1, h);
        ui.Resize(width, height);
        controlBar.Layout.Layout(ui.Gui.Scale);
    }

    // ---- Update ----

    public void Update(float seconds)
    {
        world.Update(seconds);
        selection.RemoveAll(o => o.IsDestroyed || o.IsEffectivelyDead);
        controlBar.Update(selection);
        ScrollCamera(seconds);
        if (messageTime > 0)
            messageTime -= seconds;
    }

    void ScrollCamera(float seconds)
    {
        var camera = world.Camera;
        float speed = camera.Distance * 1.6f * seconds;
        Vector3 move = Vector3.Zero;
        if (Held(Key.Left) || mouse.X <= 2) move -= camera.ScreenRight;
        if (Held(Key.Right) || mouse.X >= width - 3) move += camera.ScreenRight;
        if (Held(Key.Up) || mouse.Y <= 2) move += camera.ScreenUp;
        if (Held(Key.Down) || mouse.Y >= height - 3) move -= camera.ScreenUp;
        if (move != Vector3.Zero)
        {
            var target = camera.Target + Vector3.Normalize(move) * speed;
            target.X = Math.Clamp(target.X, 0, Game.Terrain.WorldWidth);
            target.Y = Math.Clamp(target.Y, 0, Game.Terrain.WorldHeight);
            world.LookAt(target);
        }
    }

    // ---- Input ----

    void OnMouseMove(Vector2 position)
    {
        if (middleDragging)
        {
            world.Camera.Yaw -= (position.X - lastMiddle.X) * 0.008f;
            lastMiddle = position;
        }
        mouse = position;
        uiManager.MouseMove(position);
    }

    readonly UI.Gui.GuiManager uiManager = new();
    bool uiInitialized;

    void EnsureUi()
    {
        if (uiInitialized)
            return;
        uiInitialized = true;
        uiManager.Push(controlBar.Layout);
    }

    void OnMouseDown(MouseButton button)
    {
        EnsureUi();
        if (button == MouseButton.Middle)
        {
            middleDragging = true;
            lastMiddle = mouse;
            return;
        }
        if (button == MouseButton.Left)
        {
            if (uiManager.MouseDown(mouse))
                return;
            if (placing != null)
            {
                TryPlaceBuilding();
                return;
            }
            if (attackMoveMode)
            {
                if (world.GroundPointAt(mouse, Viewport) is { } point)
                    Game.Commands.Enqueue(new MoveCommand(player.Index, SelectedIds(), point, AttackMove: true));
                attackMoveMode = false;
                return;
            }
            dragStart = mouse;
        }
        else if (button == MouseButton.Right)
        {
            if (placing != null || attackMoveMode)
            {
                placing = null;
                attackMoveMode = false;
                return;
            }
            if (!uiManager.IsOverInterface(mouse))
                IssueContextOrder();
        }
    }

    void OnMouseUp(MouseButton button)
    {
        if (button == MouseButton.Middle)
        {
            middleDragging = false;
            return;
        }
        if (button != MouseButton.Left)
            return;
        if (uiManager.MouseUp(mouse))
        {
            dragStart = null;
            return;
        }
        if (dragStart is not { } start)
            return;
        dragStart = null;

        bool additive = Held(Key.ShiftLeft) || Held(Key.ShiftRight);
        if (!additive)
            selection.Clear();

        if (Vector2.Distance(start, mouse) < 6)
        {
            var clicked = world.ObjectAt(mouse, Viewport, IsSelectable);
            if (clicked != null && !selection.Contains(clicked))
                selection.Add(clicked);
            return;
        }

        // Box selection: own mobile units inside the rectangle.
        float x0 = Math.Min(start.X, mouse.X), x1 = Math.Max(start.X, mouse.X);
        float y0 = Math.Min(start.Y, mouse.Y), y1 = Math.Max(start.Y, mouse.Y);
        foreach (var obj in world.Drawables.Keys)
        {
            if (obj.Owner != player || !IsSelectable(obj) || obj.IsKindOf(Kind.Structure))
                continue;
            if (world.ToScreen(obj.Position, Viewport) is { } p && p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 && !selection.Contains(obj))
                selection.Add(obj);
        }
    }

    static bool IsSelectable(GameObject obj) =>
        obj.IsKindOf(Kind.Selectable) && !obj.HasStatus(Status.Unselectable) && !obj.IsEffectivelyDead;

    void OnScroll(float amount)
    {
        if (uiManager.Scroll(mouse, amount))
            return;
        if (Held(Key.ControlLeft) || Held(Key.ControlRight))
        {
            world.Camera.Yaw += amount * 0.1f;
            return;
        }
        if (placing != null)
        {
            placingAngle += amount * MathF.PI / 8;
            return;
        }
        var data = Game.Catalog.GameData;
        float min = RuleValueOr(data.Get("MinCameraHeight"), 120) * 1.5f, max = RuleValueOr(data.Get("MaxCameraHeight"), 310) * 2.5f;
        world.Camera.Distance = Math.Clamp(world.Camera.Distance * (amount > 0 ? 0.9f : 1.1f), min, max);
    }

    static float RuleValueOr(string text, float fallback) => Core.Rules.RuleValue.Float(text, fallback);

    void OnKey(Key key)
    {
        switch (key)
        {
            case Key.Escape:
                placing = null;
                attackMoveMode = false;
                break;
            case Key.S:
                Game.Commands.Enqueue(new StopCommand(player.Index, SelectedIds()));
                break;
            case Key.A:
                attackMoveMode = selection.Any(o => o.AI != null);
                break;
            case Key.Space:
                if (selection.FirstOrDefault() is { } first)
                    world.LookAt(first.Position);
                break;
        }
    }

    /// <summary>Selects the player's first object of a template and centres on it (used for testing and tutorials).</summary>
    public void SelectFirst(string templateName)
    {
        var obj = Game.ObjectsOwnedBy(player).FirstOrDefault(o => o.Template.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase));
        if (obj == null)
            return;
        selection.Clear();
        selection.Add(obj);
        world.LookAt(obj.Position);
    }

    uint[] SelectedIds() => selection.Where(o => o.Owner == player).Select(o => o.Id).ToArray();

    /// <summary>A right click does whatever makes sense for the selection and what is under the cursor.</summary>
    void IssueContextOrder()
    {
        var mine = selection.Where(o => o.Owner == player).ToList();
        if (mine.Count == 0)
            return;
        var target = world.ObjectAt(mouse, Viewport, o => !o.IsEffectivelyDead);
        var ids = mine.Select(o => o.Id).ToArray();

        // Factories: right-click the ground to set a rally point.
        if (mine.All(o => o.IsKindOf(Kind.Structure)))
        {
            if (world.GroundPointAt(mouse, Viewport) is { } rally)
                foreach (var factory in mine)
                    Game.Commands.Enqueue(new SetRallyPointCommand(player.Index, factory.Id, rally));
            return;
        }

        if (target != null && target.Owner.IsEnemyOf(player) && !target.IsKindOf(Kind.Prop) && !target.IsKindOf(Kind.Shrubbery))
        {
            Game.Commands.Enqueue(new AttackObjectCommand(player.Index, ids, target.Id));
            return;
        }
        if (target != null && target.Owner == player && target.IsKindOf(Kind.Structure) && mine.Any(o => o.IsKindOf(Kind.Dozer))
            && (target.IsUnderConstruction || target.Health < target.MaxHealth))
        {
            Game.Commands.Enqueue(new RepairCommand(player.Index, mine.Where(o => o.IsKindOf(Kind.Dozer)).Select(o => o.Id).ToArray(), target.Id));
            return;
        }
        if (world.GroundPointAt(mouse, Viewport) is { } point)
            Game.Commands.Enqueue(new MoveCommand(player.Index, ids, point));
    }

    void OnCommand(CommandButton button, GameObject source)
    {
        switch (button.Command.ToUpperInvariant())
        {
            case "DOZER_CONSTRUCT":
                placing = Game.Templates.Find(button.Object);
                placingBuilder = source;
                placingAngle = placing == null ? 0 : RuleUnits.Angle(placing.Rules.GetString("PlacementViewAngle"));
                break;
            case "UNIT_BUILD":
                Game.Commands.Enqueue(new ProduceUnitCommand(player.Index, source.Id, button.Object));
                break;
            case "PLAYER_UPGRADE":
            case "OBJECT_UPGRADE":
                Game.Commands.Enqueue(new ProduceUpgradeCommand(player.Index, source.Id, button.Upgrade));
                break;
            case "SELL":
                Game.Commands.Enqueue(new SellCommand(player.Index, source.Id));
                selection.Remove(source);
                break;
            case "STOP":
                Game.Commands.Enqueue(new StopCommand(player.Index, SelectedIds()));
                break;
            case "ATTACK_MOVE":
                attackMoveMode = true;
                break;
            case "GUARD":
            case "GUARD_WITHOUT_PURSUIT":
            case "GUARD_FLYING_UNITS_ONLY":
                Game.Commands.Enqueue(new GuardCommand(player.Index, SelectedIds(), source.Position));
                break;
            default:
                ShowMessage($"'{button.Command}' commands are not available yet.");
                break;
        }
    }

    void TryPlaceBuilding()
    {
        if (placing == null || placingBuilder == null || world.GroundPointAt(mouse, Viewport) is not { } point)
            return;
        var result = Game.Building.CheckLocation(placing, point, placingAngle, player, placingBuilder);
        if (result != BuildLocation.Ok)
        {
            ShowMessage(result switch
            {
                BuildLocation.NeedsShoreline => "A shipyard must be built on the shoreline, half on land and half on water.",
                BuildLocation.NotFlatEnough => ui.Strings.Get("GUI:CantBuildNotFlatEnough"),
                BuildLocation.ObjectsInTheWay => ui.Strings.Get("GUI:CantBuildObjectsInTheWay"),
                BuildLocation.TooCloseToSupplies => ui.Strings.Get("GUI:CantBuildTooCloseToSupplies"),
                _ => ui.Strings.Get("GUI:CantBuildRestrictedTerrain"),
            });
            return;
        }
        if (player.Money.Amount < Game.Costs.BuildCost(player, placing))
        {
            ShowMessage(ui.Strings.Get("GUI:NotEnoughMoneyToBuild"));
            return;
        }
        Game.Commands.Enqueue(new ConstructCommand(player.Index, placingBuilder.Id, placing.Name, point, placingAngle));
        if (!Held(Key.ShiftLeft))
            placing = null;
    }

    // ---- Rendering ----

    public void Render(float seconds)
    {
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        world.Render(seconds, width / (float)height);

        var batch = ui.Batch;
        batch.Begin(width, height);
        DrawSelectionMarkers(batch);
        DrawPlacementGhost(batch);
        if (dragStart is { } start && Vector2.Distance(start, mouse) >= 6)
        {
            var rect = new Rect(Math.Min(start.X, mouse.X), Math.Min(start.Y, mouse.Y), MathF.Abs(mouse.X - start.X), MathF.Abs(mouse.Y - start.Y));
            batch.FillRect(rect, new Vector4(0.2f, 1, 0.2f, 0.12f));
            batch.DrawRect(rect, new Vector4(0.3f, 1, 0.3f, 0.9f));
        }

        EnsureUi();
        uiManager.Draw(ui.Gui);
        DrawTooltip(batch);
        DrawMessage(batch);
        batch.End();
    }

    /// <summary>Selected objects get a health bar above them, as in the original.</summary>
    void DrawSelectionMarkers(SpriteBatch batch)
    {
        foreach (var obj in selection)
        {
            if (world.ToScreen(obj.Position + new Vector3(0, 0, obj.Height + 6), Viewport) is not { } top)
                continue;
            float barWidth = Math.Clamp(obj.Radius * 1.4f, 20, 90) * world.Camera.FieldOfView / MathF.Max(0.3f, world.Camera.Distance / 420f);
            barWidth = Math.Clamp(barWidth, 18, 100);
            float ratio = obj.MaxHealth > 0 ? obj.Health / obj.MaxHealth : 0;
            var color = ratio > 0.5f ? new Vector4(0, 0.9f, 0, 1) : ratio > 0.25f ? new Vector4(1, 0.85f, 0, 1) : new Vector4(1, 0, 0, 1);
            var back = new Rect(top.X - barWidth / 2, top.Y - 4, barWidth, 4);
            batch.FillRect(back, new Vector4(0, 0, 0, 0.7f));
            batch.FillRect(back with { Width = barWidth * ratio }, color);
            if (obj.IsUnderConstruction)
                batch.FillRect(new Rect(back.X, back.Y + 5, barWidth * obj.ConstructionPercent / 100f, 3), new Vector4(0.3f, 0.6f, 1, 1));
        }
    }

    /// <summary>While placing a building, show its footprint, green when legal and red when not.</summary>
    void DrawPlacementGhost(SpriteBatch batch)
    {
        if (placing == null || world.GroundPointAt(mouse, Viewport) is not { } point)
            return;
        bool legal = Game.Building.CheckLocation(placing, point, placingAngle, player, placingBuilder) == BuildLocation.Ok;
        var color = legal ? new Vector4(0.2f, 1, 0.2f, 0.9f) : new Vector4(1, 0.2f, 0.2f, 0.9f);
        float a = placing.GeometryMajorRadius, b = placing.GeometryMinorRadius;
        var corners = new[] { new Vector3(a, b, 0), new Vector3(-a, b, 0), new Vector3(-a, -b, 0), new Vector3(a, -b, 0) }
            .Select(c => Vector3.Transform(c, Matrix4x4.CreateRotationZ(placingAngle)) + point)
            .Select(c => c with { Z = Game.Terrain.LayerHeight(c.X, c.Y) + 1 })
            .Select(c => world.ToScreen(c, Viewport))
            .ToList();
        if (corners.Any(c => c == null))
            return;
        for (int i = 0; i < 4; i++)
            batch.DrawLine(corners[i]!.Value, corners[(i + 1) % 4]!.Value, color, 2);
        // An arrow from the centre shows which way the building faces (for shipyards: towards the water).
        if (world.ToScreen(point, Viewport) is { } center && world.ToScreen(point + MathUtil.Forward(placingAngle) * a, Viewport) is { } front)
            batch.DrawLine(center, front, color, 2);
    }

    void DrawTooltip(SpriteBatch batch)
    {
        if (controlBar.Tooltip is not { } tooltip)
            return;
        var font = ui.Fonts.Get("Arial", 10);
        var lines = font.WrapLines(tooltip, 260 * ui.Gui.Scale.X);
        float w = lines.Max(l => font.Measure(l)) + 12, h = lines.Count * font.LineHeight + 8;
        var rect = new Rect(Math.Min(mouse.X + 16, width - w - 4), Math.Max(4, mouse.Y - h - 8), w, h);
        batch.FillRect(rect, new Vector4(0, 0, 0, 0.8f));
        batch.DrawRect(rect, new Vector4(0.8f, 0.7f, 0.2f, 1));
        float y = rect.Y + 4;
        foreach (string line in lines)
        {
            font.Draw(batch, line, new Vector2(rect.X + 6, y), Vector4.One);
            y += font.LineHeight;
        }
    }

    void DrawMessage(SpriteBatch batch)
    {
        if (messageTime <= 0 || message == null)
            return;
        var font = ui.Fonts.Get("Arial", 12, bold: true);
        float w = font.Measure(message);
        font.DrawWithShadow(batch, message, new Vector2((width - w) / 2, height * 0.12f), new Vector4(1, 1, 1, Math.Min(1, messageTime)), new Vector4(0, 0, 0, Math.Min(1, messageTime)));
    }

    public void Dispose() => world.Dispose();
}

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
    readonly Audio.GameAudio audio;
    readonly ControlBar controlBar;
    readonly Radar radar;
    UI.Gui.GuiWindow? radarWindow;
    readonly Player player;
    readonly List<GameObject> selection = new();
    readonly ScriptDirector director;

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

    // Tiberian Dawn factions: TD's sidebar, and the finished structure being placed (factory, queue entry).
    readonly TiberianDawn.TdSidebarPanel? tdSidebar;
    (uint Factory, uint Entry)? placingReady;

    // A special power waiting for its target click (e.g. an airstrike's target area).
    (CommandButton Button, GameObject Source)? pendingPower;
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
        world.Viewer = player;
        audio = new Audio.GameAudio(ui.Audio, game, world, player);
        controlBar = new ControlBar(ui, game, player);
        if (TiberianDawn.TdSidebarPanel.IsTdPlayer(player))
        {
            tdSidebar = new TiberianDawn.TdSidebarPanel(game, player, ui);
            tdSidebar.Layout(width, height);
        }
        radar = new Radar(gl, game, player, world);
        radar.Message += label => ShowMessage(ui.Strings.Get(label));
        radar.Sound += sound => game.Events.PlaySound(sound, null, player);
        if (controlBar.Layout.Find("LeftHUD") is { } leftHud)
        {
            radarWindow = leftHud;
            leftHud.CustomDraw = (w, context) => radar.Draw(context.Batch, w.ScreenRect, ui.Images);
        }
        director = new ScriptDirector(game, world, ui, player);
        director.MessageWindowRequested += ShowMessageWindow;
        game.IsNamedObjectSelected = name => selection.Any(o => o.Name == name);
        game.Events.OnDeselect += obj => selection.Remove(obj);
        game.Events.OnForceSelect += (obj, center, sound) =>
        {
            selection.Clear();
            selection.Add(obj);
            if (sound.Length > 0)
                game.Events.PlaySound(sound, null, player);
            if (center)
                world.LookAt(obj.Position);
        };
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
            Action<IMouse, Vector2> move = (_, p) => OnMouseMove(p);
            Action<IMouse, MouseButton> down = (_, b) => OnMouseDown(b);
            Action<IMouse, MouseButton> up = (_, b) => OnMouseUp(b);
            Action<IMouse, ScrollWheel> scroll = (_, w) => OnScroll(w.Y);
            m.MouseMove += move;
            m.MouseDown += down;
            m.MouseUp += up;
            m.Scroll += scroll;
            unsubscribe.Add(() => { m.MouseMove -= move; m.MouseDown -= down; m.MouseUp -= up; m.Scroll -= scroll; });
        }
        foreach (var k in input.Keyboards)
        {
            Action<IKeyboard, Key, int> keyDown = (_, key, _) => OnKey(key);
            k.KeyDown += keyDown;
            unsubscribe.Add(() => k.KeyDown -= keyDown);
        }
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
        messageWindow?.Layout(ui.Gui.Scale);
        tdSidebar?.Layout(width, height);
    }

    /// <summary>Debug: run the VICTORY or DEFEAT script action after this many seconds of play.</summary>
    public static (string Action, float At)? DebugEndGame { get; set; }
    float debugClock;

    // ---- Update ----

    /// <summary>Called once the game is over and its end timer has run out (the shell takes over).</summary>
    public Action? GameEnded { get; set; }

    public void Update(float seconds)
    {
        if (Game.IsGameOver && GameEnded is { } ended)
        {
            GameEnded = null;
            ended();
            return;
        }
        debugClock += seconds;
        // Debug (--perf): which objects there are most of, every 5 seconds.
        if (GameProfiler.Enabled && (int)(debugClock / 5) != (int)((debugClock - seconds) / 5))
        {
            var top = Game.Objects.GroupBy(o => o.Template.Name).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key}={g.Count()}");
            Console.WriteLine($"[perf] frame {Game.Frame}: {Game.Objects.Count()} objects; {string.Join(", ", top)}");
        }
        if (DebugEndGame is { } end && debugClock >= end.At && Game.Scripts != null)
        {
            DebugEndGame = null;
            Game.Scripts.ExecuteActions([new Logic.Scripting.ScriptAction { Name = end.Action }]);
        }
        world.ViewportSize = Viewport;
        world.Update(seconds);
        long t0 = GameProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        audio.Update();
        if (GameProfiler.Enabled) GameProfiler.Add("#client audio", t0);
        UpdateDemoFx(seconds);
        selection.RemoveAll(o => o.IsDestroyed || o.IsEffectivelyDead);
        t0 = GameProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        controlBar.Update(selection);
        if (GameProfiler.Enabled) GameProfiler.Add("#client control bar", t0);
        if (director.InputEnabled && !director.Camera.IsBusy)
            ScrollCamera(seconds);
        director.Update(seconds);
        cursors ??= ui.CursorsFor(input);
        if (!director.InputEnabled || director.Camera.IsBusy)
            edgeScroll = null;
        var (cursorName, cursorDirection) = ChooseCursor();
        cursors.Set(cursorName, cursorDirection);
        cursors.Update(seconds);
        if (messageTime > 0)
            messageTime -= seconds;
    }

    void ScrollCamera(float seconds)
    {
        var camera = world.Camera;
        float speed = camera.Distance * 1.6f * seconds;
        Vector3 move = Vector3.Zero;
        // Edge scrolling only once the mouse has really been in the window (not its (0,0) start).
        bool edges = mouseSeen;
        // Screen direction of mouse edge scrolling (x right, y down), for the Scroll cursor.
        var edge = Vector2.Zero;
        if (edges && mouse.X <= 2) edge.X -= 1;
        if (edges && mouse.X >= width - 3) edge.X += 1;
        if (edges && mouse.Y <= 2) edge.Y -= 1;
        if (edges && mouse.Y >= height - 3) edge.Y += 1;
        edgeScroll = edge == Vector2.Zero ? null : edge;
        if (Held(Key.Left) || edges && mouse.X <= 2) move -= camera.ScreenRight;
        if (Held(Key.Right) || edges && mouse.X >= width - 3) move += camera.ScreenRight;
        if (Held(Key.Up) || edges && mouse.Y <= 2) move += camera.ScreenUp;
        if (Held(Key.Down) || edges && mouse.Y >= height - 3) move -= camera.ScreenUp;
        if (move != Vector3.Zero)
        {
            var target = camera.Target + Vector3.Normalize(move) * speed;
            target.X = Math.Clamp(target.X, 0, Game.Terrain.WorldWidth);
            target.Y = Math.Clamp(target.Y, 0, Game.Terrain.WorldHeight);
            world.LookAt(target);
        }
    }

    // ---- Input ----

    bool mouseSeen;
    Vector2? edgeScroll;
    UI.Cursors? cursors;

    void OnMouseMove(Vector2 position)
    {
        mouseSeen = true;
        if (!director.InputEnabled)
        {
            mouse = position;
            return;
        }
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

    // The victory / defeat window (the original's ScriptActions m_messageWindow).
    UI.Gui.GuiLayout? messageWindow;

    void ShowMessageWindow(string? file)
    {
        if (messageWindow != null)
        {
            uiManager.Remove(messageWindow);
            messageWindow = null;
        }
        if (file == null || !ui.Content.FileSystem.Exists("Window/" + file))
            return;
        EnsureUi();
        messageWindow = UI.Gui.GuiLayout.Load(ui.Content.FileSystem, "Window/" + file);
        uiManager.Push(messageWindow);
        messageWindow.Layout(ui.Gui.Scale);
    }

    void EnsureUi()
    {
        if (uiInitialized)
            return;
        uiInitialized = true;
        uiManager.Push(controlBar.Layout);
    }

    /// <summary>
    /// A click on the radar (the original's LeftHUDInput): with nothing selected, or with the right
    /// button, the camera jumps there; otherwise the selection moves there (or attack-moves, or the
    /// waiting special power fires there).
    /// </summary>
    bool HandleRadarClick(MouseButton button)
    {
        if (radarWindow == null || !radar.IsActive || !radarWindow.ScreenRect.Contains(mouse))
            return false;
        if (radar.PixelToWorld(radarWindow.ScreenRect, mouse) is not { } point)
            return true;
        var ids = SelectedIds();
        if (ids.Length == 0 || button == MouseButton.Right)
        {
            world.LookAt(point);
            return true;
        }
        if (pendingPower is { } power && power.Button.HasOption("NEED_TARGET_POS"))
        {
            bool fromCommandCenter = !power.Button.Command.Equals("SPECIAL_POWER", StringComparison.OrdinalIgnoreCase);
            if (PowerSource(power.Button, power.Source) is { } user)
                Game.Commands.Enqueue(new DoSpecialPowerCommand(player.Index, power.Button.SpecialPower, fromCommandCenter ? user.Id : 0, fromCommandCenter ? [] : ids, Position: point));
            pendingPower = null;
        }
        else
            Game.Commands.Enqueue(new MoveCommand(player.Index, ids, point, AttackMove: attackMoveMode));
        attackMoveMode = false;
        return true;
    }

    void OnMouseDown(MouseButton button)
    {
        EnsureUi();
        if (director.DismissPopup() || !director.InputEnabled)
            return;
        if (button is MouseButton.Left or MouseButton.Right && HandleRadarClick(button))
            return;
        if (button == MouseButton.Middle)
        {
            middleDragging = true;
            lastMiddle = mouse;
            return;
        }
        if (button is MouseButton.Left or MouseButton.Right && tdSidebar != null && tdSidebar.Contains(mouse))
        {
            var ready = tdSidebar.Click(mouse, button == MouseButton.Right, out string? why);
            if (why != null)
                ShowMessage(why);
            if (ready is { } place)
            {
                placing = place.Template;
                placingBuilder = place.Factory;
                placingReady = (place.Factory.Id, place.EntryId);
                placingAngle = 0;
            }
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
            if (pendingPower is { } power)
            {
                UseSpecialPowerAtCursor(power.Button, power.Source);
                pendingPower = null;
                return;
            }
            dragStart = mouse;
        }
        else if (button == MouseButton.Right)
        {
            if (placing != null || attackMoveMode || pendingPower != null)
            {
                placing = null;
                placingReady = null;
                attackMoveMode = false;
                pendingPower = null;
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
        // TD: clicking the MCV you have selected deploys it.
        if (!additive && Vector2.Distance(start, mouse) < 6 && selection.Count == 1
            && world.ObjectAt(mouse, Viewport, IsSelectable) is { } again && again == selection[0] && DeploySelectedMcvs())
            return;
        if (!additive)
            selection.Clear();

        if (Vector2.Distance(start, mouse) < 6)
        {
            var clicked = world.ObjectAt(mouse, Viewport, IsSelectable);
            if (clicked != null && !selection.Contains(clicked))
            {
                selection.Add(clicked);
                world.FlashAsSelected(clicked);
                PlayVoice("VoiceSelect");
            }
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
            {
                selection.Add(obj);
                world.FlashAsSelected(obj);
            }
        }
        if (selection.Count > 0)
            PlayVoice("VoiceSelect");
    }

    /// <summary>The first selected own unit answers (the original plays one voice for the group).</summary>
    void PlayVoice(string voiceKey)
    {
        if (selection.FirstOrDefault(o => o.Owner == player && !o.IsEffectivelyDead) is not { } unit)
            return;
        string voice = unit.Template.Rules.GetString(voiceKey);
        if (voice.Length > 0)
            audio.PlayUiSound(voice, unit);
    }

    static bool IsSelectable(GameObject obj) =>
        obj.IsKindOf(Kind.Selectable) && !obj.HasStatus(Status.Unselectable) && !obj.IsEffectivelyDead;

    void OnScroll(float amount)
    {
        if (!director.InputEnabled)
            return;
        if (tdSidebar != null && tdSidebar.Contains(mouse))
        {
            tdSidebar.Scroll(mouse, amount);
            return;
        }
        if (uiManager.Scroll(mouse, amount))
            return;
        if (Held(Key.ControlLeft) || Held(Key.ControlRight))
        {
            world.Camera.Yaw += amount * 0.1f;
            return;
        }
        if (placing != null)
        {
            if (placingReady == null) // TD structures always face the same way
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
        if (!director.InputEnabled)
            return;
        switch (key)
        {
            case Key.Escape:
                placing = null;
                placingReady = null;
                attackMoveMode = false;
                pendingPower = null;
                break;
            case Key.S:
                Game.Commands.Enqueue(new StopCommand(player.Index, SelectedIds()));
                break;
            case Key.D:
                DeploySelectedMcvs();
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

    /// <summary>Tiberian Dawn: the selected MCVs unfold into Construction Yards (D, or clicking a selected MCV).</summary>
    bool DeploySelectedMcvs()
    {
        var mcvs = selection.Where(o => o.Owner == player && o.FindModule<Logic.Modules.Behaviors.TdDeployToStructure>() != null).ToList();
        if (mcvs.Count == 0)
            return false;
        if (!mcvs.Any(o => o.FindModule<Logic.Modules.Behaviors.TdDeployToStructure>()!.CanDeploy()))
        {
            ShowMessage("Cannot deploy here.");
            return true;
        }
        Game.Commands.Enqueue(new DeployCommand(player.Index, mcvs.Select(o => o.Id).ToArray()));
        return true;
    }

    /// <summary>Selects the player's first object of a template and centres on it (used for testing and tutorials).</summary>
    string? demoFx;
    float demoFxTimer;

    /// <summary>Debugging: plays an FX list in front of the camera every second and a half.</summary>
    public void DemoFx(string fxList) => demoFx = fxList;

    void UpdateDemoFx(float seconds)
    {
        if (demoFx == null || (demoFxTimer -= seconds) > 0)
            return;
        demoFxTimer = 1.5f;
        var at = world.Camera.Target;
        world.Effects.Fx.DoFxPos(demoFx, at with { Z = Game.Terrain.GroundHeight(at) });
    }

    /// <summary>Points the camera at a spot on the map (debug: --look-at).</summary>
    public void LookAt(Vector3 point) => world.LookAt(point);

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
    /// <summary>What a right-click would do here: the cursor to show for it, and the order (null = nothing).</summary>
    readonly record struct ContextOrder(string Cursor, Action? Execute);

    void IssueContextOrder() => EvaluateContext().Execute?.Invoke();

    enum AttackResult { NotPossible, Invalid, PossibleAfterMoving, Possible }

    /// <summary>
    /// The original's getCanSelectedObjectsAttack: Possible if any selected unit can hit the target
    /// from where it is, PossibleAfterMoving if one could after moving, Invalid if units with weapons
    /// can't hit it at all, NotPossible if none of them has a weapon.
    /// </summary>
    static AttackResult CanAttack(IEnumerable<GameObject> units, GameObject target)
    {
        var result = AttackResult.NotPossible;
        foreach (var unit in units)
        {
            if (!unit.Weapons.HasAnyWeapon || unit == target)
                continue;
            if (unit.Weapons.ChooseFor(target) is null)
            {
                if (result == AttackResult.NotPossible)
                    result = AttackResult.Invalid;
                continue;
            }
            float range = unit.Weapons.MaxRangeAgainst(target) + target.BoundingCircleRadius;
            if (MathUtil.Distance2D(unit.Position, target.Position) <= range)
                return AttackResult.Possible;
            if (unit.AI != null && unit.IsMobile)
                result = AttackResult.PossibleAfterMoving;
            else if (result == AttackResult.NotPossible)
                result = AttackResult.Invalid;
        }
        return result;
    }

    /// <summary>
    /// The original's evaluateContextCommand with the cursor from createCommandHint: one decision
    /// gives both the hint cursor while hovering and the order on a right-click.
    /// </summary>
    ContextOrder EvaluateContext()
    {
        var target = world.ObjectAt(mouse, Viewport, o => !o.IsEffectivelyDead);
        bool targetSelectable = target != null && IsSelectable(target);
        var mine = selection.Where(o => o.Owner == player).ToList();
        if (mine.Count == 0)
            return new(targetSelectable && target!.Owner == player ? "Select" : "Arrow", null);
        var ids = mine.Select(o => o.Id).ToArray();

        // A firing Particle Cannon: right-click steers its beam (the original's override destination).
        if (mine.Count == 1 && world.GroundPointAt(mouse, Viewport) is { } steerTo
            && mine[0].FindModules<Logic.Modules.SpecialPowers.SpecialPowerModule>().FirstOrDefault(m => m.HasOverridableDestinationActive) is { Power: { } steered })
            return new("ParticleUplinkCannon", () => Game.Commands.Enqueue(new SpecialPowerOverrideDestinationCommand(player.Index, steered.Name, mine[0].Id, steerTo)));

        // Factories: right-click the ground to set a rally point.
        if (mine.All(o => o.IsKindOf(Kind.Structure)))
        {
            if (targetSelectable)
                return new("Select", null);
            var factories = mine.Where(o => o.FindModule<Logic.Modules.Production.IProductionExit>() != null).ToList();
            if (factories.Count == 0 || world.GroundPointAt(mouse, Viewport) is not { } rally)
                return new("GenericInvalid", null);
            return new("SetRallyPoint", () =>
            {
                foreach (var factory in factories)
                    Game.Commands.Enqueue(new SetRallyPointCommand(player.Index, factory.Id, rally));
            });
        }

        if (target != null && target.Owner.IsEnemyOf(player) && !target.IsKindOf(Kind.Prop) && !target.IsKindOf(Kind.Shrubbery))
        {
            switch (CanAttack(mine, target))
            {
                case AttackResult.Possible:
                    return new("AttackObj", () => { Game.Commands.Enqueue(new AttackObjectCommand(player.Index, ids, target.Id)); PlayVoice("VoiceAttack"); });
                case AttackResult.PossibleAfterMoving:
                    return new("OutRange", () => { Game.Commands.Enqueue(new AttackObjectCommand(player.Index, ids, target.Id)); PlayVoice("VoiceAttack"); });
                case AttackResult.Invalid:
                    if (!mine.Any(o => !o.Weapons.HasAnyWeapon && o.FindModules<OpenGZH.Logic.Modules.Collide.CrateCollide>().Any(c => c.IsValidToExecute(target))))
                        return new("GenericInvalid", null);
                    break;
            }
        }
        if (target != null && target.Owner == player && target.IsKindOf(Kind.Structure) && mine.Any(o => o.IsKindOf(Kind.Dozer))
            && (target.IsUnderConstruction || target.Health < target.MaxHealth))
        {
            var dozers = mine.Where(o => o.IsKindOf(Kind.Dozer)).Select(o => o.Id).ToArray();
            return new(target.IsUnderConstruction ? "ResumeConstruction" : "DoRepair",
                () => Game.Commands.Enqueue(new RepairCommand(player.Index, dozers, target.Id)));
        }
        // Damaged vehicles right-clicked onto our repair bay go in for repairs.
        if (target != null && mine.Where(o => Logic.Modules.AI.AIUpdateInterface.CanGetRepairedAt(o, target)).Select(o => o.Id).ToArray() is { Length: > 0 } damaged)
            return new(target.IsKindOf("HEAL_PAD") ? "GetHealed" : "GetRepaired", () => Game.Commands.Enqueue(new GetRepairedCommand(player.Index, damaged, target.Id)));
        // Harvesters right-clicked onto a supply dock or our supply center go and use it (canTransferSuppliesAt).
        if (target != null && mine.Where(o => OpenGZH.Logic.Modules.Supply.SupplyGatherer.Of(o) is { } g && g.CanTransferSuppliesAt(target))
                .Select(o => o.Id).ToArray() is { Length: > 0 } harvesters)
            return new("Dock", () => Game.Commands.Enqueue(new DockCommand(player.Index, harvesters, target.Id)));
        // Infantry and vehicles right-clicked onto a ferry board it (the original's canDockAt).
        if (target != null && target.FindModule<OpenGZH.Logic.Modules.Contain.RailedTransportDockUpdate>() != null
            && mine.Where(o => o != target && (o.IsKindOf("INFANTRY") || o.IsKindOf("VEHICLE"))).Select(o => o.Id).ToArray() is { Length: > 0 } boarders)
            return new("Dock", () => Game.Commands.Enqueue(new DockCommand(player.Index, boarders, target.Id)));
        // Hijackers, terrorists and saboteurs go for a target they can act on.
        if (target != null && mine.Where(o => o.FindModules<OpenGZH.Logic.Modules.Collide.CrateCollide>().Any(c => c.IsValidToExecute(target))).Select(o => o.Id).ToArray() is { Length: > 0 } agents)
            return new("EnterAggressive", () => Game.Commands.Enqueue(new EnterCommand(player.Index, agents, target.Id)));
        // Right-clicking our own transport, a garrisonable building or a tunnel: get in.
        if (target?.Contain is { } contain && target.Owner.IsEnemyOf(player) == false
            && mine.Where(o => o != target && o.AI != null && contain.IsValidContainerFor(o, false)).Select(o => o.Id).ToArray() is { Length: > 0 } riders)
            return new("EnterFriendly", () => Game.Commands.Enqueue(new EnterCommand(player.Index, riders, target.Id)));

        // One of ours under the cursor: a left-click would select it, so no order.
        if (targetSelectable && target!.Owner == player && !target.IsKindOf("MINE"))
            return new("Select", null);
        if (world.GroundPointAt(mouse, Viewport) is { } point)
        {
            // The original's quick path check: somewhere at least one of them can go.
            var movers = mine.Where(o => o.AI != null && o.IsMobile).ToList();
            if (movers.Count == 0 || !movers.Any(o => Game.Pathfinder.IsPassable(point, o.AI!.Surfaces) || o.AI!.Surfaces.HasFlag(Logic.Locomotion.Surfaces.Air)))
                return new("GenericInvalid", null);
            return new("Move", () => { Game.Commands.Enqueue(new MoveCommand(player.Index, ids, point)); PlayVoice("VoiceMove"); });
        }
        return new("Arrow", null);
    }

    /// <summary>The cursor for where the mouse is now (the original's InGameUI hint cursors).</summary>
    (string Name, int Direction) ChooseCursor()
    {
        if (edgeScroll is { } scroll)
        {
            // Frame 0 points right and the frames go clockwise (W3DMouse::setCursorDirection).
            float theta = MathF.Atan2(scroll.Y, scroll.X);
            theta = (theta + MathF.Tau) % MathF.Tau;
            int frame = (int)(theta / (MathF.Tau / 8) + 0.5f) % 8;
            return ("Scroll", frame);
        }
        if (!director.InputEnabled)
            return ("Arrow", 0);
        if (uiManager.IsOverInterface(mouse))
            return ("Arrow", 0);
        if (placing != null)
            return ("Arrow", 0);
        if (attackMoveMode)
            return ("AttackMove", 0);
        if (pendingPower is { } power)
        {
            var button = power.Button;
            bool wantsObject = button.HasOption("NEED_TARGET_ENEMY_OBJECT") || button.HasOption("NEED_TARGET_NEUTRAL_OBJECT") || button.HasOption("NEED_TARGET_ALLY_OBJECT");
            bool valid = !wantsObject || PowerTargetValid(button, world.ObjectAt(mouse, Viewport, o => !o.IsEffectivelyDead));
            string name = valid ? button.CursorName : button.InvalidCursorName;
            return (name.Length > 0 ? name : "Target", 0);
        }
        return (EvaluateContext().Cursor, 0);
    }

    /// <summary>Whether a power that targets an object can be used on this one (enemy / neutral / ally, as the button asks).</summary>
    bool PowerTargetValid(CommandButton button, GameObject? target)
    {
        if (target == null)
            return false;
        var relation = player.RelationshipTo(target.Owner);
        if (target.Owner == player)
            relation = Logic.Players.Relationship.Allies;
        return button.HasOption("NEED_TARGET_ENEMY_OBJECT") && relation == Logic.Players.Relationship.Enemies
            || button.HasOption("NEED_TARGET_NEUTRAL_OBJECT") && relation == Logic.Players.Relationship.Neutral
            || button.HasOption("NEED_TARGET_ALLY_OBJECT") && relation == Logic.Players.Relationship.Allies;
    }

    /// <summary>Command buttons whose power needs a target on the map or an object.</summary>
    static bool NeedsTarget(CommandButton button) =>
        button.HasOption("NEED_TARGET_POS") || button.HasOption("NEED_TARGET_ENEMY_OBJECT") || button.HasOption("NEED_TARGET_NEUTRAL_OBJECT")
        || button.HasOption("NEED_TARGET_ALLY_OBJECT") || button.HasOption("CONTEXTMODE_COMMAND");

    /// <summary>The object that uses a power: the button's owner, or (for generals' powers) any of ours that has it.</summary>
    GameObject? PowerSource(CommandButton button, GameObject source)
    {
        if (Game.Catalog.SpecialPower(button.SpecialPower) is not { } power)
            return null;
        if (Logic.Modules.SpecialPowers.SpecialPowerRules.ModuleFor(source, power) != null)
            return source;
        return Game.ObjectsOwnedBy(player).FirstOrDefault(o => Logic.Modules.SpecialPowers.SpecialPowerRules.ModuleFor(o, power) is { IsReady: true })
            ?? Game.ObjectsOwnedBy(player).FirstOrDefault(o => Logic.Modules.SpecialPowers.SpecialPowerRules.ModuleFor(o, power) != null);
    }

    void UseSpecialPowerAtCursor(CommandButton button, GameObject source)
    {
        if (button.Command is "HIJACK_VEHICLE" or "CONVERT_TO_CARBOMB" or "SABOTAGE_BUILDING")
        {
            // These units do their deed by walking into the target.
            if (world.ObjectAt(mouse, Viewport, o => !o.IsEffectivelyDead) is { } victim)
                Game.Commands.Enqueue(new EnterCommand(player.Index, SelectedIds(), victim.Id));
            return;
        }
        if (button.Command.Equals("COMBATDROP", StringComparison.OrdinalIgnoreCase))
        {
            var building = world.ObjectAt(mouse, Viewport, o => !o.IsEffectivelyDead && o.IsKindOf(Kind.Structure));
            if (world.GroundPointAt(mouse, Viewport) is { } drop)
                Game.Commands.Enqueue(new CombatDropCommand(player.Index, SelectedIds(), building?.Id ?? 0, building?.Position ?? drop));
            return;
        }
        if (PowerSource(button, source) is not { } user)
            return;
        bool fromCommandCenter = !button.Command.Equals("SPECIAL_POWER", StringComparison.OrdinalIgnoreCase);
        var target = world.ObjectAt(mouse, Viewport, o => !o.IsEffectivelyDead);
        bool wantsObject = button.HasOption("NEED_TARGET_ENEMY_OBJECT") || button.HasOption("NEED_TARGET_NEUTRAL_OBJECT") || button.HasOption("NEED_TARGET_ALLY_OBJECT");
        uint sourceId = fromCommandCenter ? user.Id : 0;
        uint[] units = fromCommandCenter ? [] : SelectedIds();
        if (wantsObject && target != null)
            Game.Commands.Enqueue(new DoSpecialPowerCommand(player.Index, button.SpecialPower, sourceId, units, TargetObject: target.Id, Options: button.OptionBits()));
        else if (world.GroundPointAt(mouse, Viewport) is { } point)
            Game.Commands.Enqueue(new DoSpecialPowerCommand(player.Index, button.SpecialPower, sourceId, units, Position: point, Options: button.OptionBits()));
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
            case "COMBATDROP":
            case "HIJACK_VEHICLE":
            case "CONVERT_TO_CARBOMB":
            case "SABOTAGE_BUILDING":
                pendingPower = (button, source);
                break;
            case "HACK_INTERNET":
                Game.Commands.Enqueue(new HackInternetCommand(player.Index, SelectedIds()));
                break;
            case "TOGGLE_OVERCHARGE":
                Game.Commands.Enqueue(new ToggleOverchargeCommand(player.Index, SelectedIds()));
                break;
            case "EXECUTE_RAILED_TRANSPORT":
                Game.Commands.Enqueue(new ExecuteRailedTransportCommand(player.Index, SelectedIds()));
                break;
            case "EVACUATE":
                Game.Commands.Enqueue(new EvacuateCommand(player.Index, SelectedIds()));
                break;
            case "SPECIAL_POWER":
            case "SPECIAL_POWER_FROM_COMMAND_CENTER":
            case "SPECIAL_POWER_FROM_SHORTCUT":
                if (NeedsTarget(button))
                {
                    pendingPower = (button, source);
                }
                else if (PowerSource(button, source) is { } user)
                {
                    bool fromCommandCenter = !button.Command.Equals("SPECIAL_POWER", StringComparison.OrdinalIgnoreCase);
                    Game.Commands.Enqueue(new DoSpecialPowerCommand(player.Index, button.SpecialPower, fromCommandCenter ? user.Id : 0, fromCommandCenter ? [] : SelectedIds(), Options: button.OptionBits()));
                }
                break;
            default:
                ShowMessage($"'{button.Command}' commands are not available yet.");
                break;
        }
    }

    void TryPlaceBuilding()
    {
        if (placing == null || placingBuilder == null || PlacementPoint() is not { } point)
            return;
        var result = Game.Building.CheckLocation(placing, point, placingAngle, player, placingReady != null ? null : placingBuilder);
        if (result != BuildLocation.Ok)
        {
            ShowMessage(result switch
            {
                BuildLocation.NeedsShoreline => "A shipyard must be built on the water, right next to the shore.",
                BuildLocation.TooFarFromCommandCenter => placingReady != null ? "Must be placed next to one of your buildings." : "Too far from your Command Center.",
                BuildLocation.NotFlatEnough => ui.Strings.Get("GUI:CantBuildNotFlatEnough"),
                BuildLocation.ObjectsInTheWay => ui.Strings.Get("GUI:CantBuildObjectsInTheWay"),
                BuildLocation.TooCloseToSupplies => ui.Strings.Get("GUI:CantBuildTooCloseToSupplies"),
                _ => ui.Strings.Get("GUI:CantBuildRestrictedTerrain"),
            });
            return;
        }
        if (placingReady is { } ready)
        {
            // Tiberian Dawn: the structure is paid for and finished; it only needs a place.
            Game.Commands.Enqueue(new PlaceReadyStructureCommand(player.Index, ready.Factory, ready.Entry, point, 0));
            placing = null;
            placingReady = null;
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
        // The control bar hides while the letterbox is on, as in the original.
        if (!director.Letterbox)
        {
            uiManager.Draw(ui.Gui);
            tdSidebar?.Draw(batch);
            DrawTooltip(batch);
        }
        director.Draw(batch, width, height);
        DrawMessage(batch);
        batch.End();
    }

    /// <summary>Selected objects get a health bar above them, as in the original.</summary>
    void DrawSelectionMarkers(SpriteBatch batch)
    {
        foreach (var obj in selection)
        {
            // Infantry are short: their bar goes a little higher, clear of the head (and of TD soldiers' sprites).
            float lift = obj.IsKindOf(Kind.Infantry) ? 16 : 6;
            if (world.ToScreen(obj.Position + new Vector3(0, 0, obj.Height + lift), Viewport) is not { } top)
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
        if (placing == null || PlacementPoint() is not { } point)
            return;
        bool legal = Game.Building.CheckLocation(placing, point, placingAngle, player, placingReady != null ? null : placingBuilder) == BuildLocation.Ok;
        var color = legal ? new Vector4(0.2f, 1, 0.2f, 0.9f) : new Vector4(1, 0.2f, 0.2f, 0.9f);
        float a = placing.GeometryMajorRadius, b = placing.GeometryMinorRadius;
        var footprintCenter = point;
        if (TdFootprint(placing) is { } td)
        {
            // TD shows the whole footprint, bib included.
            footprintCenter = point + td.Offset;
            (a, b) = (td.HalfX, td.HalfY);
        }
        var corners = new[] { new Vector3(a, b, 0), new Vector3(-a, b, 0), new Vector3(-a, -b, 0), new Vector3(a, -b, 0) }
            .Select(c => Vector3.Transform(c, Matrix4x4.CreateRotationZ(placingAngle)) + footprintCenter)
            .Select(c => c with { Z = (Game.Terrain.IsUnderwater(c.X, c.Y, out float water) ? Math.Max(water, Game.Terrain.LayerHeight(c.X, c.Y)) : Game.Terrain.LayerHeight(c.X, c.Y)) + 1 })
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

    /// <summary>A TD structure's placement footprint (TdFootprint: centre offset and half sizes), if it has one.</summary>
    static (Vector3 Offset, float HalfX, float HalfY)? TdFootprint(ObjectTemplate template)
    {
        var words = Core.Rules.RuleValue.Words(template.Rules.GetString("TdFootprint"));
        if (words.Length != 4)
            return null;
        return (new Vector3(Core.Rules.RuleValue.Float(words[0]), Core.Rules.RuleValue.Float(words[1]), 0),
            Core.Rules.RuleValue.Float(words[2]), Core.Rules.RuleValue.Float(words[3]));
    }

    /// <summary>Where the building would go: the ground under the mouse; TD structures snap to TD's 36-unit cells.</summary>
    Vector3? PlacementPoint()
    {
        if (world.GroundPointAt(mouse, Viewport) is not { } point || placing == null)
            return null;
        if (TdFootprint(placing) is not { } td)
            return point;
        const float Cell = 36;
        var corner = point + td.Offset - new Vector3(td.HalfX, td.HalfY, 0);
        var snapped = new Vector3(MathF.Round(corner.X / Cell) * Cell, MathF.Round(corner.Y / Cell) * Cell, 0);
        var result = snapped + new Vector3(td.HalfX, td.HalfY, 0) - td.Offset;
        return result with { Z = Game.Terrain.GroundHeight(result) };
    }

    void DrawTooltip(SpriteBatch batch)
    {
        if (tdSidebar != null && tdSidebar.TooltipAt(mouse) is { } tdTip)
        {
            var tipFont = ui.Fonts.Get("Arial", 10, bold: true);
            float tw = tipFont.Measure(tdTip);
            var at = new Vector2(Math.Max(4, tdSidebar.Area.X - tw - 10), mouse.Y);
            batch.FillRect(new Rect(at.X - 4, at.Y - 2, tw + 8, 18), new Vector4(0, 0, 0, 0.8f));
            tipFont.Draw(batch, tdTip, at, Vector4.One);
            return;
        }
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

    readonly List<Action> unsubscribe = new();

    public void Dispose()
    {
        foreach (var action in unsubscribe)
            action();
        ui.Audio.StopAllAudio(); // the game's sounds and music end with it
        world.Dispose();
    }
}

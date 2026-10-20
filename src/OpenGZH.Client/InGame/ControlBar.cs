using System.Numerics;
using OpenGZH.Client.UI;
using OpenGZH.Client.UI.Gui;
using OpenGZH.Core.Rules;
using OpenGZH.Logic;
using OpenGZH.Logic.Modules.Production;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Client.InGame;

/// <summary>
/// The original command bar (Window/ControlBar.wnd) at the bottom of the screen, themed with the
/// player's ControlBarScheme: command buttons for the selected object, the production queue,
/// money, power and the side buttons.
/// </summary>
public sealed class ControlBar
{
    readonly UiResources ui;
    readonly GameLogic game;
    readonly Player player;
    readonly RuleNode? scheme;

    public GuiLayout Layout { get; }

    /// <summary>The command each of the 14 slots currently carries (null = empty).</summary>
    readonly CommandButton?[] slotButtons = new CommandButton?[15];
    readonly GuiPushButton?[] slots = new GuiPushButton?[15];
    readonly GuiPushButton?[] queueButtons = new GuiPushButton?[10];

    GameObject? shownObject;

    /// <summary>A command button was clicked (the game screen decides what it means).</summary>
    public event Action<CommandButton, GameObject>? CommandClicked;

    /// <summary>A production queue entry was clicked (to cancel it).</summary>
    public event Action<GameObject, ProductionUpdate.Entry>? QueueEntryClicked;

    public string? Tooltip { get; private set; }

    public ControlBar(UiResources ui, GameLogic game, Player player)
    {
        this.ui = ui;
        this.game = game;
        this.player = player;
        Layout = GuiLayout.Load(ui.Content.FileSystem, "Window/ControlBar.wnd");

        // The scheme for this side, falling back to the base faction (e.g. "America8x6").
        var schemes = game.Rules.Section("ControlBarSchemes");
        scheme = schemes.Entries.Select(e => e.Value).FirstOrDefault(s => s.GetString("Side").Equals(player.Side, StringComparison.OrdinalIgnoreCase))
              ?? schemes.Entries.Select(e => e.Value).FirstOrDefault(s => s.GetString("Side").Equals(player.BaseSide, StringComparison.OrdinalIgnoreCase));

        // Observer panels are only for spectators.
        foreach (string observerWindow in new[] { "ObserverPlayerInfoWindow", "ObserverPlayerListWindow" })
            if (Layout.Find(observerWindow) is { } window)
                window.Hidden = true;

        for (int i = 1; i <= 14; i++)
        {
            int slot = i;
            if (Layout.Find<GuiPushButton>($"ButtonCommand{i:00}") is { } button)
            {
                slots[i] = button;
                button.Clicked += _ =>
                {
                    if (slotButtons[slot] is { } command && shownObject != null)
                        CommandClicked?.Invoke(command, shownObject);
                };
                button.CustomDraw = (w, context) => DrawSlot(slot, w, context);
            }
        }
        for (int i = 1; i <= 9; i++)
        {
            int index = i - 1;
            if (Layout.Find<GuiPushButton>($"ButtonQueue{i:00}") is { } button)
            {
                queueButtons[i] = button;
                button.CustomDraw = (w, context) => DrawQueueEntry(index, w, context);
                button.Clicked += _ =>
                {
                    var queue = shownObject?.FindModule<ProductionUpdate>()?.Queue;
                    if (queue != null && index < queue.Count)
                        QueueEntryClicked?.Invoke(shownObject!, queue[index]);
                };
            }
        }

        // The scheme's background art is drawn by the layout's BackgroundMarker hook, behind everything else.
        if (Layout.Find("ControlBarParent") is { } parent)
            parent.CustomDraw = (_, context) => DrawSchemeBackground(context);
        if (Layout.Find("ExpBarForeground") is { } expBar && scheme?.GetString("ExpBarForegroundImage") is { Length: > 0 } expImage)
            expBar.CustomDraw = (w, context) => ui.Images.Draw(context.Batch, expImage, w.ScreenRect);
        if (Layout.Find("ExpBarForeground") is { } expDefault)
            expDefault.Definition.Status.Remove("IMAGE");
        ThemeSideButtons();
    }

    // ---- Scheme ----

    void DrawSchemeBackground(GuiContext context)
    {
        if (scheme == null || !scheme.TryGet("ImagePart", out var parts))
            return;
        foreach (var part in parts.AsList().OrderBy(p => RuleValue.Int(p.GetString("Layer"))).Reverse())
        {
            var position = RuleValue.Coord(part.GetString("Position"));
            var size = RuleValue.Coord(part.GetString("Size"));
            var rect = new Rect(position.X * context.Scale.X, position.Y * context.Scale.Y, size.X * context.Scale.X, size.Y * context.Scale.Y);
            ui.Images.Draw(context.Batch, part.GetString("ImageName"), rect);
        }
    }

    void ThemeSideButtons()
    {
        if (scheme == null)
            return;
        void Theme(string window, string prefix)
        {
            if (Layout.Find<GuiPushButton>(window) is not { } button)
                return;
            string enabled = scheme.GetString(prefix + "Enable"), hilite = scheme.GetString(prefix + "Hightlited"), pushed = scheme.GetString(prefix + "Pushed");
            button.CustomDraw = (w, context) =>
            {
                var b = (GuiPushButton)w;
                string image = !b.IsEnabled ? scheme.GetString(prefix + "Disabled", enabled) : b.Hilited ? hilite : enabled;
                ui.Images.Draw(context.Batch, image.Length > 0 ? image : enabled, w.ScreenRect);
            };
        }
        Theme("ButtonOptions", "OptionsButton");
        Theme("ButtonIdleWorker", "IdleWorkerButton");
        Theme("PopupCommunicator", "BuddyButton");
        Theme("ButtonPlaceBeacon", "BeaconButton");
        Theme("ButtonGeneral", "GeneralButton");
        Theme("ButtonLarge", "MinMaxButton");
    }

    Vector4 SchemeColor(string key, Vector4 fallback) => RuleColor.Parse(scheme?.GetString(key), fallback);

    // ---- Per-frame update ----

    public void Update(IReadOnlyList<GameObject> selection)
    {
        var selected = selection.FirstOrDefault(o => !o.IsEffectivelyDead && o.Owner == player);
        shownObject = selected;

        var commandWindow = Layout.Find("CommandWindow");
        var underConstruction = Layout.Find("UnderConstructionWindow");
        if (commandWindow != null)
            commandWindow.Hidden = selected == null || selected.IsUnderConstruction;
        if (underConstruction != null)
            underConstruction.Hidden = selected is not { IsUnderConstruction: true };

        Array.Clear(slotButtons);
        if (selected != null && !selected.IsUnderConstruction)
        {
            foreach (var (slot, button) in CommandSets.Buttons(game.Catalog, CurrentCommandSet(selected)))
                if (slot >= 1 && slot <= 14)
                    slotButtons[slot] = button;
        }
        for (int i = 1; i <= 14; i++)
        {
            if (slots[i] == null)
                continue;
            slots[i]!.Hidden = slotButtons[i] == null;
            slots[i]!.Enabled = slotButtons[i] is { } b && IsAvailable(b, selected!);
        }

        var queue = selected?.FindModule<ProductionUpdate>()?.Queue;
        var queueWindow = Layout.Find("ProductionQueueWindow");
        var unitWindow = Layout.Find("WinUnitSelected");
        if (queueWindow != null)
            queueWindow.Hidden = queue == null || queue.Count == 0;
        if (unitWindow != null)
            unitWindow.Hidden = selected == null || queue is { Count: > 0 };

        if (Layout.Find<GuiStaticText>("MoneyDisplay") is { } money)
            money.Text = "$ " + player.Money.Amount;
        if (Layout.Find("PowerWindow") is { } power)
            power.CustomDraw = (w, context) => DrawPowerBar(w.ScreenRect, context);
        if (Layout.Find("CameoWindow") is { } cameo)
            cameo.CustomDraw = (w, context) => DrawPortrait(w.ScreenRect, context);

        Tooltip = null;
        for (int i = 1; i <= 14; i++)
            if (slots[i] is { Hilited: true, Hidden: false } && slotButtons[i] is { } hovered)
                Tooltip = TooltipFor(hovered);
    }

    /// <summary>Objects may switch command sets with upgrades (CommandSetUpgrade); the template's set is the default.</summary>
    static string CurrentCommandSet(GameObject obj) => obj.CommandSetOverride ?? obj.Template.CommandSet;

    bool IsAvailable(CommandButton button, GameObject selected)
    {
        switch (button.Command.ToUpperInvariant())
        {
            case "UNIT_BUILD":
            case "DOZER_CONSTRUCT":
                var template = game.Templates.Find(button.Object);
                return template != null && Prerequisites.AreMet(game, player, template.Rules);
            case "PLAYER_UPGRADE":
                return !player.HasUpgrade(button.Upgrade) && (game.Catalog.Upgrade(button.Upgrade) is not { } u || Prerequisites.AreMet(game, player, u));
            case "OBJECT_UPGRADE":
                return !selected.HasObjectUpgrade(button.Upgrade);
            default:
                return true;
        }
    }

    string TooltipFor(CommandButton button)
    {
        string title = ui.Strings.Get(button.TextLabel).Replace("&", "");
        var template = button.Object.Length > 0 ? game.Templates.Find(button.Object) : null;
        int cost = template != null ? game.Costs.BuildCost(player, template)
            : button.Upgrade.Length > 0 && game.Catalog.Upgrade(button.Upgrade) is { } upgrade ? RuleValue.Int(upgrade.GetString("BuildCost")) : 0;
        string description = button.DescriptLabel.Length > 0 ? ui.Strings.Get(button.DescriptLabel) : "";
        return cost > 0 ? $"{title}\n${cost}\n{description}" : $"{title}\n{description}";
    }

    // ---- Drawing ----

    void DrawSlot(int slot, GuiWindow window, GuiContext context)
    {
        if (slotButtons[slot] is not { } button)
            return;
        var r = window.ScreenRect;
        ui.Images.Draw(context.Batch, button.ButtonImage, r, window.IsEnabled ? Vector4.One : new Vector4(0.45f, 0.45f, 0.45f, 1));

        // Button border colour by kind, from the scheme.
        string borderKey = button.Command.ToUpperInvariant() switch
        {
            "UNIT_BUILD" or "DOZER_CONSTRUCT" => "ButtonBorderBuildColor",
            "PLAYER_UPGRADE" or "OBJECT_UPGRADE" => "ButtonBorderUpgradeColor",
            "SELL" or "SET_RALLY_POINT" or "STOP" or "GUARD" or "ATTACK_MOVE" => "ButtonBorderSystemColor",
            _ => "ButtonBorderActionColor",
        };
        var border = SchemeColor(borderKey, new Vector4(0.3f, 0.5f, 0.9f, 1));
        context.Batch.DrawRect(r, window.Hilited ? Vector4.One : border, window.Hilited ? 2 : 1);
    }

    void DrawQueueEntry(int index, GuiWindow window, GuiContext context)
    {
        var queue = shownObject?.FindModule<ProductionUpdate>()?.Queue;
        if (queue == null || index >= queue.Count)
            return;
        var entry = queue[index];
        string image = entry.Unit?.ButtonImage
            ?? (entry.Upgrade != null ? game.Catalog.Upgrade(entry.Upgrade)?.GetString("ButtonImage") : null) ?? "";
        var r = window.ScreenRect;
        ui.Images.Draw(context.Batch, image, r);
        if (index == 0)
            DrawBuildClock(context, r, entry.Progress);
    }

    /// <summary>The original "build up" clock: a dark overlay sweeping away as the item completes.</summary>
    void DrawBuildClock(GuiContext context, Rect r, float progress)
    {
        var color = SchemeColor("BuildUpClockColor", new Vector4(0, 0, 0, 0.6f));
        float remaining = 1 - Math.Clamp(progress, 0, 1);
        context.Batch.FillRect(new Rect(r.X, r.Y, r.Width, r.Height * remaining), color);
    }

    void DrawPowerBar(Rect r, GuiContext context)
    {
        var energy = player.Energy;
        float produced = (energy.Production + energy.Bonus) * energy.ProductionBonus;
        float max = Math.Max(1, Math.Max(produced, energy.Consumption));
        float fill = produced <= 0 ? 0 : Math.Min(1, energy.Consumption / max);
        var color = energy.HasSufficientPower ? (fill > 0.8f ? new Vector4(1, 0.85f, 0, 1) : new Vector4(0, 0.85f, 0, 1)) : new Vector4(0.9f, 0, 0, 1);
        context.Batch.FillRect(new Rect(r.X, r.Y, r.Width * Math.Max(fill, produced > 0 ? 0.02f : 1), r.Height), color);
    }

    void DrawPortrait(Rect r, GuiContext context)
    {
        if (shownObject == null)
            return;
        ui.Images.Draw(context.Batch, shownObject.Template.SelectPortrait, r);
    }
}

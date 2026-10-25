using System.Numerics;
using OpenGZH.Client.UI;
using OpenGZH.Logic;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Modules.Production;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Client.TiberianDawn;

/// <summary>
/// Tiberian Dawn's sidebar for a player of a TD faction in a Zero Hour engine match: structures on the
/// left (from the Construction Yard), units on the right (from barracks, factory and helipad), as TD's
/// SidebarClass lays them out. Left click builds (a finished structure is then placed on the map);
/// right click puts the last one of that kind back (cancel). The mouse wheel scrolls a column.
/// </summary>
public sealed class TdSidebarPanel
{
    const float CameoW = 64, CameoH = 48, Gap = 4;

    readonly GameLogic game;
    readonly Player player;
    readonly UiResources ui;
    readonly int[] scroll = new int[2];

    public Rect Area { get; private set; }

    /// <summary>One cameo: what it makes and the factory that makes it.</summary>
    public sealed record Item(ObjectTemplate Template, GameObject Factory, ProductionUpdate Production);

    public TdSidebarPanel(GameLogic game, Player player, UiResources ui)
    {
        this.game = game;
        this.player = player;
        this.ui = ui;
    }

    /// <summary>Whether this player's faction is a Tiberian Dawn one.</summary>
    public static bool IsTdPlayer(Player player) =>
        Core.Rules.RuleValue.Bool(player.Faction?.GetString("TdFaction") ?? "");

    public void Layout(int width, int height)
    {
        float scale = height / 720f;
        float w = CameoW * 2 + Gap * 3;
        float top = 8 * scale, bottom = height - 225 * scale;
        Area = new Rect(width - w - 4, top, w, Math.Max(CameoH + Gap * 2, bottom - top));
    }

    public bool Contains(Vector2 point) =>
        point.X >= Area.X && point.X < Area.X + Area.Width && point.Y >= Area.Y && point.Y < Area.Y + Area.Height;

    int VisibleRows => Math.Max(1, (int)((Area.Height - Gap) / (CameoH + Gap)));

    // ---- What can be built ----

    IEnumerable<GameObject> Factories(bool structures) =>
        game.ObjectsOwnedBy(player).Where(o => !o.IsEffectivelyDead && !o.IsUnderConstruction
            && o.FindModule<ProductionUpdate>() is { } p && p.PlacesStructures == structures);

    /// <summary>Column 0: structures; column 1: units (each from the least busy factory that makes it).</summary>
    public List<Item> Column(int column)
    {
        var items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        foreach (var factory in Factories(column == 0))
        {
            var production = factory.FindModule<ProductionUpdate>()!;
            foreach (string name in CommandSets.BuildableObjects(game.Catalog, factory.CommandSetName, "UNIT_BUILD"))
            {
                if (game.Templates.Find(name) is not { } template || !player.CanBuild(template)
                    || !Prerequisites.AreMet(game, player, template.Rules))
                    continue;
                if (!items.TryGetValue(name, out var existing) || production.Queue.Count < existing.Production.Queue.Count)
                    items[name] = new Item(template, factory, production);
            }
        }
        return items.Values.ToList();
    }

    /// <summary>The queue entries for this kind of object, at any of the player's factories of that column.</summary>
    IEnumerable<(ProductionUpdate Production, GameObject Factory, ProductionUpdate.Entry Entry)> Entries(ObjectTemplate template, int column) =>
        Factories(column == 0).SelectMany(f => f.FindModule<ProductionUpdate>()!.Queue
            .Where(e => e.Unit?.Name == template.Name).Select(e => (f.FindModule<ProductionUpdate>()!, f, e)));

    (int Column, Item Item)? ItemAt(Vector2 point)
    {
        for (int column = 0; column < 2; column++)
        {
            var list = Column(column);
            for (int row = 0; row < VisibleRows; row++)
            {
                int index = row + scroll[column];
                if (index >= list.Count)
                    break;
                var r = CameoRect(column, row);
                if (point.X >= r.X && point.X < r.X + r.Width && point.Y >= r.Y && point.Y < r.Y + r.Height)
                    return (column, list[index]);
            }
        }
        return null;
    }

    Rect CameoRect(int column, int row) =>
        new(Area.X + Gap + column * (CameoW + Gap), Area.Y + Gap + row * (CameoH + Gap), CameoW, CameoH);

    // ---- Input ----

    /// <summary>
    /// A click on the sidebar. Returns a finished structure to place (its factory and queue entry), else
    /// null; <paramref name="message"/> says why nothing happened, if anything should be said.
    /// </summary>
    public (ObjectTemplate Template, GameObject Factory, uint EntryId)? Click(Vector2 point, bool right, out string? message)
    {
        message = null;
        if (ItemAt(point) is not { } hit)
            return null;
        var (column, item) = hit;
        var entries = Entries(item.Template, column).ToList();
        if (right)
        {
            // TD's right click: hold, then cancel. Here: cancel the last one of this kind.
            if (entries.Count > 0)
            {
                var last = entries[^1];
                game.Commands.Enqueue(new CancelProductionCommand(player.Index, last.Factory.Id, last.Entry.Id));
            }
            return null;
        }
        if (column == 0)
        {
            if (entries.FirstOrDefault(e => e.Entry.ReadyToPlace) is { Entry: { } ready } done)
                return (item.Template, done.Factory, ready.Id);
            // The yard makes one structure at a time.
            if (Factories(true).All(f => f.FindModule<ProductionUpdate>()!.Queue.Count > 0))
            {
                message = "Unable to comply, building in progress.";
                return null;
            }
            var yard = Factories(true).First(f => f.FindModule<ProductionUpdate>()!.Queue.Count == 0);
            Produce(yard, item.Template, out message);
            return null;
        }
        Produce(item.Factory, item.Template, out message);
        return null;
    }

    void Produce(GameObject factory, ObjectTemplate template, out string? message)
    {
        message = factory.FindModule<ProductionUpdate>()!.CanQueueUnit(template) switch
        {
            "NotEnoughMoney" => "Insufficient funds.",
            "QueueFull" => "Unable to comply, building in progress.",
            _ => null,
        };
        if (message == null)
            game.Commands.Enqueue(new ProduceUnitCommand(player.Index, factory.Id, template.Name));
    }

    public void Scroll(Vector2 point, float amount)
    {
        int column = point.X < Area.X + Area.Width / 2 ? 0 : 1;
        int max = Math.Max(0, Column(column).Count - VisibleRows);
        scroll[column] = Math.Clamp(scroll[column] - Math.Sign(amount), 0, max);
    }

    /// <summary>The cameo's name under the mouse (for a tooltip), if any.</summary>
    public string? TooltipAt(Vector2 point) => ItemAt(point) is { } hit
        ? $"{ui.Strings.Get(hit.Item.Template.DisplayName)}  ${game.Costs.BuildCost(player, hit.Item.Template)}" : null;

    // ---- Drawing ----

    public void Draw(SpriteBatch batch)
    {
        batch.FillRect(Area, new Vector4(0.05f, 0.06f, 0.05f, 0.85f));
        batch.DrawRect(Area, new Vector4(0.45f, 0.45f, 0.35f, 1));
        var font = ui.Fonts.Get("Arial", 9, bold: true);
        for (int column = 0; column < 2; column++)
        {
            var list = Column(column);
            scroll[column] = Math.Clamp(scroll[column], 0, Math.Max(0, list.Count - VisibleRows));
            for (int row = 0; row < VisibleRows; row++)
            {
                int index = row + scroll[column];
                if (index >= list.Count)
                    break;
                var item = list[index];
                var r = CameoRect(column, row);
                bool affordable = player.Money.Amount >= game.Costs.BuildCost(player, item.Template);
                ui.Images.Draw(batch, item.Template.ButtonImage, r, affordable ? Vector4.One : new Vector4(0.6f, 0.6f, 0.6f, 1));
                var entries = Entries(item.Template, column).ToList();
                if (entries.Count == 0)
                    continue;
                var first = entries[0].Entry;
                if (first.ReadyToPlace)
                {
                    Label(batch, font, "READY", r);
                }
                else
                {
                    // TD's clock: the part still to build is darkened.
                    float progress = Math.Clamp(first.Progress, 0, 1);
                    batch.FillRect(new Rect(r.X, r.Y + r.Height * progress, r.Width, r.Height * (1 - progress)), new Vector4(0, 0, 0, 0.55f));
                }
                if (entries.Count > 1)
                    font.DrawWithShadow(batch, entries.Count.ToString(), new Vector2(r.X + r.Width - 12, r.Y + 2), Vector4.One, new Vector4(0, 0, 0, 1));
            }
            // Scroll hints.
            if (scroll[column] > 0)
                batch.FillRect(new Rect(Area.X + Gap + column * (CameoW + Gap) + CameoW / 2 - 6, Area.Y + 1, 12, 2), Vector4.One);
            if (list.Count - scroll[column] > VisibleRows)
                batch.FillRect(new Rect(Area.X + Gap + column * (CameoW + Gap) + CameoW / 2 - 6, Area.Y + Area.Height - 3, 12, 2), Vector4.One);
        }
    }

    static void Label(SpriteBatch batch, GameFont font, string text, Rect r)
    {
        float w = font.Measure(text);
        font.DrawWithShadow(batch, text, new Vector2(r.X + (r.Width - w) / 2, r.Y + r.Height / 2 - 6), new Vector4(1, 1, 1, 1), new Vector4(0, 0, 0, 1));
    }
}

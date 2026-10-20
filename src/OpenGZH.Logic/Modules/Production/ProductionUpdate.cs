using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules.Production;

/// <summary>
/// A factory's build queue (barracks, war factory, command center, shipyard...). Units and upgrades are
/// paid for in full when queued, build one at a time, and leave through the building's exit module.
/// Low power slows production down (GameData LowEnergyPenaltyModifier).
/// </summary>
public sealed class ProductionUpdate : Module, IUpdateModule
{
    public sealed class Entry
    {
        public required uint Id { get; init; }
        public ObjectTemplate? Unit { get; init; }
        public string? Upgrade { get; init; }
        public required int Cost { get; init; }
        public required int TotalFrames { get; init; }
        public float Progress { get; set; }

        /// <summary>How many objects one purchase gives (e.g. some infantry come in pairs).</summary>
        public int Quantity { get; init; } = 1;
        public int Produced { get; set; }

        public bool IsUnit => Unit != null;
        public string Name => Unit?.Name ?? Upgrade ?? "";
    }

    readonly List<Entry> queue = new();
    uint nextEntryId = 1;
    int maxQueueEntries;
    int doorOpeningFrames, doorWaitOpenFrames, doorCloseFrames;
    int doorCloseAtFrame = -1;
    readonly Dictionary<string, int> quantityModifiers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Entry> Queue => queue;

    /// <summary>Factories keep producing only through the disable types listed (default: just DISABLED_HELD).</summary>
    public FlagSet DisabledTypesToProcess { get; private set; }

    protected override void Initialize()
    {
        DisabledTypesToProcess = Definition.Settings.Has("DisabledTypesToProcess")
            ? Flags.Disabled.Parse(Setting("DisabledTypesToProcess")) : DisabledMask.Held;
        maxQueueEntries = IntSetting("MaxQueueEntries", 9);
        doorOpeningFrames = FramesSetting("DoorOpeningTime");
        doorWaitOpenFrames = FramesSetting("DoorWaitOpenTime");
        doorCloseFrames = FramesSetting("DoorCloseTime");
        foreach (string line in ListSetting("QuantityModifier"))
        {
            string[] words = RuleValue.Words(line);
            if (words.Length >= 2)
                quantityModifiers[words[0]] = RuleValue.Int(words[1], 1);
        }
    }

    // ---- Queueing ----

    /// <summary>Why something can't be built right now (null if it can).</summary>
    public string? CanQueueUnit(ObjectTemplate unit)
    {
        var player = Owner.Owner;
        if (queue.Count >= maxQueueEntries)
            return "QueueFull";
        if (!Prerequisites.AreMet(Game, player, unit.Rules))
            return "PrerequisitesNotMet";
        if (player.Money.Amount < CostOf(unit))
            return "NotEnoughMoney";
        if (MaxSimultaneousReached(unit))
            return "MaximumReached";
        return null;
    }

    public Entry? QueueUnit(ObjectTemplate unit)
    {
        if (CanQueueUnit(unit) != null)
            return null;
        int cost = CostOf(unit);
        if (!Owner.Owner.Money.TryWithdraw(cost))
            return null;
        var entry = new Entry
        {
            Id = nextEntryId++,
            Unit = unit,
            Cost = cost,
            TotalFrames = BuildFrames(unit.BuildTimeSeconds),
            Quantity = quantityModifiers.GetValueOrDefault(unit.Name, 1),
        };
        queue.Add(entry);
        Owner.WakeUpdate(this);
        return entry;
    }

    public Entry? QueueUpgrade(string upgradeName)
    {
        var upgrade = Game.Catalog.Upgrade(upgradeName);
        if (upgrade == null || queue.Count >= maxQueueEntries || queue.Any(e => e.Upgrade == upgradeName))
            return null;
        bool isPlayerUpgrade = !upgrade.GetString("Type", "PLAYER").Equals("OBJECT", StringComparison.OrdinalIgnoreCase);
        if (isPlayerUpgrade ? Owner.Owner.HasUpgrade(upgradeName) : Owner.HasObjectUpgrade(upgradeName))
            return null;
        int cost = RuleValue.Int(upgrade.GetString("BuildCost"));
        if (!Owner.Owner.Money.TryWithdraw(cost))
            return null;
        var entry = new Entry
        {
            Id = nextEntryId++,
            Upgrade = upgradeName,
            Cost = cost,
            TotalFrames = BuildFrames(RuleValue.Float(upgrade.GetString("BuildTime"), 1)),
        };
        queue.Add(entry);
        Owner.WakeUpdate(this);
        return entry;
    }

    /// <summary>Cancels a queued item and refunds its full cost.</summary>
    public void Cancel(uint entryId)
    {
        var entry = queue.FirstOrDefault(e => e.Id == entryId);
        if (entry == null)
            return;
        queue.Remove(entry);
        Owner.Owner.Money.Deposit(entry.Cost);
    }

    public void CancelAll()
    {
        foreach (var entry in queue.ToList())
            Cancel(entry.Id);
    }

    int CostOf(ObjectTemplate unit) => Game.Costs.BuildCost(Owner.Owner, unit);

    int BuildFrames(float seconds) => Math.Max(1, LogicTime.FramesFromSeconds(seconds / Math.Max(0.01f, Game.Catalog.GameData.StructureBuildSpeedRate)));

    bool MaxSimultaneousReached(ObjectTemplate unit)
    {
        int max = RuleValue.Int(unit.Rules.GetString("MaxSimultaneousOfType"), 0);
        if (max <= 0)
            return false;
        int existing = Game.ObjectsOwnedBy(Owner.Owner).Count(o => o.Template.Name.Equals(unit.Name, StringComparison.OrdinalIgnoreCase));
        int queued = Game.ObjectsOwnedBy(Owner.Owner)
            .Select(o => o.FindModule<ProductionUpdate>()).Where(p => p != null)
            .Sum(p => p!.queue.Count(e => e.Unit == unit));
        return existing + queued >= max;
    }

    // ---- Building ----

    public int Update()
    {
        UpdateDoors();
        if (queue.Count == 0)
            return 15;
        if (Owner.IsUnderConstruction || Owner.IsDisabledBy("DISABLED_HACKED") || Owner.IsDisabledBy("DISABLED_EMP") || Owner.IsDisabledBy("DISABLED_SCRIPT_DISABLED"))
            return 5;

        var entry = queue[0];
        float rate = Owner.Owner.Energy.HasSufficientPower ? 1f : Game.Catalog.GameData.LowEnergyPenaltyModifier;
        if (entry.Progress < 1f)
            entry.Progress = Math.Min(1f, entry.Progress + rate / entry.TotalFrames);

        if (entry.Progress >= 1f)
            Complete(entry);
        return 0;
    }

    void Complete(Entry entry)
    {
        if (entry.Upgrade != null)
        {
            var upgrade = Game.Catalog.Upgrade(entry.Upgrade);
            bool isObject = upgrade?.GetString("Type", "PLAYER").Equals("OBJECT", StringComparison.OrdinalIgnoreCase) == true;
            if (isObject)
                Owner.GiveUpgrade(entry.Upgrade);
            else
                Owner.Owner.CompleteUpgrade(entry.Upgrade);
            Game.Events.PlaySound(upgrade?.GetString("ResearchCompleteSound") ?? "", Owner.Position, Owner.Owner);
            Game.Events.Eva("UpgradeComplete", Owner.Owner);
            queue.Remove(entry);
            return;
        }

        var exit = Owner.FindModule<IProductionExit>();
        if (exit != null && !exit.CanExitNow)
            return; // wait for the door / parking space

        var unit = Game.CreateObject(entry.Unit!, Owner.Owner, Owner.Position, Owner.Orientation);
        unit.ProducerId = Owner.Id;
        Owner.Owner.UnitsBuilt++;
        if (exit != null)
            exit.Exit(unit);
        else
            DefaultExit(unit);
        OpenDoor();
        Game.Events.Eva("UnitReady", Owner.Owner);
        Game.Events.PlaySound(entry.Unit!.Rules.GetString("VoiceCreate", entry.Unit.Rules["UnitSpecificSounds"]?.GetString("VoiceCreate") ?? ""), unit.Position, Owner.Owner);

        entry.Produced++;
        if (entry.Produced >= entry.Quantity)
            queue.Remove(entry);
    }

    /// <summary>With no exit module, the unit appears beside the building.</summary>
    void DefaultExit(GameObject unit)
    {
        var position = Owner.Position + MathUtil.Forward(Owner.Orientation) * (Owner.Radius + unit.Radius + 5);
        unit.AI?.Teleport(position);
    }

    void OpenDoor()
    {
        if (doorOpeningFrames <= 0)
            return;
        Owner.SetCondition("DOOR_1_OPENING");
        doorCloseAtFrame = Frame + doorOpeningFrames + doorWaitOpenFrames;
    }

    void UpdateDoors()
    {
        if (doorCloseAtFrame < 0)
            return;
        if (Frame >= doorCloseAtFrame)
        {
            Owner.SetCondition("DOOR_1_OPENING", false);
            Owner.SetCondition("DOOR_1_WAITING_OPEN", false);
            Owner.SetCondition("DOOR_1_CLOSING");
            if (Frame >= doorCloseAtFrame + doorCloseFrames)
            {
                Owner.SetCondition("DOOR_1_CLOSING", false);
                doorCloseAtFrame = -1;
            }
        }
        else if (Frame >= doorCloseAtFrame - doorWaitOpenFrames)
        {
            Owner.SetCondition("DOOR_1_OPENING", false);
            Owner.SetCondition("DOOR_1_WAITING_OPEN");
        }
    }
}

/// <summary>Where and how produced units leave the factory.</summary>
public interface IProductionExit
{
    bool CanExitNow { get; }
    void Exit(GameObject unit);
    Vector3? RallyPoint { get; set; }
}

/// <summary>Checks a definition's "Prerequisites" block against what a player owns and knows.</summary>
public static class Prerequisites
{
    /// <summary>
    /// Each "Object" line must be satisfied (all lines required); the names within one line are
    /// alternatives (any one owned object will do). Every "Science" line must be known.
    /// </summary>
    public static bool AreMet(GameLogic game, Player player, RuleNode definition)
    {
        if (!definition.TryGet("Prerequisites", out var prerequisites) || !prerequisites.IsMapping)
            return true;

        if (prerequisites.TryGet("Object", out var objectLines))
        {
            var owned = game.ObjectsOwnedBy(player).Where(o => !o.IsUnderConstruction && !o.IsEffectivelyDead)
                .Select(o => o.Template.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var line in objectLines.AsList())
                if (!RuleValue.Words(line.Value).Any(owned.Contains))
                    return false;
        }

        if (prerequisites.TryGet("Science", out var scienceLines))
            foreach (var line in scienceLines.AsList())
                if (!RuleValue.Words(line.Value).Any(player.HasScience))
                    return false;

        return true;
    }
}

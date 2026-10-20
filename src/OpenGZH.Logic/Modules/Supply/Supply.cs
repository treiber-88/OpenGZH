using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.AI;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Supply;

// The economy: supply docks (warehouses) hold boxes; harvesters (trucks, Chinooks, workers) carry them
// to a supply center, where each box becomes GameData ValuePerSupplyBox in cash.

/// <summary>Marks a supply source; the work is done by its <see cref="SupplyWarehouseDockUpdate"/>.</summary>
public sealed class SupplyWarehouseCreate : Module { }

/// <summary>Marks a supply center (the computer player uses this to know where to deliver).</summary>
public sealed class SupplyCenterCreate : Module { }

/// <summary>A pile of supply boxes (StartingBoxes). Small piles vanish when empty (DeleteWhenEmpty).</summary>
public sealed class SupplyWarehouseDockUpdate : Module
{
    public int Boxes { get; private set; }
    public int StartingBoxes { get; private set; }
    bool deleteWhenEmpty;

    protected override void Initialize()
    {
        StartingBoxes = Boxes = IntSetting("StartingBoxes", 1);
        deleteWhenEmpty = BoolSetting("DeleteWhenEmpty");
    }

    /// <summary>For the supply pile's draw module: 1 = full, 0 = empty.</summary>
    public float Fullness => StartingBoxes > 0 ? Boxes / (float)StartingBoxes : 0;

    public bool TakeBox()
    {
        if (Boxes <= 0)
            return false;
        Boxes--;
        if (Boxes == 0 && deleteWhenEmpty)
            Game.DestroyObject(Owner);
        return true;
    }

    /// <summary>Scripts can refill or set the value of a warehouse (WAREHOUSE_SET_VALUE gives cash, not boxes).</summary>
    public void SetBoxes(int boxes) => Boxes = Math.Max(0, boxes);
}

/// <summary>Where harvesters deliver. Turns boxes into money for the center's owner.</summary>
public sealed class SupplyCenterDockUpdate : Module
{
    public int Deposit(int boxes, int bonus)
    {
        int value = boxes * RuleValue.Int(Game.Catalog.GameData.Get("ValuePerSupplyBox"), 100) + bonus;
        if (value <= 0)
            return 0;
        Owner.Owner.Money.Deposit(value);
        Owner.Owner.MoneyEarned += value;
        Game.Events.MoneyEarned(Owner, value);
        return value;
    }
}

/// <summary>
/// The harvest cycle shared by supply trucks, Chinooks and workers: find the nearest supply dock with
/// boxes, load up (one box per SupplyWarehouseActionDelay), carry them to the nearest own supply center,
/// unload (SupplyCenterActionDelay), repeat. A direct order from the player pauses it.
/// </summary>
public sealed class SupplyGatherer
{
    public enum Phase { Idle, ToWarehouse, Loading, ToCenter, Unloading }

    readonly AIUpdateInterface ai;
    readonly int maxBoxes;
    readonly int warehouseDelay, centerDelay;
    readonly float scanDistance;
    readonly string depletedVoice;
    readonly Func<int> bonus;

    public Phase State { get; private set; } = Phase.Idle;
    public int Boxes { get; private set; }
    public bool Enabled { get; set; } = true;

    uint warehouseId, centerId;
    int nextActionFrame;

    GameObject Owner => ai.Owner;
    GameLogic Game => ai.Owner.Game;

    public SupplyGatherer(AIUpdateInterface ai, ModuleSettings settings, Func<int> bonus)
    {
        this.ai = ai;
        this.bonus = bonus;
        maxBoxes = Math.Max(1, settings.Int("MaxBoxes", 1));
        warehouseDelay = settings.Frames("SupplyWarehouseActionDelay", 500);
        centerDelay = settings.Frames("SupplyCenterActionDelay", 500);
        scanDistance = settings.Float("SupplyWarehouseScanDistance", 700);
        depletedVoice = settings.Text("SuppliesDepletedVoice");
    }

    /// <summary>Start (or resume) gathering, optionally from a specific dock.</summary>
    public void Start(GameObject? warehouse = null)
    {
        Enabled = true;
        warehouseId = warehouse?.Id ?? 0;
        State = Boxes >= maxBoxes ? Phase.ToCenter : Phase.Idle;
    }

    public void Pause()
    {
        Enabled = false;
        State = Phase.Idle;
        Owner.SetCondition(Condition.Docking, false);
    }

    public void Update()
    {
        if (!Enabled || Owner.IsDisabled)
            return;

        switch (State)
        {
            case Phase.Idle:
                var warehouse = Game.FindObject(warehouseId) is { } chosen && HasBoxes(chosen) ? chosen : FindWarehouse();
                if (warehouse == null)
                {
                    if (Boxes > 0)
                        State = Phase.ToCenter;
                    else if (Game.Frame % 150 == 0 && depletedVoice.Length > 0 && Game.Frame > Owner.CreatedFrame + 30)
                        Game.Events.PlaySound(depletedVoice, Owner.Position, Owner.Owner);
                    return;
                }
                warehouseId = warehouse.Id;
                ai.MoveTo(DockPoint(warehouse), CommandSource.AI);
                State = Phase.ToWarehouse;
                break;

            case Phase.ToWarehouse:
                if (Game.FindObject(warehouseId) is not { } target || !HasBoxes(target))
                {
                    State = Phase.Idle;
                    warehouseId = 0;
                    return;
                }
                if (IsAt(target))
                {
                    ai.Stop(CommandSource.AI);
                    State = Phase.Loading;
                    nextActionFrame = Game.Frame + warehouseDelay;
                    Owner.SetCondition(Condition.Docking);
                }
                else if (ai.IsIdle)
                {
                    ai.MoveTo(DockPoint(target), CommandSource.AI);
                }
                break;

            case Phase.Loading:
                if (Game.Frame < nextActionFrame)
                    return;
                var dock = Game.FindObject(warehouseId)?.FindModule<SupplyWarehouseDockUpdate>();
                if (dock != null && dock.TakeBox())
                    Boxes++;
                nextActionFrame = Game.Frame + warehouseDelay;
                if (Boxes >= maxBoxes || dock == null || dock.Boxes == 0)
                {
                    Owner.SetCondition(Condition.Docking, false);
                    Owner.SetCondition(Condition.Carrying, Boxes > 0);
                    State = Boxes > 0 ? Phase.ToCenter : Phase.Idle;
                }
                break;

            case Phase.ToCenter:
                var center = Game.FindObject(centerId) is { IsEffectivelyDead: false } c && c.Owner == Owner.Owner && !c.IsUnderConstruction ? c : FindCenter();
                if (center == null)
                    return; // wait for one to be built
                if (center.Id != centerId)
                {
                    centerId = center.Id;
                    ai.MoveTo(DockPoint(center), CommandSource.AI);
                }
                if (IsAt(center))
                {
                    ai.Stop(CommandSource.AI);
                    State = Phase.Unloading;
                    nextActionFrame = Game.Frame + centerDelay;
                    Owner.SetCondition(Condition.Docking);
                }
                else if (ai.IsIdle)
                {
                    ai.MoveTo(DockPoint(center), CommandSource.AI);
                }
                break;

            case Phase.Unloading:
                if (Game.Frame < nextActionFrame)
                    return;
                Game.FindObject(centerId)?.FindModule<SupplyCenterDockUpdate>()?.Deposit(Boxes, bonus());
                Boxes = 0;
                Owner.SetCondition(Condition.Docking, false);
                Owner.SetCondition(Condition.Carrying, false);
                centerId = 0;
                State = Phase.Idle;
                break;
        }
    }

    static bool HasBoxes(GameObject obj) => obj.FindModule<SupplyWarehouseDockUpdate>() is { Boxes: > 0 } && !obj.IsEffectivelyDead;

    GameObject? FindWarehouse()
    {
        // Search around the nearest own supply center (where the harvester belongs), else around itself.
        var home = FindCenter()?.Position ?? Owner.Position;
        return Game.ObjectsInRadius(home, scanDistance)
            .Where(HasBoxes)
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, Owner.Position))
            .FirstOrDefault();
    }

    GameObject? FindCenter() =>
        Game.ObjectsOwnedBy(Owner.Owner)
            .Where(o => o.FindModule<SupplyCenterDockUpdate>() != null && !o.IsUnderConstruction && !o.IsEffectivelyDead)
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, Owner.Position))
            .FirstOrDefault();

    Vector3 DockPoint(GameObject building)
    {
        float angle = MathUtil.AngleTo(building.Position, Owner.Position);
        var point = building.Position + MathUtil.Forward(angle) * (building.Radius + Owner.Radius + 2);
        return Game.Pathfinder.NearestPassable(point, ai.Surfaces) ?? point;
    }

    bool IsAt(GameObject building) =>
        MathUtil.Distance2D(Owner.Position, building.Position) <= building.Radius + Owner.Radius + 12;
}

/// <summary>Supply truck (China) and supply-truck-like units.</summary>
public sealed class SupplyTruckAIUpdate : AIUpdateInterface
{
    public SupplyGatherer Gatherer { get; private set; } = null!;

    protected override void Initialize()
    {
        base.Initialize();
        Gatherer = new SupplyGatherer(this, new ModuleSettings(this), () => 0);
    }

    public override void OnCreate()
    {
        base.OnCreate();
        Gatherer.Start();
    }

    public override void MoveTo(Vector3 destination, CommandSource source = CommandSource.Player)
    {
        if (source == CommandSource.Player)
            Gatherer.Pause();
        base.MoveTo(destination, source);
    }

    public override int Update()
    {
        Gatherer.Update();
        return base.Update();
    }
}

/// <summary>
/// The USA Chinook: a flying supply truck (with Supply Lines it earns UpgradedSupplyBoost extra per trip).
/// Its troop-carrying and combat-drop abilities come from its transport modules.
/// </summary>
public sealed class ChinookAIUpdate : AIUpdateInterface
{
    public SupplyGatherer Gatherer { get; private set; } = null!;

    protected override void Initialize()
    {
        base.Initialize();
        int boost = IntSetting("UpgradedSupplyBoost");
        Gatherer = new SupplyGatherer(this, new ModuleSettings(this), () => Owner.Owner.HasUpgrade("Upgrade_AmericaSupplyLines") ? boost : 0);
    }

    public override void OnCreate()
    {
        base.OnCreate();
        Gatherer.Start();
    }

    public override void MoveTo(Vector3 destination, CommandSource source = CommandSource.Player)
    {
        if (source == CommandSource.Player)
            Gatherer.Pause();
        base.MoveTo(destination, source);
    }

    public override int Update()
    {
        Gatherer.Update();
        return base.Update();
    }
}

/// <summary>
/// Periodic income (oil derricks, the Black Market, hacked internet centers): DepositAmount every
/// DepositTiming, plus InitialCaptureBonus when captured and UpgradedBoost with the named upgrade.
/// </summary>
public sealed class AutoDepositUpdate : Module, IUpdateModule, ICreateModule
{
    int amount, interval, captureBonus, nextFrame;
    string boostUpgrade = "";
    int boostAmount;
    Players.Player? lastOwner;

    protected override void Initialize()
    {
        amount = IntSetting("DepositAmount");
        interval = Math.Max(1, FramesSetting("DepositTiming", 1000));
        captureBonus = IntSetting("InitialCaptureBonus");
        // "UpgradedBoost: UpgradeType:Upgrade_AmericaSupplyLines Boost:20"
        foreach (string word in RuleValue.Words(Setting("UpgradedBoost")))
        {
            if (word.StartsWith("UpgradeType:", StringComparison.OrdinalIgnoreCase)) boostUpgrade = word["UpgradeType:".Length..];
            if (word.StartsWith("Boost:", StringComparison.OrdinalIgnoreCase)) boostAmount = RuleValue.Int(word["Boost:".Length..]);
        }
    }

    public void OnCreate()
    {
        nextFrame = Frame + interval;
        lastOwner = Owner.Owner;
    }

    public int Update()
    {
        if (Owner.Owner != lastOwner)
        {
            lastOwner = Owner.Owner;
            if (!Owner.Owner.IsNeutral && captureBonus > 0)
                Owner.Owner.Money.Deposit(captureBonus);
        }
        if (Frame < nextFrame)
            return nextFrame - Frame - 1;
        nextFrame = Frame + interval;
        if (Owner.IsUnderConstruction || Owner.Owner.IsNeutral)
            return interval - 1;
        int value = amount + (boostUpgrade.Length > 0 && Owner.Owner.HasUpgrade(boostUpgrade) ? boostAmount : 0);
        Owner.Owner.Money.Deposit(value);
        Game.Events.MoneyEarned(Owner, value);
        return interval - 1;
    }
}

/// <summary>Read access to a module's settings from helper classes that aren't modules themselves.</summary>
public readonly struct ModuleSettings(Module module)
{
    public int Int(string key, int fallback = 0) => module.Definition.GetInt(key, fallback);
    public float Float(string key, float fallback = 0) => module.Definition.GetFloat(key, fallback);
    public string Text(string key, string fallback = "") => module.Definition.Get(key, fallback);
    public int Frames(string key, float fallbackMs = 0) => LogicTime.Frames(module.Definition.GetFloat(key, fallbackMs));
}

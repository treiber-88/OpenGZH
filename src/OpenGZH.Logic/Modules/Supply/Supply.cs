using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.AI;
using OpenGZH.Logic.Modules.Dock;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Supply;

// The economy: supply docks (warehouses) hold boxes; harvesters (trucks, Chinooks, workers) dock to
// load them one at a time and dock at a supply center to turn each into GameData ValuePerSupplyBox cash.

/// <summary>Marks a supply source; the work is done by its <see cref="SupplyWarehouseDockUpdate"/>.</summary>
public sealed class SupplyWarehouseCreate : Module { }

/// <summary>Marks a supply center (the computer player uses this to know where to deliver).</summary>
public sealed class SupplyCenterCreate : Module { }

/// <summary>
/// A pile of supply boxes (the original's SupplyWarehouseDockUpdate): StartingBoxes boxes, handed out
/// one per dock action to a harvester parked close enough. Small piles vanish when empty (DeleteWhenEmpty).
/// </summary>
public sealed class SupplyWarehouseDockUpdate : DockUpdate
{
    public int Boxes { get; private set; }
    public int StartingBoxes { get; private set; }
    bool deleteWhenEmpty;

    protected override void InitializeDock()
    {
        StartingBoxes = Boxes = IntSetting("StartingBoxes", 1);
        deleteWhenEmpty = BoolSetting("DeleteWhenEmpty");
    }

    /// <summary>For the supply pile's draw module: 1 = full, 0 = empty.</summary>
    public float Fullness => StartingBoxes > 0 ? Boxes / (float)StartingBoxes : 0;

    public override int ActionDelayFor(GameObject docker) => SupplyGatherer.Of(docker)?.WarehouseDelay ?? 0;

    public override bool Action(GameObject docker, GameObject? drone)
    {
        if (Boxes == 0)
            return false;
        // Not parked close enough yet: nudge into place and try again.
        float closeEnough = docker.Geometry.BoundingCircleRadius * 2;
        float gap = Math.Max(0, MathUtil.Distance2D(docker.Position, Owner.Position) - docker.Geometry.BoundingSphereRadius - Owner.Geometry.BoundingSphereRadius);
        if (gap * gap > closeEnough * closeEnough)
        {
            float range = 0.4f * Formats.Maps.MapFile.CellSize;
            docker.SetPosition(docker.Position + new Vector3(Game.Random.Int((int)-range, (int)range), Game.Random.Int((int)-range, (int)range), 0));
            return false;
        }
        --Boxes;
        if (SupplyGatherer.Of(docker) is { } truck && truck.GainOneBox(Boxes))
        {
            if (Boxes == 0 && deleteWhenEmpty)
            {
                Game.DestroyObject(Owner);
                return false;
            }
            return true;
        }
        ++Boxes;
        return false;
    }

    /// <summary>
    /// Really damaged (SupplyWarehouseCripplingBehavior): no boxes until repaired. A harvester inside
    /// is killed (unless flying); one about to dock is sent to look elsewhere.
    /// </summary>
    public bool Crippled
    {
        get => IsDockCrippled;
        set
        {
            if (value && Game.FindObject(ActiveDocker) is { } victim)
            {
                if (IsDockerInside)
                {
                    if (!victim.IsAboveTerrain)
                        victim.Kill();
                }
                else if (SupplyGatherer.Of(victim) is { } truck)
                {
                    victim.AI!.Stop(CommandSource.AI);
                    truck.ForceWanting = true;
                }
            }
            IsDockCrippled = value;
        }
    }

    /// <summary>Kept for scripts that set a box count directly.</summary>
    public void SetBoxes(int boxes) => Boxes = Math.Max(0, boxes);

    /// <summary>The original's setCashValue: enough boxes to be worth this much (rounded up).</summary>
    public void SetCashValue(int cashValue, GameLogic game) =>
        Boxes = (int)MathF.Ceiling(cashValue / (float)Math.Max(1, RuleValue.Int(game.Catalog.GameData.Get("ValuePerSupplyBox"), 75)));
}

/// <summary>
/// Where harvesters unload (the original's SupplyCenterDockUpdate): every box carried becomes cash
/// for the center's owner, plus the harvester's upgrade boost; GrantTemporaryStealth hides the
/// harvester for a while if the center itself is stealthed.
/// </summary>
public sealed class SupplyCenterDockUpdate : DockUpdate
{
    public override int ActionDelayFor(GameObject docker) => SupplyGatherer.Of(docker)?.CenterDelay ?? 0;

    public override bool Action(GameObject docker, GameObject? drone)
    {
        if (SupplyGatherer.Of(docker) is not { } truck)
            return false;
        var owner = Owner.Owner;
        int value = 0;
        while (truck.LoseOneBox())
            value += owner.SupplyBoxValue;
        value += truck.UpgradedSupplyBoost;
        if (value > 0)
        {
            owner.Money.Deposit(value);
            owner.Score.AddMoneyEarned(value);
            int stealthFrames = FramesSetting("GrantTemporaryStealth");
            if (stealthFrames > 0 && Owner.HasStatus(Status.Stealthed) && docker.FindModule<Stealth.StealthUpdate>() is { } stealth
                && (stealth.IsTemporaryGrant || !docker.HasStatus(Status.CanStealth)))
                stealth.ReceiveGrant(true, stealthFrames);
            // (The floating "+$" is hidden for a stealthed enemy center in the original; the client decides.)
            Game.Events.MoneyEarned(docker, value);
        }
        return false;
    }
}

/// <summary>
/// The harvesting brain shared by supply trucks, Chinooks and GLA workers (the original's
/// SupplyTruckAIUpdate state machine): Busy (doing as told), Idle, Wanting (pick the best supply
/// center if carrying boxes, else the best warehouse, and dock there), Regrouping (no dock found:
/// wait by the base) and Docking. Docking and the box hand-over are done by the docks.
/// </summary>
public sealed class SupplyGatherer
{
    public enum StateId { Busy, Idle, Wanting, Regrouping, Docking }

    const float RegroupSuccessDistanceSquared = 225;

    readonly AIUpdateInterface ai;
    readonly Func<bool> isAvailable;
    readonly Func<int> boost;
    readonly string depletedVoice;
    readonly float scanDistance;
    readonly Action? onIdleEntered;

    public int MaxBoxes { get; }
    public int WarehouseDelay { get; }
    public int CenterDelay { get; }
    public int Boxes { get; private set; }
    public StateId State { get; private set; } = StateId.Busy;
    public uint PreferredDockId { get; set; }

    /// <summary>Kept name for the computer player's code: the dock it assigned this harvester to.</summary>
    public uint PreferredCenterId { get => PreferredDockId; set => PreferredDockId = value; }

    /// <summary>Told to go gathering (a new truck from a supply center, the computer player, a crippled dock).</summary>
    public bool ForceWanting { get; set; }

    /// <summary>The player stopped it: stay put.</summary>
    public bool ForceBusy { get; set; }

    public bool IsAvailableForSupplying => isAvailable();
    public int UpgradedSupplyBoost => boost();
    public bool IsCurrentlyFerryingSupplies => State is StateId.Wanting or StateId.Docking;

    GameObject Owner => ai.Owner;
    GameLogic Game => ai.Owner.Game;

    /// <summary>The harvesting brain of an object, if it has one.</summary>
    public static SupplyGatherer? Of(GameObject obj) => obj.AI switch
    {
        SupplyTruckAIUpdate truck => truck.Gatherer,
        WorkerAIUpdate worker => worker.Gatherer,
        _ => null,
    };

    public SupplyGatherer(AIUpdateInterface ai, ModuleSettings settings, Func<int> boost, Func<bool>? isAvailable = null, Action? onIdleEntered = null)
    {
        this.ai = ai;
        this.boost = boost;
        this.isAvailable = isAvailable ?? (() => true);
        this.onIdleEntered = onIdleEntered;
        MaxBoxes = settings.Int("MaxBoxes", 1);
        WarehouseDelay = settings.Frames("SupplyWarehouseActionDelay", 0);
        CenterDelay = settings.Frames("SupplyCenterActionDelay", 0);
        scanDistance = settings.Float("SupplyWarehouseScanDistance", 0);
        depletedVoice = settings.Text("SuppliesDepletedVoice");
    }

    /// <summary>The computer player looks twice as far.</summary>
    public float WarehouseScanDistance => Owner.Owner.IsComputer ? 2 * scanDistance : scanDistance;

    public bool LoseOneBox()
    {
        if (Boxes == 0)
            return false;
        --Boxes;
        Owner.SetCondition(Condition.Carrying, Boxes > 0);
        return true;
    }

    /// <summary>Takes a box; the last box of a pile (with no other pile nearby) makes the driver say so.</summary>
    public bool GainOneBox(int remainingStock)
    {
        if (Boxes >= MaxBoxes)
            return false;
        ++Boxes;
        if (remainingStock == 0)
        {
            var best = FindBestSupplyWarehouse();
            bool playDepleted = best == null || Vector3.Distance(Owner.Position, best.Position) > WarehouseScanDistance / 4;
            if (playDepleted && depletedVoice.Length > 0)
                Game.Events.PlaySound(depletedVoice, Owner.Position, Owner.Owner, Owner);
        }
        Owner.SetCondition(Condition.Carrying, Boxes > 0);
        return true;
    }

    /// <summary>Back to the starting state (Busy).</summary>
    public void Reset() => Enter(StateId.Busy);

    // ---- Finding docks (the original's ResourceGatheringManager) ----

    /// <summary>The original's ActionManager::canTransferSuppliesAt.</summary>
    public bool CanTransferSuppliesAt(GameObject dest)
    {
        if (dest.IsEffectivelyDead || Owner.IsUnderConstruction || dest.IsUnderConstruction || dest.HasStatus(Status.Sold))
            return false;
        var warehouse = dest.FindModule<SupplyWarehouseDockUpdate>();
        if (warehouse != null && (warehouse.Boxes == 0 || dest.RelationshipTo(Owner) == Players.Relationship.Enemies))
            return false;
        var center = dest.FindModule<SupplyCenterDockUpdate>();
        if (center != null && (Boxes == 0 || dest.Owner != Owner.Owner))
            return false;
        if (warehouse == null && center == null)
            return false;
        if (!IsAvailableForSupplying)
            return false;
        // A human player can't use what is still hidden in the shroud (fog is fine).
        if (Owner.Owner.IsHuman && Game.Shroud.StatusOf(dest, Owner.Owner) == Vision.ObjectShroud.Shrouded)
            return false;
        return true;
    }

    float RelativeCost(GameObject dest, out float distanceSquared)
    {
        distanceSquared = float.MaxValue;
        if (!CanTransferSuppliesAt(dest) || dest.FindModule<DockUpdate>() is not { } dock || !dock.IsClearToApproach(Owner))
            return float.MaxValue;
        distanceSquared = Vector3.DistanceSquared(Owner.Position, dest.Position);
        return distanceSquared;
    }

    public GameObject? FindBestSupplyWarehouse()
    {
        if (Game.FindObject(PreferredDockId) is { } preferred && preferred.FindModule<SupplyWarehouseDockUpdate>() != null
            && RelativeCost(preferred, out _) != float.MaxValue)
            return preferred;
        float maxDistanceSquared = WarehouseScanDistance * WarehouseScanDistance;
        GameObject? best = null;
        float bestCost = float.MaxValue;
        foreach (var warehouse in Game.Objects.Where(o => o.FindModule<SupplyWarehouseCreate>() != null || o.FindModule<SupplyWarehouseDockUpdate>() != null).ToList())
        {
            float cost = RelativeCost(warehouse, out float distanceSquared);
            if (cost < bestCost && distanceSquared < maxDistanceSquared)
            {
                best = warehouse;
                bestCost = cost;
            }
        }
        return best;
    }

    public GameObject? FindBestSupplyCenter()
    {
        if (Game.FindObject(PreferredDockId) is { } preferred && preferred.FindModule<SupplyCenterDockUpdate>() != null
            && RelativeCost(preferred, out _) != float.MaxValue)
            return preferred;
        GameObject? best = null;
        float bestCost = float.MaxValue;
        foreach (var center in Game.ObjectsOwnedBy(Owner.Owner).Where(o => o.FindModule<SupplyCenterDockUpdate>() != null).ToList())
        {
            float cost = RelativeCost(center, out _);
            if (cost < bestCost)
            {
                best = center;
                bestCost = cost;
            }
        }
        return best;
    }

    // ---- The state machine ----

    public void Update()
    {
        // Transitions out of the current state are checked first, in the original's order.
        var next = CheckConditions();
        if (next != null)
            Enter(next.Value);
        switch (State)
        {
            case StateId.Wanting:
                // Success hands over to Busy (and Docking right after); failure goes regrouping.
                Enter(UpdateWanting() ? StateId.Busy : StateId.Regrouping);
                break;
            case StateId.Regrouping:
                if (ai.IsIdle)
                    Enter(StateId.Wanting);
                break;
        }
    }

    StateId? CheckConditions()
    {
        bool idle = ai.IsIdle, docking = ai.IsDocking;
        bool notDockingOrIdle = !idle && !docking;
        return State switch
        {
            StateId.Busy => idle ? StateId.Idle : docking ? StateId.Docking : null,
            StateId.Idle => ForceBusy ? StateId.Busy : ForceWanting ? StateId.Wanting : docking ? StateId.Docking : notDockingOrIdle ? StateId.Busy : null,
            StateId.Wanting => docking ? StateId.Docking : notDockingOrIdle ? StateId.Busy : null,
            StateId.Regrouping => ai.LastCommandSource == CommandSource.Player ? StateId.Busy : null,
            StateId.Docking => ForceBusy ? StateId.Busy : IsAvailableForSupplying && idle ? StateId.Wanting : notDockingOrIdle ? StateId.Busy : null,
            _ => null,
        };
    }

    void Enter(StateId state)
    {
        State = state;
        switch (state)
        {
            case StateId.Busy:
                ForceBusy = false;
                break;
            case StateId.Idle:
                onIdleEntered?.Invoke();
                break;
            case StateId.Wanting:
            case StateId.Docking:
                ForceWanting = false;
                break;
            case StateId.Regrouping:
                if (!EnterRegrouping())
                    State = StateId.Busy;
                break;
        }
    }

    bool UpdateWanting()
    {
        if (!IsAvailableForSupplying)
            return false;
        var dock = Boxes > 0 ? FindBestSupplyCenter() : FindBestSupplyWarehouse();
        if (dock == null)
            return false;
        ai.Dock(dock, CommandSource.AI);
        return true;
    }

    /// <summary>Nothing to do: wait by a cash generator, else the command center, else any building.</summary>
    bool EnterRegrouping()
    {
        ai.IgnoreObstacle(null);
        GameObject? Closest(string kind) => Game.ObjectsOwnedBy(Owner.Owner)
            .Where(o => o.IsKindOf(kind) && !o.IsEffectivelyDead)
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, Owner.Position)).FirstOrDefault();
        var destination = Closest("CASH_GENERATOR") ?? Closest("COMMANDCENTER") ?? Closest("STRUCTURE");
        if (destination == null)
            return false;
        float gap = MathUtil.Distance2D(Owner.Position, destination.Position) - Owner.Geometry.BoundingSphereRadius - destination.Geometry.BoundingSphereRadius;
        if (Math.Max(0, gap) * Math.Max(0, gap) < RegroupSuccessDistanceSquared)
            return true;
        if (Spatial.PositionFinder.FindPositionAround(Game, destination.Position, 0, 100) is not { } spot)
            return false;
        ai.MoveTo(spot, CommandSource.AI);
        return true;
    }
}

/// <summary>Supply truck (China) and supply-truck-like units (the Chinook builds on it).</summary>
public class SupplyTruckAIUpdate : AIUpdateInterface
{
    public SupplyGatherer Gatherer { get; private set; } = null!;

    /// <summary>Extra money per delivery (the Chinook's UpgradedSupplyBoost with Supply Lines).</summary>
    protected virtual int UpgradedSupplyBoost => 0;

    /// <summary>Free to fetch supplies now (a Chinook carrying troops isn't).</summary>
    protected virtual bool IsAvailableForSupplying => true;

    protected override void Initialize()
    {
        base.Initialize();
        Gatherer = new SupplyGatherer(this, new ModuleSettings(this), () => UpgradedSupplyBoost, () => IsAvailableForSupplying);
    }

    /// <summary>A player's stop keeps it put; a player's dock order makes that dock its favourite.</summary>
    protected override bool OnOrder(AIOrder order, Action replay)
    {
        if (order.Source == CommandSource.Player)
        {
            if (order.Type == AIOrderType.Stop)
                Gatherer.ForceBusy = true;
            else if (order.Type == AIOrderType.Dock && order.TargetId != 0)
                Gatherer.PreferredDockId = order.TargetId;
        }
        return false;
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

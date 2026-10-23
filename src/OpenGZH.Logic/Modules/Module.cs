using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules;

/// <summary>
/// Base class of every object module. An object is little more than a list of modules; all of
/// its behaviour comes from them, configured by the "Modules:" entries of its rules definition.
///
/// Each module class is named exactly like the original game's module ("ActiveBody",
/// "AIUpdateInterface", "SlowDeathBehavior", ...), so the rules use the same class names.
/// A module opts into events by implementing the interfaces below.
/// </summary>
public abstract class Module
{
    public GameObject Owner { get; private set; } = null!;
    public ModuleDefinition Definition { get; private set; } = null!;

    protected GameLogic Game => Owner.Game;
    protected int Frame => Owner.Game.Frame;

    /// <summary>Called by the module registry right after construction.</summary>
    internal void Attach(GameObject owner, ModuleDefinition definition)
    {
        Owner = owner;
        Definition = definition;
        Initialize();
    }

    /// <summary>Read settings from <see cref="Definition"/>. Other modules may not exist yet.</summary>
    protected virtual void Initialize() { }

    // Convenience accessors for this module's rule settings.
    protected string Setting(string key, string fallback = "") => Definition.Get(key, fallback);
    protected float FloatSetting(string key, float fallback = 0) => Definition.GetFloat(key, fallback);
    protected int IntSetting(string key, int fallback = 0) => Definition.GetInt(key, fallback);
    protected bool BoolSetting(string key, bool fallback = false) => Definition.GetBool(key, fallback);
    protected int FramesSetting(string key, float fallbackMs = 0) => LogicTime.Frames(FloatSetting(key, fallbackMs));
    protected IEnumerable<string> ListSetting(string key) =>
        Definition.Settings.TryGet(key, out var node) ? node.AsList().Select(n => n.Value ?? "") : [];

    public override string ToString() => $"{Definition.ClassName}({Definition.Tag}) on {Owner}";
}

/// <summary>
/// When in a frame an update runs, for all objects together (the original's update phases):
/// AIs steer first, then physics moves everything, then the normal updates, then bookkeeping.
/// </summary>
public enum UpdatePhase { Initial = 0, Physics = 1, Normal = 2, Final = 3 }

/// <summary>Runs every frame, or less often: return how many frames to sleep (0 = run again next frame).</summary>
public interface IUpdateModule
{
    int Update();

    UpdatePhase Phase => UpdatePhase.Normal;

    /// <summary>
    /// While the object is disabled, the update only runs if one of these disable types applies
    /// (e.g. AIs keep running while DISABLED_HELD in a transport). Default: none, so EMP, hacking
    /// and the like freeze the module.
    /// </summary>
    FlagSet DisabledTypesToProcess => default;
}

/// <summary>Disable-type masks for <see cref="IUpdateModule.DisabledTypesToProcess"/>.</summary>
public static class DisabledMask
{
    public static readonly FlagSet All = Flags.Disabled.Parse(string.Join(' ', Enumerable.Range(0, Flags.Disabled.Count).Select(i => Flags.Disabled[i])));
    public static readonly FlagSet Held = Flags.Disabled.Parse("DISABLED_HELD");
    public static readonly FlagSet Underpowered = Flags.Disabled.Parse("DISABLED_UNDERPOWERED");
    public static readonly FlagSet SuperweaponStates = Flags.Disabled.Parse("DISABLED_SUBDUED DISABLED_UNDERPOWERED DISABLED_EMP DISABLED_HACKED");
}

/// <summary>Notified once all modules exist (object creation) and when construction finishes.</summary>
public interface ICreateModule
{
    void OnCreate();
    void OnBuildComplete() { }
}

/// <summary>Notified when the object takes damage or heals, and when its damage state changes.</summary>
public interface IDamageModule
{
    void OnDamage(DamageInfo damage) { }
    void OnHealing(DamageInfo healing) { }
    void OnBodyDamageStateChange(BodyDamageState oldState, BodyDamageState newState) { }
}

/// <summary>Notified when the object dies.</summary>
public interface IDieModule
{
    void OnDie(DamageInfo killingBlow);
}

/// <summary>Notified when the object changes owner (captured, defected) and when it is removed from the game.</summary>
public interface IOwnershipModule
{
    void OnCapture(Players.Player oldOwner, Players.Player newOwner) { }
    void OnDelete() { }
}

/// <summary>Dropped minefield layers (GenerateMinefieldBehavior) told where to lay their mines.</summary>
public interface IMinefieldTarget
{
    void SetMinefieldTarget(Vector3 position);
}

/// <summary>An airfield's parking spots (projectiles aimed at a parked plane don't hit the airfield).</summary>
public interface IParkingPlace
{
    bool HasReservedSpace(uint objectId);
}

/// <summary>Notified when the object becomes disabled (EMP, hacked, unpowered...) or stops being disabled.</summary>
public interface IDisabledEdgeModule
{
    void OnDisabledEdge(bool nowDisabled);
}

/// <summary>Notified when the object touches another object (or the ground, when other is null).</summary>
public interface ICollideModule
{
    void OnCollide(GameObject? other, Vector3 location, Vector3 normal);
}

/// <summary>
/// Shared behaviour of all die modules (the original "DieMux"): a death only triggers the module if
/// its DeathTypes, VeterancyLevels, ExemptStatus and RequiredStatus settings allow it.
/// </summary>
public abstract class DieModule : Module, IDieModule
{
    FlagSet deathTypes;
    FlagSet veterancyLevels;
    FlagSet exemptStatus;
    FlagSet requiredStatus;

    protected override void Initialize()
    {
        deathTypes = Definition.Settings.Has("DeathTypes") ? Flags.DeathType.Parse(Setting("DeathTypes")) : Flags.DeathType.Parse("ALL");
        veterancyLevels = Definition.Settings.Has("VeterancyLevels") ? Flags.Veterancy.Parse(Setting("VeterancyLevels")) : Flags.Veterancy.Parse("ALL");
        exemptStatus = Flags.ObjectStatus.Parse(Setting("ExemptStatus"));
        requiredStatus = Flags.ObjectStatus.Parse(Setting("RequiredStatus"));
        InitializeDie();
    }

    protected virtual void InitializeDie() { }

    public bool IsApplicable(DamageInfo killingBlow)
    {
        if (!deathTypes.Has(Flags.DeathType.IndexOf(killingBlow.DeathType)))
            return false;
        if (!veterancyLevels.Has((int)Owner.Veterancy))
            return false;
        if (!exemptStatus.IsEmpty && Owner.StatusFlags.HasAny(exemptStatus))
            return false;
        if (!requiredStatus.IsEmpty && !Owner.StatusFlags.HasAll(requiredStatus))
            return false;
        return true;
    }

    public void OnDie(DamageInfo killingBlow)
    {
        if (IsApplicable(killingBlow))
            Die(killingBlow);
    }

    protected abstract void Die(DamageInfo killingBlow);
}

/// <summary>
/// Shared behaviour of all upgrade modules (the original "UpgradeMux"): the module activates once the
/// owner (or its player) has the upgrades in TriggeredBy, unless one in ConflictsWith is present.
/// RequiresAllTriggers = Yes needs every trigger; otherwise any one is enough.
/// </summary>
public abstract class UpgradeModule : Module
{
    public IReadOnlyList<string> TriggeredBy { get; private set; } = [];
    public IReadOnlyList<string> ConflictsWith { get; private set; } = [];
    public IReadOnlyList<string> RemovesUpgrades { get; private set; } = [];
    public bool RequiresAllTriggers { get; private set; }
    public bool IsActive { get; private set; }
    string fxListOnUpgrade = "";

    protected override void Initialize()
    {
        TriggeredBy = Words("TriggeredBy");
        ConflictsWith = Words("ConflictsWith");
        RemovesUpgrades = Words("RemovesUpgrades");
        RequiresAllTriggers = BoolSetting("RequiresAllTriggers");
        fxListOnUpgrade = Setting("FXListUpgrade");
        InitializeUpgrade();
    }

    protected virtual void InitializeUpgrade() { }

    List<string> Words(string key) => ListSetting(key).SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();

    /// <summary>Checks the owner's upgrades and activates the module if the conditions are now met.</summary>
    public void CheckUpgrades()
    {
        if (IsActive || TriggeredBy.Count == 0)
            return;

        bool Has(string upgrade) => Owner.HasUpgrade(upgrade);
        bool triggered = RequiresAllTriggers ? TriggeredBy.All(Has) : TriggeredBy.Any(Has);
        if (!triggered || ConflictsWith.Any(Has))
            return;

        IsActive = true;
        foreach (string removed in RemovesUpgrades)
            Owner.RemoveUpgrade(removed);
        if (fxListOnUpgrade.Length > 0)
            Game.Events.PlayFx(fxListOnUpgrade, Owner.Position, Owner);
        OnUpgrade();
    }

    /// <summary>Activates the module without any upgrade (the original's giveSelfUpgrade, e.g. StartsActive).</summary>
    protected void ActivateWithoutUpgrade()
    {
        if (IsActive)
            return;
        IsActive = true;
        OnUpgrade();
    }

    /// <summary>The upgrade happened: apply the effect.</summary>
    protected abstract void OnUpgrade();

    /// <summary>Some upgrades can be undone (e.g. when a conflicting upgrade arrives).</summary>
    protected void Deactivate() => IsActive = false;

    /// <summary>
    /// Upgrades that give the player something (power, radar, cheaper units) take it back when the
    /// object is removed, and move it to the new owner when the object is captured.
    /// </summary>
    protected virtual void AddToPlayer(Player player) { }
    protected virtual void RemoveFromPlayer(Player player) { }

    internal void OnRemoved()
    {
        if (IsActive)
            RemoveFromPlayer(Owner.Owner);
    }

    internal void OnOwnerChanged(Player oldOwner)
    {
        if (!IsActive)
            return;
        RemoveFromPlayer(oldOwner);
        AddToPlayer(Owner.Owner);
    }
}

/// <summary>The object's health. Exactly one per object.</summary>
public enum BodyDamageState { Pristine = 0, Damaged = 1, ReallyDamaged = 2, Rubble = 3 }

/// <summary>A train that a script can hold in its station (the original's RailroadBehavior::setHeld).</summary>
public interface IRailroadBehavior
{
    void SetHeld(bool held);
}

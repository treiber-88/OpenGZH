using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules.SpecialPowers;

/// <summary>
/// An update module that carries out a special power over time (a unit walking up to a building to
/// capture it, a Spectre Gunship circling): SpecialAbilityUpdate and friends. The special power
/// module hands the order to it; with UpdateModuleStartsAttack the update module also decides when
/// the power counts as used (it calls <see cref="SpecialPowerModule.TriggerSpecialPower"/>).
/// </summary>
public interface ISpecialPowerUpdate
{
    bool IsPowerCurrentlyInUse => false;
    bool PassesScienceTest() => true;
    bool InitiateIntentToDoSpecialPower(SpecialPowerTemplate power, GameObject? target, Vector3? position, uint options);
}

/// <summary>
/// Base of every special power module (the original "SpecialPowerModule"): knows which power it
/// gives its object, keeps the recharge countdown (its own, or the player's shared one for generals'
/// powers), and can be paused (e.g. until an upgrade unpauses it). Subclasses override the Do...
/// methods to make the power do something.
///
/// Countdown rules follow the original exactly, including its quirks: buildings start charging when
/// built (SpecialPowerCreate), other objects as soon as they exist.
/// </summary>
public class SpecialPowerModule : Module, ICreateModule
{
    public SpecialPowerTemplate? Power { get; private set; }
    public bool UpdateModuleStartsAttack { get; private set; }
    public bool ScriptOnly { get; private set; }

    int availableOnFrame;
    int pausedCount;
    int pausedOnFrame;
    float pausedPercent;

    protected override void Initialize()
    {
        string name = Setting("SpecialPowerTemplate");
        Power = Game.Catalog.SpecialPower(name);
        if (Power == null)
            Log.Warning($"{Owner.Template.Name}: unknown special power '{name}'.");
        UpdateModuleStartsAttack = BoolSetting("UpdateModuleStartsAttack");
        ScriptOnly = BoolSetting("ScriptedSpecialPowerOnly");
        InitializePower();
    }

    protected virtual void InitializePower() { }

    public virtual void OnCreate()
    {
        if (Power == null)
            return;
        if (!Owner.IsUnderConstruction && !Power.SharedSyncedTimer)
            StartPowerRecharge();
        if (BoolSetting("StartsPaused"))
            PauseCountdown(true);
    }

    /// <summary>The object that has the power was finished (SpecialPowerCreate): start charging.</summary>
    public virtual void OnSpecialPowerCreation()
    {
        if (Power == null)
            return;
        StartPowerRecharge();
        if (Power.SharedSyncedTimer)
        {
            Owner.Owner.ExpressSpecialPowerReadyFrame(Power, Frame);
            availableOnFrame = Owner.Owner.GetOrStartSpecialPowerReadyFrame(Power);
        }
        if (BoolSetting("StartsPaused"))
            PauseCountdown(true);
    }

    public bool IsModuleFor(SpecialPowerTemplate power) => Power == power;

    // ---- Countdown ----

    public bool IsReady
    {
        get
        {
            if (Power == null)
                return false;
            if (Power.SharedSyncedTimer)
                return Frame >= Owner.Owner.GetOrStartSpecialPowerReadyFrame(Power);
            return pausedCount == 0 && Frame >= availableOnFrame;
        }
    }

    /// <summary>0..1, for the control bar's clock and the superweapon timers.</summary>
    public float PercentReady
    {
        get
        {
            if (pausedCount > 0 && pausedPercent == 1f)
                return 0.99999f;
            if (IsReady)
                return 1f;
            if (pausedCount > 0)
                return pausedPercent;
            if (Power == null)
                return 0f;
            int readyFrame = Power.SharedSyncedTimer ? Owner.Owner.GetOrStartSpecialPowerReadyFrame(Power) : availableOnFrame;
            return 1f - (readyFrame - Frame) / (float)Math.Max(1, Power.ReloadFrames);
        }
    }

    public int ReadyFrame
    {
        get
        {
            if (Power is { SharedSyncedTimer: true })
                return Owner.Owner.GetOrStartSpecialPowerReadyFrame(Power);
            if (pausedCount > 0 || Owner.IsDisabled)
                return availableOnFrame + (Frame - pausedOnFrame);
            return availableOnFrame;
        }
    }

    public void SetReadyFrame(int frame)
    {
        availableOnFrame = frame;
        pausedOnFrame = Frame;
    }

    public void StartPowerRecharge()
    {
        if (Power == null)
            return;
        if (Power.SharedSyncedTimer)
            Owner.Owner.ResetOrStartSpecialPowerReadyFrame(Power);
        else
            availableOnFrame = Frame + Power.ReloadFrames;
    }

    /// <summary>Pauses stack: two pauses need two unpauses. Time spent paused is added to the countdown.</summary>
    public void PauseCountdown(bool pause)
    {
        if (pause)
        {
            if (pausedCount == 0)
            {
                pausedOnFrame = Frame;
                pausedPercent = PercentReady;
            }
            pausedCount++;
        }
        else if (pausedCount > 0 && --pausedCount == 0)
        {
            availableOnFrame += Frame - pausedOnFrame;
        }
    }

    public bool IsPaused => pausedCount > 0;

    // ---- Using the power ----

    /// <summary>Hands the order to the object's special power update modules (e.g. SpecialAbilityUpdate).</summary>
    protected bool InitiateIntentToDoSpecialPower(GameObject? target, Vector3? position, uint options)
    {
        if (Power == null)
            return false;
        foreach (var update in Owner.FindModules<ISpecialPowerUpdate>().ToList())
            if (update.PassesScienceTest() && update.InitiateIntentToDoSpecialPower(Power, target, position, options))
                return true;
        return false;
    }

    /// <summary>The power has been used: notify scripts, play sounds, reveal the target area, start recharging.</summary>
    public void TriggerSpecialPower(Vector3? location)
    {
        AboutToDoSpecialPower(location);
        CreateViewObject(location);
        StartPowerRecharge();
    }

    void AboutToDoSpecialPower(Vector3? location)
    {
        if (Power == null)
            return;
        Game.Scripts?.NotifyOfTriggeredSpecialPower(Owner.Owner.Index, Power.Name, Owner.Id);
        AnnounceSuperweapon();
        Game.Events.PlaySound(Power.InitiateSound, Owner.Position);
        if (location is { } at)
            Game.Events.PlaySound(Power.InitiateAtLocationSound, at, null);
    }

    /// <summary>EVA warns everyone when a superweapon launches ("Enemy nuclear missile launched").</summary>
    void AnnounceSuperweapon()
    {
        string? kind = Power!.Type switch
        {
            "SPECIAL_PARTICLE_UPLINK_CANNON" or "SUPW_SPECIAL_PARTICLE_UPLINK_CANNON" or "LAZR_SPECIAL_PARTICLE_UPLINK_CANNON" => "ParticleCannon",
            "SPECIAL_NEUTRON_MISSILE" or "NUKE_SPECIAL_NEUTRON_MISSILE" or "SUPW_SPECIAL_NEUTRON_MISSILE" => "Nuke",
            "SPECIAL_SCUD_STORM" => "ScudStorm",
            "SPECIAL_GPS_SCRAMBLER" or "SLTH_SPECIAL_GPS_SCRAMBLER" => "GPS_Scrambler",
            "SPECIAL_SNEAK_ATTACK" => "Sneak_Attack",
            _ => null,
        };
        if (kind == null)
            return;
        foreach (var player in Game.Players.All.Where(p => !p.IsNeutral))
        {
            string whose = player == Owner.Owner ? "Own" : player.IsEnemyOf(Owner.Owner) ? "Enemy" : "Ally";
            Game.Events.Eva($"SuperweaponLaunched_{whose}_{kind}", player);
        }
    }

    /// <summary>A short-lived invisible object that lets the player watch the target area.</summary>
    void CreateViewObject(Vector3? location)
    {
        if (Power == null || location == null || Power.ViewObjectRange <= 0 || Power.ViewObjectFrames <= 0)
            return;
        string name = Game.Catalog.GameData.Get("SpecialPowerViewObject");
        if (name.Length == 0 || Game.CreateObject(name, Owner.Owner, location.Value) is not { } view)
            return;
        view.ShroudClearingRange = Power.ViewObjectRange;
        view.FindModule<DeletionUpdate>()?.SetLifetime(Power.ViewObjectFrames);
    }

    /// <summary>A power with no target (e.g. Emergency Repair at a building, a Baikonur launch).</summary>
    public virtual void DoSpecialPower(uint options)
    {
        if (pausedCount > 0 || Owner.IsDisabled)
            return;
        InitiateIntentToDoSpecialPower(null, null, options);
        if (!UpdateModuleStartsAttack)
            TriggerSpecialPower(null);
    }

    public virtual void DoSpecialPowerAtObject(GameObject target, uint options)
    {
        if (pausedCount > 0 || Owner.IsDisabled)
            return;
        InitiateIntentToDoSpecialPower(target, null, options);
        if (!UpdateModuleStartsAttack)
            TriggerSpecialPower(target.Position);
    }

    public virtual void DoSpecialPowerAtLocation(Vector3 location, float angle, uint options)
    {
        if (pausedCount > 0 || Owner.IsDisabled)
            return;
        InitiateIntentToDoSpecialPower(null, location, options);
        if (!UpdateModuleStartsAttack)
            TriggerSpecialPower(location);
    }

    public virtual void DoSpecialPowerUsingWaypoints(IReadOnlyList<Vector3> waypoints, uint options)
    {
        if (pausedCount > 0 || Owner.IsDisabled)
            return;
        InitiateIntentToDoSpecialPower(null, null, options);
        if (!UpdateModuleStartsAttack)
            TriggerSpecialPower(null);
    }

    /// <summary>The power's player-wide effect moves with the object when it is captured (e.g. Cash Bounty).</summary>
    public virtual void OnOwnerChanged(Player oldOwner) { }
}

/// <summary>Starts the object's special powers charging once it is built.</summary>
public sealed class SpecialPowerCreate : Module, ICreateModule
{
    public void OnCreate() { }

    public void OnBuildComplete()
    {
        foreach (var power in Owner.FindModules<SpecialPowerModule>().ToList())
            power.OnSpecialPowerCreation();
    }
}

/// <summary>Tells mission scripts that a special power has finished when the object it created dies (e.g. the airstrike plane).</summary>
public sealed class SpecialPowerCompletionDie : DieModule
{
    uint creatorId;
    bool creatorSet;

    public void SetCreator(uint id)
    {
        if (creatorSet)
            return;
        creatorSet = true;
        creatorId = id;
    }

    protected override void Die(DamageInfo killingBlow)
    {
        if (creatorId != 0 && Game.Catalog.SpecialPower(Setting("SpecialPowerTemplate")) is { } power)
            Game.Scripts?.NotifyOfCompletedSpecialPower(Owner.Owner.Index, power.Name, creatorId);
    }
}

/// <summary>Unpauses a power that StartsPaused once an upgrade is done (e.g. a building's power after research).</summary>
public sealed class UnpauseSpecialPowerUpgrade : UpgradeModule
{
    protected override void OnUpgrade()
    {
        var power = Game.Catalog.SpecialPower(Setting("SpecialPowerTemplate"));
        foreach (var module in Owner.FindModules<SpecialPowerModule>())
            if (module.Power == power)
                module.PauseCountdown(false);
    }
}

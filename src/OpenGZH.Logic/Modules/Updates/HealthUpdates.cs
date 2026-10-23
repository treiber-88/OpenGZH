using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// Healing over time. Three ways it works, chosen by the settings:
/// - Radius 0: heals only its owner, HealingAmount every HealingDelay (veteran self-repair, Emergency Repair).
/// - Radius > 0: heals friendly KindOf objects around it (Ambulance, Propaganda Tower, Barracks);
///   SingleBurst heals once and stops. Each target accepts one such healer at a time.
/// - AffectsWholePlayer: heals all of the player's KindOf objects, wherever they are.
/// Starts working at once (StartsActive) or when an upgrade arrives (TriggeredBy).
/// StartHealingDelay (self-healing only) waits that long after the last damage before healing again.
/// </summary>
public sealed class AutoHealBehavior : UpgradeModule, IUpdateModule, ICreateModule, IDamageModule
{
    bool startsActive, singleBurst, affectsWholePlayer, skipSelf;
    float healingAmount, radius;
    int healingDelay, startHealingDelay;
    string kindOf = "", forbiddenKindOf = "";
    string radiusParticles = "", pulseParticles = "";

    int soonestHealFrame;
    bool stopped;

    protected override void InitializeUpgrade()
    {
        startsActive = BoolSetting("StartsActive");
        singleBurst = BoolSetting("SingleBurst");
        healingAmount = IntSetting("HealingAmount");
        healingDelay = Definition.Settings.Has("HealingDelay") ? FramesSetting("HealingDelay") : LogicTime.SleepForever;
        startHealingDelay = FramesSetting("StartHealingDelay");
        radius = FloatSetting("Radius");
        affectsWholePlayer = BoolSetting("AffectsWholePlayer");
        skipSelf = BoolSetting("SkipSelfForHealing");
        kindOf = Setting("KindOf");
        forbiddenKindOf = Setting("ForbiddenKindOf");
        radiusParticles = Setting("RadiusParticleSystemName");
        pulseParticles = Setting("UnitHealPulseParticleSystemName");
    }

    public void OnCreate()
    {
        if (radiusParticles.Length > 0)
            Game.Events.ParticleSystem(radiusParticles, Owner.Position, Owner);
        if (startsActive)
        {
            ActivateWithoutUpgrade();
            // A random first pulse, so a group of healers doesn't pulse in step.
            Owner.SleepUpdate(this, Game.Random.Int(1, Math.Max(1, Math.Min(healingDelay, 100000))));
        }
        else
            Owner.SleepUpdate(this);
    }

    protected override void OnUpgrade() => Owner.WakeUpdate(this);

    /// <summary>A minefield that has been cleared stops repairing itself for good.</summary>
    public void StopHealing()
    {
        stopped = true;
        soonestHealFrame = int.MaxValue;
        Owner.SleepUpdate(this);
    }

    /// <summary>The upgrade is lost (a sniped vehicle loses its veteran self-repair until it earns it back).</summary>
    public void UndoUpgrade()
    {
        soonestHealFrame = 0;
        Deactivate();
    }

    public void OnDamage(DamageInfo damage)
    {
        if (stopped || !IsActive || radius != 0)
            return;
        if (startHealingDelay > 0)
            Owner.SleepUpdate(this, startHealingDelay);
        else if (Frame > soonestHealFrame)
            Owner.WakeUpdate(this);
    }

    public int Update()
    {
        if (stopped || !IsActive || Owner.IsEffectivelyDead)
            return LogicTime.SleepForever;

        if (affectsWholePlayer)
        {
            foreach (var obj in Game.ObjectsOwnedBy(Owner.Owner).ToList())
                if (!obj.IsEffectivelyDead && !(skipSelf && obj == Owner) && Wants(obj))
                    PulseHeal(obj);
            return Sleep(healingDelay);
        }

        if (radius == 0)
        {
            if (Owner.Health >= Owner.MaxHealth)
                return LogicTime.SleepForever;
            PulseHeal(Owner);
            return Sleep(healingDelay);
        }

        foreach (var obj in Game.Partition.Query(Owner.Position, radius).ToList())
        {
            if (obj.IsEffectivelyDead || (skipSelf && obj == Owner) || !Wants(obj))
                continue;
            if (obj.Owner != Owner.Owner && !Owner.Owner.IsAllyOf(obj.Owner))
                continue;
            if (MathUtil.Distance2D(obj.Position, Owner.Position) > radius)
                continue;
            PulseHeal(obj);
            if (singleBurst)
                Game.Events.HealedIcon(obj);
        }
        return singleBurst ? LogicTime.SleepForever : Sleep(healingDelay);
    }

    /// <summary>The original's UPDATE_SLEEP(n): run again in n frames.</summary>
    static int Sleep(int frames) => frames >= LogicTime.SleepForever ? LogicTime.SleepForever : Math.Max(0, frames - 1);

    bool Wants(GameObject obj) =>
        obj.Health < obj.MaxHealth
        && (kindOf.Length == 0 || obj.IsAnyKindOf(kindOf))
        && (forbiddenKindOf.Length == 0 || !obj.IsAnyKindOf(forbiddenKindOf));

    void PulseHeal(GameObject obj)
    {
        if (stopped)
            return;
        if (radius == 0)
            obj.Body?.AttemptHealing(healingAmount, Owner.Id);
        else
            obj.AttemptHealingFromSoleBenefactor(healingAmount, Owner, healingDelay);
        if (pulseParticles.Length > 0)
            Game.Events.ParticleSystem(pulseParticles, obj.Position);
        soonestHealFrame = Frame + healingDelay;
    }
}

/// <summary>
/// Buildings repair themselves slowly once they haven't been hurt for a while
/// (GameData's BaseRegenHealthPercentPerSecond and BaseRegenDelay).
/// </summary>
public sealed class BaseRegenerateUpdate : Module, IUpdateModule, ICreateModule, IDamageModule
{
    const int HealRate = 3;

    public void OnCreate()
    {
        if (Game.Catalog.GameData.BaseRegenHealthPercentPerSecond == 0)
            Owner.SleepUpdate(this);
    }

    public void OnDamage(DamageInfo damage)
    {
        if (Game.Catalog.GameData.BaseRegenHealthPercentPerSecond > 0 && damage.DamageType != "HEALING")
            Owner.SleepUpdate(this, Game.Catalog.GameData.BaseRegenDelayFrames);
        else
            Owner.SleepUpdate(this);
    }

    public int Update()
    {
        if (Owner.HasStatus(Status.UnderConstruction))
            return 0;
        if (Owner.HasStatus(Status.Sold) || Owner.Body is not { } body || body.Health >= body.MaxHealth)
            return LogicTime.SleepForever;
        float amount = HealRate * body.MaxHealth * Game.Catalog.GameData.BaseRegenHealthPercentPerSecond * LogicTime.SecondsPerFrame;
        body.AttemptHealing(amount, Owner.Id);
        return HealRate - 1;
    }
}

/// <summary>
/// Toxin poisoning: POISON damage keeps hurting for PoisonDuration, repeating the damage it did every
/// PoisonDamageInterval (unresistable, so armor doesn't help). Any healing cures it.
/// The object is tinted green while poisoned.
/// </summary>
public sealed class PoisonedBehavior : Module, IUpdateModule, ICreateModule, IDamageModule
{
    int damageInterval, duration;

    int damageFrame, stopFrame;
    float damageAmount;
    string deathType = "POISONED";

    protected override void Initialize()
    {
        damageInterval = FramesSetting("PoisonDamageInterval");
        duration = FramesSetting("PoisonDuration");
    }

    public void OnCreate() => Owner.SleepUpdate(this);

    public void OnDamage(DamageInfo damage)
    {
        if (damage.DamageType == "POISON")
            StartPoison(damage);
    }

    public void OnHealing(DamageInfo healing)
    {
        StopPoison();
        Owner.SleepUpdate(this);
    }

    public int Update()
    {
        if (stopFrame == 0)
            return LogicTime.SleepForever;
        int now = Frame;
        if (damageFrame != 0 && now >= damageFrame)
        {
            Owner.Damage(new DamageInfo { Amount = damageAmount, DamageType = "UNRESISTABLE", DamageFxOverride = "POISON", DeathType = deathType });
            damageFrame = now + damageInterval;
        }
        if (stopFrame != 0 && now >= stopFrame && !Owner.IsEffectivelyDead)
            StopPoison();
        return SleepTime();
    }

    int SleepTime()
    {
        int now = Frame;
        if (stopFrame == 0 || stopFrame == now)
            return LogicTime.SleepForever;
        int soonest = damageFrame != 0 && damageFrame < stopFrame ? damageFrame : stopFrame;
        return Math.Max(0, soonest - now - 1);
    }

    void StartPoison(DamageInfo damage)
    {
        int now = Frame;
        damageAmount = damage.ActualDamageDealt;
        stopFrame = now + duration;
        damageFrame = damageFrame != 0 ? Math.Min(damageFrame, now + damageInterval) : now + damageInterval;
        deathType = damage.DeathType;
        Owner.SetTint(TintStatus.Poisoned, true);
        Owner.SleepUpdate(this, SleepTime());
    }

    void StopPoison()
    {
        damageFrame = 0;
        stopFrame = 0;
        damageAmount = 0;
        Owner.SetTint(TintStatus.Poisoned, false);
    }
}

/// <summary>
/// Catching fire. Once FLAME or PARTICLE_BEAM damage adds up to FlameDamageLimit (counted afresh
/// after FlameDamageExpiration without flame damage), the object is AFLAME for AflameDuration,
/// taking AflameDamageAmount every AflameDamageDelay. After BurnedDelay it is BURNED (smoldering)
/// and can never catch fire again. A FireSpreadUpdate on the object passes the fire on.
/// </summary>
public sealed class FlammableUpdate : Module, IUpdateModule, ICreateModule, IDamageModule
{
    enum FlameStatus { Normal, Aflame, Burned }

    int burnedDelay, aflameDuration, aflameDamageDelay, damageExpiration;
    float aflameDamageAmount, flameDamageLimitSetting;
    string burningSound = "";

    FlameStatus status = FlameStatus.Normal;
    int aflameEndFrame, burnedEndFrame, damageEndFrame;
    float flameDamageLimit;
    int lastFlameDamageFrame;

    protected override void Initialize()
    {
        burnedDelay = FramesSetting("BurnedDelay");
        aflameDuration = FramesSetting("AflameDuration");
        aflameDamageDelay = FramesSetting("AflameDamageDelay");
        aflameDamageAmount = IntSetting("AflameDamageAmount");
        burningSound = Setting("BurningSoundName");
        flameDamageLimitSetting = FloatSetting("FlameDamageLimit", 20);
        damageExpiration = Definition.Settings.Has("FlameDamageExpiration") ? FramesSetting("FlameDamageExpiration") : LogicTime.FramesPerSecond * 2;
        flameDamageLimit = flameDamageLimitSetting;
    }

    public void OnCreate() => Owner.SleepUpdate(this);

    /// <summary>Would fire take hold on this object now?</summary>
    public bool WouldIgnite => status == FlameStatus.Normal;

    public void OnDamage(DamageInfo damage)
    {
        if (damage.DamageType is not ("FLAME" or "PARTICLE_BEAM"))
            return;
        int now = Frame;
        if (now - damageExpiration > lastFlameDamageFrame)
            flameDamageLimit = flameDamageLimitSetting;
        lastFlameDamageFrame = now;
        if (!Owner.HasStatus(Status.Aflame) && !Owner.HasStatus(Status.Burned))
        {
            flameDamageLimit -= damage.ActualDamageDealt;
            if (flameDamageLimit <= 0)
                TryToIgnite();
        }
    }

    public void TryToIgnite()
    {
        if (status != FlameStatus.Normal)
            return;
        Owner.SetStatus(Status.Aflame);
        Owner.SetCondition("AFLAME");
        if (burningSound.Length > 0)
            Game.Events.PlaySound(burningSound, Owner.Position, source: Owner);
        status = FlameStatus.Aflame;
        Owner.FindModule<FireSpreadUpdate>()?.StartFireSpreading();

        int now = Frame;
        aflameEndFrame = now + aflameDuration;
        burnedEndFrame = burnedDelay != 0 ? now + burnedDelay : 0;
        damageEndFrame = aflameDamageDelay != 0 ? now + aflameDamageDelay : 0;
        Owner.SleepUpdate(this, SleepTime());
    }

    public int Update()
    {
        int now = Frame;
        if (damageEndFrame != 0 && now >= damageEndFrame)
        {
            damageEndFrame = now + aflameDamageDelay;
            Owner.Damage(new DamageInfo { Amount = aflameDamageAmount, SourceId = Owner.Id, DamageType = "FLAME", DeathType = "BURNED" });
            if (Owner.IsDestroyed)
                return LogicTime.SleepForever;
        }
        if (burnedEndFrame != 0 && now >= burnedEndFrame)
        {
            Owner.SetStatus(Status.Burned);
            Owner.SetCondition("SMOLDERING");
        }
        if (aflameEndFrame != 0 && now >= aflameEndFrame)
        {
            status = Owner.HasStatus(Status.Burned) ? FlameStatus.Burned : FlameStatus.Normal;
            Owner.SetStatus(Status.Aflame, false);
            Owner.SetCondition("AFLAME", false);
        }
        return SleepTime();
    }

    int SleepTime()
    {
        int now = Frame;
        if (status != FlameStatus.Aflame || aflameEndFrame == 0 || aflameEndFrame <= now)
            return LogicTime.SleepForever;
        int soonest = aflameEndFrame;
        if (burnedEndFrame != 0 && burnedEndFrame < soonest && burnedEndFrame > now) soonest = burnedEndFrame;
        if (damageEndFrame != 0 && damageEndFrame < soonest && damageEndFrame > now) soonest = damageEndFrame;
        return Math.Max(0, soonest - now - 1);
    }
}

/// <summary>
/// Spreading fire: while its object burns, every MinSpreadDelay..MaxSpreadDelay it throws embers
/// (OCLEmbers) and sets the nearest flammable thing within SpreadTryRange alight.
/// </summary>
public sealed class FireSpreadUpdate : Module, IUpdateModule, ICreateModule
{
    string oclEmbers = "";
    int minDelay, maxDelay;
    float spreadRange;

    protected override void Initialize()
    {
        oclEmbers = Setting("OCLEmbers");
        minDelay = FramesSetting("MinSpreadDelay");
        maxDelay = FramesSetting("MaxSpreadDelay");
        spreadRange = FloatSetting("SpreadTryRange");
    }

    public void OnCreate() => Owner.SleepUpdate(this);

    public void StartFireSpreading()
    {
        if (Owner.HasStatus(Status.Aflame))
            Owner.SleepUpdate(this, NextDelay() - 1);
    }

    public int Update()
    {
        if (!Owner.HasStatus(Status.Aflame))
            return LogicTime.SleepForever;
        if (oclEmbers.Length > 0)
            Game.ObjectCreation.Run(oclEmbers, Owner, Owner.Position);
        if (spreadRange != 0)
        {
            var nearest = Game.Partition.Query(Owner.Position, spreadRange)
                .Where(o => o != Owner && o.FindModule<FlammableUpdate>() is { WouldIgnite: true })
                .Select(o => (Object: o, Distance: System.Numerics.Vector3.DistanceSquared(o.Position, Owner.Position)))
                .Where(p => p.Distance <= spreadRange * spreadRange)
                .OrderBy(p => p.Distance)
                .Select(p => p.Object)
                .FirstOrDefault();
            nearest?.FindModule<FlammableUpdate>()?.TryToIgnite();
        }
        return NextDelay() - 1;
    }

    int NextDelay() => Math.Max(1, Game.Random.Int(minDelay, maxDelay));
}

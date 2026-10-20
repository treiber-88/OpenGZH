using OpenGZH.Core.Rules;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Contain;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules.Stealth;

/// <summary>Objects that make and control slaves (e.g. a Stinger Site's soldiers); stealth asks about them.</summary>
public interface ISpawnBehavior
{
    bool AreAllSlavesStealthed();
    void RevealSlaves();
}

/// <summary>What a stealthy object looks like to one player.</summary>
public enum StealthLook
{
    /// <summary>Normal: not stealthed.</summary>
    None,
    /// <summary>Stealthed, seen by its own side (drawn see-through).</summary>
    VisibleFriendly,
    /// <summary>Stealthed and detected, seen by its own side (see-through with a warning effect).</summary>
    VisibleFriendlyDetected,
    /// <summary>Stealthed and detected: everyone can see and shoot it.</summary>
    VisibleDetected,
    /// <summary>Stealthed: enemies can't see it.</summary>
    Invisible,
    /// <summary>A disguised bomb truck seen by enemies: drawn as what it is pretending to be.</summary>
    DisguisedEnemy,
}

/// <summary>
/// Stealth (the original "StealthUpdate"): the object becomes STEALTHED after StealthDelay unless one
/// of its StealthForbiddenConditions holds (attacking, moving, using an ability, being hit...). A
/// detector, or doing something revealing, marks it DETECTED for a while. DisguisesAsTeam objects
/// (the GLA Bomb Truck) instead disguise themselves as a vehicle they target.
///
/// Kept from the original, quirks included: "ATTACKING" checks whether a weapon is firing, and
/// NO_BLACK_MARKET is only re-checked every BlackMarketCheckDelay.
/// </summary>
public sealed class StealthUpdate : Module, IUpdateModule, ICreateModule
{
    public FlagSet DisabledTypesToProcess => DisabledMask.Held;

    [Flags]
    enum Forbidden
    {
        None = 0,
        Attacking = 0x1, Moving = 0x2, UsingAbility = 0x4,
        FiringPrimary = 0x8, FiringSecondary = 0x10, FiringTertiary = 0x20,
        NoBlackMarket = 0x40, TakingDamage = 0x80, RidersAttacking = 0x100,
        FiringWeapon = FiringPrimary | FiringSecondary | FiringTertiary,
    }

    static readonly string[] ForbiddenNames =
        ["ATTACKING", "MOVING", "USING_ABILITY", "FIRING_PRIMARY", "FIRING_SECONDARY", "FIRING_TERTIARY", "NO_BLACK_MARKET", "TAKING_DAMAGE", "RIDERS_ATTACKING"];

    // Settings
    int stealthDelay;
    float moveThresholdSpeed;
    Forbidden forbidden;
    FlagSet requiredStatus, forbiddenStatus, hintDetectableStatus;
    bool teamDisguised, innateStealth, useRiderStealth, grantedBySpecialPower, orderIdleEnemiesToAttack;
    float revealDistanceFromTarget;
    int disguiseTransitionFrames, disguiseRevealTransitionFrames, blackMarketCheckFrames;
    string disguiseFx = "", disguiseRevealFx = "";

    // State
    int stealthAllowedFrame;
    bool enabled;
    int detectionExpiresFrame;
    int framesGranted;
    int nextBlackMarketCheckFrame;
    bool transitioningToDisguise, disguiseHalfwayReached;
    int disguiseFramesLeft;

    /// <summary>Friendly see-through opacity range and pulse (read by the client).</summary>
    public float FriendlyOpacityMin { get; private set; }
    public float FriendlyOpacityMax { get; private set; }
    public int PulseFrames { get; private set; }

    /// <summary>What a disguised object pretends to be, and whose it pretends to be (-1 = not disguised).</summary>
    public ObjectTemplate? DisguiseTemplate { get; private set; }
    public int DisguisePlayerIndex { get; private set; } = -1;
    public bool IsDisguised { get; private set; }

    /// <summary>0..1 fade while changing into or out of a disguise (1 when not changing).</summary>
    public float DisguiseOpacity { get; private set; } = 1;

    public bool CanDisguise => teamDisguised;
    public int StealthDelay => stealthDelay;
    public string EnemyDetectionEva => Setting("EnemyDetectionEvaEvent");
    public string OwnDetectionEva => Setting("OwnDetectionEvaEvent");

    protected override void Initialize()
    {
        stealthDelay = Definition.Settings.Has("StealthDelay") ? FramesSetting("StealthDelay") : int.MaxValue / 2;
        moveThresholdSpeed = RuleUnits.Velocity(Setting("MoveThresholdSpeed"));
        foreach (string word in RuleValue.Words(Setting("StealthForbiddenConditions")))
        {
            int index = Array.FindIndex(ForbiddenNames, n => n.Equals(word, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                forbidden |= (Forbidden)(1 << index);
        }
        requiredStatus = Flags.ObjectStatus.Parse(Setting("RequiredStatus"));
        forbiddenStatus = Flags.ObjectStatus.Parse(Setting("ForbiddenStatus"));
        hintDetectableStatus = Flags.ObjectStatus.Parse(Setting("HintDetectableConditions"));
        FriendlyOpacityMin = Definition.Settings.Has("FriendlyOpacityMin") ? FloatSetting("FriendlyOpacityMin") : 0.5f;
        FriendlyOpacityMax = Definition.Settings.Has("FriendlyOpacityMax") ? FloatSetting("FriendlyOpacityMax") : 1f;
        PulseFrames = Definition.Settings.Has("PulseFrequency") ? FramesSetting("PulseFrequency") : 30;
        teamDisguised = BoolSetting("DisguisesAsTeam");
        revealDistanceFromTarget = FloatSetting("RevealDistanceFromTarget");
        orderIdleEnemiesToAttack = BoolSetting("OrderIdleEnemiesToAttackMeUponReveal");
        disguiseFx = Setting("DisguiseFX");
        disguiseRevealFx = Setting("DisguiseRevealFX");
        disguiseTransitionFrames = FramesSetting("DisguiseTransitionTime");
        disguiseRevealTransitionFrames = FramesSetting("DisguiseRevealTransitionTime");
        innateStealth = BoolSetting("InnateStealth", true);
        useRiderStealth = BoolSetting("UseRiderStealth");
        blackMarketCheckFrames = FramesSetting("BlackMarketCheckDelay");
        grantedBySpecialPower = BoolSetting("GrantedBySpecialPower");

        stealthAllowedFrame = Frame + stealthDelay;
        enabled = !teamDisguised;
    }

    public void OnCreate()
    {
        if (innateStealth)
            Owner.SetStatus(Status.CanStealth);
        if (grantedBySpecialPower)
            Owner.SleepUpdate(this);
    }

    int SleepTime => enabled ? 0 : LogicTime.SleepForever;

    /// <summary>Stealth given for a while by a power (e.g. GPS Scrambler); a player order ends it early.</summary>
    public void ReceiveGrant(bool active, int frames = 0)
    {
        if (CanDisguise)
            return; // bomb trucks keep their disguise logic
        enabled = active;
        if (enabled)
        {
            Owner.SetStatus(Status.CanStealth);
            Owner.SetStatus(Status.Stealthed);
            stealthAllowedFrame = Frame;
            framesGranted = frames;
            Owner.WakeUpdate(this);
        }
        else
        {
            Owner.SetStatus(Status.CanStealth, false);
            Owner.SetStatus(Status.Stealthed, false);
            stealthAllowedFrame = int.MaxValue / 2;
            framesGranted = 0;
        }
        if (Owner.FindModule<ContainModule>() is { IsRiderChangeContain: true } contain
            && contain.Passengers.FirstOrDefault()?.FindModule<StealthUpdate>() is { } riderStealth)
            riderStealth.ReceiveGrant(active, frames);
    }

    GameObject StealthOwner()
    {
        if (useRiderStealth && Owner.FindModule<ContainModule>()?.Passengers.FirstOrDefault() is { } rider)
            return rider;
        return Owner;
    }

    bool AllowedToStealth(GameObject stealthOwner)
    {
        var self = Owner;
        var flags = forbidden;
        if (self != stealthOwner && stealthOwner.FindModule<StealthUpdate>() is { } ownerStealth)
            flags = ownerStealth.forbidden;

        if (self.IsKindOf("SPAWNS_ARE_THE_WEAPONS") && self.FindModule<ISpawnBehavior>() is { } spawns && !spawns.AreAllSlavesStealthed())
        {
            spawns.RevealSlaves();
            return false;
        }
        if (flags.HasFlag(Forbidden.Attacking) && self.HasStatus(Status.IsFiringWeapon))
            return false;
        if (flags.HasFlag(Forbidden.UsingAbility) && self.HasStatus(Status.UsingAbility))
            return false;
        if (flags.HasFlag(Forbidden.NoBlackMarket) && nextBlackMarketCheckFrame < Frame)
        {
            // Checked only every BlackMarketCheckDelay: needs a finished, living Black Market.
            nextBlackMarketCheckFrame += blackMarketCheckFrames + Game.Random.Int(0, 10);
            bool blackMarket = Game.ObjectsOwnedBy(self.Owner).Any(o => o.IsKindOf("FS_BLACK_MARKET")
                && !o.IsEffectivelyDead && !o.IsUnderConstruction && !o.HasStatus(Status.Sold));
            if (!blackMarket)
                return false;
        }
        if (!stealthOwner.HasStatus(Status.CanStealth))
            return false;
        if (flags.HasFlag(Forbidden.TakingDamage) && self.Body is { } body && body.LastDamageFrame >= Frame - 1)
            return false;
        if (!requiredStatus.IsEmpty && !self.StatusFlags.HasAll(requiredStatus))
            return false;
        if (self.StatusFlags.HasAny(forbiddenStatus))
            return false;
        if ((flags & Forbidden.FiringWeapon) != 0 && self.HasStatus(Status.IsFiringWeapon))
        {
            if ((flags & Forbidden.FiringWeapon) == Forbidden.FiringWeapon)
                return false;
            int lastFrame = Frame - 1;
            if (flags.HasFlag(Forbidden.FiringPrimary) && self.Weapons.Slots[0]?.LastFireFrame >= lastFrame)
                return false;
            if (flags.HasFlag(Forbidden.FiringSecondary) && self.Weapons.Slots[1]?.LastFireFrame >= lastFrame)
                return false;
            if (flags.HasFlag(Forbidden.FiringTertiary) && self.Weapons.Slots[2]?.LastFireFrame >= lastFrame)
                return false;
        }
        if (Game.FindObject(self.ContainedById)?.FindModule<ContainModule>() is { IsGarrisonable: false })
            return false; // only garrisons keep their occupants stealthed
        if (flags.HasFlag(Forbidden.RidersAttacking) && self.FindModule<ContainModule>() is { PassengersAllowedToFire: true } myContain
            && myContain.Passengers.Any(p => p.HasStatus(Status.IsAttacking)))
            return false;
        if (flags.HasFlag(Forbidden.Moving) && CurrentSpeed(self) > moveThresholdSpeed)
            return false;
        if (self.HasStatus(Status.ScriptUnstealthed))
            return false;
        return true;
    }

    static float CurrentSpeed(GameObject obj)
    {
        return obj.FindModule<PhysicsBehavior>()?.VelocityMagnitude ?? 0;
    }

    public int Update()
    {
        var self = Owner;
        var stealthOwner = StealthOwner();
        int delay = self == stealthOwner ? stealthDelay : stealthOwner.FindModule<StealthUpdate>()?.stealthDelay ?? stealthDelay;

        if (!enabled)
            return SleepTime;

        if (disguiseFramesLeft > 0)
        {
            disguiseFramesLeft--;
            int total = transitioningToDisguise ? disguiseTransitionFrames : disguiseRevealTransitionFrames;
            float factor = 1f - disguiseFramesLeft / (float)Math.Max(1, total);
            if (factor >= 0.5f && !disguiseHalfwayReached)
            {
                ChangeVisualDisguise();
                disguiseHalfwayReached = true;
            }
            DisguiseOpacity = MathF.Abs(1f - factor * 2f);
            if (disguiseFramesLeft == 0 && !transitioningToDisguise)
            {
                enabled = false;
                self.SetStatus(Status.Stealthed, false);
                self.SetStatus(Status.Detected, false);
                return SleepTime;
            }
        }

        if (revealDistanceFromTarget > 0 && Game.FindObject(self.AI?.TargetId ?? 0) is { } victim
            && MathUtil.DistanceSquared2D(self.Position, victim.Position) <= revealDistanceFromTarget * revealDistanceFromTarget)
        {
            MarkAsDetected();
            return SleepTime;
        }

        if (framesGranted > 0)
        {
            framesGranted--;
            if (self.AI?.LastCommandSource == CommandSource.Player || framesGranted == 0)
                ReceiveGrant(false);
        }

        if (AllowedToStealth(stealthOwner))
        {
            if (stealthAllowedFrame > Frame)
                return SleepTime;
            if (!self.HasStatus(Status.Stealthed))
                Game.Events.PlaySound(self.Template.Rules.GetString("SoundStealthOn"), self.Position);
            self.SetStatus(Status.Stealthed);
        }
        else
        {
            stealthAllowedFrame = Frame + delay;
            if (self.HasStatus(Status.Stealthed))
                Game.Events.PlaySound(self.Template.Rules.GetString("SoundStealthOn"), self.Position);
            self.SetStatus(Status.Stealthed, false);
        }

        if (detectionExpiresFrame > Frame)
        {
            if (!self.HasStatus(Status.Detected))
                Game.Events.PlaySound(self.Template.Rules.GetString("SoundStealthOff"), self.Position);
            self.SetStatus(Status.Detected);
        }
        else
        {
            self.SetStatus(Status.Detected, false);
        }
        return SleepTime;
    }

    /// <summary>Reveals the object for StealthDelay (or <paramref name="frames"/>); a disguise drops.</summary>
    public void MarkAsDetected(int frames = 0)
    {
        var stealthOwner = StealthOwner();
        var ownerStealth = stealthOwner == Owner ? this : stealthOwner.FindModule<StealthUpdate>() ?? this;
        if (IsDisguised)
            DisguiseAs(null);
        if (frames == 0)
            detectionExpiresFrame = Frame + ownerStealth.stealthDelay;
        else if (detectionExpiresFrame < Frame + frames)
            detectionExpiresFrame = Frame + frames;

        if (ownerStealth.orderIdleEnemiesToAttack)
        {
            foreach (var player in Game.Players.All.Where(p => p.IsEnemyOf(Owner.Owner)))
                foreach (var enemy in Game.ObjectsOwnedBy(player))
                    if (enemy.AI is { IsIdle: true } ai && MathUtil.Distance2D(enemy.Position, Owner.Position) <= enemy.Template.VisionRange)
                        ai.AttackObject(Owner, CommandSource.AI);
        }
    }

    /// <summary>Starts disguising as <paramref name="target"/> (null drops the disguise), fading over the transition time.</summary>
    public void DisguiseAs(GameObject? target)
    {
        if (target != null)
        {
            if (target.FindModule<StealthUpdate>() is { DisguiseTemplate: { } theirs } stealth)
            {
                DisguiseTemplate = theirs;
                DisguisePlayerIndex = stealth.DisguisePlayerIndex;
            }
            else
            {
                DisguiseTemplate = target.Template;
                DisguisePlayerIndex = target.Owner.Index;
            }
            enabled = true;
            transitioningToDisguise = true;
            disguiseFramesLeft = disguiseTransitionFrames;
            disguiseHalfwayReached = false;
            Owner.WakeUpdate(this);
        }
        else if (IsDisguised)
        {
            DisguiseTemplate = null;
            DisguisePlayerIndex = 0;
            disguiseFramesLeft = disguiseRevealTransitionFrames;
            transitioningToDisguise = false;
            disguiseHalfwayReached = false;
        }
    }

    /// <summary>Halfway through the fade the object switches look (the client draws <see cref="DisguiseTemplate"/>).</summary>
    void ChangeVisualDisguise()
    {
        if (DisguiseTemplate != null)
        {
            Game.Events.PlaySound(Owner.Template.UnitSound("DisguiseStarted"), Owner.Position);
            Game.Events.PlayFx(disguiseFx, Owner.Position);
            IsDisguised = true;
            Owner.SetStatus(Status.Disguised);
            Owner.SetCondition("DISGUISED");
        }
        else if (DisguisePlayerIndex != -1)
        {
            DisguisePlayerIndex = -1;
            bool hadTarget = Owner.AI?.TargetId is > 0;
            Game.Events.PlaySound(Owner.Template.UnitSound(hadTarget ? "DisguiseRevealedSuccess" : "DisguiseRevealedFailure"), Owner.Position);
            Game.Events.PlayFx(disguiseRevealFx, Owner.Position);
            IsDisguised = false;
            Owner.SetStatus(Status.Disguised, false);
            Owner.SetCondition("DISGUISED", false);
        }
    }

    /// <summary>How the object should look to <paramref name="player"/>.</summary>
    public StealthLook LookFor(Player player)
    {
        if (Owner.IsEffectivelyDead || !Owner.HasStatus(Status.Stealthed))
            return StealthLook.None;
        bool allied = Owner.Owner.IsAllyOf(player) || player.IsDefeated;
        if (CanDisguise)
            return !allied && IsDisguised ? StealthLook.DisguisedEnemy : StealthLook.None;
        if (Owner.HasStatus(Status.Detected))
            return allied ? StealthLook.VisibleFriendlyDetected : StealthLook.VisibleDetected;
        return allied ? StealthLook.VisibleFriendly : StealthLook.Invisible;
    }

    /// <summary>Whether <paramref name="player"/> may see (and so select or target) this object.</summary>
    public static bool IsHiddenFrom(GameObject obj, Player player) =>
        obj.FindModule<StealthUpdate>() is { } stealth && stealth.LookFor(player) == StealthLook.Invisible;
}

/// <summary>
/// Detects stealthed enemies within DetectionRange (or vision range) every DetectionRate, also
/// stealthy units hiding in garrisons. Detected units stay visible a little longer than the scan
/// interval, so they don't flicker. Can be switched off (InitiallyDisabled, or by an upgrade).
/// </summary>
public sealed class StealthDetectorUpdate : Module, IUpdateModule, ICreateModule
{
    public FlagSet DisabledTypesToProcess => DisabledMask.Held;

    int rate;
    float detectionRange;
    FlagSet extraRequiredKindOf, extraForbiddenKindOf;
    bool canDetectWhileGarrisoned, canDetectWhileContained;
    public bool Enabled { get; private set; }

    protected override void Initialize()
    {
        rate = Math.Max(1, FramesSetting("DetectionRate"));
        detectionRange = FloatSetting("DetectionRange");
        extraRequiredKindOf = Flags.KindOf.Parse(Setting("ExtraRequiredKindOf"));
        extraForbiddenKindOf = Flags.KindOf.Parse(Setting("ExtraForbiddenKindOf"));
        canDetectWhileGarrisoned = BoolSetting("CanDetectWhileGarrisoned");
        canDetectWhileContained = BoolSetting("CanDetectWhileContained");
        Enabled = !BoolSetting("InitiallyDisabled");
    }

    public void OnCreate()
    {
        if (Enabled)
            Owner.SleepUpdate(this, Game.Random.Int(1, rate));
        else
            Owner.SleepUpdate(this);
    }

    public void SetEnabled(bool on)
    {
        Enabled = on;
        if (on)
            Owner.WakeUpdate(this);
    }

    public int Update()
    {
        var self = Owner;
        if (!Enabled || self.IsEffectivelyDead || self.HasStatus(Status.Sold))
            return LogicTime.SleepForever;
        if (self.IsUnderConstruction)
            return 0;
        if (Game.FindObject(self.ContainedById)?.FindModule<ContainModule>() is { } container)
        {
            if (container.IsGarrisonable ? !canDetectWhileGarrisoned : !canDetectWhileContained)
                return rate;
        }

        float range = detectionRange > 0 ? detectionRange : self.Template.VisionRange;
        bool foundSomeone = false;
        foreach (var them in Game.Partition.Query(self.Position, range))
        {
            if (them.IsEffectivelyDead || them == self || self.Owner.IsAllyOf(them.Owner))
                continue;
            if (!extraRequiredKindOf.IsEmpty && !them.KindOf.HasAll(extraRequiredKindOf))
                continue;
            if (them.KindOf.HasAny(extraForbiddenKindOf))
                continue;
            if (MathUtil.Distance2D(self.Position, them.Position) > range)
                continue;

            if (them.HasStatus(Status.Stealthed) && them.FindModule<StealthUpdate>() is { } stealth)
            {
                foundSomeone = true;
                if (!them.HasStatus(Status.Detected))
                {
                    Game.Events.RadarEvent("StealthDiscovered", them.Position, self.Owner);
                    Game.Events.Message("MESSAGE:StealthDiscovered", self.Owner);
                    if (stealth.EnemyDetectionEva.Length > 0)
                        Game.Events.Eva(stealth.EnemyDetectionEva, self.Owner);
                    Game.Events.RadarEvent("StealthNeutralized", them.Position, them.Owner);
                    Game.Events.Message("MESSAGE:StealthNeutralized", them.Owner);
                    if (stealth.OwnDetectionEva.Length > 0)
                        Game.Events.Eva(stealth.OwnDetectionEva, them.Owner);
                }
                stealth.MarkAsDetected(rate + 1);
                Game.Events.ParticleSystem(Setting("IRGridParticleSysName"), them.Position);
            }
            else if (them.FindModule<ContainModule>() is { IsGarrisonable: true, StealthUnitsContained: > 0 } garrison)
            {
                foreach (var rider in garrison.Passengers)
                {
                    if (rider.FindModule<StealthUpdate>() is not { } riderStealth)
                        continue;
                    foundSomeone = true;
                    if (rider.Owner != self.Owner && !self.Owner.IsAllyOf(rider.Owner))
                        riderStealth.MarkAsDetected(rate + 2);
                }
            }
        }

        // The infrared ping (bright when something was found).
        if (!self.HasStatus(Status.Stealthed))
        {
            Game.Events.ParticleSystem(Setting(foundSomeone ? "IRBrightParticleSysName" : "IRParticleSysName"), self.Position, self);
            Game.Events.ParticleSystem(Setting("IRBeaconParticleSysName"), self.Position, self);
            Game.Events.PlaySound(Setting(foundSomeone ? "LoudPingSound" : "PingSound"), self.Position);
        }
        return rate;
    }
}

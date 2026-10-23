using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Bodies;

/// <summary>
/// Base class of all bodies: an object's health, armor and damage state.
/// The plain version is "ActiveBody"; the others change how damage or death works.
/// </summary>
public abstract class Body : Module, ICreateModule
{
    public float Health { get; protected set; }

    /// <summary>Which ends of a car have been flattened by tanks (CrushDie sets these; crushing uses them).</summary>
    public bool FrontCrushed { get; set; }
    public bool BackCrushed { get; set; }
    public float MaxHealth { get; protected set; }
    public float InitialHealth { get; protected set; }
    public BodyDamageState DamageState { get; private set; } = BodyDamageState.Pristine;
    public bool IsDead => Health <= 0 && MaxHealth > 0;

    /// <summary>Damage is multiplied by this (e.g. China's battle plans, crate bonuses).</summary>
    public float DamageScalar { get; set; } = 1f;

    /// <summary>When set, nothing can damage the object (scripts can make things invulnerable).</summary>
    public bool Indestructible { get; set; }

    /// <summary>Frame of the last damage taken (used by auto-heal delays).</summary>
    public int LastDamageFrame { get; private set; } = -1000;
    public uint LastDamageSourceId { get; protected set; }

    /// <summary>The last hit (the original's last damage info): who, what, and whether it did nothing.</summary>
    public LastDamageInfo? LastDamage { get; private set; }

    public bool LastDamageNoEffect => LastDamage?.NoEffect ?? true;

    // Subdual damage (microwave tanks, EMP) temporarily disables instead of killing.
    public float SubdualDamage { get; private set; }
    protected float SubdualDamageCap;
    protected float SubdualHealRatePerFrame;
    protected float SubdualHealAmount;

    public ArmorTemplate Armor { get; private set; } = ArmorTemplate.None;

    protected override void Initialize()
    {
        MaxHealth = FloatSetting("MaxHealth", 1);
        InitialHealth = FloatSetting("InitialHealth", MaxHealth);
        Health = InitialHealth;
        SubdualDamageCap = FloatSetting("SubdualDamageCap");
        SubdualHealRatePerFrame = FramesSetting("SubdualDamageHealRate");
        SubdualHealAmount = FloatSetting("SubdualDamageHealAmount");
    }

    public virtual void OnCreate()
    {
        UpdateArmor();
        UpdateDamageState();
    }

    /// <summary>Chooses the armor set matching the object's current armor conditions (veterancy, upgrades...).</summary>
    public void UpdateArmor()
    {
        var rules = Owner.Template.Rules;
        if (!rules.TryGet("ArmorSet", out var sets))
            return;

        var best = SparseMatch.Best(sets.AsList().ToList(), set => [Flags.ArmorSet.Parse(set.GetString("Conditions"))], Owner.ArmorSetFlags);
        string? bestArmor = best?.GetString("Armor");
        Armor = Game.Catalog.Armor(bestArmor ?? "");
    }

    /// <summary>Applies damage (or healing, for DamageType HEALING). The heart of combat.</summary>
    public virtual void AttemptDamage(DamageInfo damage)
    {
        if (Owner.IsEffectivelyDead)
            return;

        if (damage.DamageType.Equals("HEALING", StringComparison.OrdinalIgnoreCase) && !damage.Kill)
        {
            AttemptHealing(damage.Amount, damage.SourceId);
            return;
        }

        if (Indestructible && !damage.Kill)
            return;

        float amount = Armor.AdjustDamage(damage.DamageType, damage.Amount);

        if (damage.DamageType.Equals("KILL_PILOT", StringComparison.OrdinalIgnoreCase))
        {
            KillPilot(damage);
            return;
        }
        if (damage.DamageType.Equals("KILL_GARRISONED", StringComparison.OrdinalIgnoreCase))
        {
            KillGarrisoned(damage);
            return;
        }

        if (damage.DamageType.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
        {
            if (damage.DamageStatusType != null)
                Owner.SetStatusFor(damage.DamageStatusType, LogicTime.Frames(amount));
            return;
        }

        if (IsSubdualDamage(damage.DamageType))
        {
            AddSubdualDamage(amount);
            return;
        }

        if (!damage.DamageType.Equals("UNRESISTABLE", StringComparison.OrdinalIgnoreCase))
            amount *= DamageScalar;

        if (damage.Kill)
            amount = Health;
        var source = Game.FindObject(damage.SourceId);
        LastDamage = new LastDamageInfo(damage.SourceId, source?.Template.Name ?? "", source?.Owner.Index ?? -1, amount <= 0 && !damage.Kill, damage.DamageType);
        if (source != null)
            Owner.Owner.SetAttackedBy(source.Owner.Index);
        if (amount <= 0 && !damage.Kill)
            return;

        amount = AdjustIncomingDamage(amount, damage);
        float before = Health;
        ChangeHealth(-amount);
        damage.ActualDamageDealt = before - Health;
        LastDamageFrame = Frame;
        LastDamageSourceId = damage.SourceId;

        if (damage.ActualDamageDealt > 0)
            Game.Events.Damaged(Owner, damage);
        if (Health < before)
            foreach (var module in Owner.Modules.OfType<IDamageModule>().ToList())
                module.OnDamage(damage);

        if (Game.FindObject(damage.SourceId) is { } attacker)
            Owner.AI?.OnAttacked(attacker);

        if (Health <= 0 && before > 0)
            Die(damage);
    }

    /// <summary>
    /// A sniper shoots the driver (Jarmen Kell): the vehicle is left unmanned and neutral, for any
    /// infantry to climb into. A Combat Cycle loses its rider instead (or dies if it was moving).
    /// </summary>
    void KillPilot(DamageInfo damage)
    {
        if (!Owner.IsKindOf("VEHICLE"))
            return;
        var damager = Game.FindObject(damage.SourceId);
        LastDamageSourceId = damage.SourceId;
        if (Owner.Contain is { IsRiderChangeContain: true } contain)
        {
            if (Owner.AI is { IsMoving: true })
            {
                damager?.ScoreTheKill(Owner);
                Owner.Kill();
            }
            else if (contain.Passengers.FirstOrDefault() is { } rider)
            {
                Owner.AI?.Evacuate(Commands.CommandSource.AI, instantly: true);
                damager?.ScoreTheKill(rider);
                rider.Kill();
            }
        }
        else
        {
            Owner.SetDisabled("DISABLED_UNMANNED");
            Owner.AI?.Stop(Commands.CommandSource.AI);
            Owner.SetTeam(Game.Players.Neutral.DefaultTeam);
        }
    }

    /// <summary>Kills up to Amount of a garrisoned building's occupants (the building itself is unharmed).</summary>
    void KillGarrisoned(DamageInfo damage)
    {
        int kills = (int)MathF.Floor(damage.Amount);
        if (Owner.Contain is not { IsGarrisonable: true, Count: > 0, IsImmuneToClearBuildingAttacks: false } contain)
            return;
        var damager = Game.FindObject(damage.SourceId);
        foreach (var occupant in contain.Passengers.Where(p => !p.IsEffectivelyDead).Take(kills).ToList())
        {
            damager?.ScoreTheKill(occupant);
            occupant.Kill();
        }
    }

    /// <summary>Hook for bodies that limit damage (immortal, highlander...).</summary>
    protected virtual float AdjustIncomingDamage(float amount, DamageInfo damage) => amount;

    /// <summary>Hook for bodies with special deaths (e.g. UndeadBody's second life).</summary>
    protected virtual void Die(DamageInfo damage) => Owner.OnDie(damage);

    public void AttemptHealing(float amount, uint sourceId = 0)
    {
        if (Owner.IsEffectivelyDead && !Owner.IsKindOf("BRIDGE") && !Owner.IsKindOf("BRIDGE_TOWER"))
            return;
        amount = Armor.AdjustDamage("HEALING", amount);
        if (amount <= 0)
            return;
        float before = Health;
        ChangeHealth(amount);
        if (before <= 0 && Health > 0)
            Owner.Revive();
        if (Health > before)
        {
            var healing = new DamageInfo { Amount = Health - before, DamageType = "HEALING", SourceId = sourceId };
            foreach (var module in Owner.Modules.OfType<IDamageModule>().ToList())
                module.OnHealing(healing);
        }
    }

    /// <summary>Sets health directly (e.g. from a script or the map), clamped to 0..MaxHealth.</summary>
    public void SetHealth(float value)
    {
        Health = Math.Clamp(value, 0, MaxHealth);
        UpdateDamageState();
    }

    public void SetMaxHealth(float value, MaxHealthChange change)
    {
        float ratio = MaxHealth > 0 ? Health / MaxHealth : 1;
        float added = value - MaxHealth;
        MaxHealth = Math.Max(1, value);
        Health = change switch
        {
            MaxHealthChange.PreserveRatio => MaxHealth * ratio,
            MaxHealthChange.AddCurrentHealthToo => Health + added,
            MaxHealthChange.FullyHeal => MaxHealth,
            _ => Math.Min(Health, MaxHealth),
        };
        Health = Math.Clamp(Health, 0, MaxHealth);
        UpdateDamageState();
    }

    public void OnVeterancyChanged(VeterancyLevel from, VeterancyLevel to)
    {
        var data = Game.Catalog.GameData;
        float baseMax = MaxHealth / data.HealthBonus[(int)from];
        SetMaxHealth(baseMax * data.HealthBonus[(int)to], MaxHealthChange.PreserveRatio);
    }

    protected void ChangeHealth(float delta)
    {
        Health = Math.Clamp(Health + delta, 0, MaxHealth);
        UpdateDamageState();
    }

    /// <summary>Forces a damage state by setting health just inside it (keeps an Overlord's add-on looking as damaged as the Overlord).</summary>
    public void SetDamageState(BodyDamageState state)
    {
        var data = Game.Catalog.GameData;
        float ratio = state switch
        {
            BodyDamageState.Damaged => data.UnitDamagedThreshold,
            BodyDamageState.ReallyDamaged => data.UnitReallyDamagedThreshold,
            BodyDamageState.Rubble => 0,
            _ => 1,
        };
        ChangeHealth(Math.Max(0, MaxHealth * ratio - 1) - Health);
    }

    void UpdateDamageState()
    {
        var data = Game.Catalog.GameData;
        float ratio = MaxHealth > 0 ? Health / MaxHealth : 0;
        var state = Health <= 0 ? BodyDamageState.Rubble
            : ratio < data.UnitReallyDamagedThreshold ? BodyDamageState.ReallyDamaged
            : ratio < data.UnitDamagedThreshold ? BodyDamageState.Damaged
            : BodyDamageState.Pristine;
        if (state == DamageState)
            return;

        var old = DamageState;
        DamageState = state;
        Owner.SetCondition(Condition.Damaged, state == BodyDamageState.Damaged);
        Owner.SetCondition(Condition.ReallyDamaged, state == BodyDamageState.ReallyDamaged);
        Owner.SetCondition(Condition.Rubble, state == BodyDamageState.Rubble);
        foreach (var module in Owner.Modules.OfType<IDamageModule>().ToList())
            module.OnBodyDamageStateChange(old, state);
    }

    // ---- Subdual damage ----

    static bool IsSubdualDamage(string type) => type.StartsWith("SUBDUAL_", StringComparison.OrdinalIgnoreCase);

    public bool CanBeSubdued => SubdualDamageCap > 0;
    public bool IsSubdued => CanBeSubdued && SubdualDamage >= MaxHealth;

    void AddSubdualDamage(float amount)
    {
        if (!CanBeSubdued)
            return;
        bool wasSubdued = IsSubdued;
        SubdualDamage = Math.Clamp(SubdualDamage + amount, 0, SubdualDamageCap);
        // Gaining subdual damage tints the object blue (the original's notifySubdualDamage).
        Owner.SetTint(Objects.TintStatus.GainingSubdualDamage, amount > 0);
        if (wasSubdued != IsSubdued)
            Owner.SetDisabled("DISABLED_SUBDUED", IsSubdued);
    }

    /// <summary>Called by the logic every frame for bodies with subdual damage, so it wears off.</summary>
    protected void HealSubdual()
    {
        if (SubdualDamage <= 0 || SubdualHealRatePerFrame <= 0)
            return;
        if ((Frame - LastDamageFrame) % (int)Math.Max(1, SubdualHealRatePerFrame) == 0)
            AddSubdualDamage(-SubdualHealAmount);
    }
}

public enum MaxHealthChange { PreserveRatio, AddCurrentHealthToo, SameCurrentHealth, FullyHeal }

/// <summary>What last hit an object: its id, its template, its player (-1 if gone), and whether it did no damage.</summary>
public sealed record LastDamageInfo(uint SourceId, string SourceTemplate, int SourcePlayerIndex, bool NoEffect, string DamageType = "");

using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// Fires a weapon at its own position when hit (Reaction*) or all the time (Continuous*), picked by
/// the object's damage state (the original's FireWeaponWhenDamagedBehavior): the avalanche boulders,
/// exploding barrels. Reactions need DamageAmount or more of one of DamageTypes.
/// </summary>
public sealed class FireWeaponWhenDamagedBehavior : UpgradeModule, IUpdateModule, ICreateModule, IDamageModule
{
    readonly Weapon?[] reaction = new Weapon?[4], continuous = new Weapon?[4];
    static readonly string[] states = ["Pristine", "Damaged", "ReallyDamaged", "Rubble"];
    FlagSet damageTypes;
    float damageAmount;

    protected override void InitializeUpgrade()
    {
        damageTypes = Flags.DamageType.Parse(Setting("DamageTypes", "ALL"));
        damageAmount = FloatSetting("DamageAmount");
        for (int i = 0; i < 4; i++)
        {
            reaction[i] = MakeWeapon("ReactionWeapon" + states[i]);
            continuous[i] = MakeWeapon("ContinuousWeapon" + states[i]);
        }
    }

    Weapon? MakeWeapon(string key)
    {
        if (Game.Catalog.Weapon(Setting(key)) is not { } template)
            return null;
        var weapon = new Weapon(Owner, template, WeaponSlot.Primary);
        weapon.Reload();
        return weapon;
    }

    public void OnCreate()
    {
        if (BoolSetting("StartsActive"))
            ActivateWithoutUpgrade();
        if (!(IsActive && continuous.Any(w => w != null)))
            Owner.SleepUpdate(this);
    }

    protected override void OnUpgrade()
    {
        if (continuous.Any(w => w != null))
            Owner.WakeUpdate(this);
    }

    int StateIndex => (int)(Owner.Body?.DamageState ?? BodyDamageState.Pristine);

    public void OnDamage(DamageInfo damage)
    {
        if (!IsActive || !damageTypes.Has(Flags.DamageType.IndexOf(damage.DamageType)) || damage.ActualDamageDealt < damageAmount)
            return;
        if (reaction[StateIndex] is { IsReady: true } weapon)
            weapon.ForceFireWeapon(Owner.Position);
    }

    public int Update()
    {
        if (!IsActive)
            return LogicTime.SleepForever;
        if (continuous[StateIndex] is { IsReady: true } weapon)
            weapon.ForceFireWeapon(Owner.Position);
        return 0;
    }
}

/// <summary>
/// After a burst of MinShotsToCreateOCL or more consecutive shots from WeaponSlot, runs OCL with a
/// lifetime of OCLLifetimePerSecond ms per second of firing, capped at OCLLifetimeMaxCap (the
/// original's FireOCLAfterWeaponCooldownUpdate: the upgraded Dragon tank's burning ground).
/// </summary>
public sealed class FireOCLAfterWeaponCooldownUpdate : UpgradeModule, IUpdateModule
{
    WeaponSlot slot;
    bool valid;
    int consecutiveShots, startFrame;

    protected override void InitializeUpgrade() => slot = Setting("WeaponSlot").ToUpperInvariant() switch
    {
        "SECONDARY" or "SECONDARY_WEAPON" => WeaponSlot.Secondary,
        "TERTIARY" or "TERTIARY_WEAPON" => WeaponSlot.Tertiary,
        _ => WeaponSlot.Primary,
    };

    protected override void OnUpgrade() { }

    /// <summary>The original tests the upgrade triggers every frame rather than latching them.</summary>
    bool UpgradeConditionsMet()
    {
        if (TriggeredBy.Count == 0)
            return true;
        bool Has(string upgrade) => Owner.HasUpgrade(upgrade);
        bool triggered = RequiresAllTriggers ? TriggeredBy.All(Has) : TriggeredBy.Any(Has);
        return triggered && !ConflictsWith.Any(Has);
    }

    public int Update()
    {
        bool validThisFrame = true, validToFireOcl = true;
        var weapon = Owner.Weapons.Current;
        if (weapon == null || weapon.Slot != slot)
            validThisFrame = false;
        if (validThisFrame && !UpgradeConditionsMet())
        {
            validThisFrame = false;
            validToFireOcl = false;
        }
        int now = Frame;
        int minShots = IntSetting("MinShotsToCreateOCL", 1);
        if (validThisFrame)
        {
            if (weapon!.LastFireFrame == now - 1)
            {
                if (++consecutiveShots == 1)
                    startFrame = now;
            }
            else if (weapon.NextShotFrame < now && minShots <= consecutiveShots)
                FireOcl();
        }
        else if (validToFireOcl && Owner.Weapons.Slots[(int)slot] != null && minShots <= consecutiveShots)
            FireOcl();
        if (validThisFrame != valid)
        {
            valid = validThisFrame;
            ResetStats();
        }
        return 0;
    }

    void ResetStats()
    {
        consecutiveShots = 0;
        startFrame = 0;
    }

    void FireOcl()
    {
        float seconds = (Frame - startFrame) / (float)LogicTime.FramesPerSecond;
        seconds *= IntSetting("OCLLifetimePerSecond", 1000) * 0.001f;
        int frames = (int)(seconds * LogicTime.FramesPerSecond);
        int cap = Definition.Settings.Has("OCLLifetimeMaxCap") ? FramesSetting("OCLLifetimeMaxCap") : 1000;
        frames = Math.Min(frames, cap);
        Game.ObjectCreation.Run(Setting("OCL"), Owner, Owner.Position, Owner, lifetimeFrames: frames);
        ResetStats();
    }
}

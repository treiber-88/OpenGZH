using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Combat;

/// <summary>
/// A weapon definition from the "Weapons" rules section. Every field of the original weapon
/// definition is supported; distances are world units, times are frames, speeds per frame.
/// </summary>
public sealed class WeaponTemplate
{
    public string Name { get; }
    public RuleNode Rules { get; }

    // Damage
    public float PrimaryDamage { get; }
    public float PrimaryDamageRadius { get; }
    public float SecondaryDamage { get; }
    public float SecondaryDamageRadius { get; }
    public float ShockWaveAmount { get; }
    public float ShockWaveRadius { get; }
    public float ShockWaveTaperOff { get; }
    public string DamageType { get; }
    public string DamageStatusType { get; }
    public string DeathType { get; }
    public FlagSet RadiusDamageAffects { get; }
    public float RadiusDamageAngle { get; }
    public bool DamageDealtAtSelfPosition { get; }

    // Range and aiming
    public float AttackRange { get; }
    public float MinimumAttackRange { get; }
    public float ContinueAttackRange { get; }
    public float RequestAssistRange { get; }
    public float AcceptableAimDelta { get; }
    public float MinTargetPitch { get; }
    public float MaxTargetPitch { get; }
    public float ScatterRadius { get; }
    public float ScatterRadiusVsInfantry { get; }
    public float ScatterTargetScalar { get; }
    /// <summary>Fixed aim offsets, used once each per clip in random order (the Scud Storm's spread), scaled by ScatterTargetScalar.</summary>
    public IReadOnlyList<System.Numerics.Vector2> ScatterTargets { get; }

    // What it can hit
    public bool AntiGround { get; }
    public bool AntiAirborneVehicle { get; }
    public bool AntiAirborneInfantry { get; }
    public bool AntiProjectile { get; }
    public bool AntiSmallMissile { get; }
    public bool AntiBallisticMissile { get; }
    public bool AntiMine { get; }
    public bool AntiParachute { get; }
    public FlagSet ProjectileCollidesWith { get; }

    /// <summary>Melee-range weapons (range under one cell): they don't count as "armed" for the Battle Bus.</summary>
    public bool IsContactWeapon => AttackRange * 1.05f < Formats.Maps.MapFile.CellSize;

    /// <summary>
    /// Whether a projectile of this weapon hits something it touches on the way (the original's
    /// shouldProjectileCollideWith): always its intended victim, never its launcher, otherwise
    /// whatever the ProjectileCollidesWith flags allow (allies, enemies, structures, walls...).
    /// </summary>
    public bool ShouldProjectileCollideWith(GameObject? launcher, GameObject projectile, GameObject thing, uint intendedVictimId)
    {
        if (intendedVictimId == thing.Id)
            return true;
        if (launcher != null && (launcher == thing || launcher.ContainedById == thing.Id))
            return false;
        if ((DamageType == "FLAME" || DamageType == "PARTICLE_BEAM") && thing.HasStatus(Core.Status.Burned))
            return false;
        // Something using a sneaky targeting offset is momentarily immune to being hit.
        if (thing.AI is Modules.AI.JetAIUpdate jet && jet.TryGetSneakyTargetingOffset(out _))
            return false;
        // Planes parked on their own airfield are not hit by shots meant for them.
        if (thing.IsKindOf("FS_AIRFIELD") && thing.FindModules<Modules.IParkingPlace>().Any(p => p.HasReservedSpace(intendedVictimId)))
            return false;
        var mask = default(FlagSet);
        var relation = projectile.Owner.RelationshipTo(thing.Owner);
        if (relation == Players.Relationship.Allies) mask = mask.With(Flags.WeaponCollides.IndexOf("ALLIES"));
        else if (relation == Players.Relationship.Enemies) mask = mask.With(Flags.WeaponCollides.IndexOf("ENEMIES"));
        if (thing.IsKindOf(Core.Kind.Structure))
            mask = mask.With(Flags.WeaponCollides.IndexOf(thing.Owner == projectile.Owner ? "CONTROLLED_STRUCTURES" : "STRUCTURES"));
        if (thing.IsKindOf("SHRUBBERY")) mask = mask.With(Flags.WeaponCollides.IndexOf("SHRUBBERY"));
        if (thing.IsKindOf("PROJECTILE")) mask = mask.With(Flags.WeaponCollides.IndexOf("PROJECTILES"));
        if (OpenGZH.Core.Rules.RuleValue.Float(thing.Template.Rules.GetString("FenceWidth")) > 0) mask = mask.With(Flags.WeaponCollides.IndexOf("WALLS"));
        if (thing.IsKindOf("SMALL_MISSILE")) mask = mask.With(Flags.WeaponCollides.IndexOf("SMALL_MISSILES"));
        if (thing.IsKindOf("BALLISTIC_MISSILE")) mask = mask.With(Flags.WeaponCollides.IndexOf("BALLISTIC_MISSILES"));
        return ProjectileCollidesWith.HasAny(mask);
    }
    public bool AllowAttackGarrisonedBuildings { get; }

    // Firing rhythm (frames)
    public int ClipSize { get; }
    public int ClipReloadFrames { get; }
    public int MinDelayBetweenShots { get; }
    public int MaxDelayBetweenShots { get; }
    public int ShotsPerBarrel { get; }
    public int PreAttackDelay { get; }
    public string PreAttackType { get; }
    public bool AutoReloadsClip { get; }

    /// <summary>RETURN_TO_BASE: the clip only refills at an airfield (jets).</summary>
    public bool ReloadsAtBase { get; }
    public int AutoReloadWhenIdle { get; }
    public int ContinuousFireOne { get; }
    public int ContinuousFireTwo { get; }
    public int ContinuousFireCoast { get; }

    // Projectiles and visuals
    public string ProjectileObject { get; }
    public float WeaponSpeed { get; }
    public float MinWeaponSpeed { get; }
    public bool ScaleWeaponSpeed { get; }

    /// <summary>The projectile object dies (rather than vanishing) when it goes off.</summary>
    public bool DieOnDetonate { get; }
    public float WeaponRecoil { get; }
    public string FireSound { get; }
    public string ProjectileStreamName { get; }
    public string LaserName { get; }
    public string LaserBoneName { get; }
    public bool ShowsAmmoPips { get; }
    public bool CapableOfFollowingWaypoints { get; }
    public bool LeechRangeWeapon { get; }
    public bool MissileCallsOnDie { get; }

    /// <summary>Per veterancy level (REGULAR, VETERAN, ELITE, HEROIC).</summary>
    public string[] FireFX { get; } = new string[4];
    /// <summary>Show the fire FX even when a stealthed shooter fires (most stealthy shooters stay hidden).</summary>
    public bool PlayFxWhenStealthed { get; }
    public string[] ProjectileDetonationFX { get; } = new string[4];
    public string[] FireOCL { get; } = new string[4];
    public string[] ProjectileDetonationOCL { get; } = new string[4];
    public string[] ProjectileExhaust { get; } = new string[4];

    // Historic bonus (e.g. Toxin General's Anthrax ammo spreading)
    public int HistoricBonusTime { get; }
    public float HistoricBonusRadius { get; }
    public int HistoricBonusCount { get; }
    public string HistoricBonusWeapon { get; }

    /// <summary>Weapon bonus lines, e.g. "VETERAN DAMAGE 125%".</summary>
    public IReadOnlyList<string> WeaponBonus { get; }

    /// <summary>This weapon's own bonuses, applied on top of the global GameData ones.</summary>
    public WeaponBonusTable ExtraBonuses { get; }

    public WeaponTemplate(string name, RuleNode rules)
    {
        Name = name;
        Rules = rules;
        string S(string key, string fallback = "") => rules.GetString(key, fallback);
        float F(string key, float fallback = 0) => RuleValue.Float(S(key), fallback);
        bool B(string key, bool fallback = false) => RuleValue.Bool(S(key), fallback);

        PrimaryDamage = F("PrimaryDamage");
        PrimaryDamageRadius = F("PrimaryDamageRadius");
        SecondaryDamage = F("SecondaryDamage");
        SecondaryDamageRadius = F("SecondaryDamageRadius");
        ShockWaveAmount = F("ShockWaveAmount");
        ShockWaveRadius = F("ShockWaveRadius");
        ShockWaveTaperOff = F("ShockWaveTaperOff");
        DamageType = S("DamageType", "EXPLOSION");
        DamageStatusType = S("DamageStatusType");
        DeathType = S("DeathType", "NORMAL");
        RadiusDamageAffects = Flags.WeaponAffects.Parse(S("RadiusDamageAffects", "ALLIES ENEMIES NEUTRALS"));
        RadiusDamageAngle = RuleUnits.Angle(S("RadiusDamageAngle"), 180);
        DamageDealtAtSelfPosition = B("DamageDealtAtSelfPosition");

        AttackRange = F("AttackRange");
        MinimumAttackRange = F("MinimumAttackRange");
        ContinueAttackRange = F("ContinueAttackRange");
        RequestAssistRange = F("RequestAssistRange");
        AcceptableAimDelta = RuleUnits.Angle(S("AcceptableAimDelta"));
        MinTargetPitch = RuleUnits.Angle(S("MinTargetPitch"), -90);
        MaxTargetPitch = RuleUnits.Angle(S("MaxTargetPitch"), 90);
        ScatterRadius = F("ScatterRadius");
        ScatterRadiusVsInfantry = F("ScatterRadiusVsInfantry");
        ScatterTargetScalar = F("ScatterTargetScalar");
        var scatterTargets = new List<System.Numerics.Vector2>();
        if (rules.TryGet("ScatterTarget", out var targetsNode))
            foreach (var entry in targetsNode.AsList())
            {
                var m = System.Text.RegularExpressions.Regex.Match(entry.Value ?? "", @"X:\s*(-?[\d.]+)\s*Y:\s*(-?[\d.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                    scatterTargets.Add(new System.Numerics.Vector2(RuleValue.Float(m.Groups[1].Value), RuleValue.Float(m.Groups[2].Value)));
            }
        ScatterTargets = scatterTargets;

        AntiGround = B("AntiGround", true);
        AntiAirborneVehicle = B("AntiAirborneVehicle");
        AntiAirborneInfantry = B("AntiAirborneInfantry");
        AntiProjectile = B("AntiProjectile");
        AntiSmallMissile = B("AntiSmallMissile");
        AntiBallisticMissile = B("AntiBallisticMissile");
        AntiMine = B("AntiMine");
        AntiParachute = B("AntiParachute");
        ProjectileCollidesWith = Flags.WeaponCollides.Parse(S("ProjectileCollidesWith", "STRUCTURES"));
        AllowAttackGarrisonedBuildings = B("AllowAttackGarrisonedBldgs");

        ClipSize = RuleValue.Int(S("ClipSize"));
        ClipReloadFrames = RuleUnits.Duration(S("ClipReloadTime"));
        string[] delay = RuleValue.Words(S("DelayBetweenShots"));
        MinDelayBetweenShots = RuleUnits.Duration(delay.ElementAtOrDefault(0));
        MaxDelayBetweenShots = delay.Length > 1 ? RuleUnits.Duration(delay[1]) : MinDelayBetweenShots;
        ShotsPerBarrel = Math.Max(1, RuleValue.Int(S("ShotsPerBarrel"), 1));
        PreAttackDelay = RuleUnits.Duration(S("PreAttackDelay"));
        PreAttackType = S("PreAttackType", "PER_SHOT");
        string reload = S("AutoReloadsClip", "YES").ToUpperInvariant();
        ReloadsAtBase = reload == "RETURN_TO_BASE";
        AutoReloadsClip = reload is not ("NO" or "RETURN_TO_BASE");
        AutoReloadWhenIdle = RuleUnits.Duration(S("AutoReloadWhenIdle"));
        ContinuousFireOne = RuleValue.Int(S("ContinuousFireOne"));
        ContinuousFireTwo = RuleValue.Int(S("ContinuousFireTwo"));
        ContinuousFireCoast = RuleUnits.Duration(S("ContinuousFireCoast"));

        ProjectileObject = S("ProjectileObject").Equals("NONE", StringComparison.OrdinalIgnoreCase) ? "" : S("ProjectileObject");
        WeaponSpeed = RuleUnits.Velocity(S("WeaponSpeed"), 999999);
        MinWeaponSpeed = RuleUnits.Velocity(S("MinWeaponSpeed"), 999999);
        ScaleWeaponSpeed = B("ScaleWeaponSpeed");
        DieOnDetonate = B("DieOnDetonate");
        WeaponRecoil = RuleUnits.Angle(S("WeaponRecoil"));
        FireSound = S("FireSound");
        ProjectileStreamName = S("ProjectileStreamName");
        LaserName = S("LaserName");
        LaserBoneName = S("LaserBoneName");
        ShowsAmmoPips = B("ShowsAmmoPips");
        CapableOfFollowingWaypoints = B("CapableOfFollowingWaypoints");
        LeechRangeWeapon = B("LeechRangeWeapon");
        MissileCallsOnDie = B("MissileCallsOnDie");

        ReadPerLevel(rules, "FireFX", "VeterancyFireFX", FireFX);
        PlayFxWhenStealthed = B("PlayFXWhenStealthed");
        ReadPerLevel(rules, "ProjectileDetonationFX", "VeterancyProjectileDetonationFX", ProjectileDetonationFX);
        ReadPerLevel(rules, "FireOCL", "VeterancyFireOCL", FireOCL);
        ReadPerLevel(rules, "ProjectileDetonationOCL", "VeterancyProjectileDetonationOCL", ProjectileDetonationOCL);
        ReadPerLevel(rules, "ProjectileExhaust", "VeterancyProjectileExhaust", ProjectileExhaust);

        HistoricBonusTime = RuleUnits.Duration(S("HistoricBonusTime"));
        HistoricBonusRadius = F("HistoricBonusRadius");
        HistoricBonusCount = RuleValue.Int(S("HistoricBonusCount"));
        HistoricBonusWeapon = S("HistoricBonusWeapon");

        WeaponBonus = rules.TryGet("WeaponBonus", out var bonus) ? bonus.AsList().Select(b => b.Value ?? "").ToList() : [];
        ExtraBonuses = new WeaponBonusTable(WeaponBonus);
    }

    /// <summary>"FireFX: X" sets every level; "VeterancyFireFX: HEROIC Y" then overrides one level.</summary>
    static void ReadPerLevel(RuleNode rules, string allLevelsKey, string perLevelKey, string[] target)
    {
        string all = rules.GetString(allLevelsKey);
        Array.Fill(target, all);
        if (!rules.TryGet(perLevelKey, out var perLevel))
            return;
        foreach (var entry in perLevel.AsList())
        {
            string[] words = RuleValue.Words(entry.Value);
            if (words.Length < 2)
                continue;
            int level = Flags.Veterancy.IndexOf(words[0]);
            if (level >= 0)
                target[level] = words[1];
        }
    }

    /// <summary>Can this weapon shoot at an object (ground vs. air vs. projectile rules)?</summary>
    public bool CanTarget(Objects.GameObject target)
    {
        if (target.IsKindOf(Kind.Projectile))
            return AntiProjectile || AntiSmallMissile && target.Template.Is("SMALL_MISSILE") || AntiBallisticMissile && target.Template.Is("BALLISTIC_MISSILE");
        if (target.Template.Is("MINE"))
            return AntiMine || AntiGround;
        if (target.IsAirborneTarget)
            return target.IsKindOf(Kind.Infantry) ? AntiAirborneInfantry || AntiParachute : AntiAirborneVehicle;
        return AntiGround;
    }

    public override string ToString() => Name;
}

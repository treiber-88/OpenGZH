using System.Numerics;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Contain;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules.SpecialPowers;

/// <summary>
/// Which special powers may be used how, and on what (the original ActionManager's
/// canDoSpecialPower... rules). The control bar uses these for its cursors; commands check them again.
/// </summary>
public static class SpecialPowerRules
{
    // Powers aimed at a point on the map (the target area must not be shrouded).
    static readonly HashSet<string> GroundTargeted = new(StringComparer.OrdinalIgnoreCase)
    {
        "SPECIAL_DAISY_CUTTER", "AIRF_SPECIAL_DAISY_CUTTER", "SPECIAL_PARADROP_AMERICA", "SPECIAL_TANK_PARADROP",
        "INFA_SPECIAL_PARADROP_AMERICA", "SPECIAL_CARPET_BOMB", "SPECIAL_CHINA_CARPET_BOMB", "SPECIAL_LEAFLET_DROP",
        "EARLY_SPECIAL_LEAFLET_DROP", "EARLY_SPECIAL_CHINA_CARPET_BOMB", "AIRF_SPECIAL_CARPET_BOMB", "SUPR_SPECIAL_CRUISE_MISSILE",
        "SPECIAL_CLUSTER_MINES", "NUKE_SPECIAL_CLUSTER_MINES", "SPECIAL_EMP_PULSE", "SPECIAL_CRATE_DROP", "SPECIAL_NAPALM_STRIKE",
        "SPECIAL_BLACK_MARKET_NUKE", "SPECIAL_ANTHRAX_BOMB", "SPECIAL_TERROR_CELL", "SPECIAL_AMBUSH", "SPECIAL_NEUTRON_MISSILE",
        "NUKE_SPECIAL_NEUTRON_MISSILE", "SUPW_SPECIAL_NEUTRON_MISSILE", "SPECIAL_SCUD_STORM", "SPECIAL_A10_THUNDERBOLT_STRIKE",
        "AIRF_SPECIAL_A10_THUNDERBOLT_STRIKE", "SPECIAL_SPECTRE_GUNSHIP", "AIRF_SPECIAL_SPECTRE_GUNSHIP", "SPECIAL_REPAIR_VEHICLES",
        "EARLY_SPECIAL_REPAIR_VEHICLES", "SPECIAL_GPS_SCRAMBLER", "SLTH_SPECIAL_GPS_SCRAMBLER", "SPECIAL_ARTILLERY_BARRAGE",
        "SPECIAL_FRENZY", "EARLY_SPECIAL_FRENZY", "SPECIAL_PARTICLE_UPLINK_CANNON", "SUPW_SPECIAL_PARTICLE_UPLINK_CANNON",
        "LAZR_SPECIAL_PARTICLE_UPLINK_CANNON", "SPECIAL_CLEANUP_AREA", "SPECIAL_SNEAK_ATTACK", "SPECIAL_BATTLESHIP_BOMBARDMENT",
    };

    // Ground-targeted powers that may be aimed anywhere on the map, even into the shroud.
    static readonly HashSet<string> AnywhereOnMap = new(StringComparer.OrdinalIgnoreCase)
    {
        "SPECIAL_SPY_SATELLITE", "SPECIAL_RADAR_VAN_SCAN", "SPECIAL_SPY_DRONE", "SPECIAL_HELIX_NAPALM_BOMB",
    };

    // Drops that can't land in water.
    static readonly HashSet<string> NotOnWater = new(StringComparer.OrdinalIgnoreCase)
    {
        "SPECIAL_PARADROP_AMERICA", "INFA_SPECIAL_PARADROP_AMERICA", "SPECIAL_CRATE_DROP", "SPECIAL_TANK_PARADROP",
    };

    // Powers used with no target at all.
    static readonly HashSet<string> NoTarget = new(StringComparer.OrdinalIgnoreCase)
    {
        "SPECIAL_REMOTE_CHARGES", "SPECIAL_CIA_INTELLIGENCE", "SPECIAL_COMMUNICATIONS_DOWNLOAD", "SPECIAL_DETONATE_DIRTY_NUKE",
        "SPECIAL_CHANGE_BATTLE_PLANS", "SPECIAL_LAUNCH_BAIKONUR_ROCKET",
    };

    /// <summary>The object's module for this power, if it has one.</summary>
    public static SpecialPowerModule? ModuleFor(GameObject obj, SpecialPowerTemplate power) =>
        obj.FindModules<SpecialPowerModule>().FirstOrDefault(m => m.Power == power);

    /// <summary>The object's module for any power of this type (powers of one type can have several templates).</summary>
    public static SpecialPowerModule? ModuleOfType(GameObject obj, string type) =>
        obj.FindModules<SpecialPowerModule>().FirstOrDefault(m => m.Power != null && m.Power.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    public static SpecialAbilityUpdate? AbilityOfType(GameObject obj, string type) =>
        obj.FindModules<SpecialAbilityUpdate>().FirstOrDefault(m => m.Power != null && m.Power.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    /// <summary>The object has the module, the player has the science, and (if asked) it is charged.</summary>
    static bool SourceReady(GameObject obj, SpecialPowerTemplate power, bool checkSource, out SpecialPowerModule? module)
    {
        module = ModuleFor(obj, power);
        if (module == null)
            return false;
        if (power.RequiredScience.Length > 0 && !obj.Owner.HasScience(power.RequiredScience))
            return false;
        return !checkSource || module.PercentReady >= 1f;
    }

    /// <summary>Until the shroud exists every point counts as explored; this is where the shroud plugs in.</summary>
    static bool IsShrouded(Player player, Vector3 position) => false;

    static bool IsShroudedForAction(GameObject source, GameObject target, CommandSource commandSource) =>
        source.Owner.IsHuman && commandSource != CommandSource.Script && IsShrouded(source.Owner, target.Position);

    public static bool CanDoSpecialPower(GameObject obj, SpecialPowerTemplate power, CommandSource source, bool checkSource = true) =>
        SourceReady(obj, power, checkSource, out _) && NoTarget.Contains(power.Type);

    public static bool CanDoSpecialPowerAtLocation(GameObject obj, Vector3 location, CommandSource source, SpecialPowerTemplate power, bool checkSource = true)
    {
        if (!SourceReady(obj, power, checkSource, out _))
            return false;
        if (NotOnWater.Contains(power.Type) && obj.Game.Terrain.IsUnderwater(location.X, location.Y, out _))
            return false;
        if (GroundTargeted.Contains(power.Type))
            return !IsShrouded(obj.Owner, location);
        if (AnywhereOnMap.Contains(power.Type))
            return obj.Game.Terrain.IsInsidePlayableArea(location.X, location.Y);
        return power.IsType("SPECIAL_LAUNCH_BAIKONUR_ROCKET");
    }

    public static bool CanDoSpecialPowerAtObject(GameObject obj, GameObject target, CommandSource source, SpecialPowerTemplate power, bool checkSource = true)
    {
        if (!SourceReady(obj, power, checkSource, out _) || target.IsEffectivelyDead)
            return false;
        if (IsShroudedForAction(obj, target, source))
            return false;
        var relation = obj.Owner.RelationshipTo(target.Owner);
        bool enemy = relation == Relationship.Enemies;

        switch (power.Type.ToUpperInvariant())
        {
            case "SPECIAL_BATTLESHIP_BOMBARDMENT":
                return relation != Relationship.Allies;
            case "SPECIAL_TANKHUNTER_TNT_ATTACK":
                return target.IsKindOf(Kind.Structure) || target.IsKindOf("VEHICLE") && !target.IsKindOf("AIRCRAFT");
            case "SPECIAL_BOOBY_TRAP":
                return target.IsKindOf(Kind.Structure) && relation != Relationship.Enemies;
            case "SPECIAL_MISSILE_DEFENDER_LASER_GUIDED_MISSILES":
                return target.IsKindOf("VEHICLE") && enemy;
            case "SPECIAL_HACKER_DISABLE_BUILDING":
                return target.IsKindOf(Kind.Structure) && enemy && target.IsKindOf("CAPTURABLE") && !target.IsKindOf("REBUILD_HOLE");
            case "SPECIAL_INFANTRY_CAPTURE_BUILDING":
            case "SPECIAL_BLACKLOTUS_CAPTURE_BUILDING":
                return CanCaptureBuilding(obj, target, source);
            case "SPECIAL_BLACKLOTUS_DISABLE_VEHICLE_HACK":
                return CanDisableVehicleViaHacking(obj, target, source);
            case "SPECIAL_BLACKLOTUS_STEAL_CASH_HACK":
                return CanStealCashViaHacking(obj, target, source);
            case "SPECIAL_CASH_HACK":
                return target.IsKindOf(Kind.Structure) && enemy && target.IsKindOf("CAPTURABLE") && !target.IsKindOf("REBUILD_HOLE")
                    && !target.IsUnderConstruction && target.IsKindOf("CASH_GENERATOR");
            case "SPECIAL_DISGUISE_AS_VEHICLE":
                return target.IsKindOf("VEHICLE") && !target.IsKindOf("AIRCRAFT") && !target.IsKindOf("BOAT") && !target.IsKindOf("CLIFF_JUMPER")
                    && target.Template.FindModule("RailroadBehavior") == null;
            case "SPECIAL_DEFECTOR":
                return !target.IsKindOf(Kind.Structure) && enemy;
            case "SPECIAL_REMOTE_CHARGES":
            case "SPECIAL_TIMED_CHARGES":
            case "SPECIAL_HELIX_NAPALM_BOMB":
            {
                if (target.IsKindOf(Kind.Bridge) || target.IsKindOf("BRIDGE_TOWER"))
                    return false;
                if (!target.IsKindOf(Kind.Structure) && !target.IsKindOf("VEHICLE"))
                    return false;
                if (AbilityOfType(obj, power.Type) is not { } ability || ability.SpecialObjectCount >= ability.SpecialObjectMax)
                    return false;
                if (ability.FindSpecialObjectOn(target) != null)
                    return false;
                // One charge per target, whether timed or remote.
                string otherType = power.IsType("SPECIAL_REMOTE_CHARGES") ? "SPECIAL_TIMED_CHARGES" : power.IsType("SPECIAL_TIMED_CHARGES") ? "SPECIAL_REMOTE_CHARGES" : "";
                return otherType.Length == 0 || AbilityOfType(obj, otherType)?.FindSpecialObjectOn(target) == null;
            }
        }
        return false;
    }

    public static bool CanCaptureBuilding(GameObject obj, GameObject target, CommandSource source)
    {
        var module = ModuleOfType(obj, "SPECIAL_INFANTRY_CAPTURE_BUILDING") ?? ModuleOfType(obj, "SPECIAL_BLACKLOTUS_CAPTURE_BUILDING");
        if (module == null || module.PercentReady < 1f)
            return false;
        if (target.IsKindOf("IMMUNE_TO_CAPTURE") || target.IsEffectivelyDead || !target.IsKindOf(Kind.Structure))
            return false;
        if (target.IsUnderConstruction || target.HasStatus(Status.Sold))
            return false;
        if (IsShroudedForAction(obj, target, source))
            return false;
        var relation = obj.Owner.RelationshipTo(target.Owner);
        if (!(relation == Relationship.Enemies || target.IsKindOf("CAPTURABLE") && relation != Relationship.Allies))
            return false;
        // An occupied garrison can only be captured by clearing it out: not with friendly units inside.
        if (target.FindModule<ContainModule>() is { IsGarrisonable: true } garrison && garrison.Passengers.Any(p => p.Owner.IsAllyOf(obj.Owner)))
            return false;
        return true;
    }

    public static bool CanDisableVehicleViaHacking(GameObject obj, GameObject target, CommandSource source, bool checkSource = true)
    {
        var module = ModuleOfType(obj, "SPECIAL_BLACKLOTUS_DISABLE_VEHICLE_HACK");
        if (checkSource && (module == null || module.PercentReady < 1f))
            return false;
        if (target.IsEffectivelyDead || target.IsKindOf("AIRCRAFT") || target.IsAirborneTarget)
            return false;
        if (IsShroudedForAction(obj, target, source))
            return false;
        if (!obj.Owner.IsEnemyOf(target.Owner) || !target.IsKindOf("VEHICLE"))
            return false;
        if (target.HasStatus(Status.Stealthed) && !target.HasStatus(Status.Detected) && !target.HasStatus(Status.Disguised))
            return false;
        return !AppearsToContainFriendlies(obj, target);
    }

    public static bool CanStealCashViaHacking(GameObject obj, GameObject target, CommandSource source)
    {
        var module = ModuleOfType(obj, "SPECIAL_BLACKLOTUS_STEAL_CASH_HACK");
        if (module == null || module.PercentReady < 1f)
            return false;
        if (target.IsEffectivelyDead || target.IsUnderConstruction)
            return false;
        if (IsShroudedForAction(obj, target, source))
            return false;
        if (!obj.Owner.IsEnemyOf(target.Owner) || !target.IsKindOf("CASH_GENERATOR"))
            return false;
        if (!target.IsKindOf("CAPTURABLE") || target.IsKindOf("REBUILD_HOLE"))
            return false;
        return !(target.HasStatus(Status.Stealthed) && !target.HasStatus(Status.Detected) && !target.HasStatus(Status.Disguised));
    }

    /// <summary>A transport that holds the hacker's own side's units shouldn't be hacked.</summary>
    static bool AppearsToContainFriendlies(GameObject obj, GameObject target) =>
        target.FindModule<ContainModule>() is { } contain && contain.Passengers.Any(p => p.Owner.IsAllyOf(obj.Owner));
}

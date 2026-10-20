using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Locomotion;
using OpenGZH.Logic.Modules.Contain;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Modules.SpecialPowers;

/// <summary>
/// A power that runs an object creation list: airstrikes, paradrops, the A-10, artillery barrages,
/// the Spectre Gunship, superweapons' missiles. CreateLocation says where the objects appear; the
/// OCL's DeliverPayload nuggets then fly them to the target. UpgradeOCL lines ("SCIENCE_X OCL_Y")
/// replace the OCL once the player has the science (e.g. bigger paradrops at higher ranks).
/// </summary>
public sealed class OCLSpecialPower : SpecialPowerModule
{
    const float CreateAboveLocationHeight = 300;
    const int MaxAdjustRadiusCells = 50; // 500 world units, like the original

    string FindOcl()
    {
        foreach (string line in ListSetting("UpgradeOCL"))
        {
            string[] words = RuleValue.Words(line);
            if (words.Length >= 2 && Owner.Owner.HasScience(words[0]))
                return words[1];
        }
        return Setting("OCL");
    }

    public override void DoSpecialPowerAtLocation(Vector3 location, float angle, uint options)
    {
        if (Owner.IsDisabled)
            return;
        var target = location;
        if (BoolSetting("OCLAdjustPositionToPassable") && Game.Pathfinder.NearestPassable(location, Surfaces.Ground, MaxAdjustRadiusCells) is { } passable)
            target = passable;

        base.DoSpecialPowerAtLocation(target, angle, options);

        string ocl = FindOcl();
        switch (Setting("CreateLocation", "CREATE_AT_EDGE_NEAR_SOURCE").ToUpperInvariant())
        {
            case "CREATE_AT_EDGE_NEAR_TARGET":
                Game.ObjectCreation.Run(ocl, Owner, Game.Terrain.ClosestEdgePoint(target), secondary: target);
                break;
            case "CREATE_AT_EDGE_FARTHEST_FROM_TARGET":
                Game.ObjectCreation.Run(ocl, Owner, Game.Terrain.FarthestEdgePoint(target) + new Vector3(0, 0, CreateAboveLocationHeight), secondary: target);
                break;
            case "CREATE_AT_LOCATION":
                Game.ObjectCreation.Run(ocl, Owner, target, secondary: target);
                break;
            case "USE_OWNER_OBJECT":
                Game.ObjectCreation.Run(ocl, Owner, target, secondary: target, createOwner: false);
                break;
            case "CREATE_ABOVE_LOCATION":
                Game.ObjectCreation.Run(ocl, Owner, target + new Vector3(0, 0, CreateAboveLocationHeight), secondary: target);
                break;
            default: // CREATE_AT_EDGE_NEAR_SOURCE
                Game.ObjectCreation.Run(ocl, Owner, Game.Terrain.ClosestEdgePoint(Owner.Position), secondary: target);
                break;
        }
    }

    public override void DoSpecialPowerAtObject(GameObject target, uint options)
    {
        if (!Owner.IsDisabled)
            DoSpecialPowerAtLocation(target.Position, float.NaN, options);
    }

    public override void DoSpecialPower(uint options)
    {
        if (Owner.IsDisabled)
            return;
        var position = Owner.Position;
        base.DoSpecialPowerAtLocation(position, float.NaN, options);
        Game.ObjectCreation.Run(FindOcl(), Owner, position, secondary: position);
    }
}

/// <summary>GLA's Cash Bounty: while the player has the science, every kill pays a share of the victim's cost.</summary>
public sealed class CashBountyPower : SpecialPowerModule
{
    public override void OnCreate()
    {
        base.OnCreate();
        if (Power != null && Owner.Owner.HasScience(Power.RequiredScience))
            RaiseBounty(Owner.Owner);
    }

    public override void OnSpecialPowerCreation()
    {
        base.OnSpecialPowerCreation();
        RaiseBounty(Owner.Owner);
    }

    void RaiseBounty(Player player)
    {
        float bounty = FloatSetting("Bounty");
        if (bounty > player.CashBounty)
            player.CashBounty = bounty;
    }
}

/// <summary>Sends the object (an Ambulance) to clean up toxins and radiation around a point.</summary>
public sealed class CleanupAreaPower : SpecialPowerModule
{
    public override void DoSpecialPowerAtLocation(Vector3 location, float angle, uint options)
    {
        if (Owner.IsDisabled)
            return;
        if (Owner.FindModule<Updates.CleanupHazardUpdate>() is { } cleanup)
            cleanup.SetCleanupArea(location, FloatSetting("MaxMoveDistanceFromLocation"));
        else
            Log.Warning($"{Owner.Template.Name} uses CleanupAreaPower but has no CleanupHazardUpdate.");
    }
}

/// <summary>Makes the object fire a volley (MaxShotsToFire) with full clips, e.g. a superweapon building's weapon.</summary>
public sealed class FireWeaponPower : SpecialPowerModule
{
    int MaxShots => IntSetting("MaxShotsToFire", 1);

    public override void DoSpecialPower(uint options)
    {
        if (Owner.IsDisabled)
            return;
        base.DoSpecialPower(options);
        Fire(null, Owner.Position);
    }

    public override void DoSpecialPowerAtLocation(Vector3 location, float angle, uint options)
    {
        if (Owner.IsDisabled)
            return;
        base.DoSpecialPowerAtLocation(location, angle, options);
        Fire(null, location);
    }

    public override void DoSpecialPowerAtObject(GameObject target, uint options)
    {
        if (Owner.IsDisabled)
            return;
        base.DoSpecialPowerAtObject(target, options);
        Fire(target, target.Position);
    }

    void Fire(GameObject? target, Vector3 position)
    {
        Owner.Weapons.ReloadAll();
        if (Owner.AI is not { } ai)
            return;
        if (target != null)
            ai.AttackObject(target, CommandSource.AI, MaxShots);
        else
            ai.AttackPosition(position, CommandSource.AI, MaxShots);
    }
}

/// <summary>
/// The Baikonur rocket in the campaign: with no target it opens the launch doors; with a target it
/// creates the DetonationObject there.
/// </summary>
public sealed class BaikonurLaunchPower : SpecialPowerModule
{
    public override void DoSpecialPower(uint options)
    {
        if (Owner.IsDisabled)
            return;
        base.DoSpecialPower(options);
        Owner.SetCondition("DOOR_1_OPENING");
    }

    public override void DoSpecialPowerAtLocation(Vector3 location, float angle, uint options)
    {
        if (Owner.IsDisabled)
            return;
        base.DoSpecialPowerAtLocation(location, angle, options);
        Game.CreateObject(Setting("DetonationObject"), Owner.Owner, location, team: Owner.Team);
    }
}

/// <summary>China's Hacker / Black Lotus cash hack: takes MoneyAmount (more with UpgradeMoneyAmount sciences) from the target's owner.</summary>
public sealed class CashHackSpecialPower : SpecialPowerModule
{
    int AmountToSteal()
    {
        foreach (string line in ListSetting("UpgradeMoneyAmount"))
        {
            string[] words = RuleValue.Words(line);
            if (words.Length >= 2 && Owner.Owner.HasScience(words[0]))
                return RuleValue.Int(words[1]);
        }
        return IntSetting("MoneyAmount");
    }

    public override void DoSpecialPowerAtLocation(Vector3 location, float angle, uint options) { }

    public override void DoSpecialPowerAtObject(GameObject victim, uint options)
    {
        if (Owner.IsDisabled)
            return;
        base.DoSpecialPowerAtObject(victim, options);
        int cash = Math.Min(AmountToSteal(), victim.Owner.Money.Amount);
        if (cash <= 0)
            return;
        victim.Owner.Money.TryWithdraw(cash);
        Owner.Owner.Money.Deposit(cash);
        Owner.Owner.MoneyEarned += cash;
        Game.Events.MoneyEarned(Owner, cash);
        Game.Events.MoneyEarned(victim, -cash);
    }
}

/// <summary>Makes an enemy unit defect to this player (not used by Zero Hour's factions, kept for mods and maps).</summary>
public sealed class DefectorSpecialPower : SpecialPowerModule
{
    public override void DoSpecialPowerAtLocation(Vector3 location, float angle, uint options) { }

    public override void DoSpecialPowerAtObject(GameObject target, uint options)
    {
        if (Owner.IsDisabled)
            return;
        base.DoSpecialPowerAtObject(target, options);
        target.Defect(Owner.Owner.DefaultTeam, Power?.DetectionFrames ?? 0);
    }
}

/// <summary>
/// Spy vision (e.g. a captured building that reveals the enemy): lasts BaseDuration, plus
/// BonusDurationPerCaptured for each object inside, up to MaxDuration. SpyVisionUpdate does the revealing.
/// </summary>
public sealed class SpyVisionSpecialPower : SpecialPowerModule
{
    public override void DoSpecialPower(uint options)
    {
        if (Owner.IsDisabled)
            return;
        base.DoSpecialPower(options);
        int duration = FramesSetting("BaseDuration");
        if (Owner.FindModule<ContainModule>() is { } contain)
            duration = Math.Min(duration + contain.Count * FramesSetting("BonusDurationPerCaptured"), FramesSetting("MaxDuration"));
        Owner.FindModule<Updates.SpyVisionUpdate>()?.Activate(duration);
    }
}

using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Players;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>The USA Strategy Center's battle plans.</summary>
public class BattlePlanTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static Vector3 Ground(GameLogic game, Vector3 p) => p with { Z = game.Terrain.GroundHeight(p) };

    const uint OptionTwo = 1u << 14, OptionThree = 1u << 15;

    [Fact]
    public void SpectreGunshipOrbitsShootsAndLeaves()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        var cc = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter");
        alice.AddScience("SCIENCE_SpectreGunshipSolo");
        var targetSpot = Ground(game, cc.Position + new Vector3(500, 300, 0));
        var tank = game.CreateObject("GLATankScorpion", bob, targetSpot)!;
        game.Update();
        float tankHealth = tank.Health;
        var power = cc.FindModules<Logic.Modules.SpecialPowers.SpecialPowerModule>().First(m => m.Power?.Name == "SuperweaponSpectreGunship");
        power.SetReadyFrame(game.Frame);
        power.DoSpecialPowerAtLocation(targetSpot, float.NaN, 0);
        game.Update();
        var gunship = game.Objects.FirstOrDefault(o => o.Template.Name == "AmericaJetSpectreGunship");
        Assert.NotNull(gunship);
        uint gunshipId = gunship!.Id;
        float closest = float.MaxValue;
        bool gone = false;
        for (int i = 0; i < 30 * 120 && !gone; i++)
        {
            game.Update();
            if (game.FindObject(gunshipId) is { } g)
                closest = Math.Min(closest, Logic.Core.MathUtil.Distance2D(g.Position, targetSpot));
            else
                gone = true;
        }
        output.WriteLine($"closest approach {closest:F0}, tank {tankHealth} -> {(tank.IsEffectivelyDead ? 0 : tank.Health)}, gunship gone {gone}");
        Assert.True(closest < 400, "came to orbit the target");
        Assert.True(tank.IsEffectivelyDead || tank.Health < tankHealth, "shot the tank");
        Assert.True(gone, "left the map afterwards");
    }

    [Fact]
    public void HoldTheLineToughensTheArmyAndSwitchingTakesItBack()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var cc = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter");
        var center = game.CreateObject("AmericaStrategyCenter", alice, Ground(game, cc.Position + new Vector3(300, 0, 0)))!;
        game.TreatAsBuilt(center);
        var ranger = game.CreateObject("AmericaInfantryRanger", alice, Ground(game, cc.Position + new Vector3(0, 250, 0)))!;
        game.Update();
        float before = ranger.Body!.DamageScalar;
        var plan = center.FindModule<Logic.Modules.SpecialPowers.BattlePlanUpdate>()!;
        var power = center.FindModules<Logic.Modules.SpecialPowers.SpecialPowerModule>().First(m => m.Power?.Name == "SpecialAbilityChangeBattlePlans");

        power.DoSpecialPower(OptionTwo);
        for (int i = 0; i < 30 * 10 && plan.ActiveBattlePlan != BattlePlanStatus.HoldTheLine; i++)
            game.Update();
        output.WriteLine($"active {plan.ActiveBattlePlan}, ranger armor scalar {before} -> {ranger.Body.DamageScalar}, plans {alice.NumBattlePlansActive}");
        Assert.Equal(BattlePlanStatus.HoldTheLine, plan.ActiveBattlePlan);
        Assert.True(ranger.Body.DamageScalar < before, "takes less damage");
        Assert.True(ranger.WeaponBonusFlags.Has(Logic.Core.Flags.WeaponBonusCondition.IndexOf("BATTLEPLAN_HOLDTHELINE")));

        // A unit built now gets the bonus too.
        var late = game.CreateObject("AmericaInfantryRanger", alice, Ground(game, cc.Position + new Vector3(30, 250, 0)))!;
        Assert.Equal(ranger.Body.DamageScalar, late.Body!.DamageScalar, 3);

        power.DoSpecialPower(OptionThree);
        bool paralyzed = false;
        for (int i = 0; i < 30 * 20 && plan.ActiveBattlePlan != BattlePlanStatus.SearchAndDestroy; i++)
        {
            game.Update();
            paralyzed |= ranger.IsDisabledBy("DISABLED_PARALYZED");
        }
        output.WriteLine($"active {plan.ActiveBattlePlan}, armor {ranger.Body.DamageScalar}, paralyzed during change {paralyzed}, vision {ranger.VisionRange}");
        Assert.Equal(BattlePlanStatus.SearchAndDestroy, plan.ActiveBattlePlan);
        Assert.Equal(before, ranger.Body.DamageScalar, 3);
        Assert.True(paralyzed, "the army is paralyzed while the plan changes");
        Assert.False(ranger.WeaponBonusFlags.Has(Logic.Core.Flags.WeaponBonusCondition.IndexOf("BATTLEPLAN_HOLDTHELINE")));
        Assert.True(ranger.WeaponBonusFlags.Has(Logic.Core.Flags.WeaponBonusCondition.IndexOf("BATTLEPLAN_SEARCHANDDESTROY")));
    }
}

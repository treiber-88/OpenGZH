using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Crates, and the units that work like crates: hijackers, terrorists, saboteurs.</summary>
public class CrateTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static Vector3 Ground(GameLogic game, Vector3 p) => p with { Z = game.Terrain.GroundHeight(p) };

    static (GameLogic, Logic.Players.Player Alice, Logic.Players.Player Bob, Vector3) Setup(BaseGameFixture content)
    {
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        var spot = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter").Position + new Vector3(220, 0, 0);
        spot = game.Pathfinder.NearestPassable(spot, Logic.Locomotion.Surfaces.Ground) ?? spot;
        return (game, alice, bob, Ground(game, spot));
    }

    static void RunUntil(GameLogic game, Func<bool> done, int maxFrames)
    {
        for (int i = 0; i < maxFrames && !done(); i++)
            game.Update();
    }

    [Fact]
    public void DrivingOverAMoneyCratePaysOut()
    {
        if (!content.Available) return;
        var (game, alice, _, spot) = Setup(content);
        var crate = game.CreateObject("1000DollarCrate", game.Players.Neutral, Ground(game, spot + new Vector3(80, 0, 0)))!;
        var humvee = game.CreateObject("AmericaVehicleHumvee", alice, spot)!;
        int before = alice.Money.Amount;
        game.Commands.Enqueue(new MoveCommand(alice.Index, [humvee.Id], Ground(game, spot + new Vector3(160, 0, 0))));
        RunUntil(game, () => crate.IsDestroyed, 30 * 10);
        output.WriteLine($"money {before} -> {alice.Money.Amount}, crate gone={crate.IsDestroyed}");
        Assert.True(crate.IsDestroyed);
        Assert.Equal(before + 1000, alice.Money.Amount);
    }

    [Fact]
    public void HijackerStealsAVehicleAndRidesInIt()
    {
        if (!content.Available) return;
        var (game, alice, bob, spot) = Setup(content);
        var tank = game.CreateObject("AmericaTankCrusader", alice, spot)!;
        var hijacker = game.CreateObject("GLAInfantryHijacker", bob, Ground(game, spot + new Vector3(90, 30, 0)))!;
        game.Commands.Enqueue(new EnterCommand(bob.Index, [hijacker.Id], tank.Id));
        RunUntil(game, () => tank.Owner == bob, 30 * 15);
        output.WriteLine($"tank owner {tank.Owner}, hijacker hidden={hijacker.DrawableHidden} at {hijacker.Position}");
        Assert.Equal(bob, tank.Owner);
        Assert.True(tank.HasStatus("HIJACKED"));
        Assert.False(hijacker.IsDestroyed, "the Crusader has a pilot to throw out, so he rides along");
        Assert.True(hijacker.DrawableHidden);

        game.Commands.Enqueue(new MoveCommand(bob.Index, [tank.Id], Ground(game, spot + new Vector3(0, 150, 0))));
        RunUntil(game, () => false, 30 * 4);
        Assert.True(MathUtil.Distance2D(hijacker.Position, tank.Position) < 1, "he moves with the tank");

        tank.Kill();
        RunUntil(game, () => !hijacker.DrawableHidden, 30 * 15);
        output.WriteLine($"after the tank died: hijacker hidden={hijacker.DrawableHidden} dead={hijacker.IsEffectivelyDead}");
        Assert.False(hijacker.DrawableHidden, "and gets out when it is destroyed");
        Assert.False(hijacker.IsEffectivelyDead);
    }

    [Fact]
    public void SnipedVehicleCanBeTakenByAnyInfantry()
    {
        if (!content.Available) return;
        var (game, alice, bob, spot) = Setup(content);
        var humvee = game.CreateObject("AmericaVehicleHumvee", alice, spot)!;
        humvee.Damage(new Logic.Combat.DamageInfo { DamageType = "KILL_PILOT", Amount = 100 });
        Assert.True(humvee.IsDisabledBy("DISABLED_UNMANNED"));
        Assert.Equal(game.Players.Neutral, humvee.Owner);

        var rebel = game.CreateObject("GLAInfantryRebel", bob, Ground(game, spot + new Vector3(70, 20, 0)))!;
        game.Commands.Enqueue(new EnterCommand(bob.Index, [rebel.Id], humvee.Id));
        RunUntil(game, () => humvee.Owner == bob, 30 * 15);
        output.WriteLine($"humvee owner {humvee.Owner}, unmanned={humvee.IsDisabledBy("DISABLED_UNMANNED")}, rebel gone={rebel.IsDestroyed}");
        Assert.Equal(bob, humvee.Owner);
        Assert.False(humvee.IsDisabledBy("DISABLED_UNMANNED"));
        Assert.True(rebel.IsDestroyed, "the Rebel became its driver");
    }

    [Fact]
    public void DestroyedTankEjectsItsPilot()
    {
        if (!content.Available) return;
        var (game, alice, _, spot) = Setup(content);
        var tank = game.CreateObject("AmericaTankCrusader", alice, spot)!;
        // Only veteran (or better) crews survive to bail out.
        tank.SetVeterancy(Logic.Objects.VeterancyLevel.Veteran);
        int before = game.ObjectsOwnedBy(alice).Count(o => o.Template.Name.Contains("Pilot"));
        tank.Kill();
        for (int i = 0; i < 30 * 3; i++)
            game.Update();
        int after = game.ObjectsOwnedBy(alice).Count(o => o.Template.Name.Contains("Pilot"));
        output.WriteLine($"pilots {before} -> {after}");
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public void MinesRingTheBuildingAndHurtIntruders()
    {
        if (!content.Available) return;
        var (game, alice, bob, spot) = Setup(content);
        var cc = game.CreateObject("ChinaCommandCenter", bob, spot)!;
        game.Update();
        bob.CompleteUpgrade("Upgrade_ChinaMines");
        game.Update();
        var mines = game.ObjectsOwnedBy(bob).Where(o => o.Template.Name == "ChinaStandardMine").ToList();
        output.WriteLine($"{mines.Count} mines laid, nearest {mines.Select(m => MathUtil.Distance2D(m.Position, cc.Position)).DefaultIfEmpty().Min():F0}, farthest {mines.Select(m => MathUtil.Distance2D(m.Position, cc.Position)).DefaultIfEmpty().Max():F0}");
        Assert.True(mines.Count > 10);

        var tank = game.CreateObject("AmericaTankCrusader", alice, Ground(game, cc.Position + new Vector3(cc.BoundingCircleRadius + 120, 0, 0)))!;
        float health = tank.Health;
        game.Commands.Enqueue(new MoveCommand(alice.Index, [tank.Id], Ground(game, cc.Position + new Vector3(cc.BoundingCircleRadius + 5, 0, 0))));
        RunUntil(game, () => tank.Health < health, 30 * 15);
        output.WriteLine($"tank health {health} -> {tank.Health}, mines left {game.ObjectsOwnedBy(bob).Count(o => o.Template.Name == "ChinaStandardMine")}");
        Assert.True(tank.Health < health, "the tank set off a mine");
    }

    [Fact]
    public void SaboteurBlacksOutTheEnemyBase()
    {
        if (!content.Available) return;
        var (game, alice, bob, spot) = Setup(content);
        var plant = game.CreateObject("AmericaPowerPlant", alice, spot)!;
        var saboteur = game.CreateObject("GLAInfantrySaboteur", bob, Ground(game, spot + new Vector3(plant.BoundingCircleRadius + 60, 0, 0)))!;
        game.Update();
        Assert.True(alice.Energy.HasSufficientPower);
        game.Commands.Enqueue(new EnterCommand(bob.Index, [saboteur.Id], plant.Id));
        RunUntil(game, () => saboteur.IsDestroyed, 30 * 15);
        output.WriteLine($"saboteur used={saboteur.IsDestroyed}, alice power={alice.Energy.HasSufficientPower}");
        Assert.True(saboteur.IsDestroyed, "the saboteur is used up");
        Assert.False(alice.Energy.HasSufficientPower, "the power goes out");
        var cc = game.ObjectsOwnedBy(alice).FirstOrDefault(o => o.IsKindOf("POWERED"));
        if (cc != null)
            Assert.True(cc.IsDisabledBy("DISABLED_UNDERPOWERED"), $"{cc.Template.Name} powers down");
    }
}

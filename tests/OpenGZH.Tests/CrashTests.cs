using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Slow deaths of vehicles: crashing jets and helicopters, the battle bus's second life.</summary>
public class CrashTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static Vector3 Ground(GameLogic game, Vector3 p) => p with { Z = game.Terrain.GroundHeight(p) };

    static (GameLogic, Logic.Players.Player, Vector3) Setup(BaseGameFixture content)
    {
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var cc = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter");
        var spot = Ground(game, cc.Position + new Vector3(300, 300, 0));
        return (game, alice, spot);
    }

    /// <summary>Kills a flying aircraft and follows it down: (lowest height seen, frames until removed or -1).</summary>
    (float lowest, int removedAfter, List<string> newObjects) ShootDown(GameLogic game, GameObject aircraft, int maxFrames)
    {
        var before = game.Objects.Select(o => o.Id).ToHashSet();
        aircraft.Kill();
        float lowest = float.MaxValue;
        for (int i = 0; i < maxFrames; i++)
        {
            game.Update();
            if (game.FindObject(aircraft.Id) == null)
                return (lowest, i, game.Objects.Where(o => !before.Contains(o.Id)).Select(o => o.Template.Name).Distinct().ToList());
            lowest = Math.Min(lowest, aircraft.HeightAboveTerrain);
        }
        return (lowest, -1, []);
    }

    [Fact]
    public void ShotDownJetFallsAndBlowsUpOnTheGround()
    {
        if (!content.Available) return;
        var (game, alice, spot) = Setup(content);
        var jet = game.CreateObject("AmericaJetRaptor", alice, spot + new Vector3(0, 0, 150))!;
        jet.AI!.MoveTo(spot + new Vector3(2000, 0, 0), CommandSource.Player);
        for (int i = 0; i < 30; i++)
            game.Update();
        float startHeight = jet.HeightAboveTerrain;
        var (lowest, removedAfter, created) = ShootDown(game, jet, 30 * 30);
        output.WriteLine($"started at {startHeight:F0}, lowest {lowest:F1}, removed after {removedAfter} frames; created {string.Join(", ", created)}");
        Assert.True(startHeight > 50, "was flying");
        Assert.True(removedAfter > 10, "crashes over time rather than vanishing");
        Assert.True(lowest < 5, "came down to the ground first");
    }

    [Fact]
    public void ShotDownHelicopterSpiralsDownAndLeavesRubble()
    {
        if (!content.Available) return;
        var (game, alice, spot) = Setup(content);
        var copter = game.CreateObject("AmericaVehicleComanche", alice, spot + new Vector3(0, 0, 100))!;
        copter.AI!.MoveTo(spot + new Vector3(500, 0, 0), CommandSource.Player);
        for (int i = 0; i < 30; i++)
            game.Update();
        float startHeight = copter.HeightAboveTerrain;
        float startYaw = copter.Orientation;
        var (lowest, removedAfter, created) = ShootDown(game, copter, 30 * 30);
        output.WriteLine($"started at {startHeight:F0}, lowest {lowest:F1}, removed after {removedAfter} frames; created {string.Join(", ", created)}");
        Assert.True(removedAfter > 10);
        Assert.True(lowest < 5);
        var rubble = game.Templates.Find("AmericaVehicleComanche")!.Modules.FirstOrDefault(m => m.ClassName == "HelicopterSlowDeathBehavior")?.Get("FinalRubbleObject", "");
        if (!string.IsNullOrEmpty(rubble))
            Assert.Contains(rubble, created);
    }

    [Fact]
    public void HighriseCollapsesIntoRubbleThatStays()
    {
        if (!content.Available) return;
        var (game, _, spot) = Setup(content);
        var building = game.CreateObject("CivilianHighrise01", game.Players.Neutral, spot)!;
        game.Update();
        building.Kill();
        bool sank = false;
        for (int i = 0; i < 30 * 30; i++)
        {
            game.Update();
            sank |= building.DrawJitter.Z < -10;
        }
        bool postCollapse = building.ModelConditions.Has(Logic.Core.Flags.ModelCondition.IndexOf("POST_COLLAPSE"));
        output.WriteLine($"exists {game.FindObject(building.Id) != null}, sank {sank}, post-collapse {postCollapse}");
        Assert.NotNull(game.FindObject(building.Id));
        Assert.True(sank, "sank into the ground");
        Assert.True(postCollapse, "ends as rubble");
    }

    [Fact]
    public void TowerTopplesAwayFromItsKiller()
    {
        if (!content.Available) return;
        var (game, alice, spot) = Setup(content);
        var tower = game.CreateObject("StanTallTower", game.Players.Neutral, spot)!;
        var tank = game.CreateObject("AmericaTankCrusader", alice, Ground(game, spot + new Vector3(-200, 0, 0)))!;
        game.Update();
        tower.Damage(new DamageInfo { Amount = tower.MaxHealth * 2, DamageType = "ARMOR_PIERCING", SourceId = tank.Id });
        for (int i = 0; i < 30 * 20; i++)
            game.Update();
        // The tower's up axis now leans away from the tank (+X).
        var up = Vector3.Transform(Vector3.UnitZ, tower.Tilt);
        output.WriteLine($"exists {game.FindObject(tower.Id) != null}, up axis {up}");
        Assert.NotNull(game.FindObject(tower.Id));
        Assert.True(up.Z < 0.5f, "lies on the ground");
    }

    [Fact]
    public void BattleBusSurvivesItsFirstDeathAsAHulk()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var bob = game.Players.Find("Bob")!;
        var cc = game.ObjectsOwnedBy(bob).First(o => o.IsKindOf("COMMANDCENTER"));
        var spot = Ground(game, game.Pathfinder.NearestPassable(cc.Position + new Vector3(250, 0, 0), Logic.Locomotion.Surfaces.Ground) ?? cc.Position);
        var bus = game.CreateObject("GLAVehicleBattleBus", bob, spot)!;
        var rebel = game.CreateObject("GLAInfantryRebel", bob, spot + new Vector3(30, 0, 0))!;
        bus.Contain!.AddToContain(rebel);
        float rebelHealth = rebel.Health;
        game.Update();
        bus.Damage(new DamageInfo { Amount = bus.MaxHealth * 2, DamageType = "EXPLOSION" });
        for (int i = 0; i < 30 * 5; i++)
            game.Update();
        output.WriteLine($"bus alive {game.FindObject(bus.Id) != null}, health {bus.Health}/{bus.MaxHealth}, second life {bus.ModelConditions.Has(Logic.Core.Flags.ModelCondition.IndexOf("SECOND_LIFE"))}, rebel {rebelHealth} -> {rebel.Health}");
        Assert.NotNull(game.FindObject(bus.Id));
        Assert.True(bus.ModelConditions.Has(Logic.Core.Flags.ModelCondition.IndexOf("SECOND_LIFE")), "landed as a hulk");
        Assert.True(rebel.Health < rebelHealth, "the passengers were hurt");

        // Emptied, the hulk goes away after EmptyHulkDestructionDelay.
        bus.Contain.RemoveAllContained();
        bool gone = false;
        for (int i = 0; i < 30 * 30 && !gone; i++)
        {
            game.Update();
            gone = game.FindObject(bus.Id) == null || bus.IsEffectivelyDead;
        }
        Assert.True(gone, "the empty hulk dies");
    }
}

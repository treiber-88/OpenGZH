using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.AI;
using OpenGZH.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>The Chinook as a troop transport: landing to unload, and combat drops.</summary>
public class ChinookTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static Vector3 Ground(GameLogic game, Vector3 p) => p with { Z = game.Terrain.GroundHeight(p) };

    static void Run(GameLogic game, int frames)
    {
        for (int i = 0; i < frames; i++)
            game.Update();
    }

    (GameLogic, Logic.Players.Player, GameObject Chinook, List<GameObject> Rangers) SetupLoaded(int rangers)
    {
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var spot = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter").Position + new Vector3(250, 0, 0);
        var chinook = game.CreateObject("AmericaVehicleChinook", alice, Ground(game, spot) + new Vector3(0, 0, 50))!;
        var list = new List<GameObject>();
        for (int i = 0; i < rangers; i++)
        {
            var ranger = game.CreateObject("AmericaInfantryRanger", alice, Ground(game, spot + new Vector3(0, 20 + 10 * i, 0)))!;
            chinook.Contain!.AddToContain(ranger);
            list.Add(ranger);
        }
        Run(game, 30);
        return (game, alice, chinook, list);
    }

    [Fact]
    public void ChinookLandsToEvacuateAndTakesOffAgain()
    {
        if (!content.Available) return;
        var (game, alice, chinook, rangers) = SetupLoaded(3);
        var ai = (ChinookAIUpdate)chinook.AI!;
        Assert.Equal(3, chinook.Contain!.Count);
        float flyingHeight = chinook.HeightAboveTerrain;
        output.WriteLine($"flying at height {flyingHeight:F0}, status {ai.Status}");

        game.Commands.Enqueue(new EvacuateCommand(alice.Index, [chinook.Id]));
        bool landed = false;
        for (int i = 0; i < 30 * 20 && chinook.Contain.Count > 0; i++)
        {
            game.Update();
            landed |= ai.Status == ChinookAIUpdate.FlightStatus.Landed;
        }
        output.WriteLine($"landed={landed} inside={chinook.Contain.Count} height={chinook.HeightAboveTerrain:F0}");
        Assert.True(landed, "it lands to let them out");
        Assert.Equal(0, chinook.Contain.Count);
        Assert.All(rangers, r => Assert.False(r.IsContained));

        Run(game, 30 * 10);
        output.WriteLine($"after: status {ai.Status} height {chinook.HeightAboveTerrain:F0}");
        Assert.Equal(ChinookAIUpdate.FlightStatus.Flying, ai.Status);
        Assert.True(chinook.HeightAboveTerrain > 10, "and takes off again once everyone is out");
    }

    [Fact]
    public void CombatDropClearsAnEnemyHeldBuilding()
    {
        if (!content.Available) return;
        var (game, alice, chinook, rangers) = SetupLoaded(3);
        var bob = game.Players.Find("Bob")!;
        var building = game.Objects.Where(o => o.Contain is { IsGarrisonable: true } && !o.Owner.IsHuman && o.Owner.Name != "Bob")
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, chinook.Position)).First();
        var rebel = game.CreateObject("GLAInfantryRebel", bob, Ground(game, building.Position))!;
        building.Contain!.AddToContain(rebel);
        Assert.True(rebel.IsContained);

        game.Commands.Enqueue(new CombatDropCommand(alice.Index, [chinook.Id], building.Id, building.Position));
        for (int i = 0; i < 30 * 60; i++)
        {
            game.Update();
            if (chinook.Contain!.Count == 0 && rangers.All(r => r.IsEffectivelyDead || !r.AI!.IsInRappel()))
                break;
        }
        int inside = rangers.Count(r => r.IsContained && r.ContainedById == building.Id);
        output.WriteLine($"{building.Template.Name}: rebel dead={rebel.IsEffectivelyDead}, rangers inside={inside}, owner={building.Owner}");
        Assert.True(rebel.IsEffectivelyDead, "the first Ranger down kills the Rebel inside");
        Assert.True(inside >= 2, "and the Rangers take the building");
        Assert.Equal(alice, building.Owner);
    }
}

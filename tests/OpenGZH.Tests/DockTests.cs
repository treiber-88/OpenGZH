using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Commands;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Docking with buildings: war factory repair bays.</summary>
public class DockTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static Vector3 Ground(GameLogic game, Vector3 p) => p with { Z = game.Terrain.GroundHeight(p) };

    [Fact]
    public void WarFactoryRepairsADockedVehicle()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var cc = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter");
        var spot = game.Pathfinder.NearestPassable(cc.Position + new Vector3(250, 0, 0), Logic.Locomotion.Surfaces.Ground) ?? cc.Position;
        var factory = game.CreateObject("AmericaWarFactory", alice, Ground(game, spot))!;
        var tankSpot = game.Pathfinder.NearestPassable(spot + new Vector3(0, 200, 0), Logic.Locomotion.Surfaces.Ground) ?? spot;
        var tank = game.CreateObject("AmericaTankCrusader", alice, Ground(game, tankSpot))!;
        tank.Body!.SetHealth(tank.MaxHealth / 2);
        game.Update();

        Assert.True(Logic.Modules.AI.AIUpdateInterface.CanGetRepairedAt(tank, factory));
        game.Commands.Enqueue(new GetRepairedCommand(alice.Index, [tank.Id], factory.Id));
        bool docked = false;
        int frames = 0;
        for (; frames < 60 * 30; frames++)
        {
            game.Update();
            docked |= factory.ModelConditions.Has(Logic.Core.Flags.ModelCondition.IndexOf("DOCKING_ACTIVE"));
            if (tank.Health >= tank.MaxHealth && !tank.AI!.IsDocking)
                break;
        }
        output.WriteLine($"after {frames / 30f:F1}s: health {tank.Health}/{tank.MaxHealth}, docked {docked}, at {tank.Position}, factory {factory.Position}, state {tank.AI!.State}");
        Assert.True(docked, "the vehicle reached the repair bay");
        Assert.Equal(tank.MaxHealth, tank.Health);
        Assert.False(tank.AI.IsDocking);
    }

    [Fact]
    public void ANewOrderCancelsDocking()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var cc = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter");
        var spot = game.Pathfinder.NearestPassable(cc.Position + new Vector3(250, 0, 0), Logic.Locomotion.Surfaces.Ground) ?? cc.Position;
        var factory = game.CreateObject("AmericaWarFactory", alice, Ground(game, spot))!;
        var tank = game.CreateObject("AmericaTankCrusader", alice, Ground(game, game.Pathfinder.NearestPassable(spot + new Vector3(0, 200, 0), Logic.Locomotion.Surfaces.Ground) ?? spot))!;
        tank.Body!.SetHealth(tank.MaxHealth / 2);
        tank.AI!.GetRepairedAt(factory, CommandSource.Player);
        Assert.True(tank.AI.IsDocking);
        for (int i = 0; i < 30; i++)
            game.Update();
        tank.AI.MoveTo(tank.Position + new Vector3(-100, 0, 0), CommandSource.Player);
        Assert.False(tank.AI.IsDocking);
        var dock = factory.FindModule<Logic.Modules.Dock.DockUpdate>()!;
        Assert.True(dock.IsClearToApproach(tank));
        Assert.Equal(0u, dock.ActiveDocker);
    }
}

using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Vision;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Fog of war: what each player has explored and can see.</summary>
public class ShroudTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static void Run(GameLogic game, int frames)
    {
        for (int i = 0; i < frames; i++)
            game.Update();
    }

    [Fact]
    public void UnitsClearTheShroudAndLeaveFogBehind()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish(revealMap: false);
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        game.Update();
        var cc = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter");
        var enemyCc = game.ObjectsOwnedBy(bob).First(o => o.IsKindOf("COMMANDCENTER"));
        Assert.Equal(CellShroud.Clear, game.Shroud.StatusAt(alice, cc.Position));
        Assert.Equal(CellShroud.Shrouded, game.Shroud.StatusAt(alice, enemyCc.Position));
        Assert.False(game.Shroud.IsVisibleTo(enemyCc, alice));
        Assert.True(game.Shroud.IsVisibleTo(enemyCc, bob));

        // Scout: a Humvee drives out and back. What it saw stays explored (fogged), not shrouded.
        var start = cc.Position + new Vector3(200, 0, 0);
        var humvee = game.CreateObject("AmericaVehicleHumvee", alice, start with { Z = game.Terrain.GroundHeight(start) })!;
        var far = start + new Vector3(600, 0, 0);
        far.Z = game.Terrain.GroundHeight(far);
        Assert.Equal(CellShroud.Shrouded, game.Shroud.StatusAt(alice, far));
        game.Commands.Enqueue(new MoveCommand(alice.Index, [humvee.Id], far));
        Run(game, 30 * 15);
        output.WriteLine($"at the far point: {game.Shroud.StatusAt(alice, far)} (humvee {Vector3.Distance(humvee.Position, far):F0} away)");
        Assert.Equal(CellShroud.Clear, game.Shroud.StatusAt(alice, far));

        humvee.Kill();
        Run(game, 30 * 8);
        output.WriteLine($"after the scout died: {game.Shroud.StatusAt(alice, far)}");
        Assert.Equal(CellShroud.Fogged, game.Shroud.StatusAt(alice, far));
    }

    [Fact]
    public void PlayersCantOrderAttacksIntoTheFog()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish(revealMap: false);
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        game.Update();
        var tank = game.CreateObject("AmericaTankCrusader", alice, game.ObjectsOwnedBy(alice).First().Position + new Vector3(150, 0, 0))!;
        var enemy = game.ObjectsOwnedBy(bob).First(o => o.IsKindOf("COMMANDCENTER"));
        game.Commands.Enqueue(new AttackObjectCommand(alice.Index, [tank.Id], enemy.Id));
        game.Update();
        Assert.NotEqual(enemy.Id, tank.AI!.TargetId);

        game.Shroud.RevealMapForPlayerPermanently(alice);
        game.Commands.Enqueue(new AttackObjectCommand(alice.Index, [tank.Id], enemy.Id));
        game.Update();
        Assert.Equal(enemy.Id, tank.AI.TargetId);
    }

    [Fact]
    public void ShroudCrateRevealsTheMap()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish(revealMap: false);
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        game.Update();
        var enemyCc = game.ObjectsOwnedBy(bob).First(o => o.IsKindOf("COMMANDCENTER"));
        game.Shroud.RevealMapForPlayer(alice);
        Assert.Equal(CellShroud.Fogged, game.Shroud.StatusAt(alice, enemyCc.Position));
    }
}

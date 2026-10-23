using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.AI;
using OpenGZH.Logic.Modules.Production;
using OpenGZH.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Airfields and jets: building, taxiing, takeoff, landing and reloading.</summary>
public class AircraftTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static Vector3 Ground(GameLogic game, Vector3 p) => p with { Z = game.Terrain.GroundHeight(p) };

    (GameLogic Game, Logic.Players.Player Alice, GameObject Airfield) SetupAirfield()
    {
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var commandCenter = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter");
        var spot = Ground(game, commandCenter.Position + new Vector3(0, 250, 0));
        var airfield = game.CreateObject("AmericaAirfield", alice, spot)!;
        game.CreateObject("AmericaPowerPlant", alice, Ground(game, commandCenter.Position + new Vector3(-200, 0, 0)));
        alice.Money.Deposit(100000);
        game.Update();
        return (game, alice, airfield);
    }

    GameObject BuildRaptor(GameLogic game, Logic.Players.Player alice, GameObject airfield)
    {
        var production = airfield.FindModule<ProductionUpdate>()!;
        var raptor = game.Templates.Find("AmericaJetRaptor")!;
        output.WriteLine($"queue: {production.CanQueueUnit(raptor) ?? "ok"}");
        Assert.NotNull(production.QueueUnit(raptor));
        for (int i = 0; i < 30 * 40; i++)
        {
            game.Update();
            if (game.ObjectsOwnedBy(alice).FirstOrDefault(o => o.Template.Name == "AmericaJetRaptor") is { } built)
                return built;
        }
        throw new Xunit.Sdk.XunitException("no Raptor was built");
    }

    [Fact]
    public void AirfieldReservesASpacePerQueuedJet()
    {
        if (!content.Available) return;
        var (game, _, airfield) = SetupAirfield();
        var production = airfield.FindModule<ProductionUpdate>()!;
        var raptor = game.Templates.Find("AmericaJetRaptor")!;
        for (int i = 0; i < 4; i++)
            Assert.NotNull(production.QueueUnit(raptor));
        // Four spaces (2 x 2): a fifth jet can't be ordered.
        Assert.Equal("NoExitAvailable", production.CanQueueUnit(raptor));
        Assert.Null(production.QueueUnit(raptor));
        production.Cancel(production.Queue[0].Id);
        Assert.Null(production.CanQueueUnit(raptor));
    }

    [Fact]
    public void RaptorFiresItsMissilesThenReloadsAtBase()
    {
        if (!content.Available) return;
        var (game, alice, airfield) = SetupAirfield();
        var bob = game.Players.Find("Bob")!;
        var jet = BuildRaptor(game, alice, airfield);
        var ai = (JetAIUpdate)jet.AI!;
        for (int i = 0; i < 30 * 15 && !ai.IsIdle; i++)
            game.Update();

        var tank = game.CreateObject("GLATankScorpion", bob, Ground(game, airfield.Position + new Vector3(700, 100, 0)))!;
        tank.Body!.Indestructible = true;
        game.Commands.Enqueue(new AttackObjectCommand(alice.Index, [jet.Id], tank.Id));
        int shots = 0;
        game.Events.OnWeaponFired += (shooter, _, _) => { if (shooter == jet) shots++; };
        bool wentHome = false, reloaded = false;
        for (int i = 0; i < 30 * 150; i++)
        {
            game.Update();
            wentHome |= ai.IsOutOfSpecialReloadAmmo;
            if (wentHome && ai.IsIdle && jet.HeightAboveTerrain < 5 && !ai.IsOutOfSpecialReloadAmmo)
            {
                reloaded = jet.Weapons.All.All(w => w.ClipSize <= 0 || w.RemainingAmmo == w.ClipSize);
                if (reloaded)
                    break;
            }
        }
        output.WriteLine($"shots {shots}, out of ammo seen {wentHome}, reloaded {reloaded}, at {jet.Position}");
        Assert.True(shots >= 2, "it attacked");
        Assert.True(wentHome, "until its missiles ran out");
        Assert.True(reloaded, "then came back and reloaded");
    }

    [Fact]
    public void RaptorTaxisTakesOffReturnsAndLands()
    {
        if (!content.Available) return;
        var (game, alice, airfield) = SetupAirfield();
        var jet = BuildRaptor(game, alice, airfield);
        var ai = (JetAIUpdate)jet.AI!;
        var pp = airfield.FindModule<ParkingPlaceBehavior>()!;
        Assert.True(pp.HasReservedSpace(jet.Id));
        Assert.Equal(airfield.Id, jet.ProducerId);

        // Taxi from the hangar to the parking space.
        for (int i = 0; i < 30 * 15 && !ai.IsIdle; i++)
            game.Update();
        var parking = pp.CalcInfo(jet.Id).ParkingSpace;
        output.WriteLine($"parked at {jet.Position} (space {parking}), idle={ai.IsIdle}");
        Assert.True(ai.IsIdle, "finished taxiing out of the hangar");
        Assert.True(MathUtil.Distance2D(jet.Position, parking) < 10, "stands in its parking space");
        Assert.True(jet.HeightAboveTerrain < 5);

        // A move order: wait for the runway, taxi, pause, take off.
        var target = Ground(game, airfield.Position + new Vector3(600, 0, 0));
        game.Commands.Enqueue(new MoveCommand(alice.Index, [jet.Id], target));
        float maxHeight = 0;
        bool sawTakeoff = false;
        for (int i = 0; i < 30 * 40; i++)
        {
            game.Update();
            maxHeight = Math.Max(maxHeight, jet.HeightAboveTerrain);
            sawTakeoff |= ai.IsTakeoffOrLandingInProgress;
            if (MathUtil.Distance2D(jet.Position, target) < 80)
                break;
        }
        output.WriteLine($"after takeoff: at {jet.Position}, height {jet.HeightAboveTerrain:F0} (max {maxHeight:F0}), state {ai.State}");
        Assert.True(sawTakeoff);
        Assert.True(maxHeight > 30, "it flew");
        Assert.True(MathUtil.Distance2D(jet.Position, target) < 150, "and went where it was told");
        Assert.True(pp.HasReservedSpace(jet.Id), "Raptors keep their space while flying");

        // Stop: fly home and land.
        game.Commands.Enqueue(new StopCommand(alice.Index, [jet.Id]));
        bool landed = false;
        for (int i = 0; i < 30 * 90; i++)
        {
            game.Update();
            if (ai.IsIdle && jet.HeightAboveTerrain < 5)
            {
                landed = true;
                break;
            }
        }
        output.WriteLine($"landed={landed} at {jet.Position}, height {jet.HeightAboveTerrain:F0}, dist to space {MathUtil.Distance2D(jet.Position, parking):F0}");
        Assert.True(landed, "lands back at the airfield");
        Assert.True(MathUtil.Distance2D(jet.Position, parking) < 15, "and parks in its space");
    }
}

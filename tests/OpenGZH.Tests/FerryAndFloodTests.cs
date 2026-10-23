using OpenGZH.Logic;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Modules.Contain;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>The CHI02 ferry and the GLA01 dam-break wave.</summary>
public class FerryAndFloodTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static void Run(GameLogic game, int frames)
    {
        for (int i = 0; i < frames; i++)
            game.Update();
    }

    [Fact]
    public void FerryTakesUnitsAcross()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish("maps/CHI02/CHI02.map");
        game.Scripts = null;    // the mission scripts would end the game with the test players
        var ferry = game.Objects.First(o => o.Template.Name == "CivilianVehicleFerry");
        var ai = Assert.IsType<RailedTransportAIUpdate>(ferry.AI);
        var dock = ferry.FindModule<RailedTransportDockUpdate>()!;
        var start = ferry.Position;
        Run(game, 30 * 60);   // it sails to the nearest landing first
        output.WriteLine($"ferry {start} -> {ferry.Position}, dock open {dock.IsDockOpen}");
        Assert.True(dock.IsDockOpen, "the ferry docks at a landing");

        // Give the ferry to Alice and send a rifleman aboard.
        var alice = game.Players.Find("Alice")!;
        ferry.SetOwner(alice);
        // A walkable spot near the landing, reachable from it.
        var ramp = dock.GetExitPosition(ferry);
        var spot = Enumerable.Range(0, 400).Select(i => ramp + new System.Numerics.Vector3(MathF.Cos(i * 0.37f), MathF.Sin(i * 0.37f), 0) * (40 + i))
            .First(p => game.Pathfinder.IsValidMovementTerrain(p, Logic.Locomotion.Surfaces.Ground)
                && game.Pathfinder.IsValidMovementTerrain(p + new System.Numerics.Vector3(60, 0, 0), Logic.Locomotion.Surfaces.Ground));
        spot.Z = game.Terrain.GroundHeight(spot);
        var ranger = game.CreateObject("AmericaInfantryRanger", alice, spot)!;
        new DockCommand(alice.Index, [ranger.Id], ferry.Id).Execute(game);
        for (int i = 0; i < 30 * 40 && ranger.ContainedById != ferry.Id; i++)
            game.Update();
        output.WriteLine($"ranger contained by {ranger.ContainedById}, ai {ranger.AI?.State}");
        Assert.Equal(ferry.Id, ranger.ContainedById);

        var landing = ferry.Position;
        new ExecuteRailedTransportCommand(alice.Index, [ferry.Id]).Execute(game);
        Run(game, 30 * 5);
        Assert.False(dock.IsDockOpen, "the ramp is up while it sails");
        for (int i = 0; i < 30 * 120 && !dock.IsDockOpen; i++)
            game.Update();
        float crossed = Logic.Core.MathUtil.Distance2D(landing, ferry.Position);
        output.WriteLine($"crossed {crossed:F0}");
        Assert.True(crossed > 200, "the ferry crosses to the other landing");

        new EvacuateCommand(alice.Index, [ferry.Id]).Execute(game);
        for (int i = 0; i < 30 * 20 && ranger.ContainedById != 0; i++)
            game.Update();
        Run(game, 30 * 6);
        output.WriteLine($"ranger at {ranger.Position}, held {ranger.IsDisabledBy("DISABLED_HELD")}");
        Assert.Equal(0u, ranger.ContainedById);
        Assert.False(ranger.IsDisabledBy("DISABLED_HELD"));
    }

    [Fact]
    public void BreakingTheDamReleasesTheWave()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish("maps/GLA01/GLA01.map");
        game.Scripts = null;
        var wave = game.Objects.First(o => o.Template.Name.StartsWith("WaveGuide"));
        var dam = game.Objects.First(o => o.Template.Name == "Dam");
        Run(game, 30 * 3);
        var start = wave.Position;
        Assert.True(wave.IsDisabled, "the wave waits behind the dam");
        dam.Kill();
        float travelled = 0;
        for (int i = 0; i < 30 * 90 && !wave.IsDestroyed; i++)
        {
            game.Update();
            travelled = Math.Max(travelled, Logic.Core.MathUtil.Distance2D(start, wave.Position));
        }
        int wet = game.Objects.Count(o => o.HasStatus("WET"));
        output.WriteLine($"wave travelled {travelled:F0}, destroyed {wave.IsDestroyed}, {wet} objects soaked");
        Assert.True(travelled > 300, "the wave runs down its path");
    }
}

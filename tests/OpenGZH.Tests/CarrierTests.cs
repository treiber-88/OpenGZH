using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Modules.Production;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>The aircraft carrier's flight deck, and avalanche chunks.</summary>
public class CarrierTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static void Run(GameLogic game, int frames)
    {
        for (int i = 0; i < frames; i++)
            game.Update();
    }

    [Fact]
    public void CarrierLaunchesItsJetsAtATarget()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish("Maps/Hovercraft/Hovercraft.map");
        game.Scripts = null;    // the challenge scripts would end the game with the test players
        var carrier = game.Objects.First(o => o.Template.Name == "AmericaAircraftCarrier");
        var deck = Assert.IsType<FlightDeckBehavior>(carrier.AI);
        Run(game, 30);
        var jets = game.Objects.Where(o => o.Template.Name == "AmericaJetAircraftCarrierRaptor" && o.ProducerId == carrier.Id).ToList();
        output.WriteLine($"{jets.Count} jets on deck, owner {carrier.Owner.Name}");
        Assert.Equal(20, jets.Count);
        Assert.All(jets, j => Assert.True(deck.HasReservedSpace(j.Id)));

        // Order an attack on something across the water.
        var enemy = game.Players.Find("Bob")!;
        var target = game.CreateObject("ChinaTankBattleMaster", enemy, carrier.Position + new Vector3(600, 0, 0))!;
        carrier.AI!.AttackObject(target, CommandSource.Player);
        int launched = 0;
        for (int i = 0; i < 30 * 30; i++)
        {
            game.Update();
            launched = jets.Count(j => !j.IsEffectivelyDead && j.IsAirborneTarget);
            if (launched >= 2)
                break;
        }
        output.WriteLine($"{launched} jets airborne, target health {target.Health}");
        Assert.True(launched >= 2, "both catapults launch a jet");
    }

    [Fact]
    public void DamagedAvalancheChunksSlideDownhill()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish("Maps/Alpine Assault/Alpine Assault.map");
        var neutral = game.Players.Neutral;
        // Find a steep spot.
        Vector3 spot = default;
        float steepest = 1;
        for (float x = 200; x < game.Terrain.WorldWidth - 200; x += 40)
            for (float y = 200; y < game.Terrain.WorldHeight - 200; y += 40)
            {
                var n = game.Terrain.GroundNormal(x, y);
                if (n.Z < steepest)
                {
                    steepest = n.Z;
                    spot = new Vector3(x, y, 0);
                }
            }
        spot.Z = game.Terrain.GroundHeight(spot);
        var chunks = Enumerable.Range(0, 4)
            .Select(i => game.CreateObject("AvalancheChunk", neutral, spot + new Vector3(i % 2 * 40, i / 2 * 40, 0))!)
            .ToList();
        var start = chunks.Select(c => c.Position).ToList();
        Run(game, 40);
        chunks[0].Body!.SetDamageState(Logic.Modules.BodyDamageState.Damaged);
        Run(game, 30 * 12);
        float moved = chunks.Select((c, i) => Logic.Core.MathUtil.Distance2D(c.Position, start[i])).Max();
        output.WriteLine($"steepest normal z {steepest:F2}, most moved {moved:F0}, states {string.Join(",", chunks.Select(c => c.Body!.DamageState))}");
        Assert.True(moved > 10, "the chunks slide");
        // All have come to rest. (As in the original, a chunk that settles first can be marked damaged
        // again by neighbours still sliding, so not every one ends as rubble.)
        int moving = Logic.Core.Flags.ModelCondition.IndexOf("MOVING");
        Assert.All(chunks, c => Assert.False(c.ModelConditions.Has(moving)));
        Assert.Contains(chunks, c => c.Body!.DamageState == Logic.Modules.BodyDamageState.Rubble);
    }
}

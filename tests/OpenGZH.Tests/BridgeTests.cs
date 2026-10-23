using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Commands;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Bridges: map-drawn spans as pathfinding layers.</summary>
public class BridgeTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void MapBridgesLoadAndCanBeCrossed()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish("Maps/Winding River/Winding River.map");
        var bridges = game.Terrain.Bridges.All;
        foreach (var b in bridges)
            output.WriteLine($"bridge {b.TemplateName} layer {b.Layer}: {b.Info.From} -> {b.Info.To}, width {b.Info.BridgeWidth:F1}, object {b.Info.BridgeObjectId}");
        Assert.NotEmpty(bridges);
        var bridge = bridges[0];
        Assert.True(bridge.Info.BridgeWidth > 10, "the width comes from the bridge model");
        Assert.NotEqual(0u, bridge.Info.BridgeObjectId);

        // Start a little beyond one end, aim a little beyond the other.
        var axis = Vector3.Normalize(bridge.Info.To - bridge.Info.From);
        var start = bridge.Info.From - axis * 40;
        var goal = bridge.Info.To + axis * 40;
        start.Z = game.Terrain.GroundHeight(start);
        goal.Z = game.Terrain.GroundHeight(goal);
        var path = game.Pathfinder.FindLayeredPath(start, goal, Logic.Locomotion.Surfaces.Ground, 0);
        output.WriteLine("path: " + string.Join(" ", path.Select(p => $"({p.Position.X:F0},{p.Position.Y:F0},{p.Position.Z:F0} L{p.Layer})")));
        Assert.Contains(path, p => p.Layer == bridge.Layer);

        var alice = game.Players.Find("Alice")!;
        var tank = game.CreateObject("AmericaTankCrusader", alice, start)!;
        tank.AI!.MoveTo(goal, CommandSource.Player);
        bool wasOnBridge = false;
        float maxDeckGap = 0;
        for (int i = 0; i < 30 * 60 && Logic.Core.MathUtil.Distance2D(tank.Position, goal) > 20; i++)
        {
            game.Update();
            if (tank.Layer == bridge.Layer)
            {
                wasOnBridge = true;
                maxDeckGap = Math.Max(maxDeckGap, MathF.Abs(tank.Position.Z - bridge.GetBridgeHeight(tank.Position)));
            }
        }
        output.WriteLine($"tank at {tank.Position} (goal {goal}), was on bridge {wasOnBridge}, max gap to deck {maxDeckGap:F1}, layer now {tank.Layer}");
        Assert.True(wasOnBridge, "drove onto the bridge layer");
        Assert.True(Logic.Core.MathUtil.Distance2D(tank.Position, goal) <= 20, "and reached the far side");
        Assert.True(maxDeckGap < 5, "riding on the deck");
        Assert.Equal(0, tank.Layer);
    }
}

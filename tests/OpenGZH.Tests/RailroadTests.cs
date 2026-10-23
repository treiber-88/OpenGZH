using System.Numerics;
using OpenGZH.Logic;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Trains on their waypoint tracks.</summary>
public class RailroadTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void LocomotivePullsItsCarsAlongTheTrack()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish("Maps/Alpine Assault/Alpine Assault.map");
        var engine = game.Objects.FirstOrDefault(o => o.Template.Name == "TrainEngineAlpine");
        Assert.NotNull(engine);
        var start = engine!.Position;
        for (int i = 0; i < 30 * 20; i++)
            game.Update();
        var cars = game.Objects.Where(o => o != engine && o.FindModule<Logic.Modules.Behaviors.RailroadBehavior>() != null).ToList();
        float moved = Logic.Core.MathUtil.Distance2D(engine.Position, start);
        float spacing = cars.Count > 0 ? cars.Min(c => Logic.Core.MathUtil.Distance2D(c.Position, engine.Position)) : 0;
        output.WriteLine($"engine moved {moved:F0}, {cars.Count} cars ({string.Join(", ", cars.Select(c => c.Template.Name))}), nearest car {spacing:F0} away");
        Assert.True(moved > 50, "the engine runs along its track");
        Assert.NotEmpty(cars);
        Assert.InRange(spacing, 40, 160);
    }
}

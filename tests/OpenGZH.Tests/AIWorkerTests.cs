using OpenGZH.Formats.Maps;
using OpenGZH.Logic;
using OpenGZH.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>The GLA computer player keeps a sensible number of workers (they gather as well as build).</summary>
public class AIWorkerTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void GlaAIDoesNotBuildWorkersForever()
    {
        if (!content.Available) return;
        var mapFile = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Alpine Assault/Alpine Assault.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 77,
        [
            new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionGLA", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var bob = game.Players.Find("Bob")!;
        int Workers() => game.ObjectsOwnedBy(bob).Count(o => o.Template.Name == "GLAInfantryWorker");
        int halfway = 0;
        for (int i = 0; i < 4 * 60 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            if (i == 2 * 60 * LogicTime.FramesPerSecond)
                halfway = Workers();
        }
        output.WriteLine($"Bob's workers: {halfway} after 2 minutes, {Workers()} after 4");
        // Before the fix workers were never counted as gatherers, so one more was ordered every few seconds.
        Assert.True(Workers() <= 15, $"{Workers()} workers");
        Assert.True(Workers() - halfway <= 4, $"still adding workers: {halfway} -> {Workers()}");
    }
}

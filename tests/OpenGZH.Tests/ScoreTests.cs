using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>The score keeper behind the score screen, and how a finished game ends.</summary>
public class ScoreTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static void Run(GameLogic game, int frames)
    {
        for (int i = 0; i < frames; i++)
            game.Update();
    }

    [Fact]
    public void KillsCountForTheKillerAndAgainstTheLoser()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        game.Scripts = null;
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        var center = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        center.Z = game.Terrain.GroundHeight(center);
        var tank = game.CreateObject("AmericaTankCrusader", alice, center)!;
        var victim = game.CreateObject("ChinaTankBattleMaster", bob, center + new Vector3(80, 0, 0))!;
        int scoreBefore = alice.Score.CalculateScore();

        victim.Damage(new DamageInfo { SourceId = tank.Id, Amount = 100000, DamageType = "UNRESISTABLE" });
        Run(game, 2);

        output.WriteLine($"Alice destroyed {alice.Score.TotalUnitsDestroyed}, Bob lost {bob.Score.TotalUnitsLost}");
        Assert.Equal(1, alice.Score.TotalUnitsDestroyed);
        Assert.Equal(1, bob.Score.TotalUnitsLost);
        Assert.Equal(0, alice.Score.TotalUnitsLost);
        Assert.Equal(scoreBefore + 100, alice.Score.CalculateScore());
    }

    [Fact]
    public void StartingUnitsCountAsBuilt()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        Assert.True(alice.Score.TotalUnitsBuilt >= 1); // the starting dozer
    }

    [Fact]
    public void VictoryPlaysOnUntilTheEndTimerRunsOut()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        Assert.NotNull(game.Scripts);
        bool? result = null;
        game.Scripts!.OnMissionEnded += won => result = won;
        game.Scripts.ExecuteActions([new ScriptAction { Name = "VICTORY" }]);
        Assert.True(result);
        Run(game, 60);
        Assert.False(game.IsGameOver); // the Victorious window is still up
        int frame = game.Frame;
        Run(game, 70);
        Assert.True(game.IsGameOver);
        Assert.True(game.Frame > frame); // the world kept running meanwhile
    }
}

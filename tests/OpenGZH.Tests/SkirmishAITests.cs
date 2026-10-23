using OpenGZH.Logic;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>The computer player runs the standard skirmish scripts and builds up a base and an army.</summary>
public class SkirmishAITests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void SkirmishAIGetsSkirmishScriptsAndTeams()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var bob = game.Players.Find("Bob")!;
        Assert.True(bob.IsSkirmishAIPlayer);
        Assert.NotNull(bob.AI);
        Assert.Equal("player1", bob.Name);
        Assert.StartsWith("Skirmish", bob.ScriptName);
        Assert.EndsWith("1", bob.ScriptName);
        Assert.Equal("team" + bob.ScriptName, bob.DefaultTeam.Name);
        Assert.True(bob.TeamPrototypes.Count > 10, $"Bob has {bob.TeamPrototypes.Count} team definitions");
        int scripts = game.Scripts!.Sides[bob.Index]?.AllScripts.Count() ?? 0;
        output.WriteLine($"Bob: {bob.ScriptName}, {bob.TeamPrototypes.Count} teams, {scripts} scripts, build list {bob.BuildList.Count}");
        Assert.True(scripts > 100);
        Assert.True(bob.BuildList.Count > 5);
    }

    [Fact]
    public void SkirmishAIBuildsABaseAndAnArmy()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var bob = game.Players.Find("Bob")!;
        var log = new List<string>();
        if (Environment.GetEnvironmentVariable("OPENGZH_AI_DEBUG") == "1")
        {
            ScriptEngine.ShowDebugMessages = true;
            ScriptEngine.TraceScripts = true;
            Log.Output = m => { lock (log) if (log.Count < 3000) log.Add(m); };
        }
        int structuresAtStart = game.ObjectsOwnedBy(bob).Count(o => o.IsKindOf(Kind.Structure));
        GameProfiler.Enabled = true;
        GameProfiler.Reset();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int Minutes = int.TryParse(Environment.GetEnvironmentVariable("OPENGZH_AI_MINUTES"), out int m) ? m : 5;
        for (int i = 0; i < Minutes * 60 * LogicTime.FramesPerSecond; i++)
        {
            var frameWatch = System.Diagnostics.Stopwatch.StartNew();
            game.Update();
            if (frameWatch.ElapsedMilliseconds > 500 || i % 300 == 0)
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "opengzh_ai_progress.txt"), $"frame {i} took {frameWatch.ElapsedMilliseconds}ms{Environment.NewLine}");
            if (i % (60 * LogicTime.FramesPerSecond) == 0)
                output.WriteLine($"{i / LogicTime.FramesPerSecond}s: Bob ${bob.Money.Amount}, {game.ObjectsOwnedBy(bob).Count(o => o.IsKindOf(Kind.Structure))} structures, "
                    + $"{game.ObjectsOwnedBy(bob).Count(o => !o.IsKindOf(Kind.Structure))} units");
        }
        var structures = game.ObjectsOwnedBy(bob).Where(o => o.IsKindOf(Kind.Structure)).Select(o => o.Template.Name).ToList();
        var units = game.ObjectsOwnedBy(bob).Where(o => !o.IsKindOf(Kind.Structure)).Select(o => o.Template.Name).ToList();
        output.WriteLine($"after {Minutes} minutes ({watch.Elapsed.TotalSeconds:F0}s real): structures {string.Join(", ", structures.GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}"))}");
        output.WriteLine($"units {string.Join(", ", units.GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}"))}");
        foreach (var truck in game.ObjectsOwnedBy(bob).Where(o => o.AI is Logic.Modules.Supply.SupplyTruckAIUpdate))
        {
            var g = ((Logic.Modules.Supply.SupplyTruckAIUpdate)truck.AI!).Gatherer;
            output.WriteLine($"truck {truck.Id} at {truck.Position} state {g.State} boxes {g.Boxes} wanting {g.ForceWanting} pref {g.PreferredCenterId} ai {truck.AI!.State} team {truck.Team?.Name}");
        }
        foreach (var c in game.ObjectsOwnedBy(bob).Where(o => o.Template.Name == "ChinaSupplyCenter"))
            output.WriteLine($"center {c.Id} at {c.Position} underConstruction {c.IsUnderConstruction}");
        foreach (var line in log)
            output.WriteLine(line);
        foreach (var (name, ms) in GameProfiler.Report().Take(40))
            output.WriteLine($"profile {name}: {ms:F0} ms");
        GameProfiler.Enabled = false;
        output.WriteLine("missing actions: " + string.Join(", ", ScriptActions.Missing));
        output.WriteLine("missing conditions: " + string.Join(", ", ScriptConditions.Missing));
        Assert.True(structures.Count > structuresAtStart + 2, "the computer player builds structures");
        Assert.True(units.Count > 3, "the computer player trains units");
    }
}

public class SkirmishScriptDump(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void DumpChinaBuildScripts()
    {
        if (!content.Available || Environment.GetEnvironmentVariable("OPENGZH_AI_DEBUG") != "1") return;
        var game = content.NewSkirmish();
        var bob = game.Players.Find("Bob")!;
        var list = game.Scripts!.Sides[bob.Index]!;
        game.Update();
        var cc = game.ObjectsOwnedBy(bob).FirstOrDefault(o => o.IsKindOf("COMMANDCENTER"));
        output.WriteLine($"CC at {cc?.Position} start {bob.StartPosition}");
        foreach (var info in bob.BuildList)
        {
            var t = game.Templates.Find(info.TemplateName);
            output.WriteLine($"BL {info.TemplateName} at {info.Location} angle {info.Angle:F2} built={info.IsInitiallyBuilt} rebuilds={info.NumRebuilds} auto={info.IsAutomaticBuild} id={info.ObjectId} ok={(t == null ? "?" : game.Building.CheckLocation(t, info.Location, info.Angle, bob).ToString())}");
        }
        foreach (var group in list.Groups.Take(60))
        {
            output.WriteLine($"GROUP {group.Name} active={group.IsActive} sub={group.IsSubroutine}");
            foreach (var s in group.Scripts.Take(40))
            {
                output.WriteLine($"  SCRIPT {s.Name} active={s.IsActive} once={s.IsOneShot} delay={s.DelayEvaluationSeconds} E{s.Easy}N{s.Normal}H{s.Hard}");
                foreach (var and in s.OrConditions)
                    output.WriteLine("    IF " + string.Join(" AND ", and));
                foreach (var a in s.Actions)
                    output.WriteLine("    DO " + a);
            }
        }
    }
}

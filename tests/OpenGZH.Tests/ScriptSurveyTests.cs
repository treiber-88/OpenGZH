using OpenGZH.Formats.Maps;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Which script actions and conditions the game's scripts use (to see what the engine must support).</summary>
public class ScriptSurveyTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void SkirmishScriptsLoad()
    {
        if (!content.Available) return;
        var file = ScriptFile.Read(content.FileSystem!.ReadAllBytes("Data/Scripts/SkirmishScripts.scb"));
        output.WriteLine($"players: {string.Join(", ", file.PlayerNames)}; teams {file.Teams.Count}; scripts {file.ScriptLists.Sum(l => l.AllScripts.Count())}");
        Assert.Contains(file.PlayerNames, n => n.StartsWith("Skirmish"));
        Assert.True(file.Teams.Count > 20);

        var actions = new Dictionary<string, int>();
        var conditions = new Dictionary<string, int>();
        void Count(ScriptList list)
        {
            foreach (var s in list.AllScripts)
            {
                foreach (var a in s.Actions.Concat(s.FalseActions))
                    actions[a.Name] = actions.GetValueOrDefault(a.Name) + 1;
                foreach (var c in s.OrConditions.SelectMany(o => o))
                    conditions[c.Name] = conditions.GetValueOrDefault(c.Name) + 1;
            }
        }
        foreach (var list in file.ScriptLists)
            Count(list);
        var path = Path.Combine(Path.GetTempPath(), "opengzh-skirmish-script-usage.txt");
        File.WriteAllLines(path, actions.OrderByDescending(kv => kv.Value).Select(kv => $"A {kv.Key} {kv.Value}")
            .Concat(conditions.OrderByDescending(kv => kv.Value).Select(kv => $"C {kv.Key} {kv.Value}")));
        output.WriteLine($"usage written to {path}");
    }
}

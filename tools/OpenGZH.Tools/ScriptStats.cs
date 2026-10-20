using OpenGZH.Core.FileSystem;
using OpenGZH.Formats.Maps;

namespace OpenGZH.Tools;

/// <summary>Counts which script conditions and actions the maps use (to plan the script engine).</summary>
static class ScriptStats
{
    public static int Run(VirtualFileSystem fileSystem, string[] args)
    {
        string filter = args.Length > 0 ? args[0] : "";
        var conditions = new Dictionary<string, int>();
        var actions = new Dictionary<string, int>();
        foreach (string path in fileSystem.List("Maps", ".map", recursive: true))
        {
            if (!path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var map = MapFile.Read(fileSystem.ReadAllBytes(path));
            foreach (var script in map.Sides.Sides.SelectMany(s => s.Scripts.AllScripts))
            {
                foreach (var condition in script.OrConditions.SelectMany(c => c))
                    conditions[condition.Name] = conditions.GetValueOrDefault(condition.Name) + 1;
                foreach (var action in script.Actions.Concat(script.FalseActions))
                    actions[action.Name] = actions.GetValueOrDefault(action.Name) + 1;
            }
        }
        Console.WriteLine($"CONDITIONS ({conditions.Count}):");
        Console.WriteLine(string.Join(" ", conditions.OrderByDescending(c => c.Value).Select(c => $"{c.Key}({c.Value})")));
        Console.WriteLine($"ACTIONS ({actions.Count}):");
        Console.WriteLine(string.Join(" ", actions.OrderByDescending(c => c.Value).Select(c => $"{c.Key}({c.Value})")));
        return 0;
    }
}

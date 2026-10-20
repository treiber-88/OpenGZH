using OpenGZH.Core.FileSystem;
using OpenGZH.Formats.Maps;

namespace OpenGZH.Tools;

/// <summary>Parses every .map in the virtual file system and prints a summary, to catch reader bugs.</summary>
static class MapCheck
{
    public static int Run(VirtualFileSystem fileSystem, string[] args)
    {
        string filter = args.Length > 0 ? args[0] : "";
        int failures = 0, count = 0;
        var unread = new SortedSet<string>();

        foreach (string path in fileSystem.List("Maps", ".map", recursive: true))
        {
            if (!path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            count++;
            try
            {
                var map = MapFile.Read(fileSystem.ReadAllBytes(path));
                int scripts = map.Sides.Sides.Sum(s => s.Scripts.AllScripts.Count());
                Console.WriteLine($"{path}: {map.HeightMap.Width}x{map.HeightMap.Height}, {map.Objects.Count} objects, " +
                                  $"{map.Sides.Sides.Count} sides, {map.Sides.Teams.Count} teams, {scripts} scripts, " +
                                  $"{map.PolygonTriggers.Count} triggers, {map.BlendTiles?.TextureClasses.Count ?? 0} textures");
                foreach (string label in map.UnreadChunks)
                    unread.Add(label);
            }
            catch (Exception e)
            {
                failures++;
                Console.WriteLine($"FAIL {path}: {e.GetType().Name}: {e.Message}");
            }
        }

        Console.WriteLine($"{count} maps, {failures} failed. Chunks not read: {string.Join(", ", unread)}");
        return failures == 0 ? 0 : 1;
    }
}

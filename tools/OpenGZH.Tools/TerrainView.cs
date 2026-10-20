using OpenGZH.Core.FileSystem;
using OpenGZH.Formats.Maps;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Tools;

/// <summary>Prints a map's terrain as text: '~' water, '#' cliff, '.' ground (one character per N cells).</summary>
static class TerrainView
{
    public static int Run(VirtualFileSystem fileSystem, string[] args)
    {
        var map = MapFile.Read(fileSystem.ReadAllBytes(args[0]));
        var terrain = new TerrainLogic(map);
        int step = args.Length > 1 ? int.Parse(args[1]) : 4;
        for (int y = terrain.CellsY - 1; y >= 0; y -= step)
        {
            var line = new System.Text.StringBuilder();
            for (int x = 0; x < terrain.CellsX; x += step)
                line.Append(terrain.IsWaterCell(x, y) ? '~' : terrain.IsCliffCell(x, y) ? '#' : '.');
            Console.WriteLine(line);
        }
        return 0;
    }
}

using System.Globalization;
using System.Numerics;
using System.Text;
using OpenGZH.Core.FileSystem;

namespace OpenGZH.Logic.Multiplayer;

/// <summary>What the menus know about a map without loading it (the original's MapMetaData).</summary>
public sealed class MapMetaData
{
    /// <summary>The map's path as the game stores it (lower case, forward slashes).</summary>
    public required string FileName { get; init; }
    public string NameLookupTag { get; set; } = "";
    public bool IsOfficial { get; set; }
    public bool IsMultiplayer { get; set; }
    public int NumPlayers { get; set; }
    public long FileSize { get; set; }
    public uint Crc { get; set; }
    public Vector3 ExtentMin { get; set; }
    public Vector3 ExtentMax { get; set; }
    public Vector3 InitialCameraPosition { get; set; }
    public Dictionary<string, Vector3> Waypoints { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Vector3> SupplyPositions { get; } = new();
    public List<Vector3> TechPositions { get; } = new();

    /// <summary>"Player_N_Start" (1-based N), if the map has it.</summary>
    public Vector3? StartPosition(int playerIndex) =>
        Waypoints.TryGetValue($"Player_{playerIndex + 1}_Start", out var p) ? p : null;
}

/// <summary>
/// The map list (the original's MapCache): the official maps' metadata from Maps/MapCache.ini.
/// Keys are lower-case map paths ("maps/alpine assault/alpine assault.map").
/// </summary>
public sealed class MapCache
{
    readonly Dictionary<string, MapMetaData> maps = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<MapMetaData> Maps => maps.Values;

    public static string Key(string mapPath) => mapPath.Replace('\\', '/').ToLowerInvariant();

    public MapMetaData? Find(string mapPath) => maps.GetValueOrDefault(Key(mapPath));

    public static MapCache Load(VirtualFileSystem fileSystem)
    {
        var cache = new MapCache();
        if (fileSystem.Exists("Maps/MapCache.ini"))
            cache.Parse(fileSystem.ReadAllText("Maps/MapCache.ini"));
        return cache;
    }

    /// <summary>Reads MapCache.ini ("MapCache &lt;quoted name&gt; ... END" blocks).</summary>
    public void Parse(string text)
    {
        MapMetaData? current = null;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int comment = line.IndexOf(';');
            if (comment >= 0)
                line = line[..comment].Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith("MapCache ", StringComparison.OrdinalIgnoreCase))
            {
                current = new MapMetaData { FileName = Key(Unquote(line[9..].Trim())) };
                continue;
            }
            if (line.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null)
                    maps[current.FileName] = current;
                current = null;
                continue;
            }
            if (current == null)
                continue;
            int eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "filesize": current.FileSize = long.TryParse(value, out long size) ? size : 0; break;
                case "filecrc": current.Crc = uint.TryParse(value, out uint crc) ? crc : 0; break;
                case "isofficial": current.IsOfficial = IsYes(value); break;
                case "ismultiplayer": current.IsMultiplayer = IsYes(value); break;
                case "numplayers": current.NumPlayers = int.TryParse(value, out int n) ? n : 0; break;
                case "extentmin": current.ExtentMin = Coord(value); break;
                case "extentmax": current.ExtentMax = Coord(value); break;
                case "namelookuptag": current.NameLookupTag = value; break;
                case "initialcameraposition": current.InitialCameraPosition = Coord(value); break;
                case "techposition": current.TechPositions.Add(Coord(value)); break;
                case "supplyposition": current.SupplyPositions.Add(Coord(value)); break;
                default:
                    if (key.StartsWith("Player_", StringComparison.OrdinalIgnoreCase))
                        current.Waypoints[key] = Coord(value);
                    break;
            }
        }
    }

    static bool IsYes(string value) => value.StartsWith("y", StringComparison.OrdinalIgnoreCase) || value == "1";

    /// <summary>"X:1.0 Y:2.0 Z:0.0".</summary>
    static Vector3 Coord(string value)
    {
        float x = 0, y = 0, z = 0;
        foreach (var part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.IndexOf(':');
            if (colon < 0 || !float.TryParse(part[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                continue;
            switch (char.ToUpperInvariant(part[0]))
            {
                case 'X': x = v; break;
                case 'Y': y = v; break;
                case 'Z': z = v; break;
            }
        }
        return new Vector3(x, y, z);
    }

    /// <summary>The original's QuotedPrintableToAsciiString: "_XX" is the character with hex code XX.</summary>
    public static string Unquote(string quoted)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < quoted.Length; i++)
        {
            if (quoted[i] == '_' && i + 2 < quoted.Length && int.TryParse(quoted.AsSpan(i + 1, 2), NumberStyles.HexNumber, null, out int code))
            {
                sb.Append((char)code);
                i += 2;
            }
            else
                sb.Append(quoted[i]);
        }
        return sb.ToString();
    }
}

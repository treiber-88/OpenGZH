using System.Numerics;
using OpenGZH.Formats.Compression;

namespace OpenGZH.Formats.Maps;

/// <summary>
/// A Generals / Zero Hour map (.map), as stored on disk.
///
/// World units: one height-map cell is 10 units wide (<see cref="CellSize"/>), and height values
/// (0-255) are multiplied by <see cref="HeightScale"/>.
/// </summary>
public sealed class MapFile
{
    public const float CellSize = 10f;
    public const float HeightScale = CellSize / 16f;

    public HeightMap HeightMap { get; set; } = new();
    public BlendTileData? BlendTiles { get; set; }
    public MapLighting Lighting { get; set; } = new();
    public List<MapObject> Objects { get; } = new();
    public Dictionary<string, object> WorldInfo { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SidesList Sides { get; set; } = new();
    public List<PolygonTrigger> PolygonTriggers { get; } = new();
    public List<(int From, int To)> WaypointLinks { get; } = new();

    /// <summary>Chunk labels that were present but not understood (for diagnostics).</summary>
    public List<string> UnreadChunks { get; } = new();

    public static MapFile Read(byte[] fileData)
    {
        var data = RefPack.DecompressIfNeeded(fileData);
        var chunks = new DataChunkReader(data);
        var map = new MapFile();
        var reader = chunks.Reader;

        foreach (var chunk in chunks.Chunks())
        {
            switch (chunk.Label)
            {
                case "HeightMapData":
                    map.HeightMap = HeightMap.Read(reader, chunk.Version);
                    break;
                case "BlendTileData":
                    map.BlendTiles = BlendTileData.Read(chunks, chunk, map.HeightMap);
                    break;
                case "GlobalLighting":
                    map.Lighting = MapLighting.Read(reader, chunk);
                    break;
                case "ObjectsList":
                    foreach (var objectChunk in chunks.SubChunks(chunk))
                        if (objectChunk.Label == "Object")
                            map.Objects.Add(MapObject.Read(chunks, objectChunk.Version));
                    break;
                case "WorldInfo":
                    map.WorldInfo = chunks.ReadDict();
                    break;
                case "SidesList":
                    map.Sides = SidesList.Read(chunks, chunk);
                    break;
                case "PolygonTriggers":
                    map.PolygonTriggers.AddRange(PolygonTrigger.ReadAll(chunks, chunk));
                    break;
                case "WaypointsList":
                    int links = reader.ReadInt32();
                    for (int i = 0; i < links; i++)
                        map.WaypointLinks.Add((reader.ReadInt32(), reader.ReadInt32()));
                    break;
                default:
                    map.UnreadChunks.Add(chunk.Label);
                    break;
            }
        }

        return map;
    }
}

/// <summary>Terrain heights, one byte per grid point.</summary>
public sealed class HeightMap
{
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Cells around the edge that are outside the playable area.</summary>
    public int BorderSize { get; set; }

    /// <summary>Playable areas (width, height) inside the border; multiplayer maps may have several.</summary>
    public List<(int X, int Y)> Boundaries { get; } = new();

    public byte[] Heights { get; set; } = [];

    public int RawHeight(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        return Heights[y * Width + x];
    }

    /// <summary>World-space height of a grid point.</summary>
    public float WorldHeight(int x, int y) => RawHeight(x, y) * MapFile.HeightScale;

    /// <summary>Interpolated world-space height at a world position (origin = grid point (border, border)).</summary>
    public float HeightAt(float worldX, float worldY)
    {
        float gx = worldX / MapFile.CellSize + BorderSize;
        float gy = worldY / MapFile.CellSize + BorderSize;
        int x = (int)MathF.Floor(gx), y = (int)MathF.Floor(gy);
        float fx = gx - x, fy = gy - y;
        float h00 = WorldHeight(x, y), h10 = WorldHeight(x + 1, y);
        float h01 = WorldHeight(x, y + 1), h11 = WorldHeight(x + 1, y + 1);
        return (h00 * (1 - fx) + h10 * fx) * (1 - fy) + (h01 * (1 - fx) + h11 * fx) * fy;
    }

    internal static HeightMap Read(BinaryReader reader, int version)
    {
        var map = new HeightMap
        {
            Width = reader.ReadInt32(),
            Height = reader.ReadInt32(),
        };
        map.BorderSize = version >= 3 ? reader.ReadInt32() : 0;
        if (version >= 4)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
                map.Boundaries.Add((reader.ReadInt32(), reader.ReadInt32()));
        }
        else
        {
            map.Boundaries.Add((map.Width - 2 * map.BorderSize, map.Height - 2 * map.BorderSize));
        }

        int size = reader.ReadInt32();
        map.Heights = reader.ReadBytes(size);

        if (version == 1)
        {
            // Version 1 maps stored twice the resolution; keep every second sample.
            int newWidth = (map.Width + 1) / 2, newHeight = (map.Height + 1) / 2;
            var resized = new byte[newWidth * newHeight];
            for (int y = 0; y < newHeight; y++)
                for (int x = 0; x < newWidth; x++)
                    resized[y * newWidth + x] = map.Heights[2 * y * map.Width + 2 * x];
            map.Width = newWidth;
            map.Height = newHeight;
            map.Heights = resized;
        }
        return map;
    }
}

/// <summary>A terrain texture used by the map, and where its 64x64 tiles start in the tile list.</summary>
public sealed record TextureClass(int FirstTile, int TileCount, int Width, string Name);

/// <summary>How a cell blends a second texture over its base texture.</summary>
public sealed record BlendInfo(
    int TileIndex, bool Horizontal, bool Vertical, bool RightDiagonal, bool LeftDiagonal,
    bool Inverted, bool Flipped, bool LongDiagonal, int CustomBlendEdgeClass);

/// <summary>Custom UVs for a stretched cliff cell (in units of the original 2048-pixel terrain atlas).</summary>
public sealed record CliffInfo(int TileIndex, Vector2[] Uv, bool Flip, bool Mutant);

/// <summary>Which texture tile each terrain cell shows, and how neighbouring textures blend.</summary>
public sealed class BlendTileData
{
    /// <summary>
    /// Per cell: tile number * 4 + quadrant. Each 64x64 source tile covers 2x2 cells; bit 0 selects
    /// the right half and bit 1 the upper half.
    /// </summary>
    public short[] TileIndices { get; set; } = [];

    /// <summary>Per cell: index into <see cref="Blends"/> (0 = no blend).</summary>
    public short[] BlendIndices { get; set; } = [];

    /// <summary>Per cell: a second blend layer (three-way blends).</summary>
    public short[] ExtraBlendIndices { get; set; } = [];

    /// <summary>Per cell: index into <see cref="CliffInfos"/> (0 = normal mapping).</summary>
    public short[] CliffInfoIndices { get; set; } = [];

    /// <summary>
    /// Impassable-cliff flags saved by the map editor (one bit per cell, rows of <see cref="CliffBytesPerRow"/> bytes).
    /// Null for older maps, which compute cliffs from the heights instead.
    /// </summary>
    public byte[]? CliffStateBits { get; set; }
    public int CliffBytesPerRow { get; set; }

    public bool IsCliffFlagged(int x, int y)
    {
        if (CliffStateBits == null)
            return false;
        int index = y * CliffBytesPerRow + (x >> 3);
        return index >= 0 && index < CliffStateBits.Length && (CliffStateBits[index] & (1 << (x & 7))) != 0;
    }

    public List<TextureClass> TextureClasses { get; } = new();
    public List<TextureClass> EdgeTextureClasses { get; } = new();

    /// <summary>Blend definitions; index 0 is unused (means "no blend").</summary>
    public List<BlendInfo?> Blends { get; } = new() { null };

    /// <summary>Cliff UV definitions; index 0 is unused.</summary>
    public List<CliffInfo?> CliffInfos { get; } = new() { null };

    public int BitmapTileCount { get; set; }

    /// <summary>The texture class that owns a source tile number.</summary>
    public TextureClass? ClassOfTile(int tile) =>
        TextureClasses.FirstOrDefault(c => tile >= c.FirstTile && tile < c.FirstTile + c.TileCount);

    const int FlagValue = 0x7ADA0000;

    internal static BlendTileData Read(DataChunkReader chunks, DataChunkReader.Chunk chunk, HeightMap heightMap)
    {
        var reader = chunks.Reader;
        int version = chunk.Version;
        var data = new BlendTileData();

        int count = reader.ReadInt32();
        data.TileIndices = ReadShorts(reader, count);
        data.BlendIndices = ReadShorts(reader, count);
        data.ExtraBlendIndices = version >= 6 ? ReadShorts(reader, count) : new short[count];
        data.CliffInfoIndices = version >= 5 ? ReadShorts(reader, count) : new short[count];

        if (version >= 7)
        {
            // Cliff state bit flags; version 7 used a slightly wrong row width.
            int bytesPerRow = version == 7 ? (heightMap.Width + 1) / 8 : (heightMap.Width + 7) / 8;
            data.CliffBytesPerRow = bytesPerRow;
            data.CliffStateBits = reader.ReadBytes(heightMap.Height * bytesPerRow);
        }

        data.BitmapTileCount = reader.ReadInt32();
        int blendCount = reader.ReadInt32();
        int cliffCount = version >= 5 ? reader.ReadInt32() : 1;

        int classCount = reader.ReadInt32();
        for (int i = 0; i < classCount; i++)
        {
            int first = reader.ReadInt32();
            int tiles = reader.ReadInt32();
            int width = reader.ReadInt32();
            reader.ReadInt32(); // legacy field
            data.TextureClasses.Add(new TextureClass(first, tiles, width, chunks.ReadAsciiString()));
        }

        if (version >= 4)
        {
            reader.ReadInt32(); // edge tile count
            int edgeClassCount = reader.ReadInt32();
            for (int i = 0; i < edgeClassCount; i++)
            {
                int first = reader.ReadInt32();
                int tiles = reader.ReadInt32();
                int width = reader.ReadInt32();
                data.EdgeTextureClasses.Add(new TextureClass(first, tiles, width, chunks.ReadAsciiString()));
            }
        }

        for (int i = 1; i < blendCount; i++)
        {
            int tile = reader.ReadInt32();
            bool horizontal = reader.ReadByte() != 0;
            bool vertical = reader.ReadByte() != 0;
            bool rightDiagonal = reader.ReadByte() != 0;
            bool leftDiagonal = reader.ReadByte() != 0;
            int inverted = reader.ReadByte();
            bool longDiagonal = version >= 3 && reader.ReadByte() != 0;
            int customEdge = version >= 4 ? reader.ReadInt32() : -1;
            if (reader.ReadInt32() != FlagValue)
                throw new InvalidDataException("Corrupt blend tile data.");
            data.Blends.Add(new BlendInfo(tile, horizontal, vertical, rightDiagonal, leftDiagonal,
                (inverted & 1) != 0, (inverted & 2) != 0, longDiagonal, customEdge));
        }

        if (version >= 5)
        {
            for (int i = 1; i < cliffCount; i++)
            {
                int tile = reader.ReadInt32();
                var uv = new Vector2[4];
                for (int c = 0; c < 4; c++)
                    uv[c] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                bool flip = reader.ReadByte() != 0;
                bool mutant = reader.ReadByte() != 0;
                data.CliffInfos.Add(new CliffInfo(tile, uv, flip, mutant));
            }
        }

        return data;
    }

    static short[] ReadShorts(BinaryReader reader, int count)
    {
        var result = new short[count];
        for (int i = 0; i < count; i++)
            result[i] = reader.ReadInt16();
        return result;
    }
}

/// <summary>One directional light (ambient + diffuse colors and the light's position/direction).</summary>
public struct MapLight
{
    public Vector3 Ambient;
    public Vector3 Diffuse;
    public Vector3 Direction;
}

/// <summary>Global lighting for each time of day: terrain lights and object lights (up to 3 each).</summary>
public sealed class MapLighting
{
    public enum TimeOfDay { Invalid = 0, Morning = 1, Afternoon = 2, Evening = 3, Night = 4 }

    public TimeOfDay CurrentTime { get; set; } = TimeOfDay.Afternoon;
    public MapLight[,] TerrainLights { get; } = new MapLight[4, 3];
    public MapLight[,] ObjectLights { get; } = new MapLight[4, 3];
    public uint ShadowColor { get; set; } = 0x7F000000;

    public int TimeIndex => Math.Clamp((int)CurrentTime - 1, 0, 3);

    internal static MapLighting Read(BinaryReader reader, DataChunkReader.Chunk chunk)
    {
        var lighting = new MapLighting { CurrentTime = (TimeOfDay)reader.ReadInt32() };
        for (int time = 0; time < 4; time++)
        {
            lighting.TerrainLights[time, 0] = ReadLight(reader);
            lighting.ObjectLights[time, 0] = ReadLight(reader);
            if (chunk.Version >= 2)
                for (int j = 1; j < 3; j++)
                    lighting.ObjectLights[time, j] = ReadLight(reader);
            if (chunk.Version >= 3)
                for (int j = 1; j < 3; j++)
                    lighting.TerrainLights[time, j] = ReadLight(reader);
        }
        if (reader.BaseStream.Position < chunk.End)
            lighting.ShadowColor = reader.ReadUInt32();
        return lighting;
    }

    static MapLight ReadLight(BinaryReader reader) => new()
    {
        Ambient = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
        Diffuse = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
        Direction = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
    };
}

/// <summary>An object placed on the map: a unit, building, prop, waypoint, light...</summary>
public sealed class MapObject
{
    public Vector3 Position { get; set; }

    /// <summary>Facing in radians.</summary>
    public float Angle { get; set; }

    public int Flags { get; set; }

    /// <summary>The object template name (e.g. "AmericaTankCrusader"), or "*Waypoints/Waypoint" for waypoints.</summary>
    public string TemplateName { get; set; } = "";

    /// <summary>Per-object properties: owner team, name, health, waypoint id, ...</summary>
    public Dictionary<string, object> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsWaypoint => Properties.ContainsKey("waypointID");
    public string? Get(string key) => Properties.TryGetValue(key, out var value) ? value.ToString() : null;

    internal static MapObject Read(DataChunkReader chunks, int version)
    {
        var reader = chunks.Reader;
        var obj = new MapObject
        {
            Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            Angle = reader.ReadSingle(),
            Flags = reader.ReadInt32(),
            TemplateName = chunks.ReadAsciiString(),
        };
        if (version <= 2)
            obj.Position = obj.Position with { Z = 0 };
        if (version >= 2)
            obj.Properties = chunks.ReadDict();
        return obj;
    }
}

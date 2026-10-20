using System.Numerics;
using OpenGZH.Formats.Maps;

namespace OpenGZH.Logic.Terrain;

/// <summary>
/// The map as the simulation sees it: ground heights, cliffs, and water.
///
/// World coordinates: X east, Y north, Z up. World (0,0) is the bottom-left corner of the
/// playable area (grid point (border, border)); one grid cell is 10 world units.
/// </summary>
public sealed class TerrainLogic
{
    /// <summary>A cell is a cliff (impassable for most units) if its corners differ in height by more than this.</summary>
    public const float CliffHeightLimit = 9.8f;

    public HeightMap Heights { get; }
    public BlendTileData? Tiles { get; }
    public List<WaterArea> WaterAreas { get; } = new();

    /// <summary>Size of the whole grid in cells (including the border).</summary>
    public int CellsX => Heights.Width - 1;
    public int CellsY => Heights.Height - 1;
    public int Border => Heights.BorderSize;

    /// <summary>Playable area size in world units.</summary>
    public float WorldWidth { get; }
    public float WorldHeight { get; }

    readonly bool[] cliffCells;

    public TerrainLogic(MapFile map)
    {
        Heights = map.HeightMap;
        Tiles = map.BlendTiles;
        var playable = Heights.Boundaries.Count > 0 ? Heights.Boundaries[0] : (Heights.Width - 2 * Border, Heights.Height - 2 * Border);
        WorldWidth = playable.Item1 * MapFile.CellSize;
        WorldHeight = playable.Item2 * MapFile.CellSize;

        cliffCells = new bool[CellsX * CellsY];
        for (int y = 0; y < CellsY; y++)
            for (int x = 0; x < CellsX; x++)
                cliffCells[y * CellsX + x] = (Tiles?.CliffStateBits != null ? Tiles.IsCliffFlagged(x, y) : false) || IsSteep(x, y);

        foreach (var trigger in map.PolygonTriggers.Where(t => t.IsWater))
            WaterAreas.Add(new WaterArea(trigger));
    }

    bool IsSteep(int x, int y)
    {
        float h1 = Heights.WorldHeight(x, y), h2 = Heights.WorldHeight(x + 1, y);
        float h3 = Heights.WorldHeight(x, y + 1), h4 = Heights.WorldHeight(x + 1, y + 1);
        float min = Math.Min(Math.Min(h1, h2), Math.Min(h3, h4));
        float max = Math.Max(Math.Max(h1, h2), Math.Max(h3, h4));
        return max - min > CliffHeightLimit;
    }

    // ---- Coordinates ----

    /// <summary>Grid cell containing a world position.</summary>
    public (int X, int Y) CellAt(float worldX, float worldY) =>
        ((int)MathF.Floor(worldX / MapFile.CellSize) + Border, (int)MathF.Floor(worldY / MapFile.CellSize) + Border);

    /// <summary>World position of the center of a grid cell.</summary>
    public Vector3 CellCenter(int cellX, int cellY)
    {
        float x = (cellX - Border + 0.5f) * MapFile.CellSize;
        float y = (cellY - Border + 0.5f) * MapFile.CellSize;
        return new Vector3(x, y, GroundHeight(x, y));
    }

    public bool IsInsideGrid(int cellX, int cellY) => cellX >= 0 && cellY >= 0 && cellX < CellsX && cellY < CellsY;

    /// <summary>True if the world position is inside the playable area (units may not leave it).</summary>
    public bool IsInsidePlayableArea(float x, float y) => x >= 0 && y >= 0 && x < WorldWidth && y < WorldHeight;

    float maxTerrainHeight = float.NaN;

    float MaxTerrainHeight
    {
        get
        {
            if (float.IsNaN(maxTerrainHeight))
            {
                float max = 0;
                for (int y = 0; y < Heights.Height; y++)
                    for (int x = 0; x < Heights.Width; x++)
                        max = Math.Max(max, Heights.WorldHeight(x, y));
                maxTerrainHeight = max;
            }
            return maxTerrainHeight;
        }
    }

    /// <summary>
    /// Whether terrain blocks the straight line between two points: the original's Bresenham walk over
    /// the height grid, testing the highest corner of each cell against the line (with 0.5 slack).
    /// </summary>
    public bool IsClearLineOfSight(Vector3 from, Vector3 to)
    {
        int border = Border;
        int startX = (int)MathF.Floor(from.X / MapFile.CellSize) + border, startY = (int)MathF.Floor(from.Y / MapFile.CellSize) + border;
        int endX = (int)MathF.Floor(to.X / MapFile.CellSize) + border, endY = (int)MathF.Floor(to.Y / MapFile.CellSize) + border;
        int deltaX = Math.Abs(endX - startX), deltaY = Math.Abs(endY - startY);
        int x = startX, y = startY;
        int xinc1 = endX >= startX ? 1 : -1, xinc2 = xinc1;
        int yinc1 = endY >= startY ? 1 : -1, yinc2 = yinc1;
        int den, num, numAdd, pixels;
        if (deltaX >= deltaY)
        {
            xinc1 = 0; yinc2 = 0;
            den = deltaX; num = deltaX / 2; numAdd = deltaY; pixels = deltaX;
        }
        else
        {
            xinc2 = 0; yinc1 = 0;
            den = deltaY; num = deltaY / 2; numAdd = deltaX; pixels = deltaY;
        }

        float z = from.Z;
        float zinc = (to.Z - z) / pixels;
        for (int i = 0; i < pixels; i++)
        {
            if (x < 0 || y < 0 || x >= Heights.Width - 1 || y >= Heights.Height - 1)
                break; // off the map
            float height = Math.Max(Math.Max(Heights.WorldHeight(x, y), Heights.WorldHeight(x + 1, y)),
                                    Math.Max(Heights.WorldHeight(x, y + 1), Heights.WorldHeight(x + 1, y + 1)));
            const float Fudge = 0.5f;
            if (height > z + Fudge)
                return false;
            if (z >= MaxTerrainHeight && zinc > 0)
                break; // above all terrain and still rising
            z += zinc;
            num += numAdd;
            if (num >= den)
            {
                num -= den;
                x += xinc1;
                y += yinc1;
            }
            x += xinc2;
            y += yinc2;
        }
        return true;
    }

    /// <summary>Line of sight between the tops of two objects (eyes are near the head, not the feet).</summary>
    public bool HasLineOfSight(Objects.GameObject viewer, Objects.GameObject other) =>
        IsClearLineOfSight(viewer.Position + new Vector3(0, 0, viewer.Height), other.Position + new Vector3(0, 0, other.Height));

    /// <summary>The point on the playable area's edge nearest to a position, on the ground (where airstrikes come in).</summary>
    public Vector3 ClosestEdgePoint(Vector3 to)
    {
        // Same order as the original: top, right, bottom, left; the first of equal distances wins.
        float[] distances = [MathF.Abs(to.Y), MathF.Abs(to.X - WorldWidth), MathF.Abs(to.Y - WorldHeight), MathF.Abs(to.X)];
        int best = 0;
        for (int i = 1; i < 4; i++)
            if (distances[i] < distances[best])
                best = i;
        var point = best switch
        {
            0 => to with { Y = 0 },
            1 => to with { X = WorldWidth },
            2 => to with { Y = WorldHeight },
            _ => to with { X = 0 },
        };
        return point with { Z = GroundHeight(point.X, point.Y) };
    }

    /// <summary>The playable area's corner farthest from a position, on the ground.</summary>
    public Vector3 FarthestEdgePoint(Vector3 from)
    {
        float x = from.X < WorldWidth / 2 ? WorldWidth : 0;
        float y = from.Y < WorldHeight / 2 ? WorldHeight : 0;
        return new Vector3(x, y, GroundHeight(x, y));
    }

    // ---- Heights ----

    public float GroundHeight(float worldX, float worldY) => Heights.HeightAt(worldX, worldY);

    public float GroundHeight(Vector3 position) => GroundHeight(position.X, position.Y);

    /// <summary>The ground's surface normal at a position (for tilting vehicles on slopes).</summary>
    public Vector3 GroundNormal(float worldX, float worldY)
    {
        const float d = MapFile.CellSize * 0.5f;
        float dx = GroundHeight(worldX + d, worldY) - GroundHeight(worldX - d, worldY);
        float dy = GroundHeight(worldX, worldY + d) - GroundHeight(worldX, worldY - d);
        return Vector3.Normalize(new Vector3(-dx, -dy, 2 * d));
    }

    // ---- Cliffs and water ----

    public bool IsCliffCell(int cellX, int cellY) =>
        IsInsideGrid(cellX, cellY) && cliffCells[cellY * CellsX + cellX];

    /// <summary>The water area covering a point, or null.</summary>
    public WaterArea? WaterAt(float worldX, float worldY)
    {
        foreach (var water in WaterAreas)
            if (water.Contains(worldX, worldY))
                return water;
        return null;
    }

    /// <summary>True if there is water here that is above the ground (i.e. ships can float, tanks would drown).</summary>
    public bool IsUnderwater(float worldX, float worldY, out float waterHeight)
    {
        var water = WaterAt(worldX, worldY);
        waterHeight = water?.Height ?? 0;
        return water != null && water.Height > GroundHeight(worldX, worldY);
    }

    /// <summary>Is this grid cell water deep enough that its center is underwater?</summary>
    public bool IsWaterCell(int cellX, int cellY)
    {
        var center = CellCenter(cellX, cellY);
        return IsUnderwater(center.X, center.Y, out _);
    }

    /// <summary>Height of the surface units stand on: water level if over water, else the ground.</summary>
    public float LayerHeight(float worldX, float worldY) =>
        IsUnderwater(worldX, worldY, out float water) ? water : GroundHeight(worldX, worldY);
}

/// <summary>A body of water, defined on the map as a polygon trigger flagged as water.</summary>
public sealed class WaterArea
{
    public PolygonTrigger Polygon { get; }

    /// <summary>The water level, in world units (the polygon's first point's height, as in the original).</summary>
    public float Height { get; set; }

    readonly float minX, minY, maxX, maxY;

    public WaterArea(PolygonTrigger polygon)
    {
        Polygon = polygon;
        Height = polygon.Points.Count > 0 ? polygon.Points[0].Z : 0;
        minX = polygon.Points.Min(p => p.X);
        minY = polygon.Points.Min(p => p.Y);
        maxX = polygon.Points.Max(p => p.X);
        maxY = polygon.Points.Max(p => p.Y);
    }

    public bool Contains(float x, float y) =>
        x >= minX && x <= maxX && y >= minY && y <= maxY && Polygon.Contains(x, y);
}

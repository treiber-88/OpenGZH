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

    /// <summary>Bridges over the terrain; each is a pathfinding layer above the ground.</summary>
    public BridgeList Bridges { get; } = new();
    public BlendTileData? Tiles { get; }
    public List<WaterArea> WaterAreas { get; } = new();

    /// <summary>Size of the whole grid in cells (including the border).</summary>
    public int CellsX => Heights.Width - 1;
    public int CellsY => Heights.Height - 1;
    public int Border => Heights.BorderSize;

    /// <summary>Playable area size in world units.</summary>
    public float WorldWidth { get; private set; }
    public float WorldHeight { get; private set; }

    readonly List<(int X, int Y)> boundaries = new();
    int activeBoundary;

    /// <summary>
    /// The playable area in use (MAP_SWITCH_BORDER): maps define several, scripts grow the map as a
    /// mission goes on. An undefined or empty boundary is ignored, as in the original.
    /// </summary>
    public int ActiveBoundary
    {
        get => activeBoundary;
        set
        {
            if (value < 0 || value >= boundaries.Count || value == activeBoundary)
                return;
            if (boundaries[value].X == 0 || boundaries[value].Y == 0)
                return;
            activeBoundary = value;
            WorldWidth = boundaries[value].X * MapFile.CellSize;
            WorldHeight = boundaries[value].Y * MapFile.CellSize;
            BoundaryChanged?.Invoke();
        }
    }

    public event Action? BoundaryChanged;

    // ---- Water levels that scripts change (WATER_CHANGE_HEIGHT ...) ----

    sealed class WaterChange
    {
        public required WaterArea Water;
        public float ChangePerFrame, TargetHeight, DamageAmount, CurrentHeight;
    }

    const int MaxDynamicWater = 64;
    readonly List<WaterChange> waterToUpdate = new();

    /// <summary>Raised when water rises: everything under it takes this much WATER damage.</summary>
    public event Action<WaterArea, float>? WaterDamage;
    public event Action? WaterChanged;

    public WaterArea? FindWater(string name) => WaterAreas.FirstOrDefault(w => w.Polygon.Name == name);

    /// <summary>The original's setWaterHeight: the area's level moves; rising water damages what it covers.</summary>
    public void SetWaterHeight(WaterArea water, float height, float damageAmount)
    {
        float previous = water.Height;
        water.Height = height;
        for (int i = 0; i < water.Polygon.Points.Count; i++)
            water.Polygon.Points[i] = water.Polygon.Points[i] with { Z = height };
        if (previous != height)
            WaterChanged?.Invoke();
        if (damageAmount > 0 && height > previous)
            WaterDamage?.Invoke(water, damageAmount);
    }

    /// <summary>WATER_CHANGE_HEIGHT: at once (with the original's huge damage to anything drowned).</summary>
    public void SetWaterHeight(string name, float height)
    {
        if (FindWater(name) is { } water)
            SetWaterHeight(water, height, 999999.9f);
    }

    /// <summary>WATER_CHANGE_HEIGHT_OVER_TIME: the level moves steadily; damage is dealt once a second.</summary>
    public void ChangeWaterHeightOverTime(string name, float finalHeight, float seconds, float damageAmount)
    {
        if (waterToUpdate.Count >= MaxDynamicWater || FindWater(name) is not { } water)
            return;
        waterToUpdate.RemoveAll(w => w.Water == water);
        waterToUpdate.Add(new WaterChange
        {
            Water = water,
            ChangePerFrame = (finalHeight - water.Height) / (Core.LogicTime.FramesPerSecond * Math.Max(0.001f, seconds)),
            TargetHeight = finalHeight,
            DamageAmount = damageAmount,
            CurrentHeight = water.Height,
        });
    }

    /// <summary>Moves changing water levels on by a frame (the original's TerrainLogic::update).</summary>
    public void Update(int frame)
    {
        bool doDamageThisFrame = frame % Core.LogicTime.FramesPerSecond == 0;
        for (int i = waterToUpdate.Count - 1; i >= 0; i--)
        {
            var w = waterToUpdate[i];
            bool final = w.ChangePerFrame > 0 ? w.CurrentHeight + w.ChangePerFrame >= w.TargetHeight : w.CurrentHeight + w.ChangePerFrame <= w.TargetHeight;
            if (final)
            {
                SetWaterHeight(w.Water, w.TargetHeight, w.DamageAmount);
                waterToUpdate.RemoveAt(i);
            }
            else
            {
                w.CurrentHeight += w.ChangePerFrame;
                SetWaterHeight(w.Water, w.CurrentHeight, doDamageThisFrame ? w.DamageAmount : 0);
            }
        }
    }

    readonly bool[] cliffCells;

    public TerrainLogic(MapFile map)
    {
        Heights = map.HeightMap;
        Tiles = map.BlendTiles;
        var playable = Heights.Boundaries.Count > 0 ? Heights.Boundaries[0] : (Heights.Width - 2 * Border, Heights.Height - 2 * Border);
        WorldWidth = playable.Item1 * MapFile.CellSize;
        WorldHeight = playable.Item2 * MapFile.CellSize;
        foreach (var b in Heights.Boundaries)
            boundaries.Add((b.Item1, b.Item2));

        cliffCells = new bool[CellsX * CellsY];
        for (int y = 0; y < CellsY; y++)
            for (int x = 0; x < CellsX; x++)
                cliffCells[y * CellsX + x] = (Tiles?.CliffStateBits != null ? Tiles.IsCliffFlagged(x, y) : false) || IsSteep(x, y);

        foreach (var trigger in map.PolygonTriggers.Where(t => t.IsWater))
            WaterAreas.Add(new WaterArea(trigger));
        foreach (var trigger in map.PolygonTriggers)
            TriggerAreas.Add(new TriggerArea(trigger));
    }

    /// <summary>Every named area on the map (water areas included, as in the original).</summary>
    public List<TriggerArea> TriggerAreas { get; } = new();

    public TriggerArea? FindTriggerArea(string name) => TriggerAreas.FirstOrDefault(t => t.Name == name);

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

    /// <summary>The highest surface at a point: a bridge deck if there is one, else ground or water.</summary>
    public float HighestLayerHeight(float worldX, float worldY)
    {
        float h = LayerHeight(worldX, worldY);
        var p = new System.Numerics.Vector3(worldX, worldY, 0);
        foreach (var bridge in Bridges.All)
            if (bridge.Info.CurDamageState != Modules.BodyDamageState.Rubble && bridge.IsPointOnBridge(p))
                h = Math.Max(h, bridge.GetBridgeHeight(p));
        return h;
    }

    /// <summary>
    /// Height of a layer's surface (the original's getLayerHeight): a bridge's deck for layers 1 and up
    /// (only over the deck when <paramref name="clip"/>), otherwise the ground or water.
    /// </summary>
    public float LayerHeight(float worldX, float worldY, int layer, bool clip = true)
    {
        if (layer > 0 && Bridges.ForLayer(layer) is { } bridge)
        {
            var p = new System.Numerics.Vector3(worldX, worldY, 0);
            if (!clip || bridge.IsPointOnBridge(p))
                return bridge.GetBridgeHeight(p);
        }
        return LayerHeight(worldX, worldY);
    }
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

/// <summary>
/// A named area drawn on the map (the original's PolygonTrigger): scripts ask what is inside it,
/// what entered or left it. Points are whole world units, as the original stores them.
/// </summary>
public sealed class TriggerArea
{
    readonly (int X, int Y)[] points;
    readonly int loX, loY, hiX, hiY;

    public string Name { get; }
    public int Id { get; }
    public bool IsWater { get; }

    /// <summary>The middle of the bounding box.</summary>
    public Vector2 Center { get; }

    /// <summary>The original's radius (it adds the y bounds where it means to subtract them; kept).</summary>
    public float Radius { get; }

    public TriggerArea(Formats.Maps.PolygonTrigger trigger)
    {
        Name = trigger.Name;
        Id = trigger.Id;
        IsWater = trigger.IsWater;
        points = trigger.Points.Select(p => ((int)p.X, (int)p.Y)).ToArray();
        loX = loY = 0x7ffff0;
        hiX = hiY = -0x7ffff0;
        foreach (var (x, y) in points)
        {
            loX = Math.Min(loX, x); loY = Math.Min(loY, y);
            hiX = Math.Max(hiX, x); hiY = Math.Max(hiY, y);
        }
        Center = new Vector2((loX + hiX) / 2f, (loY + hiY) / 2f);
        float halfWidth = (hiX - loX) / 2f;
        float halfHeight = (hiY + loY) / 2f;
        Radius = MathF.Sqrt(halfHeight * halfHeight + halfWidth * halfWidth);
    }

    /// <summary>The original's pointInTrigger (even-odd crossing test on whole coordinates).</summary>
    public bool Contains(int px, int py)
    {
        if (px < loX || py < loY || px > hiX || py > hiY)
            return false;
        bool inside = false;
        for (int i = 0; i < points.Length; i++)
        {
            var p1 = points[i];
            var p2 = points[i == points.Length - 1 ? 0 : i + 1];
            if (p1.Y == p2.Y)
                continue;
            if (p1.Y < py && p2.Y < py) continue;
            if (p1.Y >= py && p2.Y >= py) continue;
            if (p1.X < px && p2.X < px) continue;
            int dy = p2.Y - p1.Y, dx = p2.X - p1.X;
            float intersectionX = p1.X + dx * (py - p1.Y) / (float)dy;
            if (intersectionX >= px)
                inside = !inside;
        }
        return inside;
    }

    public bool Contains(Vector3 p) => Contains((int)p.X, (int)p.Y);

    public override string ToString() => Name;
}

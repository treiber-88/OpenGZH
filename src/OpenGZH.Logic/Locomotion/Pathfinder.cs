using System.Numerics;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic.Locomotion;

/// <summary>
/// Finds routes over the map grid (one cell = 10 world units, as in the original pathfinder).
///
/// Every cell has a type: clear ground, water, cliff, or blocked by a structure. A unit may enter a
/// cell if its locomotor's surfaces allow it: tanks need ground, ships need water, hovercraft accept
/// both, and aircraft fly straight over everything.
/// </summary>
public sealed class Pathfinder
{
    public enum CellType : byte { Clear, Water, Cliff, Obstacle }

    readonly TerrainLogic terrain;
    readonly int width, height;
    readonly CellType[] baseCells;
    readonly int[] obstacleCount;

    public Pathfinder(TerrainLogic terrain)
    {
        this.terrain = terrain;
        width = terrain.CellsX;
        height = terrain.CellsY;
        baseCells = new CellType[width * height];
        obstacleCount = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                baseCells[y * width + x] = terrain.IsWaterCell(x, y) ? CellType.Water
                    : terrain.IsCliffCell(x, y) ? CellType.Cliff
                    : CellType.Clear;
            }
        }
    }

    public int Width => width;
    public int Height => height;

    public CellType TypeAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
            return CellType.Obstacle;
        int i = y * width + x;
        return obstacleCount[i] > 0 ? CellType.Obstacle : baseCells[i];
    }

    /// <summary>
    /// Whether a locomotor may be on this spot at all (the original's validMovementTerrain): only
    /// the terrain type matters, so cells under buildings count as valid and off-map cells don't.
    /// </summary>
    public bool IsValidMovementTerrain(Vector3 world, Surfaces surfaces)
    {
        var (x, y) = terrain.CellAt(world.X, world.Y);
        if (x < 0 || y < 0 || x >= width || y >= height)
            return false;
        int i = y * width + x;
        if (obstacleCount[i] > 0)
            return true;
        return baseCells[i] switch
        {
            CellType.Clear => (surfaces & (Surfaces.Ground | Surfaces.Rubble)) != 0,
            CellType.Water => (surfaces & Surfaces.Water) != 0,
            CellType.Cliff => (surfaces & Surfaces.Cliff) != 0,
            _ => (surfaces & Surfaces.Air) != 0,
        };
    }

    public bool IsPassable(int x, int y, Surfaces surfaces)
    {
        if ((surfaces & Surfaces.Air) != 0)
            return x >= 0 && y >= 0 && x < width && y < height;
        return TypeAt(x, y) switch
        {
            CellType.Clear => (surfaces & (Surfaces.Ground | Surfaces.Rubble)) != 0,
            CellType.Water => (surfaces & Surfaces.Water) != 0,
            CellType.Cliff => (surfaces & Surfaces.Cliff) != 0,
            _ => false,
        };
    }

    public bool IsPassable(Vector3 world, Surfaces surfaces)
    {
        var (x, y) = terrain.CellAt(world.X, world.Y);
        return IsPassable(x, y, surfaces);
    }

    // ---- Structures block the cells under their footprint ----

    public void AddObstacle(GameObject obj) => MarkFootprint(obj, +1);
    public void RemoveObstacle(GameObject obj) => MarkFootprint(obj, -1);

    void MarkFootprint(GameObject obj, int delta)
    {
        foreach (var (x, y) in FootprintCells(obj.Position, obj.Orientation, obj.Template.GeometryName, obj.Template.GeometryMajorRadius, obj.Template.GeometryMinorRadius))
            obstacleCount[y * width + x] = Math.Max(0, obstacleCount[y * width + x] + delta);
    }

    /// <summary>Grid cells covered by an object's footprint (a box or a circle on the ground).</summary>
    public IEnumerable<(int X, int Y)> FootprintCells(Vector3 position, float orientation, string geometry, float major, float minor)
    {
        bool box = geometry.Equals("BOX", StringComparison.OrdinalIgnoreCase);
        float reach = box ? MathF.Sqrt(major * major + minor * minor) : major;
        var (x0, y0) = terrain.CellAt(position.X - reach, position.Y - reach);
        var (x1, y1) = terrain.CellAt(position.X + reach, position.Y + reach);
        float cos = MathF.Cos(-orientation), sin = MathF.Sin(-orientation);

        for (int y = Math.Max(0, y0); y <= Math.Min(height - 1, y1); y++)
        {
            for (int x = Math.Max(0, x0); x <= Math.Min(width - 1, x1); x++)
            {
                var center = terrain.CellCenter(x, y);
                float dx = center.X - position.X, dy = center.Y - position.Y;
                bool inside;
                if (box)
                {
                    // Rotate into the object's local frame and test against the half extents.
                    float lx = dx * cos - dy * sin, ly = dx * sin + dy * cos;
                    inside = MathF.Abs(lx) <= major && MathF.Abs(ly) <= minor;
                }
                else
                {
                    inside = dx * dx + dy * dy <= major * major;
                }
                if (inside)
                    yield return (x, y);
            }
        }
    }

    // ---- A* search ----

    /// <summary>
    /// A route from start to goal as world-space waypoints (the start itself is not included).
    /// If the goal cannot be reached, routes to the closest reachable cell instead. Empty if stuck.
    /// </summary>
    public List<Vector3> FindPath(Vector3 start, Vector3 goal, Surfaces surfaces, int maxSearchCells = 40000)
    {
        if ((surfaces & Surfaces.Air) != 0)
            return [goal];

        var (sx, sy) = terrain.CellAt(start.X, start.Y);
        var (gx, gy) = terrain.CellAt(goal.X, goal.Y);
        sx = Math.Clamp(sx, 0, width - 1); sy = Math.Clamp(sy, 0, height - 1);
        gx = Math.Clamp(gx, 0, width - 1); gy = Math.Clamp(gy, 0, height - 1);

        if (sx == gx && sy == gy)
            return [goal];

        // A unit standing in a blocked cell (e.g. just produced inside a factory footprint) may leave it.
        int startIndex = sy * width + sx;
        int goalIndex = gy * width + gx;
        var cameFrom = new Dictionary<int, int>();
        var cost = new Dictionary<int, float> { [startIndex] = 0 };
        var open = new PriorityQueue<int, float>();
        open.Enqueue(startIndex, Heuristic(sx, sy, gx, gy));

        int best = startIndex;
        float bestDistance = Heuristic(sx, sy, gx, gy);
        int searched = 0;

        while (open.Count > 0 && searched++ < maxSearchCells)
        {
            int current = open.Dequeue();
            if (current == goalIndex)
            {
                best = current;
                break;
            }

            int cx = current % width, cy = current / width;
            float h = Heuristic(cx, cy, gx, gy);
            if (h < bestDistance)
            {
                bestDistance = h;
                best = current;
            }

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;
                    int nx = cx + dx, ny = cy + dy;
                    if (!IsPassable(nx, ny, surfaces))
                        continue;
                    // No corner cutting past blocked cells.
                    if (dx != 0 && dy != 0 && (!IsPassable(cx + dx, cy, surfaces) || !IsPassable(cx, cy + dy, surfaces)))
                        continue;

                    int next = ny * width + nx;
                    float stepCost = dx != 0 && dy != 0 ? 1.4142f : 1f;
                    float newCost = cost[current] + stepCost;
                    if (cost.TryGetValue(next, out float old) && old <= newCost)
                        continue;
                    cost[next] = newCost;
                    cameFrom[next] = current;
                    open.Enqueue(next, newCost + Heuristic(nx, ny, gx, gy));
                }
            }
        }

        if (best == startIndex)
            return [];

        var cells = new List<int>();
        for (int at = best; at != startIndex; at = cameFrom[at])
            cells.Add(at);
        cells.Reverse();

        var path = Simplify(cells, start, surfaces);
        // Finish exactly on the requested goal if it was reached.
        if (best == goalIndex)
            path[^1] = goal;
        return path;
    }

    static float Heuristic(int x, int y, int gx, int gy)
    {
        int dx = Math.Abs(x - gx), dy = Math.Abs(y - gy);
        return Math.Max(dx, dy) + 0.4142f * Math.Min(dx, dy);
    }

    /// <summary>Removes waypoints that can be skipped with a straight, passable line (smoother movement).</summary>
    List<Vector3> Simplify(List<int> cells, Vector3 start, Surfaces surfaces)
    {
        var points = cells.Select(c => terrain.CellCenter(c % width, c / width)).ToList();
        var result = new List<Vector3>();
        var from = start;
        int i = 0;
        while (i < points.Count)
        {
            int farthest = i;
            for (int j = points.Count - 1; j > i; j--)
            {
                if (IsLineClear(from, points[j], surfaces))
                {
                    farthest = j;
                    break;
                }
            }
            result.Add(points[farthest]);
            from = points[farthest];
            i = farthest + 1;
        }
        return result;
    }

    /// <summary>True if a straight line between two points only crosses passable cells.</summary>
    public bool IsLineClear(Vector3 a, Vector3 b, Surfaces surfaces)
    {
        float length = Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
        int steps = Math.Max(1, (int)(length / 4f));
        for (int s = 1; s <= steps; s++)
        {
            var p = Vector3.Lerp(a, b, s / (float)steps);
            var (x, y) = terrain.CellAt(p.X, p.Y);
            if (!IsPassable(x, y, surfaces))
                return false;
        }
        return true;
    }

    /// <summary>The nearest cell to a point that a unit with these surfaces may stand on.</summary>
    public Vector3? NearestPassable(Vector3 point, Surfaces surfaces, int searchRadiusCells = 20)
    {
        var (cx, cy) = terrain.CellAt(point.X, point.Y);
        if (IsPassable(cx, cy, surfaces))
            return point;
        for (int r = 1; r <= searchRadiusCells; r++)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if ((Math.Abs(dx) == r || Math.Abs(dy) == r) && IsPassable(cx + dx, cy + dy, surfaces))
                        return terrain.CellCenter(cx + dx, cy + dy);
        }
        return null;
    }
}

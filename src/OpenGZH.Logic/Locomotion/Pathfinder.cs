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
        foreach (var bridge in terrain.Bridges.All)
            AddBridge(bridge);
        terrain.Bridges.BridgeAdded += AddBridge;
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

    /// <summary>The first locomotor of a set that suits the cell at a position (water locomotor on water...).</summary>
    public Locomotor? ChooseBestLocomotorForPosition(IReadOnlyList<Locomotor> set, Vector3 world)
    {
        var (x, y) = terrain.CellAt(world.X, world.Y);
        var type = x < 0 || y < 0 || x >= width || y >= height ? CellType.Clear : TypeAt(x, y);
        var acceptable = type switch
        {
            CellType.Clear => Surfaces.Ground | Surfaces.Air,
            CellType.Water => Surfaces.Water | Surfaces.Air,
            CellType.Cliff => Surfaces.Cliff | Surfaces.Air,
            _ => Surfaces.Air,
        };
        return set.FirstOrDefault(l => (l.Surfaces & acceptable) != 0);
    }

    /// <summary>
    /// Whether a locomotor may be on this spot at all (the original's validMovementTerrain): only
    /// the terrain type matters, so cells under buildings count as valid and off-map cells don't.
    /// </summary>
    public bool IsValidMovementTerrain(Vector3 world, Surfaces surfaces, int layer = 0)
    {
        if (layer > 0 && IsOnLayer(world, layer, surfaces))
            return true;
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

    /// <summary>
    /// Puts an object on a layer, if it is really on that bridge at deck height; else on the ground
    /// (the original's Pathfinder::updateLayer).
    /// </summary>
    public void UpdateLayer(GameObject obj, int layer)
    {
        if (layer != 0 && !terrain.Bridges.ObjectInteractsWithBridgeLayer(obj, layer))
            layer = 0;
        obj.Layer = layer;
    }

    // ---- Bridge layers ----
    //
    // Each bridge is a layer of cells above the ground (the original's PathfindLayer). Search states
    // 0..W*H-1 are ground cells; the states after them are bridge-layer cells. Units get on and off a
    // bridge at its end cells, where a layer cell joins the ground cell below it.

    readonly List<int> layerStateCell = new();
    readonly List<int> layerStateLayer = new();
    readonly List<bool> layerStateConnects = new();
    readonly List<Dictionary<int, int>> layerStates = new() { new() }; // [layer][cell] -> state (layer 0 unused)
    readonly Dictionary<int, List<int>> connectionsAt = new();         // ground cell -> layer states joined to it

    int StateCount => width * height + layerStateCell.Count;

    /// <summary>Builds a bridge's layer (the original's PathfindLayer::init).</summary>
    public void AddBridge(Bridge bridge)
    {
        while (layerStates.Count <= bridge.Layer)
            layerStates.Add(new());
        var states = layerStates[bridge.Layer];
        var (x0, y0) = terrain.CellAt(bridge.BoundsLo.X, bridge.BoundsLo.Y);
        var (x1, y1) = terrain.CellAt(bridge.BoundsHi.X, bridge.BoundsHi.Y);
        var cellLo = terrain.CellCenter(0, 0) - new Vector3(Formats.Maps.MapFile.CellSize / 2, Formats.Maps.MapFile.CellSize / 2, 0);
        const float cell = Formats.Maps.MapFile.CellSize;
        for (int y = Math.Max(0, y0 - 1); y <= Math.Min(height - 1, y1 + 1); y++)
        {
            for (int x = Math.Max(0, x0 - 1); x <= Math.Min(width - 1, x1 + 1); x++)
            {
                var center = terrain.CellCenter(x, y);
                var lo = new Vector2(cellLo.X + x * cell, cellLo.Y + y * cell);
                var hi = lo + new Vector2(cell, cell);
                bool onEnd = bridge.IsCellOnEnd(lo, hi);
                if (!bridge.IsPointOnBridge(center) && !onEnd)
                    continue;
                int index = y * width + x;
                int state = width * height + layerStateCell.Count;
                layerStateCell.Add(index);
                layerStateLayer.Add(bridge.Layer);
                layerStateConnects.Add(onEnd);
                states[index] = state;
                if (onEnd)
                {
                    if (!connectionsAt.TryGetValue(index, out var list))
                        connectionsAt[index] = list = new();
                    list.Add(state);
                }
            }
        }
    }

    bool IsLayerUsable(int layer, Surfaces surfaces) =>
        (surfaces & (Surfaces.Ground | Surfaces.Rubble)) != 0
        && terrain.Bridges.ForLayer(layer) is { } bridge && !bridge.PathfindingClosed;

    int StateCell(int state) => state < width * height ? state : layerStateCell[state - width * height];
    int StateLayer(int state) => state < width * height ? 0 : layerStateLayer[state - width * height];

    /// <summary>Is a point on a usable bridge deck of this layer (or at its end)?</summary>
    public bool IsOnLayer(Vector3 world, int layer, Surfaces surfaces)
    {
        if (layer <= 0 || layer >= layerStates.Count || !IsLayerUsable(layer, surfaces))
            return false;
        var (x, y) = terrain.CellAt(world.X, world.Y);
        return x >= 0 && y >= 0 && x < width && y < height && layerStates[layer].ContainsKey(y * width + x);
    }

    // ---- A* search ----

    /// <summary>
    /// A route from start to goal as world-space waypoints (the start itself is not included).
    /// If the goal cannot be reached, routes to the closest reachable cell instead. Empty if stuck.
    /// </summary>
    public List<Vector3> FindPath(Vector3 start, Vector3 goal, Surfaces surfaces, int maxSearchCells = 40000) =>
        FindLayeredPath(start, goal, surfaces, 0, maxSearchCells).Select(p => p.Position).ToList();

    /// <summary>As <see cref="FindPath"/>, with the layer (0 = ground, 1+ = bridge) of every waypoint.</summary>
    public List<(Vector3 Position, int Layer)> FindLayeredPath(Vector3 start, Vector3 goal, Surfaces surfaces, int startLayer, int maxSearchCells = 40000)
    {
        if ((surfaces & Surfaces.Air) != 0)
            return [(goal, 0)];

        var (sx, sy) = terrain.CellAt(start.X, start.Y);
        var (gx, gy) = terrain.CellAt(goal.X, goal.Y);
        sx = Math.Clamp(sx, 0, width - 1); sy = Math.Clamp(sy, 0, height - 1);
        gx = Math.Clamp(gx, 0, width - 1); gy = Math.Clamp(gy, 0, height - 1);

        int startCell = sy * width + sx;
        int goalIndex = gy * width + gx;
        int startIndex = startCell;
        if (startLayer > 0 && startLayer < layerStates.Count && layerStates[startLayer].TryGetValue(startCell, out int layerStart) && IsLayerUsable(startLayer, surfaces))
            startIndex = layerStart;

        if (sx == gx && sy == gy)
            return [(goal, StateLayer(startIndex))];

        // Search state lives in reused arrays, stamped per search (no per-call allocation).
        int count = StateCount;
        if (searchStamp == null || searchStamp.Length != count)
        {
            searchStamp = new int[count];
            closedStamp = new int[count];
            searchCost = new float[count];
            searchCameFrom = new int[count];
            stamp = 0;
        }
        if (++stamp == int.MaxValue)
        {
            Array.Clear(searchStamp);
            Array.Clear(closedStamp!);
            stamp = 1;
        }
        var cost = searchCost!;
        var cameFrom = searchCameFrom!;
        var stamps = searchStamp;
        var closed = closedStamp!;
        int currentStamp = stamp;
        stamps[startIndex] = currentStamp;
        cost[startIndex] = 0;
        var open = openQueue;
        open.Clear();
        open.Enqueue(startIndex, Heuristic(sx, sy, gx, gy));

        int best = startIndex;
        float bestDistance = Heuristic(sx, sy, gx, gy);
        int searched = 0;
        int found = -1;

        void Relax(int current, int next, float stepCost, int nx, int ny)
        {
            float newCost = cost[current] + stepCost;
            if (stamps[next] == currentStamp && cost[next] <= newCost)
                return;
            stamps[next] = currentStamp;
            cost[next] = newCost;
            cameFrom[next] = current;
            open.Enqueue(next, newCost + Heuristic(nx, ny, gx, gy));
        }

        while (open.Count > 0 && searched++ < maxSearchCells)
        {
            int current = open.Dequeue();
            if (closed[current] == currentStamp)
            {
                searched--; // a stale queue entry for a state already expanded
                continue;
            }
            closed[current] = currentStamp;
            int currentCell = StateCell(current);
            int layer = StateLayer(current);
            if (currentCell == goalIndex)
            {
                found = current;
                break;
            }

            int cx = currentCell % width, cy = currentCell / width;
            float h = Heuristic(cx, cy, gx, gy);
            if (h < bestDistance)
            {
                bestDistance = h;
                best = current;
            }

            if (layer == 0)
            {
                // Still inside a building's footprint (just produced, or the building went up around it):
                // the unit may cross obstacle cells until it is out, as the original lets units leave.
                bool escaping = !IsPassable(cx, cy, surfaces);
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            continue;
                        bool passable = IsPassable(nx, ny, surfaces);
                        if (!passable && !(escaping && TypeAt(nx, ny) == CellType.Obstacle))
                            continue;
                        // No corner cutting past blocked cells.
                        if (!escaping && dx != 0 && dy != 0 && (!IsPassable(cx + dx, cy, surfaces) || !IsPassable(cx, cy + dy, surfaces)))
                            continue;
                        Relax(current, ny * width + nx, dx != 0 && dy != 0 ? 1.4142f : 1f, nx, ny);
                    }
                }
                // Onto a bridge at its end.
                if (connectionsAt.TryGetValue(currentCell, out var ups))
                    foreach (int up in ups)
                        if (IsLayerUsable(StateLayer(up), surfaces))
                            Relax(current, up, 0.5f, cx, cy);
            }
            else
            {
                if (!IsLayerUsable(layer, surfaces))
                    continue;
                var states = layerStates[layer];
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            continue;
                        if (!states.TryGetValue(ny * width + nx, out int next))
                            continue;
                        if (dx != 0 && dy != 0 && (!states.ContainsKey(cy * width + cx + dx) || !states.ContainsKey((cy + dy) * width + cx)))
                            continue;
                        Relax(current, next, dx != 0 && dy != 0 ? 1.4142f : 1f, nx, ny);
                    }
                }
                // Off the bridge at its end.
                if (layerStateConnects[current - width * height] && IsPassable(cx, cy, surfaces))
                    Relax(current, currentCell, 0.5f, cx, cy);
            }
        }

        if (found >= 0)
            best = found;
        if (best == startIndex)
            return [];

        var route = new List<int>();
        for (int at = best; at != startIndex; at = cameFrom[at])
            route.Add(at);
        route.Reverse();

        var path = Simplify(route, start, StateLayer(startIndex), surfaces);
        // Finish exactly on the requested goal if it was reached.
        if (found >= 0)
        {
            var last = path[^1];
            path[^1] = (last.Layer > 0 ? goal with { Z = last.Position.Z } : goal, last.Layer);
        }
        return path;
    }

    int[]? searchStamp, closedStamp, searchCameFrom;
    float[]? searchCost;
    int stamp;
    readonly PriorityQueue<int, float> openQueue = new();

    static float Heuristic(int x, int y, int gx, int gy)
    {
        int dx = Math.Abs(x - gx), dy = Math.Abs(y - gy);
        return Math.Max(dx, dy) + 0.4142f * Math.Min(dx, dy);
    }

    Vector3 StatePosition(int state)
    {
        int cell = StateCell(state);
        var center = terrain.CellCenter(cell % width, cell / width);
        int layer = StateLayer(state);
        if (layer > 0 && terrain.Bridges.ForLayer(layer) is { } bridge)
            center.Z = bridge.GetBridgeHeight(center);
        return center;
    }

    /// <summary>
    /// Removes waypoints that can be skipped with a straight, passable line (smoother movement).
    /// Points where the layer changes (getting on or off a bridge) are kept.
    /// </summary>
    List<(Vector3 Position, int Layer)> Simplify(List<int> states, Vector3 start, int startLayer, Surfaces surfaces)
    {
        var points = states.Select(s => (Position: StatePosition(s), Layer: StateLayer(s))).ToList();
        var result = new List<(Vector3, int)>();
        var from = start;
        int fromLayer = startLayer;
        int i = 0;
        while (i < points.Count)
        {
            int farthest = i;
            for (int j = points.Count - 1; j > i; j--)
            {
                bool sameLayer = true;
                for (int k = i; k <= j && sameLayer; k++)
                    sameLayer = points[k].Layer == fromLayer;
                if (sameLayer && IsLineClear(from, points[j].Position, surfaces, fromLayer))
                {
                    farthest = j;
                    break;
                }
            }
            result.Add(points[farthest]);
            from = points[farthest].Position;
            fromLayer = points[farthest].Layer;
            i = farthest + 1;
        }
        return result;
    }

    /// <summary>True if a straight line between two points only crosses passable cells.</summary>
    /// <summary>Objects the pathfinder can ask about (bridges); set by the game logic.</summary>
    public Func<IEnumerable<GameObject>>? ObjectSource { get; set; }

    /// <summary>
    /// The original's findBrokenBridge: a destroyed bridge on the way between two points (the computer
    /// player sends a dozer to repair it). Here: a bridge in rubble lying across the straight line.
    /// </summary>
    public GameObject? FindBrokenBridge(Vector3 from, Vector3 to, Surfaces surfaces)
    {
        if (ObjectSource == null)
            return null;
        var a = new Vector2(from.X, from.Y);
        var b = new Vector2(to.X, to.Y);
        GameObject? best = null;
        float bestDist = float.MaxValue;
        foreach (var obj in ObjectSource())
        {
            if (!obj.IsKindOf(Core.Kind.Bridge) || obj.Body is not { } body || body.DamageState != Modules.BodyDamageState.Rubble)
                continue;
            var p = new Vector2(obj.Position.X, obj.Position.Y);
            var ab = b - a;
            float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            float dist = Vector2.Distance(p, a + ab * t);
            if (dist > obj.BoundingCircleRadius)
                continue;
            float along = Vector2.Distance(a, p);
            if (along < bestDist)
            {
                bestDist = along;
                best = obj;
            }
        }
        return best;
    }

    public bool IsLineClear(Vector3 a, Vector3 b, Surfaces surfaces, int layer = 0)
    {
        float length = Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
        int steps = Math.Max(1, (int)(length / 4f));
        for (int s = 1; s <= steps; s++)
        {
            var p = Vector3.Lerp(a, b, s / (float)steps);
            if (layer > 0)
            {
                if (!IsOnLayer(p, layer, surfaces))
                    return false;
                continue;
            }
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

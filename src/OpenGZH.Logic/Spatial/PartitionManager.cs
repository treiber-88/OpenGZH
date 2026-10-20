using System.Numerics;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Spatial;

/// <summary>
/// A grid over the map so "what is near this point?" queries only look at nearby cells
/// instead of every object in the game.
/// </summary>
public sealed class PartitionManager
{
    const float CellSize = 100f;

    readonly int columns, rows;
    readonly List<GameObject>[] cells;
    readonly Dictionary<GameObject, int> cellOf = new();

    // Objects that moved (or appeared) this frame: only they are checked for collisions.
    readonly HashSet<GameObject> dirty = new();
    readonly List<GameObject> dirtyOrder = new();

    public PartitionManager(float worldWidth, float worldHeight)
    {
        columns = Math.Max(1, (int)MathF.Ceiling(worldWidth / CellSize) + 2);
        rows = Math.Max(1, (int)MathF.Ceiling(worldHeight / CellSize) + 2);
        cells = new List<GameObject>[columns * rows];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = new List<GameObject>();
    }

    int CellIndex(Vector3 position)
    {
        // One cell of margin on each side for objects slightly outside the map.
        int x = Math.Clamp((int)MathF.Floor(position.X / CellSize) + 1, 0, columns - 1);
        int y = Math.Clamp((int)MathF.Floor(position.Y / CellSize) + 1, 0, rows - 1);
        return y * columns + x;
    }

    public void Add(GameObject obj)
    {
        int index = CellIndex(obj.Position);
        cells[index].Add(obj);
        cellOf[obj] = index;
        MarkDirty(obj);
    }

    void MarkDirty(GameObject obj)
    {
        if (dirty.Add(obj))
            dirtyOrder.Add(obj);
    }

    public void Remove(GameObject obj)
    {
        if (cellOf.Remove(obj, out int index))
            cells[index].Remove(obj);
    }

    public void Moved(GameObject obj)
    {
        if (!cellOf.TryGetValue(obj, out int old))
            return;
        MarkDirty(obj);
        int index = CellIndex(obj.Position);
        if (index == old)
            return;
        cells[old].Remove(obj);
        cells[index].Add(obj);
        cellOf[obj] = index;
    }

    /// <summary>
    /// Objects that touch a circle (2D): within <paramref name="radius"/> plus their own radius.
    /// Returns a new list, so callers may change the world while looping over it.
    /// </summary>
    public List<GameObject> Query(Vector3 center, float radius)
    {
        var result = new List<GameObject>();
        float reach = radius + CellSize;
        int x0 = Math.Clamp((int)MathF.Floor((center.X - reach) / CellSize) + 1, 0, columns - 1);
        int x1 = Math.Clamp((int)MathF.Floor((center.X + reach) / CellSize) + 1, 0, columns - 1);
        int y0 = Math.Clamp((int)MathF.Floor((center.Y - reach) / CellSize) + 1, 0, rows - 1);
        int y1 = Math.Clamp((int)MathF.Floor((center.Y + reach) / CellSize) + 1, 0, rows - 1);

        var center2 = new Vector2(center.X, center.Y);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                foreach (var obj in cells[y * columns + x])
                {
                    float limit = radius + obj.BoundingCircleRadius;
                    if (Vector2.DistanceSquared(center2, new Vector2(obj.Position.X, obj.Position.Y)) <= limit * limit)
                        result.Add(obj);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Finds every pair of touching objects where at least one moved this frame, and tells both
    /// (the original's contact list): this is how tanks crush, units enter transports, crates are
    /// picked up and things push each other apart. Each pair is reported once per frame.
    /// </summary>
    public void ProcessCollisions()
    {
        var reported = new HashSet<(uint, uint)>();
        var moved = dirtyOrder.ToList();
        dirty.Clear();
        dirtyOrder.Clear();
        foreach (var obj in moved)
        {
            if (obj.IsDestroyed || !cellOf.ContainsKey(obj) || obj.IsKindOf(Core.Kind.Immobile))
                continue;
            foreach (var other in Query(obj.Position, obj.BoundingCircleRadius))
            {
                if (other == obj || other.IsDestroyed || obj.IsDestroyed)
                    continue;
                var key = obj.Id < other.Id ? (obj.Id, other.Id) : (other.Id, obj.Id);
                if (!reported.Add(key))
                    continue;
                if (obj.IsKindOf("NO_COLLIDE") || other.IsKindOf("NO_COLLIDE"))
                    continue;
                var a = new Collision.Shape(obj.Position, obj.Geometry, obj.Orientation);
                var b = new Collision.Shape(other.Position, other.Geometry, other.Orientation);
                if (!Collision.Collides(a, b, out var info))
                    continue;
                if (obj.HasStatus(Core.Status.NoCollisions) || other.HasStatus(Core.Status.NoCollisions))
                    continue;
                obj.OnCollide(other, info.Location, info.Normal);
                if (!obj.IsDestroyed && !other.IsDestroyed)
                    other.OnCollide(obj, info.Location, -info.Normal);
            }
        }
    }
}

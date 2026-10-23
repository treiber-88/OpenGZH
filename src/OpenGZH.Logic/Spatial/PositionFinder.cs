using System.Numerics;
using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Spatial;

/// <summary>The original's PartitionManager::findPositionAround: a free spot in rings around a point.</summary>
public static class PositionFinder
{
    const float RingSpacing = 5f;

    /// <summary>
    /// Tries rings from <paramref name="minRadius"/> to <paramref name="maxRadius"/>, starting at a
    /// random angle and alternating either side: the first spot that is passable land, not a cliff,
    /// not under water and not touching any object (a 5-unit circle) wins.
    /// </summary>
    public static Vector3? FindPositionAround(GameLogic game, Vector3 center, float minRadius, float maxRadius, float maxZDelta = 1e10f)
    {
        if (!game.Terrain.IsInsidePlayableArea(center.X, center.Y))
            return center;
        float startAngle = game.Random.Float(0, MathF.Tau);
        for (float dist = minRadius; dist <= maxRadius; dist += RingSpacing)
        {
            float angleSpacing = dist == minRadius ? MathF.Tau : RingSpacing / (dist + 1) * (MathF.Tau / 6);
            int samples = (int)MathF.Ceiling(MathF.Tau / angleSpacing / 2);
            for (int i = 0; i < samples; i++)
            {
                if (TryPosition(game, center, dist, startAngle + angleSpacing * i, maxZDelta) is { } a)
                    return a;
                if (i != 0 && TryPosition(game, center, dist, startAngle - angleSpacing * i, maxZDelta) is { } b)
                    return b;
            }
        }
        return null;
    }

    static Vector3? TryPosition(GameLogic game, Vector3 center, float dist, float angle, float maxZDelta)
    {
        var pos = new Vector3(dist * MathF.Cos(angle) + center.X, dist * MathF.Sin(angle) + center.Y, 0);
        pos.Z = game.Terrain.GroundHeight(pos);
        if (MathF.Abs(pos.Z - center.Z) > maxZDelta)
            return null;
        var (cx, cy) = game.Terrain.CellAt(pos.X, pos.Y);
        if (game.Terrain.IsCliffCell(cx, cy))
            return null;
        if (!game.Pathfinder.IsPassable(pos, Locomotion.Surfaces.Ground))
            return null;
        if (game.Terrain.IsUnderwater(pos.X, pos.Y, out _))
            return null;
        foreach (var obj in game.ObjectsInRadius(pos, 5 + 50))
            if (MathUtil.Distance2D(obj.Position, pos) < 5 + obj.BoundingCircleRadius)
                return null;
        return pos;
    }
}

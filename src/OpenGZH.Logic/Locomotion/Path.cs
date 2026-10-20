using System.Numerics;

namespace OpenGZH.Logic.Locomotion;

/// <summary>
/// A path for one unit to follow, with the original's "where on the path should I steer towards"
/// logic (Path::computePointOnPath): find the closest point on the path, then aim ahead at the
/// next corner (or past it) if the straight line there is passable, so units cut corners smoothly.
/// Also reports how far along the path is still left, for braking.
/// </summary>
public sealed class Path
{
    const float CellSize = Formats.Maps.MapFile.CellSize;
    const float CloseEnough = 1.0f; // PATHFIND_CLOSE_ENOUGH
    const int MaxCachedReturns = 20;

    readonly List<Vector3> nodes;

    // The original caches the last answer while the unit hasn't moved.
    bool cacheValid;
    int cacheCountdown;
    Vector3 cacheIn;
    (Vector3 Pos, float Dist) cacheOut;
    int flightStart;

    public Path(IEnumerable<Vector3> points) => nodes = points.ToList();

    public IReadOnlyList<Vector3> Nodes => nodes;
    public Vector3 LastNode => nodes[^1];
    public bool IsEmpty => nodes.Count == 0;

    static bool ReallyClose(Vector3 a, Vector3 b) =>
        MathF.Abs(a.X - b.X) <= 0.1f && MathF.Abs(a.Y - b.Y) <= 0.1f && MathF.Abs(a.Z - b.Z) <= 0.1f;

    static (Vector2 Dir, float Length) Segment(Vector3 from, Vector3 to)
    {
        var d = new Vector2(to.X - from.X, to.Y - from.Y);
        float length = d.Length();
        return (length > 0 ? d / length : Vector2.Zero, length);
    }

    /// <summary>The point to steer towards, and the distance still to go along the path.</summary>
    public (Vector3 PosOnPath, float DistAlongPath) ComputePointOnPath(Vector3 pos, Pathfinder pathfinder, Surfaces surfaces)
    {
        if (nodes.Count == 0)
            return (pos, 0);
        if (cacheValid && cacheCountdown > 0 && ReallyClose(pos, cacheIn))
        {
            cacheCountdown--;
            return cacheOut;
        }
        cacheCountdown = MaxCachedReturns;

        var posOnPath = nodes[^1];
        int closeNode = -1;
        float closeDistSqr = 99999999.9f;
        float totalPathLength = 0, lengthAlongPathToPos = 0;

        for (int i = 1; i < nodes.Count; i++)
        {
            var prev = nodes[i - 1];
            var node = nodes[i];
            var (dir, segmentLength) = Segment(prev, node);
            float along = dir.X * (pos.X - prev.X) + dir.Y * (pos.Y - prev.Y);
            Vector3 point;
            if (along < 0)
            {
                along = 0;
                point = prev;
            }
            else if (along > segmentLength)
            {
                if (i == nodes.Count - 1)
                {
                    along = segmentLength;
                    point = node;
                }
                else
                {
                    totalPathLength += segmentLength;
                    continue;
                }
            }
            else
            {
                point = new Vector3(prev.X + along * dir.X, prev.Y + along * dir.Y, 0);
            }
            float offsetSqr = (pos.X - point.X) * (pos.X - point.X) + (pos.Y - point.Y) * (pos.Y - point.Y);
            if (offsetSqr < closeDistSqr)
            {
                closeDistSqr = offsetSqr;
                closeNode = i - 1;
                posOnPath = point;
                lengthAlongPathToPos = totalPathLength + along;
            }
            totalPathLength += segmentLength;
        }

        if (closeNode >= 0 && closeNode + 1 < nodes.Count)
        {
            var closePos = nodes[closeNode];
            var nextPos = nodes[closeNode + 1];
            var (dir, segmentLength) = Segment(closePos, nextPos);
            var toPos = new Vector2(pos.X - closePos.X, pos.Y - closePos.Y);
            float along = Math.Max(0, dir.X * toPos.X + dir.Y * toPos.Y);
            float offsetSq = toPos.LengthSquared() - along * along;
            float offsetDist = offsetSq <= 0 ? 0 : MathF.Sqrt(offsetSq);
            // How far off the path we are, as a fraction of three cells.
            float k = Math.Min(1, offsetDist / (3 * CellSize));
            bool gotPos = false;
            if (pathfinder.IsLineClear(pos, nextPos, surfaces))
            {
                posOnPath = nextPos;
                gotPos = true;
                bool veryClose = segmentLength - along < 1;
                bool tryAhead = along > segmentLength * 0.5f || veryClose;
                if (tryAhead && closeNode + 2 < nodes.Count)
                {
                    var after = nodes[closeNode + 2];
                    var tryPos = new Vector3((nextPos.X + after.X) * 0.5f, (nextPos.Y + after.Y) * 0.5f, nextPos.Z);
                    if (veryClose || pathfinder.IsLineClear(pos, tryPos, surfaces))
                        posOnPath = tryPos;
                }
            }
            else if (k > 0.5f)
            {
                float tryDist = along + 0.5f * (segmentLength - along);
                var tryPos = new Vector3(closePos.X + tryDist * dir.X, closePos.Y + tryDist * dir.Y, closePos.Z);
                if (pathfinder.IsLineClear(pos, tryPos, surfaces))
                {
                    k = 0.5f;
                    gotPos = true;
                    posOnPath = tryPos;
                }
            }
            along += (1 - k) * (segmentLength - along);
            if (!gotPos)
            {
                if (along > segmentLength)
                {
                    posOnPath = nextPos;
                }
                else
                {
                    posOnPath = new Vector3(closePos.X + along * dir.X, closePos.Y + along * dir.Y, closePos.Z);
                    if (MathF.Abs(pos.X - posOnPath.X) < 1 && MathF.Abs(pos.Y - posOnPath.Y) < 1 && closeNode + 2 < nodes.Count)
                        posOnPath = nodes[closeNode + 2];
                }
            }
        }

        float distAlongPath = totalPathLength - lengthAlongPathToPos;
        float lenDelta = new Vector2(posOnPath.X - pos.X, posOnPath.Y - pos.Y).Length();
        if (lenDelta > distAlongPath && distAlongPath > CloseEnough)
            distAlongPath = lenDelta;

        cacheIn = pos;
        cacheOut = (posOnPath, distAlongPath);
        cacheValid = true;
        return cacheOut;
    }

    /// <summary>For aircraft: the next corner to fly to, and the distance left measured along the path.</summary>
    public (Vector3 Goal, float Distance) ComputeFlightDistToGoal(Vector3 pos)
    {
        if (nodes.Count == 0)
            return (Vector3.Zero, 0);
        int cur = flightStart;
        var goal = nodes[cur];
        float distance = 0;
        bool useNext = true;
        for (int next = cur + 1; next < nodes.Count; cur = next, next++)
        {
            if (useNext)
                goal = nodes[next];
            var start = nodes[cur];
            var end = nodes[next];
            var toGoal = new Vector2(end.X - pos.X, end.Y - pos.Y);
            var pathDir = new Vector2(end.X - start.X, end.Y - start.Y);
            if (pathDir.LengthSquared() > 0)
                pathDir = Vector2.Normalize(pathDir);
            float dot = Vector2.Dot(toGoal, pathDir);
            if (dot >= 0)
            {
                distance += dot;
                useNext = false;
            }
            else if (useNext)
            {
                flightStart = next;
            }
        }
        return (goal, distance);
    }
}

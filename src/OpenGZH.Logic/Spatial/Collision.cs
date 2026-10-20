using System.Numerics;
using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Spatial;

/// <summary>Where two shapes touch, and the direction from the first towards the second.</summary>
public struct CollisionInfo
{
    public Vector3 Location;
    public Vector3 Normal;
}

/// <summary>
/// The original's shape-against-shape collision tests. Circles are tested against rectangles as
/// squares (as the original does), and rectangles by checking each one's corners against the other.
/// </summary>
public static class Collision
{
    public readonly record struct Shape(Vector3 Position, Geometry Geometry, float Angle);

    /// <summary>Whether two shapes overlap: their heights must overlap, then the footprints.</summary>
    public static bool Collides(Shape a, Shape b, out CollisionInfo info)
    {
        info = default;
        float aTop = a.Position.Z + a.Geometry.MaxHeightAbovePosition, aBottom = a.Position.Z - a.Geometry.MaxHeightBelowPosition;
        float bTop = b.Position.Z + b.Geometry.MaxHeightAbovePosition, bBottom = b.Position.Z - b.Geometry.MaxHeightBelowPosition;
        if (!(aTop >= bBottom && aBottom <= bTop))
            return false;

        var ta = a.Geometry.Type;
        var tb = b.Geometry.Type;
        switch (ta, tb)
        {
            case (GeometryType.Sphere, GeometryType.Sphere): return SphereSphere(a, b, ref info);
            case (GeometryType.Sphere, GeometryType.Cylinder): return SphereNonSphere(CircleCircle, a, b, ref info);
            case (GeometryType.Sphere, GeometryType.Box): return SphereNonSphere(CircleRect, a, b, ref info);
            case (GeometryType.Cylinder, GeometryType.Sphere): return Flipped(SphereNonSphere(CircleCircle, b, a, ref info), ref info);
            case (GeometryType.Cylinder, GeometryType.Cylinder): return NonSphereNonSphere(CircleCircle, a, b, ref info);
            case (GeometryType.Cylinder, GeometryType.Box): return NonSphereNonSphere(CircleRect, a, b, ref info);
            case (GeometryType.Box, GeometryType.Sphere): return Flipped(SphereNonSphere(CircleRect, b, a, ref info), ref info);
            case (GeometryType.Box, GeometryType.Cylinder): return NonSphereNonSphere(RectCircle, a, b, ref info);
            default: return NonSphereNonSphere(RectRect, a, b, ref info);
        }
    }

    delegate bool XyTest(Shape a, Shape b, ref CollisionInfo info);

    static bool Flipped(bool result, ref CollisionInfo info)
    {
        info.Normal = -info.Normal;
        return result;
    }

    static Vector3 Normalize2D(Vector3 v)
    {
        v.Z = 0;
        float length = v.Length();
        return length > 0 ? v / length : v;
    }

    static bool SphereSphere(Shape a, Shape b, ref CollisionInfo info)
    {
        var diff = b.Position - a.Position;
        float touching = a.Geometry.MajorRadius + b.Geometry.MajorRadius;
        if (diff.LengthSquared() > touching * touching)
            return false;
        info.Normal = diff.LengthSquared() > 0 ? Vector3.Normalize(diff) : Vector3.Zero;
        info.Location = a.Position + info.Normal * a.Geometry.MajorRadius;
        return true;
    }

    static bool CircleCircle(Shape a, Shape b, ref CollisionInfo info)
    {
        var diff = (b.Position - a.Position) with { Z = 0 };
        float touching = a.Geometry.MajorRadius + b.Geometry.MajorRadius;
        if (diff.LengthSquared() > touching * touching)
            return false;
        info.Normal = Normalize2D(diff);
        info.Location = a.Position + info.Normal * a.Geometry.MajorRadius;
        return true;
    }

    static bool CircleRect(Shape a, Shape b, ref CollisionInfo info) => Flipped(RectCircle(b, a, ref info), ref info);

    static bool RectCircle(Shape a, Shape b, ref CollisionInfo info)
    {
        var square = b with { Geometry = b.Geometry.WithRadii(b.Geometry.MajorRadius, b.Geometry.MajorRadius) };
        return RectRect(a, square, ref info);
    }

    static void Corners(Shape a, Span<Vector2> points)
    {
        float c = MathF.Cos(a.Angle), s = MathF.Sin(a.Angle);
        float exc = a.Geometry.MajorRadius * c, eyc = a.Geometry.MinorRadius * c;
        float exs = a.Geometry.MajorRadius * s, eys = a.Geometry.MinorRadius * s;
        points[0] = new(a.Position.X - exc - eys, a.Position.Y + eyc - exs);
        points[1] = new(a.Position.X + exc - eys, a.Position.Y + eyc + exs);
        points[2] = new(a.Position.X - exc + eys, a.Position.Y - eyc - exs);
        points[3] = new(a.Position.X + exc + eys, a.Position.Y - eyc + exs);
    }

    static void PointsInsideRect(ReadOnlySpan<Vector2> points, Shape rect, ref Vector2 sum, ref int count)
    {
        float major = rect.Geometry.MajorRadius;
        float minor = rect.Geometry.Type == GeometryType.Sphere ? rect.Geometry.MajorRadius : rect.Geometry.MinorRadius;
        float c = MathF.Cos(-rect.Angle), s = MathF.Sin(-rect.Angle);
        foreach (var p in points)
        {
            float x = p.X - rect.Position.X, y = p.Y - rect.Position.Y;
            if (MathF.Abs(x * c - y * s) <= major && MathF.Abs(x * s + y * c) <= minor)
            {
                sum += p;
                count++;
            }
        }
    }

    static bool RectRect(Shape a, Shape b, ref CollisionInfo info)
    {
        Span<Vector2> points = stackalloc Vector2[4];
        var sum = Vector2.Zero;
        int count = 0;
        Corners(a, points);
        PointsInsideRect(points, b, ref sum, ref count);
        Corners(b, points);
        PointsInsideRect(points, a, ref sum, ref count);
        if (count == 0)
            return false;
        info.Location = new Vector3(sum / count, (a.Position.Z + b.Position.Z) * 0.5f);
        info.Normal = Normalize2D(b.Position - a.Position);
        return true;
    }

    static bool SphereNonSphere(XyTest xy, Shape a, Shape b, ref CollisionInfo info)
    {
        float bBottom = b.Position.Z, bTop = b.Position.Z + b.Geometry.MaxHeightAbovePosition;
        float radius = a.Geometry.MajorRadius;
        if (a.Position.Z >= bBottom && a.Position.Z <= bTop)
        {
            if (!xy(a, b, ref info))
                return false;
            info.Location.Z = a.Position.Z;
            return true;
        }
        // The sphere pokes into the shape from below or above: test the circle where it crosses.
        float plane;
        if (a.Position.Z < bBottom && a.Position.Z + radius >= bBottom)
            plane = bBottom;
        else if (a.Position.Z > bTop && a.Position.Z - radius <= bTop)
            plane = bTop;
        else
            return false;
        float slice = MathF.Sqrt(radius * radius - (plane - a.Position.Z) * (plane - a.Position.Z));
        var reduced = new Shape(a.Position with { Z = plane }, a.Geometry.WithRadii(slice, slice), a.Angle);
        return xy(reduced, b, ref info);
    }

    static bool NonSphereNonSphere(XyTest xy, Shape a, Shape b, ref CollisionInfo info)
    {
        float dSqr = (a.Position.X - b.Position.X) * (a.Position.X - b.Position.X) + (a.Position.Y - b.Position.Y) * (a.Position.Y - b.Position.Y);
        float minRadius = MathF.Min(MathF.Min(a.Geometry.MajorRadius, a.Geometry.MinorRadius), MathF.Min(b.Geometry.MajorRadius, b.Geometry.MinorRadius));
        bool closeEnough = minRadius * minRadius > dSqr;
        if (!closeEnough && !xy(a, b, ref info))
            return false;
        info.Location.Z = b.Position.Z > a.Position.Z
            ? (b.Position.Z + a.Position.Z + a.Geometry.MaxHeightAbovePosition) * 0.5f
            : (a.Position.Z + b.Position.Z + b.Geometry.MaxHeightAbovePosition) * 0.5f;
        return true;
    }
}

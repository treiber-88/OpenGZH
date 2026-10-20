using System.Numerics;

namespace OpenGZH.Client.Graphics;

/// <summary>The six planes of a camera's view volume, for skipping things that are off screen.</summary>
public readonly struct Frustum
{
    readonly Vector4[] planes;

    public Frustum(Matrix4x4 viewProjection)
    {
        var m = viewProjection;
        // Gribb-Hartmann plane extraction, for row-vector matrices (v * M).
        planes =
        [
            new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41), // left
            new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41), // right
            new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42), // bottom
            new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42), // top
            new Vector4(m.M13, m.M23, m.M33, m.M43),                                 // near
            new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43), // far
        ];
    }

    /// <summary>True if any part of the axis-aligned box may be visible.</summary>
    public bool Intersects(Vector3 min, Vector3 max)
    {
        foreach (var plane in planes)
        {
            // Test the box corner furthest along the plane normal.
            var corner = new Vector3(plane.X >= 0 ? max.X : min.X, plane.Y >= 0 ? max.Y : min.Y, plane.Z >= 0 ? max.Z : min.Z);
            if (plane.X * corner.X + plane.Y * corner.Y + plane.Z * corner.Z + plane.W < 0)
                return false;
        }
        return true;
    }

    public bool Intersects(Vector3 center, float radius)
    {
        foreach (var plane in planes)
        {
            float length = new Vector3(plane.X, plane.Y, plane.Z).Length();
            if ((plane.X * center.X + plane.Y * center.Y + plane.Z * center.Z + plane.W) / length < -radius)
                return false;
        }
        return true;
    }
}

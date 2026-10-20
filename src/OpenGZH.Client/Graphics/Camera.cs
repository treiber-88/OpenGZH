using System.Numerics;

namespace OpenGZH.Client.Graphics;

/// <summary>
/// The RTS camera. The world is Z-up (as in the original game); the camera orbits a ground
/// point at a given yaw, pitch and distance.
/// </summary>
public sealed class Camera
{
    /// <summary>The point on the ground the camera looks at.</summary>
    public Vector3 Target = Vector3.Zero;

    /// <summary>Rotation around the vertical axis, in radians.</summary>
    public float Yaw = MathF.PI * 0.25f;

    /// <summary>Angle above the ground plane, in radians (the original default is a steep RTS view).</summary>
    public float Pitch = 0.65f;

    public float Distance = 300f;
    public float FieldOfView = MathF.PI / 4f;
    public float NearPlane = 2f;
    public float FarPlane = 6000f;
    public float AspectRatio = 16f / 9f;

    public Vector3 Position
    {
        get
        {
            var offset = new Vector3(
                MathF.Cos(Pitch) * MathF.Cos(Yaw),
                MathF.Cos(Pitch) * MathF.Sin(Yaw),
                MathF.Sin(Pitch));
            return Target + offset * Distance;
        }
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitZ);
    public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, AspectRatio, NearPlane, FarPlane);
    public Matrix4x4 ViewProjection => View * Projection;

    /// <summary>The ground-plane directions for screen-relative scrolling.</summary>
    public Vector3 ScreenRight => Vector3.Normalize(new Vector3(-MathF.Sin(Yaw), MathF.Cos(Yaw), 0));
    public Vector3 ScreenUp => Vector3.Normalize(new Vector3(-MathF.Cos(Yaw), -MathF.Sin(Yaw), 0));

    /// <summary>A world-space ray through a screen pixel (for picking and placement).</summary>
    public (Vector3 Origin, Vector3 Direction) ScreenRay(Vector2 pixel, Vector2 viewportSize)
    {
        Matrix4x4.Invert(ViewProjection, out var inverse);
        float x = pixel.X / viewportSize.X * 2 - 1;
        float y = 1 - pixel.Y / viewportSize.Y * 2;
        var near = Vector4.Transform(new Vector4(x, y, -1, 1), inverse);
        var far = Vector4.Transform(new Vector4(x, y, 1, 1), inverse);
        var nearPoint = new Vector3(near.X, near.Y, near.Z) / near.W;
        var farPoint = new Vector3(far.X, far.Y, far.Z) / far.W;
        return (nearPoint, Vector3.Normalize(farPoint - nearPoint));
    }

    /// <summary>Frames a bounding box so it fills most of the view.</summary>
    public void Frame(Vector3 min, Vector3 max)
    {
        Target = (min + max) / 2;
        float radius = Math.Max(1f, (max - min).Length() / 2);
        Distance = radius / MathF.Sin(FieldOfView / 2) * 1.1f;
        FarPlane = Distance * 10 + 1000;
        NearPlane = Math.Max(0.5f, Distance / 200);
    }
}

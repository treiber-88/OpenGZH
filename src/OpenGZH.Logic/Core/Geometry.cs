using OpenGZH.Core.Rules;

namespace OpenGZH.Logic.Core;

public enum GeometryType { Sphere, Cylinder, Box }

/// <summary>
/// An object's collision shape, as the original's GeometryInfo: a sphere, an upright cylinder, or a
/// box (MajorRadius along the object's facing, MinorRadius across it). Positions are at the bottom
/// of cylinders and boxes, and at the centre of spheres.
/// </summary>
public sealed class Geometry
{
    public GeometryType Type { get; }
    public bool IsSmall { get; }
    public float MajorRadius { get; }
    public float MinorRadius { get; }
    public float Height { get; }
    public float BoundingCircleRadius { get; }
    public float BoundingSphereRadius { get; }

    public Geometry(GeometryType type, float majorRadius, float minorRadius, float height, bool isSmall = false)
    {
        Type = type;
        IsSmall = isSmall;
        MajorRadius = majorRadius;
        switch (type)
        {
            case GeometryType.Sphere:
                MinorRadius = majorRadius;
                Height = majorRadius;
                BoundingCircleRadius = BoundingSphereRadius = majorRadius;
                break;
            case GeometryType.Cylinder:
                MinorRadius = majorRadius;
                Height = height;
                BoundingCircleRadius = majorRadius;
                BoundingSphereRadius = Math.Max(height * 0.5f, majorRadius);
                break;
            default:
                MinorRadius = minorRadius;
                Height = height;
                BoundingCircleRadius = MathF.Sqrt(majorRadius * majorRadius + minorRadius * minorRadius);
                BoundingSphereRadius = MathF.Sqrt(majorRadius * majorRadius + minorRadius * minorRadius + height * height * 0.25f);
                break;
        }
    }

    /// <summary>Reads Geometry, GeometryMajorRadius, GeometryMinorRadius, GeometryHeight and GeometryIsSmall.</summary>
    public static Geometry FromRules(RuleNode rules)
    {
        var type = rules.GetString("Geometry", "SPHERE").ToUpperInvariant() switch
        {
            "BOX" => GeometryType.Box,
            "CYLINDER" => GeometryType.Cylinder,
            _ => GeometryType.Sphere,
        };
        float major = RuleValue.Float(rules.GetString("GeometryMajorRadius"), 1);
        float minor = RuleValue.Float(rules.GetString("GeometryMinorRadius"), major);
        float height = RuleValue.Float(rules.GetString("GeometryHeight"), 1);
        return new Geometry(type, major, minor, height, RuleValue.Bool(rules.GetString("GeometryIsSmall")));
    }

    public float MaxHeightAbovePosition => Type == GeometryType.Sphere ? MajorRadius : Height;
    public float MaxHeightBelowPosition => Type == GeometryType.Sphere ? MajorRadius : 0;
    public float ZDeltaToCenterPosition => Type == GeometryType.Sphere ? 0 : Height * 0.5f;

    /// <summary>Area of the footprint on the ground.</summary>
    public float FootprintArea => Type == GeometryType.Box ? 4 * MajorRadius * MinorRadius : MathF.PI * MajorRadius * MajorRadius;

    /// <summary>The same shape with different radii (used by collision tests that treat a circle as a square).</summary>
    public Geometry WithRadii(float major, float minor) => new(Type, major, minor, Height, IsSmall);

    /// <summary>The original's makeRandomOffsetWithinFootprint: a random point inside the footprint.</summary>
    public System.Numerics.Vector3 RandomOffsetWithinFootprint(GameRandom random)
    {
        if (Type == GeometryType.Box)
            return new System.Numerics.Vector3(random.Float(-MajorRadius, MajorRadius), random.Float(-MinorRadius, MinorRadius), 0);
        float x, y;
        do
        {
            x = random.Float(-MajorRadius, MajorRadius);
            y = random.Float(-MajorRadius, MajorRadius);
        } while (x * x + y * y > MajorRadius * MajorRadius);
        return new System.Numerics.Vector3(x, y, 0);
    }

    /// <summary>The original's makeRandomOffsetOnPerimeter: a random point on a box's edge (the centre for round shapes).</summary>
    public System.Numerics.Vector3 RandomOffsetOnPerimeter(GameRandom random)
    {
        if (Type != GeometryType.Box)
            return default;
        float x, y;
        if (random.Float(0, 1) < 0.5f)
        {
            x = random.Float(-MajorRadius, MajorRadius);
            y = random.Float(0, 1) < 0.5f ? -MinorRadius : MinorRadius;
        }
        else
        {
            y = random.Float(-MinorRadius, MinorRadius);
            x = random.Float(0, 1) < 0.5f ? -MajorRadius : MajorRadius;
        }
        return new System.Numerics.Vector3(x, y, 0);
    }
}

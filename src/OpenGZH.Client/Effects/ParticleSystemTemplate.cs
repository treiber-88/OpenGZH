using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using OpenGZH.Core.Rules;

namespace OpenGZH.Client.Effects;

/// <summary>Random numbers for visuals only (the original's GameClientRandomValue): never used by the simulation.</summary>
public static class ClientRandom
{
    static readonly Random random = new();

    /// <summary>A real number from low to high, inclusive (GameClientRandomValueReal).</summary>
    public static float Real(float low, float high) => low == high ? low : low + (float)random.NextDouble() * (high - low);

    /// <summary>A whole number from low to high, inclusive (GameClientRandomValue).</summary>
    public static int Int(int low, int high) => high <= low ? low : random.Next(low, high + 1);
}

/// <summary>A value picked at random between two limits (the original's GameClientRandomVariable).</summary>
public struct RandomVariable
{
    public float Low, High;

    public RandomVariable(float low, float high)
    {
        Low = low;
        High = high;
    }

    public readonly float Value => ClientRandom.Real(Low, High);

    public override readonly string ToString() => $"{Low}..{High}";
}

public enum ParticleShader { None, Additive, Alpha, AlphaTest, Multiply }
public enum ParticleType { None, Particle, Drawable, Streak, VolumeParticle, Smudge }
public enum EmissionVelocity { None, Ortho, Spherical, Hemispherical, Cylindrical, Outward }
public enum EmissionVolume { None, Point, Line, Box, Sphere, Cylinder }
public enum WindMotion { None, NotUsed, PingPong, Circular }

/// <summary>The original's particle priorities: when there are too many particles, lower ones go first.</summary>
public enum ParticlePriority
{
    None = 0, WeaponExplosion, Scorchmark, DustTrail, Buildup, DebrisTrail, UnitDamageFx, DeathExplosion,
    SemiConstant, Constant, WeaponTrail, AreaEffect, Critical, AlwaysRender,
}

public struct RandomKeyframe
{
    public RandomVariable Value;
    public int Frame;
}

public struct ColorKeyframe
{
    public Vector3 Color;
    public int Frame;
}

/// <summary>
/// A particle system definition from particle-systems.yaml (the original's ParticleSystemTemplate).
/// Times are in logic frames, angles in radians, colors 0..1.
/// </summary>
public sealed class ParticleSystemTemplate
{
    public const int MaxKeyframes = 8;

    public string Name { get; }
    public bool IsOneShot;
    public ParticleShader Shader = ParticleShader.None;
    public ParticleType Type = ParticleType.None;
    public string ParticleName = "";
    public RandomVariable AngleZ, AngularRateZ, AngularDamping, VelocityDamping, Lifetime;
    public int SystemLifetime;
    public RandomVariable StartSize, StartSizeRate, SizeRate, SizeRateDamping;
    public readonly RandomKeyframe[] AlphaKeys = new RandomKeyframe[MaxKeyframes];
    public readonly ColorKeyframe[] ColorKeys = new ColorKeyframe[MaxKeyframes];
    public RandomVariable ColorScale, BurstDelay, BurstCount, InitialDelay;
    public Vector3 DriftVelocity;
    public float Gravity;
    public string SlaveSystem = "";
    public Vector3 SlavePosOffset;
    public string PerParticleAttachedSystem = "";
    public EmissionVelocity VelocityType = EmissionVelocity.None;
    public ParticlePriority Priority = ParticlePriority.WeaponExplosion;
    public RandomVariable VelOrthoX, VelOrthoY, VelOrthoZ, VelSpherical, VelHemispherical;
    public RandomVariable VelCylindricalRadial, VelCylindricalNormal, VelOutward, VelOutwardOther;
    public EmissionVolume VolumeType = EmissionVolume.None;
    public Vector3 VolLineStart, VolLineEnd, VolBoxHalfSize;
    public float VolSphereRadius, VolCylinderRadius, VolCylinderLength;
    public bool IsHollow, IsGroundAligned, IsEmitAboveGroundOnly, IsParticleUpTowardsEmitter;
    public WindMotion WindMotion = WindMotion.NotUsed;
    public float WindAngleChangeMin = 0.15f, WindAngleChangeMax = 0.45f;
    public float WindPingPongStartAngleMin, WindPingPongStartAngleMax = MathF.PI / 4;
    public float WindPingPongEndAngleMin = MathF.Tau - MathF.PI / 4, WindPingPongEndAngleMax = MathF.Tau;

    public ParticleSystemTemplate(string name) => Name = name;

    public static ParticleSystemTemplate Parse(string name, RuleNode node)
    {
        var t = new ParticleSystemTemplate(name);
        string S(string key) => node.GetString(key);
        bool Has(string key) => node.Has(key);
        RandomVariable R(string key)
        {
            var w = RuleValue.Words(S(key));
            return new RandomVariable(w.Length > 0 ? RuleValue.Float(w[0]) : 0, w.Length > 1 ? RuleValue.Float(w[1]) : w.Length > 0 ? RuleValue.Float(w[0]) : 0);
        }

        if (Has("Priority")) t.Priority = ParsePriority(S("Priority"));
        t.IsOneShot = RuleValue.Bool(S("IsOneShot"));
        if (Has("Shader")) t.Shader = S("Shader").ToUpperInvariant() switch
        {
            "ADDITIVE" => ParticleShader.Additive,
            "ALPHA" => ParticleShader.Alpha,
            "ALPHA_TEST" => ParticleShader.AlphaTest,
            "MULTIPLY" => ParticleShader.Multiply,
            _ => ParticleShader.None,
        };
        if (Has("Type")) t.Type = S("Type").ToUpperInvariant() switch
        {
            "PARTICLE" => ParticleType.Particle,
            "DRAWABLE" => ParticleType.Drawable,
            "STREAK" => ParticleType.Streak,
            "VOLUME_PARTICLE" => ParticleType.VolumeParticle,
            "SMUDGE" => ParticleType.Smudge,
            _ => ParticleType.None,
        };
        t.ParticleName = S("ParticleName");
        t.AngleZ = R("AngleZ");
        t.AngularRateZ = R("AngularRateZ");
        t.AngularDamping = R("AngularDamping");
        t.VelocityDamping = R("VelocityDamping");
        t.Gravity = RuleValue.Float(S("Gravity"));
        t.SlaveSystem = S("SlaveSystem");
        t.SlavePosOffset = ParseCoord(S("SlavePosOffset"));
        t.PerParticleAttachedSystem = S("PerParticleAttachedSystem");
        t.Lifetime = R("Lifetime");
        t.SystemLifetime = RuleValue.Int(S("SystemLifetime"));
        t.StartSize = R("Size");
        t.StartSizeRate = R("StartSizeRate");
        t.SizeRate = R("SizeRate");
        t.SizeRateDamping = R("SizeRateDamping");
        for (int i = 0; i < MaxKeyframes; i++)
        {
            var alpha = RuleValue.Words(S($"Alpha{i + 1}"));
            if (alpha.Length >= 3)
                t.AlphaKeys[i] = new RandomKeyframe { Value = new RandomVariable(RuleValue.Float(alpha[0]), RuleValue.Float(alpha[1])), Frame = RuleValue.Int(alpha[2]) };
            if (Has($"Color{i + 1}"))
                t.ColorKeys[i] = ParseColorKey(S($"Color{i + 1}"));
        }
        t.ColorScale = R("ColorScale");
        t.BurstDelay = R("BurstDelay");
        t.BurstCount = R("BurstCount");
        t.InitialDelay = R("InitialDelay");
        t.DriftVelocity = ParseCoord(S("DriftVelocity"));
        if (Has("VelocityType")) t.VelocityType = S("VelocityType").ToUpperInvariant() switch
        {
            "ORTHO" => EmissionVelocity.Ortho,
            "SPHERICAL" => EmissionVelocity.Spherical,
            "HEMISPHERICAL" => EmissionVelocity.Hemispherical,
            "CYLINDRICAL" => EmissionVelocity.Cylindrical,
            "OUTWARD" => EmissionVelocity.Outward,
            _ => EmissionVelocity.None,
        };
        t.VelOrthoX = R("VelOrthoX");
        t.VelOrthoY = R("VelOrthoY");
        t.VelOrthoZ = R("VelOrthoZ");
        t.VelSpherical = R("VelSpherical");
        t.VelHemispherical = R("VelHemispherical");
        t.VelCylindricalRadial = R("VelCylindricalRadial");
        t.VelCylindricalNormal = R("VelCylindricalNormal");
        t.VelOutward = R("VelOutward");
        t.VelOutwardOther = R("VelOutwardOther");
        if (Has("VolumeType")) t.VolumeType = S("VolumeType").ToUpperInvariant() switch
        {
            "POINT" => EmissionVolume.Point,
            "LINE" => EmissionVolume.Line,
            "BOX" => EmissionVolume.Box,
            "SPHERE" => EmissionVolume.Sphere,
            "CYLINDER" => EmissionVolume.Cylinder,
            _ => EmissionVolume.None,
        };
        t.VolLineStart = ParseCoord(S("VolLineStart"));
        t.VolLineEnd = ParseCoord(S("VolLineEnd"));
        t.VolBoxHalfSize = ParseCoord(S("VolBoxHalfSize"));
        t.VolSphereRadius = RuleValue.Float(S("VolSphereRadius"));
        t.VolCylinderRadius = RuleValue.Float(S("VolCylinderRadius"));
        t.VolCylinderLength = RuleValue.Float(S("VolCylinderLength"));
        t.IsHollow = RuleValue.Bool(S("IsHollow"));
        t.IsGroundAligned = RuleValue.Bool(S("IsGroundAligned"));
        t.IsEmitAboveGroundOnly = RuleValue.Bool(S("IsEmitAboveGroundOnly"));
        t.IsParticleUpTowardsEmitter = RuleValue.Bool(S("IsParticleUpTowardsEmitter"));
        if (Has("WindMotion")) t.WindMotion = S("WindMotion").ToUpperInvariant() switch
        {
            "PINGPONG" => WindMotion.PingPong,
            "CIRCULAR" => WindMotion.Circular,
            "UNUSED" => WindMotion.NotUsed,
            _ => WindMotion.None,
        };
        if (Has("WindAngleChangeMin")) t.WindAngleChangeMin = RuleValue.Float(S("WindAngleChangeMin"));
        if (Has("WindAngleChangeMax")) t.WindAngleChangeMax = RuleValue.Float(S("WindAngleChangeMax"));
        if (Has("WindPingPongStartAngleMin")) t.WindPingPongStartAngleMin = RuleValue.Float(S("WindPingPongStartAngleMin"));
        if (Has("WindPingPongStartAngleMax")) t.WindPingPongStartAngleMax = RuleValue.Float(S("WindPingPongStartAngleMax"));
        if (Has("WindPingPongEndAngleMin")) t.WindPingPongEndAngleMin = RuleValue.Float(S("WindPingPongEndAngleMin"));
        if (Has("WindPingPongEndAngleMax")) t.WindPingPongEndAngleMax = RuleValue.Float(S("WindPingPongEndAngleMax"));
        return t;
    }

    static ParticlePriority ParsePriority(string value) => value.ToUpperInvariant() switch
    {
        "WEAPON_EXPLOSION" => ParticlePriority.WeaponExplosion,
        "SCORCHMARK" => ParticlePriority.Scorchmark,
        "DUST_TRAIL" => ParticlePriority.DustTrail,
        "BUILDUP" => ParticlePriority.Buildup,
        "DEBRIS_TRAIL" => ParticlePriority.DebrisTrail,
        "UNIT_DAMAGE_FX" => ParticlePriority.UnitDamageFx,
        "DEATH_EXPLOSION" => ParticlePriority.DeathExplosion,
        "SEMI_CONSTANT" => ParticlePriority.SemiConstant,
        "CONSTANT" => ParticlePriority.Constant,
        "WEAPON_TRAIL" => ParticlePriority.WeaponTrail,
        "AREA_EFFECT" => ParticlePriority.AreaEffect,
        "CRITICAL" => ParticlePriority.Critical,
        "ALWAYS_RENDER" => ParticlePriority.AlwaysRender,
        _ => ParticlePriority.WeaponExplosion,
    };

    static readonly Regex CoordPart = new(@"([XYZ])\s*:\s*(-?[\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ColorPart = new(@"([RGB])\s*:\s*(-?\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"X:1 Y:2 Z:3" (missing parts are 0).</summary>
    public static Vector3 ParseCoord(string text)
    {
        var v = Vector3.Zero;
        foreach (Match m in CoordPart.Matches(text))
        {
            float f = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            switch (char.ToUpperInvariant(m.Groups[1].Value[0]))
            {
                case 'X': v.X = f; break;
                case 'Y': v.Y = f; break;
                case 'Z': v.Z = f; break;
            }
        }
        return v;
    }

    /// <summary>"R:255 G:128 B:0" as 0..1 colors.</summary>
    public static Vector3 ParseColor(string text)
    {
        var c = Vector3.Zero;
        foreach (Match m in ColorPart.Matches(text))
        {
            float f = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 255f;
            switch (char.ToUpperInvariant(m.Groups[1].Value[0]))
            {
                case 'R': c.X = f; break;
                case 'G': c.Y = f; break;
                case 'B': c.Z = f; break;
            }
        }
        return c;
    }

    /// <summary>"R:255 G:255 B:255 30": a color and the frame it is reached.</summary>
    static ColorKeyframe ParseColorKey(string text)
    {
        var words = RuleValue.Words(text);
        int frame = words.Length > 0 && int.TryParse(words[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) ? f : 0;
        return new ColorKeyframe { Color = ParseColor(text), Frame = frame };
    }
}

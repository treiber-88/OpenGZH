using System.Globalization;
using System.Numerics;

namespace OpenGZH.Client.Graphics;

/// <summary>
/// A W3D texture-coordinate mapper (the original's WW3D2 mapper.cpp): animated or generated UVs for one
/// texture stage of a vertex material. Each frame it gives a texture matrix (two rows applied to the
/// input coordinate) and says what the input is: the mesh UVs, or the camera-space normal or reflection
/// vector (environment "chrome" mapping). The original mappers keep running state; these work the
/// same values out from the elapsed time, which gives the same result.
/// </summary>
public sealed class TextureMapper
{
    /// <summary>The W3DVERTMAT_STAGEn_MAPPING_* values.</summary>
    public enum MappingType
    {
        Uv = 0, Environment = 1, CheapEnvironment = 2, Screen = 3, LinearOffset = 4, Silhouette = 5, Scale = 6,
        Grid = 7, Rotate = 8, SineLinearOffset = 9, StepLinearOffset = 10, ZigZagLinearOffset = 11,
        WsClassicEnv = 12, WsEnvironment = 13, GridClassicEnv = 14, GridEnvironment = 15, Random = 16, Edge = 17, BumpEnv = 18,
    }

    /// <summary>What the texture matrix is applied to (the original's D3DTSS_TCI_* source).</summary>
    public enum InputSource { Uv = 0, CameraSpaceNormal = 1, CameraSpaceReflection = 2 }

    public MappingType Type { get; }
    public InputSource Source { get; }

    readonly Dictionary<string, string> args;
    readonly Vector2 scale = Vector2.One;
    readonly Vector2 offsetPerMs, startOffset;
    readonly bool clampFix;
    // Grid
    readonly int gridWidthLog2, lastFrame, gridOffset, sign;
    readonly float msPerFrame = 1, ooGridWidth = 1;
    // Rotate
    readonly float radiansPerMs;
    readonly Vector2 center;
    // Sine
    readonly Vector3 uafp, vafp;
    // Step
    readonly Vector2 step;
    readonly float stepsPerMs;

    TextureMapper(MappingType type, string argText)
    {
        Type = type;
        args = ParseArgs(argText);
        Source = type switch
        {
            MappingType.Environment or MappingType.WsEnvironment or MappingType.GridEnvironment => InputSource.CameraSpaceReflection,
            MappingType.CheapEnvironment or MappingType.WsClassicEnv or MappingType.GridClassicEnv => InputSource.CameraSpaceNormal,
            _ => InputSource.Uv,
        };
        if (type is MappingType.Scale or MappingType.LinearOffset or MappingType.Rotate or MappingType.SineLinearOffset
            or MappingType.StepLinearOffset or MappingType.ZigZagLinearOffset)
            scale = new Vector2(Float("UScale", 1), Float("VScale", 1));
        switch (type)
        {
            case MappingType.LinearOffset:
                offsetPerMs = new Vector2(Float("UPerSec", 0), Float("VPerSec", 0)) * -0.001f;
                startOffset = new Vector2(Float("UOffset", 0), Float("VOffset", 0));
                clampFix = Bool("ClampFix");
                break;
            case MappingType.Grid:
            case MappingType.GridClassicEnv:
            case MappingType.GridEnvironment:
            {
                float fps = Float("FPS", 1);
                gridWidthLog2 = (int)Float("Log2Width", 1);
                int gridWidth = 1 << gridWidthLog2;
                lastFrame = (int)Float("Last", 0);
                if (lastFrame == 0)
                    lastFrame = gridWidth * gridWidth;
                gridOffset = (int)Float("Offset", 0) % lastFrame;
                ooGridWidth = 1f / gridWidth;
                sign = fps == 0 ? 0 : fps < 0 ? -1 : 1;
                msPerFrame = fps == 0 ? 1 : (int)(1000f / MathF.Abs(fps));
                break;
            }
            case MappingType.Rotate:
                radiansPerMs = 2 * MathF.PI * Float("Speed", 0.1f) / 1000f;
                center = new Vector2(Float("UCenter", 0), Float("VCenter", 0));
                break;
            case MappingType.SineLinearOffset:
                uafp = new Vector3(Float("UAmp", 1), Float("UFreq", 1), Float("UPhase", 0));
                vafp = new Vector3(Float("VAmp", 1), Float("VFreq", 1), Float("VPhase", 0));
                break;
            case MappingType.StepLinearOffset:
                step = new Vector2(Float("UStep", 0), Float("VStep", 0));
                stepsPerMs = Float("SPS", 0) / 1000f;
                clampFix = Bool("ClampFix");
                break;
        }
    }

    /// <summary>The mapper for a stage, or null for plain UVs.</summary>
    public static TextureMapper? Create(int mapping, string args)
    {
        var type = (MappingType)mapping;
        return type == MappingType.Uv || !Enum.IsDefined(type) ? null : new TextureMapper(type, args);
    }

    /// <summary>
    /// The texture matrix rows at a time (milliseconds). For UV input the coordinate is (u, v, 1, 1);
    /// for normal/reflection input it is (x, y, z, 1). <paramref name="customOffset"/> is a per-object
    /// UV offset (tank treads move theirs as the tank drives).
    /// </summary>
    public (Vector4 Row0, Vector4 Row1) Matrix(float ms, Vector2 customOffset = default)
    {
        switch (Type)
        {
            case MappingType.Scale:
                return (new(scale.X, 0, 0, 0), new(0, scale.Y, 0, 0));
            case MappingType.LinearOffset:
            {
                var o = startOffset + customOffset + offsetPerMs * ms;
                if (!clampFix)
                    o -= new Vector2(MathF.Floor(o.X), MathF.Floor(o.Y));
                else
                    o = Vector2.Clamp(o, -scale, scale);
                return (new(scale.X, 0, o.X, 0), new(0, scale.Y, o.Y, 0));
            }
            case MappingType.Grid:
            {
                var (u, v) = GridOffset(ms);
                return (new(1, 0, u, 0), new(0, 1, v, 0));
            }
            case MappingType.GridClassicEnv:
            case MappingType.GridEnvironment:
            {
                var (u, v) = GridOffset(ms);
                float del = 0.5f * ooGridWidth;
                return (new(del, 0, 0, u + del), new(0, del, 0, v + del));
            }
            case MappingType.Rotate:
            {
                float angle = radiansPerMs * ms % (2 * MathF.PI);
                if (angle < 0)
                    angle += 2 * MathF.PI;
                float c = MathF.Cos(angle), s = MathF.Sin(angle);
                return (new(scale.X * c, -scale.X * s, -scale.X * (c * center.X - s * center.Y - center.X), 0),
                        new(scale.Y * s, scale.Y * c, -scale.Y * (s * center.X + c * center.Y - center.Y), 0));
            }
            case MappingType.SineLinearOffset:
            {
                float angle = ms * 2 * MathF.PI / 1000f;
                float u = uafp.X * MathF.Sin(uafp.Y * angle + uafp.Z * MathF.PI);
                float v = vafp.X * MathF.Sin(vafp.Y * angle + vafp.Z * MathF.PI);
                return (new(scale.X, 0, u, 0), new(0, scale.Y, v, 0));
            }
            case MappingType.StepLinearOffset:
            {
                var o = step * MathF.Floor(stepsPerMs * ms);
                if (!clampFix)
                    o -= new Vector2(MathF.Floor(o.X), MathF.Floor(o.Y));
                else
                    o = Vector2.Clamp(o, -scale, scale);
                return (new(scale.X, 0, o.X, 0), new(0, scale.Y, o.Y, 0));
            }
            case MappingType.Environment:
            case MappingType.CheapEnvironment:
            case MappingType.WsClassicEnv:
            case MappingType.WsEnvironment:
                // (The WS variants use world-space axes in the original; camera space is close enough here.)
                return (new(0.5f, 0, 0, 0.5f), new(0, 0.5f, 0, 0.5f));
            default:
                // Screen, silhouette, zigzag, random, edge, bump: not used by any Zero Hour model.
                return (new(1, 0, 0, 0), new(0, 1, 0, 0));
        }
    }

    (float U, float V) GridOffset(float ms)
    {
        int frame = sign >= 0 ? gridOffset : lastFrame - 1 - gridOffset;
        frame += (int)(ms / msPerFrame) * sign;
        frame %= lastFrame;
        if (frame < 0)
            frame += lastFrame;
        int rowMask = ~(-1 << gridWidthLog2);
        int x = frame & rowMask;
        int y = (frame >> gridWidthLog2) & rowMask;
        return (x * ooGridWidth, y * ooGridWidth);
    }

    /// <summary>The mapper argument text is an INI section body: "Key=Value" lines, ';' starts a comment.</summary>
    static Dictionary<string, string> ParseArgs(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n', '\r'))
        {
            string line = raw;
            int comment = line.IndexOf(';');
            if (comment >= 0)
                line = line[..comment];
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    float Float(string key, float fallback)
    {
        if (!args.TryGetValue(key, out var text))
            return fallback;
        // atof: the leading number.
        int end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] is '.' or '-' or '+' or 'e' or 'E'))
            end++;
        return float.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
    }

    bool Bool(string key) =>
        args.TryGetValue(key, out var text) && (text.StartsWith('1') || text.StartsWith("t", StringComparison.OrdinalIgnoreCase) || text.StartsWith("y", StringComparison.OrdinalIgnoreCase));
}

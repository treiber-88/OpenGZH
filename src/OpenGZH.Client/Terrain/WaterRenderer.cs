using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Maps;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.Terrain;

/// <summary>
/// Draws water areas. In the original game every water area is a polygon trigger flagged as water;
/// its points' height is the water level. The look (texture, colors, scrolling) comes from the
/// WaterSets rules for the map's time of day.
/// </summary>
public sealed class WaterRenderer : IDisposable
{
    const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        uniform mat4 uViewProjection;
        uniform vec2 uScroll;
        uniform float uTexelsPerUnit;
        out vec2 vUv;
        void main()
        {
            vUv = aPosition.xy * uTexelsPerUnit + uScroll;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uTexture;
        uniform vec4 uColor;
        out vec4 fragColor;
        void main()
        {
            vec4 texel = texture(uTexture, vUv);
            fragColor = vec4(texel.rgb * uColor.rgb, uColor.a);
        }
        """;

    readonly GL gl;
    readonly ShaderProgram shader;
    readonly Texture? texture;
    readonly uint vao, vbo;
    readonly int vertexCount;
    readonly Vector4 color;
    readonly Vector2 scrollPerSecond;
    float time;

    /// <summary>Water areas as triangles, for logic queries (e.g. "is this point water?").</summary>
    public List<PolygonTrigger> Areas { get; }

    public unsafe WaterRenderer(GL gl, MapFile map, RuleSet rules, TextureCache textures)
    {
        this.gl = gl;
        shader = new ShaderProgram(gl, VertexSource, FragmentSource);
        Areas = map.PolygonTriggers.Where(t => t.IsWater).ToList();

        string timeName = map.Lighting.CurrentTime.ToString().ToUpperInvariant();
        var waterSet = rules.Get("WaterSets", timeName) ?? rules.Get("WaterSets", "AFTERNOON");
        texture = textures.Get(waterSet?.GetString("WaterTexture") ?? "TSWater.tga");
        color = RuleColor.Parse(waterSet?.GetString("TransparentDiffuseColor"), new Vector4(1, 1, 1, 0.5f));
        float u = RuleValue.Float(waterSet?.GetString("UScrollPerMS"), 0.002f) * 1000;
        float v = RuleValue.Float(waterSet?.GetString("VScrollPerMS"), 0.002f) * 1000;
        scrollPerSecond = new Vector2(u, v) * 0.05f;

        var vertices = new List<float>();
        foreach (var area in Areas)
            foreach (var triangle in Triangulate(area.Points))
                foreach (var point in triangle)
                {
                    vertices.Add(point.X);
                    vertices.Add(point.Y);
                    vertices.Add(point.Z);
                }
        vertexCount = vertices.Count / 3;

        vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        var array = vertices.ToArray();
        fixed (float* data = array)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(array.Length * sizeof(float)), data, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);
    }

    public void Update(float seconds) => time += seconds;

    public void Draw(Matrix4x4 viewProjection)
    {
        if (vertexCount == 0)
            return;

        shader.Use();
        shader.Set("uViewProjection", viewProjection);
        shader.Set("uScroll", scrollPerSecond * time);
        shader.Set("uTexelsPerUnit", 1f / 160f);
        shader.Set("uColor", color);
        shader.Set("uTexture", 0);
        (texture)?.Bind(0);

        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(false);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(vao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)vertexCount);
        gl.BindVertexArray(0);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
    }

    /// <summary>Ear-clipping triangulation of a simple polygon (water areas are drawn by hand and may be concave).</summary>
    public static List<Vector3[]> Triangulate(IReadOnlyList<Vector3> polygon)
    {
        var result = new List<Vector3[]>();
        var points = polygon.ToList();
        if (points.Count < 3)
            return result;

        // Work counter-clockwise.
        float area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        if (area < 0)
            points.Reverse();

        int guard = points.Count * points.Count;
        while (points.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < points.Count; i++)
            {
                var prev = points[(i + points.Count - 1) % points.Count];
                var current = points[i];
                var next = points[(i + 1) % points.Count];
                if (Cross(prev, current, next) <= 0)
                    continue; // reflex corner

                bool containsOther = false;
                foreach (var p in points)
                {
                    if (p == prev || p == current || p == next)
                        continue;
                    if (InTriangle(p, prev, current, next))
                    {
                        containsOther = true;
                        break;
                    }
                }
                if (containsOther)
                    continue;

                result.Add([prev, current, next]);
                points.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
                break; // degenerate polygon; give up on the rest
        }
        if (points.Count == 3)
            result.Add([points[0], points[1], points[2]]);
        return result;
    }

    static float Cross(Vector3 a, Vector3 b, Vector3 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    static bool InTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c) =>
        Cross(a, b, p) >= 0 && Cross(b, c, p) >= 0 && Cross(c, a, p) >= 0;

    public void Dispose()
    {
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
        shader.Dispose();
    }
}

using System.Numerics;
using OpenGZH.Client.Graphics;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.Effects;

/// <summary>How a batch of effect geometry blends with the scene (the original's sprite shaders).</summary>
public enum EffectBlend
{
    /// <summary>Adds light: color times texture, alpha ignored (fire, flashes, lasers).</summary>
    Additive,
    /// <summary>Ordinary transparency (smoke, dust).</summary>
    Alpha,
    /// <summary>Cut-out: pixels below the alpha threshold are dropped.</summary>
    AlphaTest,
    /// <summary>Darkens what is behind it.</summary>
    Multiply,
}

/// <summary>
/// Draws camera-facing quads and ribbons for particles, streaks, beams and tracers. Geometry is
/// rebuilt each frame into one buffer and drawn in batches that share a texture and blend mode.
/// Effects test against depth but never write it.
/// </summary>
public sealed class EffectsRenderer : IDisposable
{
    const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec2 aUV;
        layout(location = 2) in vec4 aColor;
        uniform mat4 uViewProjection;
        out vec2 vUV;
        out vec4 vColor;
        void main()
        {
            vUV = aUV;
            vColor = aColor;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec2 vUV;
        in vec4 vColor;
        uniform sampler2D uTexture;
        uniform int uBlend;   // 0 additive, 1 alpha, 2 alpha test, 3 multiply
        uniform vec2 uScroll;
        out vec4 fragColor;
        void main()
        {
            vec4 tex = texture(uTexture, vUV + uScroll);
            if (uBlend == 0)
                fragColor = vec4(tex.rgb * vColor.rgb * tex.a, 1.0);
            else if (uBlend == 2)
            {
                vec4 c = tex * vColor;
                if (c.a < 0.375)
                    discard;
                fragColor = c;
            }
            else if (uBlend == 3)
                fragColor = vec4(tex.rgb * vColor.rgb, 1.0);
            else
                fragColor = tex * vColor;
        }
        """;

    const int FloatsPerVertex = 9;

    readonly GL gl;
    readonly ShaderProgram shader;
    readonly uint vao, vbo;
    readonly Texture white;
    float[] vertices = new float[FloatsPerVertex * 6 * 4096];
    int vertexCount;
    int bufferCapacity;

    readonly List<(Texture? Texture, EffectBlend Blend, Vector2 Scroll, int Start, int Count)> batches = new();

    Vector3 cameraRight = Vector3.UnitX, cameraUp = Vector3.UnitZ, cameraPosition;

    public unsafe EffectsRenderer(GL gl, TextureCache textures)
    {
        this.gl = gl;
        white = textures.White;
        shader = new ShaderProgram(gl, VertexSource, FragmentSource);
        vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        uint stride = FloatsPerVertex * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
        gl.BindVertexArray(0);
    }

    /// <summary>Starts a frame: the camera decides which way quads and ribbons face.</summary>
    public void Begin(Camera camera)
    {
        var view = camera.View;
        cameraRight = new Vector3(view.M11, view.M21, view.M31);
        cameraUp = new Vector3(view.M12, view.M22, view.M32);
        cameraPosition = camera.Position;
        vertexCount = 0;
        batches.Clear();
    }

    void StartBatch(Texture? texture, EffectBlend blend, Vector2 scroll = default)
    {
        if (batches.Count > 0)
        {
            var last = batches[^1];
            if (last.Texture == texture && last.Blend == blend && last.Scroll == scroll)
                return;
        }
        batches.Add((texture, blend, scroll, vertexCount, 0));
    }

    void Vertex(Vector3 p, float u, float v, Vector4 color)
    {
        int needed = (vertexCount + 1) * FloatsPerVertex;
        if (needed > vertices.Length)
            Array.Resize(ref vertices, vertices.Length * 2);
        int i = vertexCount * FloatsPerVertex;
        vertices[i] = p.X; vertices[i + 1] = p.Y; vertices[i + 2] = p.Z;
        vertices[i + 3] = u; vertices[i + 4] = v;
        vertices[i + 5] = color.X; vertices[i + 6] = color.Y; vertices[i + 7] = color.Z; vertices[i + 8] = color.W;
        vertexCount++;
        var last = batches[^1];
        batches[^1] = last with { Count = last.Count + 1 };
    }

    void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector4 color, float u0 = 0, float v0 = 0, float u1 = 1, float v1 = 1)
    {
        Vertex(a, u0, v0, color); Vertex(b, u1, v0, color); Vertex(c, u1, v1, color);
        Vertex(a, u0, v0, color); Vertex(c, u1, v1, color); Vertex(d, u0, v1, color);
    }

    /// <summary>One textured triangle (scorch marks on the terrain grid).</summary>
    public void Triangle(Texture? texture, EffectBlend blend, Vector3 a, Vector2 uvA, Vector3 b, Vector2 uvB, Vector3 c, Vector2 uvC, Vector4 color)
    {
        StartBatch(texture, blend);
        Vertex(a, uvA.X, uvA.Y, color);
        Vertex(b, uvB.X, uvB.Y, color);
        Vertex(c, uvC.X, uvC.Y, color);
    }

    /// <summary>
    /// One particle as a square of side <paramref name="size"/>, turned by <paramref name="angle"/>:
    /// facing the camera, or lying flat on the ground.
    /// </summary>
    public void Sprite(Texture? texture, EffectBlend blend, Vector3 center, float size, float angle, Vector4 color, bool groundAligned)
    {
        StartBatch(texture, blend);
        var right = groundAligned ? Vector3.UnitX : cameraRight;
        var up = groundAligned ? Vector3.UnitY : cameraUp;
        float c = MathF.Cos(angle) * size * 0.5f, s = MathF.Sin(angle) * size * 0.5f;
        var r = right * c + up * s;
        var u = -right * s + up * c;
        Quad(center - r + u, center + r + u, center + r - u, center - r - u, color);
    }

    /// <summary>
    /// A ribbon through points, facing the camera (streak particles, laser beams, tracers). Texture u
    /// runs along the ribbon <paramref name="tiles"/> times; v runs across it.
    /// </summary>
    /// <param name="tilePerPoint">
    /// The texture coordinate along the ribbon goes up by <paramref name="tiles"/> at each point (the
    /// original's segmented-line TILED_TEXTURE_MAP), instead of spreading <paramref name="tiles"/> over the length.
    /// </param>
    public void Ribbon(Texture? texture, EffectBlend blend, ReadOnlySpan<Vector3> points, ReadOnlySpan<float> widths, ReadOnlySpan<Vector4> colors,
        float tiles = 1, Vector2 scroll = default, bool tilePerPoint = false)
    {
        if (points.Length < 2)
            return;
        StartBatch(texture, blend, scroll);
        float total = 0;
        for (int i = 1; i < points.Length; i++)
            total += Vector3.Distance(points[i], points[i - 1]);
        if (total <= 0)
            return;
        float along = 0;
        for (int i = 0; i < points.Length - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            var dir = b - a;
            float length = dir.Length();
            if (length <= 0)
                continue;
            var side = Vector3.Cross(dir, cameraPosition - (a + b) * 0.5f);
            side = side.LengthSquared() > 0 ? Vector3.Normalize(side) : cameraRight;
            var wa = side * widths[i] * 0.5f;
            var wb = side * widths[i + 1] * 0.5f;
            float u0 = tilePerPoint ? i * tiles : along / total * tiles;
            along += length;
            float u1 = tilePerPoint ? (i + 1) * tiles : along / total * tiles;
            Vertex(a - wa, u0, 0, colors[i]); Vertex(b - wb, u1, 0, colors[i + 1]); Vertex(b + wb, u1, 1, colors[i + 1]);
            Vertex(a - wa, u0, 0, colors[i]); Vertex(b + wb, u1, 1, colors[i + 1]); Vertex(a + wa, u0, 1, colors[i]);
        }
    }

    /// <summary>A straight beam from a to b (one laser layer).</summary>
    public void Beam(Texture? texture, EffectBlend blend, Vector3 a, Vector3 b, float width, Vector4 color, float tiles = 1, Vector2 scroll = default)
    {
        Span<Vector3> points = [a, b];
        Span<float> widths = [width, width];
        Span<Vector4> colors = [color, color];
        Ribbon(texture, blend, points, widths, colors, tiles, scroll);
    }

    public unsafe void Draw(Matrix4x4 viewProjection)
    {
        if (vertexCount == 0)
            return;
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        int bytes = vertexCount * FloatsPerVertex * sizeof(float);
        fixed (float* data = vertices)
        {
            if (bytes > bufferCapacity)
            {
                bufferCapacity = Math.Max(bytes, bufferCapacity * 2);
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)bufferCapacity, null, BufferUsageARB.StreamDraw);
            }
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)bytes, data);
        }

        shader.Use();
        shader.Set("uViewProjection", viewProjection);
        shader.Set("uTexture", 0);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(false);
        gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        foreach (var batch in batches)
        {
            if (batch.Count == 0)
                continue;
            (batch.Texture ?? white).Bind(0);
            shader.Set("uBlend", (int)batch.Blend);
            shader.Set("uScroll", batch.Scroll);
            switch (batch.Blend)
            {
                case EffectBlend.Additive:
                    gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
                    break;
                case EffectBlend.Multiply:
                    gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
                    break;
                default:
                    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    break;
            }
            gl.DrawArrays(PrimitiveType.Triangles, batch.Start, (uint)batch.Count);
        }
        gl.BindVertexArray(0);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        shader.Dispose();
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
    }
}

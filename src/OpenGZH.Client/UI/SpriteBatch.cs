using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Formats.Images;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.UI;

/// <summary>A screen rectangle in pixels (origin top-left).</summary>
public readonly record struct Rect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public bool Contains(Vector2 point) => point.X >= X && point.Y >= Y && point.X < Right && point.Y < Bottom;
    public Rect Inflate(float amount) => new(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);

    public Rect Intersect(Rect other)
    {
        float x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        float right = Math.Min(Right, other.Right), bottom = Math.Min(Bottom, other.Bottom);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}

/// <summary>
/// Draws 2D textured and coloured quads in screen pixels, batching consecutive quads that use the
/// same texture into one draw call. Used for the whole interface and for in-game overlays.
/// </summary>
public sealed class SpriteBatch : IDisposable
{
    const int MaxQuads = 4096;
    const int FloatsPerVertex = 8; // position(2) uv(2) color(4)

    const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec4 aColor;
        uniform mat4 uProjection;
        out vec2 vUv;
        out vec4 vColor;
        void main()
        {
            vUv = aUv;
            vColor = aColor;
            gl_Position = uProjection * vec4(aPosition, 0.0, 1.0);
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        in vec4 vColor;
        uniform sampler2D uTexture;
        uniform int uAlphaOnly;
        out vec4 fragColor;
        void main()
        {
            vec4 texel = texture(uTexture, vUv);
            // Font atlases store coverage in the red channel only.
            fragColor = uAlphaOnly == 1 ? vec4(vColor.rgb, vColor.a * texel.r) : texel * vColor;
        }
        """;

    readonly GL gl;
    readonly ShaderProgram shader;
    readonly uint vao, vbo, ebo;
    readonly float[] vertices = new float[MaxQuads * 4 * FloatsPerVertex];
    readonly Texture white;
    int quadCount;
    uint currentTexture;
    bool currentAlphaOnly;
    int screenWidth, screenHeight;
    Rect? clip;

    public SpriteBatch(GL gl)
    {
        this.gl = gl;
        shader = new ShaderProgram(gl, VertexSource, FragmentSource);
        var image = new Image(1, 1);
        image.SetPixel(0, 0, 255, 255, 255, 255);
        white = new Texture(gl, image, mipmaps: false);

        vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        unsafe
        {
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), null, BufferUsageARB.DynamicDraw);
            var indices = new uint[MaxQuads * 6];
            for (uint q = 0; q < MaxQuads; q++)
            {
                uint v = q * 4;
                int i = (int)q * 6;
                indices[i] = v; indices[i + 1] = v + 1; indices[i + 2] = v + 2;
                indices[i + 3] = v; indices[i + 4] = v + 2; indices[i + 5] = v + 3;
            }
            ebo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            fixed (uint* data = indices)
                gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), data, BufferUsageARB.StaticDraw);

            uint stride = FloatsPerVertex * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));
        }
        gl.BindVertexArray(0);
    }

    public int ScreenWidth => screenWidth;
    public int ScreenHeight => screenHeight;

    public void Begin(int width, int height)
    {
        screenWidth = width;
        screenHeight = height;
        quadCount = 0;
        clip = null;
        shader.Use();
        shader.Set("uProjection", Matrix4x4.CreateOrthographicOffCenter(0, width, height, 0, -1, 1));
        shader.Set("uTexture", 0);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.Disable(EnableCap.ScissorTest);
    }

    public void End()
    {
        Flush();
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.Blend);
    }

    /// <summary>Restricts drawing to a rectangle (null = whole screen).</summary>
    public void SetClip(Rect? rect)
    {
        Flush();
        clip = rect;
        if (rect is { } r)
        {
            gl.Enable(EnableCap.ScissorTest);
            gl.Scissor((int)r.X, (int)(screenHeight - r.Bottom), (uint)Math.Max(0, r.Width), (uint)Math.Max(0, r.Height));
        }
        else
        {
            gl.Disable(EnableCap.ScissorTest);
        }
    }

    public Rect? Clip => clip;

    public void FillRect(Rect rect, Vector4 color) => Quad(white.Handle, false, rect, 0, 0, 1, 1, color);

    public void DrawRect(Rect rect, Vector4 color, float thickness = 1)
    {
        FillRect(new Rect(rect.X, rect.Y, rect.Width, thickness), color);
        FillRect(new Rect(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
        FillRect(new Rect(rect.X, rect.Y, thickness, rect.Height), color);
        FillRect(new Rect(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
    }

    public void DrawLine(Vector2 a, Vector2 b, Vector4 color, float thickness = 1)
    {
        var direction = b - a;
        float length = direction.Length();
        if (length < 0.001f)
            return;
        var normal = new Vector2(-direction.Y, direction.X) / length * (thickness / 2);
        AddQuad(white.Handle, false, a + normal, b + normal, b - normal, a - normal, Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY, color);
    }

    /// <summary>Draws part of a texture: (u0,v0)-(u1,v1) in 0..1 texture coordinates.</summary>
    public void DrawTexture(Texture texture, Rect destination, float u0, float v0, float u1, float v1, Vector4 color) =>
        Quad(texture.Handle, false, destination, u0, v0, u1, v1, color);

    public void DrawTexture(Texture texture, Rect destination, Vector4? color = null) =>
        Quad(texture.Handle, false, destination, 0, 0, 1, 1, color ?? Vector4.One);

    /// <summary>Draws a texture with an explicit UV for each corner (top-left, top-right, bottom-right, bottom-left).</summary>
    public void DrawTextureCorners(Texture texture, Rect r, Vector2 uvTopLeft, Vector2 uvTopRight, Vector2 uvBottomRight, Vector2 uvBottomLeft, Vector4 color) =>
        AddQuad(texture.Handle, false,
            new Vector2(r.X, r.Y), new Vector2(r.Right, r.Y), new Vector2(r.Right, r.Bottom), new Vector2(r.X, r.Bottom),
            uvTopLeft, uvTopRight, uvBottomRight, uvBottomLeft, color);

    /// <summary>Draws a glyph from a font atlas (coverage in the red channel), tinted.</summary>
    internal void DrawGlyph(uint atlas, Rect destination, float u0, float v0, float u1, float v1, Vector4 color) =>
        Quad(atlas, true, destination, u0, v0, u1, v1, color);

    void Quad(uint texture, bool alphaOnly, Rect r, float u0, float v0, float u1, float v1, Vector4 color) =>
        AddQuad(texture, alphaOnly,
            new Vector2(r.X, r.Y), new Vector2(r.Right, r.Y), new Vector2(r.Right, r.Bottom), new Vector2(r.X, r.Bottom),
            new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(u0, v1), color);

    void AddQuad(uint texture, bool alphaOnly, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
        Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3, Vector4 color)
    {
        if (color.W <= 0)
            return;
        if (texture != currentTexture || alphaOnly != currentAlphaOnly || quadCount >= MaxQuads)
        {
            Flush();
            currentTexture = texture;
            currentAlphaOnly = alphaOnly;
        }
        int o = quadCount * 4 * FloatsPerVertex;
        Span<Vector2> points = [p0, p1, p2, p3];
        Span<Vector2> uvs = [t0, t1, t2, t3];
        for (int i = 0; i < 4; i++)
        {
            vertices[o++] = points[i].X; vertices[o++] = points[i].Y;
            vertices[o++] = uvs[i].X; vertices[o++] = uvs[i].Y;
            vertices[o++] = color.X; vertices[o++] = color.Y; vertices[o++] = color.Z; vertices[o++] = color.W;
        }
        quadCount++;
    }

    unsafe void Flush()
    {
        if (quadCount == 0)
            return;
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* data = vertices)
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(quadCount * 4 * FloatsPerVertex * sizeof(float)), data);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, currentTexture);
        shader.Set("uAlphaOnly", currentAlphaOnly ? 1 : 0);
        gl.DrawElements(PrimitiveType.Triangles, (uint)(quadCount * 6), DrawElementsType.UnsignedInt, (void*)0);
        quadCount = 0;
    }

    public void Dispose()
    {
        gl.DeleteBuffer(vbo);
        gl.DeleteBuffer(ebo);
        gl.DeleteVertexArray(vao);
        white.Dispose();
        shader.Dispose();
    }
}

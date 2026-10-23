using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.World;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Maps;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.Terrain;

/// <summary>
/// Draws the map's roads (the original's W3DRoadBuffer::drawRoads): the pieces from
/// <see cref="RoadBuilder"/>, one batch per road type in stacking order, blended over the terrain
/// with its lighting, without writing depth.
/// </summary>
public sealed class RoadRenderer : IDisposable
{
    const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec3 aNormal;
        uniform mat4 uViewProjection;
        uniform vec3 uAmbient;
        uniform vec3 uLightDir[3];
        uniform vec3 uLightColor[3];
        out vec2 vUv;
        out vec3 vLight;
        out vec2 vWorld;
        void main()
        {
            vWorld = aPosition.xy;
            vec3 light = uAmbient;
            for (int i = 0; i < 3; i++)
                light += uLightColor[i] * max(dot(aNormal, -uLightDir[i]), 0.0);
            vLight = min(light, vec3(1.0));
            vUv = aUv;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        in vec3 vLight;
        in vec2 vWorld;
        uniform sampler2D uTexture;
        uniform sampler2D uShroud;
        uniform vec2 uShroudScale;
        out vec4 fragColor;
        void main()
        {
            vec4 texel = texture(uTexture, vUv);
            float fog = uShroudScale.x > 0.0 ? texture(uShroud, vWorld * uShroudScale).r : 1.0;
            fragColor = vec4(texel.rgb * vLight * fog, texel.a);
        }
        """;

    readonly GL gl;
    readonly ShaderProgram shader;
    readonly List<(Texture? Texture, uint Vao, uint Vbo, uint Ebo, int IndexCount)> batches = new();

    public RoadBuilder Builder { get; }

    public unsafe RoadRenderer(GL gl, MapFile map, RuleSet rules, TextureCache textures, Logic.Terrain.TerrainLogic terrain)
    {
        this.gl = gl;
        shader = new ShaderProgram(gl, VertexSource, FragmentSource);
        Builder = new RoadBuilder(map, rules);

        foreach (var (type, pieces) in Builder.DrawOrder())
        {
            var vertices = new List<float>();
            var indices = new List<uint>();
            foreach (var piece in pieces)
            {
                uint baseVertex = (uint)(vertices.Count / 8);
                foreach (var v in piece.Vertices)
                {
                    var n = terrain.GroundNormal(v.Position.X, v.Position.Y);
                    vertices.AddRange([v.Position.X, v.Position.Y, v.Position.Z, v.Uv.X, v.Uv.Y, n.X, n.Y, n.Z]);
                }
                foreach (ushort i in piece.Indices)
                    indices.Add(baseVertex + i);
            }
            if (indices.Count == 0)
                continue;

            uint vao = gl.GenVertexArray();
            gl.BindVertexArray(vao);
            uint vbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            var vArray = vertices.ToArray();
            fixed (float* data = vArray)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vArray.Length * sizeof(float)), data, BufferUsageARB.StaticDraw);
            uint ebo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            var iArray = indices.ToArray();
            fixed (uint* data = iArray)
                gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(iArray.Length * sizeof(uint)), data, BufferUsageARB.StaticDraw);
            const uint stride = 8 * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
            gl.BindVertexArray(0);
            batches.Add((textures.Get(type.Texture), vao, vbo, ebo, iArray.Length));
        }
    }

    /// <summary>The fog of war to darken the roads with, like the ground under them (null = none).</summary>
    public World.ShroudTexture? Shroud { get; set; }

    public void Draw(Matrix4x4 viewProjection, SceneLighting lighting)
    {
        if (batches.Count == 0)
            return;
        shader.Use();
        shader.Set("uShroud", 2);
        shader.Set("uShroudScale", Shroud != null ? new Vector2(1 / Shroud.WorldWidth, 1 / Shroud.WorldHeight) : Vector2.Zero);
        Shroud?.Bind(2);
        shader.Set("uViewProjection", viewProjection);
        shader.Set("uAmbient", lighting.Ambient);
        for (int i = 0; i < 3; i++)
        {
            shader.Set($"uLightDir[{i}]", lighting.Directions[i]);
            shader.Set($"uLightColor[{i}]", lighting.Colors[i]);
        }
        shader.Set("uTexture", 0);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.DepthMask(false);
        gl.Disable(EnableCap.CullFace);
        foreach (var (texture, vao, _, _, count) in batches)
        {
            texture?.Bind(0);
            gl.BindVertexArray(vao);
            unsafe { gl.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedInt, (void*)0); }
        }
        gl.BindVertexArray(0);
        gl.DepthMask(true);
        gl.DepthFunc(DepthFunction.Less);
        gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        foreach (var (_, vao, vbo, ebo, _) in batches)
        {
            gl.DeleteBuffer(vbo);
            gl.DeleteBuffer(ebo);
            gl.DeleteVertexArray(vao);
        }
        batches.Clear();
        shader.Dispose();
    }
}

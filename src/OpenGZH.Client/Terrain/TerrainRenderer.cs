using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Formats.Maps;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.Terrain;

/// <summary>
/// Draws a map's terrain: a base layer of textured cells, then up to two blend layers that fade
/// a second texture in across a cell (per-corner alpha, as in the original renderer).
/// The terrain is split into chunks so off-screen parts can be skipped.
/// </summary>
public sealed class TerrainRenderer : IDisposable
{
    const int ChunkCells = 64;
    const int FloatsPerVertex = 9; // position(3) normal(3) uv(2) alpha(1)

    const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in float aAlpha;

        uniform mat4 uViewProjection;

        out vec3 vNormal;
        out vec2 vUv;
        out float vAlpha;
        out vec2 vWorld;

        void main()
        {
            vWorld = aPosition.xy;
            vNormal = aNormal;
            vUv = aUv;
            vAlpha = aAlpha;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec3 vNormal;
        in vec2 vUv;
        in float vAlpha;
        in vec2 vWorld;

        uniform sampler2D uAtlas;
        uniform sampler2D uShroud;
        uniform vec2 uShroudScale;
        uniform vec3 uAmbientLight;
        uniform vec3 uLightDirection[3];
        uniform vec3 uLightColor[3];

        out vec4 fragColor;

        void main()
        {
            vec3 normal = normalize(vNormal);
            vec3 light = uAmbientLight;
            for (int i = 0; i < 3; i++)
                light += max(dot(normal, -uLightDirection[i]), 0.0) * uLightColor[i];
            vec4 texel = texture(uAtlas, vUv);
            float fog = uShroudScale.x > 0.0 ? texture(uShroud, vWorld * uShroudScale).r : 1.0;
            fragColor = vec4(texel.rgb * clamp(light, 0.0, 1.0) * fog, vAlpha);
        }
        """;

    sealed class Layer(uint vao, uint vbo, uint ebo, int indexCount)
    {
        public readonly uint Vao = vao, Vbo = vbo, Ebo = ebo;
        public readonly int IndexCount = indexCount;
    }

    sealed class Chunk
    {
        public Vector3 Min, Max;
        public readonly List<Layer> Layers = new(); // [0] base, [1..] blends
    }

    readonly GL gl;
    readonly ShaderProgram shader;
    readonly Texture atlasTexture;
    readonly List<Chunk> chunks = new();

    public TerrainAtlas Atlas { get; }

    public TerrainRenderer(GL gl, MapFile map, TerrainAtlas atlas)
    {
        Atlas = atlas;
        this.gl = gl;
        shader = new ShaderProgram(gl, VertexSource, FragmentSource);
        atlasTexture = new Texture(gl, atlas.Image, repeat: false);

        var heights = map.HeightMap;
        var blendTiles = map.BlendTiles;
        int cellsX = heights.Width - 1, cellsY = heights.Height - 1;

        for (int cy = 0; cy < cellsY; cy += ChunkCells)
            for (int cx = 0; cx < cellsX; cx += ChunkCells)
                chunks.Add(BuildChunk(map, atlas, blendTiles, cx, cy, Math.Min(cx + ChunkCells, cellsX), Math.Min(cy + ChunkCells, cellsY)));
    }

    Chunk BuildChunk(MapFile map, TerrainAtlas atlas, BlendTileData? blendTiles, int x0, int y0, int x1, int y1)
    {
        var heights = map.HeightMap;
        var chunk = new Chunk { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        var baseVertices = new List<float>();
        var blendVertices = new[] { new List<float>(), new List<float>() };
        var baseFlips = new List<bool>();
        var blendFlips = new[] { new List<bool>(), new List<bool>() };

        Span<Vector2> uv = stackalloc Vector2[4];
        Span<Vector3> corners = stackalloc Vector3[4];
        Span<Vector3> normals = stackalloc Vector3[4];
        Span<float> alpha = stackalloc float[4];

        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                // Corner order matches the original: (x,y), (x+1,y), (x+1,y+1), (x,y+1).
                corners[0] = GridPoint(heights, x, y);
                corners[1] = GridPoint(heights, x + 1, y);
                corners[2] = GridPoint(heights, x + 1, y + 1);
                corners[3] = GridPoint(heights, x, y + 1);
                normals[0] = GridNormal(heights, x, y);
                normals[1] = GridNormal(heights, x + 1, y);
                normals[2] = GridNormal(heights, x + 1, y + 1);
                normals[3] = GridNormal(heights, x, y + 1);
                for (int i = 0; i < 4; i++)
                {
                    chunk.Min = Vector3.Min(chunk.Min, corners[i]);
                    chunk.Max = Vector3.Max(chunk.Max, corners[i]);
                }

                int cell = y * heights.Width + x;
                if (blendTiles == null || cell >= blendTiles.TileIndices.Length)
                {
                    uv.Clear();
                    alpha.Fill(1);
                    AppendCell(baseVertices, corners, normals, uv, alpha);
                    baseFlips.Add(false);
                    continue;
                }

                int baseTile = blendTiles.TileIndices[cell];
                bool cliffFlip = false;
                var cliff = blendTiles.CliffInfoIndices[cell] > 0 && blendTiles.CliffInfoIndices[cell] < blendTiles.CliffInfos.Count
                    ? blendTiles.CliffInfos[blendTiles.CliffInfoIndices[cell]] : null;

                if (cliff != null && atlas.TryGetCliffUv(cliff, baseTile, uv))
                    cliffFlip = cliff.Flip;
                else if (!atlas.TryGetCellUv(baseTile, uv))
                    uv.Clear();

                // One triangle split per cell, shared by all layers so they stay coplanar.
                bool flip = cliffFlip;
                var blend1 = BlendAt(blendTiles, blendTiles.BlendIndices[cell]);
                var blend2 = BlendAt(blendTiles, blendTiles.ExtraBlendIndices[cell]);
                if (blend1 != null) flip = NeedsFlip(blend1);
                else if (blend2 != null) flip = NeedsFlip(blend2);

                alpha.Fill(1);
                AppendCell(baseVertices, corners, normals, uv, alpha);
                baseFlips.Add(flip);

                var blends = new[] { blend1, blend2 };
                for (int layer = 0; layer < 2; layer++)
                {
                    var blend = blends[layer];
                    if (blend == null || blend.CustomBlendEdgeClass >= 0)
                        continue;
                    if (!(cliff != null && atlas.TryGetCliffUv(cliff, blend.TileIndex, uv)) && !atlas.TryGetCellUv(blend.TileIndex, uv))
                        continue;
                    BlendAlpha(blend, alpha);
                    AppendCell(blendVertices[layer], corners, normals, uv, alpha);
                    blendFlips[layer].Add(flip);
                }
            }
        }

        chunk.Layers.Add(Upload(baseVertices, baseFlips));
        for (int layer = 0; layer < 2; layer++)
            if (blendFlips[layer].Count > 0)
                chunk.Layers.Add(Upload(blendVertices[layer], blendFlips[layer]));
        return chunk;
    }

    static BlendInfo? BlendAt(BlendTileData tiles, short index) =>
        index > 0 && index < tiles.Blends.Count ? tiles.Blends[index] : null;

    /// <summary>Which diagonal to split the cell along, so the blend gradient follows it (original rules).</summary>
    static bool NeedsFlip(BlendInfo blend)
    {
        if (blend.Horizontal || blend.Vertical) return blend.Flipped;
        if (blend.RightDiagonal) return !blend.Inverted;
        if (blend.LeftDiagonal) return blend.Inverted;
        return false;
    }

    /// <summary>Per-corner opacity of a blend layer (the original's getAlphaUVData).</summary>
    static void BlendAlpha(BlendInfo blend, Span<float> alpha)
    {
        alpha.Clear();
        if (blend.Horizontal)
        {
            if (blend.Inverted) alpha[0] = alpha[3] = 1; else alpha[1] = alpha[2] = 1;
        }
        if (blend.Vertical)
        {
            if (blend.Inverted) alpha[0] = alpha[1] = 1; else alpha[2] = alpha[3] = 1;
        }
        if (blend.RightDiagonal)
        {
            if (blend.Inverted)
            {
                alpha[1] = 1;
                if (blend.LongDiagonal) alpha[0] = alpha[2] = 1;
            }
            else
            {
                alpha[2] = 1;
                if (blend.LongDiagonal) alpha[1] = alpha[3] = 1;
            }
        }
        if (blend.LeftDiagonal)
        {
            if (blend.Inverted)
            {
                alpha[0] = 1;
                if (blend.LongDiagonal) alpha[1] = alpha[3] = 1;
            }
            else
            {
                alpha[3] = 1;
                if (blend.LongDiagonal) alpha[0] = alpha[2] = 1;
            }
        }
    }

    public static Vector3 GridPoint(HeightMap heights, int x, int y) => new(
        (x - heights.BorderSize) * MapFile.CellSize,
        (y - heights.BorderSize) * MapFile.CellSize,
        heights.WorldHeight(x, y));

    static Vector3 GridNormal(HeightMap heights, int x, int y)
    {
        float dx = heights.WorldHeight(x + 1, y) - heights.WorldHeight(x - 1, y);
        float dy = heights.WorldHeight(x, y + 1) - heights.WorldHeight(x, y - 1);
        return Vector3.Normalize(new Vector3(-dx, -dy, 2 * MapFile.CellSize));
    }

    static void AppendCell(List<float> vertices, ReadOnlySpan<Vector3> corners, ReadOnlySpan<Vector3> normals, ReadOnlySpan<Vector2> uv, ReadOnlySpan<float> alpha)
    {
        for (int i = 0; i < 4; i++)
        {
            vertices.Add(corners[i].X); vertices.Add(corners[i].Y); vertices.Add(corners[i].Z);
            vertices.Add(normals[i].X); vertices.Add(normals[i].Y); vertices.Add(normals[i].Z);
            vertices.Add(uv[i].X); vertices.Add(uv[i].Y);
            vertices.Add(alpha[i]);
        }
    }

    unsafe Layer Upload(List<float> vertices, List<bool> flips)
    {
        var indices = new uint[flips.Count * 6];
        for (int c = 0; c < flips.Count; c++)
        {
            uint b = (uint)(c * 4);
            int o = c * 6;
            if (flips[c])
            {
                // Split along corners 1-3.
                indices[o] = b; indices[o + 1] = b + 1; indices[o + 2] = b + 3;
                indices[o + 3] = b + 1; indices[o + 4] = b + 2; indices[o + 5] = b + 3;
            }
            else
            {
                // Split along corners 0-2.
                indices[o] = b; indices[o + 1] = b + 1; indices[o + 2] = b + 2;
                indices[o + 3] = b; indices[o + 4] = b + 2; indices[o + 5] = b + 3;
            }
        }

        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        var vertexArray = vertices.ToArray();
        fixed (float* v = vertexArray)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertexArray.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);
        uint ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (uint* i = indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), i, BufferUsageARB.StaticDraw);

        uint stride = FloatsPerVertex * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        gl.BindVertexArray(0);

        return new Layer(vao, vbo, ebo, indices.Length);
    }

    /// <summary>The fog of war to darken the ground with (null = none).</summary>
    public World.ShroudTexture? Shroud { get; set; }

    public void Draw(Matrix4x4 viewProjection, SceneLighting lighting)
    {
        shader.Use();
        shader.Set("uShroud", 2);
        shader.Set("uShroudScale", Shroud != null ? new Vector2(1 / Shroud.WorldWidth, 1 / Shroud.WorldHeight) : Vector2.Zero);
        Shroud?.Bind(2);
        shader.Set("uViewProjection", viewProjection);
        shader.Set("uAtlas", 0);
        shader.Set("uAmbientLight", lighting.Ambient);
        for (int i = 0; i < 3; i++)
        {
            shader.Set($"uLightDirection[{i}]", Vector3.Normalize(lighting.Directions[i]));
            shader.Set($"uLightColor[{i}]", lighting.Colors[i]);
        }
        atlasTexture.Bind(0);

        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.Disable(EnableCap.CullFace);
        gl.DepthMask(true);

        var frustum = new Frustum(viewProjection);
        var visible = chunks.Where(c => frustum.Intersects(c.Min, c.Max)).ToList();

        gl.Disable(EnableCap.Blend);
        foreach (var chunk in visible)
            DrawLayer(chunk.Layers[0]);

        // Blend layers sit exactly on the base layer: draw with depth test "equal" and alpha blending.
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.DepthMask(false);
        foreach (var chunk in visible)
            for (int i = 1; i < chunk.Layers.Count; i++)
                DrawLayer(chunk.Layers[i]);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
        gl.BindVertexArray(0);
    }

    unsafe void DrawLayer(Layer layer)
    {
        gl.BindVertexArray(layer.Vao);
        gl.DrawElements(PrimitiveType.Triangles, (uint)layer.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    public void Dispose()
    {
        foreach (var layer in chunks.SelectMany(c => c.Layers))
        {
            gl.DeleteBuffer(layer.Vbo);
            gl.DeleteBuffer(layer.Ebo);
            gl.DeleteVertexArray(layer.Vao);
        }
        atlasTexture.Dispose();
        shader.Dispose();
    }
}

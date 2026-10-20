using System.Numerics;
using OpenGZH.Formats.W3d;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.Graphics;

/// <summary>
/// GPU-ready version of a W3D model: its skeleton plus one <see cref="RenderMesh"/> per sub-object.
/// Built once per model name and shared by every object that uses it.
/// </summary>
public sealed class RenderModel : IDisposable
{
    public string Name { get; }
    public W3dHierarchy? Hierarchy { get; }
    public List<RenderMesh> Meshes { get; } = new();

    /// <summary>Approximate bounds of the rest pose (for camera framing and selection).</summary>
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }

    RenderModel(string name, W3dHierarchy? hierarchy)
    {
        Name = name;
        Hierarchy = hierarchy;
    }

    /// <summary>Builds a model from Art/W3D/&lt;name&gt;.w3d. Returns null if the file is missing.</summary>
    public static RenderModel? Build(GL gl, W3dLibrary library, string name)
    {
        var file = library.GetFile(name);
        if (file == null)
            return null;

        // A model is either an HLOD (meshes attached to bones) or a bare mesh.
        var hierarchy = file.Hierarchy;
        var model = new RenderModel(name, null);
        if (file.HLod != null)
        {
            hierarchy ??= library.GetHierarchy(file.HLod.HierarchyName);
            model = new RenderModel(name, hierarchy);
            foreach (var sub in file.HLod.HighestLod.Concat(file.HLod.Aggregates))
            {
                var mesh = library.GetMesh(sub.Name, file);
                if (mesh != null && mesh.Positions.Length > 0)
                    model.Meshes.Add(new RenderMesh(gl, mesh, sub.BoneIndex));
            }
        }
        else
        {
            foreach (var mesh in file.Meshes.Where(m => m.Positions.Length > 0))
                model.Meshes.Add(new RenderMesh(gl, mesh, 0));
        }

        model.ComputeBounds();
        return model;
    }

    void ComputeBounds()
    {
        var pose = new W3dPose(Hierarchy?.Pivots.Count ?? 1);
        pose.Update(Hierarchy, Matrix4x4.Identity);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in Meshes)
        {
            if (mesh.Source.IsHidden)
                continue;
            for (int i = 0; i < mesh.Source.Positions.Length; i++)
            {
                int bone = mesh.Source.IsSkin && i < mesh.Source.BoneInfluences.Length ? mesh.Source.BoneInfluences[i] : mesh.BoneIndex;
                var p = Vector3.Transform(mesh.Source.Positions[i], pose.Transforms[Math.Min(bone, pose.Transforms.Length - 1)]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        BoundsMin = min.X <= max.X ? min : Vector3.Zero;
        BoundsMax = min.X <= max.X ? max : Vector3.Zero;
    }

    public void Dispose()
    {
        foreach (var mesh in Meshes)
            mesh.Dispose();
    }
}

/// <summary>One drawable piece of a batch: a range of indices sharing texture, shader and material.</summary>
public sealed record DrawBatch(
    int IndexStart,
    int IndexCount,
    string? Texture,
    string? DetailTexture,
    W3dShader Shader,
    W3dVertexMaterial? Material);

/// <summary>One material pass of a mesh, with its own vertex buffer (each pass has its own UVs and colors).</summary>
public sealed class RenderPass : IDisposable
{
    readonly GL gl;
    public uint Vao { get; }
    readonly uint vbo, ebo;
    public List<DrawBatch> Batches { get; } = new();

    // Interleaved vertex: position(3) normal(3) uv0(2) uv1(2) color(4) bone(1)
    public const int FloatsPerVertex = 15;

    public unsafe RenderPass(GL gl, float[] vertices, uint[] indices)
    {
        this.gl = gl;
        Vao = gl.GenVertexArray();
        gl.BindVertexArray(Vao);

        vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* v = vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);

        ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (uint* i = indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), i, BufferUsageARB.StaticDraw);

        uint stride = FloatsPerVertex * sizeof(float);
        void Attribute(uint index, int size, int offset)
        {
            gl.EnableVertexAttribArray(index);
            gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, stride, (void*)(offset * sizeof(float)));
        }
        Attribute(0, 3, 0);   // position
        Attribute(1, 3, 3);   // normal
        Attribute(2, 2, 6);   // uv0
        Attribute(3, 2, 8);   // uv1
        Attribute(4, 4, 10);  // color
        Attribute(5, 1, 14);  // bone index

        gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        gl.DeleteBuffer(vbo);
        gl.DeleteBuffer(ebo);
        gl.DeleteVertexArray(Vao);
    }
}

/// <summary>A W3D mesh uploaded to the GPU.</summary>
public sealed class RenderMesh : IDisposable
{
    public W3dMesh Source { get; }
    public string Name => Source.Name;

    /// <summary>Bone this mesh is attached to (rigid meshes). Skins use per-vertex bones instead.</summary>
    public int BoneIndex { get; }

    /// <summary>Meshes named "HOUSECOLOR..." take the owning player's color.</summary>
    public bool IsHouseColor { get; }

    public List<RenderPass> Passes { get; } = new();

    public RenderMesh(GL gl, W3dMesh mesh, int boneIndex)
    {
        Source = mesh;
        BoneIndex = boneIndex;
        IsHouseColor = mesh.Name.StartsWith("HOUSECOLOR", StringComparison.OrdinalIgnoreCase);

        foreach (var pass in mesh.Passes)
            Passes.Add(BuildPass(gl, mesh, pass, boneIndex));

        // Meshes without material passes still get drawn untextured so nothing silently disappears.
        if (mesh.Passes.Count == 0)
            Passes.Add(BuildPass(gl, mesh, new W3dMaterialPass(), boneIndex));
    }

    static RenderPass BuildPass(GL gl, W3dMesh mesh, W3dMaterialPass pass, int boneIndex)
    {
        int vertexCount = mesh.Positions.Length;
        var vertices = new float[vertexCount * RenderPass.FloatsPerVertex];
        var stage0 = pass.Stages.ElementAtOrDefault(0);
        var stage1 = pass.Stages.ElementAtOrDefault(1);

        for (int v = 0; v < vertexCount; v++)
        {
            int o = v * RenderPass.FloatsPerVertex;
            var p = mesh.Positions[v];
            var n = v < mesh.Normals.Length ? mesh.Normals[v] : Vector3.UnitZ;
            var uv0 = stage0 != null && v < stage0.TexCoords.Length ? stage0.TexCoords[v] : Vector2.Zero;
            var uv1 = stage1 != null && v < stage1.TexCoords.Length ? stage1.TexCoords[v] : uv0;
            var color = v < pass.DiffuseColors.Length ? pass.DiffuseColors[v] : Vector4.One;
            float bone = mesh.IsSkin && v < mesh.BoneInfluences.Length ? mesh.BoneInfluences[v] : -1;

            vertices[o] = p.X; vertices[o + 1] = p.Y; vertices[o + 2] = p.Z;
            vertices[o + 3] = n.X; vertices[o + 4] = n.Y; vertices[o + 5] = n.Z;
            // Images are uploaded top row first, so W3D's (Direct3D) UVs can be used as they are.
            vertices[o + 6] = uv0.X; vertices[o + 7] = uv0.Y;
            vertices[o + 8] = uv1.X; vertices[o + 9] = uv1.Y;
            vertices[o + 10] = color.X; vertices[o + 11] = color.Y; vertices[o + 12] = color.Z; vertices[o + 13] = color.W;
            vertices[o + 14] = bone;
        }

        // Group triangles by (texture, detail texture, shader, material) so each group is one draw call.
        int triangleCount = mesh.TriangleCount;
        var groups = new Dictionary<(int Tex, int Detail, int Shader, int Material), List<int>>();
        var order = new List<(int Tex, int Detail, int Shader, int Material)>();
        for (int t = 0; t < triangleCount; t++)
        {
            int tex = PerTriangle(stage0?.TextureIds, t);
            int detail = PerTriangle(stage1?.TextureIds, t);
            int shader = PerTriangle(pass.ShaderIds, t);
            int material = PerVertex(pass.VertexMaterialIds, mesh.Indices[t * 3]);
            var key = (tex, detail, shader, material);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = new List<int>();
                order.Add(key);
            }
            list.Add(t);
        }

        var indices = new uint[triangleCount * 3];
        var batches = new List<DrawBatch>();
        int cursor = 0;
        foreach (var key in order)
        {
            int start = cursor;
            foreach (int t in groups[key])
            {
                indices[cursor++] = (uint)mesh.Indices[t * 3];
                indices[cursor++] = (uint)mesh.Indices[t * 3 + 1];
                indices[cursor++] = (uint)mesh.Indices[t * 3 + 2];
            }
            batches.Add(new DrawBatch(
                start,
                cursor - start,
                key.Tex >= 0 && key.Tex < mesh.Textures.Count ? mesh.Textures[key.Tex].Name : null,
                key.Detail >= 0 && key.Detail < mesh.Textures.Count ? mesh.Textures[key.Detail].Name : null,
                key.Shader >= 0 && key.Shader < mesh.Shaders.Count ? mesh.Shaders[key.Shader] : DefaultShader,
                key.Material >= 0 && key.Material < mesh.VertexMaterials.Count ? mesh.VertexMaterials[key.Material] : null));
        }

        var renderPass = new RenderPass(gl, vertices, indices);
        renderPass.Batches.AddRange(batches);
        return renderPass;
    }

    /// <summary>The original default shader: opaque, depth test/write on, modulate lighting, textured.</summary>
    static readonly W3dShader DefaultShader = new()
    {
        DepthCompare = 3, DepthMask = 1, SrcBlend = W3dShader.SrcOne, DestBlend = W3dShader.DstZero,
        PrimaryGradient = 1, Texturing = 1,
    };

    /// <summary>Id arrays hold either one value for everything or one value per element.</summary>
    static int PerTriangle(uint[]? ids, int triangle)
    {
        if (ids == null || ids.Length == 0) return -1;
        return (int)(ids.Length == 1 ? ids[0] : ids[Math.Min(triangle, ids.Length - 1)]);
    }

    static int PerVertex(uint[]? ids, int vertex) => PerTriangle(ids, vertex);

    public void Dispose()
    {
        foreach (var pass in Passes)
            pass.Dispose();
    }
}

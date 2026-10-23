using System.Numerics;
using OpenGZH.Formats.W3d;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.Graphics;

/// <summary>Scene lighting, as defined by a map's global lighting: ambient plus up to three directional lights.</summary>
public sealed class SceneLighting
{
    public Vector3 Ambient = new(0.35f, 0.35f, 0.35f);
    public Vector3[] Directions = [Vector3.Normalize(new Vector3(-0.8f, 0.4f, -0.8f)), Vector3.UnitZ, Vector3.UnitZ];
    public Vector3[] Colors = [new(0.9f, 0.9f, 0.85f), Vector3.Zero, Vector3.Zero];
}

/// <summary>A placed, posed and possibly animated copy of a <see cref="RenderModel"/>.</summary>
public sealed class ModelInstance
{
    public RenderModel Model { get; }
    public W3dPose Pose { get; }
    public Matrix4x4 World = Matrix4x4.Identity;

    /// <summary>Player color as 0xRRGGBB, applied to HOUSECOLOR meshes and ZHC textures. 0 = none.</summary>
    public uint HouseColor;

    public W3dAnimation? Animation;
    public float Frame;
    public bool Loop = true;

    /// <summary>Sub-objects forced hidden or shown (model condition states can toggle these).</summary>
    public readonly HashSet<string> HiddenSubObjects = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> ShownSubObjects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A color added to every light this model is lit by (the drawable's tint and selection flash).</summary>
    public Vector3 Tint;

    /// <summary>Alpha for the whole model (stealth see-through); below 1 it is drawn with the translucent things.</summary>
    public float Opacity = 1;

    /// <summary>Strength of the orange heat-vision pass on top (detected stealth units); 0 = none.</summary>
    public float HeatVision;

    /// <summary>Draw only the heat-vision pass (a detected enemy).</summary>
    public bool HeatVisionOnly;

    /// <summary>Per-mesh UV offsets added to linear-offset texture mappers (tank treads).</summary>
    public Dictionary<RenderMesh, Vector2>? MeshUvOffsets;

    public ModelInstance(RenderModel model)
    {
        Model = model;
        Pose = new W3dPose(model.Hierarchy?.Pivots.Count ?? 1);
        UpdatePose();
    }

    /// <summary>Advances the animation by <paramref name="seconds"/> and recomputes the pose.</summary>
    public void Advance(float seconds)
    {
        if (Animation != null && Animation.FrameCount > 1)
        {
            Frame += seconds * Animation.FrameRate;
            float last = Animation.FrameCount - 1;
            Frame = Loop ? Frame % last : Math.Min(Frame, last);
        }
        UpdatePose();
    }

    public void UpdatePose() => Pose.Update(Model.Hierarchy, World, Animation, Frame);

    public bool IsMeshVisible(RenderMesh mesh)
    {
        if (HiddenSubObjects.Contains(mesh.Name))
            return false;
        if (mesh.Source.IsHidden && !ShownSubObjects.Contains(mesh.Name))
            return false;
        int bone = Math.Min(mesh.BoneIndex, Pose.Visible.Length - 1);
        return mesh.Source.IsSkin || Pose.Visible[bone];
    }
}

/// <summary>Draws <see cref="ModelInstance"/>s, emulating the original fixed-function W3D shading.</summary>
public sealed class ModelRenderer : IDisposable
{
    public const int MaxBones = 128;

    const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv0;
        layout(location = 3) in vec2 aUv1;
        layout(location = 4) in vec4 aColor;
        layout(location = 5) in float aBone;

        uniform mat4 uViewProjection;
        uniform mat4 uMeshTransform;     // rigid meshes: the bone they hang from
        uniform mat4 uBones[128];        // skinned meshes: every bone of the skeleton
        uniform int uSkinned;
        uniform mat4 uView;
        // Texture mappers for the two stages: the input (0 mesh UVs, 1 camera-space normal,
        // 2 camera-space reflection) and the two rows of the texture matrix.
        uniform int uUvSource[2];
        uniform vec4 uTexRow0[2];
        uniform vec4 uTexRow1[2];

        out vec3 vNormal;
        out vec2 vUv0;
        out vec2 vUv1;
        out vec4 vColor;
        out vec2 vWorld;

        void main()
        {
            mat4 transform = uSkinned == 1 ? uBones[int(aBone + 0.5)] : uMeshTransform;
            vec4 world = transform * vec4(aPosition, 1.0);
            vWorld = world.xy;
            vNormal = normalize(mat3(transform) * aNormal);
            vec3 viewPos = (uView * world).xyz;
            vec3 viewNormal = normalize(mat3(uView) * vNormal);
            vec3 reflection = reflect(normalize(viewPos), viewNormal);
            vec2 uvs[2] = vec2[2](aUv0, aUv1);
            vec2 mapped[2];
            for (int i = 0; i < 2; i++)
            {
                vec4 source = uUvSource[i] == 1 ? vec4(viewNormal, 1.0)
                            : uUvSource[i] == 2 ? vec4(reflection, 1.0)
                            : vec4(uvs[i], 1.0, 1.0);
                mapped[i] = vec2(dot(uTexRow0[i], source), dot(uTexRow1[i], source));
            }
            vUv0 = mapped[0];
            vUv1 = mapped[1];
            vColor = aColor;
            gl_Position = uViewProjection * world;
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec3 vNormal;
        in vec2 vUv0;
        in vec2 vUv1;
        in vec4 vColor;
        in vec2 vWorld;

        uniform sampler2D uTexture;
        uniform sampler2D uShroud;
        uniform vec2 uShroudScale;
        uniform sampler2D uDetail;
        uniform int uTextured;
        uniform int uDetailFunc;       // original DetailColorFunc (0 = none)
        uniform int uGradient;         // original PriGradient: 0 decal, 1 modulate, 2 add, 5 modulate2x
        uniform float uAlphaTest;      // 0 = off

        uniform vec3 uAmbientLight;
        uniform vec3 uLightDirection[3];
        uniform vec3 uLightColor[3];
        uniform vec3 uMaterialAmbient;
        uniform vec3 uMaterialDiffuse;
        uniform vec3 uMaterialEmissive;
        uniform float uMaterialOpacity;
        uniform vec3 uTint;
        uniform float uInstanceOpacity;

        out vec4 fragColor;

        void main()
        {
            vec3 normal = normalize(vNormal);
            vec3 diffuse = vec3(0.0);
            for (int i = 0; i < 3; i++)
                diffuse += max(dot(normal, -uLightDirection[i]), 0.0) * (uLightColor[i] + uTint);

            vec3 lighting = (uAmbientLight + uTint) * uMaterialAmbient + diffuse * uMaterialDiffuse * vColor.rgb + uMaterialEmissive;
            vec4 gradient = vec4(clamp(lighting, 0.0, 1.0), uMaterialOpacity * vColor.a);

            vec4 texel = uTextured == 1 ? texture(uTexture, vUv0) : vec4(1.0);
            if (uDetailFunc != 0)
            {
                vec4 detail = texture(uDetail, vUv1);
                if (uDetailFunc == 1) texel.rgb = detail.rgb;
                else if (uDetailFunc == 2) texel.rgb *= detail.rgb;
                else if (uDetailFunc == 4) texel.rgb += detail.rgb;
                else if (uDetailFunc == 11) texel.rgb *= detail.rgb * 2.0;
                else texel.rgb *= detail.rgb;
            }

            vec4 color;
            if (uGradient == 0) color = texel;
            else if (uGradient == 2) color = vec4(texel.rgb + gradient.rgb, gradient.a);
            else if (uGradient == 5) color = vec4(texel.rgb * gradient.rgb * 2.0, texel.a * gradient.a);
            else color = texel * gradient;

            if (uAlphaTest > 0.0 && color.a < uAlphaTest)
                discard;
            color.a *= uInstanceOpacity;
            if (uShroudScale.x > 0.0)
                color.rgb *= texture(uShroud, vWorld * uShroudScale).r;
            fragColor = color;
        }
        """;

    readonly GL gl;
    readonly ShaderProgram shader;
    readonly TextureCache textures;
    readonly Dictionary<W3dVertexMaterial, (TextureMapper? Stage0, TextureMapper? Stage1)> mappers = new(ReferenceEqualityComparer.Instance);
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    float nowMs;

    public ModelRenderer(GL gl, TextureCache textures)
    {
        this.gl = gl;
        this.textures = textures;
        shader = new ShaderProgram(gl, VertexSource, FragmentSource);
    }

    readonly record struct QueuedBatch(ModelInstance Instance, RenderMesh Mesh, RenderPass Pass, DrawBatch Batch, float Depth);

    /// <summary>Draws all instances: opaque geometry first, then translucent geometry back to front.</summary>
    /// <summary>The fog of war to darken models with (null = none).</summary>
    public World.ShroudTexture? Shroud { get; set; }

    public void Draw(IEnumerable<ModelInstance> instances, Matrix4x4 viewProjection, Vector3 cameraPosition, SceneLighting lighting, Matrix4x4? view = null)
    {
        shader.Use();
        nowMs = (float)clock.Elapsed.TotalMilliseconds;
        shader.Set("uView", view ?? Matrix4x4.Identity);
        shader.Set("uShroud", 2);
        shader.Set("uShroudScale", Shroud != null ? new Vector2(1 / Shroud.WorldWidth, 1 / Shroud.WorldHeight) : Vector2.Zero);
        Shroud?.Bind(2);
        shader.Set("uViewProjection", viewProjection);
        shader.Set("uTexture", 0);
        shader.Set("uDetail", 1);
        shader.Set("uAmbientLight", lighting.Ambient);
        for (int i = 0; i < 3; i++)
        {
            shader.Set($"uLightDirection[{i}]", Vector3.Normalize(lighting.Directions[i]));
            shader.Set($"uLightColor[{i}]", lighting.Colors[i]);
        }

        var translucent = new List<QueuedBatch>();

        gl.Enable(EnableCap.DepthTest);
        gl.Enable(EnableCap.CullFace);
        gl.Disable(EnableCap.Blend);
        gl.DepthMask(true);

        var heatVision = new List<ModelInstance>();
        foreach (var instance in instances)
        {
            if (instance.HeatVision > 0)
                heatVision.Add(instance);
            if (instance.HeatVisionOnly)
                continue;
            UploadBones(instance);
            float depth = Vector3.DistanceSquared(instance.World.Translation, cameraPosition);

            foreach (var mesh in instance.Model.Meshes)
            {
                if (!instance.IsMeshVisible(mesh))
                    continue;

                foreach (var pass in mesh.Passes)
                {
                    foreach (var batch in pass.Batches)
                    {
                        if (batch.Shader.IsBlended || instance.Opacity < 1)
                            translucent.Add(new QueuedBatch(instance, mesh, pass, batch, depth));
                        else
                            DrawBatch(instance, mesh, pass, batch);
                    }
                }
            }
        }

        translucent.Sort((a, b) => b.Depth.CompareTo(a.Depth));
        ModelInstance? current = null;
        gl.Enable(EnableCap.Blend);
        foreach (var queued in translucent)
        {
            if (!ReferenceEquals(current, queued.Instance))
            {
                UploadBones(queued.Instance);
                current = queued.Instance;
            }
            DrawBatch(queued.Instance, queued.Mesh, queued.Pass, queued.Batch);
        }

        DrawHeatVision(heatVision);

        gl.Disable(EnableCap.Blend);
        gl.DepthMask(true);
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// The original's heat-vision material pass: the model again, untextured, lit only by an orange
    /// emissive scaled by the fading strength, added on top (exactly over the model, or on its own for
    /// a detected enemy, without writing depth).
    /// </summary>
    void DrawHeatVision(List<ModelInstance> instances)
    {
        if (instances.Count == 0)
            return;
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        gl.DepthMask(false);
        foreach (var instance in instances)
        {
            UploadBones(instance);
            gl.DepthFunc(instance.HeatVisionOnly ? DepthFunction.Lequal : DepthFunction.Equal);
            foreach (var mesh in instance.Model.Meshes)
            {
                if (!instance.IsMeshVisible(mesh))
                    continue;
                bool skinned = mesh.Source.IsSkin;
                shader.Set("uSkinned", skinned ? 1 : 0);
                if (!skinned)
                    shader.Set("uMeshTransform", instance.Pose.Transforms[Math.Min(mesh.BoneIndex, instance.Pose.Transforms.Length - 1)]);
                shader.Set("uMaterialAmbient", Vector3.Zero);
                shader.Set("uMaterialDiffuse", new Vector3(0.02f, 0.01f, 0));
                shader.Set("uMaterialEmissive", new Vector3(0.5f, 0.2f, 0) * instance.HeatVision);
                shader.Set("uMaterialOpacity", 1f);
                shader.Set("uInstanceOpacity", 1f);
                shader.Set("uTint", Vector3.Zero);
                shader.Set("uGradient", 1);
                shader.Set("uAlphaTest", 0f);
                shader.Set("uTextured", 0);
                shader.Set("uDetailFunc", 0);
                SetStage(0, null, default);
                SetStage(1, null, default);
                if (mesh.Source.IsTwoSided)
                    gl.Disable(EnableCap.CullFace);
                else
                    gl.Enable(EnableCap.CullFace);
                foreach (var pass in mesh.Passes.Take(1))
                {
                    gl.BindVertexArray(pass.Vao);
                    foreach (var batch in pass.Batches)
                        unsafe
                        {
                            gl.DrawElements(PrimitiveType.Triangles, (uint)batch.IndexCount, DrawElementsType.UnsignedInt, (void*)(batch.IndexStart * sizeof(uint)));
                        }
                }
            }
        }
        gl.DepthFunc(DepthFunction.Lequal);
        gl.DepthMask(true);
    }

    void UploadBones(ModelInstance instance)
    {
        bool skinned = instance.Model.Meshes.Any(m => m.Source.IsSkin);
        if (!skinned)
            return;
        int count = Math.Min(MaxBones, instance.Pose.Transforms.Length);
        shader.Set("uBones", instance.Pose.Transforms.AsSpan(0, count));
    }

    void DrawBatch(ModelInstance instance, RenderMesh mesh, RenderPass pass, DrawBatch batch)
    {
        var s = batch.Shader;
        bool skinned = mesh.Source.IsSkin;
        shader.Set("uSkinned", skinned ? 1 : 0);
        if (!skinned)
            shader.Set("uMeshTransform", instance.Pose.Transforms[Math.Min(mesh.BoneIndex, instance.Pose.Transforms.Length - 1)]);

        // Material (house color meshes replace ambient and diffuse with the player color).
        var material = batch.Material;
        Vector3 ambient = material?.Ambient ?? Vector3.One;
        Vector3 diffuse = material?.Diffuse ?? Vector3.One;
        if (mesh.IsHouseColor && instance.HouseColor != 0)
            ambient = diffuse = ColorToVector(instance.HouseColor);
        shader.Set("uMaterialAmbient", ambient);
        shader.Set("uMaterialDiffuse", diffuse);
        shader.Set("uMaterialEmissive", material?.Emissive ?? Vector3.Zero);
        shader.Set("uMaterialOpacity", material?.Opacity ?? 1f);
        shader.Set("uGradient", (int)s.PrimaryGradient);
        shader.Set("uAlphaTest", s.AlphaTest != 0 ? 0.376f : 0f); // the original used an alpha reference of 0x60
        SetMappers(instance, mesh, material);
        shader.Set("uTint", instance.Tint);
        shader.Set("uInstanceOpacity", instance.Opacity);

        // Textures.
        Texture? texture = null;
        if (s.Texturing != 0 && batch.Texture != null)
            texture = instance.HouseColor != 0 ? textures.GetHouseColored(batch.Texture, instance.HouseColor) : textures.Get(batch.Texture);
        (texture ?? textures.White).Bind(0);
        shader.Set("uTextured", texture != null ? 1 : 0);

        var detail = batch.DetailTexture != null && s.DetailColorFunc != 0 ? textures.Get(batch.DetailTexture) : null;
        (detail ?? textures.White).Bind(1);
        shader.Set("uDetailFunc", detail != null ? (int)s.DetailColorFunc : 0);

        // Fixed-function state.
        gl.DepthMask(s.DepthMask != 0);
        gl.DepthFunc(s.DepthCompare switch
        {
            0 => DepthFunction.Never, 1 => DepthFunction.Less, 2 => DepthFunction.Equal, 4 => DepthFunction.Greater,
            5 => DepthFunction.Notequal, 6 => DepthFunction.Gequal, 7 => DepthFunction.Always, _ => DepthFunction.Lequal,
        });
        if (s.IsBlended)
            gl.BlendFunc(SourceFactor(s.SrcBlend), DestinationFactor(s.DestBlend));
        else if (instance.Opacity < 1)
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);   // the original's alpha override
        if (mesh.Source.IsTwoSided)
            gl.Disable(EnableCap.CullFace);
        else
            gl.Enable(EnableCap.CullFace);

        gl.BindVertexArray(pass.Vao);
        unsafe
        {
            gl.DrawElements(PrimitiveType.Triangles, (uint)batch.IndexCount, DrawElementsType.UnsignedInt, (void*)(batch.IndexStart * sizeof(uint)));
        }
    }

    static readonly Vector4 IdentityRow0 = new(1, 0, 0, 0), IdentityRow1 = new(0, 1, 0, 0);

    (TextureMapper? Stage0, TextureMapper? Stage1) MappersFor(W3dVertexMaterial material)
    {
        if (!mappers.TryGetValue(material, out var pair))
        {
            pair = (TextureMapper.Create(material.StageMapping, material.MapperArgs),
                    TextureMapper.Create(material.Stage1Mapping, material.MapperArgs1));
            mappers[material] = pair;
        }
        return pair;
    }

    void SetMappers(ModelInstance instance, RenderMesh mesh, W3dVertexMaterial? material)
    {
        var (stage0, stage1) = material != null ? MappersFor(material) : (null, null);
        Vector2 custom = instance.MeshUvOffsets != null && instance.MeshUvOffsets.TryGetValue(mesh, out var o) ? o : default;
        SetStage(0, stage0, custom);
        SetStage(1, stage1, custom);
    }

    void SetStage(int stage, TextureMapper? mapper, Vector2 custom)
    {
        if (mapper == null)
        {
            shader.Set($"uUvSource[{stage}]", 0);
            shader.Set($"uTexRow0[{stage}]", IdentityRow0);
            shader.Set($"uTexRow1[{stage}]", IdentityRow1);
            return;
        }
        var (row0, row1) = mapper.Matrix(nowMs, custom);
        shader.Set($"uUvSource[{stage}]", (int)mapper.Source);
        shader.Set($"uTexRow0[{stage}]", row0);
        shader.Set($"uTexRow1[{stage}]", row1);
    }

    static BlendingFactor SourceFactor(byte value) => value switch
    {
        W3dShader.SrcZero => BlendingFactor.Zero,
        W3dShader.SrcSrcAlpha => BlendingFactor.SrcAlpha,
        W3dShader.SrcOneMinusSrcAlpha => BlendingFactor.OneMinusSrcAlpha,
        _ => BlendingFactor.One,
    };

    static BlendingFactor DestinationFactor(byte value) => value switch
    {
        W3dShader.DstOne => BlendingFactor.One,
        W3dShader.DstSrcColor => BlendingFactor.SrcColor,
        W3dShader.DstOneMinusSrcColor => BlendingFactor.OneMinusSrcColor,
        W3dShader.DstSrcAlpha => BlendingFactor.SrcAlpha,
        W3dShader.DstOneMinusSrcAlpha => BlendingFactor.OneMinusSrcAlpha,
        _ => BlendingFactor.Zero,
    };

    public static Vector3 ColorToVector(uint rgb) => new((rgb >> 16 & 0xFF) / 255f, (rgb >> 8 & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    public void Dispose() => shader.Dispose();
}

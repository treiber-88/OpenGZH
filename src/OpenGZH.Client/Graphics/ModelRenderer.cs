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

        out vec3 vNormal;
        out vec2 vUv0;
        out vec2 vUv1;
        out vec4 vColor;

        void main()
        {
            mat4 transform = uSkinned == 1 ? uBones[int(aBone + 0.5)] : uMeshTransform;
            vec4 world = transform * vec4(aPosition, 1.0);
            vNormal = normalize(mat3(transform) * aNormal);
            vUv0 = aUv0;
            vUv1 = aUv1;
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

        uniform sampler2D uTexture;
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

        out vec4 fragColor;

        void main()
        {
            vec3 normal = normalize(vNormal);
            vec3 diffuse = vec3(0.0);
            for (int i = 0; i < 3; i++)
                diffuse += max(dot(normal, -uLightDirection[i]), 0.0) * uLightColor[i];

            vec3 lighting = uAmbientLight * uMaterialAmbient + diffuse * uMaterialDiffuse * vColor.rgb + uMaterialEmissive;
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
            fragColor = color;
        }
        """;

    readonly GL gl;
    readonly ShaderProgram shader;
    readonly TextureCache textures;

    public ModelRenderer(GL gl, TextureCache textures)
    {
        this.gl = gl;
        this.textures = textures;
        shader = new ShaderProgram(gl, VertexSource, FragmentSource);
    }

    readonly record struct QueuedBatch(ModelInstance Instance, RenderMesh Mesh, RenderPass Pass, DrawBatch Batch, float Depth);

    /// <summary>Draws all instances: opaque geometry first, then translucent geometry back to front.</summary>
    public void Draw(IEnumerable<ModelInstance> instances, Matrix4x4 viewProjection, Vector3 cameraPosition, SceneLighting lighting)
    {
        shader.Use();
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

        foreach (var instance in instances)
        {
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
                        if (batch.Shader.IsBlended)
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

        gl.Disable(EnableCap.Blend);
        gl.DepthMask(true);
        gl.BindVertexArray(0);
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

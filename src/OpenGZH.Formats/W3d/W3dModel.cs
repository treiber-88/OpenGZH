using System.Numerics;

namespace OpenGZH.Formats.W3d;

// Data model for the contents of a .w3d file (Westwood 3D, used by Generals and Zero Hour).
// A file may contain any mix of meshes, one hierarchy (skeleton), animations and an HLOD,
// which ties meshes to the bones of a hierarchy to form a complete model.

/// <summary>Everything read from one .w3d file.</summary>
public sealed class W3dFile
{
    public List<W3dMesh> Meshes { get; } = new();
    public W3dHierarchy? Hierarchy { get; set; }
    public List<W3dAnimation> Animations { get; } = new();
    public W3dHLod? HLod { get; set; }
    public List<W3dBox> Boxes { get; } = new();
}

[Flags]
public enum W3dMeshFlags : uint
{
    None = 0,
    Hidden = 0x00001000,
    TwoSided = 0x00002000,
    CastShadow = 0x00008000,
    GeometryTypeMask = 0x00FF0000,
    CameraAligned = 0x00010000,
    Skin = 0x00020000,
    CameraOriented = 0x00060000,
}

public sealed class W3dMesh
{
    /// <summary>Mesh name, e.g. "TURRET".</summary>
    public string Name { get; set; } = "";

    /// <summary>Container (model) name, e.g. "AVLEOPARD". The full name is "Container.Name".</summary>
    public string ContainerName { get; set; } = "";

    public string FullName => ContainerName.Length > 0 ? $"{ContainerName}.{Name}" : Name;

    public W3dMeshFlags Flags { get; set; }
    public bool IsSkin => (Flags & W3dMeshFlags.GeometryTypeMask) == W3dMeshFlags.Skin;
    public bool IsHidden => (Flags & W3dMeshFlags.Hidden) != 0;
    public bool IsTwoSided => (Flags & W3dMeshFlags.TwoSided) != 0;

    public Vector3[] Positions { get; set; } = [];
    public Vector3[] Normals { get; set; } = [];

    /// <summary>For skins: the bone index each vertex is attached to (vertices are stored in that bone's space).</summary>
    public ushort[] BoneInfluences { get; set; } = [];

    /// <summary>Triangle vertex indices, three per triangle.</summary>
    public int[] Indices { get; set; } = [];

    public List<W3dTexture> Textures { get; } = new();
    public List<W3dShader> Shaders { get; } = new();
    public List<W3dVertexMaterial> VertexMaterials { get; } = new();
    public List<W3dMaterialPass> Passes { get; } = new();

    public Vector3 BoundsMin { get; set; }
    public Vector3 BoundsMax { get; set; }
    public Vector3 SphereCenter { get; set; }
    public float SphereRadius { get; set; }

    public int TriangleCount => Indices.Length / 3;
}

public sealed class W3dTexture
{
    public string Name { get; set; } = "";

    /// <summary>Texture animation: frames ("name0000.tga", ...) and how they play. Zero frames = static.</summary>
    public int FrameCount { get; set; }
    public float FrameRate { get; set; }
    public ushort Attributes { get; set; }
}

/// <summary>Fixed-function shader settings (W3dShaderStruct). Values are the original enums.</summary>
public struct W3dShader
{
    public byte DepthCompare;
    public byte DepthMask;
    public byte DestBlend;
    public byte PrimaryGradient;
    public byte SecondaryGradient;
    public byte SrcBlend;
    public byte Texturing;
    public byte DetailColorFunc;
    public byte DetailAlphaFunc;
    public byte AlphaTest;

    // Source blend values (original SrcBlendFuncType).
    public const byte SrcZero = 0, SrcOne = 1, SrcSrcAlpha = 2, SrcOneMinusSrcAlpha = 3;

    // Destination blend values (original DstBlendFuncType) - note the different order.
    public const byte DstZero = 0, DstOne = 1, DstSrcColor = 2, DstOneMinusSrcColor = 3, DstSrcAlpha = 4, DstOneMinusSrcAlpha = 5;

    public readonly bool IsBlended => !(SrcBlend == SrcOne && DestBlend == DstZero);
    public readonly bool IsAlphaTested => AlphaTest != 0;
}

public sealed class W3dVertexMaterial
{
    public string Name { get; set; } = "";
    public uint Attributes { get; set; }
    public Vector3 Ambient { get; set; }
    public Vector3 Diffuse { get; set; }
    public Vector3 Specular { get; set; }
    public Vector3 Emissive { get; set; }
    public float Shininess { get; set; }
    public float Opacity { get; set; } = 1;
    public float Translucency { get; set; }

    /// <summary>Texture-coordinate mapper (e.g. scrolling UVs for water or tank treads). 0 = none.</summary>
    public int StageMapping { get; set; }
    public string MapperArgs { get; set; } = "";

    /// <summary>The detail (second) texture stage mapper, and its arguments.</summary>
    public int Stage1Mapping { get; set; }
    public string MapperArgs1 { get; set; } = "";
}

/// <summary>
/// One rendering pass. Each id array is either a single value (applies to all) or one per vertex/triangle.
/// </summary>
public sealed class W3dMaterialPass
{
    public uint[] VertexMaterialIds { get; set; } = [];
    public uint[] ShaderIds { get; set; } = [];
    public Vector4[] DiffuseColors { get; set; } = [];

    /// <summary>Texture stages (usually one, sometimes two for detail textures).</summary>
    public List<W3dTextureStage> Stages { get; } = new();
}

public sealed class W3dTextureStage
{
    public uint[] TextureIds { get; set; } = [];
    public Vector2[] TexCoords { get; set; } = [];
}

/// <summary>A skeleton. Pivot 0 is always the root.</summary>
public sealed class W3dHierarchy
{
    public string Name { get; set; } = "";
    public Vector3 Center { get; set; }
    public List<W3dPivot> Pivots { get; } = new();

    public int FindPivot(string name) =>
        Pivots.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed class W3dPivot
{
    public string Name { get; set; } = "";
    public int ParentIndex { get; set; } = -1;
    public Vector3 Translation { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    /// <summary>Base (rest pose) transform relative to the parent: translate, then rotate.</summary>
    public Matrix4x4 BaseTransform => Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
}

/// <summary>An HLOD: the model definition that attaches meshes (sub-objects) to bones.</summary>
public sealed class W3dHLod
{
    public string Name { get; set; } = "";
    public string HierarchyName { get; set; } = "";

    /// <summary>Levels of detail, highest detail LAST (as stored in the file).</summary>
    public List<List<W3dSubObject>> Lods { get; } = new();

    /// <summary>Always-drawn objects (aggregates).</summary>
    public List<W3dSubObject> Aggregates { get; } = new();

    public List<W3dSubObject> HighestLod => Lods.Count > 0 ? Lods[^1] : new();
}

public sealed record W3dSubObject(int BoneIndex, string Name);

public sealed class W3dBox
{
    public string Name { get; set; } = "";
    public Vector3 Center { get; set; }
    public Vector3 Extent { get; set; }
}

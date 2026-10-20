using System.Numerics;
using System.Text;

namespace OpenGZH.Formats.W3d;

/// <summary>
/// Reads .w3d files. The format is a tree of chunks:
///   uint32 type, uint32 size (top bit set = the chunk contains sub-chunks), then 'size' bytes.
/// Unknown chunks are skipped, so files with extra data (emitters, AABTrees, ...) still load.
/// </summary>
public static class W3dReader
{
    // Top-level chunk types.
    const uint ChunkMesh = 0x000;
    const uint ChunkHierarchy = 0x100;
    const uint ChunkAnimation = 0x200;
    const uint ChunkCompressedAnimation = 0x280;
    const uint ChunkHLod = 0x700;
    const uint ChunkBox = 0x740;

    // Mesh sub-chunks.
    const uint Vertices = 0x02, VertexNormals = 0x03, VertexInfluences = 0x0E, MeshHeader3 = 0x1F, Triangles = 0x20;
    const uint MaterialInfo = 0x28, Shaders = 0x29, VertexMaterials = 0x2A, VertexMaterial = 0x2B;
    const uint VertexMaterialName = 0x2C, VertexMaterialInfo = 0x2D, VertexMapperArgs0 = 0x2E;
    const uint Textures = 0x30, Texture = 0x31, TextureName = 0x32, TextureInfo = 0x33;
    const uint MaterialPass = 0x38, VertexMaterialIds = 0x39, ShaderIds = 0x3A, DiffuseColors = 0x3B;
    const uint TextureStage = 0x48, TextureIds = 0x49, StageTexCoords = 0x4A;
    const uint PrelitUnlit = 0x23, PrelitVertex = 0x24, PrelitLightmapMultiPass = 0x25, PrelitLightmapMultiTexture = 0x26;

    // Hierarchy / animation / HLOD sub-chunks.
    const uint HierarchyHeader = 0x101, Pivots = 0x102;
    const uint AnimationHeader = 0x201, AnimationChannel = 0x202, BitChannel = 0x203;
    const uint CompressedAnimationHeader = 0x281, CompressedAnimationChannel = 0x282, CompressedBitChannel = 0x283;
    const uint HLodHeader = 0x701, HLodLodArray = 0x702, HLodSubObjectArrayHeader = 0x703, HLodSubObject = 0x704, HLodAggregateArray = 0x705;

    const uint TimeCodedBinaryMovementFlag = 0x80000000;

    public static W3dFile Read(byte[] data)
    {
        var file = new W3dFile();
        var reader = new BinaryReader(new MemoryStream(data, writable: false));

        foreach (var chunk in Chunks(reader, data.Length))
        {
            switch (chunk.Type)
            {
                case ChunkMesh: file.Meshes.Add(ReadMesh(reader, chunk)); break;
                case ChunkHierarchy: file.Hierarchy = ReadHierarchy(reader, chunk); break;
                case ChunkAnimation: file.Animations.Add(ReadAnimation(reader, chunk)); break;
                case ChunkCompressedAnimation: file.Animations.Add(ReadCompressedAnimation(reader, chunk)); break;
                case ChunkHLod: file.HLod = ReadHLod(reader, chunk); break;
                case ChunkBox: file.Boxes.Add(ReadBox(reader)); break;
            }
        }

        return file;
    }

    readonly record struct Chunk(uint Type, int Size, long Start)
    {
        public long End => Start + Size;
    }

    /// <summary>Enumerates the chunks between the reader's position and <paramref name="end"/>, leaving the reader at each chunk's data.</summary>
    static IEnumerable<Chunk> Chunks(BinaryReader reader, long end)
    {
        while (reader.BaseStream.Position + 8 <= end)
        {
            uint type = reader.ReadUInt32();
            int size = (int)(reader.ReadUInt32() & 0x7FFFFFFF);
            var chunk = new Chunk(type, size, reader.BaseStream.Position);
            if (chunk.End > end)
                yield break;

            yield return chunk;
            reader.BaseStream.Position = chunk.End;
        }
    }

    static IEnumerable<Chunk> SubChunks(BinaryReader reader, Chunk parent) => Chunks(reader, parent.End);

    // ---- Meshes ----

    static W3dMesh ReadMesh(BinaryReader reader, Chunk meshChunk)
    {
        var mesh = new W3dMesh();
        foreach (var chunk in SubChunks(reader, meshChunk))
        {
            switch (chunk.Type)
            {
                case MeshHeader3: ReadMeshHeader(reader, mesh); break;
                case Vertices: mesh.Positions = ReadVectors(reader, chunk.Size / 12); break;
                case VertexNormals: mesh.Normals = ReadVectors(reader, chunk.Size / 12); break;
                case VertexInfluences:
                    var bones = new ushort[chunk.Size / 8];
                    for (int i = 0; i < bones.Length; i++)
                    {
                        bones[i] = reader.ReadUInt16();
                        reader.ReadBytes(6); // second bone index and padding (unused by Generals)
                    }
                    mesh.BoneInfluences = bones;
                    break;
                case Triangles:
                    int count = chunk.Size / 32;
                    var indices = new int[count * 3];
                    for (int i = 0; i < count; i++)
                    {
                        indices[i * 3] = (int)reader.ReadUInt32();
                        indices[i * 3 + 1] = (int)reader.ReadUInt32();
                        indices[i * 3 + 2] = (int)reader.ReadUInt32();
                        reader.ReadBytes(20); // surface type, face normal, plane distance
                    }
                    mesh.Indices = indices;
                    break;
                case PrelitUnlit:
                case PrelitVertex:
                case PrelitLightmapMultiPass:
                case PrelitLightmapMultiTexture:
                    // Pre-lit variants wrap a full set of materials. Use the first one only if the
                    // mesh has no regular materials (Generals meshes normally do).
                    if (mesh.Passes.Count == 0)
                        ReadMaterials(reader, chunk, mesh);
                    break;
                default:
                    ReadMaterialChunk(reader, chunk, mesh);
                    break;
            }
        }
        return mesh;
    }

    static void ReadMaterials(BinaryReader reader, Chunk wrapper, W3dMesh mesh)
    {
        foreach (var chunk in SubChunks(reader, wrapper))
            ReadMaterialChunk(reader, chunk, mesh);
    }

    static void ReadMaterialChunk(BinaryReader reader, Chunk chunk, W3dMesh mesh)
    {
        switch (chunk.Type)
        {
            case MaterialInfo:
                break; // counts only; the arrays themselves tell us their sizes
            case Shaders:
                for (int i = 0; i < chunk.Size / 16; i++)
                    mesh.Shaders.Add(ReadShader(reader));
                break;
            case VertexMaterials:
                foreach (var vm in SubChunks(reader, chunk))
                    if (vm.Type == VertexMaterial)
                        mesh.VertexMaterials.Add(ReadVertexMaterial(reader, vm));
                break;
            case Textures:
                foreach (var tex in SubChunks(reader, chunk))
                    if (tex.Type == Texture)
                        mesh.Textures.Add(ReadTexture(reader, tex));
                break;
            case MaterialPass:
                mesh.Passes.Add(ReadMaterialPass(reader, chunk));
                break;
        }
    }

    static void ReadMeshHeader(BinaryReader reader, W3dMesh mesh)
    {
        reader.ReadUInt32(); // version
        mesh.Flags = (W3dMeshFlags)reader.ReadUInt32();
        mesh.Name = ReadFixedString(reader, 16);
        mesh.ContainerName = ReadFixedString(reader, 16);
        reader.ReadUInt32(); // triangle count
        reader.ReadUInt32(); // vertex count
        reader.ReadUInt32(); // material count
        reader.ReadUInt32(); // damage stages
        reader.ReadInt32();  // sort level
        reader.ReadUInt32(); // prelit version
        reader.ReadUInt32(); // future counts
        reader.ReadUInt32(); // vertex channels
        reader.ReadUInt32(); // face channels
        mesh.BoundsMin = ReadVector(reader);
        mesh.BoundsMax = ReadVector(reader);
        mesh.SphereCenter = ReadVector(reader);
        mesh.SphereRadius = reader.ReadSingle();
    }

    static W3dShader ReadShader(BinaryReader reader)
    {
        byte[] b = reader.ReadBytes(16);
        return new W3dShader
        {
            DepthCompare = b[0],
            DepthMask = b[1],
            DestBlend = b[3],
            PrimaryGradient = b[5],
            SecondaryGradient = b[6],
            SrcBlend = b[7],
            Texturing = b[8],
            DetailColorFunc = b[9],
            DetailAlphaFunc = b[10],
            AlphaTest = b[12],
        };
    }

    static W3dVertexMaterial ReadVertexMaterial(BinaryReader reader, Chunk chunk)
    {
        var material = new W3dVertexMaterial();
        foreach (var sub in SubChunks(reader, chunk))
        {
            switch (sub.Type)
            {
                case VertexMaterialName:
                    material.Name = ReadZeroTerminated(reader, sub.Size);
                    break;
                case VertexMaterialInfo:
                    material.Attributes = reader.ReadUInt32();
                    material.Ambient = ReadRgb(reader);
                    material.Diffuse = ReadRgb(reader);
                    material.Specular = ReadRgb(reader);
                    material.Emissive = ReadRgb(reader);
                    material.Shininess = reader.ReadSingle();
                    material.Opacity = reader.ReadSingle();
                    material.Translucency = reader.ReadSingle();
                    // Stage 0 mapping type lives in bits 16-23 of the attributes.
                    material.StageMapping = (int)((material.Attributes >> 16) & 0xFF);
                    break;
                case VertexMapperArgs0:
                    material.MapperArgs = ReadZeroTerminated(reader, sub.Size);
                    break;
            }
        }
        return material;
    }

    static W3dTexture ReadTexture(BinaryReader reader, Chunk chunk)
    {
        var texture = new W3dTexture();
        foreach (var sub in SubChunks(reader, chunk))
        {
            if (sub.Type == TextureName)
            {
                texture.Name = ReadZeroTerminated(reader, sub.Size);
            }
            else if (sub.Type == TextureInfo)
            {
                texture.Attributes = reader.ReadUInt16();
                reader.ReadUInt16(); // animation type
                texture.FrameCount = (int)reader.ReadUInt32();
                texture.FrameRate = reader.ReadSingle();
            }
        }
        return texture;
    }

    static W3dMaterialPass ReadMaterialPass(BinaryReader reader, Chunk chunk)
    {
        var pass = new W3dMaterialPass();
        foreach (var sub in SubChunks(reader, chunk))
        {
            switch (sub.Type)
            {
                case VertexMaterialIds: pass.VertexMaterialIds = ReadUInts(reader, sub.Size / 4); break;
                case ShaderIds: pass.ShaderIds = ReadUInts(reader, sub.Size / 4); break;
                case DiffuseColors:
                    var colors = new Vector4[sub.Size / 4];
                    for (int i = 0; i < colors.Length; i++)
                    {
                        byte[] c = reader.ReadBytes(4);
                        colors[i] = new Vector4(c[0], c[1], c[2], c[3]) / 255f;
                    }
                    pass.DiffuseColors = colors;
                    break;
                case TextureStage:
                    var stage = new W3dTextureStage();
                    foreach (var stageChunk in SubChunks(reader, sub))
                    {
                        if (stageChunk.Type == TextureIds)
                            stage.TextureIds = ReadUInts(reader, stageChunk.Size / 4);
                        else if (stageChunk.Type == StageTexCoords)
                        {
                            var uvs = new Vector2[stageChunk.Size / 8];
                            for (int i = 0; i < uvs.Length; i++)
                                uvs[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                            stage.TexCoords = uvs;
                        }
                    }
                    pass.Stages.Add(stage);
                    break;
            }
        }
        return pass;
    }

    // ---- Hierarchies ----

    static W3dHierarchy ReadHierarchy(BinaryReader reader, Chunk chunk)
    {
        var hierarchy = new W3dHierarchy();
        foreach (var sub in SubChunks(reader, chunk))
        {
            if (sub.Type == HierarchyHeader)
            {
                reader.ReadUInt32(); // version
                hierarchy.Name = ReadFixedString(reader, 16);
                reader.ReadUInt32(); // pivot count
                hierarchy.Center = ReadVector(reader);
            }
            else if (sub.Type == Pivots)
            {
                for (int i = 0; i < sub.Size / 60; i++)
                {
                    var pivot = new W3dPivot
                    {
                        Name = ReadFixedString(reader, 16),
                        ParentIndex = reader.ReadInt32(),
                        Translation = ReadVector(reader),
                    };
                    ReadVector(reader); // Euler angles (redundant with the quaternion)
                    pivot.Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    hierarchy.Pivots.Add(pivot);
                }
            }
        }
        return hierarchy;
    }

    // ---- Animations ----

    static W3dAnimation ReadAnimation(BinaryReader reader, Chunk chunk)
    {
        var animation = new W3dAnimation();
        foreach (var sub in SubChunks(reader, chunk))
        {
            switch (sub.Type)
            {
                case AnimationHeader:
                    reader.ReadUInt32(); // version
                    animation.Name = ReadFixedString(reader, 16);
                    animation.HierarchyName = ReadFixedString(reader, 16);
                    animation.FrameCount = (int)reader.ReadUInt32();
                    animation.FrameRate = reader.ReadUInt32();
                    break;

                case AnimationChannel:
                {
                    int first = reader.ReadUInt16();
                    int last = reader.ReadUInt16();
                    int vectorLength = reader.ReadUInt16();
                    var type = (W3dChannelType)reader.ReadUInt16();
                    int pivot = reader.ReadUInt16();
                    reader.ReadUInt16(); // padding
                    int frames = last - first + 1;
                    var values = new float[frames * vectorLength];
                    for (int i = 0; i < values.Length && reader.BaseStream.Position + 4 <= sub.End; i++)
                        values[i] = reader.ReadSingle();
                    animation.AddChannel(pivot, type, new W3dAnimation.RawChannel(first, vectorLength, values));
                    break;
                }

                case BitChannel:
                {
                    int first = reader.ReadUInt16();
                    int last = reader.ReadUInt16();
                    reader.ReadUInt16(); // channel type (always visibility)
                    int pivot = reader.ReadUInt16();
                    bool defaultValue = reader.ReadByte() != 0;
                    int frames = last - first + 1;
                    byte[] bits = reader.ReadBytes((int)Math.Min((frames + 7) / 8, sub.End - reader.BaseStream.Position));
                    var values = new bool[frames];
                    for (int i = 0; i < frames && i / 8 < bits.Length; i++)
                        values[i] = (bits[i / 8] & (1 << (i % 8))) != 0;
                    animation.AddVisibility(pivot, new W3dAnimation.VisibilityChannel(first, defaultValue, values));
                    break;
                }
            }
        }
        return animation;
    }

    static W3dAnimation ReadCompressedAnimation(BinaryReader reader, Chunk chunk)
    {
        var animation = new W3dAnimation();
        int flavor = 0;
        foreach (var sub in SubChunks(reader, chunk))
        {
            switch (sub.Type)
            {
                case CompressedAnimationHeader:
                    reader.ReadUInt32(); // version
                    animation.Name = ReadFixedString(reader, 16);
                    animation.HierarchyName = ReadFixedString(reader, 16);
                    animation.FrameCount = (int)reader.ReadUInt32();
                    animation.FrameRate = reader.ReadUInt16();
                    flavor = reader.ReadUInt16();
                    break;

                case CompressedAnimationChannel:
                    if (flavor == 0)
                        ReadTimeCodedChannel(reader, sub, animation);
                    else
                        ReadAdaptiveDeltaChannel(reader, sub, animation);
                    break;

                case CompressedBitChannel:
                    ReadTimeCodedBitChannel(reader, animation);
                    break;
            }
        }
        return animation;
    }

    static void ReadTimeCodedChannel(BinaryReader reader, Chunk chunk, W3dAnimation animation)
    {
        int keyCount = (int)reader.ReadUInt32();
        int pivot = reader.ReadUInt16();
        int vectorLength = reader.ReadByte();
        var type = (W3dChannelType)reader.ReadByte();

        // Each key: uint32 frame (top bit = step, no interpolation), then vectorLength floats.
        var frames = new int[keyCount];
        var steps = new bool[keyCount];
        var values = new float[keyCount * vectorLength];
        for (int k = 0; k < keyCount && reader.BaseStream.Position + 4 * (1 + vectorLength) <= chunk.End; k++)
        {
            uint timeCode = reader.ReadUInt32();
            frames[k] = (int)(timeCode & ~TimeCodedBinaryMovementFlag);
            steps[k] = (timeCode & TimeCodedBinaryMovementFlag) != 0;
            for (int i = 0; i < vectorLength; i++)
                values[k * vectorLength + i] = reader.ReadSingle();
        }

        if (keyCount > 0)
            animation.AddChannel(pivot, type, new W3dAnimation.TimeCodedChannel(vectorLength, frames, steps, values));
    }

    /// <summary>Filter table used by adaptive-delta animation compression (same values as the original encoder).</summary>
    static readonly float[] AdaptiveDeltaFilter = BuildAdaptiveDeltaFilter();

    static float[] BuildAdaptiveDeltaFilter()
    {
        var table = new float[256];
        for (int i = 0; i < 16; i++)
            table[i] = MathF.Pow(10, i - 8);
        for (int i = 0; i < 240; i++)
            table[16 + i] = 1.0f - MathF.Sin(MathF.PI / 180f * (90.0f * i / 240f));
        return table;
    }

    /// <summary>
    /// Adaptive delta: the first frame is stored as floats, then for every group of 16 frames and every
    /// vector component there is a 9-byte packet: one filter-table index and sixteen signed 4-bit deltas.
    /// The whole channel is decoded to one value per frame on load.
    /// </summary>
    static void ReadAdaptiveDeltaChannel(BinaryReader reader, Chunk chunk, W3dAnimation animation)
    {
        int frameCount = (int)reader.ReadUInt32();
        int pivot = reader.ReadUInt16();
        int vectorLength = reader.ReadByte();
        var type = (W3dChannelType)reader.ReadByte();
        float scale = reader.ReadSingle();

        byte[] data = reader.ReadBytes((int)(chunk.End - reader.BaseStream.Position));
        var values = new float[frameCount * vectorLength];
        const int packetSize = 9;
        int headerBytes = vectorLength * 4;

        for (int component = 0; component < vectorLength; component++)
        {
            float value = BitConverter.ToSingle(data, component * 4);
            values[component] = value;

            for (int frame = 1; frame < frameCount; frame++)
            {
                int group = (frame - 1) / 16;
                int inGroup = (frame - 1) % 16;
                int packet = headerBytes + (group * vectorLength + component) * packetSize;
                if (packet + packetSize > data.Length)
                    break;

                float filter = AdaptiveDeltaFilter[data[packet]] * scale;
                int nibbleByte = data[packet + 1 + inGroup / 2];
                int nibble = (inGroup & 1) != 0 ? nibbleByte >> 4 : nibbleByte & 0xF;
                if ((nibble & 0x8) != 0)
                    nibble -= 16; // sign extend 4 bits

                value += nibble * filter;
                values[frame * vectorLength + component] = value;
            }
        }

        animation.AddChannel(pivot, type, new W3dAnimation.RawChannel(0, vectorLength, values));
    }

    static void ReadTimeCodedBitChannel(BinaryReader reader, W3dAnimation animation)
    {
        int keyCount = (int)reader.ReadUInt32();
        int pivot = reader.ReadUInt16();
        reader.ReadByte(); // channel type
        bool defaultValue = reader.ReadByte() != 0;

        var keyFrames = new int[keyCount];
        var keyValues = new bool[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            uint code = reader.ReadUInt32();
            keyFrames[i] = (int)(code & 0x7FFFFFFF);
            keyValues[i] = (code & 0x80000000) != 0;
        }
        if (keyCount == 0)
            return;

        // Expand to one value per frame for simple lookups.
        int lastFrame = Math.Max(animation.FrameCount, keyFrames[^1] + 1);
        var values = new bool[lastFrame];
        int key = 0;
        for (int frame = 0; frame < lastFrame; frame++)
        {
            while (key + 1 < keyCount && keyFrames[key + 1] <= frame)
                key++;
            values[frame] = frame < keyFrames[0] ? keyValues[0] : keyValues[key];
        }
        animation.AddVisibility(pivot, new W3dAnimation.VisibilityChannel(0, defaultValue, values));
    }

    // ---- HLOD and boxes ----

    static W3dHLod ReadHLod(BinaryReader reader, Chunk chunk)
    {
        var hlod = new W3dHLod();
        foreach (var sub in SubChunks(reader, chunk))
        {
            switch (sub.Type)
            {
                case HLodHeader:
                    reader.ReadUInt32(); // version
                    reader.ReadUInt32(); // LOD count
                    hlod.Name = ReadFixedString(reader, 16);
                    hlod.HierarchyName = ReadFixedString(reader, 16);
                    break;
                case HLodLodArray:
                    hlod.Lods.Add(ReadSubObjects(reader, sub));
                    break;
                case HLodAggregateArray:
                    hlod.Aggregates.AddRange(ReadSubObjects(reader, sub));
                    break;
            }
        }
        return hlod;
    }

    static List<W3dSubObject> ReadSubObjects(BinaryReader reader, Chunk chunk)
    {
        var list = new List<W3dSubObject>();
        foreach (var sub in SubChunks(reader, chunk))
        {
            if (sub.Type == HLodSubObject)
            {
                int bone = (int)reader.ReadUInt32();
                list.Add(new W3dSubObject(bone, ReadFixedString(reader, 32)));
            }
            else if (sub.Type == HLodSubObjectArrayHeader)
            {
                // model count and max screen size; not needed
            }
        }
        return list;
    }

    static W3dBox ReadBox(BinaryReader reader)
    {
        reader.ReadUInt32(); // version
        reader.ReadUInt32(); // attributes
        var box = new W3dBox { Name = ReadFixedString(reader, 32) };
        reader.ReadBytes(4); // color
        box.Center = ReadVector(reader);
        box.Extent = ReadVector(reader);
        return box;
    }

    // ---- Primitive helpers ----

    static Vector3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    static Vector3[] ReadVectors(BinaryReader reader, int count)
    {
        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
            result[i] = ReadVector(reader);
        return result;
    }

    static uint[] ReadUInts(BinaryReader reader, int count)
    {
        var result = new uint[count];
        for (int i = 0; i < count; i++)
            result[i] = reader.ReadUInt32();
        return result;
    }

    /// <summary>Reads an RGB triple stored as 4 bytes (R, G, B, padding).</summary>
    static Vector3 ReadRgb(BinaryReader reader)
    {
        byte[] c = reader.ReadBytes(4);
        return new Vector3(c[0], c[1], c[2]) / 255f;
    }

    static string ReadFixedString(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        int end = Array.IndexOf(bytes, (byte)0);
        return Encoding.Latin1.GetString(bytes, 0, end < 0 ? length : end);
    }

    static string ReadZeroTerminated(BinaryReader reader, int maxLength) => ReadFixedString(reader, maxLength);
}

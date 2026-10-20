using System.Text;

namespace OpenGZH.Formats.Maps;

/// <summary>
/// Reads Westwood's "chunky" data files (.map maps and saved games).
///
/// Layout: "CkMp", a table of contents mapping numeric ids to chunk/key names, then chunks:
///   uint32 nameId, uint16 version, int32 dataSize, data (which may itself contain chunks).
/// </summary>
public sealed class DataChunkReader
{
    readonly Dictionary<uint, string> names = new();

    public BinaryReader Reader { get; }

    public readonly record struct Chunk(string Label, ushort Version, long Start, int Size)
    {
        public long End => Start + Size;
    }

    public DataChunkReader(byte[] data)
    {
        Reader = new BinaryReader(new MemoryStream(data, writable: false), Encoding.Latin1);
        ReadTableOfContents();
    }

    void ReadTableOfContents()
    {
        byte[] magic = Reader.ReadBytes(4);
        if (Encoding.ASCII.GetString(magic) != "CkMp")
            throw new InvalidDataException("Not a chunk file (missing 'CkMp').");

        int count = Reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            int length = Reader.ReadByte();
            string name = Encoding.Latin1.GetString(Reader.ReadBytes(length));
            uint id = Reader.ReadUInt32();
            names[id] = name;
        }
    }

    public string NameOf(uint id) => names.GetValueOrDefault(id, $"#{id}");

    /// <summary>Enumerates chunks from the current position up to <paramref name="end"/>, positioning the reader at each chunk's data.</summary>
    public IEnumerable<Chunk> Chunks(long end)
    {
        while (Reader.BaseStream.Position + 10 <= end)
        {
            uint id = Reader.ReadUInt32();
            ushort version = Reader.ReadUInt16();
            int size = Reader.ReadInt32();
            var chunk = new Chunk(NameOf(id), version, Reader.BaseStream.Position, size);
            yield return chunk;
            Reader.BaseStream.Position = chunk.End;
        }
    }

    public IEnumerable<Chunk> Chunks() => Chunks(Reader.BaseStream.Length);
    public IEnumerable<Chunk> SubChunks(Chunk parent) => Chunks(parent.End);

    // ---- Primitive values used inside chunks ----

    public string ReadAsciiString()
    {
        int length = Reader.ReadUInt16();
        return Encoding.Latin1.GetString(Reader.ReadBytes(length));
    }

    public string ReadUnicodeString()
    {
        int length = Reader.ReadUInt16();
        return Encoding.Unicode.GetString(Reader.ReadBytes(length * 2));
    }

    /// <summary>
    /// Reads a property dictionary: uint16 count, then for each entry an int32 holding
    /// (nameId &lt;&lt; 8 | type) followed by the value.
    /// Types: 0 bool, 1 int, 2 real, 3 ASCII string, 4 Unicode string.
    /// </summary>
    public Dictionary<string, object> ReadDict()
    {
        int count = Reader.ReadUInt16();
        var dict = new Dictionary<string, object>(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            int keyAndType = Reader.ReadInt32();
            int type = keyAndType & 0xFF;
            string key = NameOf((uint)(keyAndType >> 8));
            dict[key] = type switch
            {
                0 => Reader.ReadByte() != 0,
                1 => Reader.ReadInt32(),
                2 => Reader.ReadSingle(),
                3 => ReadAsciiString(),
                4 => ReadUnicodeString(),
                _ => throw new InvalidDataException($"Unknown dictionary value type {type} for '{key}'."),
            };
        }
        return dict;
    }
}

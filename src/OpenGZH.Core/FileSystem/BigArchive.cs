using System.Buffers.Binary;
using System.Text;

namespace OpenGZH.Core.FileSystem;

/// <summary>
/// Reads the ".big" archive format used by Generals and Zero Hour.
///
/// Layout (all header integers are big-endian except the archive size):
///   char[4]  magic        "BIGF" (or "BIG4")
///   uint32   archiveSize  little-endian, total file size
///   uint32   fileCount
///   uint32   headerSize   offset of the first file's data
///   then fileCount entries of:
///     uint32 offset, uint32 size, zero-terminated file name
/// </summary>
public sealed class BigArchive : IPackage
{
    readonly record struct Entry(uint Offset, uint Size);

    readonly FileStream stream;
    readonly Dictionary<string, Entry> entries = new(PathUtil.Comparer);
    readonly List<string> orderedPaths = new();
    readonly object streamLock = new();

    public string Name { get; }

    public BigArchive(string filePath)
    {
        Name = Path.GetFileName(filePath);
        stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ReadIndex();
    }

    void ReadIndex()
    {
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);

        string magic = Encoding.ASCII.GetString(header[..4]);
        if (magic != "BIGF" && magic != "BIG4")
            throw new InvalidDataException($"{Name} is not a .big archive (magic '{magic}').");

        uint fileCount = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);

        using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        var nameBytes = new List<byte>(128);
        for (uint i = 0; i < fileCount; i++)
        {
            uint offset = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
            uint size = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());

            nameBytes.Clear();
            byte b;
            while ((b = reader.ReadByte()) != 0)
                nameBytes.Add(b);

            string path = PathUtil.Normalize(Encoding.Latin1.GetString(nameBytes.ToArray()));

            // Some archives contain placeholder entries such as "Data/*"; skip anything that isn't a real file.
            if (path.Contains('*') || size == 0 && path.EndsWith('/'))
                continue;

            // Inside one archive, the first entry with a given name wins.
            if (entries.TryAdd(path, new Entry(offset, size)))
                orderedPaths.Add(path);
        }
    }

    public IEnumerable<string> FilePaths => orderedPaths;

    public bool Contains(string path) => entries.ContainsKey(PathUtil.Normalize(path));

    public Stream Open(string path)
    {
        if (!entries.TryGetValue(PathUtil.Normalize(path), out var entry))
            throw new FileNotFoundException($"{path} is not in {Name}.");

        // Files are small enough to read fully; this keeps the archive stream thread-safe.
        var data = new byte[entry.Size];
        lock (streamLock)
        {
            stream.Position = entry.Offset;
            stream.ReadExactly(data);
        }
        return new MemoryStream(data, writable: false);
    }

    public void Dispose() => stream.Dispose();
}

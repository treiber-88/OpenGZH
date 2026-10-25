using System.Buffers.Binary;
using System.Text;

namespace OpenGZH.Core.FileSystem;

/// <summary>
/// Reads Westwood's ".mix" archives as used by Tiberian Dawn (MIXFILE.CPP):
///   uint16 count, uint32 dataSize, then count entries {uint32 crc, uint32 offset, uint32 size}
///   sorted by crc, then the data block (offsets are relative to it).
/// The archive stores only a hash of each (upper-case) file name, so names come from a list of
/// known names (see <see cref="NameCrc"/>); entries nobody knows are listed as "XXXXXXXX.unknown".
/// </summary>
public sealed class MixArchive : IPackage
{
    readonly record struct Entry(uint Offset, uint Size);

    readonly FileStream stream;
    readonly long dataStart;
    readonly Dictionary<uint, Entry> byCrc = new();
    readonly Dictionary<string, uint> names = new(PathUtil.Comparer);

    public string Name { get; }

    /// <summary>Entries whose names were not in the known list.</summary>
    public IEnumerable<uint> UnknownCrcs => byCrc.Keys.Where(crc => !names.ContainsValue(crc));

    public int Count => byCrc.Count;

    public MixArchive(string filePath, IEnumerable<string> knownNames)
    {
        Name = Path.GetFileName(filePath);
        stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[6];
        stream.ReadExactly(header);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(header);
        long tableStart = 6;
        if (count == 0)
        {
            // The newer header (Red Alert, later TD releases): uint16 0, uint16 flags, uint16 count, uint32 size.
            int flags = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
            if ((flags & 2) != 0)
                throw new InvalidDataException($"{Name}: encrypted mix files are not supported yet.");
            Span<byte> rest = stackalloc byte[4];
            stream.ReadExactly(rest);
            count = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            tableStart = 10;
        }
        var table = new byte[count * 12];
        stream.ReadExactly(table);
        dataStart = tableStart + table.Length;
        for (int i = 0; i < count; i++)
        {
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 12));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 12 + 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 12 + 8));
            byCrc[crc] = new Entry(offset, size);
        }
        foreach (string known in knownNames)
        {
            uint crc = NameCrc(known);
            if (byCrc.ContainsKey(crc))
                names[PathUtil.Normalize(known)] = crc;
        }
        foreach (uint crc in UnknownCrcs.ToList())
            names[$"{crc:X8}.unknown"] = crc;
    }

    /// <summary>The original's Calculate_CRC of the upper-case name: rotate left one bit and add, four bytes at a time.</summary>
    public static uint NameCrc(string fileName)
    {
        var bytes = Encoding.ASCII.GetBytes(fileName.ToUpperInvariant());
        uint crc = 0;
        int i = 0;
        for (; i + 4 <= bytes.Length; i += 4)
            crc = BitOperations.RotateLeft(crc, 1) + BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
        if (i < bytes.Length)
        {
            Span<byte> rest = stackalloc byte[4];
            rest.Clear();
            bytes.AsSpan(i).CopyTo(rest);
            crc = BitOperations.RotateLeft(crc, 1) + BinaryPrimitives.ReadUInt32LittleEndian(rest);
        }
        return crc;
    }

    public IEnumerable<string> FilePaths => names.Keys;

    public bool Contains(string path) => names.ContainsKey(PathUtil.Normalize(path));

    public Stream Open(string path)
    {
        if (!names.TryGetValue(PathUtil.Normalize(path), out uint crc))
            throw new FileNotFoundException($"{path} is not in {Name}");
        var entry = byCrc[crc];
        var data = new byte[entry.Size];
        lock (stream)
        {
            stream.Position = dataStart + entry.Offset;
            stream.ReadExactly(data);
        }
        return new MemoryStream(data, writable: false);
    }

    public void Dispose() => stream.Dispose();
}

file static class BitOperations
{
    public static uint RotateLeft(uint value, int offset) => System.Numerics.BitOperations.RotateLeft(value, offset);
}

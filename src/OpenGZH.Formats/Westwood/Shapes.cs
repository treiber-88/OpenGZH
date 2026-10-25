using System.Buffers.Binary;

namespace OpenGZH.Formats.Westwood;

/// <summary>
/// A Tiberian Dawn ".shp" (KEYFRAME.CPP's Build_Frame format): every frame is the same size;
/// key frames are LCW-compressed, the others are XOR deltas against a key frame (and the frames
/// between). Pixels are palette indices; 0 is transparent.
/// </summary>
public sealed class KeyframeShape
{
    const byte KeyFrame = 0x80, KeyDelta = 0x40, Delta = 0x20;
    const int HeaderSize = 14;

    readonly byte[] data;
    readonly bool hasPalette;
    readonly byte[]?[] cache;

    public int FrameCount { get; }
    public int Width { get; }
    public int Height { get; }

    public KeyframeShape(byte[] data)
    {
        this.data = data;
        FrameCount = BinaryPrimitives.ReadUInt16LittleEndian(data);
        Width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        Height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        hasPalette = (BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(12)) & 1) != 0;
        cache = new byte[FrameCount][];
        if (FrameCount == 0 || Width == 0 || Height == 0 || HeaderSize + (FrameCount + 2) * 8 > data.Length)
            throw new InvalidDataException("not a keyframe shape");
    }

    uint Entry(int frame, int dword) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(HeaderSize + frame * 8 + dword * 4));

    ReadOnlySpan<byte> At(uint offset)
    {
        int o = (int)(offset & 0x00FFFFFF) + (hasPalette ? 768 : 0);
        return o < data.Length ? data.AsSpan(o) : ReadOnlySpan<byte>.Empty;
    }

    /// <summary>The frame's pixels (Width * Height palette indices, top row first).</summary>
    public byte[] Frame(int frame)
    {
        if (cache[frame] is { } done)
            return done;
        var buffer = new byte[Width * Height];
        uint first = Entry(frame, 0);
        byte flags = (byte)(first >> 24);
        if ((flags & KeyFrame) != 0)
            WestwoodCompression.LcwUncompress(At(first), buffer);
        else
        {
            // A delta frame (KF_DELTA) chains from the key-delta frame it names.
            int start = frame;
            if ((flags & Delta) != 0)
                start = (ushort)Entry(frame, 1);
            uint keyOffset = Entry(start, 1);
            WestwoodCompression.LcwUncompress(At(keyOffset), buffer);
            for (int f = start; f <= frame; f++)
                WestwoodCompression.ApplyXorDelta(buffer, At(Entry(f, 0)));
        }
        cache[frame] = buffer;
        return buffer;
    }
}

/// <summary>A 256-colour palette (.pal): 768 bytes of 6-bit RGB.</summary>
public sealed class WestwoodPalette
{
    public byte[] Rgb { get; } = new byte[768];

    public WestwoodPalette(byte[] data)
    {
        for (int i = 0; i < 768 && i < data.Length; i++)
            Rgb[i] = (byte)((data[i] & 0x3F) * 255 / 63);
    }

    /// <summary>RGBA for a palette index (index 0 is transparent when <paramref name="zeroIsClear"/>).</summary>
    public (byte R, byte G, byte B, byte A) Color(int index, bool zeroIsClear = true) =>
        (Rgb[index * 3], Rgb[index * 3 + 1], Rgb[index * 3 + 2], (byte)(zeroIsClear && index == 0 ? 0 : 255));

    /// <summary>Paints palette-index pixels into an RGBA image.</summary>
    public Images.Image ToImage(byte[] pixels, int width, int height, bool zeroIsClear = true)
    {
        var image = new Images.Image(width, height);
        for (int i = 0; i < width * height; i++)
        {
            var (r, g, b, a) = Color(pixels[i], zeroIsClear);
            image.Pixels[i * 4] = r;
            image.Pixels[i * 4 + 1] = g;
            image.Pixels[i * 4 + 2] = b;
            image.Pixels[i * 4 + 3] = a;
        }
        return image;
    }
}

/// <summary>
/// A theater template (".tem", ".des", ".win": TILE.H's C&C IControl_Type icon set): a block of
/// equal-sized icons (24x24 in TD) and a map from each cell of the template to an icon (0xFF = none).
/// </summary>
public sealed class IconSet
{
    readonly byte[] data;
    readonly int icons, map, transFlags;

    public int IconWidth { get; }
    public int IconHeight { get; }
    public int Count { get; }

    public IconSet(byte[] data)
    {
        this.data = data;
        IconWidth = BinaryPrimitives.ReadInt16LittleEndian(data);
        IconHeight = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(2));
        Count = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(4));
        icons = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        transFlags = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24));
        map = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(28));
        if (IconWidth <= 0 || IconHeight <= 0 || Count <= 0 || icons <= 0 || icons >= data.Length)
            throw new InvalidDataException("not an icon set");
    }

    /// <summary>The icon drawn in a template cell, or -1 for an empty cell.</summary>
    public int IconForCell(int cell)
    {
        if (map <= 0 || map + cell >= data.Length)
            return cell < Count ? cell : -1;
        byte icon = data[map + cell];
        return icon == 0xFF ? -1 : icon;
    }

    /// <summary>Number of template cells the map describes (Count unless the map says otherwise).</summary>
    public int CellCount => Count;

    /// <summary>Palette-index pixels of an icon.</summary>
    public ReadOnlySpan<byte> Icon(int icon) => data.AsSpan(icons + icon * IconWidth * IconHeight, IconWidth * IconHeight);

    /// <summary>Whether an icon has see-through (colour 0) pixels.</summary>
    public bool IsTransparent(int icon) => transFlags > 0 && transFlags + icon < data.Length && data[transFlags + icon] != 0;
}

using System.Buffers.Binary;

namespace OpenGZH.Formats.Images;

/// <summary>
/// A Windows cursor (.cur) or animated cursor (.ani, a RIFF "ACON" file), decoded to RGBA frames.
/// The original game loads its mouse cursors from Data/Cursors/*.ani (Win32Mouse).
/// </summary>
public sealed class AniCursor
{
    /// <summary>The distinct images; <see cref="Steps"/> says which to show and for how long.</summary>
    public List<Image> Frames { get; } = new();

    /// <summary>The animation: frame index and duration in jiffies (1/60 s) per step.</summary>
    public List<(int Frame, int Jiffies)> Steps { get; } = new();

    public int HotspotX { get; private set; }
    public int HotspotY { get; private set; }

    /// <summary>The frame to show at a time (seconds) since the cursor appeared.</summary>
    public Image FrameAt(double seconds)
    {
        if (Steps.Count <= 1)
            return Frames[Steps.Count == 1 ? Steps[0].Frame : 0];
        long total = Steps.Sum(s => Math.Max(1, s.Jiffies));
        long t = (long)(seconds * 60) % total;
        foreach (var (frame, jiffies) in Steps)
        {
            t -= Math.Max(1, jiffies);
            if (t < 0)
                return Frames[frame];
        }
        return Frames[Steps[^1].Frame];
    }

    /// <summary>Index of the step showing at a time (so callers can tell when the image changes).</summary>
    public int StepAt(double seconds)
    {
        if (Steps.Count <= 1)
            return 0;
        long total = Steps.Sum(s => Math.Max(1, s.Jiffies));
        long t = (long)(seconds * 60) % total;
        for (int i = 0; i < Steps.Count; i++)
        {
            t -= Math.Max(1, Steps[i].Jiffies);
            if (t < 0)
                return i;
        }
        return Steps.Count - 1;
    }

    public static AniCursor Decode(byte[] data)
    {
        var cursor = new AniCursor();
        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
            cursor.ReadAni(data);
        else
            cursor.AddIcon(data);
        if (cursor.Frames.Count == 0)
            throw new InvalidDataException("cursor has no images");
        if (cursor.Steps.Count == 0)
            for (int i = 0; i < cursor.Frames.Count; i++)
                cursor.Steps.Add((i, 10));
        return cursor;
    }

    void ReadAni(byte[] data)
    {
        int jifRate = 10, steps = 0;
        int[]? rates = null, sequence = null;
        Walk(data, 12, data.Length, ref jifRate, ref steps, ref rates, ref sequence);
        if (steps <= 0)
            steps = sequence?.Length ?? Frames.Count;
        for (int i = 0; i < steps; i++)
        {
            int frame = sequence != null && i < sequence.Length ? sequence[i] : i;
            if (frame < 0 || frame >= Frames.Count)
                continue;
            Steps.Add((frame, rates != null && i < rates.Length ? rates[i] : jifRate));
        }
    }

    void Walk(byte[] data, int start, int end, ref int jifRate, ref int steps, ref int[]? rates, ref int[]? sequence)
    {
        int i = start;
        while (i + 8 <= end)
        {
            string id = System.Text.Encoding.ASCII.GetString(data, i, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 4));
            int body = i + 8;
            if (body + size > data.Length)
                size = data.Length - body;
            switch (id)
            {
                case "LIST":
                    Walk(data, body + 4, body + size, ref jifRate, ref steps, ref rates, ref sequence);
                    break;
                case "anih" when size >= 36:
                    steps = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + 8));
                    jifRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + 28));
                    break;
                case "rate":
                    rates = ReadInts(data, body, size);
                    break;
                case "seq ":
                    sequence = ReadInts(data, body, size);
                    break;
                case "icon":
                    AddIcon(data.AsSpan(body, size).ToArray());
                    break;
            }
            i = body + size + (size & 1);
        }
    }

    static int[] ReadInts(byte[] data, int at, int size)
    {
        var result = new int[size / 4];
        for (int k = 0; k < result.Length; k++)
            result[k] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + k * 4));
        return result;
    }

    /// <summary>An .ico/.cur image (the first one in the directory).</summary>
    void AddIcon(byte[] ico)
    {
        if (ico.Length < 22)
            return;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        if (count == 0)
            return;
        int hotX = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(10));
        int hotY = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(12));
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(14));
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(18));
        if (offset <= 0 || offset >= ico.Length)
            return;
        size = Math.Min(size, ico.Length - offset);
        if (Frames.Count == 0)
        {
            HotspotX = hotX;
            HotspotY = hotY;
        }
        Frames.Add(DecodeDib(ico.AsSpan(offset, size)));
    }

    /// <summary>A device-independent bitmap with the colour image above an AND (transparency) mask.</summary>
    static Image DecodeDib(ReadOnlySpan<byte> d)
    {
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d);
        int width = BinaryPrimitives.ReadInt32LittleEndian(d[4..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(d[8..]) / 2; // colour + mask
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(d[14..]);
        int colorsUsed = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[32..]);
        int paletteSize = bits <= 8 ? (colorsUsed > 0 ? colorsUsed : 1 << bits) : 0;
        int palette = headerSize;
        int pixels = palette + paletteSize * 4;
        int stride = (width * bits + 31) / 32 * 4;
        int maskStride = (width + 31) / 32 * 4;
        int mask = pixels + stride * height;
        var image = new Image(width, height);
        bool anyAlpha = false;
        for (int y = 0; y < height; y++)
        {
            int row = pixels + (height - 1 - y) * stride; // bottom-up
            for (int x = 0; x < width; x++)
            {
                byte r, g, b, a = 255;
                if (bits <= 8)
                {
                    int bitIndex = x * bits;
                    int value = (d[row + bitIndex / 8] >> (8 - bits - bitIndex % 8)) & ((1 << bits) - 1);
                    int p = palette + value * 4;
                    b = d[p]; g = d[p + 1]; r = d[p + 2];
                }
                else
                {
                    int p = row + x * (bits / 8);
                    b = d[p]; g = d[p + 1]; r = d[p + 2];
                    if (bits == 32)
                    {
                        a = d[p + 3];
                        anyAlpha |= a != 0;
                    }
                }
                image.SetPixel(x, y, r, g, b, a);
            }
        }
        // Without real alpha, the AND mask says which pixels are see-through.
        if (!anyAlpha && mask + maskStride * height <= d.Length)
            for (int y = 0; y < height; y++)
            {
                int row = mask + (height - 1 - y) * maskStride;
                for (int x = 0; x < width; x++)
                    if ((d[row + x / 8] >> (7 - x % 8) & 1) != 0)
                        image.Pixels[(y * width + x) * 4 + 3] = 0;
            }
        return image;
    }
}

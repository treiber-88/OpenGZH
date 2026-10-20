namespace OpenGZH.Formats.Compression;

/// <summary>
/// Decompresses EA's RefPack (a.k.a. QFS) LZ77 format, used by retail .map files.
/// A compressed map starts with "EAR\0", a little-endian uncompressed size, then the RefPack stream.
/// </summary>
public static class RefPack
{
    /// <summary>
    /// Returns the data unchanged if it is not compressed, otherwise the decompressed bytes.
    /// Supports the two schemes the original engine used: "EAR\0" (RefPack) and "ZL1\0".."ZL9\0" (zlib).
    /// </summary>
    public static byte[] DecompressIfNeeded(byte[] data)
    {
        if (data.Length < 8 || data[3] != 0)
            return data;

        if (data[0] == 'E' && data[1] == 'A' && data[2] == 'R')
            return Decompress(data.AsSpan(8));

        if (data[0] == 'Z' && data[1] == 'L' && data[2] >= '1' && data[2] <= '9')
        {
            int size = BitConverter.ToInt32(data, 4);
            var output = new byte[size];
            using var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(data, 8, data.Length - 8), System.IO.Compression.CompressionMode.Decompress);
            zlib.ReadExactly(output);
            return output;
        }

        return data;
    }

    public static byte[] Decompress(ReadOnlySpan<byte> source)
    {
        int position = 0;
        byte flags = source[position++];
        byte magic = source[position++];
        if (magic != 0xFB)
            throw new InvalidDataException("Not a RefPack stream.");

        bool largeSizes = (flags & 0x80) != 0;
        int sizeBytes = largeSizes ? 4 : 3;
        if ((flags & 0x01) != 0)
            position += sizeBytes; // compressed size, not needed

        int outputSize = 0;
        for (int i = 0; i < sizeBytes; i++)
            outputSize = outputSize << 8 | source[position++];

        var output = new byte[outputSize];
        int written = 0;

        while (position < source.Length && written < outputSize)
        {
            int b0 = source[position++];
            int literal, copyLength = 0, copyOffset = 0;

            if (b0 < 0x80)
            {
                // 2-byte command: up to 3 literals, copy 3-10 bytes from up to 1024 back.
                int b1 = source[position++];
                literal = b0 & 0x03;
                copyLength = ((b0 & 0x1C) >> 2) + 3;
                copyOffset = ((b0 & 0x60) << 3) + b1 + 1;
            }
            else if (b0 < 0xC0)
            {
                // 3-byte command: copy 4-67 bytes from up to 16384 back.
                int b1 = source[position++];
                int b2 = source[position++];
                literal = b1 >> 6;
                copyLength = (b0 & 0x3F) + 4;
                copyOffset = ((b1 & 0x3F) << 8) + b2 + 1;
            }
            else if (b0 < 0xE0)
            {
                // 4-byte command: copy 5-1028 bytes from up to 131072 back.
                int b1 = source[position++];
                int b2 = source[position++];
                int b3 = source[position++];
                literal = b0 & 0x03;
                copyLength = ((b0 & 0x0C) << 6) + b3 + 5;
                copyOffset = ((b0 & 0x10) << 12) + (b1 << 8) + b2 + 1;
            }
            else if (b0 < 0xFC)
            {
                // Literal run of 4-112 bytes.
                literal = ((b0 & 0x1F) << 2) + 4;
            }
            else
            {
                // End of stream, with up to 3 final literals.
                literal = b0 & 0x03;
            }

            for (int i = 0; i < literal && written < outputSize; i++)
                output[written++] = source[position++];

            for (int i = 0; i < copyLength && written < outputSize; i++, written++)
                output[written] = output[written - copyOffset];

            if (b0 >= 0xFC)
                break;
        }

        return output;
    }
}

using System.Buffers.Binary;

namespace OpenGZH.Formats.Images;

/// <summary>
/// Decodes DirectDraw Surface (.dds) textures: DXT1, DXT3, DXT5 and uncompressed 16/24/32-bit RGB(A).
/// Only the top mip level is decoded; the renderer generates its own mipmaps.
/// </summary>
public static class DdsDecoder
{
    const uint FourCcDxt1 = 0x31545844; // "DXT1"
    const uint FourCcDxt3 = 0x33545844; // "DXT3"
    const uint FourCcDxt5 = 0x35545844; // "DXT5"
    const uint PixelFormatFourCc = 0x4;

    public static bool IsDds(byte[] data) => data.Length > 128 && data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ';

    public static Image Decode(byte[] data)
    {
        if (!IsDds(data))
            throw new InvalidDataException("Not a DDS file.");

        var header = data.AsSpan(4, 124);
        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);

        // DDS_PIXELFORMAT starts at offset 72 inside the header.
        var pixelFormat = header[72..];
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[4..]);
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[8..]);
        int rgbBits = (int)BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[12..]);
        uint rMask = BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[16..]);
        uint gMask = BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[20..]);
        uint bMask = BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[24..]);
        uint aMask = BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[28..]);

        var body = data.AsSpan(128);
        var image = new Image(width, height);

        if ((flags & PixelFormatFourCc) != 0)
        {
            switch (fourCc)
            {
                case FourCcDxt1: DecodeBlocks(body, image, 8, DecodeDxt1Block); break;
                case FourCcDxt3: DecodeBlocks(body, image, 16, DecodeDxt3Block); break;
                case FourCcDxt5: DecodeBlocks(body, image, 16, DecodeDxt5Block); break;
                default: throw new NotSupportedException($"Unsupported DDS compression 0x{fourCc:X8}.");
            }
        }
        else
        {
            DecodeUncompressed(body, image, rgbBits, rMask, gMask, bMask, aMask);
        }

        return image;
    }

    delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<byte> rgba16);

    static void DecodeBlocks(ReadOnlySpan<byte> body, Image image, int blockSize, BlockDecoder decode)
    {
        int blocksX = Math.Max(1, (image.Width + 3) / 4);
        int blocksY = Math.Max(1, (image.Height + 3) / 4);
        Span<byte> texels = stackalloc byte[16 * 4];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int offset = (by * blocksX + bx) * blockSize;
                if (offset + blockSize > body.Length)
                    return;

                decode(body.Slice(offset, blockSize), texels);

                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= image.Height) break;
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x >= image.Width) break;
                        int t = (py * 4 + px) * 4;
                        image.SetPixel(x, y, texels[t], texels[t + 1], texels[t + 2], texels[t + 3]);
                    }
                }
            }
        }
    }

    static void Rgb565(ushort c, out int r, out int g, out int b)
    {
        r = (c >> 11 & 0x1F) * 255 / 31;
        g = (c >> 5 & 0x3F) * 255 / 63;
        b = (c & 0x1F) * 255 / 31;
    }

    /// <summary>Decodes the 8-byte color part shared by DXT1/3/5. In DXT1 mode, c0 &lt;= c1 enables 1-bit alpha.</summary>
    static void DecodeColorBlock(ReadOnlySpan<byte> block, Span<byte> rgba, bool dxt1Mode)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        Rgb565(c0, out int r0, out int g0, out int b0);
        Rgb565(c1, out int r1, out int g1, out int b1);

        Span<int> palette = stackalloc int[16];
        palette[0] = r0; palette[1] = g0; palette[2] = b0; palette[3] = 255;
        palette[4] = r1; palette[5] = g1; palette[6] = b1; palette[7] = 255;

        if (c0 > c1 || !dxt1Mode)
        {
            palette[8] = (2 * r0 + r1) / 3; palette[9] = (2 * g0 + g1) / 3; palette[10] = (2 * b0 + b1) / 3; palette[11] = 255;
            palette[12] = (r0 + 2 * r1) / 3; palette[13] = (g0 + 2 * g1) / 3; palette[14] = (b0 + 2 * b1) / 3; palette[15] = 255;
        }
        else
        {
            palette[8] = (r0 + r1) / 2; palette[9] = (g0 + g1) / 2; palette[10] = (b0 + b1) / 2; palette[11] = 255;
            palette[12] = 0; palette[13] = 0; palette[14] = 0; palette[15] = 0;
        }

        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        for (int i = 0; i < 16; i++)
        {
            int p = (int)(indices >> (2 * i) & 3) * 4;
            rgba[i * 4] = (byte)palette[p];
            rgba[i * 4 + 1] = (byte)palette[p + 1];
            rgba[i * 4 + 2] = (byte)palette[p + 2];
            rgba[i * 4 + 3] = (byte)palette[p + 3];
        }
    }

    static void DecodeDxt1Block(ReadOnlySpan<byte> block, Span<byte> rgba) => DecodeColorBlock(block, rgba, dxt1Mode: true);

    static void DecodeDxt3Block(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        DecodeColorBlock(block[8..], rgba, dxt1Mode: false);
        ulong alpha = BinaryPrimitives.ReadUInt64LittleEndian(block);
        for (int i = 0; i < 16; i++)
            rgba[i * 4 + 3] = (byte)((alpha >> (4 * i) & 0xF) * 17);
    }

    static void DecodeDxt5Block(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        DecodeColorBlock(block[8..], rgba, dxt1Mode: false);

        int a0 = block[0];
        int a1 = block[1];
        Span<int> alphas = stackalloc int[8];
        alphas[0] = a0;
        alphas[1] = a1;
        if (a0 > a1)
        {
            for (int i = 1; i < 7; i++)
                alphas[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        }
        else
        {
            for (int i = 1; i < 5; i++)
                alphas[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            alphas[6] = 0;
            alphas[7] = 255;
        }

        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (ulong)block[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++)
            rgba[i * 4 + 3] = (byte)alphas[(int)(bits >> (3 * i) & 7)];
    }

    static void DecodeUncompressed(ReadOnlySpan<byte> body, Image image, int bits, uint rMask, uint gMask, uint bMask, uint aMask)
    {
        int bytesPerPixel = bits / 8;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int at = (y * image.Width + x) * bytesPerPixel;
                if (at + bytesPerPixel > body.Length)
                    return;

                uint value = 0;
                for (int i = 0; i < bytesPerPixel; i++)
                    value |= (uint)body[at + i] << (8 * i);

                image.SetPixel(x, y,
                    Channel(value, rMask), Channel(value, gMask), Channel(value, bMask),
                    aMask == 0 ? (byte)255 : Channel(value, aMask));
            }
        }
    }

    static byte Channel(uint value, uint mask)
    {
        if (mask == 0)
            return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        uint raw = (value & mask) >> shift;
        return max == 255 ? (byte)raw : (byte)(raw * 255 / max);
    }
}

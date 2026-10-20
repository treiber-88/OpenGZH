namespace OpenGZH.Formats.Images;

/// <summary>
/// Decodes Truevision TGA images: uncompressed or RLE, 8-bit grayscale, 8-bit color-mapped,
/// 16, 24 and 32 bits per pixel. These cover every TGA in the original game.
/// </summary>
public static class TgaDecoder
{
    public static Image Decode(byte[] data)
    {
        int idLength = data[0];
        int colorMapType = data[1];
        int imageType = data[2];
        int colorMapStart = data[3] | data[4] << 8;
        int colorMapLength = data[5] | data[6] << 8;
        int colorMapBits = data[7];
        int width = data[12] | data[13] << 8;
        int height = data[14] | data[15] << 8;
        int bitsPerPixel = data[16];
        int descriptor = data[17];

        bool rle = imageType >= 9;
        int baseType = rle ? imageType - 8 : imageType;   // 1 = color-mapped, 2 = true color, 3 = grayscale
        bool topToBottom = (descriptor & 0x20) != 0;
        bool rightToLeft = (descriptor & 0x10) != 0;

        int offset = 18 + idLength;

        byte[]? palette = null;
        int paletteBytesPerEntry = (colorMapBits + 7) / 8;
        if (colorMapType == 1)
        {
            palette = new byte[colorMapLength * 4];
            for (int i = 0; i < colorMapLength; i++)
            {
                ReadColor(data, offset + i * paletteBytesPerEntry, colorMapBits, 2, out byte r, out byte g, out byte b, out byte a);
                palette[i * 4] = r; palette[i * 4 + 1] = g; palette[i * 4 + 2] = b; palette[i * 4 + 3] = a;
            }
            offset += colorMapLength * paletteBytesPerEntry;
        }

        int bytesPerPixel = (bitsPerPixel + 7) / 8;
        var image = new Image(width, height);
        int pixelCount = width * height;
        var pixel = new byte[4];

        void ReadOne(int at)
        {
            if (baseType == 1 && palette != null)
            {
                int index = (bytesPerPixel == 1 ? data[at] : data[at] | data[at + 1] << 8) - colorMapStart;
                index = Math.Clamp(index, 0, colorMapLength - 1);
                Array.Copy(palette, index * 4, pixel, 0, 4);
            }
            else
            {
                ReadColor(data, at, bitsPerPixel, baseType, out pixel[0], out pixel[1], out pixel[2], out pixel[3]);
            }
        }

        void Store(int index)
        {
            int x = index % width;
            int y = index / width;
            if (rightToLeft) x = width - 1 - x;
            if (!topToBottom) y = height - 1 - y;
            image.SetPixel(x, y, pixel[0], pixel[1], pixel[2], pixel[3]);
        }

        int written = 0;
        if (!rle)
        {
            for (; written < pixelCount && offset + bytesPerPixel <= data.Length; written++, offset += bytesPerPixel)
            {
                ReadOne(offset);
                Store(written);
            }
        }
        else
        {
            while (written < pixelCount && offset < data.Length)
            {
                int packet = data[offset++];
                int count = (packet & 0x7F) + 1;
                if ((packet & 0x80) != 0)
                {
                    ReadOne(offset);
                    offset += bytesPerPixel;
                    for (int i = 0; i < count && written < pixelCount; i++)
                        Store(written++);
                }
                else
                {
                    for (int i = 0; i < count && written < pixelCount; i++, offset += bytesPerPixel)
                    {
                        ReadOne(offset);
                        Store(written++);
                    }
                }
            }
        }

        return image;
    }

    static void ReadColor(byte[] data, int at, int bits, int baseType, out byte r, out byte g, out byte b, out byte a)
    {
        switch (bits)
        {
            case 8 when baseType == 3:
                r = g = b = data[at];
                a = 255;
                break;
            case 15:
            case 16:
                int value = data[at] | data[at + 1] << 8;
                r = (byte)(((value >> 10) & 0x1F) * 255 / 31);
                g = (byte)(((value >> 5) & 0x1F) * 255 / 31);
                b = (byte)((value & 0x1F) * 255 / 31);
                // Many tools write 16-bit TGAs with the alpha bit cleared, so the bit is ignored.
                a = 255;
                break;
            case 24:
                b = data[at]; g = data[at + 1]; r = data[at + 2]; a = 255;
                break;
            case 32:
                b = data[at]; g = data[at + 1]; r = data[at + 2]; a = data[at + 3];
                break;
            default:
                r = g = b = data[at];
                a = 255;
                break;
        }
    }
}

namespace OpenGZH.Formats.Images;

/// <summary>A decoded image: tightly packed 8-bit RGBA pixels, top row first.</summary>
public sealed class Image
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public Image(int width, int height, byte[]? pixels = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4)
            throw new ArgumentException("Pixel buffer size does not match the image size.");
    }

    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a)
    {
        int i = (y * Width + x) * 4;
        Pixels[i] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
        Pixels[i + 3] = a;
    }

    /// <summary>Decodes a .dds or .tga file based on its extension (falls back to sniffing the header).</summary>
    public static Image Load(byte[] data, string fileName)
    {
        if (fileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || DdsDecoder.IsDds(data))
            return DdsDecoder.Decode(data);
        return TgaDecoder.Decode(data);
    }
}

using OpenGZH.Core.FileSystem;
using OpenGZH.Formats.Images;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.Graphics;

/// <summary>
/// Loads textures by name from the virtual file system, once each.
///
/// Name resolution follows the original game: data files usually ask for "name.tga", but the
/// shipped texture is often "name.dds", so both extensions are tried in several folders.
/// </summary>
public sealed class TextureCache : IDisposable
{
    static readonly string[] SearchFolders = ["Art/Textures/", "Art/Terrain/", "Data/English/Art/Textures/", ""];

    readonly GL gl;
    readonly VirtualFileSystem fileSystem;
    readonly Dictionary<string, Texture?> textures = new(StringComparer.OrdinalIgnoreCase);

    public Texture White { get; }

    public TextureCache(GL gl, VirtualFileSystem fileSystem)
    {
        this.gl = gl;
        this.fileSystem = fileSystem;

        var white = new Image(1, 1);
        white.SetPixel(0, 0, 255, 255, 255, 255);
        White = new Texture(gl, white, mipmaps: false);
    }

    /// <summary>Returns the texture, or null if no file with that name exists.</summary>
    public Texture? Get(string name, bool repeat = true)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (textures.TryGetValue(name, out var cached))
            return cached;

        Texture? texture = null;
        string? path = Resolve(name);
        if (path != null)
        {
            try
            {
                var image = Image.Load(fileSystem.ReadAllBytes(path), path);
                texture = new Texture(gl, image, repeat);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Could not load texture {path}: {e.Message}");
            }
        }

        textures[name] = texture;
        return texture;
    }

    /// <summary>
    /// Returns a texture recolored for a player ("house") color. Only textures whose names start
    /// with "ZHC" are recolored, using the original rules:
    ///   "ZHCD..." the top row holds a 16-entry palette; pixels matching it become a gradient of the color.
    ///   "ZHCA..." alpha marks house color: color = mix(pixel, house, 1 - alpha), then fully opaque.
    /// Other textures are returned unchanged.
    /// </summary>
    public Texture? GetHouseColored(string name, uint rgb)
    {
        if (name.Length < 4 || !name.StartsWith("ZHC", StringComparison.OrdinalIgnoreCase))
            return Get(name);

        string key = $"{name}#{rgb:X6}";
        if (textures.TryGetValue(key, out var cached))
            return cached;

        Texture? texture = null;
        string? path = Resolve(name);
        if (path != null)
        {
            var image = Image.Load(fileSystem.ReadAllBytes(path), path);
            char mode = char.ToUpperInvariant(name[3]);
            if (mode == 'D')
                RecolorPalette(image, rgb);
            else if (mode == 'A')
                RecolorAlpha(image, rgb);
            texture = new Texture(gl, image);
        }

        textures[key] = texture;
        return texture;
    }

    /// <summary>Brightness steps of the 16-entry house color gradient (from the original game).</summary>
    static readonly int[] HouseColorScale = [255, 239, 223, 211, 195, 174, 167, 151, 135, 123, 107, 91, 79, 63, 47, 35];

    static void RecolorPalette(Image image, uint rgb)
    {
        const int paletteSize = 16;
        if (image.Width < paletteSize || image.Height < 2)
            return;

        float r = (rgb >> 16 & 0xFF) / 255f, g = (rgb >> 8 & 0xFF) / 255f, b = (rgb & 0xFF) / 255f;
        var oldPalette = new uint[paletteSize];
        var newPalette = new byte[paletteSize * 3];
        for (int i = 0; i < paletteSize; i++)
        {
            int at = i * 4;
            oldPalette[i] = (uint)(image.Pixels[at] << 16 | image.Pixels[at + 1] << 8 | image.Pixels[at + 2]);
            newPalette[i * 3] = (byte)(HouseColorScale[i] * r);
            newPalette[i * 3 + 1] = (byte)(HouseColorScale[i] * g);
            newPalette[i * 3 + 2] = (byte)(HouseColorScale[i] * b);
        }

        // The palette row itself, then every pixel below it that uses a palette color.
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            int at = i * 4;
            uint pixel = (uint)(image.Pixels[at] << 16 | image.Pixels[at + 1] << 8 | image.Pixels[at + 2]);
            int index = i < paletteSize ? i : Array.IndexOf(oldPalette, pixel);
            if (index < 0)
                continue;
            image.Pixels[at] = newPalette[index * 3];
            image.Pixels[at + 1] = newPalette[index * 3 + 1];
            image.Pixels[at + 2] = newPalette[index * 3 + 2];
        }
    }

    static void RecolorAlpha(Image image, uint rgb)
    {
        float r = (rgb >> 16 & 0xFF) / 255f, g = (rgb >> 8 & 0xFF) / 255f, b = (rgb & 0xFF) / 255f;
        for (int at = 0; at < image.Pixels.Length; at += 4)
        {
            float house = (255 - image.Pixels[at + 3]) / 255f;
            if (house > 0)
            {
                image.Pixels[at] = (byte)(255 * (house * r + (1 - house) * image.Pixels[at] / 255f));
                image.Pixels[at + 1] = (byte)(255 * (house * g + (1 - house) * image.Pixels[at + 1] / 255f));
                image.Pixels[at + 2] = (byte)(255 * (house * b + (1 - house) * image.Pixels[at + 2] / 255f));
            }
            image.Pixels[at + 3] = 255;
        }
    }

    /// <summary>Finds the actual file for a texture name, trying .dds and .tga in each search folder.</summary>
    public string? Resolve(string name)
    {
        string normalized = PathUtil.Normalize(name);
        string stem = normalized.Contains('.') ? normalized[..normalized.LastIndexOf('.')] : normalized;

        foreach (string folder in SearchFolders)
        {
            foreach (string candidate in new[] { folder + stem + ".dds", folder + normalized, folder + stem + ".tga" })
                if (fileSystem.Exists(candidate))
                    return candidate;
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var texture in textures.Values)
            texture?.Dispose();
        textures.Clear();
        White.Dispose();
    }
}

using System.Numerics;
using OpenGZH.Core.FileSystem;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Images;
using OpenGZH.Formats.Maps;

namespace OpenGZH.Client.Terrain;

/// <summary>
/// Packs every terrain texture a map uses into one big image, and answers "which UVs does
/// terrain tile N use" using the original tile rules:
///   - each terrain texture (texture class) is cut into 64x64 pixel tiles, Width x Width of them;
///   - tile numbers count left to right, then bottom to top;
///   - each map cell shows one 32x32 quadrant of a tile (tile index * 4 + quadrant).
/// </summary>
public sealed class TerrainAtlas
{
    public const int TilePixels = 64;
    const int Padding = 8;
    const int AtlasWidth = 4096;

    /// <summary>The original engine's atlas width; cliff UVs in maps are stored in these units.</summary>
    const float OriginalAtlasWidth = 2048f;

    public Image Image { get; }

    readonly BlendTileData tiles;
    readonly Dictionary<TextureClass, (int X, int Y)> classPositions = new();

    public TerrainAtlas(BlendTileData tiles, RuleSet rules, VirtualFileSystem fileSystem)
    {
        this.tiles = tiles;

        // Shelf-pack each texture class square, largest first.
        var classes = tiles.TextureClasses.OrderByDescending(c => c.Width).ToList();
        int x = Padding, y = Padding, shelfHeight = 0;
        foreach (var textureClass in classes)
        {
            int size = textureClass.Width * TilePixels;
            if (x + size + Padding > AtlasWidth)
            {
                x = Padding;
                y += shelfHeight + Padding * 2;
                shelfHeight = 0;
            }
            classPositions[textureClass] = (x, y);
            x += size + Padding * 2;
            shelfHeight = Math.Max(shelfHeight, size);
        }

        int height = NextPowerOfTwo(y + shelfHeight + Padding);
        Image = new Image(AtlasWidth, height);

        foreach (var textureClass in classes)
            CopyClass(textureClass, rules, fileSystem);
    }

    void CopyClass(TextureClass textureClass, RuleSet rules, VirtualFileSystem fileSystem)
    {
        var (destX, destY) = classPositions[textureClass];
        int size = textureClass.Width * TilePixels;

        Image? source = null;
        string? textureName = rules.Get("TerrainTypes", textureClass.Name)?.GetString("Texture");
        if (textureName != null)
        {
            string path = "Art/Terrain/" + textureName;
            if (fileSystem.Exists(path))
                source = Image.Load(fileSystem.ReadAllBytes(path), path);
        }

        // The original reads tiles from the bottom of the image upwards, so use the bottom-left square.
        int sourceTop = source != null ? source.Height - size : 0;

        for (int py = -Padding; py < size + Padding; py++)
        {
            for (int px = -Padding; px < size + Padding; px++)
            {
                // Padding repeats the texture so filtering at tile edges blends with the right neighbour.
                int sx = ((px % size) + size) % size;
                int sy = ((py % size) + size) % size;
                byte r = 255, g = 0, b = 255, a = 255; // magenta = missing texture
                if (source != null && sourceTop + sy >= 0 && sx < source.Width)
                {
                    int i = ((sourceTop + sy) * source.Width + sx) * 4;
                    r = source.Pixels[i]; g = source.Pixels[i + 1]; b = source.Pixels[i + 2]; a = source.Pixels[i + 3];
                }
                int ax = destX + px, ay = destY + py;
                if (ax >= 0 && ay >= 0 && ax < Image.Width && ay < Image.Height)
                    Image.SetPixel(ax, ay, r, g, b, a);
            }
        }
    }

    readonly Dictionary<int, Vector3> averageColors = new();

    /// <summary>
    /// A tile's colour averaged down to one pixel (as the original's radar reads it from the tile's
    /// smallest mip), 0..1 per channel. Black for unknown tiles.
    /// </summary>
    public Vector3 TileAverageColor(int tileIndex)
    {
        int tile = tileIndex >> 2;
        if (averageColors.TryGetValue(tile, out var cached))
            return cached;
        var color = Vector3.Zero;
        var textureClass = tiles.ClassOfTile(tile);
        if (textureClass != null && classPositions.TryGetValue(textureClass, out var origin))
        {
            int local = tile - textureClass.FirstTile;
            int column = local % textureClass.Width;
            int rowFromBottom = local / textureClass.Width;
            int left = origin.X + column * TilePixels;
            int top = origin.Y + (textureClass.Width - 1 - rowFromBottom) * TilePixels;
            double r = 0, g = 0, b = 0;
            int n = 0;
            for (int y = top; y < top + TilePixels && y < Image.Height; y++)
                for (int x = left; x < left + TilePixels && x < Image.Width; x++)
                {
                    int i = (y * Image.Width + x) * 4;
                    r += Image.Pixels[i];
                    g += Image.Pixels[i + 1];
                    b += Image.Pixels[i + 2];
                    n++;
                }
            if (n > 0)
                color = new Vector3((float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255));
        }
        averageColors[tile] = color;
        return color;
    }

    /// <summary>
    /// UVs for the four corners of a cell showing <paramref name="tileIndex"/>, in corner order
    /// (x,y), (x+1,y), (x+1,y+1), (x,y+1). Returns false if the tile's texture is unknown.
    /// </summary>
    public bool TryGetCellUv(int tileIndex, Span<Vector2> corners)
    {
        int tile = tileIndex >> 2;
        var textureClass = tiles.ClassOfTile(tile);
        if (textureClass == null || !classPositions.TryGetValue(textureClass, out var origin))
            return false;

        int local = tile - textureClass.FirstTile;
        int column = local % textureClass.Width;
        int rowFromBottom = local / textureClass.Width;

        // Pixel rectangle of the 64x64 tile, top-down image coordinates.
        float left = origin.X + column * TilePixels;
        float top = origin.Y + (textureClass.Width - 1 - rowFromBottom) * TilePixels;

        // Quadrant: bit 0 = right half, bit 1 = upper half.
        const float half = TilePixels / 2f;
        float minX = left + ((tileIndex & 1) != 0 ? half : 0);
        float minY = top + ((tileIndex & 2) != 0 ? 0 : half);
        float maxX = minX + half, maxY = minY + half;

        corners[0] = ToUv(minX, maxY);
        corners[1] = ToUv(maxX, maxY);
        corners[2] = ToUv(maxX, minY);
        corners[3] = ToUv(minX, minY);
        return true;
    }

    /// <summary>
    /// UVs for a cliff cell. Cliffs store explicit UVs (so textures don't stretch on steep slopes),
    /// relative to the bottom-left of their texture class in the original 2048-wide atlas.
    /// </summary>
    public bool TryGetCliffUv(CliffInfo cliff, int baseTileIndex, Span<Vector2> corners)
    {
        var baseClass = tiles.ClassOfTile(baseTileIndex >> 2);
        var cliffClass = tiles.ClassOfTile(cliff.TileIndex >> 2);
        if (baseClass == null || baseClass != cliffClass || !classPositions.TryGetValue(baseClass, out var origin))
            return false;

        float bottom = origin.Y + baseClass.Width * TilePixels;
        for (int i = 0; i < 4; i++)
        {
            float px = origin.X + cliff.Uv[i].X * OriginalAtlasWidth;
            float py = bottom + cliff.Uv[i].Y * OriginalAtlasWidth;
            corners[i] = ToUv(px, py);
        }
        return true;
    }

    Vector2 ToUv(float pixelX, float pixelY) => new(pixelX / Image.Width, pixelY / Image.Height);

    static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
            result <<= 1;
        return result;
    }
}

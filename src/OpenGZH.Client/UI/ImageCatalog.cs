using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Core.Rules;

namespace OpenGZH.Client.UI;

/// <summary>A named rectangle of a texture page (the original "MappedImage"), e.g. a button or cameo.</summary>
public sealed record MappedImage(string Name, string TextureName, float U0, float V0, float U1, float V1, float PixelWidth, float PixelHeight, bool Rotated);

/// <summary>
/// All interface images: the original mapped images (rules/ui/mapped-images) plus any image that is
/// simply a texture file of that name (so modders and artists can add new cameos as single files).
/// </summary>
public sealed class ImageCatalog
{
    readonly Dictionary<string, MappedImage> images = new(StringComparer.OrdinalIgnoreCase);
    readonly TextureCache textures;

    public ImageCatalog(RuleSet rules, TextureCache textures)
    {
        this.textures = textures;
        foreach (var (name, node) in rules.Section("MappedImages").Entries)
        {
            float textureWidth = RuleValue.Float(node.GetString("TextureWidth"), 512);
            float textureHeight = RuleValue.Float(node.GetString("TextureHeight"), 512);
            var coords = ParseCoords(node.GetString("Coords"));
            bool rotated = node.GetString("Status").Contains("ROTATED_90_CLOCKWISE", StringComparison.OrdinalIgnoreCase);
            images[name] = new MappedImage(name, node.GetString("Texture"),
                coords.Left / textureWidth, coords.Top / textureHeight, coords.Right / textureWidth, coords.Bottom / textureHeight,
                coords.Right - coords.Left, coords.Bottom - coords.Top, rotated);
        }
    }

    static (float Left, float Top, float Right, float Bottom) ParseCoords(string text)
    {
        float left = 0, top = 0, right = 0, bottom = 0;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"(Left|Top|Right|Bottom)\s*:\s*(-?\d+)"))
        {
            float value = float.Parse(match.Groups[2].Value);
            switch (match.Groups[1].Value)
            {
                case "Left": left = value; break;
                case "Top": top = value; break;
                case "Right": right = value; break;
                case "Bottom": bottom = value; break;
            }
        }
        return (left, top, right, bottom);
    }

    public int Count => images.Count;

    public MappedImage? Find(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Equals("NoImage", StringComparison.OrdinalIgnoreCase))
            return null;
        if (images.TryGetValue(name, out var image))
            return image;

        // A plain texture file with this name works as a whole-image "mapped image".
        if (textures.Get(name + ".tga", repeat: false) is { } texture)
        {
            image = new MappedImage(name, name + ".tga", 0, 0, 1, 1, texture.Width, texture.Height, false);
            images[name] = image;
            return image;
        }
        return null;
    }

    public void Draw(SpriteBatch batch, MappedImage image, Rect destination, Vector4? color = null)
    {
        var texture = textures.Get(image.TextureName, repeat: false);
        if (texture == null)
            return;
        if (image.Rotated)
        {
            // Stored turned 90 degrees clockwise in the page, so the image's top-left is the page's top-right.
            batch.DrawTextureCorners(texture, destination,
                new Vector2(image.U1, image.V0), new Vector2(image.U1, image.V1),
                new Vector2(image.U0, image.V1), new Vector2(image.U0, image.V0), color ?? Vector4.One);
            return;
        }
        batch.DrawTexture(texture, destination, image.U0, image.V0, image.U1, image.V1, color ?? Vector4.One);
    }

    public void Draw(SpriteBatch batch, string? name, Rect destination, Vector4? color = null)
    {
        if (Find(name) is { } image)
            Draw(batch, image, destination, color);
    }
}

using System.Numerics;
using Silk.NET.OpenGL;
using StbTrueTypeSharp;

namespace OpenGZH.Client.UI;

/// <summary>
/// A font at one pixel size: a texture atlas of pre-rendered glyphs plus their metrics.
/// Covers Latin-1 and common typographic punctuation (enough for every language the game ships with
/// that uses Latin script).
/// </summary>
public sealed unsafe class GameFont : IDisposable
{
    readonly GL gl;
    readonly uint texture;
    readonly Dictionary<int, StbTrueType.stbtt_packedchar> glyphs = new();
    readonly int atlasSize;

    public float PixelHeight { get; }
    public float Ascent { get; }
    public float LineHeight { get; }
    public bool FakeBold { get; }

    static readonly (int First, int Count)[] Ranges = [(32, 224), (0x2010, 0x30), (0x20AC, 1)];

    public GameFont(GL gl, byte[] ttf, float pixelHeight, bool fakeBold)
    {
        this.gl = gl;
        PixelHeight = pixelHeight;
        FakeBold = fakeBold;

        atlasSize = pixelHeight > 40 ? 1024 : 512;
        var bitmap = new byte[atlasSize * atlasSize];
        var context = new StbTrueType.stbtt_pack_context();
        fixed (byte* pixels = bitmap)
        fixed (byte* font = ttf)
        {
            StbTrueType.stbtt_PackBegin(context, pixels, atlasSize, atlasSize, atlasSize, 1, null);
            StbTrueType.stbtt_PackSetOversampling(context, 2, 1);
            foreach (var (first, count) in Ranges)
            {
                var packed = new StbTrueType.stbtt_packedchar[count];
                fixed (StbTrueType.stbtt_packedchar* data = packed)
                    StbTrueType.stbtt_PackFontRange(context, font, 0, pixelHeight, first, count, data);
                for (int i = 0; i < count; i++)
                    glyphs[first + i] = packed[i];
            }
            StbTrueType.stbtt_PackEnd(context);

            var info = new StbTrueType.stbtt_fontinfo();
            StbTrueType.stbtt_InitFont(info, font, 0);
            int ascent, descent, lineGap;
            StbTrueType.stbtt_GetFontVMetrics(info, &ascent, &descent, &lineGap);
            float scale = StbTrueType.stbtt_ScaleForPixelHeight(info, pixelHeight);
            Ascent = ascent * scale;
            LineHeight = (ascent - descent + lineGap) * scale;
        }

        texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (byte* pixels = bitmap)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, (uint)atlasSize, (uint)atlasSize, 0, PixelFormat.Red, PixelType.UnsignedByte, pixels);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    }

    StbTrueType.stbtt_packedchar? Glyph(char c) =>
        glyphs.TryGetValue(c, out var g) ? g : glyphs.TryGetValue('?', out var q) ? q : null;

    public float CharWidth(char c) => Glyph(c)?.xadvance ?? 0;

    public float Measure(ReadOnlySpan<char> text)
    {
        float width = 0;
        foreach (char c in text)
            width += CharWidth(c);
        return width + (FakeBold && text.Length > 0 ? 1 : 0);
    }

    /// <summary>Draws one line of text with its top-left corner at <paramref name="position"/>.</summary>
    public void Draw(SpriteBatch batch, ReadOnlySpan<char> text, Vector2 position, Vector4 color)
    {
        float x = position.X;
        float baseline = position.Y + Ascent;
        foreach (char c in text)
        {
            if (Glyph(c) is not { } g)
                continue;
            float width = (g.xoff2 - g.xoff), height = (g.yoff2 - g.yoff);
            var rect = new Rect(MathF.Round(x) + g.xoff, MathF.Round(baseline) + g.yoff, width, height);
            float u0 = g.x0 / (float)atlasSize, v0 = g.y0 / (float)atlasSize;
            float u1 = g.x1 / (float)atlasSize, v1 = g.y1 / (float)atlasSize;
            batch.DrawGlyph(texture, rect, u0, v0, u1, v1, color);
            if (FakeBold)
                batch.DrawGlyph(texture, rect with { X = rect.X + 1 }, u0, v0, u1, v1, color);
            x += g.xadvance;
        }
    }

    /// <summary>Text with the original game's 1-pixel drop shadow/border colour behind it.</summary>
    public void DrawWithShadow(SpriteBatch batch, ReadOnlySpan<char> text, Vector2 position, Vector4 color, Vector4 shadow)
    {
        if (shadow.W > 0)
            Draw(batch, text, position + Vector2.One, shadow);
        Draw(batch, text, position, color);
    }

    /// <summary>Splits text into lines that fit <paramref name="maxWidth"/> (words are not broken).</summary>
    public List<string> WrapLines(string text, float maxWidth)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            if (maxWidth <= 0 || Measure(paragraph) <= maxWidth)
            {
                lines.Add(paragraph);
                continue;
            }
            var line = new System.Text.StringBuilder();
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (Measure(candidate) > maxWidth && line.Length > 0)
                {
                    lines.Add(line.ToString());
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    public void Dispose() => gl.DeleteTexture(texture);
}

/// <summary>
/// Finds and caches fonts by the names the original data uses ("Arial", "Times New Roman",
/// "Placard MT Condensed", "Generals"). System fonts are used when installed; otherwise the
/// freely licensed fallbacks in the OpenGZH "fonts" folder.
/// </summary>
public sealed class FontLibrary : IDisposable
{
    readonly GL gl;
    readonly string bundledFolder;
    readonly Dictionary<(string Family, int Pixels, bool Bold), GameFont> fonts = new();
    readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extra multiplier for every font (the interface scales with the screen resolution).</summary>
    public float Scale { get; set; } = 1;

    public FontLibrary(GL gl, string gameRoot)
    {
        this.gl = gl;
        bundledFolder = Path.Combine(gameRoot, "fonts");
    }

    /// <summary>
    /// A font by original name and point size. The original converted points to pixels for a 96-DPI
    /// screen (×4/3) at its 800×600 layout resolution; <see cref="Scale"/> adapts that to the real screen.
    /// </summary>
    public GameFont Get(string family, float pointSize, bool bold = false)
    {
        int pixels = Math.Max(6, (int)MathF.Round(pointSize * 4f / 3f * Scale));
        var key = (family.ToLowerInvariant(), pixels, bold);
        if (fonts.TryGetValue(key, out var font))
            return font;

        var (data, isRealBold) = FindFontFile(family, bold);
        font = new GameFont(gl, data, pixels, fakeBold: bold && !isRealBold);
        fonts[key] = font;
        return font;
    }

    (byte[] Data, bool IsBold) FindFontFile(string family, bool bold)
    {
        string name = family.ToLowerInvariant();
        string[] system, bundled;
        if (name.Contains("times"))
        {
            system = bold ? ["timesbd.ttf", "Times New Roman Bold.ttf", "LiberationSerif-Bold.ttf"] : ["times.ttf", "Times New Roman.ttf", "LiberationSerif-Regular.ttf"];
            bundled = bold ? ["Tinos-Bold.ttf"] : ["Tinos-Regular.ttf"];
        }
        else if (name.Contains("placard") || name.Contains("generals") || name.Contains("abadi"))
        {
            system = [];
            bundled = ["Oswald.ttf"];
        }
        else if (name.Contains("courier"))
        {
            system = bold ? ["courbd.ttf", "Courier New Bold.ttf"] : ["cour.ttf", "Courier New.ttf"];
            bundled = ["Arimo.ttf"];
        }
        else
        {
            system = bold ? ["arialbd.ttf", "Arial Bold.ttf", "LiberationSans-Bold.ttf"] : ["arial.ttf", "Arial.ttf", "LiberationSans-Regular.ttf"];
            bundled = ["Arimo.ttf"];
        }

        foreach (string file in system)
            foreach (string folder in SystemFontFolders())
                if (Load(Path.Combine(folder, file)) is { } data)
                    return (data, bold);
        foreach (string file in bundled)
            if (Load(Path.Combine(bundledFolder, file)) is { } data)
                return (data, file.Contains("Bold"));
        throw new FileNotFoundException($"No font file found for '{family}' (looked in the system font folders and {bundledFolder}).");
    }

    byte[]? Load(string path)
    {
        if (files.TryGetValue(path, out var cached))
            return cached;
        if (!File.Exists(path))
            return null;
        return files[path] = File.ReadAllBytes(path);
    }

    static IEnumerable<string> SystemFontFolders()
    {
        if (OperatingSystem.IsWindows())
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts/Supplemental";
            yield return "/Library/Fonts";
        }
        if (OperatingSystem.IsLinux())
        {
            yield return "/usr/share/fonts/truetype/msttcorefonts";
            yield return "/usr/share/fonts/truetype/liberation";
            yield return "/usr/share/fonts/liberation";
        }
    }

    /// <summary>Forget fonts made at the old scale (after a resolution change).</summary>
    public void Clear()
    {
        foreach (var font in fonts.Values)
            font.Dispose();
        fonts.Clear();
    }

    public void Dispose() => Clear();
}

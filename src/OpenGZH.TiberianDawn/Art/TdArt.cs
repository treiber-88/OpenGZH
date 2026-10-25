using OpenGZH.Formats.Images;
using OpenGZH.Formats.Westwood;
using OpenGZH.TiberianDawn.Data;
using OpenGZH.TiberianDawn.Logic;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Art;

/// <summary>
/// A running game's art, from the original files: shapes (theater art first, then NAME.SHP), the
/// sidebar cameos (NAMEICNH, from the theater's ICNH mix), and the terrain templates — turned into
/// RGBA images with the theater palette, a house's colour remap, and colour 4 as a half-dark shadow.
/// Also answers the logic's template icon-map questions.
/// </summary>
public sealed class TdArt : ITdArt
{
    public const int CellPixels = 24;

    readonly TdContent content;
    readonly Dictionary<string, KeyframeShape?> shapes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IconSet?> iconSets = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<TemplateTypeClass, sbyte[]?> iconMaps = new();

    public WestwoodPalette Palette { get; }
    public string TheaterExtension { get; }

    public TdArt(TdContent content, TheaterType theater)
    {
        this.content = content;
        string name = theater switch
        {
            TheaterType.THEATER_DESERT => "DESERT",
            TheaterType.THEATER_WINTER => "WINTER",
            _ => "TEMPERATE",
        };
        var (ext, palette) = TdScenario.TheaterFiles(name);
        TheaterExtension = ext;
        Palette = new WestwoodPalette(content.Files.ReadAllBytes(palette));
    }

    KeyframeShape? Load(string file)
    {
        if (shapes.TryGetValue(file, out var shape))
            return shape;
        try
        {
            shape = content.Files.Exists(file) ? new KeyframeShape(content.Files.ReadAllBytes(file)) : null;
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            shape = null;
        }
        return shapes[file] = shape;
    }

    /// <summary>An object's shapes: NAME + theater extension, else NAME.SHP.</summary>
    public KeyframeShape? Shape(string name) => Load(name + TheaterExtension) ?? Load(name + ".SHP");

    /// <summary>A sidebar cameo (the remaster names them NAMEICNH; only the temperate set ships with
    /// the freeware files, so other theaters fall back to it).</summary>
    public KeyframeShape? Cameo(string iniName) =>
        Load(iniName + "ICNH" + TheaterExtension) ?? Load(iniName + "ICNH.TEM") ?? Load(iniName + "ICON.SHP");

    public IconSet? Icons(string file)
    {
        if (iconSets.TryGetValue(file, out var set))
            return set;
        try
        {
            set = content.Files.Exists(file) ? new IconSet(content.Files.ReadAllBytes(file)) : null;
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            set = null;
        }
        return iconSets[file] = set;
    }

    /// <summary>ITdArt: which logical icons of a template have art (-1 = none).</summary>
    public sbyte[]? Template_Icon_Map(TemplateTypeClass? template)
    {
        if (template == null)
            return null;
        if (iconMaps.TryGetValue(template, out var map))
            return map;
        var set = Icons(template.IniName + TheaterExtension);
        if (set != null)
        {
            int n = Math.Max(template.Width * template.Height, 1);
            map = new sbyte[n];
            for (int i = 0; i < n; i++)
                map[i] = (sbyte)Math.Clamp(set.IconForCell(i), -1, 127);
        }
        return iconMaps[template] = map;
    }

    /// <summary>One frame as an RGBA image: 0 clear, 4 a half-dark shadow, the rest the palette
    /// (through the house remap, if any). Ghost draws the frame translucent.</summary>
    public Image? Frame(KeyframeShape shape, int frame, byte[]? remap, bool shadowOnly = false, bool ghost = false)
    {
        if (frame < 0 || frame >= shape.FrameCount)
            return null;
        byte[] pixels = shape.Frame(frame);
        var image = new Image(shape.Width, shape.Height);
        for (int i = 0; i < pixels.Length && i < shape.Width * shape.Height; i++)
        {
            byte c = pixels[i];
            if (c == 0)
                continue;
            int o = i * 4;
            if (shadowOnly || c == 4)
            {
                image.Pixels[o + 3] = 128;
                continue;
            }
            var (r, g, b, _) = Palette.Color(remap != null ? remap[c] : c, zeroIsClear: false);
            image.Pixels[o] = r;
            image.Pixels[o + 1] = g;
            image.Pixels[o + 2] = b;
            image.Pixels[o + 3] = ghost ? (byte)128 : (byte)255;
        }
        return image;
    }

    /// <summary>TD's house colour ramp: the palette entries every remap table replaces.</summary>
    public const int RemapFirst = 176, RemapLast = 191;

    /// <summary>
    /// A frame in any team colour (for Zero Hour engine players, whose colours TD's six remap tables
    /// don't cover): the house ramp keeps its shading, scaled onto <paramref name="team"/>.
    /// Shadow-only draws the frame as a half-dark shadow; ghost draws it translucent.
    /// </summary>
    public Image? Frame(KeyframeShape shape, int frame, (byte R, byte G, byte B) team, bool shadowOnly = false, bool ghost = false)
    {
        var image = Frame(shape, frame, null, shadowOnly, ghost);
        if (image == null || shadowOnly)
            return image;
        byte[] pixels = shape.Frame(frame);
        // The ramp's brightest entry maps to the team colour itself.
        float top = 0;
        for (int c = RemapFirst; c <= RemapLast; c++)
        {
            var (r, g, b, _) = Palette.Color(c, zeroIsClear: false);
            top = Math.Max(top, Luma(r, g, b));
        }
        for (int i = 0; i < pixels.Length && i < shape.Width * shape.Height; i++)
        {
            byte c = pixels[i];
            if (c < RemapFirst || c > RemapLast)
                continue;
            var (r, g, b, _) = Palette.Color(c, zeroIsClear: false);
            float k = top > 0 ? Luma(r, g, b) / top : 1;
            int o = i * 4;
            image.Pixels[o] = (byte)Math.Clamp(team.R * k, 0, 255);
            image.Pixels[o + 1] = (byte)Math.Clamp(team.G * k, 0, 255);
            image.Pixels[o + 2] = (byte)Math.Clamp(team.B * k, 0, 255);
        }
        return image;

        static float Luma(byte r, byte g, byte b) => 0.299f * r + 0.587f * g + 0.114f * b;
    }

    /// <summary>The whole 64x64 map's terrain (templates; an empty cell shows the clear template).</summary>
    public Image Terrain(TdGame game)
    {
        var map = game.Map;
        int size = TdMath.MapCellW * CellPixels;
        var image = new Image(size, size);
        var clear = Icons("CLEAR1" + TheaterExtension);
        for (int cell = 0; cell < TdDefines.MAP_CELL_TOTAL; cell++)
        {
            var c = map[cell];
            IconSet? set = null;
            int icon = -1;
            if (c.TType != TemplateType.TEMPLATE_NONE && game.Types.Template(c.TType) is { } template)
            {
                set = Icons(template.IniName + TheaterExtension);
                if (set != null && c.TIcon < template.Width * template.Height)
                    icon = set.IconForCell(c.TIcon);
            }
            if (set == null || icon < 0)
            {
                set = clear;
                icon = set?.IconForCell(c.Clear_Icon()) ?? -1;
            }
            if (set == null || icon < 0 || icon >= set.Count)
                continue;
            var pixels = set.Icon(icon);
            int px = (cell % TdMath.MapCellW) * CellPixels, py = (cell / TdMath.MapCellW) * CellPixels;
            for (int y = 0; y < set.IconHeight && y < CellPixels; y++)
            {
                for (int x = 0; x < set.IconWidth && x < CellPixels; x++)
                {
                    var (r, g, b, _) = Palette.Color(pixels[y * set.IconWidth + x], zeroIsClear: false);
                    int o = ((py + y) * size + px + x) * 4;
                    image.Pixels[o] = r;
                    image.Pixels[o + 1] = g;
                    image.Pixels[o + 2] = b;
                    image.Pixels[o + 3] = 255;
                }
            }
        }
        return image;
    }

    /// <summary>The palette colour of an index (for the radar and UI).</summary>
    public (byte R, byte G, byte B) Color(int index)
    {
        var (r, g, b, _) = Palette.Color(index, zeroIsClear: false);
        return (r, g, b);
    }
}

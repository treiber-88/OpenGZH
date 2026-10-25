using OpenGZH.Core.Rules;
using OpenGZH.Formats.Images;
using OpenGZH.Formats.Westwood;
using OpenGZH.TiberianDawn.Data;

namespace OpenGZH.TiberianDawn.Art;

/// <summary>
/// Draws a whole scenario as it looks at the start, on the CPU, the way the original draws its
/// layers: terrain templates (CellClass::Draw_It), smudges, overlays (walls and Tiberium, centred
/// on their cell), then terrain objects, buildings, vehicles, aircraft and infantry. Used to check
/// maps and art; the game itself draws with the GPU.
/// </summary>
public sealed class TdMapRenderer
{
    public const int CellPixels = 24;

    // TechnoClass::BodyShape: facing (32 steps, clockwise from north) to frame (counter-clockwise art).
    static readonly int[] BodyShape = [0, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1];

    // StoppingCoordAbs: where infantry stand in a cell (leptons, 256 to a cell).
    static readonly (int X, int Y)[] SubCellLeptons = [(0x80, 0x80), (0x40, 0x40), (0xC0, 0x40), (0x40, 0xC0), (0xC0, 0xC0)];

    readonly TdContent content;
    readonly Dictionary<string, KeyframeShape?> shapes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IconSet?> iconSets = new(StringComparer.OrdinalIgnoreCase);

    public TdMapRenderer(TdContent content) => this.content = content;

    KeyframeShape? Shape(string file)
    {
        if (shapes.TryGetValue(file, out var shape))
            return shape;
        try
        {
            shape = content.Files.Exists(file) ? new KeyframeShape(content.Files.ReadAllBytes(file)) : null;
        }
        catch (InvalidDataException)
        {
            shape = null;
        }
        return shapes[file] = shape;
    }

    IconSet? Icons(string file)
    {
        if (iconSets.TryGetValue(file, out var set))
            return set;
        try
        {
            set = content.Files.Exists(file) ? new IconSet(content.Files.ReadAllBytes(file)) : null;
        }
        catch (InvalidDataException)
        {
            set = null;
        }
        return iconSets[file] = set;
    }

    /// <summary>An object's art: theater-specific (NAME + theater extension) or NAME.SHP.</summary>
    KeyframeShape? ArtFor(string iniName, string extension) => Shape(iniName + extension) ?? Shape(iniName + ".SHP");

    sealed class Canvas(int width, int height, WestwoodPalette palette)
    {
        public readonly byte[] Index = new byte[width * height];
        public readonly int Width = width, Height = height;
        public readonly WestwoodPalette Palette = palette;
        public readonly bool[] Shadow = new bool[width * height];

        public void Stamp(ReadOnlySpan<byte> pixels, int w, int h, int x, int y, byte[]? remap = null, bool transparent = true)
        {
            for (int yy = 0; yy < h; yy++)
            {
                int py = y + yy;
                if (py < 0 || py >= Height)
                    continue;
                for (int xx = 0; xx < w; xx++)
                {
                    int px = x + xx;
                    if (px < 0 || px >= Width)
                        continue;
                    byte c = pixels[yy * w + xx];
                    if (transparent && c == 0)
                        continue;
                    int i = py * Width + px;
                    // Colour 4 marks shadow in object art (drawn as a darkening of what is underneath).
                    if (transparent && c == 4)
                    {
                        Shadow[i] = true;
                        continue;
                    }
                    Index[i] = remap != null ? remap[c] : c;
                    Shadow[i] = false;
                }
            }
        }

        public Image ToImage()
        {
            var image = new Image(Width, Height);
            for (int i = 0; i < Index.Length; i++)
            {
                var (r, g, b, _) = Palette.Color(Index[i], zeroIsClear: false);
                if (Shadow[i])
                {
                    r = (byte)(r / 2);
                    g = (byte)(g / 2);
                    b = (byte)(b / 2);
                }
                image.Pixels[i * 4] = r;
                image.Pixels[i * 4 + 1] = g;
                image.Pixels[i * 4 + 2] = b;
                image.Pixels[i * 4 + 3] = 255;
            }
            return image;
        }
    }

    byte[]? RemapFor(string house)
    {
        var houseNode = content.Find("Houses", house);
        string table = houseNode?.GetString("RemapTable") ?? "";
        if (table.Length == 0 || !content.Rules.Section("RemapTables").TryGet(table, out var node))
            return null;
        var values = node.AsList().Select(n => (byte)RuleValue.Int(n.Value ?? "0")).ToArray();
        return values.Length == 256 ? values : null;
    }

    /// <summary>The playable area of the scenario (or the whole 64x64 map) as an image.</summary>
    public Image Render(TdScenario scenario, bool wholeMap = false, int seed = 1)
    {
        var (ext, paletteFile) = TdScenario.TheaterFiles(scenario.Theater);
        var palette = new WestwoodPalette(content.Files.ReadAllBytes(paletteFile));
        int originX = wholeMap ? 0 : scenario.X, originY = wholeMap ? 0 : scenario.Y;
        int cellsW = wholeMap ? TdScenario.MapCellWidth : scenario.Width, cellsH = wholeMap ? TdScenario.MapCellHeight : scenario.Height;
        var canvas = new Canvas(cellsW * CellPixels, cellsH * CellPixels, palette);
        var random = new Random(seed);

        (int X, int Y) Pixel(int cell) => (((cell % 64) - originX) * CellPixels, ((cell / 64) - originY) * CellPixels);

        // Terrain templates; an empty cell shows the clear template's icon for its position (Clear_Icon).
        var clear = Icons("CLEAR1" + ext);
        for (int cell = 0; cell < scenario.Cells.Length; cell++)
        {
            var (px, py) = Pixel(cell);
            if (px < 0 || py < 0 || px >= canvas.Width || py >= canvas.Height)
                continue;
            var (type, icon) = scenario.Cells[cell];
            IconSet? set = null;
            int iconIndex = -1;
            if (type != TdScenario.TemplateNone && content.TemplateById(type) is { } template)
            {
                set = Icons(template.GetString("IniName") + ext);
                int cellsInTemplate = RuleValue.Int(template.GetString("Width"), 1) * RuleValue.Int(template.GetString("Height"), 1);
                if (set != null && icon < cellsInTemplate)
                    iconIndex = set.IconForCell(icon);
            }
            if (set == null || iconIndex < 0)
            {
                set = clear;
                iconIndex = set?.IconForCell((cell & 0x03) | ((cell >> 4) & 0x0C)) ?? -1;
            }
            if (set != null && iconIndex >= 0 && iconIndex < set.Count)
                canvas.Stamp(set.Icon(iconIndex), set.IconWidth, set.IconHeight, px, py, transparent: false);
        }

        // Smudges (craters, scorch marks, building bibs).
        foreach (var (cell, type, data) in scenario.Smudges)
            if (ArtFor(type, ext) is { } shape)
            {
                var (px, py) = Pixel(cell);
                canvas.Stamp(shape.Frame(Math.Clamp(data, 0, shape.FrameCount - 1)), shape.Width, shape.Height, px, py);
            }

        // Overlays: walls join their neighbours; Tiberium picks a random look and a density from its neighbours.
        var overlayAt = scenario.Overlay.GroupBy(o => o.Cell).ToDictionary(g => g.Key, g => g.Last().Type);
        string? OverlayAt(int c) => c >= 0 && c < 4096 ? overlayAt.GetValueOrDefault(c) : null;
        bool IsTiberium(string? type) => type != null && content.Find("Overlays", type)?.GetString("Land") == "LAND_TIBERIUM";
        foreach (var (cell, rawType) in overlayAt)
        {
            string type = rawType;
            var overlay = content.Find("Overlays", type);
            int frame = 0;
            if (overlay != null && IsTrue(overlay.GetString("IsWall")))
            {
                int[] neighbours = [cell - 64, cell + 1, cell + 64, cell - 1]; // N, E, S, W
                for (int i = 0; i < 4; i++)
                    if (OverlayAt(neighbours[i]) == type)
                        frame |= 1 << i;
            }
            else if (IsTiberium(type))
            {
                type = $"TI{random.Next(1, 13)}";
                int[] adjacentTable = [0, 1, 3, 4, 6, 7, 8, 10, 11];
                int count = 0;
                foreach (int d in new[] { -64, -63, 1, 65, 64, 63, -1, -65 })
                    if (IsTiberium(OverlayAt(cell + d)))
                        count++;
                frame = adjacentTable[count];
            }
            if (ArtFor(type, ext) is { } shape)
            {
                var (px, py) = Pixel(cell);
                canvas.Stamp(shape.Frame(Math.Min(frame, shape.FrameCount - 1)), shape.Width, shape.Height,
                    px + CellPixels / 2 - shape.Width / 2, py + CellPixels / 2 - shape.Height / 2);
            }
        }

        // Objects, back to front.
        var draws = new List<(int SortY, Action Draw)>();
        foreach (var (cell, type, _) in scenario.Terrain)
            if (ArtFor(type, ext) is { } shape)
            {
                var (px, py) = Pixel(cell);
                draws.Add((py + shape.Height, () => canvas.Stamp(shape.Frame(0), shape.Width, shape.Height, px, py)));
            }
        foreach (var s in scenario.Structures)
            if (ArtFor(s.Type, ext) is { } shape)
            {
                var (px, py) = Pixel(s.Cell);
                var remap = RemapFor(s.House);
                var node = content.Find("Buildings", s.Type);
                bool turret = IsTrue(node?.GetString("IsTurretEquipped") ?? "") && shape.FrameCount >= 32;
                // Turreted defences (gun turret, SAM, obelisk...) face their direction; others show frame 0.
                int frame = turret ? BodyShape[((s.Facing + 4) >> 3) & 31] : 0;
                // Under half strength (256 = full) the damaged half of the frames is used.
                if (!turret && s.Strength < 128 && shape.FrameCount >= 2)
                    frame = shape.FrameCount / 2;
                draws.Add((py + shape.Height, () => canvas.Stamp(shape.Frame(Math.Min(frame, shape.FrameCount - 1)), shape.Width, shape.Height, px, py, remap)));
            }
        foreach (var (objects, section) in new[] { (scenario.Units, "Vehicles"), (scenario.Aircraft, "Aircraft") })
            foreach (var u in objects)
                if (ArtFor(u.Type, ".SHP") is { } shape)
                {
                    var (px, py) = Pixel(u.Cell);
                    var remap = RemapFor(u.House);
                    var node = content.Find(section, u.Type);
                    int frame = shape.FrameCount >= 32 ? BodyShape[((u.Facing + 4) >> 3) & 31] : 0;
                    bool turret = IsTrue(node?.GetString("IsTurretEquipped") ?? "") && shape.FrameCount >= 64;
                    int cx = px + CellPixels / 2 - shape.Width / 2, cy = py + CellPixels / 2 - shape.Height / 2;
                    draws.Add((py + CellPixels / 2, () =>
                    {
                        canvas.Stamp(shape.Frame(frame), shape.Width, shape.Height, cx, cy, remap);
                        if (turret)
                            canvas.Stamp(shape.Frame(32 + frame), shape.Width, shape.Height, cx, cy, remap);
                    }));
                }
        foreach (var i in scenario.Infantry)
            if (ArtFor(i.Type, ".SHP") is { } shape)
            {
                var (px, py) = Pixel(i.Cell);
                var remap = RemapFor(i.House);
                var (lx, ly) = SubCellLeptons[Math.Clamp(i.SubCell, 0, 4)];
                int x = px + lx * CellPixels / 256, y = py + ly * CellPixels / 256;
                // Standing: one frame per eighth of a turn, counter-clockwise from north (as BodyShape).
                int frame = (8 - (((i.Facing + 16) >> 5) & 7)) & 7;
                draws.Add((y, () => canvas.Stamp(shape.Frame(frame), shape.Width, shape.Height, x - shape.Width / 2, y - shape.Height / 2, remap)));
            }
        foreach (var (_, draw) in draws.OrderBy(d => d.SortY))
            draw();

        return canvas.ToImage();
    }

    static bool IsTrue(string value) => value is "true" or "1" or "yes";
}

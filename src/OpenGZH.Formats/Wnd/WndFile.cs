using System.Numerics;
using System.Text.RegularExpressions;

namespace OpenGZH.Formats.Wnd;

/// <summary>One state's image and colours (an entry of ENABLEDDRAWDATA / DISABLEDDRAWDATA / HILITEDRAWDATA).</summary>
public sealed record WndDrawData(string Image, Vector4 Color, Vector4 BorderColor)
{
    public bool HasImage => !Image.Equals("NoImage", StringComparison.OrdinalIgnoreCase) && Image.Length > 0;
}

/// <summary>A window or gadget from a .wnd layout file (the original game's interface format).</summary>
public sealed class WndWindow
{
    /// <summary>USER, PUSHBUTTON, STATICTEXT, CHECKBOX, RADIOBUTTON, LISTBOX, COMBOBOX, ENTRYFIELD, PROGRESSBAR, HORZSLIDER, VERTSLIDER, TABCONTROL.</summary>
    public string Type { get; set; } = "USER";
    public string Name { get; set; } = "";

    /// <summary>Position and size in the layout's creation resolution (normally 800x600).</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int CreationWidth { get; set; } = 800;
    public int CreationHeight { get; set; } = 600;

    public HashSet<string> Status { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Style { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string SystemCallback { get; set; } = "";
    public string InputCallback { get; set; } = "";
    public string TooltipCallback { get; set; } = "";
    public string DrawCallback { get; set; } = "";

    public string FontName { get; set; } = "Arial";
    public int FontSize { get; set; } = 10;
    public bool FontBold { get; set; }

    public string HeaderTemplate { get; set; } = "";
    public string Text { get; set; } = "";
    public string Tooltip { get; set; } = "";
    public int TooltipDelay { get; set; } = -1;

    public Vector4 TextEnabled { get; set; } = Vector4.One;
    public Vector4 TextEnabledBorder { get; set; }
    public Vector4 TextDisabled { get; set; } = new(0.75f, 0.75f, 0.75f, 1);
    public Vector4 TextDisabledBorder { get; set; }
    public Vector4 TextHilite { get; set; } = Vector4.One;
    public Vector4 TextHiliteBorder { get; set; }

    public List<WndDrawData> Enabled { get; } = new();
    public List<WndDrawData> Disabled { get; } = new();
    public List<WndDrawData> Hilite { get; } = new();

    /// <summary>Gadget-specific settings (LISTBOXDATA, SLIDERDATA, STATICTEXTDATA, ...) as key/value text.</summary>
    public Dictionary<string, Dictionary<string, string>> GadgetData { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<WndWindow> Children { get; } = new();

    public string? Data(string block, string key) =>
        GadgetData.TryGetValue(block, out var values) && values.TryGetValue(key, out var value) ? value : null;

    public WndDrawData? EnabledData(int index) => index < Enabled.Count ? Enabled[index] : null;
    public WndDrawData? DisabledData(int index) => index < Disabled.Count ? Disabled[index] : null;
    public WndDrawData? HiliteData(int index) => index < Hilite.Count ? Hilite[index] : null;

    public override string ToString() => $"{Type} {Name}";
}

/// <summary>
/// Reads .wnd layouts. Statements end with ';' and may span lines; WINDOW ... END blocks nest
/// through CHILD / ENDALLCHILDREN.
/// </summary>
public static partial class WndFile
{
    public static List<WndWindow> Parse(string text)
    {
        var tokens = Tokenize(text);
        var roots = new List<WndWindow>();
        var stack = new Stack<WndWindow>();
        bool nextIsChild = false;

        foreach (string statement in tokens)
        {
            string trimmed = statement.Trim();
            if (trimmed.Length == 0)
                continue;

            switch (trimmed.ToUpperInvariant())
            {
                case "WINDOW":
                    var window = new WndWindow();
                    if (nextIsChild && stack.Count > 0)
                        stack.Peek().Children.Add(window);
                    else if (stack.Count == 0)
                        roots.Add(window);
                    else
                        stack.Peek().Children.Add(window);
                    stack.Push(window);
                    nextIsChild = false;
                    continue;
                case "CHILD":
                    nextIsChild = true;
                    continue;
                case "END":
                    if (stack.Count > 0)
                        stack.Pop();
                    continue;
                case "ENDALLCHILDREN":
                case "STARTLAYOUTBLOCK":
                case "ENDLAYOUTBLOCK":
                    continue;
            }

            int equals = trimmed.IndexOf('=');
            if (equals < 0 || stack.Count == 0)
                continue;
            string key = trimmed[..equals].Trim().ToUpperInvariant();
            string value = trimmed[(equals + 1)..].Trim();
            Apply(stack.Peek(), key, value);
        }
        return roots;
    }

    /// <summary>Splits into statements: lines are joined until ';', but block keywords stand alone.</summary>
    static IEnumerable<string> Tokenize(string text)
    {
        var current = new System.Text.StringBuilder();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();
            if (current.Length == 0 && trimmed is "WINDOW" or "END" or "CHILD" or "ENDALLCHILDREN" or "STARTLAYOUTBLOCK" or "ENDLAYOUTBLOCK")
            {
                yield return trimmed;
                continue;
            }
            current.Append(' ').Append(trimmed);
            if (trimmed.EndsWith(';'))
            {
                yield return current.ToString().TrimEnd(';', ' ');
                current.Clear();
            }
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    static void Apply(WndWindow window, string key, string value)
    {
        switch (key)
        {
            case "WINDOWTYPE": window.Type = value.Trim(); break;
            case "NAME": window.Name = Unquote(value); break;
            case "SCREENRECT":
                var numbers = Numbers(value);
                if (numbers.Count >= 4)
                {
                    window.X = numbers[0];
                    window.Y = numbers[1];
                    window.Width = numbers[2] - numbers[0];
                    window.Height = numbers[3] - numbers[1];
                }
                if (numbers.Count >= 6)
                {
                    window.CreationWidth = numbers[4];
                    window.CreationHeight = numbers[5];
                }
                break;
            case "STATUS": Flags(window.Status, value); break;
            case "STYLE": Flags(window.Style, value); break;
            case "SYSTEMCALLBACK": window.SystemCallback = Unquote(value); break;
            case "INPUTCALLBACK": window.InputCallback = Unquote(value); break;
            case "TOOLTIPCALLBACK": window.TooltipCallback = Unquote(value); break;
            case "DRAWCALLBACK": window.DrawCallback = Unquote(value); break;
            case "FONT":
                var font = FontPattern().Match(value);
                if (font.Success)
                {
                    window.FontName = font.Groups[1].Value;
                    window.FontSize = int.Parse(font.Groups[2].Value);
                    window.FontBold = font.Groups[3].Value != "0";
                }
                break;
            case "HEADERTEMPLATE": window.HeaderTemplate = Unquote(value); break;
            case "TEXT": window.Text = Unquote(value); break;
            case "TOOLTIPTEXT": window.Tooltip = Unquote(value); break;
            case "TOOLTIPDELAY": window.TooltipDelay = int.TryParse(value, out int delay) ? delay : -1; break;
            case "TEXTCOLOR": ParseTextColors(window, value); break;
            case "ENABLEDDRAWDATA": ParseDrawData(window.Enabled, value); break;
            case "DISABLEDDRAWDATA": ParseDrawData(window.Disabled, value); break;
            case "HILITEDRAWDATA": ParseDrawData(window.Hilite, value); break;
            default:
                if (key.EndsWith("DATA"))
                    window.GadgetData[key] = ParseKeyValues(value);
                break;
        }
    }

    static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];
        return value == "[None]" ? "" : value;
    }

    static List<int> Numbers(string value) =>
        NumberPattern().Matches(value).Select(m => int.Parse(m.Value)).ToList();

    static void Flags(HashSet<string> target, string value)
    {
        foreach (string flag in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            target.Add(flag);
    }

    static Vector4 ParseColor(string r, string g, string b, string a) =>
        new Vector4(int.Parse(r), int.Parse(g), int.Parse(b), int.Parse(a)) / 255f;

    static void ParseTextColors(WndWindow window, string value)
    {
        foreach (Match match in TextColorPattern().Matches(value))
        {
            var color = ParseColor(match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value, match.Groups[5].Value);
            switch (match.Groups[1].Value.ToUpperInvariant())
            {
                case "ENABLED": window.TextEnabled = color; break;
                case "ENABLEDBORDER": window.TextEnabledBorder = color; break;
                case "DISABLED": window.TextDisabled = color; break;
                case "DISABLEDBORDER": window.TextDisabledBorder = color; break;
                case "HILITE": window.TextHilite = color; break;
                case "HILITEBORDER": window.TextHiliteBorder = color; break;
            }
        }
    }

    static void ParseDrawData(List<WndDrawData> target, string value)
    {
        target.Clear();
        foreach (Match match in DrawDataPattern().Matches(value))
        {
            target.Add(new WndDrawData(
                match.Groups[1].Value,
                ParseColor(match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value, match.Groups[5].Value),
                ParseColor(match.Groups[6].Value, match.Groups[7].Value, match.Groups[8].Value, match.Groups[9].Value)));
        }
    }

    /// <summary>Gadget data such as "MINIMUM: 0, MAXIMUM: 100" or "LENGTH: 10, AUTOSCROLL: 1".</summary>
    static Dictionary<string, string> ParseKeyValues(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in value.Split(','))
        {
            int colon = part.IndexOf(':');
            if (colon < 0)
                continue;
            string k = part[..colon].Trim();
            string v = part[(colon + 1)..].Trim();
            // COLUMNWIDTH lists several numbers; keep extra values.
            result[k] = result.TryGetValue(k, out var existing) ? existing + " " + v : v;
        }
        return result;
    }

    [GeneratedRegex(@"NAME:\s*""([^""]*)""\s*,\s*SIZE:\s*(\d+)\s*,\s*BOLD:\s*(\d+)")]
    private static partial Regex FontPattern();

    [GeneratedRegex(@"-?\d+")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"(ENABLEDBORDER|ENABLED|DISABLEDBORDER|DISABLED|HILITEBORDER|HILITE):\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)")]
    private static partial Regex TextColorPattern();

    [GeneratedRegex(@"IMAGE:\s*([^,\s]+)\s*,\s*COLOR:\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*,\s*BORDERCOLOR:\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)")]
    private static partial Regex DrawDataPattern();
}

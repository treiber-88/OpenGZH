using System.Text;

namespace OpenGZH.Formats.Strings;

/// <summary>
/// Localized text, looked up by label ("OBJECT:Crusader", "CONTROLBAR:ToolTipUSABuildWarFactory").
/// Sources, later ones overriding earlier ones:
///   the game's .csf string table, a mission's map.str, and mod string files.
/// </summary>
public sealed class StringTable
{
    readonly Dictionary<string, string> strings = new(StringComparer.OrdinalIgnoreCase);

    public int Count => strings.Count;

    /// <summary>The text for a label, or the label itself if it is unknown (as the original showed "MISSING: ...").</summary>
    public string Get(string label) =>
        strings.TryGetValue(label, out var text) ? text : label;

    public bool TryGet(string label, out string text) => strings.TryGetValue(label, out text!);

    public void Set(string label, string text) => strings[label] = text;

    /// <summary>
    /// Reads a .csf file: a "CSF " header, then labels ("LBL ") each with one string ("STR ", or "STRW"
    /// with an extra audio name). String characters are UTF-16 stored bitwise-inverted.
    /// Tags are little-endian 32-bit values, so their bytes appear reversed in the file.
    /// </summary>
    public void LoadCsf(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data));
        if (ReadTag(reader) != "CSF ")
            throw new InvalidDataException("Not a CSF string table.");
        reader.ReadInt32(); // version
        int labelCount = reader.ReadInt32();
        reader.ReadInt32(); // string count
        reader.ReadInt32(); // reserved
        reader.ReadInt32(); // language

        for (int i = 0; i < labelCount && reader.BaseStream.Position < reader.BaseStream.Length; i++)
        {
            if (ReadTag(reader) != "LBL ")
                break;
            int stringCount = reader.ReadInt32();
            int labelLength = reader.ReadInt32();
            string label = Encoding.ASCII.GetString(reader.ReadBytes(labelLength));

            for (int s = 0; s < stringCount; s++)
            {
                string tag = ReadTag(reader);
                int length = reader.ReadInt32();
                byte[] raw = reader.ReadBytes(length * 2);
                for (int b = 0; b < raw.Length; b++)
                    raw[b] = (byte)~raw[b];
                string text = Encoding.Unicode.GetString(raw);
                if (tag == "STRW")
                {
                    int extraLength = reader.ReadInt32();
                    reader.ReadBytes(extraLength); // speech/audio file name, unused here
                }
                if (s == 0)
                    strings[label] = text;
            }
        }
    }

    static string ReadTag(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        Array.Reverse(bytes);
        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// Reads a text string file (map.str):
    ///   LABEL
    ///   "text, where \n is a line break"
    ///   END
    /// </summary>
    public void LoadStr(string text)
    {
        string? label = null;
        var value = new StringBuilder();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//"))
                continue;

            if (label == null)
            {
                label = trimmed;
                value.Clear();
            }
            else if (trimmed.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                strings[label] = value.ToString();
                label = null;
            }
            else
            {
                string part = trimmed.Trim('"').Replace("\\n", "\n");
                value.Append(part);
            }
        }
    }
}

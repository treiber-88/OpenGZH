namespace OpenGZH.TiberianDawn.Data;

/// <summary>
/// WWGetPrivateProfileString and friends (PROFILE.CPP), ported literally: the scenario loader reads
/// its INI text through these, quirks included — a key must start a line, its first letter is
/// matched case-sensitively (the rest not), a value runs to the next '\r', comments are not
/// stripped, a duplicate key always reads its first value, and listing a section's keys takes
/// everything from a line start to the next '=' (so a comment line joins onto the next key).
/// </summary>
public sealed class WwProfile
{
    readonly string profile;

    /// <summary>The INI text exactly as the file's bytes (Latin-1), line endings untouched.</summary>
    public WwProfile(string text) => profile = text;

    public static WwProfile FromBytes(byte[] bytes) => new(System.Text.Encoding.Latin1.GetString(bytes));

    char At(int index) => index >= 0 && index < profile.Length ? profile[index] : '\0';

    int Strchr(int from, char c)
    {
        if (from < 0 || from >= profile.Length)
            return -1;
        int i = profile.IndexOf(c, from);
        return i;
    }

    static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    bool Memicmp(int at, string s)
    {
        if (at < 0 || at + s.Length > profile.Length)
            return false;
        return string.Compare(profile, at, s, 0, s.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    /// <summary>strtrim (MiscAsm.cpp): strips spaces and tabs (only) from both ends.</summary>
    public static string Strtrim(string s) => s.Trim(' ', '\t');

    /// <summary>Finds a section: the start of its body and of the next section (as the original).</summary>
    bool Find_Section(string section, out int workptr, out int next)
    {
        string sec = "[" + section.ToUpperInvariant() + "]";
        int len = sec.Length;
        workptr = 0;
        next = -1;
        for (;;)
        {
            workptr = Strchr(workptr, '[');
            if (workptr < 0)
                return false;
            char c = workptr == 0 ? '\n' : At(workptr - 1);
            if (Memicmp(workptr, sec) && c == '\n')
            {
                workptr += len;
                while (IsSpace(At(workptr)))
                    workptr++;
                if (workptr > 4 && At(workptr - 1) == '\n' && At(workptr - 3) == '\n')
                    workptr -= 2;

                next = Strchr(workptr, '[');
                for (;;)
                {
                    if (next >= 0)
                    {
                        if (At(next - 1) == '\n')
                        {
                            if (At(next - 1) == '\n' && At(next - 3) == '\n')
                                next -= 2;
                            break;
                        }
                        next = Strchr(next + 1, '[');
                    }
                    else
                    {
                        next = workptr + (profile.Length - workptr) - 1;
                        break;
                    }
                }
                return true;
            }
            workptr++;
        }
    }

    /// <summary>
    /// WWGetPrivateProfileString(section, entry, def, retbuffer, retlen): the value (trimmed, at
    /// most retlen-1 characters) or the default when the entry is missing or empty.
    /// </summary>
    public string Get_String(string section, string entry, string? def, int retlen = 128)
    {
        string result = def == null ? "" : (def.Length > retlen - 1 ? def[..(retlen - 1)] : def);
        if (!Find_Section(section, out int workptr, out int next))
            return result;

        int entrylen = entry.Length;
        if (entrylen == 0)
            return result;
        for (;;)
        {
            workptr = Strchr(workptr, entry[0]);
            if (workptr < 0 || workptr >= next)
                return result;
            char c = At(workptr - 1);
            char c2 = At(workptr + entrylen);
            if (Memicmp(workptr, entry) && c == '\n' && (c2 == '=' || IsSpace(c2)))
            {
                workptr += entrylen;
                workptr = Strchr(workptr, '=');
                int altworkptr = workptr >= 0 ? Strchr(workptr, '\r') : -1;
                // No '=' or no '\r' after it (a NULL pointer compares below): the default stays.
                if (workptr < 0 || altworkptr < workptr)
                    return result;
                workptr++;
                while (IsSpace(At(workptr)))
                {
                    if (workptr >= altworkptr)
                        return result;
                    workptr++;
                }
                int len = altworkptr - workptr;
                if (len > retlen - 1)
                    len = retlen - 1;
                return Strtrim(profile.Substring(workptr, len));
            }
            workptr++;
        }
    }

    /// <summary>WWGetPrivateProfileString(section, NULL, ...): the section's entry names in order.</summary>
    public List<string> Entries(string section)
    {
        var names = new List<string>();
        if (!Find_Section(section, out int workptr, out int next))
            return names;
        while (workptr >= 0 && workptr < next)
        {
            int altworkptr = Strchr(workptr, '=');
            if (altworkptr >= 0 && altworkptr < next)
            {
                names.Add(Strtrim(profile[workptr..altworkptr]));
                workptr = Strchr(altworkptr, '\n');
                if (workptr < 0)
                    break;
                workptr++;
            }
            else
            {
                break;
            }
        }
        return names;
    }

    public bool Has_Section(string section) => Find_Section(section, out _, out _);

    /// <summary>WWGetPrivateProfileInt: atoi of the value (15 characters at most).</summary>
    public int Get_Int(string section, string entry, int def) => Atoi(Get_String(section, entry, def.ToString(), 15));

    /// <summary>WWGetPrivateProfileHex: sscanf "%x" of the value.</summary>
    public uint Get_Hex(string section, string entry)
    {
        string value = Get_String(section, entry, "0", 15);
        uint card = 0;
        int i = 0;
        while (i < value.Length && IsSpace(value[i]))
            i++;
        if (i + 1 < value.Length && value[i] == '0' && (value[i + 1] == 'x' || value[i + 1] == 'X'))
            i += 2;
        for (; i < value.Length && Uri.IsHexDigit(value[i]); i++)
            card = card * 16 + (uint)Convert.ToInt32(value[i].ToString(), 16);
        return card;
    }

    /// <summary>C atoi: optional spaces and sign, then digits up to the first other character.</summary>
    public static int Atoi(string? s)
    {
        if (s == null)
            return 0;
        int i = 0;
        while (i < s.Length && IsSpace(s[i]))
            i++;
        bool negative = false;
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            negative = s[i++] == '-';
        long value = 0;
        for (; i < s.Length && char.IsAsciiDigit(s[i]); i++)
            value = unchecked(value * 10 + (s[i] - '0'));
        return unchecked((int)(negative ? -value : value));
    }
}

/// <summary>C strtok over one string: each call skips leading delimiters and returns the next token
/// (null when none are left). Delimiters may change between calls, as in the original readers.</summary>
public sealed class Strtok(string? text)
{
    readonly string s = text ?? "";
    int pos;

    public string? Next(string delimiters)
    {
        while (pos < s.Length && delimiters.Contains(s[pos]))
            pos++;
        if (pos >= s.Length)
            return null;
        int start = pos;
        while (pos < s.Length && !delimiters.Contains(s[pos]))
            pos++;
        string token = s[start..pos];
        if (pos < s.Length)
            pos++;
        return token;
    }
}

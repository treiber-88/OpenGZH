namespace OpenGZH.Formats.Westwood;

/// <summary>Westwood's image compression (LCW, "format 80") and XOR deltas ("format 40").</summary>
public static class WestwoodCompression
{
    /// <summary>
    /// LCW_Uncompress: commands copy literal bytes, fill runs, or copy earlier output (relative for
    /// short copies, absolute offsets from the start of the output otherwise). Returns bytes written.
    /// </summary>
    public static int LcwUncompress(ReadOnlySpan<byte> source, Span<byte> dest)
    {
        int s = 0, d = 0;
        while (s < source.Length && d < dest.Length)
        {
            byte c = source[s++];
            if ((c & 0x80) == 0)
            {
                // 0cccpppp pppppppp: copy (c>>4)+3 bytes from (d - p).
                int count = (c >> 4) + 3;
                int pos = d - (((c & 0x0F) << 8) | source[s++]);
                for (int i = 0; i < count && d < dest.Length; i++)
                    dest[d++] = pos + i >= 0 ? dest[pos + i] : (byte)0;
            }
            else if ((c & 0x40) == 0)
            {
                // 10cccccc: copy c literal bytes (0x80 ends the data).
                int count = c & 0x3F;
                if (count == 0)
                    break;
                for (int i = 0; i < count && d < dest.Length && s < source.Length; i++)
                    dest[d++] = source[s++];
            }
            else if (c == 0xFE)
            {
                int count = source[s] | source[s + 1] << 8;
                byte value = source[s + 2];
                s += 3;
                for (int i = 0; i < count && d < dest.Length; i++)
                    dest[d++] = value;
            }
            else
            {
                int count;
                if (c == 0xFF)
                {
                    count = source[s] | source[s + 1] << 8;
                    s += 2;
                }
                else
                    count = (c & 0x3F) + 3;
                int pos = source[s] | source[s + 1] << 8;
                s += 2;
                for (int i = 0; i < count && d < dest.Length; i++)
                    dest[d++] = pos + i < d ? dest[pos + i] : (byte)0;
            }
        }
        return d;
    }

    /// <summary>Apply_XOR_Delta: skips, XORs literal bytes, or XORs a run of one value into the buffer.</summary>
    public static void ApplyXorDelta(Span<byte> dest, ReadOnlySpan<byte> delta)
    {
        int s = 0, d = 0;
        while (s < delta.Length)
        {
            byte c = delta[s++];
            if (c == 0)
            {
                int count = delta[s++];
                byte value = delta[s++];
                for (int i = 0; i < count && d < dest.Length; i++)
                    dest[d++] ^= value;
            }
            else if ((c & 0x80) == 0)
            {
                for (int i = 0; i < c && d < dest.Length; i++)
                    dest[d++] ^= delta[s++];
            }
            else if (c != 0x80)
                d += c & 0x7F;
            else
            {
                int w = delta[s] | delta[s + 1] << 8;
                s += 2;
                if (w == 0)
                    break;
                if ((w & 0x8000) == 0)
                    d += w;
                else if ((w & 0x4000) == 0)
                {
                    int count = w & 0x3FFF;
                    for (int i = 0; i < count && d < dest.Length; i++)
                        dest[d++] ^= delta[s++];
                }
                else
                {
                    int count = w & 0x3FFF;
                    byte value = delta[s++];
                    for (int i = 0; i < count && d < dest.Length; i++)
                        dest[d++] ^= value;
                }
            }
        }
    }
}

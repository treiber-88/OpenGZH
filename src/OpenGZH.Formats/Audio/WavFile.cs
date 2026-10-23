using System.Buffers.Binary;

namespace OpenGZH.Formats.Audio;

/// <summary>Decoded audio: interleaved signed 16-bit samples.</summary>
public sealed class PcmSound
{
    public required short[] Samples { get; init; }
    public required int Channels { get; init; }
    public required int SampleRate { get; init; }

    public double DurationSeconds => Samples.Length / (double)Math.Max(1, Channels) / Math.Max(1, SampleRate);
}

/// <summary>
/// RIFF WAVE files as the game ships them: 8/16-bit PCM and IMA ADPCM (format 0x11, 4 bits per sample,
/// in blocks with a per-channel header), mono or stereo.
/// </summary>
public static class WavFile
{
    static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
        1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
        7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];

    static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

    public static PcmSound Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF WAVE file.");
        int format = 0, channels = 1, rate = 22050, blockAlign = 0, bits = 16;
        ReadOnlySpan<byte> samples = default;
        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            var id = data.Slice(pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos + 4, 4));
            int body = pos + 8;
            int available = Math.Min(Math.Max(0, size), data.Length - body);
            if (id.SequenceEqual("fmt "u8) && available >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(body + 4, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(body + 12, 2));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                samples = data.Slice(body, available);
                break;
            }
            pos = body + available + (size & 1);
        }
        channels = Math.Max(1, channels);
        short[] pcm = format switch
        {
            1 when bits == 16 => DecodePcm16(samples),
            1 when bits == 8 => DecodePcm8(samples),
            0x11 => DecodeImaAdpcm(samples, channels, blockAlign),
            _ => throw new NotSupportedException($"WAV format {format} ({bits} bits) is not supported."),
        };
        return new PcmSound { Samples = pcm, Channels = channels, SampleRate = rate };
    }

    static short[] DecodePcm16(ReadOnlySpan<byte> data)
    {
        var result = new short[data.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
        return result;
    }

    static short[] DecodePcm8(ReadOnlySpan<byte> data)
    {
        var result = new short[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = (short)((data[i] - 128) << 8);
        return result;
    }

    /// <summary>Microsoft IMA ADPCM: each block starts with a sample and step index per channel, then 4-byte groups per channel.</summary>
    static short[] DecodeImaAdpcm(ReadOnlySpan<byte> data, int channels, int blockAlign)
    {
        if (blockAlign <= 4 * channels)
            blockAlign = 512 * channels;
        int samplesPerBlock = (blockAlign - 4 * channels) * 8 / (4 * channels) + 1;
        int blocks = (data.Length + blockAlign - 1) / blockAlign;
        var output = new List<short>(blocks * samplesPerBlock * channels);
        Span<int> predictor = stackalloc int[channels];
        Span<int> index = stackalloc int[channels];
        var channelSamples = new short[channels][];
        for (int c = 0; c < channels; c++)
            channelSamples[c] = new short[samplesPerBlock];

        for (int start = 0; start < data.Length; start += blockAlign)
        {
            var block = data.Slice(start, Math.Min(blockAlign, data.Length - start));
            if (block.Length < 4 * channels)
                break;
            for (int c = 0; c < channels; c++)
            {
                predictor[c] = BinaryPrimitives.ReadInt16LittleEndian(block.Slice(c * 4, 2));
                index[c] = Math.Clamp((int)block[c * 4 + 2], 0, 88);
                channelSamples[c][0] = (short)predictor[c];
            }
            int count = 1;
            int offset = 4 * channels;
            // Groups of 8 samples per channel: 4 bytes for channel 0, 4 for channel 1, ...
            while (offset + 4 * channels <= block.Length && count < samplesPerBlock)
            {
                for (int c = 0; c < channels; c++)
                {
                    int n = count;
                    for (int b = 0; b < 4; b++)
                    {
                        byte value = block[offset + c * 4 + b];
                        for (int half = 0; half < 2 && n < samplesPerBlock; half++)
                        {
                            int nibble = half == 0 ? value & 0x0F : value >> 4;
                            channelSamples[c][n++] = DecodeNibble(nibble, ref predictor[c], ref index[c]);
                        }
                    }
                }
                offset += 4 * channels;
                count = Math.Min(samplesPerBlock, count + 8);
            }
            for (int i = 0; i < count; i++)
                for (int c = 0; c < channels; c++)
                    output.Add(channelSamples[c][i]);
        }
        return output.ToArray();
    }

    static short DecodeNibble(int nibble, ref int predictor, ref int index)
    {
        int step = StepTable[index];
        int diff = step >> 3;
        if ((nibble & 1) != 0) diff += step >> 2;
        if ((nibble & 2) != 0) diff += step >> 1;
        if ((nibble & 4) != 0) diff += step;
        if ((nibble & 8) != 0) diff = -diff;
        predictor = Math.Clamp(predictor + diff, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[nibble], 0, 88);
        return (short)predictor;
    }
}

using OpenGZH.Core.FileSystem;
using OpenGZH.Formats.Audio;
using Silk.NET.OpenAL;

namespace OpenGZH.Client.Audio;

/// <summary>
/// Speech and music played as a stream: WAV speech is decoded whole into one buffer; MP3 music is
/// decoded a little at a time into a queue of buffers, so long tracks never sit in memory decoded.
/// </summary>
public sealed unsafe class MusicStream
{
    const int BufferCount = 4;
    const int SamplesPerBuffer = 44100 / 2 * 2; // about half a second of stereo

    readonly byte[] data;
    NLayer.MpegFile? mp3;
    PcmSound? wav;
    readonly uint[] buffers = new uint[BufferCount];
    readonly float[] floatScratch = new float[SamplesPerBuffer];
    readonly short[] shortScratch = new short[SamplesPerBuffer];
    bool endOfData;
    bool buffersCreated;

    MusicStream(byte[] data, bool isMp3)
    {
        this.data = data;
        if (isMp3)
            mp3 = new NLayer.MpegFile(new MemoryStream(data, writable: false));
        else
            wav = WavFile.Decode(data);
    }

    public static MusicStream? Open(VirtualFileSystem files, string path)
    {
        if (!files.Exists(path))
            return null;
        var bytes = files.ReadAllBytes(path);
        bool isMp3 = path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || !(bytes.Length > 4 && bytes[0] == 'R' && bytes[1] == 'I');
        return new MusicStream(bytes, isMp3);
    }

    public void Start(AL al, uint source)
    {
        if (!buffersCreated)
        {
            for (int i = 0; i < BufferCount; i++)
                buffers[i] = al.GenBuffer();
            buffersCreated = true;
        }
        if (wav != null)
        {
            var format = wav.Channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;
            fixed (short* p = wav.Samples)
                al.BufferData(buffers[0], format, p, wav.Samples.Length * sizeof(short), wav.SampleRate);
            uint b = buffers[0];
            al.SourceQueueBuffers(source, 1, &b);
            endOfData = true;
        }
        else
        {
            foreach (uint b in buffers)
                if (!Fill(al, b))
                    break;
                else
                {
                    uint q = b;
                    al.SourceQueueBuffers(source, 1, &q);
                }
        }
        al.SourcePlay(source);
    }

    bool Fill(AL al, uint buffer)
    {
        if (mp3 == null || endOfData)
            return false;
        int read = mp3.ReadSamples(floatScratch, 0, floatScratch.Length);
        if (read <= 0)
        {
            endOfData = true;
            return false;
        }
        for (int i = 0; i < read; i++)
            shortScratch[i] = (short)Math.Clamp((int)(floatScratch[i] * 32767f), short.MinValue, short.MaxValue);
        var format = mp3.Channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;
        fixed (short* p = shortScratch)
            al.BufferData(buffer, format, p, read * sizeof(short), mp3.SampleRate);
        return true;
    }

    /// <summary>Refills buffers the source has finished with; restarts the source if it ran dry.</summary>
    public void Pump(AL al, uint source)
    {
        if (al == null || mp3 == null)
            return;
        al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out int processed);
        while (processed-- > 0)
        {
            uint b;
            al.SourceUnqueueBuffers(source, 1, &b);
            if (Fill(al, b))
                al.SourceQueueBuffers(source, 1, &b);
        }
        al.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
        al.GetSourceProperty(source, GetSourceInteger.BuffersQueued, out int queued);
        if (state != (int)SourceState.Playing && queued > 0)
            al.SourcePlay(source);
    }

    /// <summary>All data played and nothing left queued.</summary>
    public bool IsFinished(AL? al, uint source)
    {
        if (al == null)
            return true;
        al.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
        if (state == (int)SourceState.Playing)
            return false;
        al.GetSourceProperty(source, GetSourceInteger.BuffersQueued, out int queued);
        al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out int processed);
        return endOfData && queued - processed <= 0;
    }

    /// <summary>Plays again from the start (music loops forever).</summary>
    public void Restart(AL? al, uint source)
    {
        if (al == null)
            return;
        al.SourceStop(source);
        al.SetSourceProperty(source, SourceInteger.Buffer, 0);
        endOfData = false;
        if (mp3 != null)
            mp3 = new NLayer.MpegFile(new MemoryStream(data, writable: false));
        Start(al, source);
    }

    public void Dispose(AL al, uint source)
    {
        al.SourceStop(source);
        al.SetSourceProperty(source, SourceInteger.Buffer, 0);
        if (buffersCreated)
            foreach (uint b in buffers)
                al.DeleteBuffer(b);
        buffersCreated = false;
        mp3?.Dispose();
    }
}

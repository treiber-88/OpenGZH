using System.Numerics;

namespace OpenGZH.Formats.W3d;

/// <summary>Which part of a bone transform an animation channel drives.</summary>
public enum W3dChannelType
{
    X = 0, Y = 1, Z = 2,
    XRotation = 3, YRotation = 4, ZRotation = 5,
    Quaternion = 6,
}

/// <summary>
/// A skeletal animation. Channels are stored per pivot; sampling returns a translation offset,
/// a rotation and a visibility flag for any (fractional) frame.
///
/// Three storage formats exist in the original data and are all supported:
///   raw            one value per frame,
///   time-coded     keyframes with linear interpolation (or a step when flagged),
///   adaptive delta 4-bit deltas with a per-16-frame scale (decoded to raw on load).
/// </summary>
public sealed class W3dAnimation
{
    public string Name { get; set; } = "";
    public string HierarchyName { get; set; } = "";
    public int FrameCount { get; set; }
    public float FrameRate { get; set; } = 30;

    public float Duration => FrameCount / Math.Max(1f, FrameRate);

    readonly Dictionary<(int Pivot, W3dChannelType Type), IChannel> channels = new();
    readonly Dictionary<int, VisibilityChannel> visibility = new();

    public void AddChannel(int pivot, W3dChannelType type, IChannel channel) => channels[(pivot, type)] = channel;
    public void AddVisibility(int pivot, VisibilityChannel channel) => visibility[pivot] = channel;

    public bool AnimatesPivot(int pivot) =>
        channels.ContainsKey((pivot, W3dChannelType.X)) || channels.ContainsKey((pivot, W3dChannelType.Y)) ||
        channels.ContainsKey((pivot, W3dChannelType.Z)) || channels.ContainsKey((pivot, W3dChannelType.Quaternion));

    public Vector3 GetTranslation(int pivot, float frame) => new(
        Sample(pivot, W3dChannelType.X, frame),
        Sample(pivot, W3dChannelType.Y, frame),
        Sample(pivot, W3dChannelType.Z, frame));

    public Quaternion GetRotation(int pivot, float frame)
    {
        if (!channels.TryGetValue((pivot, W3dChannelType.Quaternion), out var channel))
            return Quaternion.Identity;
        Span<float> q = stackalloc float[4];
        channel.Sample(frame, q);
        return Quaternion.Normalize(new Quaternion(q[0], q[1], q[2], q[3]));
    }

    public bool IsVisible(int pivot, float frame) =>
        !visibility.TryGetValue(pivot, out var channel) || channel.IsVisible((int)frame);

    float Sample(int pivot, W3dChannelType type, float frame)
    {
        if (!channels.TryGetValue((pivot, type), out var channel))
            return 0;
        Span<float> value = stackalloc float[1];
        channel.Sample(frame, value);
        return value[0];
    }

    public interface IChannel
    {
        void Sample(float frame, Span<float> result);
    }

    /// <summary>A value for every frame from FirstFrame to LastFrame; frames outside clamp.</summary>
    public sealed class RawChannel(int firstFrame, int vectorLength, float[] data) : IChannel
    {
        public void Sample(float frame, Span<float> result)
        {
            int frames = data.Length / vectorLength;
            float local = Math.Clamp(frame - firstFrame, 0, frames - 1);
            int a = (int)local;
            int b = Math.Min(a + 1, frames - 1);
            float t = local - a;

            // Quaternions must be interpolated as rotations, not component-wise.
            if (vectorLength == 4)
            {
                var qa = new Quaternion(data[a * 4], data[a * 4 + 1], data[a * 4 + 2], data[a * 4 + 3]);
                var qb = new Quaternion(data[b * 4], data[b * 4 + 1], data[b * 4 + 2], data[b * 4 + 3]);
                var q = Quaternion.Slerp(qa, qb, t);
                result[0] = q.X; result[1] = q.Y; result[2] = q.Z; result[3] = q.W;
                return;
            }

            for (int i = 0; i < vectorLength; i++)
                result[i] = data[a * vectorLength + i] + (data[b * vectorLength + i] - data[a * vectorLength + i]) * t;
        }
    }

    /// <summary>Keyframes at given frames. A key with <see cref="Step"/> set jumps instead of interpolating.</summary>
    public sealed class TimeCodedChannel(int vectorLength, int[] keyFrames, bool[] step, float[] values) : IChannel
    {
        public void Sample(float frame, Span<float> result)
        {
            int last = keyFrames.Length - 1;
            int index = 0;
            while (index < last && keyFrames[index + 1] <= frame)
                index++;

            if (index == last || step[index + 1])
            {
                for (int i = 0; i < vectorLength; i++)
                    result[i] = values[index * vectorLength + i];
                return;
            }

            float t = (frame - keyFrames[index]) / Math.Max(1, keyFrames[index + 1] - keyFrames[index]);
            t = Math.Clamp(t, 0, 1);
            if (vectorLength == 4)
            {
                int a = index * 4, b = (index + 1) * 4;
                var q = Quaternion.Slerp(
                    new Quaternion(values[a], values[a + 1], values[a + 2], values[a + 3]),
                    new Quaternion(values[b], values[b + 1], values[b + 2], values[b + 3]), t);
                result[0] = q.X; result[1] = q.Y; result[2] = q.Z; result[3] = q.W;
                return;
            }

            for (int i = 0; i < vectorLength; i++)
            {
                float a = values[index * vectorLength + i];
                float b = values[(index + 1) * vectorLength + i];
                result[i] = a + (b - a) * t;
            }
        }
    }

    /// <summary>Per-frame visibility of a pivot (used to show/hide meshes during an animation).</summary>
    public sealed class VisibilityChannel(int firstFrame, bool defaultValue, bool[] values)
    {
        public bool IsVisible(int frame)
        {
            int local = frame - firstFrame;
            return local >= 0 && local < values.Length ? values[local] : defaultValue;
        }
    }
}

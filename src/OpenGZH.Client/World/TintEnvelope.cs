using System.Numerics;

namespace OpenGZH.Client.World;

/// <summary>
/// A color that fades in, holds and fades out (the original's TintEnvelope). Added to the lights a
/// model is lit by: dark gray for disabled things, red for frenzy, blue for subdual damage, a quick
/// white flash on selection. Stepped once per 30th of a second, like the original.
/// </summary>
public sealed class TintEnvelope
{
    enum State { Rest, Attack, Decay, Sustain }

    /// <summary>Hold at the peak until released.</summary>
    public const int SustainIndefinitely = int.MaxValue;
    const float FadeRateEpsilon = 0.001f;

    Vector3 attackRate, decayRate, peakColor, currentColor;
    State state = State.Rest;
    int sustainCounter;

    /// <summary>Whether the envelope is changing the color at all.</summary>
    public bool IsEffective { get; private set; }
    public Vector3 Color => currentColor;

    public void Play(Vector3 peak, int attackFrames = 0, int decayFrames = 0, int sustainAtPeak = 0)
    {
        peakColor = peak;
        attackRate = (peakColor - currentColor) / Math.Max(1, attackFrames);
        decayRate = peakColor * (-1f / Math.Max(1, decayFrames));
        state = State.Attack;
        sustainCounter = sustainAtPeak;
        IsEffective = true;
        if ((currentColor - peakColor).Length() <= FadeRateEpsilon)
            state = State.Sustain;
    }

    /// <summary>Head back to no tint.</summary>
    public void Release() => state = State.Decay;

    public void Rest()
    {
        state = State.Rest;
        currentColor = Vector3.Zero;
        IsEffective = false;
    }

    public void Update()
    {
        switch (state)
        {
            case State.Rest:
                currentColor = Vector3.Zero;
                IsEffective = false;
                break;
            case State.Decay:
                if (decayRate.Length() > currentColor.Length() || currentColor.Length() <= FadeRateEpsilon)
                {
                    state = State.Rest;
                    IsEffective = false;
                }
                else
                {
                    currentColor += decayRate;
                    IsEffective = true;
                }
                break;
            case State.Attack:
                var delta = currentColor - peakColor;
                if (attackRate.Length() > delta.Length() || delta.Length() <= FadeRateEpsilon)
                    state = sustainCounter != 0 ? State.Sustain : State.Decay;
                else
                {
                    currentColor += attackRate;
                    IsEffective = true;
                }
                break;
            case State.Sustain:
                if (sustainCounter == SustainIndefinitely)
                    break;
                if (sustainCounter > 0)
                    sustainCounter--;
                else
                    Release();
                break;
        }
    }
}

namespace OpenGZH.Logic.Core;

/// <summary>
/// Deterministic random numbers for the simulation. Every player's machine must make the same
/// "random" choices in the same order, so logic code must only use this, never System.Random.
/// (xorshift128+; seeded per game.)
/// </summary>
public sealed class GameRandom
{
    ulong s0, s1;

    public GameRandom(ulong seed)
    {
        // SplitMix64 to spread the seed over both state words.
        s0 = SplitMix(ref seed);
        s1 = SplitMix(ref seed);
        if ((s0 | s1) == 0)
            s1 = 1;
    }

    static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextULong()
    {
        ulong x = s0, y = s1;
        s0 = y;
        x ^= x << 23;
        s1 = x ^ y ^ (x >> 17) ^ (y >> 26);
        return s1 + y;
    }

    /// <summary>Integer in [min, max] (inclusive, like the original GameLogicRandomValue).</summary>
    public int Int(int min, int max) => max <= min ? min : min + (int)(NextULong() % (ulong)(max - min + 1));

    /// <summary>Float in [min, max].</summary>
    public float Float(float min, float max) => min + (max - min) * (float)((NextULong() >> 11) * (1.0 / (1UL << 53)));

    public bool Chance(float probability) => Float(0, 1) < probability;
}

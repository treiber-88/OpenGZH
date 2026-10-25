namespace OpenGZH.TiberianDawn.Logic;

/// <summary>
/// Tiberian Dawn's three random number sources, as the original computes them:
/// <list type="bullet">
/// <item>IRandom / Random_Pick (WIN32LIB IRANDOM.CPP): the C library's rand() (Microsoft's: seed * 214013 + 2531011),
/// masked to the range and retried until it fits. Seeded with srand(Seed) at scenario start.</item>
/// <item>Random() (IRANDOM.CPP's assembly): a byte from a shifting 32-bit register (RandNumb).</item>
/// <item>Sim_Random (RAND.CPP): the next byte of a fixed 256-entry table.</item>
/// </list>
/// </summary>
public sealed class TdRandom
{
    uint crtSeed = 1;
    uint randNumb = 0x12349876;
    int simRandIndex;

    /// <summary>srand: seeds the C library generator.</summary>
    public void Srand(uint seed) => crtSeed = seed;

    /// <summary>rand: Microsoft's C runtime generator (0..32767).</summary>
    public int Rand()
    {
        crtSeed = crtSeed * 214013 + 2531011;
        return (int)((crtSeed >> 16) & 0x7FFF);
    }

    /// <summary>Get_Random_Mask: all-ones mask just covering the value (1 for 0).</summary>
    static int RandomMask(int maxval)
    {
        if (maxval == 0)
            return 1;
        int bit = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)maxval); // bsr
        return (1 << ((bit + 1) & 31)) - 1; // x86 shl masks the count to 5 bits
    }

    /// <summary>IRandom: a number from minval to maxval inclusive.</summary>
    public int IRandom(int minval, int maxval)
    {
        if (minval > maxval)
            (minval, maxval) = (maxval, minval);
        int mask = RandomMask(maxval - minval);
        int num;
        while ((num = (Rand() & mask) + minval) > maxval)
        {
        }
        return num;
    }

    /// <summary>The RandNumb seed for Random() (INIT.CPP sets it to the scenario Seed).</summary>
    public uint RandNumb { get => randNumb; set => randNumb = value; }

    /// <summary>Random(): IRANDOM.CPP's assembly, step by step on the four bytes of RandNumb.</summary>
    public byte Random()
    {
        byte b0 = (byte)randNumb, b1 = (byte)(randNumb >> 8), b2 = (byte)(randNumb >> 16), b3 = (byte)(randNumb >> 24);
        int al = b0;
        al >>= 1;                       // shr al,1
        int carry = al & 1;             // the second shr's carry is bit 1 of b0
        al >>= 1;                       // shr al,1
        int newCarry = b2 >> 7;         // rcl [esi+2],1
        b2 = (byte)((b2 << 1) | carry);
        carry = newCarry;
        newCarry = b1 >> 7;             // rcl [esi+1],1
        b1 = (byte)((b1 << 1) | carry);
        carry = newCarry ^ 1;           // cmc
        int diff = al - b0 - carry;     // sbb al,[esi]
        al = diff & 0xFF;
        carry = al & 1;                 // shr al,1
        al >>= 1;
        b0 = (byte)((b0 >> 1) | (carry << 7)); // rcr [esi],1
        randNumb = (uint)(b0 | b1 << 8 | b2 << 16 | b3 << 24);
        return (byte)(b0 ^ b1);
    }

    static readonly byte[] RandVals =
    [
        0x47, 0xce, 0xc6, 0x6e, 0xd7, 0x9f, 0x98, 0x29, 0x92, 0x0c, 0x74, 0xa2, 0x65, 0x20, 0x4b, 0x4f, 0x1e, 0xed, 0x3a, 0xdf, 0xa5, 0x7d, 0xb5, 0xc8,
        0x86, 0x01, 0x81, 0xca, 0xf1, 0x17, 0xd6, 0x23, 0xe1, 0xbd, 0x0e, 0xe4, 0x62, 0xfa, 0xd9, 0x5c, 0x68, 0xf5, 0x7f, 0xdc, 0xe7, 0xb9, 0xc4, 0xb3,
        0x7a, 0xd8, 0x06, 0x3e, 0xeb, 0x09, 0x1a, 0x31, 0x3f, 0x46, 0x28, 0x12, 0xf0, 0x10, 0x84, 0x76, 0x3b, 0xc5, 0x53, 0x18, 0x14, 0x73, 0x7e, 0x59,
        0x48, 0x93, 0xaa, 0x1d, 0x5d, 0x79, 0x24, 0x61, 0x1b, 0xfd, 0x2b, 0xa8, 0xc2, 0xdb, 0xe8, 0x2a, 0xb0, 0x25, 0x95, 0xab, 0x96, 0x83, 0xfc, 0x5f,
        0x9c, 0x32, 0x78, 0x9a, 0x9e, 0xe2, 0x8e, 0x35, 0x4c, 0x41, 0xa1, 0x69, 0x5a, 0xfe, 0xa7, 0xa4, 0xf6, 0x6d, 0xc1, 0x58, 0x0a, 0xcf, 0xea, 0xc3,
        0xba, 0x85, 0x99, 0x8d, 0x36, 0xb6, 0xdd, 0xd3, 0x04, 0xe6, 0x45, 0x0d, 0x60, 0xae, 0xa3, 0x22, 0x4d, 0xe9, 0xc9, 0x9b, 0xb7, 0x0f, 0x02, 0x42,
        0xf9, 0x0b, 0x8f, 0x43, 0x44, 0x87, 0x70, 0xbe, 0xe3, 0xf8, 0xee, 0xa9, 0xbc, 0xc0, 0x67, 0x33, 0x16, 0x37, 0x57, 0xad, 0x5e, 0x9d, 0x64, 0x40,
        0x54, 0x05, 0x2c, 0xe0, 0xb2, 0x97, 0x08, 0xaf, 0x75, 0x8a, 0x5b, 0xfb, 0x4e, 0xbf, 0x91, 0xf3, 0xcb, 0x7c, 0x63, 0xef, 0x89, 0x52, 0x6c, 0x2f,
        0x21, 0x4a, 0xf7, 0xcd, 0x2e, 0xf4, 0xc7, 0x6f, 0x19, 0xb1, 0x66, 0xcc, 0x90, 0x8c, 0x50, 0x51, 0x26, 0x7b, 0xda, 0x49, 0x80, 0x30, 0x55, 0x1f,
        0xd2, 0xb4, 0xd1, 0xd5, 0x6b, 0xf2, 0x72, 0xbb, 0x13, 0x3d, 0xff, 0x15, 0x38, 0xe5, 0xd4, 0xde, 0x2d, 0x27, 0x94, 0xa0, 0xd0, 0x39, 0x82, 0x8b,
        0x03, 0xac, 0x3c, 0x34, 0x77, 0xb8, 0xec, 0x00, 0x07, 0x1c, 0x88, 0xa6, 0x56, 0x11, 0x71, 0x6a,
    ];

    /// <summary>The Sim_Random table position (SimRandIndex; the original advances its low byte).</summary>
    public int SimRandIndex { get => simRandIndex; set => simRandIndex = value; }

    public int Sim_Random()
    {
        simRandIndex = (simRandIndex & ~0xFF) | ((simRandIndex + 1) & 0xFF);
        return RandVals[simRandIndex & 0xFF];
    }

    /// <summary>Sim_IRandom: Fixed_To_Cardinal(max - min, Sim_Random()) + min.</summary>
    public int Sim_IRandom(int minval, int maxval) => (int)TdMath.FixedToCardinal((uint)(maxval - minval), (uint)Sim_Random()) + minval;
}

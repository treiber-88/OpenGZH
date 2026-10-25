namespace OpenGZH.TiberianDawn.Logic;

/// <summary>
/// Tiberian Dawn's world measurements and their maths, ported from FUNCTION.H, COORD.CPP, CONST.CPP
/// and MiscAsm.cpp (the Win32 build's assembly routines), so movement and facing behave exactly
/// like the original.
///
/// A COORDINATE packs Y in the high 16 bits and X in the low 16 bits, in leptons (256 per cell;
/// the cell is the high byte, the position inside it the low byte). A CELL is y * 64 + x on the
/// 64x64 map. A DirType is 0..255 clockwise from north.
/// </summary>
public static class TdMath
{
    public const int MapCellW = 64, MapCellH = 64, MapCellTotal = MapCellW * MapCellH;
    public const int CellLepton = 256, CellPixel = 24;
    public const short RefreshEol = 32767;

    // ---- Coordinates and cells ----

    public static uint XyCoord(int x, int y) => (uint)((ushort)y << 16 | (ushort)x);
    public static int CoordX(uint coord) => (short)(coord & 0xFFFF);
    public static int CoordY(uint coord) => (short)(coord >> 16);
    public static int CoordXLepton(uint coord) => (int)(coord & 0xFF);
    public static int CoordYLepton(uint coord) => (int)((coord >> 16) & 0xFF);
    public static int CellX(int cell) => cell & 0x3F;
    public static int CellY(int cell) => (cell >> 6) & 0x3F;
    public static int XyCell(int x, int y) => (y << 6) | x;

    /// <summary>
    /// XY_Cell used as an offset with negative values, exactly as the original computes it:
    /// (short)((y &lt;&lt; 6) | x). (With a negative x the OR sets the high bits; the original has that quirk.)
    /// </summary>
    public static int XyCellOffset(int x, int y) => (short)((y << 6) | x);

    /// <summary>The cell a coordinate is in (Coord_Cell: the high byte of each axis).</summary>
    public static int CoordCell(uint coord) => XyCell((int)((coord >> 8) & 0x3F), (int)((coord >> 24) & 0x3F));

    /// <summary>The centre of a cell (Cell_Coord).</summary>
    public static uint CellCoord(int cell) => (uint)(((cell & 0x0FC0) << 2 | 0x80) << 16 | (((cell & 0x003F) << 1) + 1) << 7);

    /// <summary>Coord_Add / Coord_Sub: per-axis 16-bit addition.</summary>
    public static uint CoordAdd(uint a, uint b) => XyCoord(CoordX(a) + CoordX(b), CoordY(a) + CoordY(b));
    public static uint CoordSub(uint a, uint b) => XyCoord(CoordX(a) - CoordX(b), CoordY(a) - CoordY(b));

    /// <summary>Coord_Snap: the centre of the cell the coordinate is in.</summary>
    public static uint CoordSnap(uint coord) => (uint)(((coord >> 16) & 0xFF00 | 0x80) << 16 | (coord & 0xFF00) | 0x80);

    /// <summary>Coord_Mid: half way between two coordinates.</summary>
    public static uint CoordMid(uint a, uint b) =>
        (uint)((((a >> 16) + (b >> 16)) >> 1) << 16 | (((a & 0xFFFF) + (b & 0xFFFF)) >> 1) & 0xFFFF);

    public static int LeptonToPixel(int lepton) => ((lepton * CellPixel) + (CellLepton / 2) - (lepton < 0 ? CellLepton - 1 : 0)) / CellLepton;
    public static int PixelToLepton(int pixel) => ((pixel * CellLepton) + (CellPixel / 2) - (pixel < 0 ? CellPixel - 1 : 0)) / CellPixel;
    public static int CellToLepton(int cell) => cell << 8;

    /// <summary>XYP_COORD (the macro): pixel offsets as leptons, summed — so a negative x borrows from y.</summary>
    public static uint XYP_COORD(int x, int y) => (uint)((x * CellLepton) / CellPixel + (((y * CellLepton) / CellPixel) << 16));

    /// <summary>XYPixel_Coord: pixel offsets as leptons, packed with MAKE_LONG (no borrow).</summary>
    public static uint XYPixel_Coord(int x, int y) => (uint)((((y * CellLepton) / CellPixel) << 16) | (((x * CellLepton) / CellPixel) & 0xFFFF));

    /// <summary>XYP_Coord (the inline function, not the XYP_COORD macro): pixel offsets as a coordinate.</summary>
    public static uint XypCoord(int x, int y) => XyCoord(PixelToLepton(x), PixelToLepton(y));
    public static int LeptonToCell(int lepton) => (int)((uint)(lepton + 0x0080) >> 8);

    // ---- Facings ----

    /// <summary>FacingType: the eight compass points, N = 0 clockwise.</summary>
    public enum Facing { N, NE, E, SE, S, SW, W, NW }

    /// <summary>AdjacentCell: the cell offset to each neighbour.</summary>
    public static readonly int[] AdjacentCell = [-MapCellW, -(MapCellW - 1), 1, MapCellW + 1, MapCellW, MapCellW - 1, -1, -(MapCellW + 1)];

    /// <summary>Adjacent_Cell(CELL, FacingType): the neighbouring cell (CELL is a signed short; no masking).</summary>
    public static int AdjacentCellOf(int cell, FacingType dir) => (short)(cell + AdjacentCell[(int)dir]);

    /// <summary>FacingType + int / FacingType - int: the original's operators wrap to 0..7.</summary>
    public static FacingType FacingAdd(FacingType f, int n) => (FacingType)(((int)f + n) & 0x07);
    public static FacingType FacingSub(FacingType f, int n) => (FacingType)(((int)f - n) & 0x07);

    /// <summary>Dir_Facing as a FacingType.</summary>
    public static FacingType DirFacingType(byte dir) => (FacingType)(((byte)(dir + 0x10)) >> 5);

    static readonly short[][] MoveSpillage =
    [
        [0, -MapCellW, RefreshEol],
        [0, -MapCellW, 1, -(MapCellW - 1), RefreshEol],
        [0, 1, RefreshEol],
        [0, 1, MapCellW, MapCellW + 1, RefreshEol],
        [0, MapCellW, RefreshEol],
        [0, -1, MapCellW, MapCellW - 1, RefreshEol],
        [0, -1, RefreshEol],
        [0, -1, -MapCellW, -(MapCellW + 1), RefreshEol],
        [0, RefreshEol],
    ];

    static readonly sbyte[] SpillTable = [8, 6, 2, -1, 0, 7, 1, -1, 4, 5, 3, -1, -1, -1, -1, -1];

    static readonly short[] Gigundo = BuildGigundo();

    static short[] BuildGigundo()
    {
        var list = new List<short>();
        for (int row = -2; row <= 2; row++)
            for (int col = -2; col <= 2; col++)
                list.Add((short)(row * MapCellW + col));
        list.Add(RefreshEol);
        return [.. list];
    }

    static readonly byte[] Pixel2LeptonTable =
    [
        0x00, 0x0B, 0x15, 0x20, 0x2B, 0x35, 0x40, 0x4B, 0x55, 0x60, 0x6B, 0x75,
        0x80, 0x8B, 0x95, 0xA0, 0xAB, 0xB5, 0xC0, 0xCB, 0xD5, 0xE0, 0xEB, 0xF5,
    ];

    /// <summary>Coord_Spillage_List (COORD.CPP): the cells (offsets) an object of a pixel size at a
    /// coordinate overlaps. Up to a cell: by which edges it crosses; up to two: worked out; above: 5x5.</summary>
    public static short[] Coord_Spillage_List(uint coord, int maxsize)
    {
        const int iconPixel = (int)TdDefines.ICON_PIXEL_W;
        if (maxsize > iconPixel * 2)
            return Gigundo;

        if (maxsize > iconPixel)
        {
            maxsize = Math.Min(maxsize, iconPixel * 2) / 2;
            int mx = (int)FixedToCardinal((uint)iconPixel, (uint)CoordXLepton(coord));
            int my = (int)FixedToCardinal((uint)iconPixel, (uint)CoordYLepton(coord));
            int left = mx - maxsize, right = mx + maxsize, top = my - maxsize, bottom = my + maxsize;
            var manual = new List<short> { 0 };
            if (left < 0) manual.Add(-1);
            if (right >= iconPixel) manual.Add(1);
            if (top < 0) manual.Add(-MapCellW);
            if (bottom >= iconPixel) manual.Add(MapCellW);
            if (left < 0 && top < 0) manual.Add(-(MapCellW + 1));
            if (right >= iconPixel && bottom >= iconPixel) manual.Add(MapCellW + 1);
            if (left < 0 && bottom >= iconPixel) manual.Add(MapCellW - 1);
            if (right >= iconPixel && top < 0) manual.Add(-(MapCellW - 1));
            manual.Add(RefreshEol);
            return [.. manual];
        }

        int posval = Pixel2LeptonTable[(iconPixel - maxsize) / 2];
        int x = CoordXLepton(coord) - 0x0080;
        int y = CoordYLepton(coord) - 0x0080;
        int index = 0;
        if (y > posval) index |= 0x08;
        if (y < -posval) index |= 0x04;
        if (x > posval) index |= 0x02;
        if (x < -posval) index |= 0x01;
        // An impossible combination (-1) would read before the table in the original; it can't occur.
        return MoveSpillage[Math.Max((int)SpillTable[index], 0)];
    }

    /// <summary>StoppingCoordAbs (CONST.CPP): the five spots in a cell (centre, then the quarters).</summary>
    public static readonly uint[] StoppingCoordAbs = [0x00800080, 0x00400040, 0x004000C0, 0x00C00040, 0x00C000C0];

    /// <summary>AdjacentCoord: one cell's step in each direction.</summary>
    public static readonly uint[] AdjacentCoord = [0xFF000000, 0xFF000100, 0x00000100, 0x01000100, 0x01000000, 0x0100FF00, 0x0000FF00, 0xFF00FF00];

    public static byte FacingDir(Facing facing) => (byte)((int)facing << 5);

    /// <summary>Dir_Facing: a direction rounded to the nearest compass point (Facing8 table).</summary>
    public static Facing DirFacing(byte dir) => (Facing)(((dir + 16) >> 5) & 7);

    /// <summary>Facing32: CONST.CPP's table (with its compensation for the art's 45 degree distortion).</summary>
    public static readonly byte[] Facing32 =
    [
        0,0,0,0,0,1,1,1,1,1,1,1,1,1,2,2,2,2,2,2,2,2,3,3,3,3,3,3,3,3,3,3,
        3,4,4,4,4,4,4,5,5,5,5,5,5,5,6,6,6,6,6,6,6,7,7,7,7,7,7,7,8,8,8,8,
        8,8,8,9,9,9,9,9,9,9,10,10,10,10,10,10,10,11,11,11,11,11,11,11,12,12,12,12,12,12,12,12,
        13,13,13,13,13,13,13,13,14,14,14,14,14,14,14,14,14,15,15,15,15,15,15,15,15,15,16,16,16,16,16,16,
        16,16,16,16,16,17,17,17,17,17,17,17,17,17,18,18,18,18,18,18,18,18,18,19,19,19,19,19,19,19,19,19,
        19,20,20,20,20,20,20,21,21,21,21,21,21,21,22,22,22,22,22,22,22,23,23,23,23,23,23,23,24,24,24,24,
        24,24,24,25,25,25,25,25,25,25,26,26,26,26,26,26,26,27,27,27,27,27,27,27,28,28,28,28,28,28,28,28,
        29,29,29,29,29,29,29,29,30,30,30,30,30,30,30,30,30,31,31,31,31,31,31,31,31,31,0,0,0,0,0,0,
    ];

    /// <summary>TechnoClass::BodyShape: 32-step facing to art frame (the art turns the other way).</summary>
    public static readonly int[] BodyShape = [0, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1];

    /// <summary>Dir_Diff: the signed turn from dir1 to dir2 (-128..127).</summary>
    public static int DirDiff(byte dir1, byte dir2) => (sbyte)dir2 - (sbyte)dir1;

    /// <summary>Desired_Facing256 (MiscAsm.cpp): the direction from one point to another.</summary>
    public static byte DesiredFacing256(int srcX, int srcY, int dstX, int dstY)
    {
        uint ebx = 0;
        long ecxSigned = dstX - srcX;
        uint ecx;
        if (ecxSigned < 0)
        {
            ecx = (uint)-ecxSigned;
            ebx = 0b11000000; // leftward
        }
        else
            ecx = (uint)ecxSigned;
        long eaxSigned = srcY - dstY;
        uint eax;
        if (eaxSigned < 0)
        {
            ebx ^= 0b01000000; // downward
            eax = (uint)-eaxSigned;
        }
        else
            eax = (uint)eaxSigned;
        uint edx = (ebx & 0b01000000) ^ 0b01000000;
        if (!(eax < ecx))
        {
            (eax, ecx) = (ecx, eax);
            edx ^= 0b01000000; // closer to the Y axis
        }
        uint pushed = edx;
        if ((eax & 0xFFFFFF00) == 0)
            while ((ecx & 0xFFFFFF00) != 0)
            {
                ecx >>= 1;
                eax >>= 1;
            }
        uint result;
        if (ecx == 0)
            result = 0xFFFFFFFF;
        else
            result = (uint)(((ulong)eax << 8) / ecx);
        result >>= 3;
        if (pushed != 0)
        {
            pushed--;
            result = (uint)-(int)result;
        }
        result += pushed + ebx;
        return (byte)(result & 0xFF);
    }

    static readonly byte[] NewFacing8 = [1, 2, 1, 0, 7, 6, 7, 0, 3, 2, 3, 4, 5, 6, 5, 4];

    /// <summary>Desired_Facing8 (MiscAsm.cpp): the direction to the nearest compass point.</summary>
    public static byte DesiredFacing8(int x1, int y1, int x2, int y2)
    {
        uint index = 0;
        long dy = y1 - y2;
        if (dy < 0)
        {
            index++;
            dy = -dy;
        }
        index <<= 1;
        long dx = x2 - x1;
        if (dx < 0)
        {
            index++;
            dx = -dx;
        }
        uint minor = (uint)dx, major = (uint)dy;
        bool carry = minor < major;     // cmp ecx,edx: carry when x < y
        if (!carry)
            (minor, major) = (major, minor);
        index = index << 1 | (carry ? 1u : 0u);
        uint half = (major + 1) >> 1;
        bool closeToMajor = minor < half;
        index = index << 1 | (closeToMajor ? 1u : 0u);
        return (byte)(NewFacing8[index] << 5);
    }

    public static byte Direction(uint from, uint to) => DesiredFacing256(CoordX(from), CoordY(from), CoordX(to), CoordY(to));
    public static byte Direction8(uint from, uint to) => DesiredFacing8(CoordX(from), CoordY(from), CoordX(to), CoordY(to));
    public static byte CellDirection(int from, int to) => DesiredFacing8(CellX(from), CellY(from), CellX(to), CellY(to));

    // ---- Distance and movement ----

    /// <summary>Distance_Coord: the larger axis difference plus half the smaller (in leptons).</summary>
    public static int Distance(uint a, uint b)
    {
        int dx = Math.Abs((short)((short)(a & 0xFFFF) - (short)(b & 0xFFFF)));
        int dy = Math.Abs((short)((short)(a >> 16) - (short)(b >> 16)));
        if (!(dy > dx))
            (dy, dx) = (dx, dy);
        return (ushort)(dy + (dx >> 1));
    }

    /// <summary>Distance(CELL, CELL): the same rule in whole cells.</summary>
    public static int CellDistance(int a, int b)
    {
        int dy = Math.Abs(CellY(a) - CellY(b)), dx = Math.Abs(CellX(a) - CellX(b));
        return dy > dx ? dy + (dx >> 1) : dx + (dy >> 1);
    }

    static readonly sbyte[] CosTable = BuildTable(
    [
        0x00,0x03,0x06,0x09,0x0c,0x0f,0x12,0x15,0x18,0x1b,0x1e,0x21,0x24,0x27,0x2a,0x2d,
        0x30,0x33,0x36,0x39,0x3b,0x3e,0x41,0x43,0x46,0x49,0x4b,0x4e,0x50,0x52,0x55,0x57,
        0x59,0x5b,0x5e,0x60,0x62,0x64,0x65,0x67,0x69,0x6b,0x6c,0x6e,0x6f,0x71,0x72,0x74,
        0x75,0x76,0x77,0x78,0x79,0x7a,0x7b,0x7b,0x7c,0x7d,0x7d,0x7e,0x7e,0x7e,0x7e,0x7e,
        0x7f,0x7e,0x7e,0x7e,0x7e,0x7e,0x7d,0x7d,0x7c,0x7b,0x7b,0x7a,0x79,0x78,0x77,0x76,
        0x75,0x74,0x72,0x71,0x70,0x6e,0x6c,0x6b,0x69,0x67,0x66,0x64,0x62,0x60,0x5e,0x5b,
        0x59,0x57,0x55,0x52,0x50,0x4e,0x4b,0x49,0x46,0x43,0x41,0x3e,0x3b,0x39,0x36,0x33,
        0x30,0x2d,0x2a,0x27,0x24,0x21,0x1e,0x1b,0x18,0x15,0x12,0x0f,0x0c,0x09,0x06,0x03,
        0x00,0xfd,0xfa,0xf7,0xf4,0xf1,0xee,0xeb,0xe8,0xe5,0xe2,0xdf,0xdc,0xd9,0xd6,0xd3,
        0xd0,0xcd,0xca,0xc7,0xc5,0xc2,0xbf,0xbd,0xba,0xb7,0xb5,0xb2,0xb0,0xae,0xab,0xa9,
        0xa7,0xa5,0xa2,0xa0,0x9e,0x9c,0x9a,0x99,0x97,0x95,0x94,0x92,0x91,0x8f,0x8e,0x8c,
        0x8b,0x8a,0x89,0x88,0x87,0x86,0x85,0x85,0x84,0x83,0x83,0x82,0x82,0x82,0x82,0x82,
        0x82,0x82,0x82,0x82,0x82,0x82,0x83,0x83,0x84,0x85,0x85,0x86,0x87,0x88,0x89,0x8a,
        0x8b,0x8c,0x8e,0x8f,0x90,0x92,0x94,0x95,0x97,0x99,0x9a,0x9c,0x9e,0xa0,0xa2,0xa5,
        0xa7,0xa9,0xab,0xae,0xb0,0xb2,0xb5,0xb7,0xba,0xbd,0xbf,0xc2,0xc5,0xc7,0xca,0xcd,
        0xd0,0xd3,0xd6,0xd9,0xdc,0xdf,0xe2,0xe5,0xe8,0xeb,0xee,0xf1,0xf4,0xf7,0xfa,0xfd,
    ]);

    // The sine table is the cosine table a quarter turn on (as in COORD.CPP).
    static readonly sbyte[] SinTable = Enumerable.Range(0, 256).Select(i => CosTable[(i + 64) & 0xFF]).ToArray();

    static sbyte[] BuildTable(int[] values) => values.Select(v => (sbyte)(byte)v).ToArray();

    /// <summary>calcx / calcy (COORD.CPP): table value times distance, scaled by 1/128 into 16 bits.</summary>
    static int Calc(sbyte table, int distance)
    {
        int product = (short)table * (short)(ushort)distance;
        return (short)(((product << 1) >> 8) & 0xFFFF);
    }

    /// <summary>Move_Point: moves a point a distance in a direction (Y grows downward).</summary>
    public static void MovePoint(ref short x, ref short y, byte dir, int distance)
    {
        x = (short)(x + Calc(CosTable[dir], distance));
        y = (short)(y - Calc(SinTable[dir], distance));
    }

    /// <summary>Coord_Move: a coordinate moved a distance in a direction.</summary>
    public static uint CoordMove(uint start, byte dir, int distance)
    {
        short x = (short)CoordX(start), y = (short)CoordY(start);
        MovePoint(ref x, ref y, dir, distance);
        return XyCoord(x, y);
    }

    /// <summary>Adjacent_Cell for a coordinate: the centre of the neighbouring cell that way.</summary>
    public static uint AdjacentCellCoord(uint coord, Facing facing) => CoordSnap(CoordAdd(AdjacentCoord[(int)facing & 7], coord));

    // ---- Fixed point (0..256 = 0..100%) ----

    /// <summary>Cardinal_To_Fixed: cardinal / base as a 0..256 fraction (0xFFFF if base is 0).</summary>
    public static uint CardinalToFixed(uint basis, uint cardinal) => basis == 0 ? 0xFFFF : (cardinal << 8) / basis;

    /// <summary>Fixed_To_Cardinal: base * fixed / 256, rounded (0xFFFF if it overflows 24 bits).</summary>
    public static uint FixedToCardinal(uint basis, uint fixedValue)
    {
        uint value = basis * fixedValue + 0x80;
        return (value & 0xFF000000) != 0 ? 0xFFFF : value >> 8;
    }
}

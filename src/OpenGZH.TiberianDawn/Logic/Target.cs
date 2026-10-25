namespace OpenGZH.TiberianDawn.Logic;

/// <summary>
/// TARGET (TARGET.H): a 16-bit reference to anything — the top 4 bits say what kind (cell, unit,
/// building...), the low 12 bits which one (a cell number or a heap slot). Kept in a uint here,
/// always truncated to 16 bits as the original's unsigned short.
/// </summary>
public static class Target
{
    public const uint TARGET_NONE = 0;
    public const int TARGET_MANTISSA = 12;
    public const uint TARGET_MANTISSA_MASK = (1u << TARGET_MANTISSA) - 1;

    public static KindType Target_Kind(uint a) => (KindType)((a & 0xFFFF) >> TARGET_MANTISSA);
    public static int Target_Value(uint a) => (int)(a & TARGET_MANTISSA_MASK);

    public static bool Is_Target_Team(uint a) => Target_Kind(a) == KindType.KIND_TEAM;
    public static bool Is_Target_TeamType(uint a) => Target_Kind(a) == KindType.KIND_TEAMTYPE;
    public static bool Is_Target_Trigger(uint a) => Target_Kind(a) == KindType.KIND_TRIGGER;
    public static bool Is_Target_Infantry(uint a) => Target_Kind(a) == KindType.KIND_INFANTRY;
    public static bool Is_Target_Bullet(uint a) => Target_Kind(a) == KindType.KIND_BULLET;
    public static bool Is_Target_Terrain(uint a) => Target_Kind(a) == KindType.KIND_TERRAIN;
    public static bool Is_Target_Cell(uint a) => Target_Kind(a) == KindType.KIND_CELL;
    public static bool Is_Target_Unit(uint a) => Target_Kind(a) == KindType.KIND_UNIT;
    public static bool Is_Target_Building(uint a) => Target_Kind(a) == KindType.KIND_BUILDING;
    public static bool Is_Target_Template(uint a) => Target_Kind(a) == KindType.KIND_TEMPLATE;
    public static bool Is_Target_Aircraft(uint a) => Target_Kind(a) == KindType.KIND_AIRCRAFT;
    public static bool Is_Target_Animation(uint a) => Target_Kind(a) == KindType.KIND_ANIMATION;

    public static uint Build_Target(KindType kind, int value) => (ushort)(((uint)kind << TARGET_MANTISSA) | (uint)value);

    /// <summary>As_Target(CELL): (a negative cell sign-extends over the kind, as the original's OR does).</summary>
    public static uint As_Target(int cell) => (ushort)(((uint)KindType.KIND_CELL << TARGET_MANTISSA) | (uint)(short)cell);
}

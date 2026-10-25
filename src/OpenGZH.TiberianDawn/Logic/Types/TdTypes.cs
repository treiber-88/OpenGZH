using OpenGZH.Core.Rules;

namespace OpenGZH.TiberianDawn.Logic.Types;

// The type classes of TYPE.H. Each holds the shared, unchanging data of one kind of thing (the
// Medium Tank, a tree, a road template); field names match the rules files (mods/tiberiandawn/rules),
// which were converted from the original constructor tables. Methods are ported from the ...DATA.CPP
// files; ones that need the running game take it as a parameter.

/// <summary>SpecialClass: the special game options (SPECIAL.H, with Init()'s defaults).</summary>
public sealed class SpecialOptions
{
    public bool IsScrollMod, IsGross, IsEasy, IsDifficult, IsSpeedBuild, IsDefenderAdvantage = true, IsVisibleTarget, IsVariation;
    public bool IsJurassic, IsJuvenile, IsSmartDefense, IsTreeTarget, IsMCVDeploy, IsVisceroids, IsMonoEnabled, IsInert, IsShowPath;
    public bool IsThreePoint, IsTGrowth = true, IsTSpread = true, IsTFast = true, IsRoad, IsScatter, IsCaptureTheFlag, IsNamed;
    public bool IsFromInstall, IsSeparate, IsFromWChat, IsEarlyWin, ModernBalance;
}

/// <summary>WeaponTypeClass: what a weapon fires, its damage, rate of fire, range, sound and muzzle animation.</summary>
public sealed class WeaponTypeClass
{
    public int Id;
    public BulletType Fires = BulletType.BULLET_NONE;
    public int Attack, ROF, Range;
    public VocType Sound = VocType.VOC_NONE;
    public AnimType Anim = AnimType.ANIM_NONE;
}

/// <summary>WarheadTypeClass: spread, what it destroys, and damage modifiers by armor (256 = full).</summary>
public sealed class WarheadTypeClass
{
    public int Id;
    public int SpreadFactor;
    public bool IsWallDestroyer, IsWoodDestroyer, IsTiberiumDestroyer;
    public int[] Modifier = [];
}

/// <summary>GroundType: per land type, the radar colour, movement cost per speed type, and buildability.</summary>
public sealed class GroundType
{
    public int Id;
    public string Color = "";
    public int[] Cost = [];
    public bool Build;
}

/// <summary>HouseTypeClass: a side's name, colours, remap table and biases.</summary>
public sealed class HouseTypeClass
{
    public HousesType House;
    public string IniName = "", Suffix = "", RemapTable = "", Prefix = "";
    public int FullName, Lemon;
    public PlayerColorType RemapColor;
    public float FirepowerBias = 1, GroundspeedBias = 1, AirspeedBias = 1, ArmorBias = 1, ROFBias = 1, CostBias = 1, BuildSpeedBias = 1;
    /// <summary>The 256-entry colour remap (palette index to the house colour's index).</summary>
    public byte[] Remap = [];
}

/// <summary>AbstractTypeClass: the INI name and the text name number.</summary>
public abstract class AbstractTypeClass
{
    public string IniName = "";
    public int Name;
    /// <summary>The type's number (its value in the source's type enum, e.g. UNIT_MTANK).</summary>
    public int Id;
    public virtual RTTIType What_Am_I() => RTTIType.RTTI_ABSTRACTTYPE;
    public virtual int Full_Name(SpecialOptions special) => Name;
    public virtual uint Coord_Fixup(uint coord) => coord;
    public virtual int Get_Ownable() => 0xFFFF;
    public override string ToString() => IniName;
}

/// <summary>ObjectTypeClass: everything that can be placed on the map.</summary>
public abstract class ObjectTypeClass : AbstractTypeClass
{
    public bool IsCrushable, IsStealthy, IsSelectable, IsLegalTarget, IsInsignificant, IsImmune, IsFlammable, IsSentient;
    public ArmorType Armor;
    public int MaxStrength;
    /// <summary>The number of frames in the object's art (Get_Build_Frame_Count of its image), set when the art loads.</summary>
    public int ImageFrames;

    static readonly short[] Empty = [TdMath.RefreshEol];
    public virtual short[] Occupy_List(SpecialOptions special, bool placement = false) => Empty;
    public virtual short[] Overlap_List() => Empty;
    public virtual int Max_Pips() => 0;
    public virtual void Dimensions(out int width, out int height) { width = 10; height = 10; }
    public virtual int Cost_Of(SpecialOptions special) => 0;
}

/// <summary>TechnoTypeClass: buildings, vehicles, infantry and aircraft.</summary>
public abstract class TechnoTypeClass : ObjectTypeClass
{
    public bool IsLeader, IsScanner, IsNominal, IsTheater, IsTurretEquipped, IsTwoShooter, IsRepairable, IsBuildable, IsCrew, IsTransporter;
    public int SightRange, Cost, Scenario, Level;
    public long Pre;
    public int Risk, Reward;
    public MPHType MaxSpeed;
    public int MaxAmmo;
    public long Ownable;
    public WeaponType Primary = WeaponType.WEAPON_NONE, Secondary = WeaponType.WEAPON_NONE;

    public virtual int Raw_Cost(TdTypes types, SpecialOptions special) => Cost;
    public int Max_Passengers() => IsTransporter ? 5 : 0;
    public virtual int Repair_Cost(TdTypes types) => 0;
    public virtual int Repair_Step() => 0;
    public override int Cost_Of(SpecialOptions special) => Cost;
    public override int Get_Ownable() => (int)Ownable;

    /// <summary>Legal_Placement (the remaster's RA-AI version): every cell of the object is on the map
    /// and clear to move into; buildings use their own check.</summary>
    public virtual int Legal_Placement(TdGame game, int pos)
    {
        if (pos == -1)
            return 0;
        foreach (short offset in Occupy_List(game.Special, true))
        {
            if (offset == TdMath.RefreshEol)
                break;
            int cell = pos + offset;
            if (!game.Map.In_Radar(cell))
                return 0;
            if (!game.Map[cell].Is_Clear_To_Move(false, false))
                return 0;
        }
        return 1;
    }
}

public sealed class BuildingTypeClass : TechnoTypeClass
{
    public const int REPAIR_PERCENT = 102, REPAIR_STEP = 5;

    public bool IsBibbed, IsWall, IsFactory, IsSimpleDamage, IsSturdy, IsCaptureable, IsRegulated, IsUnsellable;
    /// <summary>Get_Buildup_Data() != NULL: the type has build-up (make) art, set when the art loads. Only those can be sold.</summary>
    public bool HasBuildup;
    public RTTIType ToBuild;
    /// <summary>Where a produced unit leaves, in pixels from the top left ("x,y").</summary>
    public string ExitPoint = "0,0";
    public short[] ExitList = [];
    public StructType Type;
    public long CanEnter;
    public int StartFace;
    public int Capacity, Power, Drain;
    /// <summary>The building's raw strength argument (MaxStrength is this doubled, as the original constructor does).</summary>
    public int Strength;
    public BSizeType Size;
    public short[] OccupyList = [], OverlapList = [];
    /// <summary>Animation per BStateType: [start frame, frame count, rate].</summary>
    public Dictionary<string, int[]> Anims = new();

    public override RTTIType What_Am_I() => RTTIType.RTTI_BUILDINGTYPE;
    public override uint Coord_Fixup(uint coord) => coord & 0xFF00FF00;

    static readonly int[] WidthTable = [1, 2, 1, 2, 2, 3, 3, 4, 5];
    static readonly int[] HeightTable = [1, 1, 2, 2, 3, 2, 3, 2, 5];
    public int Width() => WidthTable[(int)Size];
    public int Height() => HeightTable[(int)Size];

    public override void Dimensions(out int width, out int height)
    {
        width = WidthTable[(int)Size] * TdMath.CellPixel;
        width -= width / 5;
        height = HeightTable[(int)Size] * TdMath.CellPixel;
        height -= height / 5;
    }

    public override int Max_Pips() => Math.Clamp(Capacity / 100, 0, 10);

    /// <summary>The exit point in leptons (XYP_COORD).</summary>
    public uint ExitCoord()
    {
        var parts = ExitPoint.Split(',');
        int x = int.TryParse(parts[0], out int px) ? px : 0, y = parts.Length > 1 && int.TryParse(parts[1], out int py) ? py : 0;
        return TdMath.XYP_COORD(x, y);
    }

    /// <summary>Legal_Placement: every cell the building (with its bib) would cover is on the map and generally clear.</summary>
    public override int Legal_Placement(TdGame game, int pos)
    {
        if (pos == -1)
            return 0;
        foreach (short offset in Occupy_List(game.Types, game.Special, true))
        {
            if (offset == TdMath.RefreshEol)
                break;
            int cell = pos + offset;
            if (!game.Map.In_Radar(cell))
                return 0;
            if (!game.Map[cell].Is_Generally_Clear())
                return 0;
        }
        return 1;
    }

    /// <summary>Who_Can_Build_Me: the house's newest construction yard able to build this (busy ones only in theory).</summary>
    public Objects.BuildingClass? Who_Can_Build_Me(TdGame game, bool intheory, bool legal, HousesType house)
    {
        for (int index = game.Buildings.Count - 1; index >= 0; index--)
        {
            var building = game.Buildings.Ptr(index);
            if (!building.IsInLimbo &&
                building.House.Class.House == house &&
                building.Class.ToBuild == RTTIType.RTTI_BUILDINGTYPE &&
                building.Mission != MissionType.MISSION_DECONSTRUCTION &&
                ((1L << (int)building.ActLike) & Ownable) != 0 &&
                (!legal || building.House.Can_Build(this, building.ActLike)) &&
                (intheory || !building.In_Radio_Contact()))
                return building;
        }
        return null;
    }

    /// <summary>Bib_And_Offset: the concrete pad (BIB1-3 by width) and the cell it goes in, if any.</summary>
    public bool Bib_And_Offset(SpecialOptions special, out SmudgeType bib, ref int cell)
    {
        bib = SmudgeType.SMUDGE_NONE;
        if (IsBibbed && !special.IsRoad)
        {
            bib = Width() switch
            {
                2 => SmudgeType.SMUDGE_BIB3,
                3 => SmudgeType.SMUDGE_BIB2,
                4 => SmudgeType.SMUDGE_BIB1,
                _ => SmudgeType.SMUDGE_NONE,
            };
            if (bib != SmudgeType.SMUDGE_NONE)
                cell += (Height() - 1) * TdMath.MapCellW;
        }
        return bib != SmudgeType.SMUDGE_NONE;
    }

    /// <summary>Occupy_List: the cells the building covers (with its bib when placing, or with road bibs).</summary>
    public short[] Occupy_List(TdTypes types, SpecialOptions special, bool placement = false)
    {
        int cell = 0;
        SmudgeType bib = SmudgeType.SMUDGE_NONE;
        if ((placement && Bib_And_Offset(special, out bib, ref cell)) || (special.IsRoad && (Type == StructType.STRUCT_BARRACKS || (placement && Type == StructType.STRUCT_REFINERY))))
        {
            if (special.IsRoad && Type == StructType.STRUCT_BARRACKS)
            {
                bib = SmudgeType.SMUDGE_BIB3;
                cell = 0;
            }
            if (special.IsRoad && Type == StructType.STRUCT_REFINERY)
            {
                bib = SmudgeType.SMUDGE_BIB2;
                cell = TdMath.MapCellW;
            }
            var list = new List<short>();
            foreach (short offset in types.Smudge(bib).Occupy_List(special))
            {
                if (offset == TdMath.RefreshEol)
                    break;
                list.Add((short)(offset + cell));
            }
            foreach (short offset in OccupyList)
            {
                if (offset == TdMath.RefreshEol)
                    break;
                list.Add(offset);
            }
            list.Add(TdMath.RefreshEol);
            return list.ToArray();
        }
        return OccupyList.Length > 0 ? OccupyList : [TdMath.RefreshEol];
    }

    public override short[] Occupy_List(SpecialOptions special, bool placement = false) => OccupyList.Length > 0 ? OccupyList : [TdMath.RefreshEol];
    public override short[] Overlap_List() => OverlapList.Length > 0 ? OverlapList : [TdMath.RefreshEol];

    public override int Raw_Cost(TdTypes types, SpecialOptions special)
    {
        // (PATCH: in 1.07 multiplayer the gun turret costs 250. IsV107 is a network version check; off here.)
        int cost = Cost;
        if (Type == StructType.STRUCT_HELIPAD)
            cost -= types.Aircraft(AircraftType.AIRCRAFT_ORCA).Cost;
        if (Type == StructType.STRUCT_REFINERY)
            cost -= types.Unit(UnitType.UNIT_HARVESTER).Cost;
        return cost;
    }

    public override int Repair_Cost(TdTypes types)
    {
        int cost = Raw_Cost(types, types.Special) * REPAIR_STEP / MaxStrength;
        cost /= 2;
        cost = Math.Max(cost, 1);
        cost = (int)TdMath.FixedToCardinal((uint)cost, REPAIR_PERCENT);
        return Math.Max(cost, 1);
    }

    public override int Repair_Step() => REPAIR_STEP;

    public override int Cost_Of(SpecialOptions special) => Cost; // (the helipad special case needs types: see Cost_Of(types))

    public int Cost_Of(TdTypes types) =>
        types.Special.IsSeparate && Type == StructType.STRUCT_HELIPAD ? Raw_Cost(types, types.Special) : Cost;

    /// <summary>An animation sequence: start frame, count and rate (all 0..1 by default, as Init does).</summary>
    public (int Start, int Count, int Rate) Anim(BStateType state) =>
        Anims.TryGetValue(state.ToString(), out var a) && a.Length >= 3 ? (a[0], a[1], a[2]) : (0, 1, 0);
}

public sealed class UnitTypeClass : TechnoTypeClass
{
    public const int TIBERIUM_STEP = 25, STEP_COUNT = 28, FULL_LOAD_CREDITS = TIBERIUM_STEP * STEP_COUNT, REPAIR_PERCENT = 102, REPAIR_STEP = 4;

    public bool IsCrateGoodie, IsPieceOfEight, IsCrusher, IsToHarvest, IsChunkyShape, IsRadarEquipped, IsFireAnim, IsLockTurret;
    public bool IsTracked, IsGigundo, IsCloakable, IsAnimating;
    public UnitType Type;
    public SpeedType Speed;
    public int ROT, TurretOffset;
    public MissionType Mission;
    public AnimType Explosion = AnimType.ANIM_NONE;
    /// <summary>The largest frame's size (set from the art in One_Time).</summary>
    public int MaxSize;

    public override RTTIType What_Am_I() => RTTIType.RTTI_UNITTYPE;

    public override void Dimensions(out int width, out int height)
    {
        if (Type == UnitType.UNIT_GUNBOAT)
        {
            width = 46;
            height = 18;
        }
        else
        {
            width = MaxSize - MaxSize / 4;
            height = MaxSize - MaxSize / 4;
        }
    }

    public override short[] Occupy_List(SpecialOptions special, bool placement = false) =>
        Type == UnitType.UNIT_GUNBOAT ? [0, -1, 1, TdMath.RefreshEol] : [0, TdMath.RefreshEol];

    public override int Max_Pips()
    {
        if (Type == UnitType.UNIT_HARVESTER)
            return FULL_LOAD_CREDITS / 100;
        return IsTransporter ? Max_Passengers() : 0;
    }

    public override int Repair_Cost(TdTypes types) => (int)TdMath.FixedToCardinal((uint)(Cost / (MaxStrength / REPAIR_STEP)), REPAIR_PERCENT);
    public override int Repair_Step() => REPAIR_STEP;
}

public sealed class InfantryTypeClass : TechnoTypeClass
{
    public bool IsFemale, IsCrawling, IsCapture, IsFraidyCat, IsCivilian, IsAvoidingTiberium;
    public InfantryType Type;
    /// <summary>DoControls: per DoType, [first frame, frame count, frames to jump per facing].</summary>
    public Dictionary<string, int[]> DoTable = new();
    public int FireLaunch, ProneLaunch;

    public override RTTIType What_Am_I() => RTTIType.RTTI_INFANTRYTYPE;
    public override void Dimensions(out int width, out int height) { width = 12; height = 16; }
    public override short[] Occupy_List(SpecialOptions special, bool placement = false) => [0, TdMath.RefreshEol];

    public (int Frame, int Count, int Jump) DoControl(DoType action) =>
        DoTable.TryGetValue(action.ToString(), out var d) && d.Length >= 3 ? (d[0], d[1], d[2]) : (0, 1, 0);

    public override int Full_Name(SpecialOptions special)
    {
        if (!IsNominal || special.IsNamed || Type is InfantryType.INFANTRY_C10 or InfantryType.INFANTRY_DELPHI or InfantryType.INFANTRY_MOEBIUS)
            return Name;
        return (int)TdDefines.TXT_CIVILIAN;
    }
}

public sealed class AircraftTypeClass : TechnoTypeClass
{
    public const int REPAIR_PERCENT = 102, REPAIR_STEP = 2;

    public bool IsFixedWing, IsLandable, IsRotorEquipped, IsRotorCustom;
    public AircraftType Type;
    public int ROT;
    public MissionType Mission;

    public override RTTIType What_Am_I() => RTTIType.RTTI_AIRCRAFTTYPE;
    public override void Dimensions(out int width, out int height) { width = 21; height = 20; }
    public override short[] Occupy_List(SpecialOptions special, bool placement = false) => [0, TdMath.RefreshEol];
    public override short[] Overlap_List() =>
        [-(TdMath.MapCellW - 1), -TdMath.MapCellW, -(TdMath.MapCellW + 1), -1, 1, TdMath.MapCellW - 1, TdMath.MapCellW, TdMath.MapCellW + 1, TdMath.RefreshEol];
    public override int Max_Pips() => IsTransporter ? Max_Passengers() : Primary != WeaponType.WEAPON_NONE ? 5 : 0;
    public override int Repair_Cost(TdTypes types) => (int)TdMath.FixedToCardinal((uint)(Cost / (MaxStrength / REPAIR_STEP)), REPAIR_PERCENT);
    public override int Repair_Step() => REPAIR_STEP;
}

public sealed class BulletTypeClass : ObjectTypeClass
{
    public bool IsHigh, IsArcing, IsHoming, IsDropping, IsInvisible, IsProximityArmed, IsFlameEquipped, IsFueled, IsFaceless, IsInaccurate, IsTranslucent, IsAntiAircraft;
    public BulletType Type;
    public MPHType MaxSpeed;
    public WarheadType Warhead;
    public AnimType Explosion = AnimType.ANIM_NONE;
    public int ROT, Arming, Range;
    public override RTTIType What_Am_I() => RTTIType.RTTI_BULLETTYPE;
}

public sealed class TerrainTypeClass : ObjectTypeClass
{
    public TerrainType Type;
    public bool IsDestroyable, IsTransformable, IsTiberiumSpawn;
    /// <summary>The base of the object (for sorting and targeting), in pixels ("x,y").</summary>
    public string CenterBase = "0,0";
    /// <summary>The theaters it appears in (THEATERF_ flags).</summary>
    public long Theater;
    public short[] Occupy = [], Overlap = [];

    public override RTTIType What_Am_I() => RTTIType.RTTI_TERRAINTYPE;

    /// <summary>CenterBase as the original's coordinate (XYP_COORD of the pixel offset).</summary>
    public uint CenterBaseCoord()
    {
        var parts = CenterBase.Split(',');
        int x = int.TryParse(parts[0], out int px) ? px : 0, y = parts.Length > 1 && int.TryParse(parts[1], out int py) ? py : 0;
        return TdMath.XYP_COORD(x, y);
    }
    public override uint Coord_Fixup(uint coord) => coord & 0xFF00FF00;
    public override short[] Occupy_List(SpecialOptions special, bool placement = false) => Occupy.Length > 0 ? Occupy : [TdMath.RefreshEol];
    public override short[] Overlap_List() => Overlap.Length > 0 ? Overlap : [TdMath.RefreshEol];
}

public sealed class TemplateTypeClass : ObjectTypeClass
{
    public TemplateType Type;
    public string Theater = "";
    public LandType Land, AltLand;
    public int Width = 1, Height = 1;
    public int[] AltIcons = [];

    public override RTTIType What_Am_I() => RTTIType.RTTI_TEMPLATETYPE;
    public override uint Coord_Fixup(uint coord) => coord & 0xFF00FF00;

    /// <summary>The land type of one of the template's icons (CellClass::Recalc_Attributes' template part).</summary>
    public LandType LandOf(int icon)
    {
        foreach (int alt in AltIcons)
        {
            if (alt == -1)
                break;
            if (alt == icon)
                return AltLand;
        }
        return Land;
    }
}

public sealed class AnimTypeClass : ObjectTypeClass
{
    public bool IsNormalized, IsGroundLayer, IsTranslucent, IsWhiteTrans, IsFlameThrower, IsScorcher, IsCraterForming, IsSticky;
    public AnimType Type;
    public int Size, Biggest, Damage, Delay, Start, LoopStart, LoopEnd, Stages, Loops;
    public VocType Sound = VocType.VOC_NONE;
    public AnimType ChainTo = AnimType.ANIM_NONE, VirtualAnim = AnimType.ANIM_NONE;
    public int VirtualStages = -1, VirtualScale = 0x100;
    public override RTTIType What_Am_I() => RTTIType.RTTI_ANIMTYPE;
}

public sealed class OverlayTypeClass : ObjectTypeClass
{
    public OverlayType Type;
    public LandType Land;
    public int DamageLevels, DamagePoints;
    public bool IsTheater, IsWall, IsHigh, IsTiberium, IsWooden, IsCrate, IsRadarVisible;
    public override RTTIType What_Am_I() => RTTIType.RTTI_OVERLAYTYPE;
    public override uint Coord_Fixup(uint coord) => coord & 0xFF00FF00;
    public override short[] Occupy_List(SpecialOptions special, bool placement = false) => [0, TdMath.RefreshEol];
}

public sealed class SmudgeTypeClass : ObjectTypeClass
{
    public SmudgeType Type;
    public int Width = 1, Height = 1;
    public bool IsCrater, IsBib;
    public override RTTIType What_Am_I() => RTTIType.RTTI_SMUDGETYPE;

    public override short[] Occupy_List(SpecialOptions special, bool placement = false)
    {
        var list = new List<short>();
        for (int x = 0; x < Width; x++)
            for (int y = 0; y < Height; y++)
                list.Add((short)(x + y * TdMath.MapCellW));
        list.Add(TdMath.RefreshEol);
        return list.ToArray();
    }

    public override short[] Overlap_List() => Occupy_List(new SpecialOptions());
}

/// <summary>All the type tables of a loaded mod, indexed by their enums (the original's As_Reference / From_Name).</summary>
public sealed class TdTypes
{
    public SpecialOptions Special { get; } = new();

    readonly Dictionary<int, BuildingTypeClass> buildings = new();
    readonly Dictionary<int, UnitTypeClass> units = new();
    readonly Dictionary<int, InfantryTypeClass> infantry = new();
    readonly Dictionary<int, AircraftTypeClass> aircraft = new();
    readonly Dictionary<int, BulletTypeClass> bullets = new();
    readonly Dictionary<int, AnimTypeClass> anims = new();
    readonly Dictionary<int, TerrainTypeClass> terrains = new();
    readonly Dictionary<int, TemplateTypeClass> templates = new();
    readonly Dictionary<int, OverlayTypeClass> overlays = new();
    readonly Dictionary<int, SmudgeTypeClass> smudges = new();
    readonly Dictionary<int, WeaponTypeClass> weapons = new();
    readonly Dictionary<int, WarheadTypeClass> warheads = new();
    readonly Dictionary<int, GroundType> ground = new();
    readonly Dictionary<int, HouseTypeClass> houses = new();
    readonly Dictionary<string, ObjectTypeClass> byName = new(StringComparer.OrdinalIgnoreCase);

    public TdTypes(RuleSet rules)
    {
        Load(rules, "Buildings", buildings, () => new BuildingTypeClass());
        Load(rules, "Vehicles", units, () => new UnitTypeClass());
        Load(rules, "Infantry", infantry, () => new InfantryTypeClass());
        Load(rules, "Aircraft", aircraft, () => new AircraftTypeClass());
        Load(rules, "Projectiles", bullets, () => new BulletTypeClass());
        Load(rules, "Animations", anims, () => new AnimTypeClass());
        Load(rules, "TerrainObjects", terrains, () => new TerrainTypeClass());
        Load(rules, "Templates", templates, () => new TemplateTypeClass());
        Load(rules, "Overlays", overlays, () => new OverlayTypeClass());
        Load(rules, "Smudges", smudges, () => new SmudgeTypeClass());
        LoadPlain(rules, "Weapons", weapons, () => new WeaponTypeClass(), w => w.Id);
        LoadPlain(rules, "Warheads", warheads, () => new WarheadTypeClass(), w => w.Id);
        LoadPlain(rules, "Ground", ground, () => new GroundType(), g => g.Id);
        foreach (var (name, node) in rules.Section("RemapTables").Entries)
            remapTables[name] = node.AsList().Select(n => (byte)(RuleBinder.Number(n.Value) ?? 0)).ToArray();
        foreach (var (_, node) in rules.Section("Houses").Entries)
        {
            var house = new HouseTypeClass();
            RuleBinder.Bind(house, node);
            if (rules.Section("RemapTables").TryGet(house.RemapTable, out var remap))
                house.Remap = remap.AsList().Select(n => (byte)(RuleBinder.Number(n.Value) ?? 0)).ToArray();
            houses[(int)house.House] = house;
        }
    }

    void Load<T>(RuleSet rules, string section, Dictionary<int, T> table, Func<T> make) where T : ObjectTypeClass
    {
        foreach (var (_, node) in rules.Section(section).Entries)
        {
            var type = make();
            RuleBinder.Bind(type, node);
            table[type.Id] = type;
            byName.TryAdd(type.IniName, type);
        }
    }

    static void LoadPlain<T>(RuleSet rules, string section, Dictionary<int, T> table, Func<T> make, Func<T, int> id)
    {
        foreach (var (_, node) in rules.Section(section).Entries)
        {
            var item = make();
            RuleBinder.Bind(item, node);
            table[id(item)] = item;
        }
    }

    // Every type of a kind (for tools and other engines that convert TD's data).
    public IEnumerable<BuildingTypeClass> AllBuildings => buildings.OrderBy(e => e.Key).Select(e => e.Value);
    public IEnumerable<UnitTypeClass> AllUnits => units.OrderBy(e => e.Key).Select(e => e.Value);
    public IEnumerable<InfantryTypeClass> AllInfantry => infantry.OrderBy(e => e.Key).Select(e => e.Value);
    public IEnumerable<AircraftTypeClass> AllAircraft => aircraft.OrderBy(e => e.Key).Select(e => e.Value);
    public IEnumerable<BulletTypeClass> AllBullets => bullets.OrderBy(e => e.Key).Select(e => e.Value);
    public IEnumerable<WeaponTypeClass> AllWeapons => weapons.OrderBy(e => e.Key).Select(e => e.Value);
    public IEnumerable<WarheadTypeClass> AllWarheads => warheads.OrderBy(e => e.Key).Select(e => e.Value);
    public IEnumerable<TerrainTypeClass> AllTerrains => terrains.OrderBy(e => e.Key).Select(e => e.Value);
    public IEnumerable<OverlayTypeClass> AllOverlays => overlays.OrderBy(e => e.Key).Select(e => e.Value);
    public IEnumerable<AnimTypeClass> AllAnims => anims.OrderBy(e => e.Key).Select(e => e.Value);

    public BuildingTypeClass Building(StructType type) => buildings[(int)type];
    public UnitTypeClass Unit(UnitType type) => units[(int)type];
    public InfantryTypeClass Infantry(InfantryType type) => infantry[(int)type];
    public AircraftTypeClass Aircraft(AircraftType type) => aircraft[(int)type];
    public BulletTypeClass Bullet(BulletType type) => bullets[(int)type];
    public AnimTypeClass Anim(AnimType type) => anims[(int)type];
    public TerrainTypeClass Terrain(TerrainType type) => terrains[(int)type];
    public TemplateTypeClass? Template(TemplateType type) => templates.GetValueOrDefault((int)type);
    public OverlayTypeClass Overlay(OverlayType type) => overlays[(int)type];
    public SmudgeTypeClass Smudge(SmudgeType type) => smudges[(int)type];
    public WeaponTypeClass Weapon(WeaponType type) => weapons[(int)type];
    public WarheadTypeClass Warhead(WarheadType type) => warheads[(int)type];
    public GroundType Ground(LandType land) => ground[(int)land];
    public HouseTypeClass House(HousesType house) => houses[(int)house];

    public IEnumerable<BuildingTypeClass> Buildings => buildings.Values;
    public IEnumerable<UnitTypeClass> Units => units.Values;
    public IEnumerable<TemplateTypeClass> Templates => templates.Values;
    public IEnumerable<HouseTypeClass> Houses => houses.Values;

    /// <summary>The From_Name functions: any placeable type by its INI name (case-insensitive).</summary>
    public ObjectTypeClass? FromName(string iniName) => byName.GetValueOrDefault(iniName);

    public T? FromName<T>(string iniName) where T : ObjectTypeClass => byName.GetValueOrDefault(iniName) as T;

    // ---- The From_Name lookups (case-insensitive IniName; the NONE value when not found or null) ----

    static T Find<T>(IEnumerable<KeyValuePair<int, string>> table, string? name, T none) where T : struct, Enum
    {
        if (name != null)
            foreach (var (id, ini) in table.OrderBy(e => e.Key))
                if (string.Equals(ini, name, StringComparison.OrdinalIgnoreCase))
                    return (T)Enum.ToObject(typeof(T), id);
        return none;
    }

    public HousesType House_From_Name(string? name) => Find(houses.Select(h => new KeyValuePair<int, string>(h.Key, h.Value.IniName)), name, HousesType.HOUSE_NONE);
    public UnitType Unit_From_Name(string? name) => Find(units.Select(h => new KeyValuePair<int, string>(h.Key, h.Value.IniName)), name, UnitType.UNIT_NONE);
    public InfantryType Infantry_From_Name(string? name) => Find(infantry.Select(h => new KeyValuePair<int, string>(h.Key, h.Value.IniName)), name, InfantryType.INFANTRY_NONE);
    public AircraftType Aircraft_From_Name(string? name) => Find(aircraft.Select(h => new KeyValuePair<int, string>(h.Key, h.Value.IniName)), name, AircraftType.AIRCRAFT_NONE);
    public StructType Building_From_Name(string? name) => Find(buildings.Select(h => new KeyValuePair<int, string>(h.Key, h.Value.IniName)), name, StructType.STRUCT_NONE);
    public TerrainType Terrain_From_Name(string? name) => Find(terrains.Select(h => new KeyValuePair<int, string>(h.Key, h.Value.IniName)), name, TerrainType.TERRAIN_NONE);
    public OverlayType Overlay_From_Name(string? name) => Find(overlays.Select(h => new KeyValuePair<int, string>(h.Key, h.Value.IniName)), name, OverlayType.OVERLAY_NONE);
    public SmudgeType Smudge_From_Name(string? name) => Find(smudges.Select(h => new KeyValuePair<int, string>(h.Key, h.Value.IniName)), name, SmudgeType.SMUDGE_NONE);
    public TemplateType Template_From_Name(string? name) => Find(templates.Select(h => new KeyValuePair<int, string>(h.Key, h.Value.IniName)), name, TemplateType.TEMPLATE_NONE);

    readonly Dictionary<string, byte[]> remapTables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A remap table by its source name (RemapGold, RemapRed...).</summary>
    public byte[]? Remap_Table(string name) => remapTables.GetValueOrDefault(name);

    /// <summary>The remap table for a multiplayer colour (HouseClass::Init_Data).</summary>
    public byte[]? Remap_For_Color(PlayerColorType color) => color switch
    {
        PlayerColorType.REMAP_GOLD => Remap_Table("RemapGold"),
        PlayerColorType.REMAP_RED => Remap_Table("RemapRed"),
        PlayerColorType.REMAP_LTBLUE => Remap_Table("RemapLtBlue"),
        PlayerColorType.REMAP_ORANGE => Remap_Table("RemapOrange"),
        PlayerColorType.REMAP_GREEN => Remap_Table("RemapGreen"),
        PlayerColorType.REMAP_BLUE => Remap_Table("RemapBlue"),
        _ => null,
    };

    public HouseTypeClass? HouseFromName(string iniName) => houses.Values.FirstOrDefault(h => h.IniName.Equals(iniName, StringComparison.OrdinalIgnoreCase));
}

using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Scenario;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic;

// HOUSE.CPP's USE_RA_AI section: the computer opponent the remaster brought over from Red Alert for
// skirmish (GAME_GLYPHX_MULTIPLAYER). An expert system picks what to build and when to attack; the
// base is divided into zones around its centre, and defences go where a zone is weakest.
public sealed partial class HouseClass
{
    public struct ZoneDefense
    {
        public int AirDefense, ArmorDefense, InfantryDefense;
    }

    public struct BuildChoiceClass(UrgencyType urgency, StructType structure)
    {
        public UrgencyType Urgency = urgency;
        public StructType Structure = structure;
    }

    // ---- The data (HOUSE.H's USE_RA_AI members) ----

    public long OldBScan;
    public int CurInfantry, CurAircraft;
    public int MaxInfantry, MaxAircraft;

    public uint Center;
    public int Radius;
    public readonly ZoneDefense[] ZoneInfo = new ZoneDefense[(int)ZoneType.ZONE_COUNT];
    public int LATime;
    public RTTIType LAType = RTTIType.RTTI_NONE;
    public ZoneType LAZone = ZoneType.ZONE_NONE;
    public HousesType LAEnemy = HousesType.HOUSE_NONE;
    public uint ToCapture = Target.TARGET_NONE;
    public int RadarSpied, PointTotal;
    readonly int[] BQuantity = new int[(int)StructType.STRUCT_COUNT];
    readonly int[] UQuantity = new int[(int)UnitType.UNIT_COUNT];
    readonly int[] IQuantity = new int[(int)InfantryType.INFANTRY_COUNT];
    readonly int[] AQuantity = new int[(int)AircraftType.AIRCRAFT_COUNT];
    CountDownTimer Attack = null!;
    public HousesType Enemy = HousesType.HOUSE_NONE;
    public CountDownTimer AITimer = null!;
    public StructType BuildStructure = StructType.STRUCT_NONE;
    public UnitType BuildUnit = UnitType.UNIT_NONE;
    public InfantryType BuildInfantry = InfantryType.INFANTRY_NONE;
    public AircraftType BuildAircraft = AircraftType.AIRCRAFT_NONE;
    public StateType State = StateType.STATE_BUILDUP;
    public bool IsBaseBuilding, IsTiberiumShort, IsParanoid;

    // The static BuildChoice heap (STRUCT_COUNT entries; Alloc fails when full).
    static readonly List<BuildChoiceClass> BuildChoice = new();

    /// <summary>The constructor's USE_RA_AI part.</summary>
    void Init_RA_AI()
    {
        IsBaseBuilding = true;
        Center = 0;
        Radius = 0;
        Array.Clear(ZoneInfo);
        LATime = 0;
        LAType = RTTIType.RTTI_NONE;
        LAZone = ZoneType.ZONE_NONE;
        LAEnemy = HousesType.HOUSE_NONE;
        ToCapture = Target.TARGET_NONE;
        RadarSpied = 0;
        PointTotal = 0;
        Array.Clear(BQuantity);
        Array.Clear(UQuantity);
        Array.Clear(IQuantity);
        Array.Clear(AQuantity);
        Attack = new CountDownTimer(game);
        Attack.Set(0);
        Enemy = HousesType.HOUSE_NONE;
        AITimer = new CountDownTimer(game);
        AITimer.Set(0);
        BuildStructure = StructType.STRUCT_NONE;
        BuildUnit = UnitType.UNIT_NONE;
        BuildInfantry = InfantryType.INFANTRY_NONE;
        BuildAircraft = AircraftType.AIRCRAFT_NONE;
        State = StateType.STATE_BUILDUP;
        IsTiberiumShort = false;
        IQ = game.Rule.MaxIQ;
        IsParanoid = false;
        OldBScan = 0;
        Assign_Handicap(DiffType.DIFF_NORMAL);
    }

    bool Percent_Chance(int percent) => game.Random_Pick(0, 99) < percent;

    /// <summary>RA's fixed-point round up: the integer part of a value with 8 fraction bits, rounded up.</summary>
    static ushort Round_Up(ushort val)
    {
        if ((val & 0xff) == 0)
            return val;   // as the source: a whole value comes back unshifted
        val &= 0xff00;
        val += 0x0100;
        val >>= 8;
        return val;
    }

    // Rule ratio (8.8 fixed) times a count, truncated to the unsigned short the source passes.
    static ushort Ratio(ushort ratio, int count) => (ushort)(ratio * count);

    bool Can_Make_Money() => Available_Money() > 300 || (BScan & (long)TdDefines.STRUCTF_REFINERY) != 0;

    int Cost_Of(BuildingTypeClass b) => b.Cost_Of(game.Types);

    public BuildingTypeClass? Suggest_New_Building() =>
        BuildStructure != StructType.STRUCT_NONE ? game.Types.Building(BuildStructure) : null;

    /// <summary>Find_Building: one of this house's buildings of a type (in a zone, if given).</summary>
    public BuildingClass? Find_Building(StructType type, ZoneType zone = ZoneType.ZONE_NONE)
    {
        if (BQuantity[(int)type] > 0)
        {
            for (int index = 0; index < game.Buildings.Count; index++)
            {
                var b = game.Buildings.Ptr(index);
                if (!b.IsInLimbo && b.House == this && b.Class.Type == type)
                    if (zone == ZoneType.ZONE_NONE || Which_Zone(b) == zone)
                        return b;
            }
        }
        return null;
    }

    /// <summary>Find_Build_Location: a spot for a new building, in the zone whose defences need its
    /// strengths most (combat buildings aren't put next to others).</summary>
    public uint Find_Build_Location(BuildingClass building)
    {
        var zonerating = new int[(int)ZoneType.ZONE_COUNT];
        int zAir = 0, zArmor = 0, zInf = 0;
        int antiair = building.Anti_Air();
        int antiarmor = building.Anti_Armor();
        int antiinfantry = building.Anti_Infantry();

        ZoneType z;
        for (z = ZoneType.ZONE_NORTH; z < ZoneType.ZONE_COUNT; z++)
        {
            zAir += ZoneInfo[(int)z].AirDefense;
            zArmor += ZoneInfo[(int)z].ArmorDefense;
            zInf += ZoneInfo[(int)z].InfantryDefense;
        }
        int perimeter = ZoneType.ZONE_COUNT - ZoneType.ZONE_NORTH;
        zAir /= perimeter;
        zArmor /= perimeter;
        zInf /= perimeter;

        for (z = ZoneType.ZONE_FIRST; z < ZoneType.ZONE_COUNT; z++)
        {
            int diff = zAir - ZoneInfo[(int)z].AirDefense;
            if (z == ZoneType.ZONE_CORE) diff /= 2;
            if (diff > 0) zonerating[(int)z] += Math.Min(antiair, diff);
            diff = zArmor - ZoneInfo[(int)z].ArmorDefense;
            if (z == ZoneType.ZONE_CORE) diff /= 2;
            if (diff > 0) zonerating[(int)z] += Math.Min(antiarmor, diff);
            diff = zInf - ZoneInfo[(int)z].InfantryDefense;
            if (z == ZoneType.ZONE_CORE) diff /= 2;
            if (diff > 0) zonerating[(int)z] += Math.Min(antiinfantry, diff);
        }

        var zone = (ZoneType)game.Random_Pick((int)ZoneType.ZONE_FIRST, (int)ZoneType.ZONE_WEST);
        int largest = 0;
        for (z = ZoneType.ZONE_FIRST; z < ZoneType.ZONE_COUNT; z++)
        {
            if (zonerating[(int)z] > largest)
            {
                zone = z;
                largest = zonerating[(int)z];
            }
        }

        int zcell = Find_Cell_In_Zone(building, zone);
        if (zcell != 0)
            return TdMath.CellCoord(zcell);

        // Not in the preferred zone: try any zone.
        ZoneType[] zones = [ZoneType.ZONE_CORE, ZoneType.ZONE_NORTH, ZoneType.ZONE_SOUTH, ZoneType.ZONE_EAST, ZoneType.ZONE_WEST];
        int start = game.Random_Pick(0, zones.Length - 1);
        for (int zz = 0; zz < zones.Length; zz++)
        {
            var tryzone = zones[(zz + start) % zones.Length];
            zcell = Find_Cell_In_Zone(building, tryzone);
            // As the source: this returns the cell number itself as the coordinate.
            if (zcell != 0)
                return (uint)zcell;
        }
        return 0;
    }

    /// <summary>Recalc_Center: the base's centre (weighted by building cost), its average radius,
    /// and each zone's defence strengths.</summary>
    public void Recalc_Center()
    {
        Center = 0;
        Radius = 0;
        Array.Clear(ZoneInfo);

        if (CurBuildings > 0)
        {
            int x = 0, y = 0, count = 0;
            for (int index = 0; index < game.Buildings.Count; index++)
            {
                var b = game.Buildings.Ptr(index);
                if (!b.IsInLimbo && b.House == this && b.Strength > 0)
                {
                    // Expensive buildings weigh more.
                    int weight = (Cost_Of(b.Class) / 1000) + 1;
                    for (int i = 0; i < weight; i++)
                    {
                        x += TdMath.CoordX(b.Center_Coord());
                        y += TdMath.CoordY(b.Center_Coord());
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                x /= count;
                y /= count;
                Center = TdMath.XyCoord(x, y);
            }

            if (count > 1)
            {
                int radius = 0;
                for (int index = 0; index < game.Buildings.Count; index++)
                {
                    var b = game.Buildings.Ptr(index);
                    if (!b.IsInLimbo && b.House == this && b.Strength > 0)
                        radius += TdMath.Distance(Center, b.Center_Coord());
                }
                Radius = Math.Max(radius / count, 2 * (int)TdDefines.CELL_LEPTON_W);

                for (int index = 0; index < game.Buildings.Count; index++)
                {
                    var b = game.Buildings.Ptr(index);
                    if (!b.IsInLimbo && b.House == this && b.Strength > 0)
                    {
                        var zone = Which_Zone(b);
                        if (zone != ZoneType.ZONE_NONE)
                        {
                            ZoneInfo[(int)zone].ArmorDefense += b.Anti_Armor();
                            ZoneInfo[(int)zone].AirDefense += b.Anti_Air();
                            ZoneInfo[(int)zone].InfantryDefense += b.Anti_Infantry();
                        }
                    }
                }
            }
            else
            {
                Radius = 0x0200;
            }
        }
    }

    /// <summary>Expert_AI: pick the enemy (nearest, most damaging, biggest), raise unit limits to
    /// match, update the base state, rate each strategy's urgency and act on them, most urgent first.</summary>
    public int Expert_AI()
    {
        if (Enemy != HousesType.HOUSE_NONE)
        {
            var h = game.HouseOrNull(Enemy);
            if (h == null || !h.IsActive || h.IsDefeated || Is_Ally(h) || h.BScan == 0)
                Enemy = HousesType.HOUSE_NONE;
        }

        if (ActiveBScan != 0 && Center != 0 && Attack.Time == 0)
        {
            int close = 0;
            var enemy = HousesType.HOUSE_NONE;
            int maxunit = 0, maxinfantry = 0, maxvessel = 0, maxaircraft = 0, maxbuilding = 0;
            int enemycount = 0;
            for (var house = HousesType.HOUSE_FIRST; house < HousesType.HOUSE_COUNT; house++)
            {
                var h = game.HouseOrNull(house);
                if (h != null && h.IsActive && !h.IsDefeated && !Is_Ally(h))
                {
                    // Pick no enemy while any has no base yet (distances would be wrong).
                    if (!h.IsStarted)
                    {
                        enemy = HousesType.HOUSE_NONE;
                        break;
                    }

                    maxunit += h.CurUnits;
                    maxbuilding += h.CurBuildings;
                    maxinfantry += h.CurInfantry;
                    maxaircraft += h.CurAircraft;
                    enemycount++;

                    int value = ((int)TdDefines.MAP_CELL_W * 2) - TdMath.Distance(Center, h.Center);
                    value *= 2;
                    value += h.BuildingsKilled[(int)Class.House] * 5;
                    value += h.UnitsKilled[(int)Class.House];
                    value += h.CurUnits - CurUnits;
                    value += h.CurBuildings - CurBuildings;
                    value += (h.CurInfantry - CurInfantry) / 4;
                    if (house == LAEnemy)
                        value += 100;

                    if (value > close)
                    {
                        enemy = house;
                        close = value;
                    }
                }
            }

            Enemy = enemy;

            if (enemycount != 0)
            {
                maxunit /= enemycount;
                maxbuilding /= enemycount;
                maxinfantry /= enemycount;
                maxvessel /= enemycount;
                maxaircraft /= enemycount;
            }
            if ((uint)MaxBuilding < (uint)maxbuilding + 10)
                MaxBuilding = maxbuilding + 10;
            if ((uint)MaxUnit < (uint)maxunit + 10)
                MaxUnit = maxunit + 10;
            if ((uint)MaxInfantry < (uint)maxinfantry + 10)
                MaxInfantry = maxinfantry + 10;
            if ((uint)MaxAircraft < (uint)maxaircraft + 10)
                MaxAircraft = maxaircraft + 10;
        }

        // Base state transitions.
        if (State == StateType.STATE_ENDGAME)
        {
            Fire_Sale();
            Do_All_To_Hunt();
        }
        else
        {
            if (State == StateType.STATE_BUILDUP && Available_Money() < 25)
                State = StateType.STATE_BROKE;
            if (State == StateType.STATE_BROKE && Available_Money() >= 25)
                State = StateType.STATE_BUILDUP;
            if (State == StateType.STATE_ATTACKED && LATime + TdDefines.TICKS_PER_MINUTE < game.Frame)
                State = StateType.STATE_BUILDUP;
            if (State != StateType.STATE_ATTACKED && LATime + TdDefines.TICKS_PER_MINUTE > game.Frame)
                State = StateType.STATE_ATTACKED;
        }

        var urgency = new UrgencyType[(int)StrategyType.STRATEGY_COUNT];
        for (var strat = StrategyType.STRATEGY_FIRST; strat < StrategyType.STRATEGY_COUNT; strat++)
        {
            urgency[(int)strat] = strat switch
            {
                StrategyType.STRATEGY_BUILD_POWER => Check_Build_Power(),
                StrategyType.STRATEGY_BUILD_DEFENSE => Check_Build_Defense(),
                StrategyType.STRATEGY_BUILD_INCOME => Check_Build_Income(),
                StrategyType.STRATEGY_FIRE_SALE => Check_Fire_Sale(),
                StrategyType.STRATEGY_BUILD_ENGINEER => Check_Build_Engineer(),
                StrategyType.STRATEGY_BUILD_OFFENSE => Check_Build_Offense(),
                StrategyType.STRATEGY_RAISE_MONEY => Check_Raise_Money(),
                StrategyType.STRATEGY_RAISE_POWER => Check_Raise_Power(),
                StrategyType.STRATEGY_LOWER_POWER => Check_Lower_Power(),
                StrategyType.STRATEGY_ATTACK => Check_Attack(),
                _ => UrgencyType.URGENCY_NONE,
            };
        }

        // Act on every strategy at each urgency, most urgent first. (The source's comment says it
        // stops after the first level that acted; the code doesn't.)
        for (var u = UrgencyType.URGENCY_CRITICAL; u >= UrgencyType.URGENCY_LOW; u--)
        {
            bool acted = false;
            for (var strat = StrategyType.STRATEGY_FIRST; strat < StrategyType.STRATEGY_COUNT; strat++)
            {
                if (urgency[(int)strat] != u)
                    continue;
                switch (strat)
                {
                    case StrategyType.STRATEGY_BUILD_POWER: acted |= AI_Build_Power(u); break;
                    case StrategyType.STRATEGY_BUILD_DEFENSE: acted |= AI_Build_Defense(u); break;
                    case StrategyType.STRATEGY_BUILD_INCOME: acted |= AI_Build_Income(u); break;
                    case StrategyType.STRATEGY_FIRE_SALE: acted |= AI_Fire_Sale(u); break;
                    case StrategyType.STRATEGY_BUILD_ENGINEER: acted |= AI_Build_Engineer(u); break;
                    case StrategyType.STRATEGY_BUILD_OFFENSE: acted |= AI_Build_Offense(u); break;
                    case StrategyType.STRATEGY_RAISE_MONEY: acted |= AI_Raise_Money(u); break;
                    case StrategyType.STRATEGY_RAISE_POWER: acted |= AI_Raise_Power(u); break;
                    case StrategyType.STRATEGY_LOWER_POWER: acted |= AI_Lower_Power(u); break;
                    case StrategyType.STRATEGY_ATTACK: acted |= AI_Attack(u); break;
                }
            }
        }

        return TdDefines.TICKS_PER_SECOND * 5 + game.Random_Pick(1, TdDefines.TICKS_PER_SECOND / 2);
    }

    UrgencyType Check_Build_Power()
    {
        int frac = Power_Fraction();
        var urgency = UrgencyType.URGENCY_NONE;
        if (frac < 0x0100 && Can_Make_Money())
        {
            urgency = UrgencyType.URGENCY_LOW;
            if (frac < 0x00C0)
                urgency = UrgencyType.URGENCY_MEDIUM;
        }
        return urgency;
    }

    UrgencyType Check_Build_Defense() => UrgencyType.URGENCY_NONE;
    UrgencyType Check_Build_Offense() => UrgencyType.URGENCY_NONE;

    UrgencyType Check_Attack()
    {
        if (game.Frame > TdDefines.TICKS_PER_MINUTE && Attack.Time == 0)
            return State == StateType.STATE_ATTACKED ? UrgencyType.URGENCY_LOW : UrgencyType.URGENCY_CRITICAL;
        return UrgencyType.URGENCY_NONE;
    }

    UrgencyType Check_Build_Income() => UrgencyType.URGENCY_NONE;

    /// <summary>No factories left at all: sell everything, the game is basically over.</summary>
    UrgencyType Check_Fire_Sale()
    {
        long factories = (long)(TdDefines.STRUCTF_BARRACKS | TdDefines.STRUCTF_CONST | TdDefines.STRUCTF_AIRSTRIP | TdDefines.STRUCTF_WEAP | TdDefines.STRUCTF_HELIPAD);
        if (State != StateType.STATE_ATTACKED && CurBuildings != 0 && (ActiveBScan & factories) == 0)
            return UrgencyType.URGENCY_CRITICAL;
        return UrgencyType.URGENCY_NONE;
    }

    UrgencyType Check_Build_Engineer() => UrgencyType.URGENCY_NONE;

    UrgencyType Check_Raise_Money()
    {
        var urgency = UrgencyType.URGENCY_NONE;
        if (Available_Money() < 100)
            urgency = UrgencyType.URGENCY_LOW;
        if (Available_Money() < 2000 && !Can_Make_Money())
            urgency++;
        return urgency;
    }

    UrgencyType Check_Lower_Power() => Power > Drain + 300 ? UrgencyType.URGENCY_LOW : UrgencyType.URGENCY_NONE;

    UrgencyType Check_Raise_Power()
    {
        var urgency = UrgencyType.URGENCY_NONE;
        if (Power_Fraction() < game.Rule.PowerEmergencyFraction && Power < Drain - 400)
        {
            urgency = UrgencyType.URGENCY_MEDIUM;
            if (State == StateType.STATE_ATTACKED)
                urgency++;
        }
        return urgency;
    }

    /// <summary>AI_Attack: most armed units go hunting (all of them once the base is gone); the
    /// others guarding the base may shuffle to another spot. Then wait for the next attack.</summary>
    bool AI_Attack(UrgencyType urgency)
    {
        bool shuffle = !((game.Frame > TdDefines.TICKS_PER_MINUTE && CurBuildings == 0) || Percent_Chance(33));
        bool forced = CurBuildings == 0;
        int index;
        for (index = 0; index < game.Aircraft.Count; index++)
        {
            var a = game.Aircraft.Ptr(index);
            if (!a.IsInLimbo && a.House == this && a.Strength > 0)
                if (!shuffle && a.Is_Weapon_Equipped() && (forced || Percent_Chance(75)))
                    a.Assign_Mission(MissionType.MISSION_HUNT);
        }
        for (index = 0; index < game.Units.Count; index++)
        {
            var u = game.Units.Ptr(index);
            if (!u.IsInLimbo && u.House == this && u.Strength > 0)
            {
                if (!shuffle && u.Is_Weapon_Equipped() && (forced || Percent_Chance(75)))
                    u.Assign_Mission(MissionType.MISSION_HUNT);
                else if (Percent_Chance(20) && u.Mission == MissionType.MISSION_GUARD_AREA && Which_Zone(u) != ZoneType.ZONE_NONE)
                    u.ArchiveTarget = Target.As_Target(Where_To_Go(u));
            }
        }
        for (index = 0; index < game.Infantry.Count; index++)
        {
            var i = game.Infantry.Ptr(index);
            if (!i.IsInLimbo && i.House == this && i.Strength > 0)
            {
                if (!shuffle && (i.Is_Weapon_Equipped() || i.Class.Type == InfantryType.INFANTRY_E7) && (forced || Percent_Chance(75)))
                    i.Assign_Mission(MissionType.MISSION_HUNT);
                else if (Percent_Chance(20) && i.Mission == MissionType.MISSION_GUARD_AREA && Which_Zone(i) != ZoneType.ZONE_NONE)
                    i.ArchiveTarget = Target.As_Target(Where_To_Go(i));
            }
        }
        Attack.Set(game.Rule.AttackInterval * game.Random_Pick(TdDefines.TICKS_PER_MINUTE / 2, TdDefines.TICKS_PER_MINUTE * 2));
        return true;
    }

    bool AI_Build_Power(UrgencyType urgency) => false;
    bool AI_Build_Defense(UrgencyType urgency) => false;
    bool AI_Build_Offense(UrgencyType urgency) => false;
    bool AI_Build_Income(UrgencyType urgency) => false;

    bool AI_Fire_Sale(UrgencyType urgency)
    {
        if (CurBuildings != 0 && urgency == UrgencyType.URGENCY_CRITICAL)
        {
            Fire_Sale();
            Do_All_To_Hunt();
            return true;
        }
        return false;
    }

    bool AI_Build_Engineer(UrgencyType urgency) => false;

    bool AI_Lower_Power(UrgencyType urgency)
    {
        var b = Find_Building(StructType.STRUCT_POWER);
        if (b != null)
        {
            b.Sell_Back(1);
            return true;
        }
        b = Find_Building(StructType.STRUCT_ADVANCED_POWER);
        if (b != null)
        {
            b.Sell_Back(1);
            return true;
        }
        return false;
    }

    static readonly (StructType Structure, UrgencyType Urgency)[] RaisePowerTypes =
    [
        (StructType.STRUCT_BIO_LAB, UrgencyType.URGENCY_LOW),
        (StructType.STRUCT_EYE, UrgencyType.URGENCY_MEDIUM),
        (StructType.STRUCT_RADAR, UrgencyType.URGENCY_MEDIUM),
        (StructType.STRUCT_REPAIR, UrgencyType.URGENCY_MEDIUM),
        (StructType.STRUCT_OBELISK, UrgencyType.URGENCY_HIGH),
        (StructType.STRUCT_TURRET, UrgencyType.URGENCY_HIGH),
        (StructType.STRUCT_ATOWER, UrgencyType.URGENCY_HIGH),
        (StructType.STRUCT_GTOWER, UrgencyType.URGENCY_HIGH),
    ];

    static readonly (StructType Structure, UrgencyType Urgency)[] RaiseMoneyTypes =
    [
        (StructType.STRUCT_BIO_LAB, UrgencyType.URGENCY_LOW),
        (StructType.STRUCT_EYE, UrgencyType.URGENCY_MEDIUM),
        (StructType.STRUCT_RADAR, UrgencyType.URGENCY_MEDIUM),
        (StructType.STRUCT_STORAGE, UrgencyType.URGENCY_LOW),
        (StructType.STRUCT_REPAIR, UrgencyType.URGENCY_MEDIUM),
        (StructType.STRUCT_OBELISK, UrgencyType.URGENCY_HIGH),
        (StructType.STRUCT_TURRET, UrgencyType.URGENCY_HIGH),
        (StructType.STRUCT_ATOWER, UrgencyType.URGENCY_HIGH),
        (StructType.STRUCT_GTOWER, UrgencyType.URGENCY_HIGH),
        (StructType.STRUCT_HELIPAD, UrgencyType.URGENCY_MEDIUM),
        (StructType.STRUCT_POWER, UrgencyType.URGENCY_HIGH),
        (StructType.STRUCT_AIRSTRIP, UrgencyType.URGENCY_HIGH),
        (StructType.STRUCT_CONST, UrgencyType.URGENCY_CRITICAL),
    ];

    bool Sell_First_Of((StructType Structure, UrgencyType Urgency)[] types, UrgencyType urgency)
    {
        foreach (var (structure, need) in types)
        {
            if (urgency >= need)
            {
                var b = Find_Building(structure);
                if (b != null)
                {
                    b.Sell_Back(1);
                    return true;
                }
            }
        }
        return false;
    }

    bool AI_Raise_Power(UrgencyType urgency) => Sell_First_Of(RaisePowerTypes, urgency);
    bool AI_Raise_Money(UrgencyType urgency) => Sell_First_Of(RaiseMoneyTypes, urgency);

    void Choose(UrgencyType urgency, StructType type)
    {
        if (BuildChoice.Count < (int)StructType.STRUCT_COUNT)
            BuildChoice.Add(new BuildChoiceClass(urgency, type));
    }

    /// <summary>AI_Building: decide the next building — power when short, refineries, barracks, a
    /// war factory, defences, SAMs (and radar) against an air threat, helipads and airstrips —
    /// and pick the most urgent.</summary>
    public int AI_Building()
    {
        if (BuildStructure != StructType.STRUCT_NONE)
            return TdDefines.TICKS_PER_SECOND;

        if (IsBaseBuilding)
        {
            // (The base-size limit is computed but no longer applied, as the source.)
            int quant = 0;
            for (var h = HousesType.HOUSE_FIRST; h < HousesType.HOUSE_COUNT; h++)
            {
                var hptr = game.HouseOrNull(h);
                if (hptr != null && hptr.IsActive && hptr.IsHuman && quant < hptr.CurBuildings)
                    quant = hptr.CurBuildings;
            }
            quant += game.Rule.BaseSizeAdd;

            BuildChoice.Clear();
            long money = Available_Money();
            bool hasincome = BQuantity[(int)StructType.STRUCT_REFINERY] > 0 && !IsTiberiumShort && UQuantity[(int)UnitType.UNIT_HARVESTER] > 0;
            HouseClass? enemy = Enemy != HousesType.HOUSE_NONE ? game.HouseOrNull(Enemy) : null;
            var rule = game.Rule;

            // Power when there isn't enough.
            var b = game.Types.Building(StructType.STRUCT_ADVANCED_POWER);
            if (Can_Build(b, ActLike) && Power <= Drain + rule.PowerSurplus && Cost_Of(b) < money)
            {
                Choose(BQuantity[(int)StructType.STRUCT_REFINERY] == 0 ? UrgencyType.URGENCY_LOW : UrgencyType.URGENCY_MEDIUM, b.Type);
            }
            else
            {
                b = game.Types.Building(StructType.STRUCT_POWER);
                if (Can_Build(b, ActLike) && Power <= Drain + rule.PowerSurplus && Cost_Of(b) < money)
                    Choose(BQuantity[(int)StructType.STRUCT_REFINERY] == 0 ? UrgencyType.URGENCY_LOW : UrgencyType.URGENCY_MEDIUM, b.Type);
            }

            // A refinery.
            uint current = (uint)BQuantity[(int)StructType.STRUCT_REFINERY];
            if (!IsTiberiumShort && current < Round_Up(Ratio(rule.RefineryRatio, CurBuildings)) && current < (uint)rule.RefineryLimit)
            {
                b = game.Types.Building(StructType.STRUCT_REFINERY);
                if (Can_Build(b, ActLike) && (money > Cost_Of(b) || hasincome))
                    Choose(BQuantity[(int)StructType.STRUCT_REFINERY] == 0 ? UrgencyType.URGENCY_HIGH : UrgencyType.URGENCY_MEDIUM, b.Type);
            }

            // Barracks (or Hand of Nod), when troopers can be paid for.
            current = (uint)(BQuantity[(int)StructType.STRUCT_BARRACKS] + BQuantity[(int)StructType.STRUCT_HAND]);
            if (current < Round_Up(Ratio(rule.BarracksRatio, CurBuildings)) && current < (uint)rule.BarracksLimit && (money > 300 || hasincome))
            {
                b = game.Types.Building(StructType.STRUCT_BARRACKS);
                if (Can_Build(b, ActLike) && (Cost_Of(b) < money || hasincome))
                {
                    Choose(current > 0 ? UrgencyType.URGENCY_LOW : UrgencyType.URGENCY_MEDIUM, b.Type);
                }
                else
                {
                    b = game.Types.Building(StructType.STRUCT_HAND);
                    if (Can_Build(b, ActLike) && (Cost_Of(b) < money || hasincome))
                        Choose(current > 0 ? UrgencyType.URGENCY_LOW : UrgencyType.URGENCY_MEDIUM, b.Type);
                }
            }

            // A war factory.
            current = (uint)BQuantity[(int)StructType.STRUCT_WEAP];
            if (current < Round_Up(Ratio(rule.WarRatio, CurBuildings)) && current < (uint)rule.WarLimit && (money > 2000 || hasincome))
            {
                b = game.Types.Building(StructType.STRUCT_WEAP);
                if (Can_Build(b, ActLike) && (Cost_Of(b) < money || hasincome))
                    Choose(current > 0 ? UrgencyType.URGENCY_LOW : UrgencyType.URGENCY_MEDIUM, b.Type);
            }

            // Base defence: an Obelisk if possible, else a turret.
            current = (uint)(BQuantity[(int)StructType.STRUCT_TURRET] + BQuantity[(int)StructType.STRUCT_OBELISK]);
            if (current < Round_Up(Ratio(rule.DefenseRatio, CurBuildings)) && current < (uint)rule.DefenseLimit)
            {
                b = game.Types.Building(StructType.STRUCT_OBELISK);
                if (Can_Build(b, ActLike) && (Cost_Of(b) < money || hasincome))
                {
                    Choose(UrgencyType.URGENCY_MEDIUM, b.Type);
                }
                else
                {
                    b = game.Types.Building(StructType.STRUCT_TURRET);
                    if (Can_Build(b, ActLike) && (Cost_Of(b) < money || hasincome))
                        Choose(UrgencyType.URGENCY_MEDIUM, b.Type);
                }
            }

            // Air defence, only when some enemy has aircraft.
            current = (uint)BQuantity[(int)StructType.STRUCT_SAM];
            if (current < Round_Up(Ratio(rule.AARatio, CurBuildings)) && current < (uint)rule.AALimit)
            {
                bool airthreat = false;
                int threatQuantity = 0;
                if (enemy != null && enemy.AScan != 0)
                {
                    airthreat = true;
                    threatQuantity = enemy.CurAircraft;
                }
                if (!airthreat)
                {
                    for (var house = HousesType.HOUSE_FIRST; house < HousesType.HOUSE_COUNT; house++)
                    {
                        var h = game.HouseOrNull(house);
                        if (h != null && !Is_Ally(house) && h.AScan != 0)
                        {
                            airthreat = true;
                            break;
                        }
                    }
                }
                if (airthreat)
                {
                    if (BQuantity[(int)StructType.STRUCT_RADAR] == 0)
                    {
                        b = game.Types.Building(StructType.STRUCT_RADAR);
                        if (Can_Build(b, ActLike) && (Cost_Of(b) < money || hasincome))
                            Choose(UrgencyType.URGENCY_HIGH, b.Type);
                    }
                    b = game.Types.Building(StructType.STRUCT_SAM);
                    if (Can_Build(b, ActLike) && (Cost_Of(b) < money || hasincome))
                        Choose(current < (uint)threatQuantity ? UrgencyType.URGENCY_HIGH : UrgencyType.URGENCY_MEDIUM, b.Type);
                }
            }

            // A helipad.
            current = (uint)BQuantity[(int)StructType.STRUCT_HELIPAD];
            if (current < Round_Up(Ratio(rule.HelipadRatio, CurBuildings)) && current < (uint)rule.HelipadLimit)
            {
                b = game.Types.Building(StructType.STRUCT_HELIPAD);
                if (Can_Build(b, ActLike) && (Cost_Of(b) < money || hasincome))
                {
                    int threatQuantity = enemy?.CurAircraft ?? 0;
                    Choose((uint)CurAircraft < (uint)threatQuantity ? UrgencyType.URGENCY_HIGH : UrgencyType.URGENCY_MEDIUM, b.Type);
                }
            }

            // An airstrip.
            current = (uint)BQuantity[(int)StructType.STRUCT_AIRSTRIP];
            if (current < Round_Up(Ratio(rule.AirstripRatio, CurBuildings)) && current < (uint)rule.AirstripLimit)
            {
                b = game.Types.Building(StructType.STRUCT_AIRSTRIP);
                if (Can_Build(b, ActLike) && (Cost_Of(b) < money || hasincome))
                {
                    int threatQuantity = enemy?.CurAircraft ?? 0;
                    Choose((uint)CurAircraft < (uint)threatQuantity ? UrgencyType.URGENCY_HIGH : UrgencyType.URGENCY_MEDIUM, b.Type);
                }
            }

            // The most urgent choice (the first one, on a tie).
            var best = UrgencyType.URGENCY_NONE;
            int bestindex = 0;
            for (int index = 0; index < BuildChoice.Count; index++)
            {
                if (BuildChoice[index].Urgency > best)
                {
                    bestindex = index;
                    best = BuildChoice[index].Urgency;
                }
            }
            if (best != UrgencyType.URGENCY_NONE)
                BuildStructure = BuildChoice[bestindex].Structure;
        }
        return TdDefines.TICKS_PER_SECOND;
    }

    /// <summary>AI_Unit: a harvester per refinery; otherwise what teams want, then (base building)
    /// a weighted random pick favouring armed vehicles.</summary>
    public int AI_Unit()
    {
        if (BuildUnit != UnitType.UNIT_NONE)
            return TdDefines.TICKS_PER_SECOND;
        if ((uint)CurUnits >= (uint)MaxUnit)
            return TdDefines.TICKS_PER_SECOND;

        if (IQ >= game.Rule.IQHarvester && !IsTiberiumShort && !IsHuman &&
            BQuantity[(int)StructType.STRUCT_REFINERY] > UQuantity[(int)UnitType.UNIT_HARVESTER] && Difficulty != DiffType.DIFF_HARD)
        {
            if ((uint)game.Types.Unit(UnitType.UNIT_HARVESTER).Level <= (uint)game.BuildLevel)
            {
                BuildUnit = UnitType.UNIT_HARVESTER;
                return TdDefines.TICKS_PER_SECOND;
            }
        }

        int count = (int)UnitType.UNIT_COUNT;
        var counter = new int[count];
        int index;
        for (index = 0; index < game.Teams.Count; index++)
        {
            var tptr = game.Teams.Ptr(index);
            var team = tptr.Class;
            if (((team.IsReinforcable && !tptr.IsFullStrength) || (!tptr.IsForcedActive && !tptr.IsHasBeen && !tptr.IsAltered)) && team.House == Class.House)
                for (int sub = 0; sub < team.ClassCount; sub++)
                    if (team.Class[sub] is UnitTypeClass ut)
                        counter[(int)ut.Type] = 1;
        }

        for (index = 0; index < game.TeamTypes.Count; index++)
        {
            var team = game.TeamTypes.Ptr(index);
            if (team.House == Class.House && team.IsPrebuilt && (!team.IsAutocreate || IsAlerted))
                for (int sub = 0; sub < team.ClassCount; sub++)
                    if (team.Class[sub] is UnitTypeClass ut)
                        counter[(int)ut.Type] = Math.Max(counter[(int)ut.Type], 1);
        }

        for (int uindex = 0; uindex < game.Units.Count; uindex++)
        {
            var unit = game.Units.Ptr(uindex);
            if (counter[(int)unit.Class.Type] > 0)
                counter[(int)unit.Class.Type]--;
        }

        int bestval = -1, bestcount = 0;
        var bestlist = new UnitType[count];
        for (var utype = UnitType.UNIT_FIRST; utype < UnitType.UNIT_COUNT; utype++)
        {
            var t = game.Types.Unit(utype);
            if (counter[(int)utype] > 0 && Can_Build(t, Class.House) && t.Cost_Of(game.Special) <= Available_Money())
            {
                if (bestval == -1 || bestval < counter[(int)utype])
                {
                    bestval = counter[(int)utype];
                    bestcount = 0;
                }
                bestlist[bestcount++] = utype;
            }
        }
        if (bestcount != 0)
            BuildUnit = bestlist[game.Random_Pick(0, bestcount - 1)];

        if (IsBaseBuilding)
        {
            var weights = new int[count];
            int total = 0;
            for (var u = UnitType.UNIT_FIRST; u < UnitType.UNIT_COUNT; u++)
            {
                var utype = game.Types.Unit(u);
                if (Can_Build(utype, ActLike) && utype.Type != UnitType.UNIT_HARVESTER)
                    weights[(int)u] = utype.Primary != WeaponType.WEAPON_NONE ? 20 : 1;
                else
                    weights[(int)u] = 0;
                total += weights[(int)u];
            }
            if (total > 0)
            {
                int choice = game.Random_Pick(0, total - 1);
                for (var u = UnitType.UNIT_FIRST; u < UnitType.UNIT_COUNT; u++)
                {
                    if (choice < weights[(int)u])
                    {
                        BuildUnit = u;
                        break;
                    }
                    choice -= weights[(int)u];
                }
            }
        }
        return TdDefines.TICKS_PER_SECOND;
    }

    public int AI_Vessel() => TdDefines.TICKS_PER_SECOND;   // (TD has no naval production)

    /// <summary>AI_Infantry: (base building) a weighted pick among the infantry worth building —
    /// when the enemy has more of a type, money is plentiful, or the base needs bodies.</summary>
    public int AI_Infantry()
    {
        if (BuildInfantry != InfantryType.INFANTRY_NONE)
            return TdDefines.TICKS_PER_SECOND;
        if ((uint)CurInfantry >= (uint)MaxInfantry)
            return TdDefines.TICKS_PER_SECOND;

        if (IsBaseBuilding)
        {
            HouseClass? enemy = Enemy != HousesType.HOUSE_NONE ? game.HouseOrNull(Enemy) : null;
            var types = new InfantryType[(int)InfantryType.INFANTRY_COUNT];
            var values = new int[(int)InfantryType.INFANTRY_COUNT];
            int count = 0, total = 0;
            for (var index = InfantryType.INFANTRY_FIRST; index < InfantryType.INFANTRY_COUNT; index++)
            {
                var itype = game.Types.Infantry(index);
                if (Can_Build(itype, ActLike) && (uint)itype.Level <= (uint)game.BuildLevel)
                {
                    values[count] = 0;
                    if ((enemy != null && enemy.IQuantity[(int)index] > IQuantity[(int)index]) ||
                        Available_Money() > game.Rule.InfantryReserve ||
                        CurInfantry < CurBuildings * game.Rule.InfantryBaseMult)
                    {
                        switch (index)
                        {
                            case InfantryType.INFANTRY_E1: values[count] = 3; break;
                            case InfantryType.INFANTRY_E2: values[count] = 5; break;
                            case InfantryType.INFANTRY_E3: values[count] = 2; break;
                            case InfantryType.INFANTRY_E4: values[count] = 5; break;
                            case InfantryType.INFANTRY_E7:
                                if (CurInfantry > 5)
                                    values[count] = 1 - Math.Max(IQuantity[(int)index], 0);
                                break;
                            default: values[count] = 0; break;
                        }
                    }
                    if (values[count] > 0)
                    {
                        types[count] = index;
                        total += values[count];
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                int pick = game.Random_Pick(0, total - 1);
                for (int index = 0; index < count; index++)
                {
                    if (pick < values[index])
                    {
                        BuildInfantry = types[index];
                        break;
                    }
                    pick -= values[index];
                }
            }
        }
        return TdDefines.TICKS_PER_SECOND;
    }

    /// <summary>AI_Aircraft: an Apache or an Orca for each airstrip... as the source checks.</summary>
    public int AI_Aircraft()
    {
        if (!IsHuman && IQ >= game.Rule.IQAircraft)
        {
            if (BuildAircraft != AircraftType.AIRCRAFT_NONE)
                return TdDefines.TICKS_PER_SECOND;
            if ((uint)CurAircraft >= (uint)MaxAircraft)
                return TdDefines.TICKS_PER_SECOND;

            int air = AQuantity[(int)AircraftType.AIRCRAFT_HELICOPTER] + AQuantity[(int)AircraftType.AIRCRAFT_ORCA];
            var heli = game.Types.Aircraft(AircraftType.AIRCRAFT_HELICOPTER);
            if (Can_Build(heli, ActLike) && (uint)heli.Level <= (uint)game.BuildLevel && BQuantity[(int)StructType.STRUCT_AIRSTRIP] > air)
            {
                BuildAircraft = AircraftType.AIRCRAFT_HELICOPTER;
                return TdDefines.TICKS_PER_SECOND;
            }
            var orca = game.Types.Aircraft(AircraftType.AIRCRAFT_ORCA);
            if (Can_Build(orca, ActLike) && (uint)orca.Level <= (uint)game.BuildLevel && BQuantity[(int)StructType.STRUCT_AIRSTRIP] > air)
            {
                BuildAircraft = AircraftType.AIRCRAFT_ORCA;
                return TdDefines.TICKS_PER_SECOND;
            }
        }
        return TdDefines.TICKS_PER_SECOND;
    }

    /// <summary>Production_Begun: the suggestion that was started is cleared.</summary>
    public void Production_Begun(TechnoClass? product)
    {
        switch (product)
        {
            case UnitClass u when u.Class.Type == BuildUnit: BuildUnit = UnitType.UNIT_NONE; break;
            case InfantryClass i when i.Class.Type == BuildInfantry: BuildInfantry = InfantryType.INFANTRY_NONE; break;
            case BuildingClass b when b.Class.Type == BuildStructure: BuildStructure = StructType.STRUCT_NONE; break;
            case AircraftClass a when a.Class.Type == BuildAircraft: BuildAircraft = AircraftType.AIRCRAFT_NONE; break;
        }
    }

    /// <summary>Tracking_Remove: one fewer of the object's kind and type.</summary>
    public void Tracking_Remove(TechnoClass techno)
    {
        switch (techno)
        {
            case BuildingClass b:
                CurBuildings--;
                BQuantity[(int)b.Class.Type]--;
                break;
            case AircraftClass a:
                CurAircraft--;
                AQuantity[(int)a.Class.Type]--;
                break;
            case InfantryClass i:
                CurInfantry--;
                if (!i.IsTechnician)
                    IQuantity[(int)i.Class.Type]--;
                break;
            case UnitClass u:
                CurUnits--;
                UQuantity[(int)u.Class.Type]--;
                break;
        }
    }

    /// <summary>Tracking_Add: one more of the object's kind and type (and its scan bit). Units are
    /// also counted by FootClass, so CurUnits goes up twice for them, as in the remaster.</summary>
    public void Tracking_Add(TechnoClass techno)
    {
        switch (techno)
        {
            case BuildingClass b:
                CurBuildings++;
                BQuantity[(int)b.Class.Type]++;
                BScan |= 1L << (int)b.Class.Type;
                break;
            case AircraftClass a:
                CurAircraft++;
                AQuantity[(int)a.Class.Type]++;
                AScan |= 1L << (int)a.Class.Type;
                break;
            case InfantryClass i:
                CurInfantry++;
                if (!i.IsTechnician)
                {
                    IQuantity[(int)i.Class.Type]++;
                    IScan |= 1L << (int)i.Class.Type;
                }
                break;
            case UnitClass u:
                CurUnits++;
                UQuantity[(int)u.Class.Type]++;
                UScan |= 1L << (int)u.Class.Type;
                break;
        }
    }

    public ref int Factory_Counter(RTTIType rtti, out bool ok)
    {
        ok = true;
        switch (rtti)
        {
            case RTTIType.RTTI_UNITTYPE:
            case RTTIType.RTTI_UNIT:
                return ref UnitFactories;
            case RTTIType.RTTI_AIRCRAFTTYPE:
            case RTTIType.RTTI_AIRCRAFT:
                return ref AircraftFactories;
            case RTTIType.RTTI_INFANTRYTYPE:
            case RTTIType.RTTI_INFANTRY:
                return ref InfantryFactories;
            case RTTIType.RTTI_BUILDINGTYPE:
            case RTTIType.RTTI_BUILDING:
                return ref BuildingFactories;
        }
        ok = false;
        return ref SpecialFactories;
    }

    public void Active_Remove(TechnoClass? techno)
    {
        if (techno is BuildingClass b)
        {
            ref int f = ref Factory_Counter(b.Class.ToBuild, out bool ok);
            if (ok)
                f--;
        }
    }

    public void Active_Add(TechnoClass? techno)
    {
        if (techno is BuildingClass b)
        {
            ref int f = ref Factory_Counter(b.Class.ToBuild, out bool ok);
            if (ok)
                f++;
        }
    }

    /// <summary>Which_Zone: the base core within the radius; north/east/south/west out to four radii.</summary>
    public ZoneType Which_Zone(uint coord)
    {
        if (coord == 0)
            return ZoneType.ZONE_NONE;
        int distance = TdMath.Distance(Center, coord);
        if (distance <= Radius)
            return ZoneType.ZONE_CORE;
        if (distance > Radius * 4)
            return ZoneType.ZONE_NONE;
        int facing = TdMath.Direction(Center, coord);
        if (facing < (int)DirType.DIR_NE || facing > (int)DirType.DIR_NW)
            return ZoneType.ZONE_NORTH;
        if (facing >= (int)DirType.DIR_NE && facing < (int)DirType.DIR_SE)
            return ZoneType.ZONE_EAST;
        if (facing >= (int)DirType.DIR_SE && facing < (int)DirType.DIR_SW)
            return ZoneType.ZONE_SOUTH;
        return ZoneType.ZONE_WEST;
    }

    public ZoneType Which_Zone(ObjectClass? obj) => obj == null ? ZoneType.ZONE_NONE : Which_Zone(obj.Center_Coord());
    public ZoneType Which_Zone_Cell(int cell) => Which_Zone(TdMath.CellCoord(cell));

    /// <summary>Recalc_Attributes (once a frame, all houses): the scan bits rebuilt from the objects.</summary>
    public static void Recalc_Attributes(TdGame game)
    {
        int index;
        for (index = 0; index < game.Houses.Count; index++)
        {
            var house = game.Houses.Ptr(index);
            house.BScan = house.ActiveBScan = 0;
            house.IScan = house.ActiveIScan = 0;
            house.UScan = house.ActiveUScan = 0;
            house.AScan = house.ActiveAScan = 0;
        }
        for (index = 0; index < game.Units.Count; index++)
        {
            var unit = game.Units.Ptr(index);
            unit.House.UScan |= 1L << (int)unit.Class.Type;
            if (unit.IsLocked && !unit.IsInLimbo)
                unit.House.ActiveUScan |= 1L << (int)unit.Class.Type;
        }
        for (index = 0; index < game.Infantry.Count; index++)
        {
            var infantry = game.Infantry.Ptr(index);
            infantry.House.IScan |= 1L << (int)infantry.Class.Type;
            if (infantry.IsLocked && !infantry.IsInLimbo)
                infantry.House.ActiveIScan |= 1L << (int)infantry.Class.Type;
        }
        for (index = 0; index < game.Aircraft.Count; index++)
        {
            var aircraft = game.Aircraft.Ptr(index);
            aircraft.House.AScan |= 1L << (int)aircraft.Class.Type;
            if (aircraft.IsLocked && !aircraft.IsInLimbo)
                aircraft.House.ActiveAScan |= 1L << (int)aircraft.Class.Type;
        }
        for (index = 0; index < game.Buildings.Count; index++)
        {
            var building = game.Buildings.Ptr(index);
            if ((int)building.Class.Type < 32)
            {
                building.House.BScan |= 1L << (int)building.Class.Type;
                if (building.IsLocked && !building.IsInLimbo)
                {
                    building.House.ActiveBScan |= 1L << (int)building.Class.Type;
                    building.House.OldBScan |= 1L << (int)building.Class.Type;
                }
            }
        }
    }

    public int Zone_Cell(ZoneType zone) => zone switch
    {
        ZoneType.ZONE_CORE => TdMath.CoordCell(Center),
        ZoneType.ZONE_NORTH => TdMath.CoordCell(TdMath.CoordMove(Center, (byte)DirType.DIR_N, Radius * 3)),
        ZoneType.ZONE_EAST => TdMath.CoordCell(TdMath.CoordMove(Center, (byte)DirType.DIR_E, Radius * 3)),
        ZoneType.ZONE_WEST => TdMath.CoordCell(TdMath.CoordMove(Center, (byte)DirType.DIR_W, Radius * 3)),
        ZoneType.ZONE_SOUTH => TdMath.CoordCell(TdMath.CoordMove(Center, (byte)DirType.DIR_S, Radius * 3)),
        _ => 0,
    };

    /// <summary>Where_To_Go: unarmed objects stay in the core; armed ones guard a random outer zone.</summary>
    public int Where_To_Go(FootClass obj)
    {
        var zone = obj.Anti_Air() + obj.Anti_Armor() + obj.Anti_Infantry() == 0
            ? ZoneType.ZONE_CORE
            : (ZoneType)game.Random_Pick((int)ZoneType.ZONE_NORTH, (int)ZoneType.ZONE_WEST);
        int cell = Random_Cell_In_Zone(zone);
        return game.Map.Nearby_Location(cell);
    }

    /// <summary>Find_Juicy_Target: the nearest enemy vehicle outside its own base (anti-air ones look
    /// twice as far, harvesters half).</summary>
    public uint Find_Juicy_Target(uint coord)
    {
        UnitClass? best = null;
        int value = 0;
        for (int index = 0; index < game.Units.Count; index++)
        {
            var unit = game.Units.Ptr(index);
            if (!unit.IsInLimbo && !Is_Ally(unit) && unit.House.Which_Zone(unit) == ZoneType.ZONE_NONE)
            {
                int val = TdMath.Distance(coord, unit.Center_Coord());
                if (unit.Anti_Air() != 0) val *= 2;
                if (unit.Class.Type == UnitType.UNIT_HARVESTER) val /= 2;
                if (value == 0 || val < value)
                {
                    value = val;
                    best = unit;
                }
            }
        }
        return best?.As_Target() ?? Target.TARGET_NONE;
    }

    public int Get_Quantity(AircraftType aircraft) => AQuantity[(int)aircraft];
    public int Get_Quantity(StructType building) => BQuantity[(int)building];

    public FactoryClass? Fetch_Factory(RTTIType rtti)
    {
        int factoryIndex = rtti switch
        {
            RTTIType.RTTI_INFANTRY or RTTIType.RTTI_INFANTRYTYPE => InfantryFactory,
            RTTIType.RTTI_UNIT or RTTIType.RTTI_UNITTYPE => UnitFactory,
            RTTIType.RTTI_BUILDING or RTTIType.RTTI_BUILDINGTYPE => BuildingFactory,
            RTTIType.RTTI_AIRCRAFT or RTTIType.RTTI_AIRCRAFTTYPE => AircraftFactory,
            _ => -1,
        };
        return factoryIndex != -1 ? game.Factories.RawPtr(factoryIndex) : null;
    }

    public int Factory_Count(RTTIType rtti)
    {
        ref int f = ref Factory_Counter(rtti, out bool ok);
        return ok ? f : 0;
    }

    /// <summary>Fire_Sale: sell every building.</summary>
    public bool Fire_Sale()
    {
        if (CurBuildings > 0)
        {
            for (int index = 0; index < game.Buildings.Count; index++)
            {
                var b = game.Buildings.Ptr(index);
                if (!b.IsInLimbo && b.House == this && b.Strength > 0)
                    b.Sell_Back(1);
            }
            return true;
        }
        return false;
    }

    /// <summary>Do_All_To_Hunt: everything leaves its team and hunts.</summary>
    public void Do_All_To_Hunt()
    {
        int index;
        for (index = 0; index < game.Units.Count; index++)
        {
            var unit = game.Units.Ptr(index);
            if (unit.House == this && unit.IsDown && !unit.IsInLimbo)
            {
                unit.Team?.Remove(unit);
                unit.Assign_Mission(MissionType.MISSION_HUNT);
            }
        }
        for (index = 0; index < game.Infantry.Count; index++)
        {
            var infantry = game.Infantry.Ptr(index);
            if (infantry.House == this && infantry.IsDown && !infantry.IsInLimbo)
            {
                infantry.Team?.Remove(infantry);
                infantry.Assign_Mission(MissionType.MISSION_HUNT);
            }
        }
        for (index = 0; index < game.Aircraft.Count; index++)
        {
            var aircraft = game.Aircraft.Ptr(index);
            if (aircraft.House == this && aircraft.IsDown && !aircraft.IsInLimbo)
            {
                aircraft.Team?.Remove(aircraft);
                aircraft.Assign_Mission(MissionType.MISSION_HUNT);
            }
        }
    }

    public bool Is_Allowed_To_Ally(HousesType house)
    {
        if (house == HousesType.HOUSE_NONE)
            return false;
        if (Is_Ally(house))
            return false;
        if (game.ScenarioInit != 0)
            return true;
        if (game.GameToPlay == GameType.GAME_NORMAL)
            return false;
        if (IsDefeated)
            return false;

        int housecount = 0, allycount = 0;
        for (var house2 = HousesType.HOUSE_MULTI1; house2 < HousesType.HOUSE_COUNT; house2++)
        {
            var hptr = game.HouseOrNull(house2);
            if (hptr != null && hptr.IsActive && !hptr.IsDefeated)
            {
                housecount++;
                if (Is_Ally(hptr))
                    allycount++;
            }
        }
        return housecount != allycount + 1;
    }

    /// <summary>Computer_Paranoid: computers ally with each other against the humans (not in GlyphX
    /// skirmish, as the remaster).</summary>
    public static void Computer_Paranoid(TdGame game)
    {
        if (game.GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER)
            return;
        for (var house = HousesType.HOUSE_MULTI1; house < HousesType.HOUSE_COUNT; house++)
        {
            var hptr = game.HouseOrNull(house);
            if (hptr != null && hptr.IsActive && !hptr.IsDefeated && !hptr.IsHuman)
            {
                hptr.IsParanoid = true;
                for (var house2 = HousesType.HOUSE_MULTI1; house2 < HousesType.HOUSE_COUNT; house2++)
                {
                    var hptr2 = game.HouseOrNull(house2);
                    if (hptr2 != null && hptr2.IsActive && !hptr2.IsDefeated)
                    {
                        if (hptr2.IsHuman)
                            hptr.Make_Enemy(house2);
                        else
                            hptr.Make_Ally(house2);
                    }
                }
            }
        }
    }


    /// <summary>Find_Cell_In_Zone: the legal spot (adjacent to the base, for buildings) closest to a
    /// random cell in the zone.</summary>
    public int Find_Cell_In_Zone(TechnoClass? techno, ZoneType zone)
    {
        if (techno == null)
            return 0;
        int bestval = -1;
        int bestcell = 0;
        var ttype = techno.Techno_Type_Class();

        int trycell = Random_Cell_In_Zone(zone);
        short[]? list = techno.What_Am_I() == RTTIType.RTTI_BUILDING ? techno.Occupy_List(true) : null;

        for (int cell = 0; cell < TdDefines.MAP_CELL_TOTAL; cell++)
        {
            if (game.Map.In_Radar(cell) && Which_Zone_Cell(cell) != ZoneType.ZONE_NONE)
            {
                bool ok = ttype.Legal_Placement(game, cell) != 0;
                if (ok && list != null && !game.Map.Passes_Proximity_Check(ttype, techno.House.Class.House, list, cell))
                    ok = false;
                if (ok)
                {
                    int dist = TdMath.Distance(TdMath.CellCoord(cell), TdMath.CellCoord(trycell));
                    if (bestval == -1 || dist < bestval)
                    {
                        bestval = dist;
                        bestcell = cell;
                    }
                }
            }
        }
        return bestcell;
    }

    /// <summary>Random_Cell_In_Zone: somewhere in the zone (falling back to the core, then the
    /// centre), clipped to the map.</summary>
    public int Random_Cell_In_Zone(ZoneType zone)
    {
        uint coord = 0;
        int maxdist;
        var map = game.Map;
        int cellLeptonW = (int)TdDefines.CELL_LEPTON_W, cellLeptonH = (int)TdDefines.CELL_LEPTON_H;
        // The source passes two Random_Picks to Coord_Move; MSVC evaluates the distance (the right
        // one) first.
        switch (zone)
        {
            case ZoneType.ZONE_CORE:
                coord = game.Coord_Scatter(Center, game.Random_Pick(0, Radius), true);
                break;
            case ZoneType.ZONE_NORTH:
            {
                maxdist = Math.Min(Radius * 3, (TdMath.CoordY(Center) - TdMath.CellToLepton(map.MapCellY)) - cellLeptonH);
                if (maxdist < 0) break;
                int dist = game.Random_Pick(Math.Min(Radius * 2, maxdist), Math.Min(Radius * 3, maxdist));
                int dir = game.Random_Pick((int)DirType.DIR_N, (int)DirType.DIR_E) - 32;
                coord = TdMath.CoordMove(Center, (byte)dir, dist);
                break;
            }
            case ZoneType.ZONE_EAST:
            {
                maxdist = Math.Min(Radius * 3, (TdMath.CellToLepton(map.MapCellX + map.MapCellWidth) - TdMath.CoordX(Center)) - cellLeptonW);
                if (maxdist < 0) break;
                int dist = game.Random_Pick(Math.Min(Radius * 2, maxdist), Math.Min(Radius * 3, maxdist));
                int dir = game.Random_Pick((int)DirType.DIR_NE, (int)DirType.DIR_SE);
                coord = TdMath.CoordMove(Center, (byte)dir, dist);
                break;
            }
            case ZoneType.ZONE_SOUTH:
            {
                maxdist = Math.Min(Radius * 3, (TdMath.CellToLepton(map.MapCellY + map.MapCellHeight) - TdMath.CoordY(Center)) - cellLeptonH);
                if (maxdist < 0) break;
                int dist = game.Random_Pick(Math.Min(Radius * 2, maxdist), Math.Min(Radius * 3, maxdist));
                int dir = game.Random_Pick((int)DirType.DIR_SE, (int)DirType.DIR_SW);
                coord = TdMath.CoordMove(Center, (byte)dir, dist);
                break;
            }
            case ZoneType.ZONE_WEST:
            {
                maxdist = Math.Min(Radius * 3, (TdMath.CoordX(Center) - TdMath.CellToLepton(map.MapCellX)) - cellLeptonW);
                if (maxdist < 0) break;
                int dist = game.Random_Pick(Math.Min(Radius * 2, maxdist), Math.Min(Radius * 3, maxdist));
                int dir = game.Random_Pick((int)DirType.DIR_SW, (int)DirType.DIR_NW);
                coord = TdMath.CoordMove(Center, (byte)dir, dist);
                break;
            }
        }

        int cell;
        if (coord == 0 || !map.In_Radar(TdMath.CoordCell(coord)))
            cell = zone == ZoneType.ZONE_CORE ? TdMath.CoordCell(Center) : Random_Cell_In_Zone(ZoneType.ZONE_CORE);
        else
            cell = TdMath.CoordCell(coord);

        if (!map.In_Radar(cell))
        {
            int x = TdMath.CellX(cell), y = TdMath.CellY(cell);
            if (x < map.MapCellX) x = map.MapCellX;
            if (y < map.MapCellY) y = map.MapCellY;
            if (x >= map.MapCellX + map.MapCellWidth) x = map.MapCellX + map.MapCellWidth - 1;
            if (y >= map.MapCellY + map.MapCellHeight) y = map.MapCellY + map.MapCellHeight - 1;
            cell = TdMath.XyCell(x, y);
        }
        return cell;
    }

    public uint Get_Ally_Flags() => allies;

    /// <summary>Clobber_All: the house and everything it owns are removed (unused skirmish slots).</summary>
    public void Clobber_All()
    {
        int i;
        for (i = 0; i < game.Aircraft.Count; i++)
            if (game.Aircraft.Ptr(i).House == this) { game.Aircraft.Ptr(i).Delete(); i--; }
        for (i = 0; i < game.Units.Count; i++)
            if (game.Units.Ptr(i).House == this) { game.Units.Ptr(i).Delete(); i--; }
        for (i = 0; i < game.Infantry.Count; i++)
            if (game.Infantry.Ptr(i).House == this) { game.Infantry.Ptr(i).Delete(); i--; }
        for (i = 0; i < game.Buildings.Count; i++)
            if (game.Buildings.Ptr(i).House == this) { game.Buildings.Ptr(i).Delete(); i--; }
        for (i = 0; i < game.TeamTypes.Count; i++)
            if (game.TeamTypes.Ptr(i).House == Class.House) { game.TeamTypes.Ptr(i).Delete(); i--; }
        for (i = 0; i < game.Triggers.Count; i++)
            if (game.Triggers.Ptr(i).House == Class.House) { game.Triggers.Ptr(i).Delete(); i--; }
        Delete();
    }
}

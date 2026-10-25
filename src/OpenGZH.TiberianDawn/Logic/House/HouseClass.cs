using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Scenario;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic;

/// <summary>
/// HouseClass (HOUSE.CPP): one side — money and Tiberium, power, what it has built (scans),
/// production, special weapons, alliances, threat regions, triggers on its events, and winning
/// or losing.
/// </summary>
public sealed partial class HouseClass
{
    const int SPEAK_DELAY = (int)TdDefines.TICKS_PER_MINUTE * 2;
    const int TEAM_DELAY = (int)TdDefines.TICKS_PER_MINUTE / 10;
    const int DAMAGE_DELAY = (int)TdDefines.TICKS_PER_MINUTE;

    readonly TdGame game;
    public readonly HouseTypeClass Class;
    public float FirepowerBias = 1, GroundspeedBias = 1, AirspeedBias = 1, ArmorBias = 1, ROFBias = 1, CostBias = 1, BuildSpeedBias = 1;
    public float RepairDelay, BuildDelay;
    public HousesType ActLike;
    public bool IsActive, IsHuman, WasHuman, IsStarted, IsAlerted, IsDiscovered, IsMaxedOut, IsDefeated, IsToDie, IsToWin, IsToLose;
    public bool IsCivEvacuated, IsRecalcNeeded, IsVisionary, IsAirstrikePending, IsFreeHarvester;
    public int NukePieces;
    public readonly CountDownTimer FreeHarvester;
    public readonly SuperClass IonCannon, AirStrike, NukeStrike;
    public StructType JustBuilt = StructType.STRUCT_NONE;
    public int Blockage;
    public readonly CountDownTimer AlertTime, BorrowedTime;
    public long BScan, ActiveBScan, NewBScan, NewActiveBScan;
    public long UScan, ActiveUScan, NewUScan, NewActiveUScan;
    public long IScan, ActiveIScan, NewIScan, NewActiveIScan;
    public long AScan, ActiveAScan, NewAScan, NewActiveAScan;
    public uint CreditsSpent, HarvestedCredits;
    public int CurUnits, CurBuildings, MaxUnit, MaxBuilding;
    public long Tiberium, Credits, InitialCredits, Capacity;
    public bool Resigned, IGaveUp;
    public readonly UnitTrackerClass AircraftTotals, InfantryTotals, UnitTotals, BuildingTotals;
    public readonly UnitTrackerClass DestroyedAircraft, DestroyedInfantry, DestroyedUnits, DestroyedBuildings, CapturedBuildings, TotalCrates;
    public int AircraftFactories, InfantryFactories, UnitFactories, BuildingFactories, SpecialFactories;
    public int Power, Drain;
    public SourceType Edge = SourceType.SOURCE_NORTH;
    public int AircraftFactory = -1, InfantryFactory = -1, UnitFactory = -1, BuildingFactory = -1, SpecialFactory = -1;
    public RadarEnum Radar = RadarEnum.RADAR_NONE;
    public uint FlagLocation = Target.TARGET_NONE;
    public int FlagHome;
    public PlayerColorType RemapColor;
    public byte[] RemapTable;
    public string Name = "Computer";
    public readonly int[] UnitsKilled = new int[(int)HousesType.HOUSE_COUNT];
    public int UnitsLost;
    public readonly int[] BuildingsKilled = new int[(int)HousesType.HOUSE_COUNT];
    public int BuildingsLost;
    public HousesType WhoLastHurtMe;
    public int StartLocationOverride = -1;
    public readonly RegionClass[] Regions = new RegionClass[TdDefines.MAP_TOTAL_REGIONS];
    public readonly CountDownTimer BlitzTime;
    public int NukeDest;
    public long VisibleCredits;
    public bool DebugUnlockBuildables;
    public readonly CountDownTimer ScreenShakeTime;
    /// <summary>The handicap (difficulty) of this house.</summary>
    public DiffType Difficulty = DiffType.DIFF_NORMAL;
    /// <summary>The house's smartness (Rule.MaxIQ unless changed; 0 is presumed for humans).</summary>
    public int IQ;

    uint allies;
    readonly CountDownTimer damageTime, teamTime, triggerTime, speakAttackDelay, speakPowerDelay, speakMoneyDelay, speakMaxedDelay;

    HouseClass(TdGame game, HousesType house)
    {
        this.game = game;
        Class = game.Types.House(house);
        CountDownTimer T() => new(game);
        FreeHarvester = T(); AlertTime = T(); BorrowedTime = T(); BlitzTime = T(); ScreenShakeTime = T();
        damageTime = T(); teamTime = T(); triggerTime = T(); speakAttackDelay = T(); speakPowerDelay = T(); speakMoneyDelay = T(); speakMaxedDelay = T();

        IonCannon = new SuperClass(game, (int)TdDefines.ION_CANNON_GONE_TIME, VoxType.VOX_ION_READY, VoxType.VOX_ION_CHARGING, VoxType.VOX_ION_CHARGING, VoxType.VOX_NO_POWER);
        AirStrike = new SuperClass(game, (int)TdDefines.AIR_CANNON_GONE_TIME, VoxType.VOX_AIRSTRIKE_READY, VoxType.VOX_NONE, VoxType.VOX_NOT_READY, VoxType.VOX_NOT_READY);
        NukeStrike = new SuperClass(game, (int)TdDefines.NUKE_GONE_TIME, VoxType.VOX_NUKE_AVAILABLE, VoxType.VOX_NONE, VoxType.VOX_NOT_READY, VoxType.VOX_NO_POWER);

        WhoLastHurtMe = house;
        AlertTime.Set(0);
        ActLike = Class.House;
        BlitzTime.Clear();
        ScreenShakeTime.Clear();
        damageTime.Set(DAMAGE_DELAY);
        game.HouseTriggers[(int)house].Clear();
        IsRecalcNeeded = true;
        Make_Ally(house);
        NukePieces = 0x07;
        RemapTable = Class.Remap;
        RemapColor = Class.RemapColor;
        speakAttackDelay.Set(1);
        speakMaxedDelay.Set(1);
        speakMoneyDelay.Set(1);
        speakMoneyDelay.Set(game.Options.Normalize_Delay(SPEAK_DELAY));
        speakPowerDelay.Set(1);
        teamTime.Set(TEAM_DELAY);
        triggerTime.Set(0);

        AircraftTotals = new((int)AircraftType.AIRCRAFT_COUNT);
        InfantryTotals = new((int)InfantryType.INFANTRY_COUNT);
        UnitTotals = new((int)UnitType.UNIT_COUNT);
        BuildingTotals = new((int)StructType.STRUCT_COUNT);
        DestroyedAircraft = new((int)AircraftType.AIRCRAFT_COUNT);
        DestroyedInfantry = new((int)InfantryType.INFANTRY_COUNT);
        DestroyedUnits = new((int)UnitType.UNIT_COUNT);
        DestroyedBuildings = new((int)StructType.STRUCT_COUNT);
        CapturedBuildings = new((int)StructType.STRUCT_COUNT);
        TotalCrates = new(15);

        Init_RA_AI();
    }

    /// <summary>new HouseClass: a slot in the house heap (null when full).</summary>
    public static HouseClass? Create(TdGame game, HousesType house)
    {
        int slot = game.Houses.Allocate();
        if (slot < 0)
            return null;
        var h = new HouseClass(game, house);
        game.Houses.Place(slot, h);
        h.IsActive = true;
        return h;
    }

    public void Delete()
    {
        IsActive = false;
        game.Houses.Free(this);
    }

    /// <summary>Assign_Handicap: the remaster's difficulty biases (multiplied by the side's own in multiplayer).</summary>
    public DiffType Assign_Handicap(DiffType handicap)
    {
        DiffType old = Difficulty;
        Difficulty = handicap;
        var d = game.Rule.Diff[(int)handicap];
        if (game.GameToPlay != GameType.GAME_NORMAL)
        {
            var hptr = game.Types.House(ActLike);
            FirepowerBias = hptr.FirepowerBias * d.FirepowerBias;
            GroundspeedBias = hptr.GroundspeedBias * d.GroundspeedBias;
            AirspeedBias = hptr.AirspeedBias * d.AirspeedBias;
            ArmorBias = hptr.ArmorBias * d.ArmorBias;
            ROFBias = hptr.ROFBias * d.ROFBias;
            CostBias = hptr.CostBias * d.CostBias;
            RepairDelay = d.RepairDelay;
            BuildDelay = d.BuildDelay;
            BuildSpeedBias = hptr.BuildSpeedBias * d.BuildSpeedBias;
        }
        else
        {
            FirepowerBias = d.FirepowerBias;
            GroundspeedBias = d.GroundspeedBias;
            AirspeedBias = d.AirspeedBias;
            ArmorBias = d.ArmorBias;
            ROFBias = d.ROFBias;
            CostBias = d.CostBias;
            RepairDelay = d.RepairDelay;
            BuildDelay = d.BuildDelay;
            BuildSpeedBias = d.BuildSpeedBias;
        }
        return old;
    }

    /// <summary>Can_Build: buildable, ownable, prerequisites present and the tech level reached (with the
    /// campaign's special cases per side and mission).</summary>
    public bool Can_Build(TechnoTypeClass? type, HousesType house)
    {
        if (type == null || !type.IsBuildable || ((1L << (int)house) & type.Ownable) == 0)
            return false;
        // The computer can always build everything in the campaign.
        if (!IsHuman && game.GameToPlay == GameType.GAME_NORMAL)
            return true;

        long flags = ActiveBScan;

        // Computer players go by their building counts, so their choices stay consistent.
        if (!IsHuman)
        {
            flags = 0;
            for (int i = 0; i < 32; i++)
                if (BQuantity[i] > 0)
                    flags |= (long)(1 << i);
        }

        long pre = type.Pre;

        // Advanced buildings count as their basic versions.
        if ((flags & (long)TdDefines.STRUCTF_ADVANCED_POWER) != 0) flags |= (long)TdDefines.STRUCTF_POWER;
        if ((flags & (long)TdDefines.STRUCTF_HAND) != 0) flags |= (long)TdDefines.STRUCTF_BARRACKS;
        if ((flags & (long)TdDefines.STRUCTF_OBELISK) != 0) flags |= (long)TdDefines.STRUCTF_ATOWER;
        if ((flags & (long)TdDefines.STRUCTF_TEMPLE) != 0) flags |= (long)TdDefines.STRUCTF_EYE;
        if ((flags & (long)TdDefines.STRUCTF_AIRSTRIP) != 0) flags |= (long)TdDefines.STRUCTF_WEAP;
        if ((flags & (long)TdDefines.STRUCTF_SAM) != 0) flags |= (long)TdDefines.STRUCTF_HELIPAD;

        if (game.GameToPlay != GameType.GAME_NORMAL || (game.Special.IsJurassic && game.AreThingiesEnabled))
        {
            if (DebugUnlockBuildables)
                return true;
            return (pre & flags) == pre && type.Level <= game.BuildLevel;
        }

        int level = game.BuildLevel;
        var unitType = (type as UnitTypeClass)?.Type;
        var infType = (type as InfantryTypeClass)?.Type;
        var structType = (type as BuildingTypeClass)?.Type;

        // Nod's stealth tank in mission 11 needs the mission building.
        if (house == HousesType.HOUSE_BAD && unitType == UnitType.UNIT_STANK && level == 11)
        {
            pre = (long)TdDefines.STRUCTF_MISSION;
            level = type.Scenario;
        }
        if (house == HousesType.HOUSE_GOOD && infType == InfantryType.INFANTRY_E3 && level < 7)
            return false;
        if (house == HousesType.HOUSE_GOOD && unitType == UnitType.UNIT_MLRS && level < 9)
            return false;
        if (house == HousesType.HOUSE_BAD && unitType == UnitType.UNIT_APC)
            return false;
        if ((structType == StructType.STRUCT_TEMPLE || structType == StructType.STRUCT_OBELISK) && Class.House == HousesType.HOUSE_GOOD)
            return false;
        if (unitType == UnitType.UNIT_MLRS && Class.House == HousesType.HOUSE_BAD)
            return false;
        if (structType == StructType.STRUCT_EYE && Class.House == HousesType.HOUSE_BAD)
            return false;
        if (house == HousesType.HOUSE_BAD && level >= 12 && structType == StructType.STRUCT_ADVANCED_POWER)
            level = type.Scenario;
        if (house == HousesType.HOUSE_BAD && structType == StructType.STRUCT_HELIPAD)
            return false;
        if (house == HousesType.HOUSE_GOOD && level < 8 && structType == StructType.STRUCT_SANDBAG_WALL)
            return false;
        if (house == HousesType.HOUSE_GOOD && level == 2)
            level = 1;
        if (DebugUnlockBuildables)
        {
            level = 98;
            pre = 0;
        }
        if (game.DebugCheat)
            level = 98;

        return (pre & flags) == pre && type.Scenario <= level;
    }

    /// <summary>AI: once a frame — scans swap in, win/lose, alerts and teams, power damage, EVA, special
    /// weapons, defeat, the house's triggers, radar, and the sidebar.</summary>
    public void AI()
    {
        game.Logic_Switch_Player_Context(this);

        IScan = NewIScan; BScan = NewBScan; UScan = NewUScan; AScan = NewAScan;
        ActiveIScan = NewActiveIScan; ActiveBScan = NewActiveBScan; ActiveUScan = NewActiveUScan; ActiveAScan = NewActiveAScan;
        NewIScan = NewBScan = NewUScan = NewAScan = 0;
        NewActiveIScan = NewActiveBScan = NewActiveUScan = NewActiveAScan = 0;

        // A skirmish computer house builds a base and starts producing at once.
        if (!IsHuman && game.GameToPlay != GameType.GAME_NORMAL && (IsBaseBuilding || IQ >= game.Rule.IQProduction))
        {
            IsBaseBuilding = true;
            IsStarted = true;
            IsAlerted = true;
        }

        bool normal = game.GameToPlay == GameType.GAME_NORMAL;
        bool isPlayer = this == game.PlayerPtr;

        if (normal && IsToWin && BorrowedTime.Expired && Blockage <= 0)
        {
            IsToWin = false;
            if (isPlayer)
                game.PlayerWins = true;
            else
                game.PlayerLoses = true;
        }

        if (normal && IsToLose && BorrowedTime.Expired)
        {
            IsToLose = false;
            if (isPlayer)
                game.PlayerLoses = true;
            else
                game.PlayerWins = true;
        }

        if (IsToDie && BorrowedTime.Expired)
        {
            IsToDie = false;
            Blowup_All();
            if (game.GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER)
                MPlayer_Defeated();
        }

        if (normal)
        {
            Power = Math.Max(Power, 0);
            Drain = Math.Max(Drain, 0);
        }

        // An alerted computer sends out extra teams now and then.
        if (IsAlerted && AlertTime.Expired)
        {
            int maxteams = game.Random_Pick(2, ((game.BuildLevel - 1) / 3) + 1);
            for (int index = 0; index < maxteams; index++)
            {
                var ttype = Suggested_New_Team(true);
                if (ttype != null)
                {
                    game.ScenarioInit++;
                    ttype.Create_One_Of();
                    game.ScenarioInit--;
                }
            }

            if (normal && game.PlayerPtr.Difficulty == DiffType.DIFF_HARD)
                AlertTime.Set(TdDefines.TICKS_PER_MINUTE * game.Random_Pick(4, 10));
            else if (normal && game.PlayerPtr.Difficulty == DiffType.DIFF_EASY)
                AlertTime.Set(TdDefines.TICKS_PER_MINUTE * game.Random_Pick(16, 40));
            else
                AlertTime.Set(TdDefines.TICKS_PER_MINUTE * game.Random_Pick(5, 20));
        }

        if (teamTime.Expired)
        {
            Suggested_New_Team(false)?.Create_One_Of();

            // Capture the flag: nothing parks on the flag's home cell.
            if (game.Special.IsCaptureTheFlag && !normal && FlagHome != 0)
            {
                var techno = game.Map[FlagHome].Cell_Techno();
                if (techno != null)
                {
                    bool moving = techno is FootClass foot && techno.What_Am_I() is RTTIType.RTTI_INFANTRY or RTTIType.RTTI_UNIT && game.Target_Legal(foot.NavCom);
                    if (!moving)
                        techno.Scatter(0, true, true);
                }
            }

            // Multiplayer: dinosaurs (or a Visceroid) appear now and then.
            if (!normal && Class.House == HousesType.HOUSE_JP)
            {
                bool jurassic = game.Special.IsJurassic && game.AreThingiesEnabled;
                int rlimit = jurassic ? 450 : 1000;
                if (game.IRandom(0, rlimit) <= 5)
                {
                    UnitClass? obj = null;
                    if (jurassic)
                        obj = UnitClass.Create(game, (UnitType)game.Random_Pick((int)UnitType.UNIT_TRIC, (int)UnitType.UNIT_STEG), HousesType.HOUSE_JP);
                    else if (game.Special.IsVisceroids && game.BuildLevel >= 7 && (UScan & (long)TdDefines.UNITF_VICE) == 0)
                        obj = UnitClass.Create(game, UnitType.UNIT_VICE, HousesType.HOUSE_JP);

                    if (obj != null)
                    {
                        // XY_Cell(x + Random, y + Random): the arguments right to left.
                        int ry = game.Random_Pick(0, game.Map.MapCellHeight - 1);
                        int rx = game.Random_Pick(0, game.Map.MapCellWidth - 1);
                        int cell = TdMath.XyCell(game.Map.MapCellX + rx, game.Map.MapCellY + ry);
                        if (!game.Scan_Place_Object(obj, cell))
                            obj.Delete();
                    }
                }
            }
            teamTime.Set(TEAM_DELAY);
        }

        // Low power slowly damages the buildings that use it (down to half).
        if (damageTime.Expired)
        {
            if (IsFreeHarvester && FreeHarvester.Expired)
            {
                IsFreeHarvester = false;
                game.Create_Special_Reinforcement(this, game.Types.Unit(UnitType.UNIT_HARVESTER), null);
            }

            if (Power_Fraction() < 0x100)
            {
                for (int index = 0; index < game.Buildings.Count; index++)
                {
                    var b = game.Buildings.Ptr(index);
                    if (b.House == this && b.Health_Ratio() > 0x080 && b.Class.Drain != 0)
                    {
                        int damage = 1;
                        b.Take_Damage(ref damage, 0, WarheadType.WARHEAD_AP, null);
                    }
                }
            }
            damageTime.Set(DAMAGE_DELAY);
        }

        if (isPlayer && BScan == 0)
            game.Sidebar_Sell_Mode_Off();

        // EVA: no money, silos needed, low power.
        if (isPlayer)
        {
            if (speakMoneyDelay.Expired && Available_Money() < 100 && UnitFactories + BuildingFactories + InfantryFactories > 0)
            {
                game.Speak(VoxType.VOX_NO_CASH);
                speakMoneyDelay.Set(game.Options.Normalize_Delay(SPEAK_DELAY));
                game.On_Message((int)TdDefines.TXT_INSUFFICIENT_FUNDS, 35.0f);
            }

            if (speakMaxedDelay.Expired && IsMaxedOut)
            {
                IsMaxedOut = false;
                if ((Capacity - Tiberium) < 300 && Capacity > 500 && (BScan & (long)(TdDefines.STRUCTF_REFINERY | TdDefines.STRUCTF_CONST)) != 0)
                {
                    game.Speak(VoxType.VOX_NEED_MO_CAPACITY);
                    speakMaxedDelay.Set(game.Options.Normalize_Delay(SPEAK_DELAY));
                }
            }

            if (speakPowerDelay.Expired && Power_Fraction() < 0x0100 && (BScan & (long)TdDefines.STRUCTF_CONST) != 0)
            {
                game.Speak(VoxType.VOX_LOW_POWER);
                speakPowerDelay.Set(game.Options.Normalize_Delay(SPEAK_DELAY));
                game.On_Message((int)TdDefines.TXT_LOW_POWER, 35.0f);
            }
        }

        if (game.Target_Legal(FlagLocation))
        {
            var unit = game.As_Unit(FlagLocation);
            if (unit != null)
            {
                unit.Mark(MarkType.MARK_CHANGE);
            }
            else
            {
                int cell = game.As_Cell(FlagLocation);
                game.Map[cell].Flag_Update();
                game.Map[cell].Redraw_Objects();
            }
        }

        bool isTime = false;
        if (triggerTime.Expired)
        {
            isTime = true;
            triggerTime.Set(TdDefines.TICKS_PER_MINUTE / 10);
        }

        // The ion cannon (with an Advanced Comm. Center).
        if (IonCannon.Is_Present())
        {
            if ((ActiveBScan & (long)TdDefines.STRUCTF_EYE) == 0 && !IonCannon.Is_One_Time())
            {
                if (IonCannon.Remove())
                    IsRecalcNeeded = true;
            }
            else
            {
                IonCannon.Suspend(Power_Fraction() < 0x0100);
                IonCannon.AI(isPlayer);
            }
            if (IonCannon.Is_Ready() && !IsHuman)
                Special_Weapon_AI(SpecialWeaponType.SPC_ION_CANNON);
        }
        else if ((normal || game.Rule.AllowSuperWeapons) && (ActiveBScan & (long)TdDefines.STRUCTF_EYE) != 0 &&
                 (ActLike == HousesType.HOUSE_GOOD || !normal) && (IsHuman || !normal))
        {
            IonCannon.Enable(false, isPlayer, Power_Fraction() < 0x0100);
            Special_To_Sidebar(SpecialWeaponType.SPC_ION_CANNON);
        }

        // The nuke (with a Temple of Nod and, in campaigns, all three pieces).
        if (NukeStrike.Is_Present())
        {
            if ((ActiveBScan & (long)TdDefines.STRUCTF_TEMPLE) == 0 && (!NukeStrike.Is_One_Time() || normal))
            {
                if (NukeStrike.Remove(true))
                    IsRecalcNeeded = true;
            }
            else
            {
                NukeStrike.Suspend(Power_Fraction() < 0x0100);
                NukeStrike.AI(isPlayer);
            }
            if (NukeStrike.Is_Ready() && !IsHuman)
                Special_Weapon_AI(SpecialWeaponType.SPC_NUCLEAR_BOMB);
        }
        else if ((normal || game.Rule.AllowSuperWeapons) && (ActiveBScan & (long)TdDefines.STRUCTF_TEMPLE) != 0 && Has_Nuke_Device() && IsHuman)
        {
            NukeStrike.Enable(normal, isPlayer);
            Special_To_Sidebar(SpecialWeaponType.SPC_NUCLEAR_BOMB);
        }

        if (AirStrike.Is_Present())
        {
            AirStrike.AI(isPlayer);
            if (AirStrike.Is_Ready() && !IsHuman)
                Special_Weapon_AI(SpecialWeaponType.SPC_AIR_STRIKE);
        }

        if (IsAirstrikePending)
        {
            IsAirstrikePending = false;
            if (AirStrike.Enable(false, isPlayer))
            {
                AirStrike.Forced_Charge(isPlayer);
                Special_To_Sidebar(SpecialWeaponType.SPC_AIR_STRIKE);
            }
        }

        if (!normal && Class.House != HousesType.HOUSE_JP)
            Check_Pertinent_Structures();

        if (!normal && !IsDefeated && ActiveBScan == 0 && ActiveAScan == 0 && UScan == 0 && ActiveIScan == 0 && game.Frame > 0)
            MPlayer_Defeated();

        // The house's triggers.
        var triggers = game.HouseTriggers[(int)Class.House];
        for (int index = 0; index < triggers.Count; index++)
        {
            var t = triggers[index];
            HousesType me = Class.House;
            if (JustBuilt != StructType.STRUCT_NONE && t.Spring(EventType.EVENT_BUILD, me, (int)JustBuilt))
            {
                JustBuilt = StructType.STRUCT_NONE;
                continue;
            }
            if (IsCivEvacuated && t.Spring(EventType.EVENT_EVAC_CIVILIAN, me))
                continue;
            if (t.Spring(EventType.EVENT_NBUILDINGS_DESTROYED, me, BuildingsLost))
                continue;
            if (t.Spring(EventType.EVENT_NUNITS_DESTROYED, me, UnitsLost))
                continue;
            if (IsDiscovered && t.Spring(EventType.EVENT_HOUSE_DISCOVERED, me))
            {
                IsDiscovered = false;
                continue;
            }
            if (game.EndCountDown == 0)
            {
                long units = (ActiveUScan & ~(long)TdDefines.UNITF_GUNBOAT) | IScan |
                             (ActiveAScan & ~(long)(TdDefines.AIRCRAFTF_TRANSPORT | TdDefines.AIRCRAFTF_CARGO | TdDefines.AIRCRAFTF_A10));
                if (ActiveBScan == 0 && t.Spring(EventType.EVENT_BUILDINGS_DESTROYED, me))
                    continue;
                if (units == 0 && t.Spring(EventType.EVENT_UNITS_DESTROYED, me))
                    continue;
                if ((ActiveBScan | units) == 0 && t.Spring(EventType.EVENT_ALL_DESTROYED, me))
                    continue;
            }
            if (t.Spring(EventType.EVENT_CREDITS, me, (int)Credits))
                continue;
            if (isTime && t.Spring(EventType.EVENT_TIME, me))
                continue;
            if ((BScan & (long)(TdDefines.STRUCTF_AIRSTRIP | TdDefines.STRUCTF_HAND | TdDefines.STRUCTF_WEAP | TdDefines.STRUCTF_BARRACKS)) == 0 &&
                t.Spring(EventType.EVENT_NOFACTORIES, me))
                continue;
        }

        // The radar (with a Comm. Center and enough power).
        if (isPlayer)
        {
            bool hasRadar = (BScan & (long)(TdDefines.STRUCTF_RADAR | TdDefines.STRUCTF_EYE)) != 0;
            var radar = game.RadarState;
            if (radar.Is_Radar_Active())
            {
                if (!hasRadar || Power_Fraction() < 0x0100)
                    radar.Radar_Activate(0);
            }
            else if (hasRadar)
            {
                if (Power_Fraction() >= 0x0100)
                    radar.Radar_Activate(1);
            }
            else if (radar.Is_Radar_Existing())
            {
                radar.Radar_Activate(4);
            }

            Radar = !hasRadar ? RadarEnum.RADAR_NONE : (radar.Is_Radar_Active() || radar.Is_Radar_Activating()) ? RadarEnum.RADAR_ON : RadarEnum.RADAR_OFF;
        }

        VisibleCredits = Credits + Tiberium;

        // The skirmish computer's expert system and production choices.
        if (game.GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER && !IsHuman)
        {
            if (IsBaseBuilding && AITimer.Time == 0)
                AITimer.Set(Expert_AI());
            if (!IsBaseBuilding && State == StateType.STATE_ENDGAME)
            {
                Fire_Sale();
                Do_All_To_Hunt();
            }
            AI_Building();
            AI_Unit();
            AI_Vessel();
            AI_Infantry();
            AI_Aircraft();
        }

        if (isPlayer && IsRecalcNeeded)
        {
            IsRecalcNeeded = false;
            game.Sidebar_Recalc();
            for (int index = 0; index < game.Buildings.Count; index++)
            {
                var building = game.Buildings.Ptr(index);
                if (building.Owner() == Class.House)
                {
                    building.Update_Specials();
                    if (game.PlayerPtr == building.House)
                        building.Update_Buildables();
                }
            }
            game.Recalculate_Placement_Distances();
        }
    }

    void Special_To_Sidebar(SpecialWeaponType special)
    {
        if (game.GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER)
        {
            if (IsHuman)
                game.Sidebar_Add(RTTIType.RTTI_SPECIAL, (int)special, this, false);
        }
        else if (this == game.PlayerPtr)
        {
            game.Sidebar_Add(RTTIType.RTTI_SPECIAL, (int)special, this, false);
        }
    }

    /// <summary>Attacked: "base under attack" (every half minute or so), and the house's attacked triggers.</summary>
    public void Attacked(BuildingClass? source)
    {
        if (speakAttackDelay.Expired && game.PlayerPtr.Class.House == Class.House)
        {
            if (game.GameToPlay == GameType.GAME_NORMAL)
                game.Speak(VoxType.VOX_BASE_UNDER_ATTACK, null, source?.Center_Coord() ?? 0);
            else
                game.Speak(VoxType.VOX_BASE_UNDER_ATTACK, this);
            speakAttackDelay.Set(game.Options.Normalize_Delay((TdDefines.TICKS_PER_MINUTE / 2) + (TdDefines.TICKS_PER_SECOND * 5)));

            var triggers = game.HouseTriggers[(int)Class.House];
            for (int index = 0; index < triggers.Count; index++)
                triggers[index].Spring(EventType.EVENT_ATTACKED, Class.House);
        }
    }

    public void Harvested(int tiberium)
    {
        long oldtib = Tiberium;
        Tiberium += tiberium;
        if (Tiberium > Capacity)
        {
            Tiberium = Capacity;
            IsMaxedOut = true;
        }
        HarvestedCredits += (uint)tiberium;
        Silo_Redraw_Check(oldtib, Capacity);
    }

    public long Available_Money() => Tiberium + Credits;

    /// <summary>Spend_Money: from the stored Tiberium first, then credits.</summary>
    public void Spend_Money(int money)
    {
        long oldtib = Tiberium;
        if (money > Tiberium)
        {
            money -= (int)Tiberium;
            Tiberium = 0;
            Credits -= money;
        }
        else
        {
            Tiberium -= money;
        }
        Silo_Redraw_Check(oldtib, Capacity);
        // As the original: only the part paid in credits is counted when Tiberium ran out.
        CreditsSpent += (uint)money;
    }

    public void Refund_Money(int money) => Credits += money;

    public void Adjust_Power(int adjust) => Power += adjust;
    public void Adjust_Drain(int adjust) => Drain += adjust;

    /// <summary>Adjust_Capacity: storage changes; Tiberium that no longer fits is refunded (or lost, in anger).</summary>
    public int Adjust_Capacity(int adjust, bool inanger = false)
    {
        long oldcap = Capacity;
        int retval = 0;
        Capacity += adjust;
        Capacity = Math.Max(Capacity, 0L);
        if (Tiberium > Capacity)
        {
            retval = (int)(Tiberium - Capacity);
            Tiberium = Capacity;
            if (!inanger)
            {
                Refund_Money(retval);
                retval = 0;
            }
            else
            {
                IsMaxedOut = true;
            }
        }
        Silo_Redraw_Check(Tiberium, oldcap);
        return retval;
    }

    void Silo_Redraw_Check(long oldtib, long oldcap)
    {
        long oldratio = oldcap != 0 ? (oldtib * 5) / oldcap : 0;
        long newratio = Capacity != 0 ? (Tiberium * 5) / Capacity : 0;
        if (oldratio != newratio)
        {
            for (int index = 0; index < game.Buildings.Count; index++)
            {
                var b = game.Buildings.Ptr(index);
                if (!b.IsInLimbo && b.House == this && b.Class.Type == StructType.STRUCT_STORAGE)
                    b.Mark(MarkType.MARK_CHANGE);
            }
        }
    }

    /// <summary>Read_INI: every house's section — MaxUnit, MaxBuilding, Credits (hundreds), Edge, Allies.</summary>
    public static void Read_INI(TdGame game, Data.WwProfile ini)
    {
        for (var index = HousesType.HOUSE_FIRST; index < HousesType.HOUSE_COUNT; index++)
        {
            string hname = game.Types.House(index).IniName;
            int maxunit = Math.Max(ini.Get_Int(hname, "MaxUnit", (int)TdDefines.EACH_UNIT_MAX), 150);
            int maxbuilding = Math.Max(ini.Get_Int(hname, "MaxBuilding", (int)TdDefines.EACH_BUILDING_MAX), 150);
            int credits = ini.Get_Int(hname, "Credits", 0);

            var p = Create(game, index);
            if (p == null)
                continue;
            p.MaxBuilding = maxbuilding;
            p.MaxUnit = maxunit;
            p.Credits = (long)credits * 100;
            p.InitialCredits = p.Credits;

            p.Edge = game.Source_From_Name(ini.Get_String(hname, "Edge", "", 127));
            if (p.Edge == SourceType.SOURCE_NONE)
                p.Edge = SourceType.SOURCE_NORTH;

            if (game.GameToPlay == GameType.GAME_NORMAL)
            {
                string buf = ini.Get_String(hname, "Allies", "", 127);
                if (buf.Length != 0)
                {
                    var tok = new Data.Strtok(buf);
                    for (string? t = tok.Next(", \t"); t != null; t = tok.Next(", \t"))
                        p.Make_Ally(game.Types.House_From_Name(t));
                }
                else if (p.Class.House == HousesType.HOUSE_GOOD)
                {
                    p.Make_Ally(HousesType.HOUSE_NEUTRAL);
                }
            }
        }
    }

    public bool Is_Ally(HousesType house) => house != HousesType.HOUSE_NONE && ((1u << (int)house) & allies) != 0;
    public bool Is_Ally(HouseClass? house) => house != null && Is_Ally(house.Class.House);
    public bool Is_Ally(ObjectClass? obj) => obj != null && Is_Ally(obj.Owner());
    public uint Get_Allies() => allies;

    /// <summary>Make_Ally: (in multiplayer, units stop targeting the new ally, and everyone is told).</summary>
    public void Make_Ally(HousesType house)
    {
        if (house != HousesType.HOUSE_NONE && !Is_Ally(house))
        {
            if (game.ScenarioInit == 0 && (IsDefeated || house == HousesType.HOUSE_JP))
                return;
            allies |= 1u << (int)house;

            if ((game.DebugFlag || game.GameToPlay != GameType.GAME_NORMAL) && game.ScenarioInit == 0)
            {
                for (int index = 0; index < game.Logic.Count; index++)
                {
                    var obj = game.Logic[index];
                    if (obj != null && !obj.IsInLimbo && obj.Owner() == Class.House && obj is TechnoClass techno)
                    {
                        uint target = techno.TarCom;
                        if (game.Target_Legal(target) && game.As_Techno(target) is { } t && Is_Ally(t))
                            techno.TarCom = Target.TARGET_NONE;
                    }
                }
                game.Add_Message($"{Name} has allied with {game.HouseOrNull(house)?.Name}", RemapColor, 1200);
            }
        }
    }

    public void Make_Enemy(HousesType house)
    {
        if (house != HousesType.HOUSE_NONE && Is_Ally(house))
        {
            var enemy = game.HouseOrNull(house);
            allies &= ~(1u << (int)house);
            if (enemy != null && enemy.Is_Ally(this))
                enemy.allies &= ~(1u << (int)Class.House);

            if ((game.DebugFlag || game.GameToPlay != GameType.GAME_NORMAL) && game.ScenarioInit == 0)
                game.Add_Message($"{Name} is at war with {enemy?.Name}", RemapColor, 600);
        }
    }

    /// <summary>Remap_Table: the colour remap (campaign Nod buildings use the red table).</summary>
    public byte[]? Remap_Table(bool blushing = false, bool unit = false)
    {
        if (blushing)
            return game.FadingLight;
        if (game.GameToPlay == GameType.GAME_NORMAL)
        {
            if (!unit && Class.House == HousesType.HOUSE_BAD)
                return game.Types.Remap_Table("RemapRed");
            if (game.Special.IsJurassic && Class.House == HousesType.HOUSE_MULTI4)
                return game.Types.Remap_Table("RemapLtBlue");
            return Class.Remap;
        }
        return RemapTable;
    }

    public TeamTypeClass? Suggested_New_Team(bool alertcheck) =>
        TeamTypeClass.Suggested_New_Team(game, this, UScan, IScan, IsAlerted && alertcheck);

    static readonly int[] ThreatOffsets =
    [
        -(int)TdDefines.MAP_REGION_WIDTH - 1, -(int)TdDefines.MAP_REGION_WIDTH, -(int)TdDefines.MAP_REGION_WIDTH + 1,
        -1, 0, 1,
        (int)TdDefines.MAP_REGION_WIDTH - 1, (int)TdDefines.MAP_REGION_WIDTH, (int)TdDefines.MAP_REGION_WIDTH + 1,
    ];
    static readonly int[] ThreatShift = [2, 1, 2, 1, 0, 1, 2, 1, 2];

    /// <summary>Adjust_Threat: the region gets the full threat, its neighbours a half or quarter.</summary>
    public void Adjust_Threat(int region, int threat)
    {
        bool neg = threat < 0;
        if (neg)
            threat = -threat;
        for (int lp = 0; lp < 9; lp++)
        {
            int r = region + ThreatOffsets[lp];
            if ((uint)r < (uint)Regions.Length)
                Regions[r].Adjust_Threat(threat >> ThreatShift[lp], neg);
        }
    }

    ref int Factory_Slot(RTTIType type, out bool ok)
    {
        ok = true;
        switch (type)
        {
            case RTTIType.RTTI_AIRCRAFT:
            case RTTIType.RTTI_AIRCRAFTTYPE:
                return ref AircraftFactory;
            case RTTIType.RTTI_UNIT:
            case RTTIType.RTTI_UNITTYPE:
                return ref UnitFactory;
            case RTTIType.RTTI_BUILDING:
            case RTTIType.RTTI_BUILDINGTYPE:
                return ref BuildingFactory;
            case RTTIType.RTTI_INFANTRY:
            case RTTIType.RTTI_INFANTRYTYPE:
                return ref InfantryFactory;
            case RTTIType.RTTI_SPECIAL:
                return ref SpecialFactory;
        }
        ok = false;
        return ref SpecialFactory;
    }

    /// <summary>Begin_Production: start (or resume) building something of a kind.</summary>
    /// <summary>Begin_Production(): the computer house may start building (the trigger action).</summary>
    public void Begin_Production() => IsStarted = true;

    public ProdFailType Begin_Production(RTTIType type, int id)
    {
        ref int factory = ref Factory_Slot(type, out bool ok);
        if (!ok)
            return ProdFailType.PROD_ILLEGAL;

        var tech = game.Fetch_Techno_Type(type, id);
        bool result = true;
        FactoryClass? fptr;

        if (factory != -1)
        {
            fptr = game.Factories.RawPtr(factory);
            if (fptr != null && fptr.Is_Building())
                return ProdFailType.PROD_CANT;
        }
        else
        {
            fptr = FactoryClass.Create(game);
            if (fptr == null)
                return ProdFailType.PROD_CANT;
            factory = game.Factories.ID(fptr);
            result = tech != null ? fptr.Set(tech, this) : fptr.Set(id, this);
            if (!result)
            {
                fptr.Delete();
                fptr = null;
                factory = -1;
            }
        }

        if (result && fptr != null)
        {
            fptr.Start();
            if (game.GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER ? IsHuman : game.PlayerPtr == this)
                game.Sidebar_Factory_Link(factory, type, id, this);
            return ProdFailType.PROD_OK;
        }
        return ProdFailType.PROD_CANT;
    }

    public ProdFailType Suspend_Production(RTTIType type)
    {
        ref int factory = ref Factory_Slot(type, out bool ok);
        if (!ok)
            return ProdFailType.PROD_ILLEGAL;
        if (factory == -1)
            return ProdFailType.PROD_CANT;
        var fptr = game.Factories.RawPtr(factory);
        if (fptr == null)
            return ProdFailType.PROD_CANT;
        fptr.Suspend();
        return ProdFailType.PROD_OK;
    }

    public ProdFailType Abandon_Production(RTTIType type)
    {
        ref int factory = ref Factory_Slot(type, out bool ok);
        if (!ok)
            return ProdFailType.PROD_ILLEGAL;
        if (factory == -1)
            return ProdFailType.PROD_CANT;
        var fptr = game.Factories.RawPtr(factory);
        if (fptr == null)
            return ProdFailType.PROD_CANT;

        // The sidebar drops the item (and a building waiting to be placed).
        if (game.GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER ? IsHuman : game.PlayerPtr == this)
            game.Sidebar_Abandon_Production(type, factory, this);

        fptr.Abandon();
        fptr.Delete();
        factory = -1;
        return ProdFailType.PROD_OK;
    }

    /// <summary>Special_Weapon_AI: the computer fires at the most valuable enemy building.</summary>
    public void Special_Weapon_AI(SpecialWeaponType id)
    {
        BuildingClass? bestptr = null;
        int best = -1;
        for (int index = 0; index < game.Buildings.Count; index++)
        {
            var b = game.Buildings.Ptr(index);
            if (!b.IsInLimbo && b.Strength != 0 && !Is_Ally(b) && (b.Value() > best || best == -1))
            {
                best = b.Value();
                bestptr = b;
            }
        }
        if (bestptr != null)
            Place_Special_Blast(id, TdMath.CoordCell(bestptr.Center_Coord()));
    }

    /// <summary>Place_Special_Blast: fires the ion cannon, nuke or air strike at a cell.</summary>
    public bool Place_Special_Blast(SpecialWeaponType id, int cell)
    {
        bool fired = false;
        string? what = null;
        bool isPlayer = this == game.PlayerPtr;

        switch (id)
        {
            case SpecialWeaponType.SPC_ION_CANNON:
                if (IonCannon.Is_Ready())
                {
                    AnimClass.Create(game, AnimType.ANIM_ION_CANNON, TdMath.CellCoord(cell))?.Set_Owner(Class.House);
                    if (isPlayer)
                        game.IsTargettingMode = false;
                    IonCannon.Discharged(isPlayer);
                    IsRecalcNeeded = true;
                    fired = true;
                    what = "ION";
                }
                break;

            case SpecialWeaponType.SPC_NUCLEAR_BOMB:
                if (NukeStrike.Is_Ready())
                {
                    BuildingClass? launchsite = null;
                    for (int index = 0; index < game.Buildings.Count; index++)
                    {
                        var b = game.Buildings.Ptr(index);
                        if (!b.IsInLimbo && b.House == this && b.Class.Type == StructType.STRUCT_TEMPLE)
                        {
                            launchsite = b;
                            break;
                        }
                    }

                    if (launchsite != null)
                    {
                        launchsite.Assign_Mission(MissionType.MISSION_MISSILE);
                        launchsite.Commence();
                        NukeDest = cell;
                        NukePieces = 0;
                    }
                    else
                    {
                        // No Temple: in multiplayer the nuke just drops from the top of the map.
                        if (game.GameToPlay == GameType.GAME_NORMAL)
                            return false;
                        var bullet = BulletClass.Create(game, BulletType.BULLET_NUKE_DOWN);
                        if (bullet != null)
                        {
                            uint start = TdMath.CellCoord(TdMath.XyCell(TdMath.CellX(cell), 0));
                            bullet.Assign_Target(Target.As_Target(cell));
                            bullet.Payback = null;
                            bullet.Strength = 1;
                            if (!bullet.Unlimbo(start, (byte)DirType.DIR_S))
                                bullet.Delete();
                            else
                                bullet.PrimaryFacing.Set_Current((byte)DirType.DIR_S);
                            game.Speak(VoxType.VOX_INCOMING_NUKE);
                            game.Sound_Effect(VocType.VOC_NUKE_FIRE, start);
                        }
                    }

                    if (isPlayer)
                        game.IsTargettingMode = false;
                    NukeStrike.Discharged(isPlayer);
                    IsRecalcNeeded = true;
                    fired = true;
                    what = "NUKE";
                }
                break;

            case SpecialWeaponType.SPC_AIR_STRIKE:
                if (AirStrike.Is_Ready())
                {
                    int strike = game.GameToPlay == GameType.GAME_NORMAL
                        ? Math.Clamp(game.BuildLevel / 3, 1, 3)
                        : Math.Clamp(game.MPlayerUnitCount / 5, 1, 3);
                    game.Create_Air_Reinforcement(this, AircraftType.AIRCRAFT_A10, strike, MissionType.MISSION_HUNT, Target.As_Target(cell), Target.TARGET_NONE);
                    if (isPlayer)
                        game.IsTargettingMode = false;
                    AirStrike.Discharged(isPlayer);
                    IsRecalcNeeded = true;
                    fired = true;
                    what = "AIR";
                }
                break;
        }

        if (IsHuman && fired && what != null)
            game.On_Achievement_Event(this, "SUPERWEAPON_FIRED", what);
        return true;
    }

    /// <summary>Place_Object: a finished product is placed — a unit leaves its factory (cell -1), a
    /// building is put down at a cell (next to the base, for humans).</summary>
    public bool Place_Object(RTTIType type, int cell)
    {
        FactoryClass? factory = null;
        switch (type)
        {
            case RTTIType.RTTI_AIRCRAFT:
            case RTTIType.RTTI_AIRCRAFTTYPE:
                if (AircraftFactory != -1) factory = game.Factories.RawPtr(AircraftFactory);
                break;
            case RTTIType.RTTI_INFANTRY:
            case RTTIType.RTTI_INFANTRYTYPE:
                if (InfantryFactory != -1) factory = game.Factories.RawPtr(InfantryFactory);
                break;
            case RTTIType.RTTI_UNIT:
            case RTTIType.RTTI_UNITTYPE:
                if (UnitFactory != -1) factory = game.Factories.RawPtr(UnitFactory);
                break;
            case RTTIType.RTTI_BUILDING:
            case RTTIType.RTTI_BUILDINGTYPE:
                if (BuildingFactory != -1) factory = game.Factories.RawPtr(BuildingFactory);
                break;
        }

        if (factory != null && factory.Has_Completed())
        {
            var tech = factory.Get_Object();
            if (cell == -1)
            {
                var pending = factory.Get_Object();
                if (pending != null)
                {
                    var builder = pending.Who_Can_Build_Me(false, false);
                    var objectType = pending.Techno_Type_Class();
                    if (builder != null && builder.Exit_Object(pending) != 0)
                    {
                        factory.Set_Is_Blocked(false);
                        factory.Completed();
                        Abandon_Production(type);
                        if (IsHuman)
                        {
                            game.On_Achievement_Event(this, "UNIT_CONSTRUCTED", objectType.IniName);
                            if (pending.IsActive)
                                game.On_Ping(this, pending.Center_Coord());
                        }
                    }
                    else
                    {
                        factory.Set_Is_Blocked(true);
                        return false;
                    }
                }
            }
            else
            {
                if (tech == null)
                    return false;
                var builder = tech.Who_Can_Build_Me(false, false);
                if (builder != null)
                {
                    if (tech is not BuildingClass building || building.Passes_Proximity_Check(cell))
                    {
                        builder.Transmit_Message(RadioMessageType.RADIO_HELLO, tech);
                        if (tech.Unlimbo(TdMath.CellCoord(cell)))
                        {
                            factory.Completed();
                            Abandon_Production(type);
                            if (game.PlayerPtr == this)
                            {
                                game.Sound_Effect(VocType.VOC_SLAM);
                                game.Clear_Pending_Placement();
                            }
                            return true;
                        }
                        if (this == game.PlayerPtr)
                            game.Speak(VoxType.VOX_DEPLOY);
                        builder.Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                    }
                }
                return false;
            }
        }
        return true;
    }

    /// <summary>Add_Nuke_Piece: (the "which piece" picking keeps the original's always-last choice).</summary>
    public void Add_Nuke_Piece(int piece = -1)
    {
        if (piece == -1)
        {
            piece = 1;
            if ((NukePieces & 0x01) == 0) piece = 1;
            if ((NukePieces & 0x02) == 0) piece = 2;
            if ((NukePieces & 0x04) == 0) piece = 3;
        }
        NukePieces = (NukePieces | (1 << (piece - 1))) & 0x07;
    }

    public void Detach(uint target, bool all) { }

    public bool Does_Enemy_Building_Exist(StructType btype)
    {
        long bflag = 1L << (int)btype;
        for (var index = HousesType.HOUSE_FIRST; index < HousesType.HOUSE_COUNT; index++)
        {
            var house = game.HouseOrNull(index);
            if (house != null && !Is_Ally(house) && (house.BScan & bflag) != 0)
                return true;
        }
        return false;
    }

    /// <summary>Suggest_New_Object: what the computer's factory should build next — a harvester if it
    /// has none, else what its teams want (most-wanted, at random among ties); buildings from the base plan.</summary>
    public TechnoTypeClass? Suggest_New_Object(RTTIType objecttype)
    {
        // A skirmish computer builds what its expert system chose.
        if (!IsHuman && game.GameToPlay != GameType.GAME_NORMAL)
        {
            return objecttype switch
            {
                RTTIType.RTTI_AIRCRAFT or RTTIType.RTTI_AIRCRAFTTYPE => BuildAircraft != AircraftType.AIRCRAFT_NONE ? game.Types.Aircraft(BuildAircraft) : null,
                RTTIType.RTTI_UNIT or RTTIType.RTTI_UNITTYPE => BuildUnit != UnitType.UNIT_NONE ? game.Types.Unit(BuildUnit) : null,
                RTTIType.RTTI_INFANTRY or RTTIType.RTTI_INFANTRYTYPE => BuildInfantry != InfantryType.INFANTRY_NONE ? game.Types.Infantry(BuildInfantry) : null,
                RTTIType.RTTI_BUILDING or RTTIType.RTTI_BUILDINGTYPE => BuildStructure != StructType.STRUCT_NONE ? game.Types.Building(BuildStructure) : null,
                _ => null,
            };
        }

        bool normal = game.GameToPlay == GameType.GAME_NORMAL;
        switch (objecttype)
        {
            case RTTIType.RTTI_UNIT:
            case RTTIType.RTTI_UNITTYPE:
            {
                if (CurUnits >= MaxUnit)
                    break;

                if (!(normal && game.PlayerPtr.Difficulty == DiffType.DIFF_EASY) && !IsHuman &&
                    (ActiveBScan & (long)TdDefines.STRUCTF_REFINERY) != 0 && (UScan & (long)TdDefines.UNITF_HARVESTER) == 0)
                {
                    var harvester = game.Types.Unit(UnitType.UNIT_HARVESTER);
                    if (harvester.Scenario <= game.BuildLevel)
                        return harvester;
                }

                int count = (int)UnitType.UNIT_COUNT;
                var counter = new int[count];
                if (!normal)
                    for (int index = 0; index < count; index++)
                        counter[index] = Can_Build(game.Types.Unit((UnitType)index), Class.House) && game.Types.Unit((UnitType)index).Level <= game.BuildLevel ? 16 : 0;

                for (int index = 0; index < game.Teams.Count; index++)
                {
                    var tptr = game.Teams.Ptr(index);
                    var team = tptr.Class;
                    if (!tptr.IsFullStrength && team.House == Class.House)
                        for (int sub = 0; sub < team.ClassCount; sub++)
                            if (team.Class[sub] is UnitTypeClass ut)
                                counter[(int)ut.Type] = 1;
                }

                for (int index = 0; index < game.TeamTypes.Count; index++)
                {
                    var team = game.TeamTypes.Ptr(index);
                    if (team.House == Class.House && team.IsPrebuilt && (!team.IsAutocreate || IsAlerted))
                        for (int sub = 0; sub < team.ClassCount; sub++)
                            if (team.Class[sub] is UnitTypeClass ut)
                                counter[(int)ut.Type] = Math.Max(counter[(int)ut.Type], team.DesiredNum[sub]);
                }

                for (int uindex = 0; uindex < game.Units.Count; uindex++)
                {
                    var unit = game.Units.Ptr(uindex);
                    if (unit.Team == null && unit.House == this && unit.Mission is not (MissionType.MISSION_GUARD_AREA or MissionType.MISSION_HUNT or MissionType.MISSION_STICKY or MissionType.MISSION_SLEEP))
                        counter[(int)unit.Class.Type]--;
                }

                return Pick_Best(counter, i => game.Types.Unit((UnitType)i));
            }

            case RTTIType.RTTI_INFANTRY:
            case RTTIType.RTTI_INFANTRYTYPE:
            {
                if (CurUnits >= MaxUnit)
                    break;

                int count = (int)InfantryType.INFANTRY_COUNT;
                var counter = new int[count];
                if (!normal)
                    for (int index = 0; index < count; index++)
                        counter[index] = Can_Build(game.Types.Infantry((InfantryType)index), Class.House) && game.Types.Infantry((InfantryType)index).Level <= game.BuildLevel ? 16 : 0;

                for (int index = 0; index < game.Teams.Count; index++)
                {
                    var tptr = game.Teams.Ptr(index);
                    var team = tptr.Class;
                    if ((team.IsReinforcable || !tptr.IsFullStrength) && team.House == Class.House)
                        for (int sub = 0; sub < team.ClassCount; sub++)
                            if (team.Class[sub] is InfantryTypeClass it)
                                counter[(int)it.Type] += team.DesiredNum[sub] + 1;
                }

                for (int index = 0; index < game.TeamTypes.Count; index++)
                {
                    var team = game.TeamTypes.Ptr(index);
                    if (team.House == Class.House && team.IsPrebuilt && (!team.IsAutocreate || IsAlerted))
                        for (int sub = 0; sub < team.ClassCount; sub++)
                            if (team.Class[sub] is InfantryTypeClass it)
                            {
                                int t = (int)it.Type;
                                counter[t] = Math.Max(counter[t], team.DesiredNum[sub]);
                                counter[t] = Math.Min(counter[t], 5);
                            }
                }

                for (int uindex = 0; uindex < game.Infantry.Count; uindex++)
                {
                    var inf = game.Infantry.Ptr(uindex);
                    if (inf.Team == null && inf.House == this && inf.Mission is not (MissionType.MISSION_GUARD_AREA or MissionType.MISSION_HUNT or MissionType.MISSION_STICKY or MissionType.MISSION_SLEEP))
                        counter[(int)inf.Class.Type]--;
                }

                return Pick_Best(counter, i => game.Types.Infantry((InfantryType)i));
            }

            case RTTIType.RTTI_BUILDING:
            case RTTIType.RTTI_BUILDINGTYPE:
                if (CurBuildings < MaxBuilding)
                {
                    var node = game.Base.Next_Buildable();
                    if (node != null)
                        return game.Types.Building(node.Type);
                }
                break;
        }
        return null;
    }

    /// <summary>The best-wanted affordable buildable types (as the original: once a best is found,
    /// later types with any positive count are added to the list too).</summary>
    TechnoTypeClass? Pick_Best(int[] counter, Func<int, TechnoTypeClass> type)
    {
        int bestval = -1;
        var bestlist = new List<int>();
        for (int i = 0; i < counter.Length; i++)
        {
            var t = type(i);
            if (counter[i] > 0 && Can_Build(t, Class.House) && t.Cost_Of(game.Special) <= Available_Money())
            {
                if (bestval == -1 || bestval < counter[i])
                {
                    bestval = counter[i];
                    bestlist.Clear();
                }
                bestlist.Add(i);
            }
        }
        return bestlist.Count != 0 ? type(bestlist[game.Random_Pick(0, bestlist.Count - 1)]) : null;
    }

    // ---- Capture the flag ----

    public bool Flag_Remove(uint target, bool setHome = false)
    {
        bool rc = false;
        if (game.Target_Legal(target))
        {
            var obj = game.As_Unit(target);
            if (obj != null)
            {
                rc = obj.Flag_Remove();
            }
            else
            {
                int cell = game.As_Cell(target);
                if (game.Map.In_Radar(cell))
                    rc = game.Map[cell].Flag_Remove();
            }
            if (rc && FlagLocation == target)
                FlagLocation = Target.TARGET_NONE;

            if (setHome && FlagHome != 0)
            {
                game.Map[FlagHome].Overlay = OverlayType.OVERLAY_NONE;
                game.Map.Flag_Cell(FlagHome);
                FlagHome = 0;
            }
        }
        return rc;
    }

    /// <summary>Flag_Attach(cell): the flag is put on the ground (searching outward for a free spot).</summary>
    public bool Flag_Attach(int cell, bool setHome = false)
    {
        bool clockwise = game.IRandom(0, 1) != 0;
        if (!game.Map.In_Radar(cell))
            return false;

        Flag_Remove(FlagLocation, setHome);
        int newcell = cell;
        bool rc = game.Map[newcell].Flag_Place(Class.House);
        if (!rc)
        {
            for (int dist = 1; dist < 32; dist++)
            {
                int rot = game.IRandom((int)FacingType.FACING_N, (int)FacingType.FACING_NW);
                for (int fcounter = 0; fcounter <= 7; fcounter++)
                {
                    newcell = TdMath.CoordCell(TdMath.CoordMove(TdMath.CellCoord(cell), (byte)(rot << 5), dist * 256));
                    if (game.Map.In_Radar(newcell) && game.Map[newcell].Flag_Place(Class.House))
                    {
                        dist = 32;
                        rc = true;
                        break;
                    }
                    if (clockwise)
                    {
                        rot++;
                        if (rot > (int)FacingType.FACING_NW) rot = (int)FacingType.FACING_N;
                    }
                    else
                    {
                        rot--;
                        if (rot < (int)FacingType.FACING_N) rot = (int)FacingType.FACING_NW;
                    }
                }
            }
        }

        if (rc)
        {
            FlagLocation = Target.As_Target(newcell);
            if (setHome || FlagHome == 0)
            {
                game.Map[newcell].Overlay = OverlayType.OVERLAY_FLAG_SPOT;
                FlagHome = newcell;
            }
        }
        else if (FlagHome != 0)
        {
            rc = game.Map[FlagHome].Flag_Place(Class.House);
        }
        return rc;
    }

    public bool Flag_Attach(UnitClass? obj, bool setHome = false)
    {
        if (obj != null && !obj.IsInLimbo)
        {
            Flag_Remove(FlagLocation, setHome);
            obj.Flag_Attach(Class.House);
            FlagLocation = obj.As_Target();
            return true;
        }
        return false;
    }

    /// <summary>MPlayer_Defeated: this player is out; the game ends when one side (or alliance) remains.</summary>
    public void MPlayer_Defeated()
    {
        IsDefeated = true;

        // A smart computer's defeat makes the other computers band together against the humans.
        if (IQ == game.Rule.MaxIQ && !IsHuman && game.Rule.IsComputerParanoid)
            Computer_Paranoid(game);
        if (game.Special.IsCaptureTheFlag)
        {
            if (FlagLocation != 0)
                Flag_Remove(FlagLocation, true);
            else if (FlagHome != 0)
                Flag_Remove((uint)FlagHome, true);
        }
        IonCannon.Remove(true);
        AirStrike.Remove(true);
        NukeStrike.Remove(true);

        if (game.PlayerPtr == this)
            game.On_Defeated_Message($"{Name} has been defeated");
        else if (IsHuman)
            game.Add_Message($"{Name} has been defeated", RemapColor, 600);

        int numAlive = 0, numHumans = 0;
        for (int i = 0; i < game.MPlayerMax; i++)
        {
            var hptr = game.HouseOrNull((HousesType)((int)HousesType.HOUSE_MULTI1 + i));
            if (hptr != null && !hptr.IsDefeated)
            {
                if (hptr.IsHuman)
                    numHumans++;
                numAlive++;
            }
        }

        bool allAllies = true;
        for (int i = 0; i < game.MPlayerMax && allAllies; i++)
        {
            var hptr = game.HouseOrNull((HousesType)((int)HousesType.HOUSE_MULTI1 + i));
            if (hptr == null || hptr.IsDefeated)
                continue;
            for (int j = 0; j < game.MPlayerMax; j++)
            {
                var hptr2 = game.HouseOrNull((HousesType)((int)HousesType.HOUSE_MULTI1 + j));
                if (hptr2 != null && !hptr2.IsDefeated && !hptr.Is_Ally(hptr2))
                {
                    allAllies = false;
                    break;
                }
            }
        }
        if (allAllies)
            numAlive = 1;

        if (numAlive == 1 || numHumans == 0)
        {
            if (game.PlayerPtr.IsDefeated)
                game.PlayerLoses = true;
            else
                game.PlayerWins = true;
            // (The original then updates the multiplayer score table and drops network connections.)
        }
    }

    /// <summary>Blowup_All: everything the house owns is destroyed (infantry die by random warheads).</summary>
    public void Blowup_All()
    {
        for (int i = 0; i < game.Units.Count; i++)
        {
            var u = game.Units.Ptr(i);
            if (u.House == this && !u.IsInLimbo)
            {
                int count = 0;
                while (i < game.Units.Count && game.Units.Ptr(i) == u && u.Strength != 0)
                {
                    game.Explosion_Damage(u.Center_Coord(), 0x7fff, null, WarheadType.WARHEAD_HE);
                    count++;
                    if (count > 5)
                    {
                        u.Delete();
                        break;
                    }
                }
                i--;
            }
        }

        for (int i = 0; i < game.Aircraft.Count; i++)
        {
            var a = game.Aircraft.Ptr(i);
            if (a.House == this && !a.IsInLimbo)
            {
                int damage = 0x7fff;
                a.Take_Damage(ref damage, 0, WarheadType.WARHEAD_HE, null);
                if (!a.IsActive)
                    i--;
            }
        }

        for (int i = 0; i < game.Buildings.Count; i++)
        {
            var b = game.Buildings.Ptr(i);
            if (b.House == this && !b.IsInLimbo)
            {
                int count = 0;
                b.IsSurvivorless = true;
                while (i < game.Buildings.Count && game.Buildings.Ptr(i) == b && b.Strength != 0)
                {
                    game.Explosion_Damage(b.Center_Coord(), 0x7fff, null, WarheadType.WARHEAD_HE);
                    count++;
                    if (count > 5)
                    {
                        b.Delete();
                        break;
                    }
                }
            }
        }

        for (int i = 0; i < game.Infantry.Count; i++)
        {
            var inf = game.Infantry.Ptr(i);
            if (inf.House == this && !inf.IsInLimbo)
            {
                int count = 0;
                while (i < game.Infantry.Count && game.Infantry.Ptr(i) == inf && inf.Strength != 0)
                {
                    var warhead = (WarheadType)game.IRandom((int)WarheadType.WARHEAD_SA, (int)WarheadType.WARHEAD_FIRE);
                    game.Explosion_Damage(inf.Center_Coord(), 0x7fff, null, warhead);
                    if (inf.IsActive)
                    {
                        int damage = 0x7fff;
                        inf.Take_Damage(ref damage, 0, warhead);
                    }
                    count++;
                    if (count > 5)
                    {
                        inf.Delete();
                        break;
                    }
                }
            }
        }
    }

    int Borrowed_Delay() => game.IsV107 ? TdDefines.TICKS_PER_SECOND * 3 : TdDefines.TICKS_PER_SECOND;

    public bool Flag_To_Die()
    {
        if (!IsToWin && !IsToDie && !IsToLose)
        {
            IsToDie = true;
            BorrowedTime.Set(Borrowed_Delay());
        }
        return IsToDie;
    }

    public bool Flag_To_Win()
    {
        if (!IsToWin && !IsToDie && !IsToLose)
        {
            IsToWin = true;
            BorrowedTime.Set(Borrowed_Delay());
        }
        return IsToWin;
    }

    public bool Flag_To_Lose()
    {
        IsToWin = false;
        if (!IsToDie && !IsToLose)
        {
            IsToLose = true;
            BorrowedTime.Set(Borrowed_Delay());
        }
        return IsToLose;
    }

    /// <summary>Init_Data: a multiplayer house's colour, side and starting money.</summary>
    public void Init_Data(PlayerColorType color, HousesType house, int credits)
    {
        Credits = InitialCredits = credits;
        VisibleCredits = Credits;
        ActLike = house;
        RemapColor = color;
        RemapTable = game.Types.Remap_For_Color(color) ?? RemapTable;
    }

    /// <summary>Power_Fraction: power output over drain (0x100 = enough).</summary>
    public int Power_Fraction()
    {
        if (Power != 0)
            return Drain != 0 ? (int)TdMath.CardinalToFixed((uint)Drain, (uint)Power) : 0x0100;
        return 0;
    }

    public int Tiberium_Fraction() => Tiberium == 0 ? 0 : (int)TdMath.CardinalToFixed((uint)Capacity, (uint)Tiberium);

    public bool Has_Nuke_Device() => game.GameToPlay != GameType.GAME_NORMAL || !IsHuman || (NukePieces & 0x07) == 0x07;

    /// <summary>Sell_Wall: sells an owned wall segment for half its cost.</summary>
    public void Sell_Wall(int cell)
    {
        if (cell <= 0)
            return;
        var c = game.Map[cell];
        OverlayType overlay = c.Overlay;
        if (overlay == OverlayType.OVERLAY_NONE || c.Owner != Class.House || !game.Types.Overlay(overlay).IsWall)
            return;

        StructType wall = overlay switch
        {
            OverlayType.OVERLAY_SANDBAG_WALL => StructType.STRUCT_SANDBAG_WALL,
            OverlayType.OVERLAY_CYCLONE_WALL => StructType.STRUCT_CYCLONE_WALL,
            OverlayType.OVERLAY_BRICK_WALL => StructType.STRUCT_BRICK_WALL,
            OverlayType.OVERLAY_BARBWIRE_WALL => StructType.STRUCT_BARBWIRE_WALL,
            OverlayType.OVERLAY_WOOD_WALL => StructType.STRUCT_WOOD_WALL,
            _ => StructType.STRUCT_NONE,
        };
        if (wall == StructType.STRUCT_NONE)
            return;
        var btype = game.Types.Building(wall);
        if (btype.IsUnsellable)
            return;

        if (game.PlayerPtr == this)
            game.Sound_Effect(VocType.VOC_CASHTURN);
        Refund_Money(btype.Cost_Of(game.Types) / 2);
        c.Overlay = OverlayType.OVERLAY_NONE;
        c.OverlayData = 0;
        c.Owner = HousesType.HOUSE_NONE;
        c.Wall_Update();
        c.Adjacent_Cell(FacingType.FACING_N)?.Wall_Update();
        c.Adjacent_Cell(FacingType.FACING_W)?.Wall_Update();
        c.Adjacent_Cell(FacingType.FACING_S)?.Wall_Update();
        c.Adjacent_Cell(FacingType.FACING_E)?.Wall_Update();
        c.Recalc_Attributes();
        c.Redraw_Objects();
        game.Detach_This_From_All(Target.As_Target(cell), true);
    }

    /// <summary>Check_Pertinent_Structures: (the "early win" option) a house with no buildings or MCV dies.</summary>
    public void Check_Pertinent_Structures()
    {
        if (!game.Special.IsEarlyWin || IsToDie || IsToWin || IsToLose || IsDefeated)
            return;

        bool anyGood = false;
        for (int index = 0; index < game.Buildings.Count && !anyGood; index++)
        {
            var b = game.Buildings.Ptr(index);
            if (b.IsActive && b.House == this && !b.Class.IsWall && !b.IsInLimbo && b.Strength > 0)
                anyGood = true;
        }
        for (int index = 0; index < game.Units.Count && !anyGood; index++)
        {
            var unit = game.Units.Ptr(index);
            if (unit.IsActive && unit.Class.Type == UnitType.UNIT_MCV && unit.House == this && !unit.IsInLimbo && unit.Strength > 0)
                anyGood = true;
        }
        if (!anyGood)
            Flag_To_Die();
    }
}

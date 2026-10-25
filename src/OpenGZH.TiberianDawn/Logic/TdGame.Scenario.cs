using OpenGZH.TiberianDawn.Data;
using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Scenario;

namespace OpenGZH.TiberianDawn.Logic;

// INI.CPP / SCENARIO.CPP / DISPLAY.CPP: loading a scenario into the game.
public sealed partial class TdGame
{
    public static readonly string[] TheaterNames = ["DESERT", "JUNGLE", "TEMPERATE", "WINTER"];

    // [Basic] strings the front end uses.
    public string ScenarioName = "";
    public string IntroMovie = "x", BriefMovie = "x", WinMovie = "x", WinMovie2 = "x", WinMovie3 = "x", WinMovie4 = "x", LoseMovie = "x", ActionMovie = "x";
    public string TransitTheme = "No Theme";
    public string BriefingText = "";
    public uint ScenarioCRC;
    public int CarryOverMoney, CarryOverPercent, CarryOverCap;
    public HousesType Whom = HousesType.HOUSE_GOOD;

    /// <summary>The scenario number, side, direction and variation from a name like SCG01EA.</summary>
    public bool Set_Scenario_From_Name(string name)
    {
        if (name.Length < 7 || !name.StartsWith("SC", StringComparison.OrdinalIgnoreCase) || !int.TryParse(name.AsSpan(3, 2), out int number))
            return false;
        Scenario = number;
        ScenPlayer = char.ToUpperInvariant(name[2]) switch
        {
            'G' => ScenarioPlayerType.SCEN_PLAYER_GDI,
            'B' => ScenarioPlayerType.SCEN_PLAYER_NOD,
            'J' => ScenarioPlayerType.SCEN_PLAYER_JP,
            _ => ScenarioPlayerType.SCEN_PLAYER_MPLAYER,
        };
        ScenDir = char.ToUpperInvariant(name[5]) == 'W' ? ScenarioDirType.SCEN_DIR_WEST : ScenarioDirType.SCEN_DIR_EAST;
        ScenVar = (ScenarioVarType)Math.Clamp(char.ToUpperInvariant(name[6]) - 'A', 0, 3);
        ScenarioName = name[..7];
        return true;
    }

    public static TheaterType Theater_From_Name(string? name)
    {
        if (name != null)
            for (int index = 0; index < TheaterNames.Length; index++)
                if (string.Equals(name, TheaterNames[index], StringComparison.OrdinalIgnoreCase))
                    return (TheaterType)index;
        return TheaterType.THEATER_NONE;
    }

    /// <summary>Clear_Scenario: everything back to empty before a scenario loads.</summary>
    public void Clear_Scenario()
    {
        EndCountDown = TdDefines.TICKS_PER_SECOND * 30;
        CrateCount = 0;
        CrateTimer.Set(0);
        CrateMaker = false;
        Map.Init_Clear();
        Logic.Clear();
        Houses.FreeAll();
        foreach (var list in HouseTriggers)
            list.Clear();
        CurrentObject.Clear();
        TeamTypes.FreeAll();
        Teams.FreeAll();
        Array.Clear(TeamNumber);
        Triggers.FreeAll();
        Aircraft.FreeAll();
        Anims.FreeAll();
        Buildings.FreeAll();
        Bullets.FreeAll();
        Infantry.FreeAll();
        Overlays.FreeAll();
        Smudges.FreeAll();
        Templates.FreeAll();
        Terrains.FreeAll();
        Units.FreeAll();
        Factories.FreeAll();
        Base.Init();
        Array.Clear(CellTriggers);
    }

    /// <summary>DisplayClass::Read_INI: map size, theater, waypoints, and the cell triggers.</summary>
    void Map_Read_INI(WwProfile ini)
    {
        Map.Set_Map_Dimensions(ini.Get_Int("MAP", "X", 1),
            ini.Get_Int("MAP", "Y", 1),
            ini.Get_Int("MAP", "Width", TdDefines.MAP_CELL_W - 2),
            ini.Get_Int("MAP", "Height", TdDefines.MAP_CELL_H - 2));

        string name = ini.Get_String("MAP", "Theater", TheaterNames[(int)TheaterType.THEATER_DESERT], 13);
        Map.Theater = Theater_From_Name(name);
        if (Map.Theater == TheaterType.THEATER_NONE)
            Map.Theater = TheaterType.THEATER_DESERT;

        for (int i = 0; i < (int)WaypointType.WAYPT_COUNT; i++)
        {
            Waypoint[i] = ini.Get_Int("Waypoints", i.ToString(), -1);
            if (Waypoint[i] != -1)
                Map[Waypoint[i]].IsWaypoint = true;
        }
        if (Waypoint[(int)WaypointType.WAYPT_HOME] == -1)
            Waypoint[(int)WaypointType.WAYPT_HOME] = TdMath.XyCell(Map.MapCellX, Map.MapCellY);

        foreach (string key in ini.Entries("CellTriggers"))
        {
            string buf = ini.Get_String("CellTriggers", key, null, 20 - 1);
            int cell = WwProfile.Atoi(key);
            if (cell > 0 && cell < TdDefines.MAP_CELL_TOTAL && !Map[cell].IsTrigger)
            {
                CellTriggers[cell] = TriggerClass.As_Pointer(this, buf);
                if (CellTriggers[cell] != null)
                {
                    Map[cell].IsTrigger = true;
                    CellTriggers[cell]!.AttachCount++;
                }
            }
        }
    }

    /// <summary>
    /// Read_Scenario_Ini: load a scenario from its INI text (and .BIN map, when there is one).
    /// <paramref name="root"/> is the scenario name (SCG01EA); missionIni is MISSION.INI, where
    /// the briefings of scenarios without a [Briefing] section live.
    /// </summary>
    public bool Read_Scenario_Ini(string root, string iniText, byte[]? bin, bool fresh = true, string? missionIni = null)
    {
        ScenarioInit++;

        if (fresh)
            Clear_Scenario();
        if (ScenarioName.Length == 0)
            Set_Scenario_From_Name(root);

        var ini = new WwProfile(iniText);

        ScenarioCRC = 0;
        foreach (char c in iniText)
            Add_CRC(ref ScenarioCRC, (byte)c);

        IntroMovie = ini.Get_String("Basic", "Intro", "x", 13);
        BriefMovie = ini.Get_String("Basic", "Brief", "x", 13);
        WinMovie = ini.Get_String("Basic", "Win", "x", 13);
        WinMovie2 = ini.Get_String("Basic", "Win2", "x", 13);
        WinMovie3 = ini.Get_String("Basic", "Win3", "x", 13);
        WinMovie4 = ini.Get_String("Basic", "Win4", "x", 13);
        LoseMovie = ini.Get_String("Basic", "Lose", "x", 13);
        ActionMovie = ini.Get_String("Basic", "Action", "x", 13);

        if (GameToPlay == GameType.GAME_NORMAL)
        {
            // The remaster's menu: build level follows the scenario; the N64 missions need 15.
            if (Scenario <= 15)
                BuildLevel = Scenario;
            else if (string.Equals(ScenarioName, "scg30ea", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ScenarioName, "scg90ea", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ScenarioName, "scb22ea", StringComparison.OrdinalIgnoreCase))
                BuildLevel = 15;
            else
                BuildLevel = ini.Get_Int("Basic", "BuildLevel", Scenario);
        }
        if (Special.IsJurassic && AreThingiesEnabled)
            BuildLevel = 98;

        TransitTheme = ini.Get_String("Basic", "Theme", "No Theme", 128);

        TeamTypeClass.Read_INI(this, ini);
        HouseClass.Read_INI(this, ini);
        TriggerClass.Read_INI(this, ini);
        Map_Read_INI(ini);

        if (GameToPlay == GameType.GAME_NORMAL)
        {
            string buf = ini.Get_String("Basic", "Player", "GoodGuy", 127);
            CarryOverPercent = ini.Get_Int("Basic", "CarryOverMoney", 100);
            CarryOverPercent = (int)TdMath.CardinalToFixed(100, (uint)CarryOverPercent);
            CarryOverCap = ini.Get_Int("Basic", "CarryOverCap", -1);
            PlayerPtr = HouseFor(Types.House_From_Name(buf));
            PlayerPtr.IsHuman = true;
            int carryover = CarryOverCap != -1
                ? Math.Min((int)TdMath.FixedToCardinal((uint)CarryOverMoney, (uint)CarryOverPercent), CarryOverCap)
                : (int)TdMath.FixedToCardinal((uint)CarryOverMoney, (uint)CarryOverPercent);
            PlayerPtr.Credits += carryover;
            PlayerPtr.InitialCredits += carryover;
            if (Special.IsJurassic)
                PlayerPtr.ActLike = Whom;
            if (Special.IsEasy)
                PlayerPtr.Assign_Handicap(DiffType.DIFF_EASY);
            else if (Special.IsDifficult)
                PlayerPtr.Assign_Handicap(DiffType.DIFF_HARD);
        }
        else
        {
            GlyphX_Assign_Houses(skirmishSeed);
        }

        if (fresh)
        {
            if (!Map.Read_Binary(bin, ref ScenarioCRC))
                TemplateClass.Read_INI(this, ini);
        }

        TerrainClass.Read_INI(this, ini);
        UnitClass.Read_INI(this, ini);
        AircraftClass.Read_INI(this, ini);
        InfantryClass.Read_INI(this, ini);
        BuildingClass.Read_INI(this, ini);
        Base.Read_INI(ini);
        OverlayClass.Read_INI(this, ini);
        SmudgeClass.Read_INI(this, ini);

        // The briefing: [Briefing] 1, 2, 3... joined with spaces (the buffer held 512 characters).
        BriefingText = Read_Briefing(ini, "Briefing");
        if (BriefingText.Length == 0 && missionIni != null)
            BriefingText = Read_Briefing(new WwProfile(missionIni), root);

        Map.Overpass();

        // The remaster's fixes to individual scenarios.
        if (Is_Scenario("scb07ea"))
            Map[2795].Override_Land_Type(LandType.LAND_ROCK);
        if (Is_Scenario("scb09ea"))
            Attach_Radar_Trigger("dely", onlyIfNone: false);
        if (Is_Scenario("scb10eb"))
            Map[2015].Override_Land_Type(LandType.LAND_ROCK);
        if (Is_Scenario("scb13eb"))
        {
            var prod = TriggerClass.Create(this)!;
            prod.Set_Name("prod");
            prod.Event = EventType.EVENT_PLAYER_ENTERED;
            prod.Action = TriggerClass.ActionType.ACTION_BEGIN_PRODUCTION;
            prod.House = HousesType.HOUSE_BAD;
            foreach (int cell in (int[])[276, 340, 404, 468])
            {
                CellTriggers[cell] = prod;
                prod.AttachCount++;
            }
            var xxxx = TriggerClass.As_Pointer(this, "xxxx");
            if (xxxx != null)
                xxxx.IsPersistant = TriggerClass.PersistantType.PERSISTANT;
        }
        if (Is_Scenario("scb13ec"))
            Attach_Radar_Trigger("delx", onlyIfNone: true);

        Fixup_Scenario();

        if (GameToPlay != GameType.GAME_NORMAL || ScenPlayer == ScenarioPlayerType.SCEN_PLAYER_2PLAYER || ScenPlayer == ScenarioPlayerType.SCEN_PLAYER_MPLAYER)
        {
            // (The remaster's computer never blitzes; its AI decides when to attack.)
            if (MPlayerGhosts || DebugMap)
                MPlayerBlitz = false;

            if (!DebugMap)
            {
                int saveInit = ScenarioInit;
                ScenarioInit = 0;
                Create_Units();
                ScenarioInit = saveInit;
            }

            if (MPlayerGoodies != 0)
                for (int index = 0; index < MPlayerCount; index++)
                    Map.Place_Random_Crate();
        }

        ScenarioInit--;
        return true;
    }

    /// <summary>Read_Scenario (SCENARIO.CPP): clear, read the scenario, then Fill_In_Data.</summary>
    public bool Read_Scenario(string root, string iniText, byte[]? bin, string? missionIni = null)
    {
        Clear_Scenario();
        ScenarioInit++;
        if (!Read_Scenario_Ini(root, iniText, bin, true, missionIni))
            return false;
        Fill_In_Data();
        ScenarioInit--;
        return true;
    }

    /// <summary>Fill_In_Data: the factories fill the sidebar.</summary>
    public void Fill_In_Data()
    {
        ScenarioInit++;
        for (int index = 0; index < Buildings.Count; index++)
            Buildings.Ptr(index).Update_Buildables();
        ScenarioInit--;
    }

    bool Is_Scenario(string name) => string.Equals(ScenarioName, name, StringComparison.OrdinalIgnoreCase);

    // The first GDI Comm. Center gets the named trigger.
    void Attach_Radar_Trigger(string name, bool onlyIfNone)
    {
        for (int index = 0; index < Buildings.Count; ++index)
        {
            var building = Buildings.Ptr(index);
            if (building.Owner() == HousesType.HOUSE_GOOD && building.Class.Type == StructType.STRUCT_RADAR && (!onlyIfNone || building.Trigger == null))
            {
                building.Trigger = TriggerClass.As_Pointer(this, name);
                if (building.Trigger != null)
                    building.Trigger.AttachCount++;
                break;
            }
        }
    }

    static string Read_Briefing(WwProfile ini, string section)
    {
        const int BriefingSize = 512;
        var text = new System.Text.StringBuilder();
        for (int index = 1; ; index++)
        {
            int len = BriefingSize - text.Length - 1;
            if (len <= 0)
                break;
            string stage = ini.Get_String(section, index.ToString(), "", len);
            if (stage.Length == 0)
                break;
            text.Append(stage).Append(' ');
        }
        return text.ToString();
    }

    /// <summary>Fixup_Scenario: the remaster's per-scenario rule changes (fraidy-cat civilians avoid
    /// tiberium only in GDI 8B, the Orca's laser in mission 72, Modern Balance numbers).</summary>
    public void Fixup_Scenario()
    {
        bool isScg08ea = GameToPlay == GameType.GAME_NORMAL && PlayerPtr.ActLike == HousesType.HOUSE_GOOD && Scenario == 8 && ScenVar == ScenarioVarType.SCEN_VAR_B;
        for (var index = InfantryType.INFANTRY_FIRST; index < InfantryType.INFANTRY_COUNT; index++)
        {
            var infantryType = Types.Infantry(index);
            if (infantryType.IsFraidyCat)
                infantryType.IsAvoidingTiberium = isScg08ea;
        }

        Types.Aircraft(AircraftType.AIRCRAFT_ORCA).Primary = GameToPlay == GameType.GAME_NORMAL && Scenario == 72
            ? WeaponType.WEAPON_OBELISK_LASER
            : WeaponType.WEAPON_DRAGON;

        if (Special.ModernBalance)
        {
            Types.Building(StructType.STRUCT_WEAP).MaxStrength = 520;
            Types.Unit(UnitType.UNIT_APC).Pre |= TdDefines.STRUCTF_REPAIR;
        }
        else
        {
            Types.Building(StructType.STRUCT_WEAP).MaxStrength = 400;
            Types.Unit(UnitType.UNIT_APC).Pre &= ~(long)TdDefines.STRUCTF_REPAIR;
        }
    }
}

using OpenGZH.TiberianDawn.Logic.Objects;

namespace OpenGZH.TiberianDawn.Logic;

/// <summary>One player of a skirmish (the remaster's CNCPlayerInfoStruct).</summary>
public sealed record TdSkirmishPlayer(string Name, HousesType Side, PlayerColorType Color, bool IsAI, int Team, int StartLocation = TdGame.RANDOM_START_POSITION);

/// <summary>The skirmish options (the remaster's CNCMultiplayerOptionsStruct).</summary>
public sealed record TdSkirmishOptions
{
    public int Credits = 5000;
    public bool Bases = true;
    public bool Tiberium = true;
    public bool Crates;
    public int UnitCount = 0;
    public int BuildLevel = 10;
    public bool CaptureTheFlag;
    public bool MCVDeploy;
    public bool Visceroids;
    public bool EarlyWin;
    public bool ModernBalance;
    public bool SuperWeapons = true;
}

// The remaster's skirmish setup (DLLInterface.cpp's CNC_Set_Multiplayer_Data and
// GlyphX_Assign_Houses, INI.CPP's Create_Units).
public sealed partial class TdGame
{
    public const int RANDOM_START_POSITION = 0x7f;

    public int MPlayerCredits;
    public bool MPlayerSolo;
    public ushort MPlayerLocalID;
    public readonly ushort[] MPlayerID = new ushort[TdDefines.MAX_PLAYERS];
    public readonly string[] MPlayerNames = new string[TdDefines.MAX_PLAYERS];
    public readonly bool[] MPlayerIsHuman = new bool[TdDefines.MAX_PLAYERS];
    public readonly int[] MPlayerTeamIDs = new int[TdDefines.MAX_PLAYERS];
    public readonly int[] MPlayerStartLocations = new int[TdDefines.MAX_PLAYERS];
    int reserveInfantryIndex, reserveUnitIndex;

    static HousesType MPlayerID_To_HousesType(ushort id) => (HousesType)(id & 0x000f);
    static PlayerColorType MPlayerID_To_ColorIndex(ushort id) => (PlayerColorType)(id >> 4);
    static ushort Build_MPlayerID(int colorIndex, HousesType house) => (ushort)((colorIndex << 4) | (int)house);

    /// <summary>CNC_Set_Multiplayer_Data: the players and options of a skirmish. The first player is
    /// the local (human) one.</summary>
    public bool Set_Multiplayer_Data(TdSkirmishOptions options, IReadOnlyList<TdSkirmishPlayer> players)
    {
        if (players.Count <= 0 || players.Count > TdDefines.MAX_PLAYERS)
            return false;

        GameToPlay = GameType.GAME_GLYPHX_MULTIPLAYER;
        ScenPlayer = ScenarioPlayerType.SCEN_PLAYER_MPLAYER;
        MPlayerBases = options.Bases;
        MPlayerCredits = options.Credits;
        MPlayerTiberium = options.Tiberium ? 1 : 0;
        MPlayerGoodies = options.Crates ? 1 : 0;
        MPlayerGhosts = false;
        MPlayerSolo = true;
        MPlayerUnitCount = options.UnitCount;
        BuildLevel = options.BuildLevel;
        Special.IsMCVDeploy = options.MCVDeploy;
        Special.IsVisceroids = options.Visceroids;
        Special.IsCaptureTheFlag = options.CaptureTheFlag;
        Special.IsEarlyWin = options.EarlyWin;
        Special.ModernBalance = options.ModernBalance;
        Rule.AllowSuperWeapons = options.SuperWeapons;
        Special.IsTGrowth = options.Tiberium;
        Special.IsTSpread = options.Tiberium;

        MPlayerCount = 0;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            MPlayerHouses[i] = p.Side;
            MPlayerNames[i] = p.Name.Length >= 12 ? p.Name[..11] : p.Name;   // MPLAYER_NAME_MAX
            MPlayerID[i] = Build_MPlayerID((int)p.Color, p.Side);
            MPlayerIsHuman[i] = !p.IsAI;
            if (p.IsAI)
                MPlayerGhosts = true;
            MPlayerTeamIDs[i] = p.Team;
            MPlayerStartLocations[i] = p.StartLocation;
            if (i > 0 && MPlayerStartLocations[i] == 0 && MPlayerStartLocations[0] == 0)
                MPlayerStartLocations[i] = i;
            MPlayerCount++;
        }
        MPlayerLocalID = MPlayerID[0];
        return true;
    }

    /// <summary>GlyphX_Assign_Houses: each player gets a random multiplayer house, its side, colour
    /// and money, and a start location (shuffled among the free ones); unused computer houses are
    /// removed; players on the same team become allies.</summary>
    void GlyphX_Assign_Houses(uint seed)
    {
        var houseUsed = new bool[TdDefines.MAX_PLAYERS];
        Random.Srand(seed);

        // The start locations no player asked for, shuffled.
        var randomStartLocations = new int[26];
        int numStartLocations = 0, numRandomStartLocations = 0;
        for (int i = 0; i < 26; i++)
        {
            if (Waypoint[i] != -1)
            {
                bool preassigned = false;
                for (int j = 0; !preassigned && j < MPlayerCount; j++)
                    if (MPlayerStartLocations[j] == numStartLocations)
                        preassigned = true;
                if (!preassigned && i < TdDefines.MAX_PLAYERS)
                    randomStartLocations[numRandomStartLocations++] = numStartLocations;
                numStartLocations++;
            }
        }
        if (numRandomStartLocations > 1)
        {
            const int RAND_MAX = 0x7fff;
            for (int i = 0; i < numRandomStartLocations - 1; i++)
            {
                int j = i + Random.Rand() / (RAND_MAX / (numRandomStartLocations - i) + 1);
                (randomStartLocations[j], randomStartLocations[i]) = (randomStartLocations[i], randomStartLocations[j]);
            }
        }

        int randomStartLocation = 0;
        for (int i = 0; i < MPlayerCount; i++)
        {
            int j = Random_Pick(0, MPlayerMax - 1);
            if (houseUsed[j])
            {
                i--;
                continue;
            }
            var house = (HousesType)(j + (int)HousesType.HOUSE_MULTI1);
            var prefHouse = MPlayerID_To_HousesType(MPlayerID[i]);
            var color = MPlayerID_To_ColorIndex(MPlayerID[i]);
            var housep = HouseFor(house);
            MPlayerHouses[i] = house;
            houseUsed[j] = true;
            housep.Name = MPlayerNames[i];
            housep.IsHuman = MPlayerIsHuman[i];
            housep.Init_Data(color, prefHouse, MPlayerCredits);
            if (MPlayerStartLocations[i] != RANDOM_START_POSITION)
                housep.StartLocationOverride = MPlayerStartLocations[i];
            else
                housep.StartLocationOverride = randomStartLocation < numRandomStartLocations ? randomStartLocations[randomStartLocation++] : -1;
            if (MPlayerID[i] == MPlayerLocalID)
                PlayerPtr = housep;
        }

        // Houses nobody plays are removed with everything they own.
        for (int i = 0; i < TdDefines.MAX_PLAYERS; i++)
        {
            if (houseUsed[i])
                continue;
            var housep = HouseOrNull((HousesType)(i + (int)HousesType.HOUSE_MULTI1));
            if (housep != null && !housep.IsHuman)
                housep.Clobber_All();
        }

        for (int i = 0; i < MPlayerCount; i++)
        {
            var housep = HouseOrNull(MPlayerHouses[i]);
            if (housep == null)
                continue;
            for (int j = 0; j < MPlayerCount; j++)
                if (i != j && MPlayerTeamIDs[i] == MPlayerTeamIDs[j] && HouseOrNull(MPlayerHouses[j]) != null)
                    housep.Make_Ally(MPlayerHouses[j]);
        }
    }

    void Reserve_Infantry()
    {
        if (Infantry.Count == Infantry.Length)
        {
            Infantry.Ptr(reserveInfantryIndex).Delete();
            reserveInfantryIndex = (reserveInfantryIndex + 1) % Infantry.Length;
        }
    }

    void Reserve_Unit()
    {
        if (Units.Count == Units.Length)
        {
            Units.Ptr(reserveUnitIndex).Delete();
            reserveUnitIndex = (reserveUnitIndex + 1) % Units.Length;
        }
    }

    static readonly (int MinLevel, int GDICount, UnitType GDIType, int NODCount, UnitType NODType)[] UTable =
    [
        (0, 1, UnitType.UNIT_MTANK, 2, UnitType.UNIT_LTANK),
        (2, 1, UnitType.UNIT_JEEP, 1, UnitType.UNIT_BUGGY),
        (3, 1, UnitType.UNIT_MLRS, 1, UnitType.UNIT_ARTY),
        (4, 1, UnitType.UNIT_APC, 2, UnitType.UNIT_BUGGY),
        (5, 1, UnitType.UNIT_JEEP, 1, UnitType.UNIT_BIKE),
        (5, 2, UnitType.UNIT_JEEP, 1, UnitType.UNIT_FTANK),
        (6, 1, UnitType.UNIT_MSAM, 1, UnitType.UNIT_MSAM),
        (7, 1, UnitType.UNIT_HTANK, 2, UnitType.UNIT_STANK),
    ];

    static readonly (int MinLevel, int GDICount, InfantryType GDIType, int NODCount, InfantryType NODType)[] ITable =
    [
        (0, 1, InfantryType.INFANTRY_E1, 1, InfantryType.INFANTRY_E1),
        (1, 1, InfantryType.INFANTRY_E2, 1, InfantryType.INFANTRY_E3),
        (3, 1, InfantryType.INFANTRY_E3, 1, InfantryType.INFANTRY_E3),
        (5, 1, InfantryType.INFANTRY_E3, 1, InfantryType.INFANTRY_E4),
        (7, 1, InfantryType.INFANTRY_RAMBO, 1, InfantryType.INFANTRY_RAMBO),
    ];

    /// <summary>Create_Units: each player's MCV (with bases on) at its start location, plus its share
    /// of starting units and infantry, spread in small groups around it.</summary>
    void Create_Units()
    {
        reserveInfantryIndex = reserveUnitIndex = 0;

        int uLimit = 0, iLimit = 0;
        for (int i = 0; i < UTable.Length; i++)
            if ((uint)BuildLevel >= (uint)UTable[i].MinLevel)
                uLimit = i;
        // As the source, the infantry limit is read from the unit table.
        for (int i = 0; i < ITable.Length; i++)
            if ((uint)BuildLevel >= (uint)UTable[i].MinLevel)
                iLimit = i;

        var numUnits = new int[UTable.Length];
        int totUnits = (MPlayerUnitCount * 2) / 3;
        for (int i = 0, j = 0; i < totUnits; i++)
        {
            numUnits[j]++;
            if (++j > uLimit)
                j = 0;
        }

        var numInfantry = new int[ITable.Length];
        int totInfantry = MPlayerUnitCount - totUnits;
        for (int i = 0, j = 0; i < totInfantry; i++)
        {
            numInfantry[j]++;
            if (++j > iLimit)
                j = 0;
        }

        var waypts = new int[26];
        int numWaypts = 0;
        for (int i = 0; i < 26; i++)
            if (Waypoint[i] != -1)
                waypts[numWaypts++] = Waypoint[i];

        for (var h = HousesType.HOUSE_MULTI1; h < HousesType.HOUSE_MULTI1 + MPlayerMax; h++)
        {
            var hptr = HouseOrNull(h);
            if (hptr == null)
                continue;

            // (StartLocationOverride of -1 reads before the array in the source; the first waypoint here.)
            int centroid = waypts[Math.Max(hptr.StartLocationOverride, 0)];
            int scaleval = 1;
            TechnoClass? obj;

            if (MPlayerBases)
            {
                // Both humans and the computer get an MCV (the remaster moved this out of the human branch).
                Reserve_Unit();
                obj = UnitClass.Create(this, UnitType.UNIT_MCV, h);
                if (obj != null && !obj.Unlimbo(TdMath.CellCoord(centroid), (byte)DirType.DIR_N))
                {
                    if (!Scan_Place_Object(obj, centroid))
                    {
                        obj.Delete();
                        obj = null;
                    }
                }
                if (obj != null)
                {
                    hptr.FlagHome = 0;
                    hptr.FlagLocation = 0;
                    if (Special.IsCaptureTheFlag)
                        hptr.Flag_Attach((UnitClass)obj, true);
                }
            }
            else if (Special.IsCaptureTheFlag)
            {
                Reserve_Unit();
                obj = UnitClass.Create(this, UnitType.UNIT_MHQ, h);
                obj?.Unlimbo(TdMath.CellCoord(centroid), (byte)DirType.DIR_N);
                hptr.FlagHome = 0;
                hptr.FlagLocation = 0;
            }

            hptr.MaxUnit = MPlayerUnitCount * scaleval;

            bool gdi = hptr.ActLike == HousesType.HOUSE_GOOD;
            for (int i = 0; i <= uLimit; i++)
            {
                int centerpt = Clip_Scatter(centroid, 4);
                for (int j = 0; j < numUnits[i] * scaleval; j++)
                {
                    int n = gdi ? UTable[i].GDICount : UTable[i].NODCount;
                    var type = gdi ? UTable[i].GDIType : UTable[i].NODType;
                    for (int k = 0; k < n; k++)
                    {
                        Reserve_Unit();
                        var unit = UnitClass.Create(this, type, h);
                        if (unit == null)
                            continue;
                        if (!Scan_Place_Object(unit, centerpt))
                            unit.Delete();
                        else if (!hptr.IsHuman)
                            unit.Set_Mission(MissionType.MISSION_GUARD_AREA);
                    }
                }
            }

            for (int i = 0; i <= iLimit; i++)
            {
                int centerpt = Clip_Scatter(centroid, 4);
                for (int j = 0; j < numInfantry[i] * scaleval; j++)
                {
                    int n = gdi ? ITable[i].GDICount : ITable[i].NODCount;
                    var type = gdi ? ITable[i].GDIType : ITable[i].NODType;
                    for (int k = 0; k < n; k++)
                    {
                        Reserve_Infantry();
                        var inf = InfantryClass.Create(this, type, h);
                        if (inf == null)
                            continue;
                        if (!Scan_Place_Object(inf, centerpt))
                            inf.Delete();
                        else if (!hptr.IsHuman)
                            inf.Set_Mission(MissionType.MISSION_GUARD_AREA);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Start a skirmish on a multiplayer map (SCM01EA...): the players were given with
    /// Set_Multiplayer_Data. The seed drives the house and start-location draw.
    /// </summary>
    public bool Start_Skirmish(string root, string iniText, byte[]? bin, uint seed)
    {
        ScenarioName = root;
        Set_Scenario_From_Name(root);
        GameToPlay = GameType.GAME_GLYPHX_MULTIPLAYER;
        ScenPlayer = ScenarioPlayerType.SCEN_PLAYER_MPLAYER;
        skirmishSeed = seed;
        Random.RandNumb = seed;
        return Read_Scenario(root, iniText, bin);
    }

    uint skirmishSeed;
}

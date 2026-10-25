using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Scenario;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic;

/// <summary>What the logic asks of the art side (the template icon maps that Read_Binary checks).</summary>
public interface ITdArt
{
    sbyte[]? Template_Icon_Map(TemplateTypeClass? template);
}

/// <summary>
/// The front end's side of the game: sounds, speech, messages, the sidebar, screen effects. Every
/// method has a do-nothing default, so the logic runs headless; a client overrides what it shows.
/// </summary>
public interface ITdHooks
{
    void Speak(VoxType voice, HouseClass? house, uint coord) { }
    void Sound_Effect(VocType voc, uint coord, int variation) { }
    void Add_Message(string text, PlayerColorType color, int timeout) { }
    void On_Message(int text, float timeout) { }
    void On_Defeated_Message(string text) { }
    void On_Achievement_Event(HouseClass? house, string type, string reason) { }
    void On_Ping(HouseClass house, uint coord) { }
    void Shake_The_Screen(int shakes, HousesType house) { }
    void Fade_Palette_To_White(int delay) { }
    void Fade_Palette_To_Game(int delay) { }
    void Sidebar_Add(RTTIType type, int id, HouseClass? house, bool viaCapture) { }
    void Sidebar_Recalc() { }
    void Sidebar_Sell_Mode_Off() { }
    void Sidebar_Factory_Link(int factory, RTTIType type, int id, HouseClass house) { }
    void Sidebar_Abandon_Production(RTTIType type, int factory, HouseClass house) { }
    void Recalculate_Placement_Distances() { }
    void Clear_Pending_Placement() { }
}

sealed class NullHooks : ITdHooks { }

/// <summary>The keyboard modifiers and mouse position the logic reads (the original's Keyboard/Get_Mouse_X).</summary>
public sealed class TdInput
{
    public int MouseX, MouseY;
    public bool Alt, Ctrl, Shift;
}

/// <summary>OptionsClass (OPTIONS.CPP): the game options the logic reads.</summary>
public sealed class TdOptions
{
    public int GameSpeed = (int)(TdDefines.TIMER_SECOND / TdDefines.TICKS_PER_SECOND);
    public bool IsDeathAnnounce;   // true only in the German and French builds

    static readonly int[,] Adjust =
    {
        { 2, 2, 1, 1, 1, 1, 1, 1 },
        { 3, 3, 3, 2, 2, 2, 1, 1 },
        { 5, 4, 4, 3, 3, 2, 2, 1 },
        { 7, 6, 5, 4, 4, 4, 3, 2 },
    };

    /// <summary>Normalize_Delay: a delay in frames adjusted for the game speed.</summary>
    public int Normalize_Delay(int delay)
    {
        if (delay != 0)
        {
            if (delay < 5)
                delay = Adjust[delay - 1, GameSpeed];
            else
                delay = (delay * 8) / (GameSpeed + 1);
        }
        return delay;
    }
}

/// <summary>
/// The radar's on/off state (RadarClass::Radar_Activate). Without a sidebar showing, it switches
/// at once (controls 2 and 3), as the original does when Map.IsSidebarActive is false.
/// </summary>
public sealed class TdRadarState
{
    public bool IsSidebarActive;
    public bool IsRadarActive, IsRadarActivating, IsRadarDeactivating, DoesRadarExist;
    public int RadarAnimFrame;
    public const int RADAR_ACTIVATED_FRAME = 22;
    public const int MAX_RADAR_FRAMES = 41;

    public bool Is_Radar_Active() => IsRadarActive;
    public bool Is_Radar_Activating() => IsRadarActivating;
    public bool Is_Radar_Existing() => DoesRadarExist;

    public bool Radar_Activate(int control)
    {
        bool old = IsRadarActive;
        switch (control)
        {
            case -1:
                Radar_Activate(IsRadarActive == false ? 1 : 0);
                break;
            case 0:
                if (IsSidebarActive)
                {
                    if (IsRadarActive && !IsRadarDeactivating)
                    {
                        IsRadarDeactivating = true;
                        IsRadarActive = false;
                        if (IsRadarActivating)
                            IsRadarActivating = false;
                        else
                            RadarAnimFrame = RADAR_ACTIVATED_FRAME;
                    }
                }
                else
                {
                    Radar_Activate(2);
                }
                return old;
            case 1:
                if (IsSidebarActive)
                {
                    if (!IsRadarActivating && !IsRadarActive)
                    {
                        IsRadarActivating = true;
                        if (IsRadarDeactivating)
                            IsRadarDeactivating = false;
                        else
                            RadarAnimFrame = DoesRadarExist ? MAX_RADAR_FRAMES : 0;
                    }
                }
                else
                {
                    Radar_Activate(3);
                }
                return old;
            case 2:
                IsRadarActive = false;
                IsRadarActivating = false;
                IsRadarDeactivating = false;
                break;
            case 3:
                IsRadarActive = true;
                IsRadarActivating = false;
                IsRadarDeactivating = false;
                break;
            case 4:
                IsRadarActive = false;
                IsRadarActivating = false;
                IsRadarDeactivating = false;
                DoesRadarExist = false;
                break;
        }
        return old;
    }
}

/// <summary>The remaster's per-house selection lists (SelectedObjectsType / CurrentObject).</summary>
public sealed class SelectedObjects
{
    readonly List<ObjectClass>[] lists = Enumerable.Range(0, (int)HousesType.HOUSE_COUNT).Select(_ => new List<ObjectClass>()).ToArray();

    List<ObjectClass> L(HousesType house) => lists[(int)house];

    public IReadOnlyList<ObjectClass> Of(HousesType house) => L(house);
    public int Count(HousesType house) => L(house).Count;
    public void Add(HousesType house, ObjectClass obj) => L(house).Add(obj);
    public void Add_Head(HousesType house, ObjectClass obj) => L(house).Insert(0, obj);
    public void Delete(HousesType house, ObjectClass obj) => L(house).Remove(obj);

    public void Delete_All(ObjectClass obj)
    {
        foreach (var l in lists)
            l.Remove(obj);
    }

    public void Delete_All_Except(ObjectClass obj, HousesType house)
    {
        for (int i = 0; i < lists.Length; i++)
            if (i != (int)house)
                lists[i].Remove(obj);
    }

    public void Clear()
    {
        foreach (var l in lists)
            l.Clear();
    }
}

/// <summary>LogicClass (LOGIC.CPP): the list of objects that think each frame.</summary>
public sealed class LogicClass(TdGame game) : LayerClass
{
    /// <summary>AI: crates, teams, every object, the scan bits, the map, factories, then the houses.</summary>
    public void AI()
    {
        int index;

        // Crate regeneration.
        if (game.GameToPlay != GameType.GAME_NORMAL && game.CrateMaker && game.CrateTimer.Expired)
        {
            game.Map.Place_Random_Crate();
            game.CrateTimer.Set(TdDefines.TICKS_PER_MINUTE * game.Random_Pick(7, 15));
        }

        for (index = 0; index < game.Teams.Count; index++)
            game.Teams.Ptr(index).AI();

        // Every sentient object; one destroyed during its AI must not make the loop skip another.
        for (index = 0; index < Count; index++)
        {
            ObjectClass obj = this[index];
            int count = Count;
            obj.AI();
            int countDiff = Count - count;
            if (countDiff < 0)
                index += countDiff;
        }

        // A second pass so the owner houses get their scan bits.
        for (index = 0; index < game.Units.Count; index++)
        {
            var unit = game.Units.Ptr(index);
            if (unit.IsLocked && (game.GameToPlay != GameType.GAME_NORMAL || !unit.House.IsHuman || unit.IsDiscoveredByPlayer))
            {
                unit.House.NewUScan |= 1L << (int)unit.Class.Type;
                if (!unit.IsInLimbo)
                    unit.House.NewActiveUScan |= 1L << (int)unit.Class.Type;
            }
        }
        for (index = 0; index < game.Infantry.Count; index++)
        {
            var infantry = game.Infantry.Ptr(index);
            if (infantry.IsLocked && (game.GameToPlay != GameType.GAME_NORMAL || !infantry.House.IsHuman || infantry.IsDiscoveredByPlayer))
            {
                infantry.House.NewIScan |= 1L << (int)infantry.Class.Type;
                if (!infantry.IsInLimbo)
                    infantry.House.NewActiveIScan |= 1L << (int)infantry.Class.Type;
            }
        }
        for (index = 0; index < game.Aircraft.Count; index++)
        {
            var aircraft = game.Aircraft.Ptr(index);
            if (aircraft.IsLocked && (game.GameToPlay != GameType.GAME_NORMAL || !aircraft.House.IsHuman || aircraft.IsDiscoveredByPlayer))
            {
                aircraft.House.NewAScan |= 1L << (int)aircraft.Class.Type;
                if (!aircraft.IsInLimbo)
                    aircraft.House.NewActiveAScan |= 1L << (int)aircraft.Class.Type;
            }
        }
        for (index = 0; index < game.Buildings.Count; index++)
        {
            var building = game.Buildings.Ptr(index);
            if (building.IsLocked && (game.GameToPlay != GameType.GAME_NORMAL || !building.House.IsHuman || building.IsDiscoveredByPlayer))
            {
                building.House.NewBScan |= 1L << (int)building.Class.Type;
                if (!building.IsInLimbo)
                    building.House.NewActiveBScan |= 1L << (int)building.Class.Type;
            }
        }

        // The remaster's AI rebuilds every house's scan bits here.
        HouseClass.Recalc_Attributes(game);

        game.Map.Logic();

        for (index = 0; index < game.Factories.Count; index++)
            game.Factories.Ptr(index).AI();

        // As the remaster: in multiplayer only the player houses, Neutral and the dinosaurs think.
        if (game.GameToPlay != GameType.GAME_NORMAL)
        {
            for (var house = HousesType.HOUSE_MULTI1; house < HousesType.HOUSE_COUNT; house++)
            {
                var hptr = game.HouseOrNull(house);
                if (hptr != null && hptr.IsActive)
                    hptr.AI();
            }
            var neutral = game.HouseOrNull(HousesType.HOUSE_NEUTRAL);
            if (neutral != null && neutral.IsActive)
                neutral.AI();
            var jp = game.HouseOrNull(HousesType.HOUSE_JP);
            if (jp != null && jp.IsActive)
                jp.AI();
        }
        else
        {
            for (var house = HousesType.HOUSE_FIRST; house < HousesType.HOUSE_COUNT; house++)
            {
                var hptr = game.HouseOrNull(house);
                if (hptr != null && hptr.IsActive)
                    hptr.AI();
            }
        }
    }

    public void Clear_Recently_Created_Bits()
    {
        for (int index = 0; index < Count; index++)
            this[index].IsRecentlyCreated = false;
    }
}

/// <summary>
/// One running Tiberian Dawn game: everything the original kept in globals (the object heaps, the
/// map, the houses, the scenario state, the random generators), with the global functions that
/// worked on them.
/// </summary>
public sealed partial class TdGame
{
    public static readonly string[] SourceName =
        ["North", "East", "South", "West", "Shipping", "Beach", "Air", "Visible", "EnemyBase", "HomeBase", "Ocean"];

    public readonly TdTypes Types;
    public readonly RulesClass Rule = new();
    public readonly TdOptions Options = new();
    public SpecialOptions Special => Types.Special;
    public ITdArt? Art;
    public ITdHooks Hooks = new NullHooks();

    // The object heaps (INIT.CPP's Set_Heap sizes).
    public readonly TdHeap<UnitClass> Units = new((int)TdDefines.UNIT_MAX);
    public readonly TdHeap<FactoryClass> Factories = new((int)TdDefines.FACTORY_MAX);
    public readonly TdHeap<TerrainClass> Terrains = new((int)TdDefines.TERRAIN_MAX);
    public readonly TdHeap<TemplateClass> Templates = new((int)TdDefines.TEMPLATE_MAX);
    public readonly TdHeap<SmudgeClass> Smudges = new((int)TdDefines.SMUDGE_MAX);
    public readonly TdHeap<OverlayClass> Overlays = new((int)TdDefines.OVERLAY_MAX);
    public readonly TdHeap<InfantryClass> Infantry = new((int)TdDefines.INFANTRY_MAX);
    public readonly TdHeap<BulletClass> Bullets = new((int)TdDefines.BULLET_MAX);
    public readonly TdHeap<BuildingClass> Buildings = new((int)TdDefines.BUILDING_MAX);
    public readonly TdHeap<AnimClass> Anims = new((int)TdDefines.ANIM_MAX);
    public readonly TdHeap<AircraftClass> Aircraft = new((int)TdDefines.AIRCRAFT_MAX);
    public readonly TdHeap<TriggerClass> Triggers = new((int)TdDefines.TRIGGER_MAX);
    public readonly TdHeap<TeamTypeClass> TeamTypes = new((int)TdDefines.TEAMTYPE_MAX);
    public readonly TdHeap<TeamClass> Teams = new((int)TdDefines.TEAM_MAX);
    public readonly TdHeap<HouseClass> Houses = new((int)TdDefines.HOUSE_MAX);

    public readonly TdMap Map;
    public readonly LogicClass Logic;
    public readonly BaseClass Base;
    public readonly PathFinderState PathFinder = new();
    public readonly TdRandom Random = new();
    public readonly TdRadarState RadarState = new();
    public readonly TdInput Input = new();
    public readonly SelectedObjects CurrentObject = new();

    public readonly CountDownTimer CrateTimer;

    // Scenario state.
    public long Frame;
    public int ScenarioInit;
    public bool GameActive = true;   // set in Init_Game, before any scenario loads
    public HouseClass PlayerPtr = null!;
    public GameType GameToPlay = GameType.GAME_NORMAL;
    public int BuildLevel = 10;
    public int Scenario = 1;
    public ScenarioPlayerType ScenPlayer = ScenarioPlayerType.SCEN_PLAYER_GDI;
    public ScenarioDirType ScenDir = ScenarioDirType.SCEN_DIR_EAST;
    public ScenarioVarType ScenVar = ScenarioVarType.SCEN_VAR_A;
    public readonly int[] Waypoint = Enumerable.Repeat(-1, (int)WaypointType.WAYPT_COUNT).ToArray();
    public readonly TriggerClass?[] CellTriggers = new TriggerClass?[TdDefines.MAP_CELL_TOTAL];
    public readonly List<TriggerClass>[] HouseTriggers = Enumerable.Range(0, (int)HousesType.HOUSE_COUNT).Select(_ => new List<TriggerClass>()).ToArray();
    public readonly int[] TeamNumber = new int[TdDefines.TEAMTYPE_MAX];
    public bool PlayerWins, PlayerLoses, PlayerRestarts;
    public int EndCountDown;   // frames until loss checks start (Clear_Scenario sets 30 seconds)
    public int CrateCount;
    public bool CrateMaker;
    public bool SpecialFlag;
    public bool TempleIoned;
    public StructType SabotagedType = StructType.STRUCT_NONE;
    public HousesType SmudgeToOwn = HousesType.HOUSE_NONE, OverlayToOwn = HousesType.HOUSE_NONE;
    public byte[]? FadingLight;
    public bool IsTargettingMode;
    public ObjectClass? PendingObject;
    public int LParam;
    public bool AllowVoice = true;

    // Multiplayer settings.
    public int MPlayerMax = 6, MPlayerCount, MPlayerUnitCount = 10, MPlayerTiberium, MPlayerGoodies;
    public bool MPlayerBases = true, MPlayerBlitz, MPlayerGhosts;
    public readonly HousesType[] MPlayerHouses = new HousesType[TdDefines.MAX_PLAYERS];

    // Debug and version flags.
    public bool DebugMap, DebugFlag, DebugCheat, DebugUnshroud, DebugInstantBuild;
    public bool IsV107 = true;
    public bool AreThingiesEnabled;
    public bool ShareAllyVisibility = true;

    public TdGame(TdTypes types)
    {
        Types = types;
        Map = new TdMap(this);
        Logic = new LogicClass(this);
        Base = new BaseClass(this);
        CrateTimer = new CountDownTimer(this);
    }

    bool firstUpdate = true;

    /// <summary>One game frame, as the remaster's CNC_Advance_Instance runs it: each skirmish
    /// player's sidebar sends out finished units, the ground layer is sorted, every object thinks
    /// (not on a skirmish's very first frame), player commands run, the sidebars are rechecked,
    /// the end-game grace period counts down, and the frame advances. Returns true when the game
    /// has ended. Win and loss are left in PlayerWins/PlayerLoses for the front end.</summary>
    public bool Main_Loop()
    {
        bool multiplayer = GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER;
        if (multiplayer)
        {
            var oldPlayer = PlayerPtr;
            for (int i = 0; i < MPlayerCount; i++)
                if (HouseOrNull(MPlayerHouses[i]) is { } player)
                {
                    Logic_Switch_Player_Context(player);
                    SidebarFor(player).AI();
                }
            PlayerPtr = oldPlayer;
        }
        else if (PlayerPtr != null)
        {
            SidebarFor(PlayerPtr).AI();
        }

        Map.Layer[(int)LayerType.LAYER_GROUND].Sort();
        if (!(firstUpdate && multiplayer))
        {
            var oldPlayer = PlayerPtr;
            Logic.Clear_Recently_Created_Bits();
            Logic.AI();
            PlayerPtr = oldPlayer;
        }
        firstUpdate = false;

        Queue_AI();
        if (multiplayer)
            for (int i = 0; i < MPlayerCount; i++)
                if (HouseOrNull(MPlayerHouses[i]) is { } player)
                    SidebarFor(player).Recalc();

        if (EndCountDown != 0)
            EndCountDown--;
        Frame++;
        return !GameActive;
    }

    // ---- The houses ----

    /// <summary>HouseClass::As_Pointer: the active house of that type, searched by type.</summary>
    public HouseClass? HouseOrNull(HousesType house)
    {
        for (int index = 0; index < Houses.Count; index++)
            if (Houses.Ptr(index).Class.House == house)
                return Houses.Ptr(index);
        return null;
    }

    public HouseClass? HouseOrNull(int house) => HouseOrNull((HousesType)house);

    /// <summary>As_Pointer where the original dereferences the result without a check.</summary>
    public HouseClass HouseFor(HousesType house) => HouseOrNull(house)!;

    // ---- Random numbers (FUNCTION.H's Random_Pick and Sim_Random_Pick) ----

    public int IRandom(int minval, int maxval) => Random.IRandom(minval, maxval);
    public int Random_Pick(int a, int b) => Random.IRandom(a, b);
    public int Sim_Random_Pick(int a, int b) => Random.Sim_IRandom(a, b);

    /// <summary>Coord_Scatter: a coordinate moved a distance in a random direction (unchanged if that
    /// leaves the map), optionally snapped to the cell centre.</summary>
    public uint Coord_Scatter(uint coord, int distance, bool lockToCell = false)
    {
        uint newcoord = TdMath.CoordMove(coord, (byte)Random_Pick((int)DirType.DIR_N, (int)DirType.DIR_MAX), distance);
        if ((newcoord & 0xC000C000u) != 0)
            newcoord = coord;
        if (lockToCell)
            newcoord = TdMath.CoordSnap(newcoord);
        return newcoord;
    }

    // ---- TARGET.CPP ----

    public TriggerClass? As_Trigger(uint target, bool checkActive = true)
    {
        var trigger = Target.Is_Target_Trigger(target) ? Triggers.RawPtr(Target.Target_Value(target)) : null;
        return checkActive && trigger != null && !trigger.IsActive ? null : trigger;
    }

    public TeamClass? As_Team(uint target, bool checkActive = true)
    {
        var team = Target.Is_Target_Team(target) ? Teams.RawPtr(Target.Target_Value(target)) : null;
        return checkActive && team != null && !team.IsActive ? null : team;
    }

    public TeamTypeClass? As_TeamType(uint target) =>
        Target.Is_Target_TeamType(target) ? TeamTypes.RawPtr(Target.Target_Value(target)) : null;

    public AnimClass? As_Animation(uint target, bool checkActive = true)
    {
        var anim = Target.Is_Target_Animation(target) ? Anims.RawPtr(Target.Target_Value(target)) : null;
        return checkActive && anim != null && !anim.IsActive ? null : anim;
    }

    public BulletClass? As_Bullet(uint target, bool checkActive = true)
    {
        var bullet = Target.Is_Target_Bullet(target) ? Bullets.RawPtr(Target.Target_Value(target)) : null;
        return checkActive && bullet != null && !bullet.IsActive ? null : bullet;
    }

    public AircraftClass? As_Aircraft(uint target, bool checkActive = true)
    {
        var aircraft = Target.Is_Target_Aircraft(target) ? Aircraft.RawPtr(Target.Target_Value(target)) : null;
        return checkActive && aircraft != null && !aircraft.IsActive ? null : aircraft;
    }

    public TechnoClass? As_Techno(uint target, bool checkActive = true) =>
        As_Object(target, checkActive) is { } obj && obj.Is_Techno() ? (TechnoClass)obj : null;

    public ObjectClass? As_Object(uint target, bool checkActive = true)
    {
        int val = Target.Target_Value(target);
        ObjectClass? obj = Target.Target_Kind(target) switch
        {
            KindType.KIND_INFANTRY => Infantry.RawPtr(val),
            KindType.KIND_UNIT => Units.RawPtr(val),
            KindType.KIND_BUILDING => Buildings.RawPtr(val),
            KindType.KIND_AIRCRAFT => Aircraft.RawPtr(val),
            KindType.KIND_TERRAIN => Terrains.RawPtr(val),
            KindType.KIND_BULLET => Bullets.RawPtr(val),
            KindType.KIND_ANIMATION => Anims.RawPtr(val),
            _ => null,
        };
        return checkActive && obj != null && !obj.IsActive ? null : obj;
    }

    public UnitClass? As_Unit(uint target, bool checkActive = true)
    {
        var unit = Target.Is_Target_Unit(target) ? Units.RawPtr(Target.Target_Value(target)) : null;
        return checkActive && unit != null && !unit.IsActive ? null : unit;
    }

    public InfantryClass? As_Infantry(uint target, bool checkActive = true)
    {
        var infantry = Target.Is_Target_Infantry(target) ? Infantry.RawPtr(Target.Target_Value(target)) : null;
        return checkActive && infantry != null && !infantry.IsActive ? null : infantry;
    }

    public BuildingClass? As_Building(uint target, bool checkActive = true)
    {
        var building = Target.Is_Target_Building(target) ? Buildings.RawPtr(Target.Target_Value(target)) : null;
        return checkActive && building != null && !building.IsActive ? null : building;
    }

    /// <summary>Target_Legal: anything but TARGET_NONE, unless it names an object that is gone.</summary>
    public bool Target_Legal(uint target)
    {
        if (target == Target.TARGET_NONE)
            return false;
        var obj = As_Object(target, false);
        return obj == null || obj.IsActive;
    }

    public int As_Cell(uint target) => TdMath.CoordCell(As_Coord(target));

    public uint As_Coord(uint target)
    {
        if (Target_Legal(target))
        {
            if (Target.Is_Target_Cell(target))
                return TdMath.CellCoord((short)Target.Target_Value(target));
            var obj = As_Object(target);
            if (obj != null)
                return obj.IsActive ? obj.Target_Coord() : 0;
        }
        return 0;
    }

    public uint As_Movement_Coord(uint target)
    {
        if (Target_Legal(target))
        {
            if (Target.Is_Target_Cell(target))
                return TdMath.CellCoord((short)Target.Target_Value(target));
            var obj = As_Object(target);
            if (obj != null)
                return obj.Docking_Coord();
        }
        return 0;
    }

    /// <summary>ObjectClass::Detach_This_From_All: everything that might refer to the target lets go of it.</summary>
    public void Detach_This_From_All(uint target, bool all = true)
    {
        if (!Target_Legal(target))
            return;
        for (int index = 0; index < Houses.Count; index++)
            Houses.Ptr(index).Detach(target, all);
        for (int index = 0; index < Teams.Count; index++)
            Teams.Ptr(index).Detach(target, all);
        for (int index = 0; index < Units.Count; index++)
            Units.Ptr(index).Detach(target, all);
        for (int index = 0; index < Infantry.Count; index++)
            Infantry.Ptr(index).Detach(target, all);
        for (int index = 0; index < Aircraft.Count; index++)
            Aircraft.Ptr(index).Detach(target, all);
        for (int index = 0; index < Buildings.Count; index++)
            Buildings.Ptr(index).Detach(target, all);
        for (int index = 0; index < Bullets.Count; index++)
            Bullets.Ptr(index).Detach(target, all);
        for (int index = 0; index < Anims.Count; index++)
            Anims.Ptr(index).Detach(target, all);
    }

    // ---- COMBAT.CPP ----

    /// <summary>Modify_Damage: damage scaled by the warhead against the armor, then halved per
    /// (spread-scaled) distance step from the impact.</summary>
    public int Modify_Damage(int damage, WarheadType warhead, ArmorType armor, int distance)
    {
        if (Special.IsInert || damage == 0 || warhead == WarheadType.WARHEAD_NONE)
            return 0;

        var whead = Types.Warhead(warhead);
        damage = (int)TdMath.FixedToCardinal((uint)damage, (uint)whead.Modifier[(int)armor]);

        if (damage != 0)
        {
            distance >>= whead.SpreadFactor;
            distance = Math.Clamp(distance, 0, 16);
            damage >>= distance;
        }
        return damage;
    }

    /// <summary>Explosion_Damage: hurt everything in the cell and the ones around it (up to 32
    /// objects, not the source), then wear down tiberium and walls there.</summary>
    public void Explosion_Damage(uint coord, int strength, TechnoClass? source, WarheadType warhead)
    {
        if (strength == 0 || Special.IsInert || warhead == WarheadType.WARHEAD_NONE)
            return;

        var whead = Types.Warhead(warhead);
        int range = (int)(TdDefines.ICON_LEPTON_W + (TdDefines.ICON_LEPTON_W >> 1));
        int cell = TdMath.CoordCell(coord);
        if ((uint)cell >= TdDefines.MAP_CELL_TOTAL)
            return;

        CellClass? cellptr = Map[cell];
        ObjectClass? impacto = cellptr.Cell_Occupier();

        var objects = new ObjectClass[32];
        int count = 0;
        for (var i = FacingType.FACING_NONE; i < FacingType.FACING_COUNT; i++)
        {
            if (i != FacingType.FACING_NONE)
            {
                cellptr = Map[cell].Adjacent_Cell(i);
                if (cellptr == null)
                    continue;
            }

            // Overlappers are left out: selection can change them, which desyncs multiplayer.
            ObjectClass? obj = cellptr!.Cell_Occupier();
            while (obj != null)
            {
                if (!obj.IsToDamage && obj != source)
                {
                    obj.IsToDamage = true;
                    objects[count++] = obj;
                    if (count >= objects.Length)
                        break;
                }
                obj = obj.Next;
            }
            if (count >= objects.Length)
                break;
        }

        for (int index = 0; index < count; index++)
        {
            var obj = objects[index];
            obj.IsToDamage = false;
            int distance = obj.What_Am_I() == RTTIType.RTTI_BUILDING && impacto == obj ? 0 : TdMath.Distance(coord, obj.Center_Coord());
            if (obj.IsDown && !obj.IsInLimbo && distance < range)
            {
                int damage = strength;
                // High explosive does double damage against aircraft.
                if (warhead == WarheadType.WARHEAD_HE && obj.What_Am_I() == RTTIType.RTTI_AIRCRAFT)
                    damage *= 2;
                if (damage != 0)
                    obj.Take_Damage(ref damage, distance, warhead, source);
            }
        }

        cellptr = Map[cell];
        cellptr.Reduce_Tiberium(strength / 10);
        if (cellptr.Overlay != OverlayType.OVERLAY_NONE)
        {
            var optr = Types.Overlay(cellptr.Overlay);
            if (optr.IsWall && (whead.IsWallDestroyer || (whead.IsWoodDestroyer && optr.IsWooden)))
                Map[cell].Reduce_Wall(strength);
        }
    }

    // ---- Type operations (the ...DATA.CPP Create_One_Of / Create_And_Place / Who_Can_Build_Me) ----

    public TechnoTypeClass? Fetch_Techno_Type(RTTIType type, int id) => type switch
    {
        RTTIType.RTTI_UNITTYPE or RTTIType.RTTI_UNIT => Types.Unit((UnitType)id),
        RTTIType.RTTI_BUILDINGTYPE or RTTIType.RTTI_BUILDING => Types.Building((StructType)id),
        RTTIType.RTTI_INFANTRYTYPE or RTTIType.RTTI_INFANTRY => Types.Infantry((InfantryType)id),
        RTTIType.RTTI_AIRCRAFTTYPE or RTTIType.RTTI_AIRCRAFT => Types.Aircraft((AircraftType)id),
        _ => null,
    };

    /// <summary>Create_One_Of: a new object of the type, in limbo (null when its heap is full).
    /// Buildings with no house go to Neutral; the others need a house, as the original.</summary>
    public ObjectClass? Create_One_Of(ObjectTypeClass type, HouseClass? house) => type switch
    {
        AircraftTypeClass a => AircraftClass.Create(this, a.Type, house!.Class.House),
        BuildingTypeClass b => BuildingClass.Create(this, b.Type, house?.Class.House ?? HousesType.HOUSE_NEUTRAL),
        InfantryTypeClass i => InfantryClass.Create(this, i.Type, house!.Class.House),
        UnitTypeClass u => UnitClass.Create(this, u.Type, house!.Class.House),
        OverlayTypeClass o => OverlayClass.Create(this, o.Type, -1),
        SmudgeTypeClass s => SmudgeClass.Create(this, s.Type),
        TerrainTypeClass t => TerrainClass.Create(this, t.Type, -1),
        TemplateTypeClass tt => TemplateClass.Create(this, tt.Type, -1),
        _ => null,
    };

    /// <summary>Create_And_Place: make one and put it down at the cell.</summary>
    public bool Create_And_Place(ObjectTypeClass type, int cell, HousesType house)
    {
        switch (type)
        {
            case AircraftTypeClass:
                return false;
            case BuildingTypeClass b:
            {
                var ptr = BuildingClass.Create(this, b.Type, house);
                return ptr != null && ptr.Unlimbo(TdMath.CellCoord(cell), (byte)DirType.DIR_N);
            }
            case InfantryTypeClass it:
            {
                var i = InfantryClass.Create(this, it.Type, house);
                if (i != null)
                {
                    uint coord = Map[cell].Closest_Free_Spot(TdMath.CellCoord(cell));
                    if (coord != 0)
                        return i.Unlimbo(coord, (byte)DirType.DIR_E);
                    i.Delete();
                }
                return false;
            }
            case UnitTypeClass ut:
            {
                var unit = UnitClass.Create(this, ut.Type, house);
                return unit != null && unit.Unlimbo(TdMath.CellCoord(cell), (byte)Random_Pick((int)DirType.DIR_N, (int)DirType.DIR_MAX));
            }
            case OverlayTypeClass o:
                return OverlayClass.Create(this, o.Type, cell) != null;
            case SmudgeTypeClass s:
                return SmudgeClass.Create(this, s.Type, TdMath.CellCoord(cell)) != null;
            case TerrainTypeClass t:
                return TerrainClass.Create(this, t.Type, cell) != null;
            case TemplateTypeClass tt:
                return TemplateClass.Create(this, tt.Type, cell) != null;
        }
        return false;
    }

    /// <summary>Who_Can_Build_Me: the house's factory for this kind (its leader if it has one).</summary>
    public BuildingClass? Who_Can_Build_Me(ObjectTypeClass type, bool intheory, bool legal, HousesType house)
    {
        if (type is BuildingTypeClass btype)
            return btype.Who_Can_Build_Me(this, intheory, legal, house);
        if (type is not TechnoTypeClass ttype)
            return null;

        RTTIType toBuild = type.What_Am_I();
        BuildingClass? anybuilding = null;
        for (int index = 0; index < Buildings.Count; index++)
        {
            var building = Buildings.Ptr(index);
            bool ok = !building.IsInLimbo &&
                      building.House.Class.House == house &&
                      building.Mission != MissionType.MISSION_DECONSTRUCTION &&
                      ((1L << (int)building.ActLike) & ttype.Ownable) != 0 &&
                      (!legal || building.House.Can_Build(ttype, building.ActLike)) &&
                      building.Class.ToBuild == toBuild &&
                      // Aircraft ignore the "in theory" test.
                      (toBuild == RTTIType.RTTI_AIRCRAFTTYPE || intheory || !building.In_Radio_Contact());
            if (!ok)
                continue;
            // As the original: infantry note the building before checking for the leader.
            if (toBuild == RTTIType.RTTI_INFANTRYTYPE)
            {
                anybuilding = building;
                if (building.IsLeader)
                    return building;
            }
            else
            {
                if (building.IsLeader)
                    return building;
                anybuilding = building;
            }
        }
        return anybuilding;
    }

    /// <summary>TechnoTypeClass::Time_To_Build: the cost, eased for computer buildings on easier
    /// settings, cheaper for Nod vehicles, slowed for dim AIs, and slowed by low power.</summary>
    public int Time_To_Build(TechnoTypeClass type, HousesType house)
    {
        int time = type.Raw_Cost(Types, Special);
        var hptr = HouseOrNull(house);
        if (hptr == null || !hptr.IsActive)
            return time;

        if (PlayerPtr.Difficulty != DiffType.DIFF_HARD &&
            GameToPlay == GameType.GAME_NORMAL &&
            type.What_Am_I() == RTTIType.RTTI_BUILDINGTYPE &&
            PlayerPtr.Class.House != hptr.Class.House)
            time = (time + (PlayerPtr.Difficulty == DiffType.DIFF_EASY ? 4000 : 2000)) / 2;

        if (type.What_Am_I() == RTTIType.RTTI_UNITTYPE && (type.Ownable & TdDefines.HOUSEF_GOOD) == 0)
            time -= time / 4;

        if (!hptr.IsHuman && Rule.Diff[(int)hptr.Difficulty].IsBuildSlowdown)
            time = (int)((time * Rule.MaxIQ * 2.0f) / (hptr.IQ + Rule.MaxIQ));

        int power = hptr.Power_Fraction();
        int invScale = 256;
        if (power == 0x000)
            invScale = 64;
        else if (power < 0x080)
            invScale = 103;
        else if (power < 0x0100)
            invScale = 171;
        return (time * 256) / invScale;
    }

    public SourceType Source_From_Name(string? name)
    {
        if (name != null)
            for (var source = SourceType.SOURCE_FIRST; source < SourceType.SOURCE_COUNT; source++)
                if (string.Equals(SourceName[(int)source], name, StringComparison.OrdinalIgnoreCase))
                    return source;
        return SourceType.SOURCE_NONE;
    }

    public string Name_From_Source(SourceType source) =>
        source >= SourceType.SOURCE_FIRST && source < SourceType.SOURCE_COUNT ? SourceName[(int)source] : "None";

    // ---- INI.CPP's object placement ----

    /// <summary>Scan_Place_Object: put the object in the cell, else the nearest free spot out to 31
    /// cells (two passes per distance, the second scattered so units don't line up on spokes).</summary>
    public bool Scan_Place_Object(ObjectClass obj, int cell)
    {
        TechnoClass? techno;
        if (Map.In_Radar(cell))
        {
            techno = Map[cell].Cell_Techno();
            if (techno == null || (techno.What_Am_I() == RTTIType.RTTI_INFANTRY && obj.What_Am_I() == RTTIType.RTTI_INFANTRY))
                if (obj.Unlimbo(TdMath.CellCoord(cell), (byte)DirType.DIR_N))
                    return true;
        }

        for (int dist = 1; dist < 32; dist++)
        {
            var rot = (FacingType)IRandom((int)FacingType.FACING_N, (int)FacingType.FACING_NW);
            for (int tryval = 0; tryval < 2; tryval++)
            {
                for (var fcounter = FacingType.FACING_N; fcounter <= FacingType.FACING_NW; fcounter++)
                {
                    int newcell = Clip_Move(cell, rot, dist);
                    if (tryval > 0)
                        newcell = Clip_Scatter(newcell, 1);

                    if (newcell != cell)
                    {
                        techno = Map[newcell].Cell_Techno();
                        if (techno == null || (techno.What_Am_I() == RTTIType.RTTI_INFANTRY && obj.What_Am_I() == RTTIType.RTTI_INFANTRY))
                            if (obj.Unlimbo(TdMath.CellCoord(newcell), (byte)DirType.DIR_N))
                                return true;
                    }

                    rot++;
                    if (rot > FacingType.FACING_NW)
                        rot = FacingType.FACING_N;
                }
            }
        }
        return false;
    }

    int Clip_Scatter(int cell, int maxdist)
    {
        int x = TdMath.CellX(cell), y = TdMath.CellY(cell);
        int xmin = Map.MapCellX, xmax = xmin + Map.MapCellWidth - 1;
        int ymin = Map.MapCellY, ymax = ymin + Map.MapCellHeight - 1;

        int xdist = IRandom(0, maxdist);
        if (IRandom(0, 1) == 0)
        {
            x += xdist;
            if (x > xmax)
                x = xmax;
        }
        else
        {
            x -= xdist;
            if (x < xmin)
                x = xmin;
        }

        int ydist = IRandom(0, maxdist);
        if (IRandom(0, 1) == 0)
        {
            y += ydist;
            if (y > ymax)
                y = ymax;
        }
        else
        {
            y -= ydist;
            if (y < ymin)
                y = ymin;
        }
        return TdMath.XyCell(x, y);
    }

    int Clip_Move(int cell, FacingType facing, int dist)
    {
        int x = TdMath.CellX(cell), y = TdMath.CellY(cell);
        int xmin = Map.MapCellX, xmax = xmin + Map.MapCellWidth - 1;
        int ymin = Map.MapCellY, ymax = ymin + Map.MapCellHeight - 1;
        switch (facing)
        {
            case FacingType.FACING_N: y -= dist; break;
            case FacingType.FACING_NE: x += dist; y -= dist; break;
            case FacingType.FACING_E: x += dist; break;
            case FacingType.FACING_SE: x += dist; y += dist; break;
            case FacingType.FACING_S: y += dist; break;
            case FacingType.FACING_SW: x -= dist; y += dist; break;
            case FacingType.FACING_W: x -= dist; break;
            case FacingType.FACING_NW: x -= dist; y -= dist; break;
        }
        if (x > xmax) x = xmax;
        if (x < xmin) x = xmin;
        if (y > ymax) y = ymax;
        if (y < ymin) y = ymin;
        return TdMath.XyCell(x, y);
    }

    /// <summary>Add_CRC (QUEUE.CPP): rotate left one bit, add the value.</summary>
    public static void Add_CRC(ref uint crc, uint val)
    {
        uint hibit = (crc & 0x80000000) != 0 ? 1u : 0u;
        crc <<= 1;
        crc += val;
        crc += hibit;
    }

    // ---- Selection and commands ----

    public void Unselect_All()
    {
        var list = CurrentObject.Of(PlayerPtr.Class.House);
        while (list.Count > 0)
        {
            int before = list.Count;
            list[0].Unselect();
            if (before <= list.Count)
                CurrentObject.Delete(PlayerPtr.Class.House, list[0]);
        }
    }

    /// <summary>Queue_Mission: a mission order goes out as an event (false when the queue is full).</summary>
    public bool Queue_Mission(uint whom, MissionType mission, uint target, uint destination)
    {
        return OutList.Add(EventClass.MegaMission(this, whom, mission, target, destination));
    }

    /// <summary>The GlyphX multiplayer build switches the player to whichever house is thinking.</summary>
    public void Logic_Switch_Player_Context(HouseClass house)
    {
        if (GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER && house.IsHuman)
            PlayerPtr = house;
    }

    // ---- Front-end hooks ----

    public void Speak(VoxType voice, HouseClass? house = null, uint coord = 0)
    {
        if (AllowVoice)
            Hooks.Speak(voice, house, coord);
    }

    public void Sound_Effect(VocType voc, uint coord = 0, int variation = 1) => Hooks.Sound_Effect(voc, coord, variation);
    public void Add_Message(string text, PlayerColorType color, int timeout) => Hooks.Add_Message(text, color, timeout);
    public void On_Message(int text, float timeout) => Hooks.On_Message(text, timeout);
    public void On_Defeated_Message(string text) => Hooks.On_Defeated_Message(text);
    public void On_Achievement_Event(HouseClass? house, string type, string reason) => Hooks.On_Achievement_Event(house, type, reason);
    public void On_Ping(HouseClass house, uint coord) => Hooks.On_Ping(house, coord);
    public void Shake_The_Screen(int shakes, HousesType house = HousesType.HOUSE_NONE) => Hooks.Shake_The_Screen(shakes, house);
    public void Fade_Palette_To_White(int delay) => Hooks.Fade_Palette_To_White(delay);
    public void Fade_Palette_To_Game(int delay) => Hooks.Fade_Palette_To_Game(delay);
}

using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic;

// The player's side of the remaster (DLLInterface.cpp's sidebar requests and DISPLAY.CPP's
// Selection_At_Mouse / Command_Object): what a front end calls when the player clicks.
public sealed partial class TdGame
{
    readonly Dictionary<HouseClass, TdSidebar> sidebars = new();

    /// <summary>The house's sidebar (the remaster keeps one per player).</summary>
    public TdSidebar SidebarFor(HouseClass house)
    {
        if (!sidebars.TryGetValue(house, out var sidebar))
            sidebars[house] = sidebar = new TdSidebar(this, house);
        return sidebar;
    }

    // The building being placed and where it may go (DLLExportClass::PlacementType/PlacementDistance).
    public BuildingTypeClass? PlacementType;
    readonly byte[] placementDistance = new byte[TdDefines.MAP_CELL_TOTAL];

    // ---- Sidebar hooks from the logic ----

    public void Sidebar_Add(RTTIType type, int id, HouseClass? house, bool viaCapture)
    {
        house ??= PlayerPtr;
        if (house != null && SidebarFor(house).Add(type, id, viaCapture))
            Hooks.Sidebar_Add(type, id, house, viaCapture);
    }

    /// <summary>SidebarClass::Recalc (campaign only; in skirmish each player's sidebar is
    /// recalculated every frame).</summary>
    public void Sidebar_Recalc()
    {
        if (GameToPlay != GameType.GAME_GLYPHX_MULTIPLAYER && PlayerPtr != null)
            SidebarFor(PlayerPtr).Recalc();
        Hooks.Sidebar_Recalc();
    }

    public void Sidebar_Sell_Mode_Off() => Hooks.Sidebar_Sell_Mode_Off();

    public void Sidebar_Factory_Link(int factory, RTTIType type, int id, HouseClass house)
    {
        SidebarFor(house).Factory_Link(factory, type, id);
        Hooks.Sidebar_Factory_Link(factory, type, id, house);
    }

    public void Sidebar_Abandon_Production(RTTIType type, int factory, HouseClass house)
    {
        SidebarFor(house).Abandon_Production(type, factory);
        Hooks.Sidebar_Abandon_Production(type, factory, house);
    }

    public void Recalculate_Placement_Distances()
    {
        if (PlacementType != null)
            Calculate_Placement_Distances();
        Hooks.Recalculate_Placement_Distances();
    }

    public void Clear_Pending_Placement()
    {
        PlacementType = null;
        PendingObject = null;
        Hooks.Clear_Pending_Placement();
    }

    // ---- Construction (DLLExportClass::MP_Construction_Action) ----

    public enum SidebarRequest { StartConstruction, HoldConstruction, CancelConstruction, StartPlacement }

    public bool Start_Construction(RTTIType type, int id) => Construction_Action(SidebarRequest.StartConstruction, type, id);
    public bool Hold_Construction(RTTIType type, int id) => Construction_Action(SidebarRequest.HoldConstruction, type, id);

    public bool Cancel_Construction(RTTIType type, int id) =>
        Cancel_Placement() && Construction_Action(SidebarRequest.CancelConstruction, type, id);

    bool Construction_Action(SidebarRequest action, RTTIType type, int id)
    {
        var sidebar = SidebarFor(PlayerPtr);
        var b = sidebar.Find(type, id);
        if (b == null)
            return false;

        int genfactory = type switch
        {
            RTTIType.RTTI_INFANTRYTYPE => PlayerPtr.InfantryFactory,
            RTTIType.RTTI_UNITTYPE => PlayerPtr.UnitFactory,
            RTTIType.RTTI_AIRCRAFTTYPE => PlayerPtr.AircraftFactory,
            RTTIType.RTTI_BUILDINGTYPE => PlayerPtr.BuildingFactory,
            _ => -1,
        };
        int fnumber = b.Factory;
        bool special = type == RTTIType.RTTI_SPECIAL;
        var choice = special ? null : Fetch_Techno_Type(type, id);
        var factory = fnumber != -1 ? Factories.RawPtr(fnumber) : null;

        if (special || choice == null)
            return false;

        // The factory of this kind is busy with something else.
        if (fnumber == -1 && genfactory != -1)
        {
            Speak(VoxType.VOX_NO_FACTORY);
            return false;
        }

        if (factory != null)
        {
            switch (action)
            {
                case SidebarRequest.CancelConstruction:
                    Speak(VoxType.VOX_CANCELED);
                    OutList.Add(EventClass.Specific(this, EventClass.EventType.ABANDON, type, id));
                    break;
                case SidebarRequest.HoldConstruction:
                    if (factory.Is_Building())
                    {
                        Speak(VoxType.VOX_SUSPENDED);
                        OutList.Add(EventClass.Specific(this, EventClass.EventType.SUSPEND, type, id));
                    }
                    break;
                default:
                    if (factory.Is_Building())
                    {
                        Speak(VoxType.VOX_NO_FACTORY);
                        return false;
                    }
                    if (factory.Has_Completed())
                    {
                        var pending = factory.Get_Object();
                        if (pending != null)
                        {
                            var builder = Who_Can_Build_Me(pending.Techno_Type_Class(), false, false, pending.House.Class.House);
                            if (builder == null)
                            {
                                Speak(VoxType.VOX_NO_FACTORY);
                                OutList.Add(EventClass.Specific(this, EventClass.EventType.ABANDON, type, id));
                            }
                            else if (pending is BuildingClass && action == SidebarRequest.StartPlacement)
                            {
                                Unselect_All();
                            }
                        }
                    }
                    else if (action == SidebarRequest.StartConstruction)
                    {
                        // Suspended production resumes.
                        Speak(VoxType.VOX_BUILDING);
                        OutList.Add(EventClass.Specific(this, EventClass.EventType.PRODUCE, type, id));
                        return true;
                    }
                    break;
            }
        }
        else if (action is SidebarRequest.StartConstruction or SidebarRequest.StartPlacement)
        {
            Speak(VoxType.VOX_BUILDING);
            OutList.Add(EventClass.Specific(this, EventClass.EventType.PRODUCE, type, id));
            Queue_AI();
            return true;
        }
        return true;
    }

    /// <summary>The finished building waiting in the player's construction yard, if any.</summary>
    public BuildingClass? Pending_Building()
    {
        var b = SidebarFor(PlayerPtr).Column[0].Buildables.FirstOrDefault(x => x.Factory != -1 && Factories.RawPtr(x.Factory) is { } f && f.Has_Completed());
        return b != null ? Factories.RawPtr(b.Factory)!.Get_Object() as BuildingClass : null;
    }

    /// <summary>Start_Placement: the finished building follows the cursor until placed.</summary>
    public bool Start_Placement(RTTIType type, int id)
    {
        var building = Pending_Building();
        if (building != null && Fetch_Techno_Type(type, id) is BuildingTypeClass btype && building.Class == btype)
        {
            PlacementType = btype;
            PendingObject = building;
            Recalculate_Placement_Distances();
            return Construction_Action(SidebarRequest.StartPlacement, type, id);
        }
        return true;
    }

    public bool Cancel_Placement()
    {
        PlacementType = null;
        PendingObject = null;
        return true;
    }

    /// <summary>Place: put the pending building down with its top-left at the cell.</summary>
    public bool Place(int cell)
    {
        var building = Pending_Building();
        if (building != null && PlacementType != null)
        {
            if (PlayerPtr.Place_Object(building.What_Am_I(), cell))
            {
                PlacementType = null;
                PendingObject = null;
            }
            return true;
        }
        return false;
    }

    /// <summary>Calculate_Placement_Distances: cells next to the player's buildings, bibs and walls
    /// (0 = owned, 1 = adjacent).</summary>
    void Calculate_Placement_Distances()
    {
        int mx = Map.MapCellX, my = Map.MapCellY, mw = Map.MapCellWidth, mh = Map.MapCellHeight;
        if (mx > 0) { mx--; mw++; }
        if (mw < TdDefines.MAP_CELL_W) mw++;
        if (my > 0) { my--; mh++; }
        if (mh < TdDefines.MAP_CELL_H) mh++;

        Array.Fill(placementDistance, (byte)255);
        for (int y = 0; y < mh; y++)
        {
            for (int x = 0; x < mw; x++)
            {
                int cell = TdMath.XyCell(mx + x, my + y);
                var b = Map[cell].Cell_Building();
                if ((b != null && b.House.Class.House == PlayerPtr.Class.House) || Map[cell].Owner == PlayerPtr.Class.House)
                {
                    placementDistance[cell] = 0;
                    for (var facing = FacingType.FACING_N; facing < FacingType.FACING_COUNT; facing++)
                    {
                        int adj = TdMath.AdjacentCellOf(cell, facing);
                        if (Map.In_Radar(adj))
                            placementDistance[adj] = Math.Min(placementDistance[adj], (byte)1);
                    }
                }
            }
        }
    }

    /// <summary>Passes_Proximity_Check: some cell of the building would touch the player's base.</summary>
    public bool Passes_Placement_Proximity(int cell)
    {
        if (PlacementType == null)
            return false;
        foreach (short offset in PlacementType.Occupy_List(Types, Special, true))
        {
            if (offset == TdMath.RefreshEol)
                break;
            int c = cell + offset;
            if (!Map.In_Radar(c))
                return false;
            if (placementDistance[c] <= 1)
                return true;
        }
        return false;
    }

    /// <summary>Whether the pending building can go at the cell (clear and touching the base).</summary>
    public bool Can_Place_At(int cell) =>
        PlacementType != null && PlacementType.Legal_Placement(this, cell) != 0 && Passes_Placement_Proximity(cell);

    // ---- The map: selection and orders (DISPLAY.CPP) ----

    /// <summary>Unselect_All_Except.</summary>
    public void Unselect_All_Except(ObjectClass obj)
    {
        var list = CurrentObject.Of(PlayerPtr.Class.House);
        int index = 0;
        while (index < list.Count)
        {
            if (list[index] == obj)
            {
                index++;
                continue;
            }
            int before = list.Count;
            list[index].Unselect();
            if (before <= list.Count)
                CurrentObject.Delete(PlayerPtr.Class.House, list[index]);
        }
    }

    static readonly ActionType[] PriorityActions =
    [
        ActionType.ACTION_ATTACK, ActionType.ACTION_ENTER, ActionType.ACTION_REPAIR,
        ActionType.ACTION_SABOTAGE, ActionType.ACTION_CAPTURE, ActionType.ACTION_MOVE,
    ];

    static int Action_Priority(ActionType action)
    {
        int i = Array.IndexOf(PriorityActions, action);
        return i < 0 ? int.MaxValue : i;
    }

    /// <summary>Best_Object_With_Action: the selected object (one per type) whose action ranks highest.</summary>
    ObjectClass? Best_Object_With_Action(Func<ObjectClass, ActionType> what)
    {
        var objects = CurrentObject.Of(PlayerPtr.Class.House);
        if (objects.Count == 0)
            return null;
        var checkedTypes = new HashSet<ObjectTypeClass>();
        int bestPriority = int.MaxValue;
        ObjectClass best = objects[0];
        foreach (var obj in objects)
        {
            if (!checkedTypes.Add(obj.Class_Of()))
                continue;
            int priority = Action_Priority(what(obj));
            if (priority < bestPriority)
            {
                bestPriority = priority;
                best = obj;
                if (bestPriority == 0)
                    break;
            }
        }
        return best;
    }

    public ActionType Best_Object_Action(ObjectClass target) => Best_Object_With_Action(o => o.What_Action(target))?.What_Action(target) ?? ActionType.ACTION_NONE;
    public ActionType Best_Object_Action(int cell) => Best_Object_With_Action(o => o.What_Action(cell))?.What_Action(cell) ?? ActionType.ACTION_NONE;

    /// <summary>What a right-click at the coordinate would do (for the cursor).</summary>
    public ActionType Action_At(uint coord, out ObjectClass? obj)
    {
        obj = null;
        if (coord == 0)
            return ActionType.ACTION_NONE;
        int cell = TdMath.CoordCell(coord);
        bool shadow = !Map[cell].Is_Visible(PlayerPtr) && !DebugUnshroud;
        if (!shadow)
            obj = Map.Close_Object(coord);
        if (CurrentObject.Count(PlayerPtr.Class.House) == 0)
            return ActionType.ACTION_NONE;
        return obj != null ? Best_Object_Action(obj) : Best_Object_Action(cell);
    }

    /// <summary>Selection_At_Mouse: a left-click selects what's there (shift toggles), or clears.</summary>
    public void Selection_At_Mouse(uint coord, bool shift)
    {
        if (coord == 0)
            return;
        int cell = TdMath.CoordCell(coord);
        bool shadow = !Map[cell].Is_Visible(PlayerPtr) && !DebugUnshroud;
        ObjectClass? obj = shadow ? null : Map.Close_Object(coord);
        if (obj != null)
            Mouse_Left_Release(cell, obj, shift ? ActionType.ACTION_TOGGLE_SELECT : ActionType.ACTION_SELECT);
        else
            Unselect_All();
    }

    /// <summary>Command_Object: a right-click orders the selection to do the best action there.</summary>
    public void Command_Object(uint coord)
    {
        if (coord == 0)
            return;
        int cell = TdMath.CoordCell(coord);
        var action = Action_At(coord, out var obj);
        if (action != ActionType.ACTION_SELECT)
            Mouse_Left_Release(cell, obj, action);
    }

    /// <summary>Mouse_Left_Release (without the rubber band, which is Select_These).</summary>
    void Mouse_Left_Release(int cell, ObjectClass? obj, ActionType action)
    {
        var mine = CurrentObject.Of(PlayerPtr.Class.House);
        if (action == ActionType.ACTION_TOGGLE_SELECT)
        {
            if (obj == null || mine.Count == 0)
            {
                action = ActionType.ACTION_SELECT;
            }
            else if (obj.Is_Selected_By_Player())
            {
                obj.Unselect();
            }
            else
            {
                obj.Select();
            }
        }

        if (action == ActionType.ACTION_SELECT || (action == ActionType.ACTION_NONE && obj != null && obj.Class_Of().IsSelectable && !obj.Is_Selected_By_Player()))
        {
            if (obj != null)
            {
                if (obj.Is_Selected_By_Player())
                    obj.Unselect();
                if (obj.Select())
                    Unselect_All_Except(obj);
            }
        }

        if (action != ActionType.ACTION_NONE && action != ActionType.ACTION_SELECT && action != ActionType.ACTION_TOGGLE_SELECT)
        {
            AllowVoice = true;
            foreach (var tobject in mine.ToList())
            {
                ActionType a;
                if (obj != null)
                {
                    a = tobject.What_Action(obj);
                    tobject.Active_Click_With(a, obj);
                }
                else
                {
                    a = tobject.What_Action(cell);
                    tobject.Active_Click_With(a, cell);
                }
                if (a != ActionType.ACTION_NONE)
                    AllowVoice = false;
            }
            AllowVoice = true;

            if (action == ActionType.ACTION_REPAIR && obj is BuildingClass)
                OutList.Add(EventClass.Of(this, EventClass.EventType.REPAIR, obj.As_Target()));
            if (action == ActionType.ACTION_SELL_UNIT && obj is AircraftClass or UnitClass)
                OutList.Add(EventClass.Sell(this, obj!.As_Target()));
            if (action == ActionType.ACTION_SELL)
                OutList.Add(EventClass.Sell(this, obj != null ? obj.As_Target() : Target.As_Target(cell)));
            if (action == ActionType.ACTION_ION)
                OutList.Add(EventClass.Special_Place(this, (int)SpecialWeaponType.SPC_ION_CANNON, cell));
            if (action == ActionType.ACTION_NUKE_BOMB)
                OutList.Add(EventClass.Special_Place(this, (int)SpecialWeaponType.SPC_NUCLEAR_BOMB, cell));
            if (action == ActionType.ACTION_AIR_STRIKE)
                OutList.Add(EventClass.Special_Place(this, (int)SpecialWeaponType.SPC_AIR_STRIKE, cell));
        }
    }

    static bool Should_Exclude_From_Selection(ObjectClass obj) =>
        obj is UnitClass u && (u.Class.IsToHarvest || u.Class.Type == UnitType.UNIT_MCV);

    /// <summary>Select_These: everything selectable (not buildings) in the box between two map
    /// coordinates. Others' objects drop out when the player's own are in it; a box of mixed units
    /// drops harvesters and MCVs.</summary>
    public void Select_These(uint coord1, uint coord2, bool additive)
    {
        int x1 = TdMath.CoordX(coord1), x2 = TdMath.CoordX(coord2);
        int y1 = TdMath.CoordY(coord1), y2 = TdMath.CoordY(coord2);
        if (x1 > x2) (x1, x2) = (x2, x1);
        if (y1 > y2) (y1, y2) = (y2, y1);

        if (!additive)
            Unselect_All();

        AllowVoice = true;
        var ground = Map.Layer[(int)LayerType.LAYER_GROUND];
        for (int index = 0; index < ground.Count; index++)
        {
            var obj = ground[index];
            uint oc = obj.Center_Coord();
            int x = TdMath.CoordX(oc), y = TdMath.CoordY(oc);
            if (obj.Class_Of().IsSelectable && obj.What_Am_I() != RTTIType.RTTI_BUILDING &&
                (obj is not TechnoClass t || !t.Is_Cloaked(PlayerPtr)) &&
                x >= x1 && x <= x2 && y >= y1 && y <= y2)
            {
                bool oldAllowVoice = AllowVoice;
                bool isPlayerControlled = obj.Owner() == PlayerPtr.Class.House;
                AllowVoice &= isPlayerControlled;
                if (obj.Select(true) && isPlayerControlled)
                    oldAllowVoice = false;
                AllowVoice = oldAllowVoice;
            }
        }
        for (int index = 0; index < Aircraft.Count; index++)
        {
            var aircraft = Aircraft.Ptr(index);
            uint oc = TdMath.CoordAdd(aircraft.Center_Coord(), TdMath.XyCoord(0, -TdMath.PixelToLepton(aircraft.Altitude)));
            int x = TdMath.CoordX(oc), y = TdMath.CoordY(oc);
            if (aircraft.Class_Of().IsSelectable && !aircraft.Is_Cloaked(PlayerPtr) && !aircraft.Is_Selected_By_Player() &&
                x >= x1 && x <= x2 && y >= y1 && y <= y2)
            {
                bool oldAllowVoice = AllowVoice;
                bool isPlayerControlled = aircraft.Owner() == PlayerPtr.Class.House;
                AllowVoice &= isPlayerControlled;
                if (aircraft.Select(true) && isPlayerControlled)
                    oldAllowVoice = false;
                AllowVoice = oldAllowVoice;
            }
        }

        var mine = CurrentObject.Of(PlayerPtr.Class.House);
        bool playerUnits = mine.Any(o => o.Owner() == PlayerPtr.Class.House);
        bool otherUnits = mine.Any(o => o.Owner() != PlayerPtr.Class.House);
        if (playerUnits && otherUnits)
            foreach (var o in mine.Where(o => o.Owner() != PlayerPtr.Class.House).ToList())
                o.Unselect();

        if (!additive && playerUnits)
        {
            bool any = mine.Any(Should_Exclude_From_Selection);
            bool all = mine.All(Should_Exclude_From_Selection);
            if (any && !all)
                foreach (var o in mine.Where(Should_Exclude_From_Selection).ToList())
                    o.Unselect();
        }
        AllowVoice = true;
    }

    /// <summary>Mouse_Right_Press (the classic controls' cancel): drop the pending building,
    /// targeting mode, or the selection.</summary>
    public void Mouse_Right_Press()
    {
        if (PendingObject != null)
        {
            Cancel_Placement();
        }
        else if (IsTargettingMode)
        {
            IsTargettingMode = false;
        }
        else
        {
            Unselect_All();
        }
    }
}

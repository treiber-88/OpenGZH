using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic;

/// <summary>The cell's occupation flags (CellClass::Flag): five infantry spots, then vehicle, monolith, building.</summary>
public sealed class CellFlags
{
    public byte Composite;
    public readonly OccupyBits Occupy;

    public CellFlags() => Occupy = new OccupyBits(this);

    public sealed class OccupyBits(CellFlags flags)
    {
        bool Get(int bit) => (flags.Composite & (1 << bit)) != 0;
        void Put(int bit, bool value) => flags.Composite = (byte)(value ? flags.Composite | (1 << bit) : flags.Composite & ~(1 << bit));

        public bool Center { get => Get(0); set => Put(0, value); }
        public bool NW { get => Get(1); set => Put(1, value); }
        public bool NE { get => Get(2); set => Put(2, value); }
        public bool SW { get => Get(3); set => Put(3, value); }
        public bool SE { get => Get(4); set => Put(4, value); }
        public bool Vehicle { get => Get(5); set => Put(5, value); }
        public bool Monolith { get => Get(6); set => Put(6, value); }
        public bool Building { get => Get(7); set => Put(7, value); }
    }
}

/// <summary>
/// CellClass (CELL.CPP): one map cell — its terrain template, overlay (walls, Tiberium, crates),
/// smudge, who occupies it (a linked list of objects) and overlaps it, infantry spots, who has
/// mapped/seen it, and the crate pick-up.
/// </summary>
public sealed class CellClass
{
    readonly TdGame game;
    readonly short cellNumber;

    public bool IsPlot, IsCursorHere, IsMapped, IsVisible, IsTrigger, IsWaypoint, IsRadarCursor, IsFlagged;
    public TemplateType TType = TemplateType.TEMPLATE_NONE;
    public byte TIcon;
    public OverlayType Overlay = OverlayType.OVERLAY_NONE;
    public byte OverlayData;
    public SmudgeType Smudge = SmudgeType.SMUDGE_NONE;
    public byte SmudgeData;
    public HousesType Owner = HousesType.HOUSE_NONE;
    public HousesType InfType = HousesType.HOUSE_NONE;
    ObjectClass? occupierPtr;
    public readonly ObjectClass?[] Overlapper = new ObjectClass?[3];
    public uint IsMappedByPlayerMask;
    public uint IsVisibleByPlayerMask;
    public readonly CellFlags Flag = new();
    LandType land;
    LandType overrideLand = LandType.LAND_COUNT;
    AnimClass? ctfFlag;

    public CellClass(TdGame game, int cellNumber)
    {
        this.game = game;
        this.cellNumber = (short)cellNumber;
    }

    /// <summary>CellClass(): every field back to its starting value (Init_Cells assigns a fresh cell).</summary>
    public void Clear()
    {
        IsPlot = IsCursorHere = IsMapped = IsVisible = IsTrigger = IsWaypoint = IsRadarCursor = IsFlagged = false;
        TType = TemplateType.TEMPLATE_NONE;
        TIcon = 0;
        Overlay = OverlayType.OVERLAY_NONE;
        OverlayData = 0;
        Smudge = SmudgeType.SMUDGE_NONE;
        SmudgeData = 0;
        Owner = HousesType.HOUSE_NONE;
        InfType = HousesType.HOUSE_NONE;
        occupierPtr = null;
        Array.Clear(Overlapper);
        IsMappedByPlayerMask = 0;
        IsVisibleByPlayerMask = 0;
        Flag.Composite = 0;
        land = 0;
        overrideLand = LandType.LAND_COUNT;
        ctfFlag = null;
    }

    public int Cell_Number() => cellNumber;
    public uint Cell_Coord() => TdMath.CellCoord(cellNumber);
    public uint As_Target() => Target.As_Target(cellNumber);
    public LandType Land_Type() => overrideLand != LandType.LAND_COUNT ? overrideLand : land;
    public void Override_Land_Type(LandType type) => overrideLand = type;
    public bool Is_Spot_Free(int spotIndex) => (Flag.Composite & (1 << spotIndex)) == 0;
    public uint Free_Spot() => Closest_Free_Spot(Cell_Coord());

    /// <summary>Cell_Occupier: the first active object in the cell. (As the original, skipping a
    /// deleted object at the head also clears the head pointer.)</summary>
    public ObjectClass? Cell_Occupier()
    {
        ObjectClass? ptr = occupierPtr;
        while (ptr != null && !ptr.IsActive)
        {
            ptr = ptr.Next;
            occupierPtr = null;
        }
        return ptr;
    }

    /// <summary>Cell_Techno: the techno nearest a pixel spot within the cell (the cell's corner by default).</summary>
    public TechnoClass? Cell_Techno(int x = 0, int y = 0)
    {
        TechnoClass? close = null;
        long distance = 0;
        uint click = TdMath.XyCoord(TdMath.PixelToLepton(x), TdMath.PixelToLepton(y));

        ObjectClass? obj = Cell_Occupier();
        while (obj != null && obj.IsActive)
        {
            if (obj.Is_Techno())
            {
                uint coord = obj.Center_Coord() & 0x00FF00FF;
                long dist = TdMath.Distance(coord, click);
                if (close == null || dist < distance)
                {
                    close = (TechnoClass)obj;
                    distance = dist;
                }
            }
            obj = obj.Next;
        }
        return close;
    }

    public ObjectClass? Cell_Find_Object(RTTIType rtti)
    {
        ObjectClass? obj = Cell_Occupier();
        while (obj != null && obj.IsActive)
        {
            if (obj.What_Am_I() == rtti)
                return obj;
            obj = obj.Next;
        }
        return null;
    }

    public BuildingClass? Cell_Building() => (BuildingClass?)Cell_Find_Object(RTTIType.RTTI_BUILDING);
    public TerrainClass? Cell_Terrain() => (TerrainClass?)Cell_Find_Object(RTTIType.RTTI_TERRAIN);
    public UnitClass? Cell_Unit() => (UnitClass?)Cell_Find_Object(RTTIType.RTTI_UNIT);
    public InfantryClass? Cell_Infantry() => (InfantryClass?)Cell_Find_Object(RTTIType.RTTI_INFANTRY);

    /// <summary>Cell_Object: an aircraft here, else the nearest techno, else a tree.</summary>
    public ObjectClass? Cell_Object(int x = 0, int y = 0) =>
        Cell_Find_Object(RTTIType.RTTI_AIRCRAFT) ?? (ObjectClass?)Cell_Techno(x, y) ?? Cell_Terrain();

    /// <summary>Redraw_Objects: everything in or over the cell is flagged for redrawing (once per frame).</summary>
    public void Redraw_Objects(bool forced = false)
    {
        var map = game.Map;
        if (map.In_View(cellNumber) && (forced || !map.Is_Cell_Flagged(cellNumber)))
        {
            map.Flag_Cell(cellNumber);

            for (ObjectClass? optr = Cell_Occupier(); optr != null; optr = optr.Next)
                if (optr.IsActive)
                    optr.Mark(MarkType.MARK_CHANGE);

            for (int index = 0; index < Overlapper.Length; index++)
            {
                var o = Overlapper[index];
                if (o != null)
                {
                    if (!o.IsActive)
                        Overlapper[index] = null;
                    else
                        o.Mark(MarkType.MARK_CHANGE);
                }
            }
        }
    }

    /// <summary>Is_Generally_Clear: nothing in the cell (optionally ignoring cloaked units), no owned
    /// bib, wall or flag, and buildable ground. Always true while the scenario is being set up.</summary>
    public bool Is_Generally_Clear(bool ignoreCloaked = false)
    {
        if (game.ScenarioInit != 0)
            return true;

        if (Flag.Composite != 0 || IsFlagged || Cell_Occupier() != null)
        {
            var occupier = Cell_Occupier();
            if (!ignoreCloaked || (occupier != null && occupier.Is_Techno() && ((TechnoClass)occupier).Cloak != CloakType.CLOAKED))
                return false;
        }

        if (Smudge != SmudgeType.SMUDGE_NONE && game.Types.Smudge(Smudge).IsBib && Owner != HousesType.HOUSE_NONE)
            return false;

        if (Overlay != OverlayType.OVERLAY_NONE && (Overlay == OverlayType.OVERLAY_FLAG_SPOT || game.Types.Overlay(Overlay).IsWall))
            return false;

        return game.Types.Ground(Land_Type()).Build;
    }

    /// <summary>Is_Clear_To_Move (CELL.CPP, the remaster's RA-AI section): nothing in the cell (optionally
    /// ignoring infantry or vehicles) and no wall.</summary>
    public bool Is_Clear_To_Move(bool ignoreInfantry, bool ignoreVehicles)
    {
        int composite = Flag.Composite;
        if (ignoreInfantry)
            composite &= 0xE0;
        if (ignoreVehicles)
            composite &= 0x5F;
        if (composite != 0)
            return false;
        if (Overlay != OverlayType.OVERLAY_NONE && game.Types.Overlay(Overlay).IsWall)
            return false;
        return true;
    }

    /// <summary>Recalc_Attributes: the land type from the overlay, else the template's icon.</summary>
    public void Recalc_Attributes()
    {
        if (Overlay != OverlayType.OVERLAY_NONE)
        {
            land = game.Types.Overlay(Overlay).Land;
            if (land != LandType.LAND_CLEAR)
                return;
        }

        if (TType != TemplateType.TEMPLATE_NONE)
        {
            land = game.Types.Template(TType).LandOf(TIcon);
            return;
        }
        land = game.Types.Template(TemplateType.TEMPLATE_CLEAR1).Land;
    }

    /// <summary>Occupy_Down: the object goes at the head of the cell's list; the player may now see it.</summary>
    public void Occupy_Down(ObjectClass obj)
    {
        // Already here?
        for (ObjectClass? optr = Cell_Occupier(); optr != null; optr = optr.Next)
        {
            if (optr == obj)
                return;
            if (optr.Next == null)
                break;
        }

        obj.Next = Cell_Occupier();
        occupierPtr = obj;
        game.Map.Radar_Pixel(cellNumber);

        if (game.GameToPlay != GameType.GAME_GLYPHX_MULTIPLAYER)
        {
            if (IsVisible || game.GameToPlay != GameType.GAME_NORMAL)
                obj.Revealed(game.PlayerPtr);
        }
        else
        {
            for (int i = 0; i < game.MPlayerCount; i++)
            {
                HousesType houseType = game.MPlayerHouses[i];
                if (Is_Visible(houseType))
                    obj.Revealed(game.HouseOrNull(houseType));
            }
        }

        switch (obj.What_Am_I())
        {
            case RTTIType.RTTI_BUILDING:
                Flag.Occupy.Building = true;
                break;
            case RTTIType.RTTI_AIRCRAFT:
            case RTTIType.RTTI_UNIT:
                Flag.Occupy.Vehicle = true;
                break;
            case RTTIType.RTTI_TERRAIN:
                Flag.Occupy.Monolith = true;
                break;
        }
    }

    public void Occupy_Up(ObjectClass obj)
    {
        ObjectClass? optr = Cell_Occupier();
        if (optr == obj)
        {
            occupierPtr = obj.Next;
            obj.Next = null;
        }
        else
        {
            while (optr != null)
            {
                if (optr.Next == obj)
                {
                    optr.Next = obj.Next;
                    obj.Next = null;
                    break;
                }
                if (optr.Next == null)
                    break;
                optr = optr.Next;
            }
        }

        game.Map.Radar_Pixel(cellNumber);

        switch (obj.What_Am_I())
        {
            case RTTIType.RTTI_BUILDING:
                Flag.Occupy.Building = false;
                break;
            case RTTIType.RTTI_AIRCRAFT:
            case RTTIType.RTTI_UNIT:
                Flag.Occupy.Vehicle = false;
                break;
            case RTTIType.RTTI_TERRAIN:
                Flag.Occupy.Monolith = false;
                break;
        }
    }

    /// <summary>Overlap_Down: notes an object drawn over this cell (three slots; buildings may push out others).</summary>
    public void Overlap_Down(ObjectClass? obj)
    {
        if (obj == null)
            return;

        int freeSlot = -1;
        for (int index = 0; index < Overlapper.Length; index++)
        {
            if (Overlapper[index] == obj)
                return;
            if (Overlapper[index] == null)
                freeSlot = index;
        }

        if (freeSlot < 0 && obj.What_Am_I() == RTTIType.RTTI_BUILDING)
        {
            for (int index = 0; index < Overlapper.Length; index++)
            {
                var rtti = Overlapper[index]!.What_Am_I();
                if (rtti != RTTIType.RTTI_BUILDING && rtti != RTTIType.RTTI_TERRAIN)
                {
                    Overlapper[index] = obj;
                    break;
                }
            }
        }

        if (freeSlot >= 0)
            Overlapper[freeSlot] = obj;

        if (game.GameToPlay != GameType.GAME_GLYPHX_MULTIPLAYER)
        {
            if (IsVisible)
                obj.Revealed(game.PlayerPtr);
        }
        else if (obj is TechnoClass tech)
        {
            obj.Revealed(tech.House);
        }
        else
        {
            for (int i = 0; i < game.MPlayerCount; i++)
                obj.Revealed(game.HouseOrNull(game.MPlayerHouses[i]));
        }
    }

    public void Overlap_Up(ObjectClass obj)
    {
        for (int index = 0; index < Overlapper.Length; index++)
        {
            if (Overlapper[index] == obj)
            {
                Overlapper[index] = null;
                break;
            }
        }
    }

    // Concrete pieces (the C_ values in CELL.CPP).
    const int C_NONE = -1, C_LEFT = 0, C_RIGHT = 1, C_RIGHT_UPDOWN = 2, C_LEFT_UPDOWN = 3, C_UP_RIGHT = 4, C_UP_LEFT = 5, C_DOWN_RIGHT = 6,
              C_DOWN_LEFT = 7, C_RIGHT_DOWN = 8, C_LEFT_DOWN = 9, C_RIGHT_UP = 10, C_LEFT_UP = 11, C_UPDOWN_RIGHT = 12, C_UPDOWN_LEFT = 13;

    static readonly FacingType[] ConcreteEven = [FacingType.FACING_N, FacingType.FACING_S, FacingType.FACING_SW, FacingType.FACING_W, FacingType.FACING_NW];
    static readonly FacingType[] ConcreteOdd = [FacingType.FACING_N, FacingType.FACING_NE, FacingType.FACING_E, FacingType.FACING_SE, FacingType.FACING_S];

    /// <summary>Concrete_Calc: picks the concrete piece from its neighbours (and fills corners).</summary>
    public void Concrete_Calc()
    {
        const int OF_N = 0x01, OF_NE = 0x02, OF_E = 0x04, OF_SE = 0x08, OF_S = 0x10;
        const int EF_N = 0x01, EF_NW = 0x10, EF_W = 0x08, EF_SW = 0x04, EF_S = 0x02;

        bool isodd = (cellNumber & 0x01) != 0;
        var faces = isodd ? ConcreteOdd : ConcreteEven;
        int index = 0;
        for (int i = 0; i < faces.Length; i++)
        {
            var cellptr = Adjacent_Cell(faces[i]);
            if (cellptr != null && cellptr.Overlay == OverlayType.OVERLAY_CONCRETE)
                index |= 1 << i;
        }

        int icon;
        if (Overlay == OverlayType.OVERLAY_CONCRETE)
        {
            if (isodd)
            {
                icon = index switch
                {
                    OF_NE or (OF_N | OF_NE) or (OF_E | OF_N) or (OF_E | OF_NE) or (OF_N | OF_NE | OF_E) or (OF_S | OF_N | OF_NE) => C_RIGHT_UP,
                    OF_SE or (OF_E | OF_SE) or (OF_S | OF_SE) or (OF_S | OF_E) or (OF_S | OF_SE | OF_E) or (OF_S | OF_SE | OF_N) => C_RIGHT_DOWN,
                    (OF_SE | OF_NE) or (OF_SE | OF_NE | OF_N) or (OF_SE | OF_NE | OF_S) or (OF_SE | OF_NE | OF_S | OF_N) or (OF_SE | OF_E | OF_N) or
                    (OF_SE | OF_E | OF_NE | OF_N) or (OF_S | OF_E | OF_N) or (OF_S | OF_E | OF_NE) or (OF_S | OF_E | OF_NE | OF_N) or
                    (OF_S | OF_SE | OF_E | OF_N) or (OF_S | OF_SE | OF_E | OF_NE | OF_N) or (OF_S | OF_SE | OF_E | OF_NE) => C_RIGHT_UPDOWN,
                    _ => C_RIGHT,
                };
            }
            else
            {
                icon = index switch
                {
                    EF_NW or (EF_NW | EF_N) or (EF_W | EF_N) or (EF_NW | EF_W | EF_N) or (EF_NW | EF_W) or (EF_NW | EF_S | EF_N) => C_LEFT_UP,
                    EF_SW or (EF_SW | EF_S) or (EF_W | EF_S) or (EF_W | EF_SW | EF_S) or (EF_W | EF_SW) or (EF_SW | EF_S | EF_N) => C_LEFT_DOWN,
                    (EF_NW | EF_SW) or (EF_NW | EF_SW | EF_N) or (EF_NW | EF_SW | EF_S) or (EF_NW | EF_SW | EF_S | EF_N) or (EF_W | EF_S | EF_N) or
                    (EF_W | EF_SW | EF_N) or (EF_W | EF_SW | EF_S | EF_N) or (EF_NW | EF_W | EF_S) or (EF_NW | EF_W | EF_S | EF_N) or
                    (EF_NW | EF_W | EF_SW | EF_S | EF_N) or (EF_NW | EF_W | EF_SW | EF_N) or (EF_NW | EF_W | EF_SW | EF_S) => C_LEFT_UPDOWN,
                    _ => C_LEFT,
                };
            }
        }
        else
        {
            // An empty cell between concrete on two sides gets concrete too.
            icon = C_NONE;
            if (isodd)
            {
                index &= ~(OF_NE | OF_SE);
                icon = index switch
                {
                    OF_N | OF_E => C_UP_RIGHT,
                    OF_E | OF_S => C_DOWN_RIGHT,
                    OF_N | OF_E | OF_S => C_UPDOWN_RIGHT,
                    _ => C_NONE,
                };
            }
            else
            {
                index &= ~(EF_NW | EF_SW);
                icon = index switch
                {
                    EF_N | EF_W => C_UP_LEFT,
                    EF_W | EF_S => C_DOWN_LEFT,
                    EF_N | EF_W | EF_S => C_UPDOWN_LEFT,
                    _ => C_NONE,
                };
            }
            if (icon != C_NONE)
            {
                OverlayClass.Create(game, OverlayType.OVERLAY_CONCRETE, cellNumber);
                icon = C_NONE;
            }
        }

        if (icon != C_NONE && OverlayData != icon)
        {
            OverlayData = (byte)icon;
            Redraw_Objects();
        }
    }

    static readonly FacingType[] WallOffsets = [FacingType.FACING_N, FacingType.FACING_E, FacingType.FACING_S, FacingType.FACING_W, FacingType.FACING_NONE];

    /// <summary>Wall_Update: neighbouring walls (and this one) pick their joined frames; fully
    /// destroyed segments are removed.</summary>
    public void Wall_Update()
    {
        if (Overlay == OverlayType.OVERLAY_NONE || !game.Types.Overlay(Overlay).IsWall)
            return;

        foreach (FacingType offset in WallOffsets)
        {
            var newcell = Adjacent_Cell(offset);
            if (newcell != null && newcell.Overlay != OverlayType.OVERLAY_NONE && game.Types.Overlay(newcell.Overlay).IsWall)
            {
                int icon = 0;
                for (int i = 0; i < 4; i++)
                {
                    var adjcell = newcell.Adjacent_Cell(WallOffsets[i]);
                    if (adjcell != null && adjcell.Overlay == newcell.Overlay)
                        icon |= 1 << i;
                }
                newcell.OverlayData = (byte)((newcell.OverlayData & 0xF0) | icon);

                if ((newcell.Overlay == OverlayType.OVERLAY_BRICK_WALL && newcell.OverlayData == 48) ||
                    (newcell.Overlay == OverlayType.OVERLAY_SANDBAG_WALL && newcell.OverlayData == 16) ||
                    (newcell.Overlay == OverlayType.OVERLAY_CYCLONE_WALL && newcell.OverlayData == 32) ||
                    (newcell.Overlay == OverlayType.OVERLAY_BARBWIRE_WALL && newcell.OverlayData == 16))
                {
                    newcell.Overlay = OverlayType.OVERLAY_NONE;
                    newcell.OverlayData = 0;
                    game.Detach_This_From_All(Target.As_Target(newcell.Cell_Number()), true);
                }

                newcell.Recalc_Attributes();
                newcell.Redraw_Objects();
            }
        }
    }

    /// <summary>Reduce_Tiberium: harvests up to some levels; the amount taken.</summary>
    public int Reduce_Tiberium(int levels)
    {
        int reducer = 0;
        if (levels != 0 && land == LandType.LAND_TIBERIUM)
        {
            if (OverlayData > levels)
            {
                OverlayData = (byte)(OverlayData - levels);
                reducer = levels;
            }
            else
            {
                Overlay = OverlayType.OVERLAY_NONE;
                reducer = OverlayData;
                OverlayData = 0;
                Recalc_Attributes();
            }
        }
        return reducer;
    }

    /// <summary>Reduce_Wall: damages a wall (a chance below its damage points); true if it's destroyed.</summary>
    public bool Reduce_Wall(int damage)
    {
        if (Overlay != OverlayType.OVERLAY_NONE)
        {
            var wall = game.Types.Overlay(Overlay);
            if (wall.IsWall)
            {
                bool destroyed = damage >= wall.DamagePoints || game.Random_Pick(0, wall.DamagePoints) < damage;
                if (destroyed)
                {
                    OverlayData = (byte)(OverlayData + 16);
                    if ((OverlayData >> 4) >= wall.DamageLevels)
                    {
                        game.Detach_This_From_All(As_Target());
                        Owner = HousesType.HOUSE_NONE;
                        Overlay = OverlayType.OVERLAY_NONE;
                        OverlayData = 0;
                        Recalc_Attributes();
                        Redraw_Objects();
                        Adjacent_Cell(FacingType.FACING_N)?.Wall_Update();
                        Adjacent_Cell(FacingType.FACING_W)?.Wall_Update();
                        Adjacent_Cell(FacingType.FACING_S)?.Wall_Update();
                        Adjacent_Cell(FacingType.FACING_E)?.Wall_Update();
                        return true;
                    }
                }
            }
        }
        return false;
    }

    public Scenario.TriggerClass? Get_Trigger() => IsTrigger ? game.CellTriggers[cellNumber] : null;

    /// <summary>Spot_Index: which of the five infantry spots a coordinate is in (0 = centre).</summary>
    public static int Spot_Index(uint coord)
    {
        uint rel = coord & 0x00FF00FF;
        if (TdMath.Distance(rel, 0x00800080) < 60)
            return 0;
        int index = 0;
        if (TdMath.CoordX(rel) > 0x80)
            index |= 0x01;
        if (TdMath.CoordY(rel) > 0x80)
            index |= 0x02;
        return index + 1;
    }

    static readonly byte[][] SpotSequence = [[1, 2, 3, 4], [0, 2, 3, 4], [0, 1, 4, 3], [0, 1, 4, 2], [0, 2, 3, 1]];
    static readonly byte[][] SpotAlternate = [[1, 2, 3, 4], [2, 3, 4, 1], [3, 4, 1, 2], [4, 1, 2, 3]];

    /// <summary>Closest_Free_Spot: the nearest free infantry spot (0 if none, or a vehicle or tree is here).</summary>
    public uint Closest_Free_Spot(uint coord, bool any = false)
    {
        int spotIndex = Spot_Index(coord);
        coord &= 0xFF00FF00;

        if (!any && (Flag.Occupy.Vehicle || Flag.Occupy.Monolith))
            return 0;

        if (any || Is_Spot_Free(spotIndex))
            return TdMath.CoordAdd(coord, TdMath.StoppingCoordAbs[spotIndex]);

        byte[] sequence = spotIndex == 0 ? SpotAlternate[game.Random_Pick(0, 3)] : SpotSequence[spotIndex];
        foreach (byte pos in sequence)
            if (Is_Spot_Free(pos))
                return TdMath.CoordAdd(coord, TdMath.StoppingCoordAbs[pos]);
        return 0;
    }

    /// <summary>Clear_Icon: the clear terrain's icon varies with the cell's position.</summary>
    public int Clear_Icon() => (cellNumber & 0x03) | ((cellNumber >> 4) & 0x0C);

    /// <summary>Incoming: warns the occupants (they scatter if allowed).</summary>
    public void Incoming(uint threat = 0, bool forced = false, bool nokidding = false)
    {
        for (ObjectClass? obj = Cell_Occupier(); obj != null; obj = obj.Next)
        {
            if (nokidding || game.Special.IsScatter || (obj is TechnoClass techno && !techno.House.IsHuman))
                obj.Scatter(threat, forced, nokidding);
        }
    }

    /// <summary>Adjacent_Cell: the neighbouring cell (this one for FACING_NONE; null off the map).</summary>
    public CellClass? Adjacent_Cell(FacingType face)
    {
        if (face == FacingType.FACING_NONE)
            return this;
        if ((uint)face >= (uint)FacingType.FACING_COUNT)
            return null;
        int newcell = TdMath.AdjacentCellOf(cellNumber, face);
        if ((uint)newcell >= TdDefines.MAP_CELL_TOTAL)
            return null;
        return game.Map[newcell];
    }

    /// <summary>Adjust_Threat: the region's threat as other (non-allied human) houses see it.</summary>
    public void Adjust_Threat(HousesType house, int threatValue)
    {
        int region = game.Map.Cell_Region(cellNumber);
        for (var lp = HousesType.HOUSE_FIRST; lp < HousesType.HOUSE_COUNT; lp++)
        {
            if (lp == house)
                continue;
            var housePtr = game.HouseOrNull(lp);
            if (housePtr != null && (!housePtr.IsHuman || !housePtr.Is_Ally(house)))
                housePtr.Adjust_Threat(region, threatValue);
        }
    }

    static readonly int[] TiberiumAdjust = [0, 1, 3, 4, 6, 7, 8, 10, 11];

    /// <summary>Tiberium_Adjust: the field's growth stage from its Tiberium neighbours; its value.</summary>
    public int Tiberium_Adjust(bool pregame = false)
    {
        if (Overlay != OverlayType.OVERLAY_NONE && game.Types.Overlay(Overlay).Land == LandType.LAND_TIBERIUM)
        {
            int count = 0;
            if (pregame)
                Overlay = (OverlayType)game.Random_Pick((int)OverlayType.OVERLAY_TIBERIUM1, (int)OverlayType.OVERLAY_TIBERIUM12);

            for (var face = FacingType.FACING_FIRST; face < FacingType.FACING_COUNT; face++)
            {
                int c = cellNumber + TdMath.AdjacentCell[(int)face];
                if ((uint)c >= TdDefines.MAP_CELL_TOTAL)
                    continue;
                var adj = Adjacent_Cell(face);
                if (adj != null && adj.Overlay != OverlayType.OVERLAY_NONE && game.Types.Overlay(adj.Overlay).Land == LandType.LAND_TIBERIUM)
                    count++;
            }

            OverlayData = (byte)TiberiumAdjust[count];
            return (OverlayData + 1) * UnitTypeClass.TIBERIUM_STEP;
        }
        return 0;
    }

    // The crate goodies.
    const int MONEY = 0, UNIT = 1, NUKE = 2, ION = 3, NUKE_MISSILE = 4, ION_BLAST = 5, AIR_STRIKE = 6, HEAL_BASE = 7, CLOAK = 8,
              EXPLOSION = 9, NAPALM = 10, SQUAD = 11, VISCEROID = 12, DARKNESS = 13, REVEAL = 14;

    static readonly int[] CrateWhat =
    [
        DARKNESS, DARKNESS, REVEAL, REVEAL, NUKE, NUKE_MISSILE, ION_BLAST, ION_BLAST, AIR_STRIKE, AIR_STRIKE, AIR_STRIKE, AIR_STRIKE,
        HEAL_BASE, HEAL_BASE, CLOAK, CLOAK, EXPLOSION, EXPLOSION, EXPLOSION, EXPLOSION, NAPALM, NAPALM, NAPALM,
        SQUAD, SQUAD, SQUAD, SQUAD, SQUAD, UNIT, UNIT, UNIT, UNIT, UNIT, VISCEROID,
    ];

    static readonly InfantryType[] CrateSquad =
    [
        InfantryType.INFANTRY_E1, InfantryType.INFANTRY_E1, InfantryType.INFANTRY_E1, InfantryType.INFANTRY_E1, InfantryType.INFANTRY_E1, InfantryType.INFANTRY_E1,
        InfantryType.INFANTRY_E2, InfantryType.INFANTRY_E3, InfantryType.INFANTRY_E4, InfantryType.INFANTRY_E5, InfantryType.INFANTRY_E7, InfantryType.INFANTRY_RAMBO,
    ];

    /// <summary>Goodie_Check: picks up a crate (wooden ones when entering, steel ones — nuke pieces for
    /// Nod — at the cell's centre). False if the object shouldn't keep moving in (a unit appeared).</summary>
    public bool Goodie_Check(FootClass? obj, bool checkSteel = false)
    {
        if (obj == null || Overlay == OverlayType.OVERLAY_NONE || !game.Types.Overlay(Overlay).IsCrate)
            return true;

        bool steel = Overlay == OverlayType.OVERLAY_STEEL_CRATE;

        if (checkSteel && steel)
        {
            Redraw_Objects();
            Overlay = OverlayType.OVERLAY_NONE;
            OverlayData = 0;
            if (obj.Owner() == HousesType.HOUSE_BAD)
            {
                obj.House.Add_Nuke_Piece();
                AnimClass.Create(game, AnimType.ANIM_CRATE_EMPULSE, Cell_Coord());
            }
            return true;
        }

        if (checkSteel || steel)
            return true;

        Redraw_Objects();
        Overlay = OverlayType.OVERLAY_NONE;
        OverlayData = 0;

        var house = obj.House;
        int what = MONEY;
        var refinery = game.Types.Building(StructType.STRUCT_REFINERY);
        var power = game.Types.Building(StructType.STRUCT_POWER);

        // Multiplayer: something random (money if you need a refinery, an MCV if you've nothing).
        if (game.GameToPlay != GameType.GAME_NORMAL && (game.Random_Pick(1, 2) == 1 || house.BScan == 0))
        {
            what = -1;
            if ((house.BScan & (long)(TdDefines.STRUCTF_CONST | TdDefines.STRUCTF_REFINERY)) == (long)TdDefines.STRUCTF_CONST &&
                house.Available_Money() < refinery.Cost)
                what = MONEY;

            if (game.Random_Pick(0, 1) == 0 && game.MPlayerBases && (house.UScan & (long)TdDefines.UNITF_MCV) == 0 && house.BScan == 0 &&
                house.Available_Money() > (refinery.Cost + power.Cost) * house.CostBias)
                what = UNIT;

            bool allowSuper = !(game.GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER && !game.Rule.AllowSuperWeapons);

            while (what == -1)
            {
                what = CrateWhat[game.Random_Pick(0, CrateWhat.Length - 1)];
                if (what == REVEAL && house.IsVisionary) what = -1;
                if (what == AIR_STRIKE && (!allowSuper || house.AirStrike.Is_Present())) what = -1;
                if (what == NUKE_MISSILE && (!allowSuper || house.NukeStrike.Is_Present())) what = -1;
                if (what == ION_BLAST && (!allowSuper || house.IonCannon.Is_Present())) what = -1;
                if (what == CLOAK && obj.IsCloakable) what = -1;
            }
        }

        if (game.GameToPlay == GameType.GAME_INTERNET)
            house.TotalCrates.Increment_Unit_Total(what);

        game.CrateCount--;
        if (!game.CrateMaker && game.CrateCount <= 0 && game.GameToPlay != GameType.GAME_NORMAL)
        {
            game.CrateMaker = true;
            game.CrateTimer.Set(1);
        }

        switch (what)
        {
            default:
            case MONEY:
                AnimClass.Create(game, AnimType.ANIM_CRATE_DOLLAR, Cell_Coord());
                if (game.GameToPlay == GameType.GAME_NORMAL)
                    game.HouseFor(obj.Owner()).Refund_Money(2000);
                else
                    game.HouseFor(obj.Owner()).Refund_Money(100 + (game.Random_Pick(0, 19) * 100));
                break;

            case DARKNESS:
            {
                AnimClass.Create(game, AnimType.ANIM_CRATE_EMPULSE, Cell_Coord());
                bool shroud = game.GameToPlay != GameType.GAME_GLYPHX_MULTIPLAYER ? house == game.PlayerPtr : house.IsHuman;
                if (shroud)
                {
                    for (int cell = 0; cell < TdDefines.MAP_CELL_TOTAL; cell++)
                    {
                        var cellptr = game.Map[cell];
                        if (cellptr.Is_Mapped(house) || cellptr.Is_Visible(house))
                        {
                            cellptr.Redraw_Objects();
                            cellptr.Set_Mapped(house, false);
                            cellptr.Set_Visible(house, false);
                        }
                    }
                    var ground = game.Map.Layer[(int)LayerType.LAYER_GROUND];
                    for (int index = 0; index < ground.Count; index++)
                    {
                        var o = ground[index];
                        if (o != null && o is TechnoClass t && t.House == house)
                            o.Look();
                    }
                    game.Map.Flag_To_Redraw(true);
                }
                break;
            }

            case REVEAL:
            {
                bool reveal = game.GameToPlay != GameType.GAME_GLYPHX_MULTIPLAYER ? house == game.PlayerPtr : house.IsHuman;
                AnimClass.Create(game, AnimType.ANIM_CRATE_EMPULSE, Cell_Coord());
                house.IsVisionary = true;
                if (reveal)
                    for (int cell = 0; cell < TdDefines.MAP_CELL_TOTAL; cell++)
                        game.Map.Map_Cell(cell, house, true);
                break;
            }

            case UNIT:
            {
                UnitTypeClass? utp = null;
                if (game.MPlayerBases && (house.UScan & (long)TdDefines.UNITF_MCV) == 0 && house.BScan == 0 &&
                    house.Available_Money() > refinery.Cost + power.Cost)
                    utp = game.Types.Unit(UnitType.UNIT_MCV);

                if (utp == null && (house.BScan & (long)TdDefines.STRUCTF_REFINERY) != 0 && (house.UScan & (long)TdDefines.UNITF_HARVESTER) == 0)
                    utp = game.Types.Unit(UnitType.UNIT_HARVESTER);

                while (utp == null)
                {
                    var utype = (UnitType)game.Random_Pick((int)UnitType.UNIT_FIRST, (int)UnitType.UNIT_COUNT - 1);
                    if (utype != UnitType.UNIT_MCV || game.MPlayerBases)
                    {
                        utp = game.Types.Unit(utype);
                        if (utp.IsCrateGoodie && (utp.Ownable & (1L << (int)obj.Owner())) != 0 && utp.Level <= game.BuildLevel + 2)
                            break;
                        utp = null;
                    }
                }

                var unit = UnitClass.Create(game, utp.Type, house.Class.House);
                if (unit != null)
                {
                    if (unit.Unlimbo(Cell_Coord()))
                        return false;
                    unit.Delete();
                }
                break;
            }

            case SQUAD:
                for (int index = 0; index < 5; index++)
                {
                    var itype = CrateSquad[game.Random_Pick(0, CrateSquad.Length - 1)];
                    game.Create_And_Place(game.Types.Infantry(itype), cellNumber, obj.Owner());
                }
                return false;

            case NUKE:
                AnimClass.Create(game, AnimType.ANIM_ATOM_BLAST, Cell_Coord());
                break;

            case ION:
                AnimClass.Create(game, AnimType.ANIM_ION_CANNON, Cell_Coord());
                break;

            case NUKE_MISSILE:
                AnimClass.Create(game, AnimType.ANIM_CRATE_MISSILE, Cell_Coord());
                if (house.NukeStrike.Enable(true))
                    Crate_Special_To_Sidebar(obj, SpecialWeaponType.SPC_NUCLEAR_BOMB);
                break;

            case ION_BLAST:
                AnimClass.Create(game, AnimType.ANIM_CRATE_EARTH, Cell_Coord());
                if (house.IonCannon.Enable(true))
                    Crate_Special_To_Sidebar(obj, SpecialWeaponType.SPC_ION_CANNON);
                break;

            case AIR_STRIKE:
                AnimClass.Create(game, AnimType.ANIM_CRATE_DEVIATOR, Cell_Coord());
                if (house.AirStrike.Enable(true))
                    Crate_Special_To_Sidebar(obj, SpecialWeaponType.SPC_AIR_STRIKE);
                break;

            case EXPLOSION:
            {
                int damage = 400;
                obj.Take_Damage(ref damage, 0, WarheadType.WARHEAD_HE);
                for (int index = 0; index < 5; index++)
                {
                    // Coord_Scatter(Cell_Coord(), Random_Pick(0, 0x0200)): the distance is picked first.
                    int dist = game.Random_Pick(0, 0x0200);
                    uint coord = game.Coord_Scatter(Cell_Coord(), dist);
                    AnimClass.Create(game, AnimType.ANIM_FBALL1, coord);
                    damage = 400;
                    game.Explosion_Damage(coord, damage, null, WarheadType.WARHEAD_HE);
                }
                break;
            }

            case NAPALM:
            {
                uint coord = TdMath.CoordMid(Cell_Coord(), obj.Center_Coord());
                AnimClass.Create(game, AnimType.ANIM_NAPALM3, coord);
                game.Explosion_Damage(coord, 600, null, WarheadType.WARHEAD_FIRE);
                break;
            }

            case VISCEROID:
            {
                var unit = UnitClass.Create(game, UnitType.UNIT_VICE, HousesType.HOUSE_JP);
                if (unit != null)
                {
                    if (unit.Unlimbo(Cell_Coord()))
                        return false;
                    unit.Delete();
                }
                break;
            }

            case CLOAK:
            {
                AnimClass.Create(game, AnimType.ANIM_CRATE_STEALTH, Cell_Coord());
                var ground = game.Map.Layer[(int)LayerType.LAYER_GROUND];
                for (int index = 0; index < ground.Count; index++)
                {
                    var o = ground[index];
                    if (o is TechnoClass techno && TdMath.Distance(Cell_Coord(), o.Center_Coord()) < 0x0300)
                        techno.IsCloakable = true;
                }
                break;
            }

            case HEAL_BASE:
                AnimClass.Create(game, AnimType.ANIM_CRATE_INVUN, Cell_Coord());
                for (int index = 0; index < game.Logic.Count; index++)
                {
                    var o = game.Logic[index];
                    // As the original: it checks the crate collector (not each object) is a techno.
                    if (o != null && obj.Is_Techno() && house.Class.House == o.Owner())
                        o.Strength = o.Class_Of().MaxStrength;
                }
                break;
        }
        return true;
    }

    void Crate_Special_To_Sidebar(FootClass obj, SpecialWeaponType special)
    {
        if (game.GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER)
        {
            if (obj.House.IsHuman)
                game.Sidebar_Add(RTTIType.RTTI_SPECIAL, (int)special, obj.House, false);
        }
        else if (obj.IsOwnedByPlayer)
        {
            game.Sidebar_Add(RTTIType.RTTI_SPECIAL, (int)special, obj.House, false);
        }
    }

    /// <summary>Flag_Place: a capture-the-flag flag is put here.</summary>
    public bool Flag_Place(HousesType house)
    {
        if (!IsFlagged && Is_Generally_Clear())
        {
            IsFlagged = true;
            Owner = house;
            Flag_Update();
            Redraw_Objects();
            return true;
        }
        return false;
    }

    public bool Flag_Remove()
    {
        if (IsFlagged)
        {
            IsFlagged = false;
            Owner = HousesType.HOUSE_NONE;
            Flag_Update();
            Redraw_Objects();
            return true;
        }
        return false;
    }

    public void Flag_Update()
    {
        if (IsFlagged && ctfFlag == null)
            Flag_Create();
        else if (!IsFlagged && ctfFlag != null)
            Flag_Destroy();
    }

    /// <summary>Flag_Create: the flag's animation (making room in a full animation heap if needed).</summary>
    public void Flag_Create()
    {
        if (ctfFlag == null)
        {
            ctfFlag = AnimClass.Create(game, AnimType.ANIM_FLAG, Cell_Coord(), 0, 1, true);
            if (ctfFlag == null)
            {
                for (int i = 0; i < game.Anims.Count; ++i)
                {
                    var anim = game.Anims.Ptr(i);
                    if (anim.Class.Type != AnimType.ANIM_FLAG)
                    {
                        anim.Delete_This();
                        break;
                    }
                }
                ctfFlag = AnimClass.Create(game, AnimType.ANIM_FLAG, Cell_Coord(), 0, 1, true);
            }
            ctfFlag?.Set_Owner(Owner);
        }
    }

    public void Flag_Destroy()
    {
        ctfFlag?.Delete();
        ctfFlag = null;
    }

    /// <summary>Shimmer: cloaked units here flicker.</summary>
    public void Shimmer()
    {
        for (ObjectClass? obj = Cell_Occupier(); obj != null; obj = obj.Next)
            obj.Do_Shimmer();
    }

    public void Set_Mapped(HousesType house, bool set = true)
    {
        int shift = (int)house;
        if (set)
            IsMappedByPlayerMask |= 1u << shift;
        else
            IsMappedByPlayerMask &= ~(1u << shift);
    }

    public void Set_Mapped(HouseClass? player, bool set = true)
    {
        if (player?.Class != null)
        {
            Set_Mapped(player.Class.House, set);
            if (game.GameToPlay == GameType.GAME_NORMAL && player.IsHuman)
                IsMapped = set;
        }
    }

    public bool Is_Mapped(HousesType house) => (IsMappedByPlayerMask & (1u << (int)house)) != 0;
    public bool Is_Mapped(HouseClass? player) => player?.Class != null && Is_Mapped(player.Class.House);

    public void Set_Visible(HousesType house, bool set = true)
    {
        int shift = (int)house;
        if (set)
            IsVisibleByPlayerMask |= 1u << shift;
        else
            IsVisibleByPlayerMask &= ~(1u << shift);
    }

    public void Set_Visible(HouseClass? player, bool set = true)
    {
        if (player?.Class != null)
        {
            Set_Visible(player.Class.House, set);
            if (game.GameToPlay == GameType.GAME_NORMAL && player.IsHuman)
                IsVisible = set;
        }
    }

    public bool Is_Visible(HousesType house) => (IsVisibleByPlayerMask & (1u << (int)house)) != 0;
    public bool Is_Visible(HouseClass? player) => player?.Class != null && Is_Visible(player.Class.House);
}

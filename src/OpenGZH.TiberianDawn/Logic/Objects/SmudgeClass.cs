using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// SmudgeClass (SMUDGE.CPP): craters, scorch marks and building bibs. The object only exists long
/// enough to be placed: it marks the cells (CellClass.Smudge) and deletes itself.
/// </summary>
public sealed class SmudgeClass : ObjectClass
{
    public readonly SmudgeTypeClass Class;

    SmudgeClass(TdGame game, int slot, SmudgeType type, uint pos, HousesType house) : base(game)
    {
        game.Smudges.Place(slot, this);
        Set_Active();

        Class = game.Types.Smudge(type);
        if (pos != 0xFFFFFFFF)
        {
            game.SmudgeToOwn = house;
            if (!Unlimbo(pos))
                Delete_This();
            game.SmudgeToOwn = HousesType.HOUSE_NONE;
        }
    }

    /// <summary>new SmudgeClass(type, pos, house): placed at once when a position is given.</summary>
    public static SmudgeClass? Create(TdGame game, SmudgeType type, uint pos = 0xFFFFFFFF, HousesType house = HousesType.HOUSE_NONE)
    {
        int slot = game.Smudges.Allocate();
        if (slot < 0)
            return null;
        return new SmudgeClass(game, slot, type, pos, house);
    }

    protected override void Destruct()
    {
        if (Game.GameActive)
            Limbo();
        base.Destruct();
    }

    protected override void Release()
    {
        IsActive = false;
        Game.Smudges.Free(this);
    }

    public override RTTIType What_Am_I() => RTTIType.RTTI_SMUDGE;
    public override ObjectTypeClass Class_Of() => Class;

    /// <summary>Mark(MARK_DOWN): writes the smudge into the cells (craters deepen) and deletes itself.</summary>
    public override bool Mark(MarkType mark = MarkType.MARK_CHANGE)
    {
        if (base.Mark(mark) && mark == MarkType.MARK_DOWN)
        {
            int origin = TdMath.CoordCell(Coord);
            for (int w = 0; w < Class.Width; w++)
            {
                for (int h = 0; h < Class.Height; h++)
                {
                    int newcell = origin + w + (h * TdMath.MapCellW);
                    if (!Game.Map.In_Radar(newcell))
                        continue;

                    var cell = Game.Map[newcell];
                    if (Class.IsBib)
                    {
                        cell.Smudge = Class.Type;
                        cell.SmudgeData = (byte)(w + (h * Class.Width));
                        cell.Owner = Game.SmudgeToOwn;
                    }
                    else if (cell.Is_Generally_Clear())
                    {
                        if (Class.IsCrater && cell.Smudge != SmudgeType.SMUDGE_NONE && Game.Types.Smudge(cell.Smudge).IsCrater)
                        {
                            cell.SmudgeData++;
                            cell.SmudgeData = (byte)Math.Min((int)cell.SmudgeData, 4);
                        }

                        if (cell.Smudge == SmudgeType.SMUDGE_NONE)
                        {
                            // A crater's shape depends on where in the cell it landed.
                            cell.Smudge = Class.IsCrater ? (SmudgeType)((int)SmudgeType.SMUDGE_CRATER1 + CellClass.Spot_Index(Coord)) : Class.Type;
                            cell.SmudgeData = 0;
                        }
                    }
                    cell.Redraw_Objects();
                }
            }
            Delete_This();
            return true;
        }
        return false;
    }

    /// <summary>Disown: removes a bib from the cells (not under walls).</summary>
    public void Disown(int cell)
    {
        if (Class.IsBib)
        {
            for (int w = 0; w < Class.Width; w++)
            {
                for (int h = 0; h < Class.Height; h++)
                {
                    var cellptr = Game.Map[cell + w + (h * TdMath.MapCellW)];
                    if (cellptr.Overlay == OverlayType.OVERLAY_NONE || !Game.Types.Overlay(cellptr.Overlay).IsWall)
                    {
                        cellptr.Smudge = SmudgeType.SMUDGE_NONE;
                        cellptr.SmudgeData = 0;
                        cellptr.Owner = HousesType.HOUSE_NONE;
                        cellptr.Redraw_Objects();
                    }
                }
            }
        }
    }

    /// <summary>Read_INI: the scenario's [SMUDGE] — type,cell,data.</summary>
    public static void Read_INI(TdGame game, Data.WwProfile ini)
    {
        string buf = "";
        foreach (string key in ini.Entries("SMUDGE"))
        {
            buf = ini.Get_String("SMUDGE", key, buf, 128 - 1);
            var tok = new Data.Strtok(buf);
            SmudgeType smudge = game.Types.Smudge_From_Name(tok.Next(","));
            if (smudge == SmudgeType.SMUDGE_NONE)
                continue;
            string? ptr = tok.Next(",");
            if (ptr == null)
                continue;
            int data = 0;
            int cell = (short)Data.WwProfile.Atoi(ptr);
            ptr = tok.Next(",");
            if (ptr != null)
                data = Data.WwProfile.Atoi(ptr);
            Create(game, smudge, TdMath.CellCoord(cell));
            if (game.Map[cell].Smudge == smudge && data != 0)
                game.Map[cell].SmudgeData = (byte)data;
        }
    }
}

/// <summary>
/// OverlayClass (OVERLAY.CPP): walls, Tiberium, crates, concrete and roads. Like smudges, the
/// object writes itself into the cell (CellClass.Overlay) and is deleted.
/// </summary>
public sealed class OverlayClass : ObjectClass
{
    public readonly OverlayTypeClass Class;

    OverlayClass(TdGame game, int slot, OverlayType type, int pos, HousesType house) : base(game)
    {
        game.Overlays.Place(slot, this);
        Set_Active();

        Class = game.Types.Overlay(type);
        if (pos != -1)
        {
            game.OverlayToOwn = house;
            Unlimbo(TdMath.CellCoord(pos));
            game.OverlayToOwn = HousesType.HOUSE_NONE;
        }
    }

    /// <summary>new OverlayClass(type, cell, house): placed at once when a cell is given.</summary>
    public static OverlayClass? Create(TdGame game, OverlayType type, int pos = -1, HousesType house = HousesType.HOUSE_NONE)
    {
        int slot = game.Overlays.Allocate();
        if (slot < 0)
            return null;
        return new OverlayClass(game, slot, type, pos, house);
    }

    /// <summary>OverlayTypeClass::Create_One_Of: an unplaced overlay of the type (the house is ignored, as the original).</summary>
    public static OverlayClass? Create_One_Of(TdGame game, OverlayType type, HouseClass? house) => Create(game, type, -1);

    protected override void Destruct()
    {
        if (Game.GameActive)
            Limbo();
        base.Destruct();
    }

    protected override void Release()
    {
        IsActive = false;
        Game.Overlays.Free(this);
    }

    public override RTTIType What_Am_I() => RTTIType.RTTI_OVERLAY;
    public override ObjectTypeClass Class_Of() => Class;

    /// <summary>Mark(MARK_DOWN): writes the overlay into the cell (roads, walls, Tiberium, concrete
    /// with its paired cell, crates) and deletes itself.</summary>
    public override bool Mark(MarkType mark = MarkType.MARK_CHANGE)
    {
        if (base.Mark(mark) && mark == MarkType.MARK_DOWN)
        {
            int cell = TdMath.CoordCell(Coord);
            var cellptr = Game.Map[cell];

            if (Class.Type == OverlayType.OVERLAY_ROAD)
            {
                // A second road piece on a road makes it the other road frame.
                if ((cellptr.Overlay == OverlayType.OVERLAY_ROAD && cellptr.OverlayData == 0) ||
                    (cellptr.Overlay == OverlayType.OVERLAY_NONE && cellptr.Is_Generally_Clear()))
                {
                    cellptr.OverlayData = cellptr.Overlay == OverlayType.OVERLAY_ROAD ? (byte)1 : (byte)0;
                    cellptr.Overlay = Class.Type;
                    cellptr.Redraw_Objects();
                }
            }
            else
            {
                if (Class.IsWall)
                {
                    if (cellptr.Is_Generally_Clear() && cellptr.Overlay != OverlayType.OVERLAY_FLAG_SPOT)
                    {
                        cellptr.Overlay = Class.Type;
                        cellptr.OverlayData = 0;
                        cellptr.Redraw_Objects();
                        cellptr.Wall_Update();
                        if (Game.OverlayToOwn != HousesType.HOUSE_NONE)
                            cellptr.Owner = Game.OverlayToOwn;
                    }
                    else
                    {
                        Delete_This();
                        return false;
                    }
                }
                else if ((cellptr.Overlay == OverlayType.OVERLAY_NONE || cellptr.Overlay == OverlayType.OVERLAY_SQUISH) &&
                         cellptr.Cell_Terrain() == null && Game.Types.Ground(cellptr.Land_Type()).Build)
                {
                    if (Class.IsCrate)
                        Game.CrateCount++;

                    // (The squish mark only shows in "gross" mode.)
                    if (!Game.Special.IsGross && Class.Type != OverlayType.OVERLAY_SQUISH)
                    {
                        cellptr.Overlay = Class.Type;
                        cellptr.OverlayData = 0;
                    }
                    cellptr.Redraw_Objects();

                    if (Class.Land == LandType.LAND_TIBERIUM)
                    {
                        cellptr.OverlayData = 1;
                        cellptr.Tiberium_Adjust();
                    }
                    else if (Class.Type == OverlayType.OVERLAY_CONCRETE)
                    {
                        // Concrete comes in pairs of cells.
                        cellptr.Smudge = SmudgeType.SMUDGE_NONE;
                        cellptr.SmudgeData = 0;
                        cellptr.Concrete_Calc();

                        int newcell = (TdMath.CellX(cell) & 0x01) != 0
                            ? TdMath.AdjacentCellOf(cellptr.Cell_Number(), FacingType.FACING_W)
                            : TdMath.AdjacentCellOf(cellptr.Cell_Number(), FacingType.FACING_E);
                        if (Game.Map[newcell].Overlay != OverlayType.OVERLAY_CONCRETE)
                            Create(Game, Class.Type, newcell);

                        foreach (FacingType face in ConcreteFaces)
                            cellptr.Adjacent_Cell(face)?.Concrete_Calc();
                    }
                }
                cellptr.Recalc_Attributes();
            }
            Delete_This();
            return true;
        }
        return false;
    }

    static readonly FacingType[] ConcreteFaces = [FacingType.FACING_N, FacingType.FACING_E, FacingType.FACING_S, FacingType.FACING_W];

    /// <summary>Read_INI: the scenario's [OVERLAY] — cell=type (no crates in multiplayer; not on the top or bottom row).</summary>
    public static void Read_INI(TdGame game, Data.WwProfile ini)
    {
        string buf = "";
        foreach (string key in ini.Entries("OVERLAY"))
        {
            int cell = (short)Data.WwProfile.Atoi(key);
            buf = ini.Get_String("OVERLAY", key, buf, 128 - 1);
            var tok = new Data.Strtok(buf);
            OverlayType classid = game.Types.Overlay_From_Name(tok.Next(",\n\r"));
            if (classid != OverlayType.OVERLAY_NONE && (game.GameToPlay == GameType.GAME_NORMAL || !game.Types.Overlay(classid).IsCrate))
            {
                if (cell >= TdMath.MapCellW && cell <= TdDefines.MAP_CELL_TOTAL - TdMath.MapCellW)
                    Create(game, classid, cell);
            }
        }
    }
}

using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic;

/// <summary>
/// LayerClass (LAYER.CPP): the objects in one display layer (ground, air, top). The ground layer
/// is kept sorted by Sort_Y so things further down the screen are drawn over those above.
/// </summary>
public class LayerClass
{
    readonly List<ObjectClass> items = new();

    public int Count => items.Count;
    public ObjectClass this[int index] { get => items[index]; set => items[index] = value; }

    public void Clear() => items.Clear();

    public bool Submit(ObjectClass obj, bool sort = false)
    {
        if (sort)
            return Sorted_Add(obj);
        items.Add(obj);
        return true;
    }

    public bool Delete(ObjectClass obj) => items.Remove(obj);

    /// <summary>Sort: one bubble pass (the list stays nearly sorted from frame to frame).</summary>
    public void Sort()
    {
        for (int index = 0; index < items.Count - 1; index++)
        {
            if (items[index + 1].Sort_Y() < items[index].Sort_Y())
                (items[index + 1], items[index]) = (items[index], items[index + 1]);
        }
    }

    /// <summary>Sorted_Add: inserted before the first object that sorts after it.</summary>
    public bool Sorted_Add(ObjectClass obj)
    {
        int index;
        for (index = 0; index < items.Count; index++)
            if (items[index].Sort_Y() > obj.Sort_Y())
                break;
        items.Insert(index, obj);
        return true;
    }
}

/// <summary>
/// The game map (MAP.CPP's MapClass, with the logic parts of DISPLAY.CPP's DisplayClass): the
/// 64x64 cells, the playable rectangle, layers, revealing the shroud, Tiberium growth, and the
/// reinforcement entry points. The drawing parts of DisplayClass are in the client.
/// </summary>
public sealed class TdMap
{
    readonly TdGame game;
    readonly CellClass[] cells;
    readonly System.Collections.BitArray cellRedraw = new(TdDefines.MAP_CELL_TOTAL);

    public int MapCellX, MapCellY, MapCellWidth, MapCellHeight;
    public long TotalValue;
    public TheaterType Theater = TheaterType.THEATER_DESERT;
    public readonly LayerClass[] Layer = [new(), new(), new()];

    /// <summary>The tactical view (top-left coordinate and size in leptons), set by the client.</summary>
    public uint TacticalCoord;
    public int TacLeptonWidth = 0x7FFF, TacLeptonHeight = 0x7FFF;
    public int TacPixelX, TacPixelY;
    public bool IgnoreViewConstraints;

    readonly int[] tiberiumGrowth = new int[50];
    int tiberiumGrowthCount;
    readonly int[] tiberiumSpread = new int[50];
    int tiberiumSpreadCount;
    int tiberiumScan;
    bool isForwardScan = true;

    public TdMap(TdGame game)
    {
        this.game = game;
        cells = new CellClass[TdDefines.MAP_CELL_TOTAL];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = new CellClass(game, i);
    }

    /// <summary>Map[cell]. (The original indexes without checks; a cell off the 64x64 map wraps here.)</summary>
    public CellClass this[int cell] => cells[cell & (TdDefines.MAP_CELL_TOTAL - 1)];

    const int MCW = TdMath.MapCellW;

    /// <summary>RadiusOffset/RadiusCount: the cells within each sight range (1 to 10), nearest first.</summary>
    static readonly int[] RadiusOffset =
    [
        0,
        (-MCW*1)-1,(-MCW*1)+0,(-MCW*1)+1,-1,1,(MCW*1)-1,(MCW*1)+0,(MCW*1)+1,
        (-MCW*2)-1,(-MCW*2)+0,(-MCW*2)+1,(-MCW*1)-2,(-MCW*1)+2,-2,2,(MCW*1)-2,(MCW*1)+2,(MCW*2)-1,(MCW*2)+0,(MCW*2)+1,
        (-MCW*3)-1,(-MCW*3)+0,(-MCW*3)+1,(-MCW*2)-2,(-MCW*2)+2,(-MCW*1)-3,(-MCW*1)+3,-3,3,(MCW*1)-3,(MCW*1)+3,(MCW*2)-2,(MCW*2)+2,(MCW*3)-1,(MCW*3)+0,(MCW*3)+1,
        (-MCW*4)-1,(-MCW*4)+0,(-MCW*4)+1,(-MCW*3)-3,(-MCW*3)-2,(-MCW*3)+2,(-MCW*3)+3,(-MCW*2)-3,(-MCW*2)+3,(-MCW*1)-4,(-MCW*1)+4,-4,4,(MCW*1)-4,(MCW*1)+4,(MCW*2)-3,(MCW*2)+3,(MCW*3)-3,(MCW*3)-2,(MCW*3)+2,(MCW*3)+3,(MCW*4)-1,(MCW*4)+0,(MCW*4)+1,
        (-MCW*5)-1,(-MCW*5)+0,(-MCW*5)+1,(-MCW*4)-3,(-MCW*4)-2,(-MCW*4)+2,(-MCW*4)+3,(-MCW*3)-4,(-MCW*3)+4,(-MCW*2)-4,(-MCW*2)+4,(-MCW*1)-5,(-MCW*1)+5,-5,5,(MCW*1)-5,(MCW*1)+5,(MCW*2)-4,(MCW*2)+4,(MCW*3)-4,(MCW*3)+4,(MCW*4)-3,(MCW*4)-2,(MCW*4)+2,(MCW*4)+3,(MCW*5)-1,(MCW*5)+0,(MCW*5)+1,
        (-MCW*6)-1,(-MCW*6)+0,(-MCW*6)+1,(-MCW*5)-3,(-MCW*5)-2,(-MCW*5)+2,(-MCW*5)+3,(-MCW*4)-4,(-MCW*4)+4,(-MCW*3)-5,(-MCW*3)+5,(-MCW*2)-5,(-MCW*2)+5,(-MCW*1)-6,(-MCW*1)+6,-6,6,(MCW*1)-6,(MCW*1)+6,(MCW*2)-5,(MCW*2)+5,(MCW*3)-5,(MCW*3)+5,(MCW*4)-4,(MCW*4)+4,(MCW*5)-3,(MCW*5)-2,(MCW*5)+2,(MCW*5)+3,(MCW*6)-1,(MCW*6)+0,(MCW*6)+1,
        (-MCW*7)-1,(-MCW*7)+0,(-MCW*7)+1,(-MCW*6)-3,(-MCW*6)-2,(-MCW*6)+2,(-MCW*6)+3,(-MCW*5)-5,(-MCW*5)-4,(-MCW*5)+4,(-MCW*5)+5,(-MCW*4)-5,(-MCW*4)+5,(-MCW*3)-6,(-MCW*3)+6,(-MCW*2)-6,(-MCW*2)+6,(-MCW*1)-7,(-MCW*1)+7,-7,7,(MCW*1)-7,(MCW*1)+7,(MCW*2)-6,(MCW*2)+6,(MCW*3)-6,(MCW*3)+6,(MCW*4)-5,(MCW*4)+5,(MCW*5)-5,(MCW*5)-4,(MCW*5)+4,(MCW*5)+5,(MCW*6)-3,(MCW*6)-2,(MCW*6)+2,(MCW*6)+3,(MCW*7)-1,(MCW*7)+0,(MCW*7)+1,
        (-MCW*8)-1,(-MCW*8)+0,(-MCW*8)+1,(-MCW*7)-3,(-MCW*7)-2,(-MCW*7)+2,(-MCW*7)+3,(-MCW*6)-5,(-MCW*6)-4,(-MCW*6)+4,(-MCW*6)+5,(-MCW*5)-6,(-MCW*5)+6,(-MCW*4)-6,(-MCW*4)+6,(-MCW*3)-7,(-MCW*3)+7,(-MCW*2)-7,(-MCW*2)+7,(-MCW*1)-8,(-MCW*1)+8,-8,8,(MCW*1)-8,(MCW*1)+8,(MCW*2)-7,(MCW*2)+7,(MCW*3)-7,(MCW*3)+7,(MCW*4)-6,(MCW*4)+6,(MCW*5)-6,(MCW*5)+6,(MCW*6)-5,(MCW*6)-4,(MCW*6)+4,(MCW*6)+5,(MCW*7)-3,(MCW*7)-2,(MCW*7)+2,(MCW*7)+3,(MCW*8)-1,(MCW*8)+0,(MCW*8)+1,
        (-MCW*9)-1,(-MCW*9)+0,(-MCW*9)+1,(-MCW*8)-3,(-MCW*8)-2,(-MCW*8)+2,(-MCW*8)+3,(-MCW*7)-5,(-MCW*7)-4,(-MCW*7)+4,(-MCW*7)+5,(-MCW*6)-6,(-MCW*6)+6,(-MCW*5)-7,(-MCW*5)+7,(-MCW*4)-7,(-MCW*4)+7,(-MCW*3)-8,(-MCW*3)+8,(-MCW*2)-8,(-MCW*2)+8,(-MCW*1)-9,(-MCW*1)+9,-9,9,(MCW*1)-9,(MCW*1)+9,(MCW*2)-8,(MCW*2)+8,(MCW*3)-8,(MCW*3)+8,(MCW*4)-7,(MCW*4)+7,(MCW*5)-7,(MCW*5)+7,(MCW*6)-6,(MCW*6)+6,(MCW*7)-5,(MCW*7)-4,(MCW*7)+4,(MCW*7)+5,(MCW*8)-3,(MCW*8)-2,(MCW*8)+2,(MCW*8)+3,(MCW*9)-1,(MCW*9)+0,(MCW*9)+1,
        (-MCW*10)-1,(-MCW*10)+0,(-MCW*10)+1,(-MCW*9)-3,(-MCW*9)-2,(-MCW*9)+2,(-MCW*9)+3,(-MCW*8)-5,(-MCW*8)-4,(-MCW*8)+4,(-MCW*8)+5,(-MCW*7)-7,(-MCW*7)-6,(-MCW*7)+6,(-MCW*7)+7,(-MCW*6)-7,(-MCW*6)+7,(-MCW*5)-8,(-MCW*5)+8,(-MCW*4)-8,(-MCW*4)+8,(-MCW*3)-9,(-MCW*3)+9,(-MCW*2)-9,(-MCW*2)+9,(-MCW*1)-10,(-MCW*1)+10,-10,10,(MCW*1)-10,(MCW*1)+10,(MCW*2)-9,(MCW*2)+9,(MCW*3)-9,(MCW*3)+9,(MCW*4)-8,(MCW*4)+8,(MCW*5)-8,(MCW*5)+8,(MCW*6)-7,(MCW*6)+7,(MCW*7)-7,(MCW*7)-6,(MCW*7)+6,(MCW*7)+7,(MCW*8)-5,(MCW*8)-4,
        (MCW*8)+4,(MCW*8)+5,(MCW*9)-3,(MCW*9)-2,(MCW*9)+2,(MCW*9)+3,(MCW*10)-1,(MCW*10)+0,(MCW*10)+1,
    ];
    static readonly int[] RadiusCount = [1, 9, 21, 37, 61, 89, 121, 161, 205, 253, 309];

    /// <summary>Init_Clear: all cells cleared, the Tiberium scan restarted, the layers emptied.</summary>
    public void Init_Clear()
    {
        Init_Cells();
        tiberiumScan = 0;
        isForwardScan = true;
        tiberiumGrowthCount = 0;
        tiberiumSpreadCount = 0;
        foreach (var layer in Layer)
            layer.Clear();
    }

    public void Init_Cells()
    {
        TotalValue = 0;
        foreach (var cell in cells)
            cell.Clear();
    }

    public void Set_Map_Dimensions(int x, int y, int w, int h)
    {
        MapCellX = x;
        MapCellY = y;
        MapCellWidth = w;
        MapCellHeight = h;
    }

    /// <summary>Sight_From: reveals the cells within a sight range for a human house (only the outer
    /// ring when incremental — the unit has just moved one cell).</summary>
    public void Sight_From(HouseClass? house, int cell, int sightrange, bool incremental = false)
    {
        if ((house == null || !house.IsHuman) && !game.ShareAllyVisibility)
            return;
        if (!In_Radar(cell))
            return;
        if (sightrange == 0 || sightrange > 10)
            return;

        int xx = TdMath.CellX(cell);
        int count = RadiusCount[sightrange];
        int ptr = 0;
        if (incremental && sightrange > 1)
        {
            ptr += RadiusCount[sightrange - 2];
            count -= RadiusCount[sightrange - 2];
        }

        while (count-- > 0)
        {
            int newcell = (short)(cell + RadiusOffset[ptr++]);
            if ((uint)newcell >= TdDefines.MAP_CELL_TOTAL)
                continue;
            int xdiff = Math.Abs(TdMath.CellX(newcell) - xx);
            if (xdiff > sightrange)
                continue;
            if (TdMath.CellDistance(newcell, cell) > sightrange)
                continue;
            Map_Cell(newcell, house, true);
        }
    }

    public int Cell_Distance(int cell1, int cell2) => TdMath.CellDistance(cell1, cell2);

    /// <summary>In_Radar: inside the playable rectangle.</summary>
    public bool In_Radar(int cell)
    {
        if ((cell & 0xF000) != 0)
            return false;
        return (uint)(TdMath.CellX(cell) - MapCellX) < (uint)MapCellWidth && (uint)(TdMath.CellY(cell) - MapCellY) < (uint)MapCellHeight;
    }

    /// <summary>Place_Down: the object occupies its cells and overlaps the cells it's drawn over.</summary>
    public void Place_Down(int cell, ObjectClass? obj)
    {
        if (obj == null)
            return;
        foreach (short o in obj.Occupy_List())
        {
            if (o == TdMath.RefreshEol)
                break;
            int newcell = (short)(cell + o);
            if ((uint)newcell < TdDefines.MAP_CELL_TOTAL)
            {
                this[newcell].Occupy_Down(obj);
                this[newcell].Recalc_Attributes();
                this[newcell].Redraw_Objects();
            }
        }
        foreach (short o in obj.Overlap_List())
        {
            if (o == TdMath.RefreshEol)
                break;
            int newcell = (short)(cell + o);
            if ((uint)newcell < TdDefines.MAP_CELL_TOTAL)
            {
                this[newcell].Overlap_Down(obj);
                this[newcell].Redraw_Objects();
            }
        }
    }

    public void Pick_Up(int cell, ObjectClass? obj)
    {
        if (obj == null)
            return;
        foreach (short o in obj.Occupy_List())
        {
            if (o == TdMath.RefreshEol)
                break;
            int newcell = (short)(cell + o);
            if ((uint)newcell < TdDefines.MAP_CELL_TOTAL)
            {
                this[newcell].Occupy_Up(obj);
                this[newcell].Recalc_Attributes();
                this[newcell].Redraw_Objects();
            }
        }
        foreach (short o in obj.Overlap_List())
        {
            if (o == TdMath.RefreshEol)
                break;
            int newcell = (short)(cell + o);
            if ((uint)newcell < TdDefines.MAP_CELL_TOTAL)
            {
                this[newcell].Overlap_Up(obj);
                this[newcell].Redraw_Objects();
            }
        }
    }

    public void Overlap_Down(int cell, ObjectClass? obj)
    {
        if (obj == null)
            return;
        foreach (short o in obj.Overlap_List())
        {
            if (o == TdMath.RefreshEol)
                break;
            int newcell = (short)(cell + o);
            if ((uint)newcell < TdDefines.MAP_CELL_TOTAL)
            {
                this[newcell].Overlap_Down(obj);
                this[newcell].Redraw_Objects();
            }
        }
    }

    public void Overlap_Up(int cell, ObjectClass? obj)
    {
        if (obj == null)
            return;
        foreach (short o in obj.Overlap_List())
        {
            if (o == TdMath.RefreshEol)
                break;
            int newcell = (short)(cell + o);
            if ((uint)newcell < TdDefines.MAP_CELL_TOTAL)
            {
                this[newcell].Overlap_Up(obj);
                this[newcell].Redraw_Objects();
            }
        }
    }

    /// <summary>Overpass: every Tiberium cell gets a random look and its growth stage; the total value.</summary>
    public long Overpass()
    {
        long value = 0;
        for (int y = 0; y < MapCellHeight - 1; y++)
            for (int x = 0; x < MapCellWidth; x++)
                value += this[(MapCellY + y) * TdMath.MapCellW + (MapCellX + x)].Tiberium_Adjust(true);
        return value;
    }

    /// <summary>Read_Binary: the scenario's .BIN (template and icon per cell), invalid icons cleared.</summary>
    public bool Read_Binary(byte[]? data, ref uint crc)
    {
        if (data == null)
            return false;

        int i;
        for (i = 0; i < TdDefines.MAP_CELL_TOTAL; i++)
        {
            if (i * 2 + 1 >= data.Length)
                break;
            var ttype = (TemplateType)data[i * 2];
            byte ticon = data[i * 2 + 1];
            if ((int)ttype == 255)
                ttype = TemplateType.TEMPLATE_NONE;

            if (ttype != TemplateType.TEMPLATE_CLEAR1 && ttype != TemplateType.TEMPLATE_NONE)
            {
                var template = game.Types.Template(ttype);
                // The icon set's map (-1 = an empty icon).
                sbyte[]? iconMap = game.Art?.Template_Icon_Map(template);
                if (iconMap != null && (ticon >= template.Width * template.Height || (ticon < iconMap.Length && iconMap[ticon] == -1)))
                {
                    ticon = 0;
                    ttype = TemplateType.TEMPLATE_NONE;
                }
            }

            var cell = cells[i];
            cell.TType = ttype;
            cell.TIcon = ticon;
            cell.Recalc_Attributes();
            TdGame.Add_CRC(ref crc, (uint)(byte)(int)cell.TType);
            TdGame.Add_CRC(ref crc, cell.TIcon);
        }
        return i == TdDefines.MAP_CELL_TOTAL;
    }

    /// <summary>Logic: Tiberium grows and spreads — 30 cells are scanned per frame; when the scan
    /// completes, a few candidates grow a stage or seed a neighbouring cell.</summary>
    public void Logic()
    {
        var special = game.Special;
        if (!special.IsTGrowth && !special.IsTSpread)
            return;

        int subcount = 30;
        int index;
        for (index = tiberiumScan; index < TdDefines.MAP_CELL_TOTAL; index++)
        {
            int cell = isForwardScan ? index : (TdDefines.MAP_CELL_TOTAL - 1) - index;
            var ptr = this[cell];

            if (special.IsTGrowth && ptr.Land_Type() == LandType.LAND_TIBERIUM && ptr.OverlayData < 11)
            {
                if (tiberiumGrowthCount < tiberiumGrowth.Length)
                    tiberiumGrowth[tiberiumGrowthCount++] = cell;
                else
                    tiberiumGrowth[game.Random_Pick(0, tiberiumGrowthCount - 1)] = cell;
            }

            // As the original's precedence: (spreading && rich Tiberium) || a blossom tree.
            var terrain = ptr.Cell_Terrain();
            if ((special.IsTSpread && ptr.Land_Type() == LandType.LAND_TIBERIUM && ptr.OverlayData > 6) ||
                (terrain != null && terrain.Class.IsTiberiumSpawn))
            {
                int tries = terrain != null ? 3 : 1;
                for (int i = 0; i < tries; i++)
                {
                    if (tiberiumSpreadCount < tiberiumSpread.Length)
                        tiberiumSpread[tiberiumSpreadCount++] = cell;
                    else
                        tiberiumSpread[game.Random_Pick(0, tiberiumSpreadCount - 1)] = cell;
                }
            }

            subcount--;
            if (subcount == 0)
                break;
        }
        tiberiumScan = index;

        if (tiberiumScan >= TdDefines.MAP_CELL_TOTAL)
        {
            int tries = 1;
            if (special.IsTFast || game.GameToPlay != GameType.GAME_NORMAL)
                tries = 2;
            if (game.GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER && game.MPlayerTiberium > 1)
                tries += (game.MPlayerTiberium - 1) << 1;

            tiberiumScan = 0;
            isForwardScan = !isForwardScan;

            if (tiberiumGrowthCount != 0)
            {
                for (int i = 0; i < tries; i++)
                {
                    int pick = game.Random_Pick(0, tiberiumGrowthCount - 1);
                    var newcell = this[tiberiumGrowth[pick]];
                    if (newcell.Land_Type() == LandType.LAND_TIBERIUM && newcell.OverlayData < 12 - 1)
                        newcell.OverlayData++;
                    tiberiumGrowth[pick] = tiberiumGrowth[tiberiumGrowthCount - 1];
                    tiberiumGrowthCount--;
                    if (tiberiumGrowthCount <= 0)
                        break;
                }
            }
            tiberiumGrowthCount = 0;

            if (tiberiumSpreadCount != 0)
            {
                for (int i = 0; i < tries; i++)
                {
                    int pick = game.Random_Pick(0, tiberiumSpreadCount - 1);
                    int cell = tiberiumSpread[pick];
                    if (In_Radar(cell))
                    {
                        int offset = game.Random_Pick((int)FacingType.FACING_N, (int)FacingType.FACING_NW);
                        for (int f = 0; f < (int)FacingType.FACING_COUNT; f++)
                        {
                            var newcell = this[cell].Adjacent_Cell(TdMath.FacingAdd((FacingType)f, offset));
                            if (newcell != null && newcell.Cell_Object() == null && newcell.Land_Type() == LandType.LAND_CLEAR && newcell.Overlay == OverlayType.OVERLAY_NONE)
                            {
                                if (newcell.TType is TemplateType.TEMPLATE_BRIDGE1 or TemplateType.TEMPLATE_BRIDGE2 or TemplateType.TEMPLATE_BRIDGE3 or TemplateType.TEMPLATE_BRIDGE4)
                                    continue;
                                OverlayClass.Create(game, (OverlayType)game.Random_Pick((int)OverlayType.OVERLAY_TIBERIUM1, (int)OverlayType.OVERLAY_TIBERIUM12), newcell.Cell_Number());
                                newcell.OverlayData = 1;
                                break;
                            }
                        }
                    }
                    tiberiumSpread[pick] = tiberiumSpread[tiberiumSpreadCount - 1];
                    tiberiumSpreadCount--;
                    if (tiberiumSpreadCount <= 0)
                        break;
                }
            }
            tiberiumSpreadCount = 0;
        }
    }

    /// <summary>Cell_Region: the 4x4 threat region a cell is in (regions are padded by one all round).</summary>
    public int Cell_Region(int cell) =>
        ((TdMath.CellX(cell) / (int)TdDefines.REGION_WIDTH) + 1) + (((TdMath.CellY(cell) / (int)TdDefines.REGION_HEIGHT) + 1) * (int)TdDefines.MAP_REGION_WIDTH);

    /// <summary>Cell_Threat: the house's threat value for the cell's region (1 if none but visible).</summary>
    public int Cell_Threat(int cell, HousesType house)
    {
        int threat = game.HouseFor(house).Regions[Cell_Region(this[cell].Cell_Number())].Threat_Value();
        if (threat == 0 && this[cell].Is_Visible(house))
            threat = 1;
        return threat;
    }

    /// <summary>Place_Random_Crate: a wooden crate in a random clear cell (100 tries).</summary>
    public bool Place_Random_Crate()
    {
        int old = game.ScenarioInit;
        game.ScenarioInit = 0;
        for (int index = 0; index < 100; index++)
        {
            int x = game.Random_Pick(0, MapCellWidth - 1);
            int y = game.Random_Pick(0, MapCellHeight - 1);
            int cell = TdMath.XyCell(MapCellX + x, MapCellY + y);
            var ptr = this[cell];
            if (ptr.Is_Generally_Clear() && ptr.Overlay == OverlayType.OVERLAY_NONE)
            {
                ptr.Overlay = OverlayType.OVERLAY_WOOD_CRATE;
                ptr.OverlayData = 0;
                ptr.Redraw_Objects();
                game.ScenarioInit = old;
                return true;
            }
        }
        game.ScenarioInit = old;
        return false;
    }

    static readonly int[] CloseOffsets = [0, -1, 1, -MCW, MCW, MCW - 1, MCW + 1, -(MCW - 1), -(MCW + 1)];

    /// <summary>Close_Object: the (visible) object nearest a coordinate, within about 3/4 of a cell.</summary>
    public ObjectClass? Close_Object(uint coord)
    {
        ObjectClass? obj = null;
        int distance = 0;
        int cell = TdMath.CoordCell(coord);

        foreach (int offset in CloseOffsets)
        {
            int newcell = cell + offset;
            if (!In_Radar(newcell))
                continue;
            for (ObjectClass? o = this[newcell].Cell_Occupier(); o != null; o = o.Next)
            {
                if (!o.Is_Techno() || !((TechnoClass)o).Is_Cloaked(game.PlayerPtr))
                {
                    int d;
                    if (o.What_Am_I() == RTTIType.RTTI_BUILDING)
                    {
                        d = TdMath.Distance(coord, TdMath.CellCoord(newcell));
                        if (d > 0x00C0)
                            d = -1;
                    }
                    else
                    {
                        d = TdMath.Distance(coord, o.Center_Coord());
                    }
                    if (d >= 0 && (obj == null || d < distance))
                    {
                        distance = d;
                        obj = o;
                    }
                }
            }
        }

        for (int index = 0; index < game.Aircraft.Count; index++)
        {
            var aircraft = game.Aircraft.Ptr(index);
            if (aircraft.In_Which_Layer() != LayerType.LAYER_GROUND && !aircraft.Is_Cloaked(game.PlayerPtr))
            {
                int d = TdMath.Distance(coord, TdMath.CoordAdd(aircraft.Center_Coord(), TdMath.XyCoord(0, -TdMath.PixelToLepton(aircraft.Altitude))));
                if (d >= 0 && (obj == null || d < distance))
                {
                    distance = d;
                    obj = aircraft;
                }
            }
        }

        if (obj != null && distance > 0xC0)
            obj = null;
        return obj;
    }

    /// <summary>Passes_Proximity_Check (DISPLAY.CPP, the remaster's RA-AI version): a building placed
    /// with this offset list at the cell touches one of the house's buildings, bibs or walls.</summary>
    public bool Passes_Proximity_Check(ObjectTypeClass? obj, HousesType house, short[] list, int trycell)
    {
        if (game.DebugMap)
            return true;
        if (obj == null || obj.What_Am_I() != RTTIType.RTTI_BUILDINGTYPE)
            return true;

        foreach (short offset in list)
        {
            if (offset == TdMath.RefreshEol)
                break;
            int cell = trycell + offset;
            for (var facing = FacingType.FACING_N; facing < FacingType.FACING_COUNT; facing++)
            {
                int newcell = TdMath.AdjacentCellOf(cell, facing);
                if (!In_Radar(cell))
                    return false;
                var techno = this[newcell].Cell_Techno();
                // Friendly walls and bibs count, even with no building there.
                if (this[newcell].Owner == house)
                    return true;
                if (techno != null && techno.What_Am_I() == RTTIType.RTTI_BUILDING && techno.House.Class.House == house)
                    return true;
            }
        }
        return false;
    }

    /// <summary>Nearby_Location (MAP.CPP, in the remaster's RA-AI section, used by unconditional code):
    /// one of the nearest (up to ten) clear cells, in rings round the cell; which one depends on the frame.</summary>
    public int Nearby_Location(int cell)
    {
        var topten = new int[10];
        int count = 0;
        int xx = TdMath.CellX(cell);
        int yy = TdMath.CellY(cell);
        int left = MapCellX, right = MapCellX + MapCellWidth - 1, top = MapCellY, bottom = MapCellY + MapCellHeight - 1;

        bool Try(int x, int y)
        {
            int newcell = TdMath.XyCell(x, y);
            if (In_Radar(newcell) && this[newcell].Is_Clear_To_Move(false, false))
                topten[count++] = newcell;
            return count == topten.Length;
        }

        for (int radius = 0; radius < MCW; radius++)
        {
            bool full = false;
            for (int x = xx - radius; x <= xx + radius && !full; x++)
            {
                if (x >= left && x <= right)
                {
                    if (yy - radius >= top && Try(x, yy - radius))
                        full = true;
                    else if (count == topten.Length)
                        full = true;
                    if (!full && yy + radius <= bottom && Try(x, yy + radius))
                        full = true;
                    else if (count == topten.Length)
                        full = true;
                }
            }
            if (count == topten.Length)
                break;

            for (int y = yy - radius; y <= yy + radius && !full; y++)
            {
                if (y >= top && y <= bottom)
                {
                    if (xx - radius >= left && Try(xx - radius, y))
                        full = true;
                    else if (count == topten.Length)
                        full = true;
                    if (!full && xx + radius <= right && Try(xx + radius, y))
                        full = true;
                    else if (count == topten.Length)
                        full = true;
                }
            }
            if (count > 0)
                break;
        }

        if (count > 0)
            return topten[(int)(game.Frame % count)];
        return 0;
    }

    // ---- DisplayClass (logic parts) ----

    public void Submit(ObjectClass? obj, LayerType layer)
    {
        if (obj != null)
            Layer[(int)layer].Submit(obj, layer == LayerType.LAYER_GROUND);
    }

    public void Remove(ObjectClass? obj, LayerType layer)
    {
        if (obj != null && layer != LayerType.LAYER_NONE)
            Layer[(int)layer].Delete(obj);
    }

    /// <summary>MapClass::Detach: nothing outside the scenario editor.</summary>
    public void Detach(ObjectClass obj) { }

    /// <summary>Refresh_Cells: the listed cells' contents are redrawn.</summary>
    public void Refresh_Cells(int cell, short[] list)
    {
        int start = list.Length > 0 && list[0] == TdDefines.REFRESH_SIDEBAR ? 1 : 0;
        for (int i = start; i < list.Length && list[i] != TdMath.RefreshEol; i++)
        {
            int newcell = cell + list[i];
            if (In_Radar(newcell))
                this[newcell].Redraw_Objects();
        }
    }

    /// <summary>Map_Cell: a cell becomes mapped and visible for a house (and its allies); unmapped
    /// neighbours that would be fully shrouded edges are mapped too.</summary>
    public bool Map_Cell(int cell, HouseClass? house, bool andForAllies = false)
    {
        if (house == null || cell >= TdDefines.MAP_CELL_TOTAL)
            return false;
        if (!house.IsHuman && (!game.ShareAllyVisibility || !andForAllies))
            return false;

        if (game.ShareAllyVisibility && andForAllies)
        {
            HousesType first = game.GameToPlay == GameType.GAME_NORMAL ? HousesType.HOUSE_FIRST : HousesType.HOUSE_MULTI1;
            for (var houseType = first; houseType < HousesType.HOUSE_COUNT; houseType++)
            {
                var hptr = game.HouseOrNull(houseType);
                if (hptr != null && hptr.IsActive && hptr != house && house.Is_Ally(hptr))
                    Map_Cell(cell, hptr, false);
            }
        }

        if (this[cell].Is_Mapped(house))
            return false;

        this[cell].Set_Mapped(house);
        this[cell].Set_Visible(house);
        this[cell].Redraw_Objects();

        int xx = TdMath.CellX(cell);
        for (int dir = 0; dir < (int)FacingType.FACING_COUNT; dir++)
        {
            int c = TdMath.AdjacentCellOf(cell, (FacingType)dir);
            if ((uint)c >= TdDefines.MAP_CELL_TOTAL)
                continue;
            if (Math.Abs(TdMath.CellX(c) - xx) > 1)
                continue;
            if (c != cell && !this[c].Is_Mapped(house))
            {
                int shadow = Cell_Shadow(c, house);
                if (shadow == -1)
                {
                    Map_Cell(c, house, false);
                }
                else if (shadow != -2)
                {
                    this[c].Set_Visible(house);
                    this[c].Redraw_Objects();
                }
            }
        }

        this[cell].Cell_Techno()?.Revealed(house);
        return true;
    }

    static readonly sbyte[] CardShadow = [-2, 0, 1, 2, 3, -1, 4, -1, 5, 6, -1, -1, 7, -1, -1, -1];
    static readonly sbyte[] DiagShadow = [-2, 8, 9, -1, 10, -1, -1, -1, 11, -1, -1, -1, -1, -1, -1, -1];

    /// <summary>Cell_Shadow: the shroud edge frame for an unmapped cell from its mapped neighbours
    /// (-1 = should be mapped, -2 = no edge).</summary>
    public int Cell_Shadow(int cell, HouseClass? house)
    {
        if (house == null)
            return -1;
        if ((uint)(TdMath.CellX(cell) - 1) >= TdMath.MapCellW - 2)
            return -2;
        if ((uint)(TdMath.CellY(cell) - 1) >= TdMath.MapCellH - 2)
            return -2;

        int value = -1;
        if (!this[cell].Is_Mapped(house))
        {
            int index = 0;
            int ptr = cell - 1;
            if (this[ptr].Is_Mapped(house)) index |= 0x08;
            ptr += MCW + 1;
            if (this[ptr].Is_Mapped(house)) index |= 0x04;
            ptr -= MCW - 1;
            if (this[ptr].Is_Mapped(house)) index |= 0x02;
            ptr -= MCW + 1;
            if (this[ptr].Is_Mapped(house)) index |= 0x01;
            value = CardShadow[index];

            if (value == -2)
            {
                index = 0;
                ptr--;
                if (this[ptr].Is_Mapped(house)) index |= 0x08;
                ptr += MCW * 2;
                if (this[ptr].Is_Mapped(house)) index |= 0x04;
                ptr += 2;
                if (this[ptr].Is_Mapped(house)) index |= 0x02;
                ptr -= MCW * 2;
                if (this[ptr].Is_Mapped(house)) index |= 0x01;
                value = DiagShadow[index];
            }
        }
        return value;
    }

    /// <summary>Pixel_To_Coord: a screen pixel to a map coordinate (0 if outside the view).</summary>
    public uint Pixel_To_Coord(int x, int y)
    {
        x = TdMath.PixelToLepton(x - TacPixelX);
        y = TdMath.PixelToLepton(y - TacPixelY);
        if (IgnoreViewConstraints || (x < TacLeptonWidth && y < TacLeptonHeight))
            return TdMath.CoordAdd(TacticalCoord, TdMath.XyCoord(x, y));
        return 0;
    }

    /// <summary>Calculated_Cell: where reinforcements arrive from a source (an edge, the air, the beach...).</summary>
    public int Calculated_Cell(SourceType dir, HousesType house)
    {
        int cell = 0;
        while (cell == 0)
        {
            int x, y, index;
            switch (dir)
            {
                case SourceType.SOURCE_SHIPPING:
                    for (y = 0; y < MapCellHeight; y++)
                    {
                        for (x = 0; x < MapCellWidth; x++)
                            if (this[TdMath.XyCell(MapCellX + x, MapCellY + y)].Land_Type() != LandType.LAND_WATER)
                                break;
                        if (x == MapCellWidth)
                            return TdMath.XyCell(MapCellX + MapCellWidth, MapCellY + y);
                    }
                    return 0;

                case SourceType.SOURCE_NORTH:
                    index = game.Random_Pick(1, MapCellWidth);
                    for (x = 0; x < MapCellWidth; x++)
                    {
                        cell = TdMath.XyCell(MapCellX + ((x + index) % MapCellWidth), MapCellY - 1);
                        if (this[cell].Is_Generally_Clear() && this[cell + MCW].Is_Generally_Clear())
                            break;
                    }
                    if (x == MapCellWidth)
                        return 0;
                    break;

                case SourceType.SOURCE_EAST:
                    index = game.Random_Pick(1, MapCellHeight);
                    for (y = 0; y < MapCellHeight; y++)
                    {
                        cell = TdMath.XyCell(MapCellX + MapCellWidth, MapCellY + ((y + index) % MapCellHeight));
                        if (this[cell].Is_Generally_Clear() && this[cell - 1].Is_Generally_Clear())
                            break;
                    }
                    if (y == MapCellHeight)
                        return 0;
                    break;

                case SourceType.SOURCE_SOUTH:
                    index = game.Random_Pick(1, MapCellWidth);
                    for (x = 0; x < MapCellWidth; x++)
                    {
                        cell = TdMath.XyCell(MapCellX + ((x + index) % MapCellWidth), MapCellY + MapCellHeight);
                        if (this[cell].Is_Generally_Clear() && this[cell - MCW].Is_Generally_Clear())
                            break;
                    }
                    if (x == MapCellWidth)
                        return 0;
                    break;

                case SourceType.SOURCE_WEST:
                    index = game.Random_Pick(1, MapCellHeight);
                    for (y = 0; y < MapCellHeight; y++)
                    {
                        cell = TdMath.XyCell(MapCellX - 1, MapCellY + ((y + index) % MapCellHeight));
                        if (this[cell].Is_Generally_Clear() && this[cell + 1].Is_Generally_Clear())
                            break;
                    }
                    if (y == MapCellHeight)
                        return 0;
                    break;

                case SourceType.SOURCE_AIR:
                    cell = game.Waypoint[(int)WaypointType.WAYPT_REINF];
                    if (cell < 1)
                        return TdMath.CoordCell(TacticalCoord);
                    if (this[cell].Cell_Techno() != null)
                    {
                        for (int radius = 1; radius < 7; radius++)
                        {
                            int newcell = TdMath.CoordCell(game.Coord_Scatter(TdMath.CellCoord(cell), radius << 8, true));
                            if (In_Radar(newcell) && this[newcell].Cell_Techno() == null)
                            {
                                cell = newcell;
                                break;
                            }
                        }
                    }
                    break;

                case SourceType.SOURCE_VISIBLE:
                {
                    // XY_Cell(x + Random(width), y + Random(height)): the arguments right to left.
                    int ry = game.Random_Pick(0, TdMath.LeptonToCell(TacLeptonHeight) - 1);
                    int rx = game.Random_Pick(0, TdMath.LeptonToCell(TacLeptonWidth) - 1);
                    cell = TdMath.XyCell(TdMath.CellX(TdMath.CoordCell(TacticalCoord)) + rx, TdMath.CellY(TdMath.CoordCell(TacticalCoord)) + ry);
                    if (house == game.PlayerPtr.Class.House && !In_Radar(cell))
                        cell = 0;
                    break;
                }

                case SourceType.SOURCE_ENEMYBASE:
                case SourceType.SOURCE_HOMEBASE:
                    return 0;

                case SourceType.SOURCE_BEACH:
                    return Beach_Cell();

                case SourceType.SOURCE_OCEAN:
                    cell = TdMath.XyCell(game.Random_Pick(0, MapCellWidth - 2) + MapCellX, MapCellHeight + MapCellY);
                    break;

                default:
                    return 0;
            }

            cell &= 0x0FFF;
            if (cell != 0 && this[cell].Cell_Techno() != null)
                cell = 0;
        }
        return cell;
    }

    /// <summary>SOURCE_BEACH: a landing spot for the hovercraft on the south coast (from the middle
    /// of a run of usable beach cells; one row further down in the desert).</summary>
    int Beach_Cell()
    {
        var cellsFound = new List<int>();
        for (int x = 0; x < MapCellWidth; x++)
        {
            int newcell = 0;
            if (this[TdMath.XyCell(x + MapCellX, MapCellHeight + MapCellY)].Land_Type() != LandType.LAND_WATER)
                continue;
            if (this[TdMath.XyCell(x + MapCellX, MapCellHeight + MapCellY - 1)].Land_Type() != LandType.LAND_WATER)
                continue;

            for (int y = MapCellHeight; y >= 0; y--)
            {
                newcell = TdMath.XyCell(x + MapCellX, y + MapCellY);
                if (this[newcell].Cell_Techno() != null)
                    break;
                if (this[newcell].Land_Type() != LandType.LAND_WATER)
                    break;
            }

            LandType land = this[newcell].Land_Type();
            if ((land == LandType.LAND_BEACH || land == LandType.LAND_CLEAR || land == LandType.LAND_ROAD) &&
                this[newcell].Cell_Techno() == null && this[newcell].Cell_Terrain() == null &&
                this[newcell - MCW].Cell_Techno() == null && this[newcell - MCW].Cell_Terrain() == null &&
                this[newcell - (MCW * 2)].Cell_Terrain() == null && this[newcell - (MCW * 2)].Cell_Techno() == null)
            {
                cellsFound.Add(newcell);
                if (cellsFound.Count >= MCW)
                    break;
            }
        }

        var alternate = new List<int>();
        for (int index = 1; index < cellsFound.Count - 1; index++)
        {
            if (TdMath.CellX(cellsFound[index - 1]) + 1 == TdMath.CellX(cellsFound[index]) && TdMath.CellX(cellsFound[index + 1]) - 1 == TdMath.CellX(cellsFound[index]))
                alternate.Add(cellsFound[index]);
        }

        int cell = 0;
        if (alternate.Count != 0)
            cell = alternate.Count < 4 ? alternate[^1] : alternate[alternate.Count - (alternate.Count / 4)];
        else if (cellsFound.Count != 0)
            cell = cellsFound.Count < 4 ? cellsFound[^1] : cellsFound[cellsFound.Count - (cellsFound.Count / 4)];

        if (cell != 0 && Theater == TheaterType.THEATER_DESERT)
            cell += MCW;
        return cell;
    }

    /// <summary>In_View: the cell is within the tactical view.</summary>
    public bool In_View(int cell)
    {
        uint coord = TdMath.CellCoord(cell) & 0xFF00FF00;
        uint tcoord = TacticalCoord & 0xFF00FF00;
        if ((TdMath.CoordX(coord) - TdMath.CoordX(tcoord)) > TacLeptonWidth + 255)
            return false;
        if ((TdMath.CoordY(coord) - TdMath.CoordY(tcoord)) > TacLeptonHeight + 255)
            return false;
        return true;
    }

    /// <summary>Closest_Free_Spot: a free infantry spot in the coordinate's cell (or a fixed spot if off the map).</summary>
    public uint Closest_Free_Spot(uint coord, bool any = false)
    {
        if ((coord & 0xC000C000) != 0)
            return 0x00800080;
        return this[TdMath.CoordCell(coord)].Closest_Free_Spot(coord, any);
    }

    public bool Is_Spot_Free(uint coord) => this[TdMath.CoordCell(coord)].Is_Spot_Free(CellClass.Spot_Index(coord));

    // ---- Drawing bookkeeping (the client redraws everything each frame; these keep the original's calls) ----

    public bool Is_Cell_Flagged(int cell) => cellRedraw[cell & (TdDefines.MAP_CELL_TOTAL - 1)];
    public void Flag_Cell(int cell) => cellRedraw[cell & (TdDefines.MAP_CELL_TOTAL - 1)] = true;
    public void Clear_Cell_Flags() => cellRedraw.SetAll(false);
    public void Flag_To_Redraw(bool complete) { }
    public void Radar_Pixel(int cell) { }
}

using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// TemplateClass (TEMPLATE.CPP): a piece of terrain art (cliffs, roads, shores) placed on the map.
/// Like a smudge, it only lives long enough to write its icons into the cells (TType/TIcon), then
/// deletes itself. Needs the template's icon set (the art side's icon map), as the original needs
/// the image data: icons the set leaves empty are skipped.
/// </summary>
public sealed class TemplateClass : ObjectClass
{
    public readonly TemplateTypeClass Class;

    // While a placed template deletes itself, lifting it must not clear the icons it just wrote.
    static bool noup;

    TemplateClass(TdGame game, int slot, TemplateType type, int pos) : base(game)
    {
        game.Templates.Place(slot, this);
        Set_Active();

        Class = game.Types.Template(type)!;
        if (pos != -1)
            Unlimbo(TdMath.CellCoord(pos));
    }

    /// <summary>new TemplateClass(type, cell): placed at once when a cell is given.</summary>
    public static TemplateClass? Create(TdGame game, TemplateType type, int pos = -1)
    {
        int slot = game.Templates.Allocate();
        if (slot < 0)
            return null;
        return new TemplateClass(game, slot, type, pos);
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
        Game.Templates.Free(this);
    }

    public override RTTIType What_Am_I() => RTTIType.RTTI_TEMPLATE;
    public override ObjectTypeClass Class_Of() => Class;
    public uint As_Target() => Target.Build_Target(KindType.KIND_TEMPLATE, Game.Templates.ID(this));

    /// <summary>Mark: down writes the template's icons into the cells (TEMPLATE_CLEAR1 clears them)
    /// and deletes the object; up lifts icons that are still this template's.</summary>
    public override bool Mark(MarkType mark = MarkType.MARK_CHANGE)
    {
        sbyte[]? map = Game.Art?.Template_Icon_Map(Class);
        if (map != null && base.Mark(mark))
        {
            for (int y = 0; y < Class.Height; y++)
            {
                for (int x = 0; x < Class.Width; x++)
                {
                    int cell = TdMath.CoordCell(Coord) + y * TdMath.MapCellW + x;
                    if (!Game.Map.In_Radar(cell))
                        continue;

                    var cellptr = Game.Map[cell];
                    int number = y * Class.Width + x;

                    // Only logical icons that map to a real icon do anything.
                    bool real = number < map.Length && map[number] != -1;
                    if (!real)
                        continue;

                    if (mark == MarkType.MARK_UP && !noup)
                    {
                        if (cellptr.TType == Class.Type && cellptr.TIcon == number)
                        {
                            cellptr.TType = TemplateType.TEMPLATE_NONE;
                            cellptr.TIcon = 0;
                        }
                    }

                    if (mark == MarkType.MARK_DOWN)
                    {
                        if (Class.Type == TemplateType.TEMPLATE_CLEAR1)
                        {
                            cellptr.TType = TemplateType.TEMPLATE_NONE;
                            cellptr.TIcon = 0;
                        }
                        else
                        {
                            cellptr.TType = Class.Type;
                            cellptr.TIcon = (byte)number;
                        }
                    }
                    cellptr.Redraw_Objects();
                    cellptr.Recalc_Attributes();
                }
            }

            if (mark == MarkType.MARK_DOWN)
            {
                noup = true;
                Delete_This();
                noup = false;
            }
            return true;
        }
        return false;
    }

    /// <summary>Read_INI: the scenario's [TEMPLATE] — cell=type (the binary map replaced it).</summary>
    public static void Read_INI(TdGame game, Data.WwProfile ini)
    {
        string buf = "";
        foreach (string key in ini.Entries("TEMPLATE"))
        {
            int cell = Data.WwProfile.Atoi(key);
            buf = ini.Get_String("TEMPLATE", key, null, 128 - 1);
            var temp = game.Types.Template_From_Name(new Data.Strtok(buf).Next(",\r\n"));
            if (temp != TemplateType.TEMPLATE_NONE)
                Create(game, temp, cell);
        }
    }
}

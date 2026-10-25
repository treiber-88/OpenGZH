using OpenGZH.TiberianDawn.Logic.Objects;

namespace OpenGZH.TiberianDawn.Logic.Scenario;

/// <summary>BaseNodeClass: one planned building of the computer's base.</summary>
public sealed class BaseNodeClass
{
    public StructType Type;
    public uint Coord;
}

/// <summary>
/// BaseClass (BASE.CPP): the computer's base plan from the scenario ([Base]) — which buildings go
/// where; the computer rebuilds whatever is missing, in order.
/// </summary>
public sealed class BaseClass(TdGame game)
{
    public readonly List<BaseNodeClass> Nodes = new();
    public HousesType House;

    public void Init() => Nodes.Clear();

    /// <summary>Read_INI: [Base] Count, then 000=TYPE,coordinate...; the plan belongs to the player's enemy.</summary>
    public void Read_INI(Data.WwProfile ini)
    {
        string buf = ini.Get_String("BASIC", "Player", "GoodGuy", 20);
        House = game.Types.House_From_Name(buf) == HousesType.HOUSE_GOOD ? HousesType.HOUSE_BAD : HousesType.HOUSE_GOOD;

        int count = ini.Get_Int("Base", "Count", 0);
        string value = "";
        for (int i = 0; i < count; i++)
        {
            value = ini.Get_String("Base", i.ToString("000"), value, 128 - 1);
            var tok = new Data.Strtok(value);
            var node = new BaseNodeClass { Type = game.Types.Building_From_Name(tok.Next(",")) };
            node.Coord = (uint)Data.WwProfile.Atoi(tok.Next(","));
            Nodes.Add(node);
        }
    }

    public bool Is_Built(int index) => Get_Building(index) != null;

    /// <summary>Get_Building: the building standing where the node says (found among the cell's occupier and overlappers).</summary>
    public BuildingClass? Get_Building(int index)
    {
        var node = Nodes[index];
        var cell = game.Map[TdMath.CoordCell(node.Coord)];
        ObjectClass?[] obj = [cell.Cell_Building(), cell.Overlapper[0], cell.Overlapper[1], cell.Overlapper[2]];
        foreach (var o in obj)
            if (o is BuildingClass b && o.Coord == node.Coord && b.Class.Type == node.Type)
                return b;
        return null;
    }

    public bool Is_Node(BuildingClass obj) => Get_Node(obj) != null;

    public BaseNodeClass? Get_Node(BuildingClass obj)
    {
        foreach (var node in Nodes)
            if (obj.Class.Type == node.Type && obj.Coord == node.Coord)
                return node;
        return null;
    }

    public BaseNodeClass? Get_Node(int index) => Nodes[index];

    /// <summary>Next_Buildable: the first node (of a type, if given) that isn't built.</summary>
    public BaseNodeClass? Next_Buildable(StructType type = StructType.STRUCT_NONE)
    {
        for (int i = 0; i < Nodes.Count; i++)
        {
            if ((type == StructType.STRUCT_NONE || Nodes[i].Type == type) && !Is_Built(i))
                return Nodes[i];
        }
        return null;
    }
}

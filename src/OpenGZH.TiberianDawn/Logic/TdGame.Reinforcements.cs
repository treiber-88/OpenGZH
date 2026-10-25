using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Scenario;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic;

// REINF.CPP: bringing reinforcements onto the map — by an edge, by air, by sea (the hovercraft),
// or along the shipping lane (the gunboat).
public sealed partial class TdGame
{
    /// <summary>Do_Reinforcements: create the team type's objects (and a team, if it has orders)
    /// and bring them on where their transport, or the house's edge, says.</summary>
    public bool Do_Reinforcements(TeamTypeClass? teamtype)
    {
        if (teamtype == null || teamtype.ClassCount == 0)
            return false;

        var owner = HouseFor(teamtype.House);

        // A team only if it has something to do.
        TeamClass? team = null;
        if (teamtype.MissionCount != 0)
        {
            team = TeamClass.Create(this, teamtype, HouseOrNull(teamtype.House));
            if (team == null)
                return false;
            team.Force_Active();
        }

        // A transport carrying others is only a loaner; one alone is the reinforcement itself.
        bool okvoice = true;
        bool airtransport = false;
        bool watertransport = false;
        bool onlytransport = true;
        int index;
        for (index = 0; index < teamtype.ClassCount; index++)
        {
            var c = teamtype.Class[index]!;
            if (c.IsTransporter || c.What_Am_I() == RTTIType.RTTI_AIRCRAFTTYPE)
            {
                if (c.What_Am_I() == RTTIType.RTTI_AIRCRAFTTYPE)
                    airtransport = true;
                else
                    watertransport = ((UnitTypeClass)c).Type == UnitType.UNIT_HOVER;
            }
            else
            {
                onlytransport = false;
            }
        }

        SourceType source;
        if (airtransport)
            source = SourceType.SOURCE_AIR;
        else if (watertransport)
            source = SourceType.SOURCE_BEACH;
        else if (teamtype.Class[0] is UnitTypeClass { Type: UnitType.UNIT_GUNBOAT })
            source = SourceType.SOURCE_SHIPPING;   // the gunboat always comes along the shipping lane
        else
            source = owner.Edge;

        if (source == SourceType.SOURCE_NONE)
        {
            team?.Delete();
            return false;
        }

        // Make the objects: transports in one list, everything else in another.
        FootClass? transport = null;
        FootClass? obj = null;
        for (index = 0; index < teamtype.ClassCount; index++)
        {
            var tclass = teamtype.Class[index]!;
            for (int sub = 0; sub < teamtype.DesiredNum[index]; sub++)
            {
                ScenarioInit++;
                var temp = Create_One_Of(tclass, HouseOrNull(teamtype.House)) as FootClass;
                ScenarioInit--;
                if (temp == null)
                    continue;

                // Everything joins the team except the hover lander.
                if (team != null && temp is not UnitClass { Class.Type: UnitType.UNIT_HOVER })
                {
                    ScenarioInit++;
                    team.Add(temp);
                    ScenarioInit--;
                }

                if (tclass.IsTransporter && (!airtransport || tclass.What_Am_I() == RTTIType.RTTI_AIRCRAFTTYPE))
                {
                    if (!onlytransport && temp.What_Am_I() != RTTIType.RTTI_UNIT)
                        temp.IsALoaner = true;
                    temp.Next = transport;
                    transport = temp;
                }
                else
                {
                    // A-10s are never the player's to control.
                    if (temp is AircraftClass { Class.Type: AircraftType.AIRCRAFT_A10 })
                        temp.IsALoaner = true;
                    temp.Next = obj;
                    obj = temp;
                }
            }
        }

        // Nothing could be made (probably the object limit).
        if (obj == null && transport == null)
        {
            team?.Delete();
            return false;
        }

        // The passengers go inside the transport.
        if (transport != null)
        {
            if (obj != null)
            {
                // A cargo plane announces when it unloads, not now.
                if (transport is AircraftClass { Class.Type: AircraftType.AIRCRAFT_CARGO })
                    okvoice = false;
                transport.Cargo.Attach(obj);
            }
            obj = transport;
        }

        bool placed = false;
        int cell = 0;
        switch (source)
        {
            case SourceType.SOURCE_SHIPPING:
                cell = Map.Calculated_Cell(source, teamtype.House);
                obj!.IsALoaner = true;
                if (obj.Unlimbo(TdMath.CellCoord(cell), (byte)DirType.DIR_W))
                {
                    obj.Assign_Mission(MissionType.MISSION_GUARD);
                    obj.Assign_Destination(Target.As_Target(TdMath.XyCell(Map.MapCellX - 1, TdMath.CellY(TdMath.CoordCell(obj.Coord)))));
                }
                else
                {
                    team?.Delete();
                    obj.Delete();
                    return false;
                }
                break;

            case SourceType.SOURCE_NORTH:
            case SourceType.SOURCE_SOUTH:
            case SourceType.SOURCE_EAST:
            case SourceType.SOURCE_WEST:
            {
                var eface = (FacingType)((int)source << 1);   // the facing to enter the map
                if (airtransport)
                    ScenarioInit++;
                cell = Map.Calculated_Cell(source, teamtype.House);
                if (airtransport)
                    ScenarioInit--;
                int newcell = cell;
                var o = (FootClass?)obj!.Next;
                obj.Next = null;
                bool ok = true;
                while (newcell > 0 && obj != null)
                {
                    byte desiredfacing = (byte)((int)eface << 5);   // Facing_Dir
                    if (obj.What_Am_I() == RTTIType.RTTI_AIRCRAFT)
                        desiredfacing = (byte)Random_Pick((int)DirType.DIR_N, (int)DirType.DIR_MAX);

                    if (ok && obj.Unlimbo(TdMath.CellCoord(newcell), desiredfacing))
                    {
                        // A team member guards; the team gives the real orders.
                        if (obj.What_Am_I() == RTTIType.RTTI_AIRCRAFT)
                        {
                            ok = false;
                        }
                        else
                        {
                            if (team != null)
                            {
                                obj.Assign_Mission(MissionType.MISSION_GUARD);
                            }
                            else
                            {
                                obj.Assign_Mission(MissionType.MISSION_MOVE);
                                obj.Assign_Destination(Target.As_Target(TdMath.AdjacentCellOf(newcell, eface)));
                            }
                            obj.Commence();
                        }
                    }
                    else
                    {
                        ok = true;
                        // Try a neighbouring cell off the map that it can enter; failing that, give up.
                        FacingType adj;
                        for (adj = FacingType.FACING_N; adj < FacingType.FACING_COUNT; adj++)
                        {
                            int trycell = TdMath.AdjacentCellOf(newcell, adj);
                            if (!Map.In_Radar(trycell) && obj.Can_Enter_Cell(trycell, adj) == MoveType.MOVE_OK)
                            {
                                newcell = trycell;
                                break;
                            }
                        }
                        if (adj < FacingType.FACING_COUNT)
                            continue;
                        newcell = -1;
                    }

                    obj = o;
                    if (obj != null)
                    {
                        o = (FootClass?)obj.Next;
                        obj.Next = null;
                    }
                }

                // Whatever could not be placed is deleted.
                while (o != null)
                {
                    var old = o;
                    o = (FootClass?)o.Next;
                    old.Next = null;
                    old.Delete();
                }
                break;
            }

            // Aircraft come in as separate groups of one.
            case SourceType.SOURCE_AIR:
            {
                var thisone = (AircraftClass?)obj;
                uint target = Target.TARGET_NONE;
                while (thisone != null)
                {
                    var next = (AircraftClass?)thisone.Next;

                    // Cargo planes line up with the airstrip they will unload at.
                    long reinforcementDelay = -1;
                    ScenarioInit++;
                    uint newcoord = TdMath.CellCoord(Map.Calculated_Cell(owner.Edge, teamtype.House));
                    ScenarioInit--;
                    if (thisone.Class.Type == AircraftType.AIRCRAFT_CARGO)
                    {
                        var building = thisone.Find_Docking_Bay(StructType.STRUCT_AIRSTRIP, false);
                        if (building != null)
                        {
                            uint dockingCoord = building.Docking_Coord();
                            int borderX = TdMath.CellToLepton(Map.MapCellX + Map.MapCellWidth) | 0x80;
                            if (Special.ModernBalance)
                            {
                                // Five seconds to the airstrip at Normal speed (45 ms per tick).
                                int speed = (int)Types.Aircraft(AircraftType.AIRCRAFT_CARGO).MaxSpeed;
                                int spawnX = TdMath.CoordX(dockingCoord) + ((speed * 1000 * 75) / (45 * 10));
                                if (spawnX > borderX)
                                {
                                    reinforcementDelay = (spawnX - borderX) / speed;
                                    spawnX = borderX;
                                }
                                newcoord = TdMath.XyCoord(spawnX, TdMath.CoordY(dockingCoord));
                            }
                            else
                            {
                                newcoord = TdMath.XyCoord(borderX, TdMath.CoordY(dockingCoord));
                            }
                            if (teamtype.MissionCount != 0)
                                teamtype.MissionList[0].Argument = (int)building.As_Target();
                        }
                    }

                    thisone.Next = null;
                    ScenarioInit++;
                    placed = thisone.Unlimbo(newcoord, (byte)DirType.DIR_W);
                    if (Special.ModernBalance && reinforcementDelay >= 0)
                        thisone.Set_Reinforcement_Delay(reinforcementDelay);
                    ScenarioInit--;

                    if (placed)
                    {
                        if (team == null)
                        {
                            if (thisone.Class.IsFixedWing)
                            {
                                thisone.Assign_Mission(MissionType.MISSION_HUNT);
                                if (thisone.Class.Type == AircraftType.AIRCRAFT_A10)
                                {
                                    // A group of A-10s goes after the same first target.
                                    if (target == Target.TARGET_NONE)
                                        target = thisone.Greatest_Threat(ThreatType.THREAT_NORMAL);
                                    thisone.Assign_Target(target);
                                }
                            }
                            else
                            {
                                if (thisone.Class.IsTransporter && thisone.Cargo.Is_Something_Attached())
                                    thisone.Assign_Mission(MissionType.MISSION_UNLOAD);
                                else
                                    thisone.Assign_Mission(MissionType.MISSION_MOVE);
                                thisone.Assign_Destination(Target.As_Target(Map.Calculated_Cell(source, teamtype.House)));
                            }
                            thisone.Commence();
                        }
                    }
                    else
                    {
                        thisone.Delete();
                    }
                    thisone = next;
                }

                if (!placed)
                    team?.Delete();

                // No phantom teams or team types when the reinforcement could not be made.
                if (GameToPlay == GameType.GAME_NORMAL && !placed)
                    return false;
                break;
            }

            case SourceType.SOURCE_OCEAN:
            case SourceType.SOURCE_BEACH:
                cell = Map.Calculated_Cell(SourceType.SOURCE_BEACH, teamtype.House);
                if (cell != 0)
                {
                    int edge = TdMath.XyCell(TdMath.CellX(cell), Map.MapCellY + Map.MapCellHeight);
                    placed = obj!.Unlimbo(TdMath.CellCoord(edge), (byte)DirType.DIR_N);
                    if (placed)
                    {
                        if (team == null)
                        {
                            obj.IsLocked = false;
                            obj.Assign_Mission(MissionType.MISSION_UNLOAD);
                            obj.Commence();
                            obj.Assign_Destination(Target.As_Target(cell));
                        }
                    }
                    else
                    {
                        team?.Delete();
                        obj.Delete();
                        return false;
                    }
                }
                break;
        }

        if (okvoice && teamtype.House == PlayerPtr.Class.House)
            Speak(VoxType.VOX_REINFORCEMENTS, null, cell != 0 ? TdMath.CellCoord(cell) : 0);
        return true;
    }

    /// <summary>Create_Special_Reinforcement: a one-off team of one object (and maybe a transport),
    /// such as the free harvester or a cargo plane delivery.</summary>
    public bool Create_Special_Reinforcement(HouseClass? house, TechnoTypeClass? type, TechnoTypeClass? another,
        TeamMissionType mission = TeamMissionType.TMISSION_NONE, int argument = 0)
    {
        if (house == null || type == null)
            return false;

        var team = TeamTypeClass.Create(this);
        if (team == null)
            return false;

        // A ground reinforcement from the south whose edge is all water comes by hover lander.
        if (another == null && (type.What_Am_I() == RTTIType.RTTI_UNITTYPE || type.What_Am_I() == RTTIType.RTTI_INFANTRYTYPE))
        {
            if (house.Edge == SourceType.SOURCE_SOUTH)
            {
                bool found = false;
                for (int index = Map.MapCellX; index < Map.MapCellX + Map.MapCellWidth - 1; index++)
                {
                    int cell = TdMath.XyCell(index, Map.MapCellY + Map.MapCellHeight);
                    if (Map[cell].Is_Generally_Clear() && Map[cell - (int)TdDefines.MAP_CELL_W].Is_Generally_Clear())
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    mission = TeamMissionType.TMISSION_NONE;
                    another = Types.Unit(UnitType.UNIT_HOVER);
                }
            }

            if (another == null)
            {
                mission = TeamMissionType.TMISSION_MOVECELL;
                argument = Map.Calculated_Cell(SourceType.SOURCE_AIR, house.Class.House);
            }
        }

        // Without orders it would just sit at the map edge.
        if (another == null && mission == TeamMissionType.TMISSION_NONE)
        {
            mission = TeamMissionType.TMISSION_MOVECELL;
            argument = Map.Calculated_Cell(SourceType.SOURCE_AIR, house.Class.House);
        }

        team.Set_Name("TEMP");
        team.IsReinforcable = false;
        team.IsTransient = true;
        team.ClassCount = 1;
        team.Class[0] = type;
        team.DesiredNum[0] = 1;
        team.MissionCount = 1;
        if (mission == TeamMissionType.TMISSION_NONE)
        {
            if (another != null && (another.What_Am_I() != RTTIType.RTTI_UNITTYPE || ((UnitTypeClass)another).Type != UnitType.UNIT_HOVER))
                team.MissionList[0] = new TeamMissionStruct { Mission = TeamMissionType.TMISSION_UNLOAD, Argument = (int)WaypointType.WAYPT_REINF };
        }
        else
        {
            team.MissionList[0] = new TeamMissionStruct { Mission = mission, Argument = argument };
        }
        team.House = house.Class.House;
        if (another != null)
        {
            team.ClassCount++;
            team.Class[1] = another;
            team.DesiredNum[1] = 1;
        }

        bool ok = Do_Reinforcements(team);
        if (!ok && GameToPlay == GameType.GAME_NORMAL)
            team.Delete();
        return ok;
    }

    /// <summary>Create_Air_Reinforcement: a number of aircraft in from the house's edge with orders
    /// (the air strike). Returns how many arrived — one fewer than made when one could not be
    /// placed, as the original.</summary>
    public int Create_Air_Reinforcement(HouseClass house, AircraftType air, int number, MissionType mission, uint tarcom, uint navcom)
    {
        var type = Types.Aircraft(air);
        int sub;
        for (sub = 0; sub < number; sub++)
        {
            ScenarioInit++;
            var obj = Create_One_Of(type, house) as TechnoClass;
            ScenarioInit--;
            if (obj == null)
                return sub;

            obj.IsALoaner = true;

            var source = house.Edge;
            switch (source)
            {
                case SourceType.SOURCE_NORTH:
                case SourceType.SOURCE_EAST:
                case SourceType.SOURCE_SOUTH:
                case SourceType.SOURCE_WEST:
                    break;
                default:
                    source = SourceType.SOURCE_NORTH;
                    break;
            }
            int newcell = Map.Calculated_Cell(source, house.Class.House);

            ScenarioInit++;
            bool placed = obj.Unlimbo(TdMath.CellCoord(newcell), (byte)DirType.DIR_N);
            ScenarioInit--;
            if (placed)
            {
                obj.Assign_Mission(mission);
                if (navcom != Target.TARGET_NONE)
                    obj.Assign_Destination(navcom);
                if (tarcom != Target.TARGET_NONE)
                    obj.Assign_Target(tarcom);
                obj.Commence();
            }
            else
            {
                obj.Delete();
                sub--;
                return sub;
            }
        }
        return sub;
    }
}

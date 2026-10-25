using OpenGZH.TiberianDawn.Logic.Scenario;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// FootClass (FOOT.CPP): everything that moves under its own power (vehicles, infantry, aircraft):
/// a destination (NavCom), a short planned path, team membership and the movement missions.
/// The path finder (FINDPATH.CPP) is in FootClass.FindPath.cs.
/// </summary>
public abstract partial class FootClass : TechnoClass
{
    public const int PATH_DELAY = 15;
    public const int PATH_RETRY = 10;

    public bool IsInitiated, IsNewNavCom, IsPlanningToLook, IsDeploying, IsFiring, IsRotating, IsDriving, IsUnloading;
    byte speed;
    public uint NavCom = Target.TARGET_NONE;
    public uint SuspendedNavCom = Target.TARGET_NONE;
    public TeamClass? Team;
    public byte Group;
    public FootClass? Member;
    public readonly FacingType[] Path = new FacingType[TdDefines.CONQUER_PATH_MAX];
    public readonly CountDownTimer PathDelay;
    public int TryTryAgain;
    public readonly CountDownTimer BaseAttackTimer;
    uint headToCoord;

    /// <summary>The speed this object is set to (0..255, a fraction of its maximum).</summary>
    public byte Speed => speed;

    protected FootClass(TdGame game, HousesType house) : base(game, house)
    {
        PathDelay = new CountDownTimer(game);
        BaseAttackTimer = new CountDownTimer(game);
        Member = null;
        Team = null;
        Path[0] = FacingType.FACING_NONE;
        NavCom = Target.TARGET_NONE;
        SuspendedNavCom = Target.TARGET_NONE;
        headToCoord = 0;
        PathDelay.Set(0);
        Group = unchecked((byte)-1);
        TryTryAgain = PATH_RETRY;
        House.CurUnits++;
    }

    /// <summary>~FootClass: the house has one unit fewer (only while a game is running).</summary>
    protected override void Destruct()
    {
        if (Game.GameActive)
            House.CurUnits--;
        base.Destruct();
    }

    public uint Head_To_Coord() => headToCoord;

    public virtual void Set_Speed(int value) => speed = (byte)(value & 0xFF);

    public override bool Mark(MarkType mark)
    {
        if (base.Mark(mark))
        {
            int cell = TdMath.CoordCell(Coord);
            bool fixedWing = this is AircraftClass a && a.Class.IsFixedWing;
            switch (mark)
            {
                case MarkType.MARK_UP:
                    if (!fixedWing)
                        Game.Map.Pick_Up(cell, this);
                    break;
                case MarkType.MARK_DOWN:
                    if (!fixedWing)
                        Game.Map.Place_Down(cell, this);
                    break;
                default:
                    Game.Map.Refresh_Cells(cell, Overlap_List());
                    Game.Map.Refresh_Cells(cell, Occupy_List());
                    break;
            }
            return true;
        }
        return false;
    }

    static readonly int[] FaceAdjust = [0, 1, -1, 2, -2, 3, -3, 4];

    /// <summary>Basic_Path: plans the next few steps towards NavCom, trying cheaper move thresholds.</summary>
    public bool Basic_Path()
    {
        Path[0] = FacingType.FACING_NONE;
        if (Game.Target_Legal(NavCom))
        {
            int cell = Game.As_Cell(NavCom);

            // A blocked destination far away: aim at a free cell beside it instead.
            if (Can_Enter_Cell(cell) == MoveType.MOVE_NO && DistanceToTarget(NavCom) > 0x0300)
            {
                int f2 = TdMath.CellDirection(cell, TdMath.CoordCell(Coord)) >> 5;
                for (int index = 0; index < FaceAdjust.Length; index++)
                {
                    int cell2 = TdMath.AdjacentCellOf(cell, (FacingType)((f2 + FaceAdjust[index]) & 0x7));
                    if (Can_Enter_Cell(cell2, FacingType.FACING_NONE) <= MoveType.MOVE_CLOAK)
                    {
                        cell = cell2;
                        break;
                    }
                }
            }

            Mark(MarkType.MARK_UP);
            Path[0] = FacingType.FACING_NONE;
            bool found1 = false;
            var path1 = new PathType();
            var workpath1 = new FacingType[200];
            var workpath2 = new FacingType[200];
            MoveType maxtype = MoveType.MOVE_TEMP;
            if (!House.IsHuman)
                maxtype = MoveType.MOVE_DESTROYABLE;
            else if (Mission == MissionType.MISSION_MOVE && DistanceToTarget(NavCom) < 0x0280)
                maxtype = MoveType.MOVE_DESTROYABLE;

            PathType? path = Find_Path(cell, workpath1, workpath1.Length, maxtype);
            if (path != null && path.Cost != 0)
            {
                path1.CopyFrom(path);
                found1 = true;
                path = Find_Path(cell, workpath2, workpath2.Length, MoveType.MOVE_CLOAK);
                if (path != null && path.Cost != 0 && path.Cost < Math.Max(path1.Cost + (path1.Cost / 2), 3))
                {
                    path1.CopyFrom(path);
                    Array.Copy(workpath2, workpath1, workpath1.Length);
                }
                else
                {
                    for (MoveType move = MoveType.MOVE_MOVING_BLOCK; move < maxtype; move++)
                    {
                        path = Find_Path(cell, workpath2, workpath2.Length, move);
                        if (path != null && path.Cost != 0 && path.Cost < Math.Max(path1.Cost + (path1.Cost / 2), 3))
                        {
                            path1.CopyFrom(path);
                            Array.Copy(workpath2, workpath1, workpath1.Length);
                        }
                    }
                }
            }

            if (found1)
            {
                Fixup_Path(path1);
                // As the original: the length used is that of the last search made (the shared result).
                Array.Copy(workpath1, Path, Math.Min(path!.Length, Path.Length));
            }
            Mark(MarkType.MARK_DOWN);

            PathDelay.Set(PATH_DELAY);
            if (Path[0] != FacingType.FACING_NONE)
                return true;
            Stop_Driver();
        }
        return false;
    }

    public virtual void Fixup_Path(PathType path) { }

    public override int Mission_Move()
    {
        if (!Game.Target_Legal(NavCom) && !IsDriving && MissionQueue == MissionType.MISSION_NONE)
            Enter_Idle_Mode();
        if (!Game.Target_Legal(TarCom) && !House.IsHuman)
            Target_Something_Nearby(ThreatType.THREAT_RANGE);
        return TdDefines.TICKS_PER_SECOND + 3;
    }

    public override int Mission_Capture()
    {
        if (!Game.Target_Legal(NavCom) && !In_Radio_Contact())
        {
            Enter_Idle_Mode();
            if (Game.Map[TdMath.CoordCell(Center_Coord())].Cell_Building() != null)
                Scatter(0, true);
        }
        return TdDefines.TICKS_PER_SECOND - 2;
    }

    public override int Mission_Attack()
    {
        if (Game.Target_Legal(TarCom))
            Approach_Target();
        else
            Enter_Idle_Mode();
        return TdDefines.TICKS_PER_SECOND + 2;
    }

    public override int Mission_Guard()
    {
        if (!Target_Something_Nearby(ThreatType.THREAT_RANGE))
            Random_Animate();
        return TdDefines.TICKS_PER_SECOND + Game.Random_Pick(0, 4);
    }

    public override int Mission_Hunt()
    {
        if (!Target_Something_Nearby(ThreatType.THREAT_NORMAL))
        {
            Random_Animate();
        }
        else if (this is InfantryClass { Class.Type: InfantryType.INFANTRY_E7 })
        {
            Assign_Destination(TarCom);
            Assign_Mission(MissionType.MISSION_CAPTURE);
        }
        else
        {
            Approach_Target();
        }
        return TdDefines.TICKS_PER_SECOND + 5;
    }

    /// <summary>Mission_Timed_Hunt: the computer's units guard until the house is weakened or blitzes.</summary>
    public override int Mission_Timed_Hunt()
    {
        bool changed = false;
        if (!House.IsHuman)
        {
            if ((Game.MPlayerBlitz && House.BlitzTime.Time == 0) || House.CurUnits < ((House.MaxUnit * 4) / 5))
            {
                Assign_Mission(MissionType.MISSION_HUNT);
                changed = true;
            }

            int rndmax = Game.MPlayerBases ? 5000 : 1000;
            if (Game.IRandom(0, rndmax) == 1)
            {
                Assign_Mission(MissionType.MISSION_HUNT);
                changed = true;
            }

            if (!changed)
                Mission_Guard_Area();
        }
        return TdDefines.TICKS_PER_SECOND + Game.Random_Pick(0, 4);
    }

    public virtual bool Stop_Driver()
    {
        if (headToCoord != 0)
        {
            headToCoord = 0;
            Set_Speed(0);
            IsDriving = false;
            return true;
        }
        return false;
    }

    public virtual bool Start_Driver(ref uint headto)
    {
        Stop_Driver();
        if (headto != 0)
        {
            headToCoord = headto;
            IsDriving = true;

            // A crate in the cell ahead is picked up first; it may stop the move.
            if (Game.Map[TdMath.CoordCell(headto)].Goodie_Check(this))
                return true;
            headToCoord = 0;
            IsDriving = false;
        }
        return false;
    }

    /// <summary>Lets subclasses change where they are heading while driving (DriveClass's track logic).</summary>
    protected void Set_Head_To_Coord(uint coord) => headToCoord = coord;

    public override uint Sort_Y()
    {
        if (IsUnloading)
            return TdMath.CoordAdd(Coord, 0x01000000);
        if (In_Radio_Contact() && IsTethered && Contact_With_Whom()!.What_Am_I() == RTTIType.RTTI_UNIT)
            return TdMath.CoordAdd(Coord, 0x01000000);
        return TdMath.CoordAdd(Coord, 0x00300000);
    }

    public override void Stun()
    {
        Assign_Destination(Target.TARGET_NONE);
        Path[0] = FacingType.FACING_NONE;
        Stop_Driver();
        base.Stun();
    }

    static readonly int[] ApproachAngles = [0, 8, -8, 16, -16, 24, -24, 32, -32, 48, -48, 64, -64];

    /// <summary>Approach_Target: picks a spot within weapon range of the target (facing it) to move to.</summary>
    public virtual void Approach_Target()
    {
        if (Game.Target_Legal(TarCom))
        {
            int maxrange = Math.Max(Weapon_Range(0), Weapon_Range(1));
            if (!Game.Target_Legal(NavCom) && (!In_Range(TarCom) || !IsLocked))
            {
                var obj = Game.As_Building(TarCom);
                if (obj != null)
                    maxrange += (obj.Class.Width() + obj.Class.Height()) * (0x100 / 4);
                maxrange -= 0x00B7;
                maxrange = Math.Max(maxrange, 0);

                uint tcoord = Game.As_Coord(TarCom);
                uint trycoord;
                int tcell = TdMath.CoordCell(tcoord);
                int trycell = tcell;
                byte dir = TdMath.Direction(tcoord, Center_Coord());
                bool found = false;

                for (int range = maxrange; range > 0x0080; range -= 0x0100)
                {
                    for (int index = 0; index < ApproachAngles.Length; index++)
                    {
                        trycoord = TdMath.CoordMove(tcoord, (byte)(dir + ApproachAngles[index]), range);
                        if (TdMath.Distance(trycoord, tcoord) < range)
                        {
                            trycell = TdMath.CoordCell(trycoord);
                            if (Can_Enter_Cell(trycell) <= MoveType.MOVE_CLOAK && Game.Map.In_Radar(trycell))
                            {
                                found = true;
                                break;
                            }
                        }
                    }
                    if (found)
                        break;
                }

                if (found && What_Am_I() != RTTIType.RTTI_INFANTRY)
                    Assign_Destination(Target.As_Target(trycell));
                else
                    Assign_Destination(TarCom);
            }
        }
    }

    /// <summary>Mission_Guard_Area: stays near the guard spot (ArchiveTarget), attacking what comes close.</summary>
    public override int Mission_Guard_Area()
    {
        if (this is UnitClass { Class.IsToHarvest: true })
        {
            Assign_Mission(MissionType.MISSION_HARVEST);
            return 1 + Game.Random_Pick(1, 10);
        }

        if (!Game.Target_Legal(ArchiveTarget))
            ArchiveTarget = Target.As_Target(TdMath.CoordCell(Coord));

        // A guard spot off the map is given up.
        if (Game.Target_Legal(NavCom) && Target.Is_Target_Cell(NavCom))
        {
            int cell = Game.As_Cell(NavCom);
            int x = TdMath.CellX(cell);
            int y = TdMath.CellY(cell);
            var map = Game.Map;
            if (x < map.MapCellX || y < map.MapCellY || x >= (map.MapCellX + map.MapCellWidth) || y >= (map.MapCellY + map.MapCellHeight))
            {
                Assign_Target(Target.TARGET_NONE);
                Assign_Destination(Target.TARGET_NONE);
                ArchiveTarget = Target.As_Target(TdMath.CoordCell(Coord));
            }
        }

        int maxrange = Math.Max(Weapon_Range(0), Weapon_Range(1)) + 0x0100;
        if (!Game.Target_Legal(NavCom) && (DistanceToTarget(ArchiveTarget) > maxrange || (!Game.Target_Legal(TarCom) && DistanceToTarget(ArchiveTarget) > 0x0200)))
        {
            Assign_Target(Target.TARGET_NONE);
            Assign_Destination(ArchiveTarget);
        }

        if (!Game.Target_Legal(TarCom))
        {
            uint old = Coord;
            Coord = Game.As_Coord(ArchiveTarget);
            Target_Something_Nearby(ThreatType.THREAT_AREA);
            Coord = old;
            if (Game.Target_Legal(TarCom))
                return 1;
            Random_Animate();
        }
        else
        {
            Approach_Target();
        }
        return TdDefines.TICKS_PER_SECOND + Game.Random_Pick(0, 4);
    }

    public override bool Unlimbo(uint coord, byte dir = 0)
    {
        if (base.Unlimbo(coord, dir))
        {
            Revealed(House);
            Path[0] = FacingType.FACING_NONE;
            return true;
        }
        return false;
    }

    /// <summary>Assign_Mission: a new mission drops the planned path (except the gunboat's).</summary>
    public override void Assign_Mission(MissionType order)
    {
        if (this is not UnitClass { Class.Type: UnitType.UNIT_GUNBOAT })
            Path[0] = FacingType.FACING_NONE;
        base.Assign_Mission(order);
    }

    public override bool Limbo()
    {
        if (!IsInLimbo)
            Team?.Remove(this);
        return base.Limbo();
    }

    /// <summary>Take_Damage: tells the team, or (computer and smart-defense units) strikes back.</summary>
    public override ResultType Take_Damage(ref int damage, int distance, WarheadType warhead, TechnoClass? source = null)
    {
        ResultType result = base.Take_Damage(ref damage, distance, warhead, source);

        if (result != ResultType.RESULT_NONE && Team != null)
        {
            if (Game.GameToPlay != GameType.GAME_NORMAL || (source != null && !House.Is_Ally(source)))
                Team.Took_Damage(this, result, source);
        }
        else if (result != ResultType.RESULT_DESTROYED)
        {
            // Units whose target fights back (other than with a Nike) keep shooting at it.
            WeaponType weap = WeaponType.WEAPON_NONE;
            var tarcomTechno = Game.As_Techno(TarCom);
            if (tarcomTechno != null)
                weap = tarcomTechno.Techno_Type_Class().Primary;
            bool tweap = weap != WeaponType.WEAPON_NONE && weap != WeaponType.WEAPON_NIKE;

            if (source != null && Mission == MissionType.MISSION_STICKY)
                Enter_Idle_Mode();

            var primary = Techno_Type_Class().Primary;
            if (source != null &&
                !House.Is_Ally(source) &&
                primary != WeaponType.WEAPON_NONE &&
                (source.What_Am_I() != RTTIType.RTTI_AIRCRAFT || Game.Types.Bullet(Game.Types.Weapon(primary).Fires).IsAntiAircraft) &&
                (!Game.Target_Legal(TarCom) || ((!House.IsHuman || Game.Special.IsSmartDefense) && (!tweap || !In_Range(TarCom)))) &&
                (Mission == MissionType.MISSION_AMBUSH ||
                 Mission == MissionType.MISSION_GUARD ||
                 Mission == MissionType.MISSION_RESCUE ||
                 Mission == MissionType.MISSION_GUARD_AREA ||
                 Mission == MissionType.MISSION_ATTACK ||
                 Mission == MissionType.MISSION_TIMED_HUNT))
            {
                if (!House.IsHuman)
                {
                    if (Mission == MissionType.MISSION_TIMED_HUNT)
                        Set_Mission(MissionType.MISSION_HUNT);
                    else
                        Assign_Mission(MissionType.MISSION_HUNT);
                    Assign_Target(source.As_Target());
                }
                else
                {
                    if (In_Range(source, 0))
                        Assign_Target(source.As_Target());
                    else if (Game.Special.IsScatter)
                        Scatter(0, true);
                }
            }
            else
            {
                if (!IsDriving && !Game.Target_Legal(TarCom) && !Game.Target_Legal(NavCom) && Game.Special.IsScatter && What_Am_I() != RTTIType.RTTI_AIRCRAFT)
                    Scatter(0, true);
            }
        }
        return result;
    }

    public override void Active_Click_With(ActionType action, ObjectClass obj)
    {
        switch (action)
        {
            case ActionType.ACTION_GUARD_AREA:
                if (Can_Player_Fire() && Can_Player_Move())
                    Player_Assign_Mission(MissionType.MISSION_GUARD_AREA, obj.As_Target());
                break;
            case ActionType.ACTION_SELF:
                Player_Assign_Mission(MissionType.MISSION_UNLOAD);
                break;
            case ActionType.ACTION_ATTACK:
                if (Can_Player_Fire())
                    Player_Assign_Mission(MissionType.MISSION_ATTACK, obj.As_Target());
                break;
            case ActionType.ACTION_ENTER:
                if (Can_Player_Move() && obj != null && obj.Is_Techno())
                    Player_Assign_Mission(MissionType.MISSION_ENTER, Target.TARGET_NONE, obj.As_Target());
                break;
            case ActionType.ACTION_CAPTURE:
                if (Can_Player_Move())
                    Player_Assign_Mission(MissionType.MISSION_CAPTURE, Target.TARGET_NONE, obj.As_Target());
                break;
            case ActionType.ACTION_SABOTAGE:
                if (Can_Player_Move())
                    Player_Assign_Mission(MissionType.MISSION_SABOTAGE, Target.TARGET_NONE, obj.As_Target());
                break;
            case ActionType.ACTION_NOMOVE:
            case ActionType.ACTION_MOVE:
                if (Can_Player_Move())
                    Player_Assign_Mission(MissionType.MISSION_MOVE, Target.TARGET_NONE, obj.As_Target());
                break;
            case ActionType.ACTION_NO_DEPLOY:
                Game.Speak(VoxType.VOX_DEPLOY);
                break;
        }
    }

    public override void Active_Click_With(ActionType action, int cell)
    {
        switch (action)
        {
            case ActionType.ACTION_HARVEST:
                Player_Assign_Mission(MissionType.MISSION_HARVEST, Target.TARGET_NONE, Target.As_Target(cell));
                break;
            case ActionType.ACTION_MOVE:
                if (Game.AllowVoice)
                {
                    uint coord = Game.Map.Pixel_To_Coord(Game.Input.MouseX, Game.Input.MouseY);
                    var player = Game.PlayerPtr.Class.House;
                    Game.OutList.Add(EventClass.Anim(Game, AnimType.ANIM_MOVE_FLASH, player, coord, 1 << (int)player));
                }
                goto case ActionType.ACTION_NOMOVE;
            case ActionType.ACTION_NOMOVE:
                if (What_Am_I() != RTTIType.RTTI_AIRCRAFT || Game.Map[cell].Is_Visible(Game.PlayerPtr))
                    Player_Assign_Mission(MissionType.MISSION_MOVE, Target.TARGET_NONE, Target.As_Target(cell));
                break;
            case ActionType.ACTION_ATTACK:
                Player_Assign_Mission(MissionType.MISSION_ATTACK, Target.As_Target(cell));
                break;
            case ActionType.ACTION_GUARD_AREA:
                if (Can_Player_Fire() && Can_Player_Move())
                    Player_Assign_Mission(MissionType.MISSION_GUARD_AREA, Target.As_Target(cell));
                break;
        }
    }

    /// <summary>Per_Cell_Process: on entering a cell — cloak shimmer near scanners, stop when in range,
    /// spring "player entered" triggers, pick up crates.</summary>
    public override void Per_Cell_Process(bool center)
    {
        IsUnloading = false;

        if (Cloak == CloakType.CLOAKED)
        {
            for (FacingType face = FacingType.FACING_N; face < FacingType.FACING_COUNT; face++)
            {
                int cell = TdMath.AdjacentCellOf(TdMath.CoordCell(Coord), face);
                if (Game.Map.In_Radar(cell))
                {
                    var techno = Game.Map[cell].Cell_Techno();
                    if (techno != null && !House.Is_Ally(techno) && techno.Techno_Type_Class().IsScanner)
                    {
                        Do_Shimmer();
                        break;
                    }
                }
            }
        }

        if (this is not UnitClass { Class.Type: UnitType.UNIT_GUNBOAT })
        {
            bool inrange = In_Range(TarCom);
            var techno = Game.As_Techno(TarCom);
            if (techno != null && techno.What_Am_I() != RTTIType.RTTI_BUILDING)
            {
                var foot = (FootClass)techno;
                MPHType maxSpeed = techno.Techno_Type_Class().MaxSpeed;
                uint rangecoord = maxSpeed > MPHType.MPH_SLOW ? foot.Likely_Coord() : foot.Target_Coord();
                inrange = In_Range_Coord(rangecoord);
            }

            if (Game.Target_Legal(TarCom) && (Mission == MissionType.MISSION_RESCUE || Mission == MissionType.MISSION_GUARD_AREA || Mission == MissionType.MISSION_ATTACK || Mission == MissionType.MISSION_HUNT) && inrange)
            {
                Assign_Destination(Target.TARGET_NONE);
                Path[0] = FacingType.FACING_NONE;
            }
        }

        var trigger = Game.Map[TdMath.CoordCell(Coord)].Get_Trigger();
        if (Cloak != CloakType.CLOAKED && trigger != null && trigger.House == Owner())
            trigger.Spring(EventType.EVENT_PLAYER_ENTERED, TdMath.CoordCell(Coord));

        Game.Map[TdMath.CoordCell(Coord)].Goodie_Check(this, true);

        base.Per_Cell_Process(center);
    }

    public override void Override_Mission(MissionType mission, uint tarcom, uint navcom)
    {
        SuspendedNavCom = NavCom;
        base.Override_Mission(mission, tarcom, navcom);
        Assign_Destination(navcom);
    }

    public override bool Restore_Mission()
    {
        if (base.Restore_Mission())
        {
            Assign_Destination(SuspendedNavCom);
            return true;
        }
        return false;
    }

    public override RadioMessageType Receive_Message(RadioClass? from, RadioMessageType message, ref int param)
    {
        switch (message)
        {
            case RadioMessageType.RADIO_ON_DEPOT:
            {
                var building = Game.Map[TdMath.CoordCell(Center_Coord())].Cell_Building();
                if (building != null && building.Class.Type == StructType.STRUCT_REPAIR)
                    return RadioMessageType.RADIO_ROGER;
                return RadioMessageType.RADIO_NEGATIVE;
            }

            case RadioMessageType.RADIO_REPAIR:
                if (Game.Target_Legal(NavCom))
                    return RadioMessageType.RADIO_NEGATIVE;
                break;

            case RadioMessageType.RADIO_RUN_AWAY:
                if (In_Radio_Contact())
                {
                    if (NavCom == Contact_With_Whom()!.As_Target())
                        Assign_Destination(Target.TARGET_NONE);
                }
                if (Mission == MissionType.MISSION_SLEEP)
                {
                    Assign_Mission(MissionType.MISSION_GUARD);
                    Commence();
                }
                if (Mission == MissionType.MISSION_ENTER)
                    Assign_Mission(MissionType.MISSION_GUARD);
                if (!IsRotating && !Game.Target_Legal(NavCom))
                    Scatter(0, true, true);
                break;

            case RadioMessageType.RADIO_NEED_TO_MOVE:
                param = (int)NavCom;
                if (!Game.Target_Legal(NavCom))
                    return RadioMessageType.RADIO_ROGER;
                return RadioMessageType.RADIO_NEGATIVE;

            case RadioMessageType.RADIO_MOVE_HERE:
                if (NavCom != (uint)param)
                {
                    if (Target.As_Target(TdMath.CoordCell(Coord)) == (uint)param)
                        return RadioMessageType.RADIO_YEA_NOW_WHAT;
                    if (Mission == MissionType.MISSION_GUARD && MissionQueue == MissionType.MISSION_NONE)
                        Assign_Mission(MissionType.MISSION_MOVE);
                    Assign_Destination((uint)param);
                }
                return RadioMessageType.RADIO_ROGER;

            case RadioMessageType.RADIO_TRYING_TO_LOAD:
                if (Mission == MissionType.MISSION_ENTER || MissionQueue == MissionType.MISSION_ENTER)
                {
                    base.Receive_Message(from, message, ref param);
                    return RadioMessageType.RADIO_ROGER;
                }
                break;
        }
        return base.Receive_Message(from, message, ref param);
    }

    /// <summary>Mission_Enter: keeps asking the transport/building to dock; gives up if refused.</summary>
    public override int Mission_Enter()
    {
        TechnoClass? contact = Contact_With_Whom();
        contact ??= Game.As_Techno(ArchiveTarget);
        contact ??= Game.As_Techno(NavCom);

        if (contact != null)
        {
            if (Transmit_Message(RadioMessageType.RADIO_DOCKING, contact) != RadioMessageType.RADIO_ROGER && !IsTethered)
            {
                Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                Enter_Idle_Mode();
            }
        }
        else if (MissionQueue == MissionType.MISSION_NONE)
        {
            if (this is UnitClass { Class.IsToHarvest: true })
            {
                Assign_Mission(MissionType.MISSION_HARVEST);
                Assign_Target(As_Target());
                Assign_Destination(Target.TARGET_NONE);
            }
            else
            {
                ArchiveTarget = Target.TARGET_NONE;
                Enter_Idle_Mode();
            }
        }
        return TdDefines.TICKS_PER_SECOND / 2;
    }

    public override void Assign_Destination(uint target) => NavCom = target;

    public override void Detach_All(bool all = true)
    {
        Team?.Remove(this);
        Team = null;
        base.Detach_All(all);
    }

    /// <summary>Rescue_Mission: how much this unit wants to come to the rescue against the target.</summary>
    public int Rescue_Mission(uint tarcom)
    {
        if (!Game.Target_Legal(tarcom))
            return 0;
        if (TarCom == tarcom)
            return -Risk();

        if (Game.Target_Legal(TarCom))
        {
            var techno = Game.As_Techno(TarCom);
            if (techno != null && techno.Techno_Type_Class().Primary != WeaponType.WEAPON_NONE)
                return 0;
        }

        if (Team != null || Mission == MissionType.MISSION_HARVEST || Risk() == 0)
            return 0;

        int dist = DistanceToTarget(tarcom) - Weapon_Range(0);
        int threat = Risk() * 256;
        if (dist > 0)
        {
            int speed = (int)Math.Max((uint)Techno_Type_Class().MaxSpeed, 1u);
            int ratio = speed > 0 ? Math.Max(dist / speed, 1) : 1;
            threat = Math.Max(threat / ratio, 1);
        }
        return threat;
    }

    /// <summary>Death_Announcement: "unit lost" (or the faction's death cry) for the player's own units.</summary>
    public override void Death_Announcement(TechnoClass? source = null)
    {
        bool glyphx = Game.GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER;
        // As the original's precedence: (glyphx && human) || (!glyphx && discovered) || owned.
        if ((glyphx && House.IsHuman) || (!glyphx && Is_Discovered_By_Player()) || Is_Owned_By_Player())
        {
            if (source is not InfantryClass { Class.Type: InfantryType.INFANTRY_RAMBO })
            {
                if (this is InfantryClass infantry && infantry.Class.IsCivilian && !infantry.IsTechnician)
                {
                    if (Game.Options.IsDeathAnnounce)
                        Game.Speak(VoxType.VOX_DEAD_CIV, House, Center_Coord());
                }
                else if (House != Game.PlayerPtr && Game.GameToPlay != GameType.GAME_NORMAL)
                {
                    if (Game.Options.IsDeathAnnounce)
                        Game.Speak(VoxType.VOX_ENEMY_UNIT, House);
                }
                else if ((glyphx && House.IsHuman) || House == Game.PlayerPtr || Game.Options.IsDeathAnnounce)
                {
                    if (!Game.Options.IsDeathAnnounce)
                    {
                        Game.Speak(VoxType.VOX_UNIT_LOST, House, Center_Coord());
                    }
                    else
                    {
                        switch (House.ActLike)
                        {
                            case HousesType.HOUSE_GOOD:
                                Game.Speak(VoxType.VOX_DEAD_GDI, House, Center_Coord());
                                break;
                            case HousesType.HOUSE_BAD:
                                Game.Speak(VoxType.VOX_DEAD_NOD, House, Center_Coord());
                                break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Greatest_Threat: anti-air weapons also look at aircraft; ground is always included.
    /// A human's cloaked unit on guard picks no targets.</summary>
    public override uint Greatest_Threat(ThreatType method)
    {
        if (House.IsHuman && IsCloakable && Mission == MissionType.MISSION_GUARD)
            return Target.TARGET_NONE;

        var tclass = Techno_Type_Class();
        if (tclass.Primary != WeaponType.WEAPON_NONE && Game.Types.Bullet(Game.Types.Weapon(tclass.Primary).Fires).IsAntiAircraft)
            method |= ThreatType.THREAT_AIR;
        if (tclass.Secondary != WeaponType.WEAPON_NONE && Game.Types.Bullet(Game.Types.Weapon(tclass.Secondary).Fires).IsAntiAircraft)
            method |= ThreatType.THREAT_AIR;
        return base.Greatest_Threat(method | (ThreatType)TdDefines.THREAT_GROUND);
    }

    public override void Detach(uint target, bool all)
    {
        base.Detach(target, all);

        if (!Game.SpecialFlag && ArchiveTarget == target)
            ArchiveTarget = Target.TARGET_NONE;

        if (SuspendedNavCom == target)
        {
            SuspendedNavCom = Target.TARGET_NONE;
            SuspendedMission = MissionType.MISSION_NONE;
        }

        if (NavCom == target)
        {
            NavCom = Target.TARGET_NONE;
            Path[0] = FacingType.FACING_NONE;
            Restore_Mission();
        }

        if (TarCom == target && House.IsHuman)
            Path[0] = FacingType.FACING_NONE;
    }

    public virtual int Offload_Tiberium_Bail() => 0;

    public override MoveType Can_Enter_Cell(int cell, FacingType facing = FacingType.FACING_NONE) => MoveType.MOVE_OK;

    /// <summary>Can_Demolish: a unit or aircraft on a repair bay may be sold.</summary>
    public override bool Can_Demolish()
    {
        switch (What_Am_I())
        {
            case RTTIType.RTTI_UNIT:
            case RTTIType.RTTI_AIRCRAFT:
                if (In_Radio_Contact() &&
                    Contact_With_Whom() is BuildingClass { Class.Type: StructType.STRUCT_REPAIR } bay &&
                    Distance(bay) < 0x0080)
                    return true;
                break;
        }
        return base.Can_Demolish();
    }

    public override void Sell_Back(int control)
    {
        if (control != 0)
        {
            if (House == Game.PlayerPtr)
                Game.Sound_Effect(VocType.VOC_CASHTURN);
            House.Refund_Money(Refund_Amount());
            Stun();
            Limbo();
            Delete();
        }
    }

    /// <summary>Likely_Coord: where this object will be shortly (the spot it is driving to).</summary>
    public virtual uint Likely_Coord()
    {
        if (Head_To_Coord() != 0)
            return Head_To_Coord();
        return Target_Coord();
    }
}

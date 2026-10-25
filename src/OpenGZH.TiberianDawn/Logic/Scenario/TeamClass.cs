using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Scenario;

/// <summary>
/// TeamClass (TEAM.CPP): a group of units following a team type's orders together — recruiting,
/// gathering, moving as a group (waiting for laggards) and attacking one target.
/// </summary>
public sealed class TeamClass : AbstractClass
{
    const int STRAY_DISTANCE = 2;

    public readonly TeamTypeClass Class;
    public readonly HouseClass House;
    public bool IsForcedActive, IsHasBeen, IsFullStrength, IsUnderStrength, IsReforming, IsLagging;
    bool isAltered, isMoving, isNextMission, suspended;
    public int Center, ObjectiveCenter;
    public uint MissionTarget = Logic.Target.TARGET_NONE;
    public uint Target = Logic.Target.TARGET_NONE;
    public int Total, Risk;
    public readonly CountDownTimer SuspendTimer;
    int currentMission;
    readonly CountDownTimer timeOut;
    FootClass? member;
    readonly int[] quantity = new int[TeamTypeClass.MAX_TEAM_CLASSCOUNT];

    /// <summary>Number: how many teams of each team type exist (TeamClass::Number[]).</summary>
    public static int Number(TdGame game, int index) => (uint)index < (uint)game.TeamNumber.Length ? game.TeamNumber[index] : 0;

    TeamClass(TdGame game, TeamTypeClass type, HouseClass owner) : base(game)
    {
        Class = type;
        House = owner;
        SuspendTimer = new CountDownTimer(game);
        timeOut = new CountDownTimer(game);
        isAltered = true;
        IsUnderStrength = true;
        currentMission = -1;
        isNextMission = true;
        timeOut.Set(0);
        SuspendTimer.Clear();
        game.TeamNumber[game.TeamTypes.ID(Class)]++;
    }

    /// <summary>new TeamClass: a slot in the team heap (null when full).</summary>
    public static TeamClass? Create(TdGame game, TeamTypeClass type, HouseClass? owner)
    {
        int slot = game.Teams.Allocate();
        if (slot < 0 || owner == null)
            return null;
        var team = new TeamClass(game, type, owner);
        game.Teams.Place(slot, team);
        team.Set_Active();
        return team;
    }

    /// <summary>~TeamClass: the members leave; a transient team type goes with its last team.</summary>
    protected override void Destruct()
    {
        if (Game.GameActive)
        {
            int id = Game.TeamTypes.ID(Class);
            Game.TeamNumber[id]--;
            while (member != null)
                Remove(member);
            if (Class.IsTransient && Game.TeamNumber[id] == 0)
                Class.Delete();
        }
        base.Destruct();
    }

    protected override void Release()
    {
        IsActive = false;
        Game.Teams.Free(this);
    }

    public FootClass? Member => member;
    /// <summary>JustAltered (the AI reads it: HouseClass is a friend of TeamClass in the remaster).</summary>
    public bool IsAltered => isAltered;
    public uint As_Target() => Logic.Target.Build_Target(KindType.KIND_TEAM, Game.Teams.ID(this));
    public void Force_Active()
    {
        IsForcedActive = true;
        IsUnderStrength = false;
    }

    /// <summary>Assign_Mission_Target: members chasing the old mission target stop; the new one is set.</summary>
    public void Assign_Mission_Target(uint newTarget)
    {
        for (FootClass? unit = member; unit != null; unit = unit.Member)
        {
            bool tar = unit.TarCom == MissionTarget;
            bool nav = unit.NavCom == MissionTarget;
            if (tar || nav)
            {
                unit.Assign_Mission(MissionType.MISSION_GUARD);
                if (nav)
                    unit.Assign_Destination(Logic.Target.TARGET_NONE);
                if (tar)
                    unit.Assign_Target(Logic.Target.TARGET_NONE);
            }
        }

        if (Target == MissionTarget || !Game.Target_Legal(Target))
            MissionTarget = Target = newTarget;
        else
            MissionTarget = newTarget;
    }

    TeamMissionStruct Mission_At(int index) =>
        (uint)index < (uint)TeamTypeClass.MAX_TEAM_MISSIONS ? Class.MissionList[index] : new TeamMissionStruct { Mission = TeamMissionType.TMISSION_NONE };

    /// <summary>AI: strength checks, retreat when weakened, recruiting, and carrying out the current order.</summary>
    public override void AI()
    {
        int desired = 0;
        bool oldUnder = IsUnderStrength;

        if (suspended)
        {
            if (!SuspendTimer.Expired)
                return;
            suspended = false;
        }

        if (isAltered)
        {
            for (int index = 0; index < Class.ClassCount; index++)
                desired += Class.DesiredNum[index];

            if (Total != 0)
            {
                IsFullStrength = Total == desired;
                if (House.IsHuman)
                    IsUnderStrength = false;
                else if (Class.IsReinforcable)
                    IsUnderStrength = Total <= desired / 3;
                else
                    IsUnderStrength = !IsHasBeen;
                isAltered = false;
            }
            else
            {
                IsUnderStrength = true;
                IsFullStrength = false;
                Center = 0;
                if (House.IsHuman || IsHasBeen)
                {
                    Delete_This();
                    return;
                }
            }

            if (oldUnder != IsUnderStrength)
                IsReforming = true;
        }

        // Weakened on the move: fall back to a safe spot near one of the house's unarmed buildings.
        if (isMoving && IsUnderStrength)
        {
            isMoving = false;
            currentMission = -1;
            if (Total != 0)
            {
                Calc_Center(out Center, out ObjectiveCenter);
                int dest = Center;
                int max = int.MaxValue;
                for (int index = 0; index < Game.Buildings.Count; index++)
                {
                    var b = Game.Buildings.Ptr(index);
                    if (!b.IsInLimbo && b.House == House && b.Class.Primary == WeaponType.WEAPON_NONE)
                    {
                        int cell = TdMath.CoordCell(b.Center_Coord());
                        int dist = Game.Map.Cell_Distance(cell, Center) * (Game.Map.Cell_Threat(cell, House.Class.House) + 1);
                        if (b.Class.Type == StructType.STRUCT_REPAIR)
                            dist >>= 1;
                        if (dist < max)
                        {
                            cell = member!.Safety_Point(Center, cell, 2, 4);
                            if (cell != -1)
                            {
                                max = dist;
                                dest = cell;
                            }
                        }
                    }
                }
                Target = Logic.Target.As_Target(dest);
                Coordinate_Move();
                return;
            }
            Center = 0;
        }

        // Ready to go: everyone gestures and the orders start.
        if (!isMoving && (IsFullStrength || IsForcedActive))
        {
            isMoving = true;
            IsHasBeen = true;
            IsUnderStrength = false;
            DoType doaction = Game.Random_Pick(1, 2) == 1 ? DoType.DO_GESTURE1 : DoType.DO_GESTURE2;
            for (FootClass? techno = member; techno != null; techno = techno.Member)
            {
                if (!techno.IsInLimbo && techno is InfantryClass inf)
                    inf.Do_Action(doaction);
                if (IsReforming || IsForcedActive)
                    techno.IsInitiated = true;
            }
            currentMission = -1;
            isNextMission = true;
            IsForcedActive = false;
        }

        if (IsReforming || isMoving || Center == 0)
            Calc_Center(out Center, out ObjectiveCenter);

        // As the original's precedence: !moving || ((!full && reinforcable) && !human).
        if (!isMoving || (!IsFullStrength && Class.IsReinforcable && !House.IsHuman))
        {
            for (int index = 0; index < Class.ClassCount; index++)
                if (quantity[index] < Class.DesiredNum[index])
                    Recruit(index);
        }

        if (member == null && IsHasBeen)
        {
            Delete_This();
            return;
        }

        // The next order.
        if (isMoving && !IsReforming && isNextMission)
        {
            isNextMission = false;
            currentMission++;
            if (currentMission < Class.MissionCount)
            {
                var mission = Mission_At(currentMission);
                timeOut.Set(mission.Argument * (TdDefines.TICKS_PER_MINUTE / 10));
                Target = Logic.Target.TARGET_NONE;
                switch (mission.Mission)
                {
                    case TeamMissionType.TMISSION_MOVECELL:
                        Assign_Mission_Target(Logic.Target.As_Target((short)mission.Argument));
                        break;
                    case TeamMissionType.TMISSION_MOVE:
                    case TeamMissionType.TMISSION_UNLOAD:
                        if (mission.Argument < (int)WaypointType.WAYPT_COUNT)
                            // A negative argument (SCG07EA's grd3 has "Move:-16") reads before the
                            // original's Waypoint array, into another object's memory; it is taken
                            // here as an unassigned waypoint (-1).
                            Assign_Mission_Target(Logic.Target.As_Target((short)(mission.Argument >= 0 ? Game.Waypoint[mission.Argument] : -1)));
                        else
                            Assign_Mission_Target((uint)mission.Argument);
                        break;
                    case TeamMissionType.TMISSION_ATTACKTARCOM:
                        Assign_Mission_Target((uint)mission.Argument);
                        break;
                    default:
                        Assign_Mission_Target(Logic.Target.TARGET_NONE);
                        break;
                }
            }
            else
            {
                Delete_This();
                return;
            }
        }

        if (member != null && isMoving && !IsReforming && !IsUnderStrength)
        {
            if (!Game.Target_Legal(Target))
                Target = MissionTarget;

            var mission = Mission_At(currentMission);
            switch (mission.Mission)
            {
                case TeamMissionType.TMISSION_ATTACKBASE:
                    Attack_Order(ThreatType.THREAT_BUILDINGS);
                    break;
                case TeamMissionType.TMISSION_ATTACKUNITS:
                    Attack_Order(ThreatType.THREAT_VEHICLES | ThreatType.THREAT_INFANTRY);
                    break;
                case TeamMissionType.TMISSION_ATTACKCIVILIANS:
                    Attack_Order(ThreatType.THREAT_CIVILIANS);
                    break;
                case TeamMissionType.TMISSION_ATTACKTARCOM:
                case TeamMissionType.TMISSION_RAMPAGE:
                    Attack_Order(ThreatType.THREAT_NORMAL);
                    break;
                case TeamMissionType.TMISSION_DEFENDBASE:
                case TeamMissionType.TMISSION_MOVE:
                case TeamMissionType.TMISSION_RETREAT:
                    Coordinate_Move();
                    break;
                case TeamMissionType.TMISSION_UNLOAD:
                    Coordinate_Unload();
                    break;
                case TeamMissionType.TMISSION_GUARD:
                    Coordinate_Regroup();
                    break;
                case TeamMissionType.TMISSION_LOOP:
                    currentMission = mission.Argument - 1;
                    isNextMission = true;
                    break;
            }

            switch (mission.Mission)
            {
                case TeamMissionType.TMISSION_ATTACKBASE:
                case TeamMissionType.TMISSION_ATTACKUNITS:
                case TeamMissionType.TMISSION_ATTACKCIVILIANS:
                case TeamMissionType.TMISSION_RAMPAGE:
                case TeamMissionType.TMISSION_DEFENDBASE:
                case TeamMissionType.TMISSION_UNLOAD:
                case TeamMissionType.TMISSION_RETREAT:
                case TeamMissionType.TMISSION_GUARD:
                    if (timeOut.Expired)
                        isNextMission = true;
                    break;
            }
        }
        else if (isMoving)
        {
            IsReforming = !Coordinate_Regroup();
        }
        else
        {
            Coordinate_Move();
        }
    }

    void Attack_Order(ThreatType threat)
    {
        if (!Game.Target_Legal(MissionTarget))
        {
            Assign_Mission_Target(member!.Greatest_Threat(threat));
            if (!Game.Target_Legal(MissionTarget))
                isNextMission = true;
        }
        Coordinate_Attack();
    }

    /// <summary>Add: an object joins (if it may: not busy, not in a higher-priority team, and wanted).</summary>
    public bool Add(FootClass? obj, int typeindex = -1)
    {
        if (obj == null || obj.Strength == 0 || (obj.IsInLimbo && Game.ScenarioInit == 0) || obj.In_Radio_Contact() || obj.House != House)
            return false;

        var team = obj.Team;
        if (team == this)
            return true;

        if (obj.Mission is MissionType.MISSION_STICKY or MissionType.MISSION_SLEEP or MissionType.MISSION_GUARD_AREA or MissionType.MISSION_HUNT or MissionType.MISSION_HARVEST)
            return false;

        if (team != null && team.Class.RecruitPriority >= Class.RecruitPriority)
            return false;

        if (typeindex == -1)
        {
            for (typeindex = 0; typeindex < Class.ClassCount; typeindex++)
                if (Class.Class[typeindex] == obj.Class_Of())
                    break;
        }

        // A type the team doesn't use reads an unused (zero) slot, so it isn't wanted.
        if (typeindex >= TeamTypeClass.MAX_TEAM_CLASSCOUNT || quantity[typeindex] >= Class.DesiredNum[typeindex])
            return false;

        team?.Remove(obj);

        quantity[typeindex]++;
        obj.IsInitiated = member == null;
        obj.Member = member;
        member = obj;
        obj.Team = this;
        Total++;
        Risk += obj.Risk();
        if (Center == 0)
            Calc_Center(out Center, out ObjectiveCenter);
        isAltered = true;
        return true;
    }

    /// <summary>Remove: an object leaves (someone else leads if it was the only initiated one).</summary>
    public bool Remove(FootClass obj, int typeindex = -1)
    {
        if (obj.Team != this)
            return true;

        if (typeindex == -1)
        {
            for (typeindex = 0; typeindex < Class.ClassCount; typeindex++)
                if (Class.Class[typeindex] == obj.Class_Of())
                    break;
        }
        if ((uint)typeindex < (uint)Class.ClassCount)
            quantity[typeindex]--;

        bool initiated = false;
        FootClass? prev = null;
        FootClass? curr = member;
        bool found = false;
        while (curr != null && (!found || !initiated))
        {
            if (curr == obj)
            {
                if (prev != null)
                    prev.Member = curr.Member;
                else
                    member = curr.Member;
                FootClass? temp = curr.Member;
                curr.Member = null;
                curr.Team = null;
                curr = temp;
                Total--;
                found = true;
                Risk -= obj.Risk();
                continue;
            }
            initiated |= curr.IsInitiated;
            prev = curr;
            curr = curr.Member;
        }

        if (!initiated && member != null)
        {
            member.IsInitiated = true;
            Center = 0;
        }
        isAltered = true;
        return true;
    }

    /// <summary>Recruit: free units of the type join (transports bring their passengers).</summary>
    int Recruit(int typeindex)
    {
        int added = 0;
        if (Class.DesiredNum[typeindex] > quantity[typeindex])
        {
            var type = Class.Class[typeindex];
            if (type is InfantryTypeClass)
            {
                for (int index = 0; index < Game.Infantry.Count; index++)
                {
                    var infantry = Game.Infantry.Ptr(index);
                    if (infantry.House == House && infantry.Class == type && Add(infantry, typeindex))
                        added++;
                    if (quantity[typeindex] >= Class.DesiredNum[typeindex])
                        break;
                }
            }
            if (type is UnitTypeClass)
            {
                for (int index = 0; index < Game.Units.Count; index++)
                {
                    var unit = Game.Units.Ptr(index);
                    if (unit.House == House && unit.Class == type && Add(unit, typeindex))
                    {
                        added++;
                        for (FootClass? f = unit.Cargo.Attached_Object(); f != null; f = f.Next as FootClass)
                            Add(f);
                    }
                    if (quantity[typeindex] >= Class.DesiredNum[typeindex])
                        break;
                }
            }
        }
        return added;
    }

    public void Detach(uint target, bool all)
    {
        if (Target == target)
            Target = Logic.Target.TARGET_NONE;
        if (MissionTarget == target)
            MissionTarget = Logic.Target.TARGET_NONE;
    }

    /// <summary>Calc_Center: the average cell of the initiated members, and the member nearest the target.</summary>
    void Calc_Center(out int center, out int objCenter)
    {
        long x = 0, y = 0;
        int dist = int.MaxValue;
        int count = 0;
        objCenter = 0;
        center = 0;

        for (FootClass? unit = member; unit != null; unit = unit.Member)
        {
            if (unit.IsInitiated && !unit.IsInLimbo)
            {
                int c = TdMath.CoordCell(unit.Center_Coord());
                if (unit.DistanceToTarget(Target) < dist)
                {
                    dist = unit.DistanceToTarget(Target);
                    objCenter = c;
                }
                x += TdMath.CellX(c);
                y += TdMath.CellY(c);
                count++;
            }
        }

        if (count != 0)
        {
            x /= count;
            y /= count;
            center = TdMath.XyCell((int)x, (int)y);
        }
    }

    /// <summary>Took_Damage: a moving team turns on its attacker (unless its target can already shoot them).</summary>
    public void Took_Damage(FootClass obj, ResultType result, TechnoClass? source)
    {
        if (result != ResultType.RESULT_NONE && !Class.IsSuicide && isMoving)
        {
            if (source != null && !Is_A_Member(source) && member != null && member.What_Am_I() != RTTIType.RTTI_AIRCRAFT && Target != source.As_Target())
            {
                if (Game.Target_Legal(Target))
                {
                    var techno = Game.As_Techno(Target);
                    if (techno != null && techno.Techno_Type_Class().Primary != WeaponType.WEAPON_NONE && techno.In_Range_Coord(TdMath.CellCoord(Center), 0))
                        return;
                }
                Target = source.As_Target();
            }
        }
    }

    void Coordinate_Attack()
    {
        if (!Game.Target_Legal(Target))
            Target = MissionTarget;

        if (!Game.Target_Legal(Target))
        {
            isNextMission = true;
            return;
        }

        for (FootClass? unit = member; unit != null; unit = unit.Member)
        {
            Coordinate_Conscript(unit);
            if (unit.IsInitiated && !unit.IsInLimbo)
            {
                if (unit.Mission is not (MissionType.MISSION_ATTACK or MissionType.MISSION_ENTER or MissionType.MISSION_CAPTURE))
                {
                    unit.Assign_Mission(MissionType.MISSION_ATTACK);
                    unit.Assign_Target(Logic.Target.TARGET_NONE);
                    unit.Assign_Destination(Logic.Target.TARGET_NONE);
                }
                if (unit.TarCom != Target)
                    unit.Assign_Target(Target);
            }
        }
    }

    /// <summary>Coordinate_Regroup: members gather at the centre; true when all are there.</summary>
    bool Coordinate_Regroup()
    {
        bool retval = true;
        for (FootClass? unit = member; unit != null; unit = unit.Member)
        {
            Coordinate_Conscript(unit);
            if (unit.IsInitiated && !unit.IsInLimbo)
            {
                if (unit.DistanceToCell(Center) > STRAY_DISTANCE && (unit.Mission != MissionType.MISSION_GUARD_AREA || !Game.Target_Legal(unit.TarCom)))
                {
                    if (unit.Mission != MissionType.MISSION_MOVE || !Game.Target_Legal(unit.NavCom) || TdMath.CellDistance(Game.As_Cell(unit.NavCom), Center) > STRAY_DISTANCE)
                    {
                        unit.Assign_Mission(MissionType.MISSION_MOVE);
                        unit.Assign_Destination(Logic.Target.As_Target(Center));
                    }
                    retval = false;
                }
                else
                {
                    unit.IsInitiated = true;
                    if (unit.Mission != MissionType.MISSION_GUARD_AREA)
                    {
                        unit.Assign_Mission(MissionType.MISSION_GUARD);
                        unit.Assign_Destination(Logic.Target.TARGET_NONE);
                    }
                }
            }
        }
        return retval;
    }

    /// <summary>Coordinate_Move: the group moves to the target (waiting for laggards); done when all arrive.</summary>
    void Coordinate_Move()
    {
        bool finished = true;
        if (!Game.Target_Legal(Target))
            Target = MissionTarget;

        if (Game.Target_Legal(Target))
        {
            if (!Lagging_Units())
            {
                for (FootClass? unit = member; unit != null; unit = unit.Member)
                {
                    Coordinate_Conscript(unit);
                    if (unit.IsInitiated && !unit.IsInLimbo)
                    {
                        if (unit.What_Am_I() != RTTIType.RTTI_AIRCRAFT && unit.DistanceToCell(Center) > STRAY_DISTANCE)
                        {
                            IsLagging = true;
                            finished = false;
                        }
                        else if ((unit.DistanceToTarget(Target) / (int)TdDefines.ICON_LEPTON_W) > STRAY_DISTANCE ||
                                 (unit is AircraftClass air && air.Altitude > 0 && Mission_At(currentMission + 1).Mission != TeamMissionType.TMISSION_MOVE))
                        {
                            if (unit.Mission != MissionType.MISSION_MOVE)
                                unit.Assign_Mission(MissionType.MISSION_MOVE);
                            if (unit.NavCom != Target)
                            {
                                uint target = Target;
                                if (unit is AircraftClass aircraft && !aircraft.Class.IsFixedWing && !aircraft.Is_LZ_Clear(target))
                                    target = aircraft.New_LZ(target, true);
                                unit.Assign_Destination(target);
                            }
                            finished = false;
                        }
                        else if (unit.Mission == MissionType.MISSION_MOVE && !Game.Target_Legal(unit.NavCom))
                        {
                            unit.Enter_Idle_Mode();
                        }
                    }
                }
            }
            else
            {
                finished = false;
            }
        }

        if (finished && isMoving)
            isNextMission = true;
    }

    bool Lagging_Units()
    {
        bool lag = false;
        if (!IsLagging)
            return false;

        for (FootClass? unit = member; unit != null; unit = unit.Member)
        {
            if (unit.IsInLimbo)
                continue;
            if (unit.DistanceToCell(ObjectiveCenter) > STRAY_DISTANCE)
            {
                if (unit.Mission != MissionType.MISSION_MOVE || !Game.Target_Legal(unit.NavCom) || TdMath.CellDistance(Game.As_Cell(unit.NavCom), ObjectiveCenter) > STRAY_DISTANCE)
                {
                    unit.Assign_Mission(MissionType.MISSION_MOVE);
                    unit.Assign_Destination(Logic.Target.As_Target(ObjectiveCenter));
                }
                lag = true;
            }
            else
            {
                unit.Assign_Mission(MissionType.MISSION_GUARD);
                unit.Assign_Destination(Logic.Target.TARGET_NONE);
            }
        }
        IsLagging = lag;
        return lag;
    }

    void Coordinate_Unload()
    {
        bool finished = true;
        FootClass? unit = member;
        while (unit != null)
        {
            Coordinate_Conscript(unit);
            if (unit.IsInitiated && !unit.IsInLimbo && unit.Cargo.Is_Something_Attached())
            {
                if (unit.IsALoaner)
                {
                    Remove(unit);
                    unit.Commence();
                    unit.Assign_Mission(MissionType.MISSION_UNLOAD);
                    unit.Assign_Destination(Target);
                }
                else if (unit.Mission != MissionType.MISSION_UNLOAD)
                {
                    unit.Assign_Mission(MissionType.MISSION_UNLOAD);
                    unit.Assign_Destination(Target);
                }
                finished = false;
            }
            // As the original: the next member is read after the unit may have left (then it's null, ending the loop).
            unit = unit.Member;
        }
        if (finished)
            isNextMission = true;
    }

    void Coordinate_Conscript(FootClass? unit)
    {
        if (unit != null && !unit.IsInitiated && !unit.IsInLimbo)
        {
            if (unit.DistanceToCell(Center) > STRAY_DISTANCE)
            {
                if (!Game.Target_Legal(unit.NavCom))
                {
                    unit.Assign_Mission(MissionType.MISSION_MOVE);
                    unit.Assign_Target(Logic.Target.TARGET_NONE);
                    unit.Assign_Destination(Logic.Target.As_Target(Center));
                }
            }
            else
            {
                unit.IsInitiated = true;
            }
        }
    }

    bool Is_A_Member(object who)
    {
        for (FootClass? unit = member; unit != null; unit = unit.Member)
            if (unit == who)
                return true;
        return false;
    }

    /// <summary>Suspend_Teams: teams below a priority give up their members for two minutes.</summary>
    public static void Suspend_Teams(TdGame game, int priority)
    {
        for (int index = 0; index < game.Teams.Count; index++)
        {
            var team = game.Teams.Ptr(index);
            if (team.Class.RecruitPriority < priority)
            {
                while (team.member != null)
                    team.Remove(team.member);
                team.isAltered = true;
                team.SuspendTimer.Set(TdDefines.TICKS_PER_MINUTE * 2);
                team.suspended = true;
            }
        }
    }
}

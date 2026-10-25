using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// AircraftClass (AIRCRAFT.CPP): helicopters (Orca, Apache, Chinook) and fixed-wing planes (A-10,
/// the cargo plane). Take-off and landing, landing zones, helipads, strafing runs and air drops.
/// </summary>
public sealed class AircraftClass : FootClass
{
    public const int FLIGHT_LEVEL = 24;

    public readonly AircraftTypeClass Class;
    public readonly FlyClass Fly = new();
    public readonly FacingClass SecondaryFacing = new();
    bool isLanding, isTakingOff, isHoming, isHovering;
    public byte Jitter;
    public int Altitude;
    readonly CountDownTimer sightTimer;
    int attacksRemaining;
    long reinforcementStart;

    AircraftClass(TdGame game, AircraftType classid, HousesType house) : base(game, house)
    {
        Class = game.Types.Aircraft(classid);
        sightTimer = new CountDownTimer(game);
        IsSecondShot = !Class.IsTwoShooter;
        Ammo = Class.MaxAmmo;
        attacksRemaining = 3;
        Altitude = FLIGHT_LEVEL;
        isLanding = false;
        isTakingOff = false;
        isHovering = false;
        isHoming = false;
        Strength = Class.MaxStrength;
        NavCom = Target.TARGET_NONE;
        SecondaryFacing.Copy_From(PrimaryFacing);
        Jitter = 0;
        reinforcementStart = -1;
        if (classid != AircraftType.AIRCRAFT_CARGO && game.GameToPlay == GameType.GAME_INTERNET)
            House.AircraftTotals.Increment_Unit_Total((int)classid);
        House.Tracking_Add(this);
    }

    /// <summary>new AircraftClass: the first free slot of the aircraft heap (null when full).</summary>
    public static AircraftClass? Create(TdGame game, AircraftType classid, HousesType house)
    {
        int slot = game.Aircraft.Allocate();
        if (slot < 0)
            return null;
        var air = new AircraftClass(game, classid, house);
        game.Aircraft.Place(slot, air);
        air.Set_Active();
        return air;
    }

    protected override void Destruct()
    {
        if (Game.GameActive)
        {
            House.Tracking_Remove(this);
            while (Cargo.Is_Something_Attached())
                Cargo.Detach_Object()?.Delete();
            Limbo();
        }
        if (Game.GameActive && Team != null)
            Team.Remove(this);
        base.Destruct();
    }

    protected override void Release()
    {
        IsActive = false;
        Game.Aircraft.Free(this);
    }

    public override RTTIType What_Am_I() => RTTIType.RTTI_AIRCRAFT;
    public override ObjectTypeClass Class_Of() => Class;
    public override uint As_Target() => Target.Build_Target(KindType.KIND_AIRCRAFT, Game.Aircraft.ID(this));

    public bool IsLanding => isLanding;
    public bool IsTakingOff => isTakingOff;

    public override bool Unlimbo(uint coord, byte dir = 0)
    {
        if (base.Unlimbo(coord, dir))
        {
            House.AScan |= 1L << (int)Class.Type;
            House.ActiveAScan |= 1L << (int)Class.Type;
            SecondaryFacing.Set(dir);
            Stage.Set_Rate(1);
            Stage.Set_Stage(0);
            Set_Speed(Altitude == FLIGHT_LEVEL ? 0xFF : 0);
            return true;
        }
        return false;
    }

    /// <summary>Read_INI: the scenario's [AIRCRAFT] — house, type, strength, cell, facing, mission.</summary>
    public static void Read_INI(TdGame game, Data.WwProfile ini)
    {
        string buf = "";
        foreach (string key in ini.Entries("AIRCRAFT"))
        {
            buf = ini.Get_String("AIRCRAFT", key, buf, 128 - 1);
            var tok = new Data.Strtok(buf);
            HousesType inhouse = game.Types.House_From_Name(tok.Next(","));
            if (inhouse == HousesType.HOUSE_NONE)
                continue;
            AircraftType classid = game.Types.Aircraft_From_Name(tok.Next(","));
            if (classid == AircraftType.AIRCRAFT_NONE || game.HouseOrNull(inhouse) == null)
                continue;

            var air = Create(game, classid, inhouse);
            if (air == null)
                continue;

            int strength = Data.WwProfile.Atoi(tok.Next(","));
            uint coord = TdMath.CellCoord((short)Data.WwProfile.Atoi(tok.Next(",")));
            byte dir = (byte)Data.WwProfile.Atoi(tok.Next(","));

            if (!game.Map.In_Radar(TdMath.CoordCell(coord)))
            {
                air.Delete();
            }
            else
            {
                air.Strength = (int)TdMath.FixedToCardinal((uint)air.Class.MaxStrength, (uint)strength);
                if (air.Unlimbo(coord, dir))
                    air.Assign_Mission(MissionClass.Mission_From_Name(tok.Next(",\n\r")));
                else
                    air.Delete();
            }
        }
    }

    /// <summary>Mission_Hunt: planes find a target, fly at it and bomb (three passes); helicopters attack.</summary>
    public override int Mission_Hunt()
    {
        if (Class.IsFixedWing)
        {
            if (Class.Primary == WeaponType.WEAPON_NONE && Class.Secondary == WeaponType.WEAPON_NONE)
            {
                Assign_Mission(MissionType.MISSION_RETREAT);
                return 1;
            }

            const int LOOK_FOR_TARGET = 0, FLY_TO_TARGET = 1, DROP_BOMBS = 2;
            switch (Status)
            {
                case LOOK_FOR_TARGET:
                    if (Game.Target_Legal(TarCom))
                    {
                        Status = FLY_TO_TARGET;
                        return 1;
                    }
                    Assign_Target(Greatest_Threat(ThreatType.THREAT_NORMAL));
                    if (!Game.Target_Legal(TarCom))
                        Enter_Idle_Mode();
                    break;

                case FLY_TO_TARGET:
                    if (Game.Target_Legal(TarCom))
                    {
                        isHoming = true;
                        if (!PrimaryFacing.Is_Rotating())
                            PrimaryFacing.Set_Desired(DirectionToTarget(TarCom));
                        if (DistanceToTarget(TarCom) < 0x0380)
                        {
                            isHoming = false;
                            Status = DROP_BOMBS;
                            return 1;
                        }
                    }
                    else
                    {
                        Status = LOOK_FOR_TARGET;
                    }
                    break;

                case DROP_BOMBS:
                    if (Ammo == 0)
                    {
                        attacksRemaining--;
                        if (attacksRemaining == 0)
                        {
                            Assign_Mission(MissionType.MISSION_RETREAT);
                            Commence();
                        }
                        else
                        {
                            Ammo = Class.MaxAmmo;
                            Status = LOOK_FOR_TARGET;
                        }
                    }
                    if (!Game.Target_Legal(TarCom))
                    {
                        Status = LOOK_FOR_TARGET;
                    }
                    else
                    {
                        Fire_At(TarCom, 0);
                        Game.Map[Game.As_Cell(TarCom)].Incoming(Coord, true);
                        return 5;
                    }
                    break;
            }
        }
        else if (Ammo == 0)
        {
            Enter_Idle_Mode();
        }
        else
        {
            Assign_Mission(MissionType.MISSION_ATTACK);
            return 1;
        }
        return TdDefines.TICKS_PER_SECOND;
    }

    public override void AI()
    {
        if (!isLanding && !isTakingOff)
            Commence();

        base.AI();

        if (!isLanding && !isTakingOff)
            Commence();

        if (PrimaryFacing.Is_Rotating() && PrimaryFacing.Rotation_Adjust(Class.ROT))
            Mark();

        // Planes point where they fly; helicopters turn freely.
        if (Class.IsFixedWing)
            SecondaryFacing.Copy_From(PrimaryFacing);

        if (SecondaryFacing.Is_Rotating() && SecondaryFacing.Rotation_Adjust(Class.ROT))
            Mark();

        // Modern balance: the cargo plane waits off-map until its reinforcement time.
        bool doPhysics = true;
        if (Game.Special.ModernBalance && Class.Type == AircraftType.AIRCRAFT_CARGO && !Game.Map.In_Radar(TdMath.CoordCell(Coord)) && reinforcementStart > Game.Frame)
            doPhysics = false;

        if (doPhysics && Fly.Physics(ref Coord, PrimaryFacing.Current) != ImpactType.IMPACT_NONE)
            Mark();

        if (House.IsHuman && Class.SightRange != 0 && sightTimer.Expired)
        {
            Game.Map.Sight_From(House, TdMath.CoordCell(Coord), Class.SightRange, false);
            sightTimer.Set(TdDefines.TICKS_PER_SECOND);
        }

        // Climbing or descending (the doors must be shut).
        if (Door.Is_Door_Closed() && (isLanding || isTakingOff))
        {
            Mark();
            LayerType layer = In_Which_Layer();

            if (isLanding)
            {
                if (Altitude != 0)
                    Altitude--;
                if (Altitude == 0)
                {
                    isLanding = false;
                    Set_Speed(0);
                    if (Game.Target_Legal(NavCom) && Game.As_Techno(NavCom) == Contact_With_Whom())
                    {
                        if (In_Radio_Contact() && Transmit_Message(RadioMessageType.RADIO_IM_IN) != RadioMessageType.RADIO_ROGER)
                            Scatter(0, true);
                    }
                }
            }

            if (isTakingOff)
            {
                Altitude++;
                if (Altitude >= FLIGHT_LEVEL)
                {
                    Altitude = FLIGHT_LEVEL;
                    isTakingOff = false;
                }
            }

            // Between the ground layer and the air: (un)tether, and occupy or free the cell.
            if (layer != In_Which_Layer())
            {
                bool ok = true;
                if (In_Which_Layer() == LayerType.LAYER_GROUND && !Is_LZ_Clear(Target.As_Target(TdMath.CoordCell(Coord))))
                {
                    isTakingOff = true;
                    Altitude++;
                    ok = false;
                }

                if (ok)
                {
                    Game.Map.Remove(this, layer);
                    Game.Map.Submit(this, In_Which_Layer());

                    if (In_Which_Layer() == LayerType.LAYER_GROUND)
                    {
                        Assign_Destination(Target.TARGET_NONE);
                        Transmit_Message(RadioMessageType.RADIO_TETHER);
                        Game.Map.Place_Down(TdMath.CoordCell(Coord), this);
                        if (House.IsHuman)
                            Game.Map.Sight_From(House, TdMath.CoordCell(Coord), 1, false);
                    }
                    else
                    {
                        Transmit_Message(RadioMessageType.RADIO_UNTETHER);
                        Game.Map.Pick_Up(TdMath.CoordCell(Coord), this);
                        if (In_Radio_Contact() && Game.Target_Legal(NavCom) && NavCom != Contact_With_Whom()!.As_Target())
                            Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                    }
                }
            }
        }

        if (Game.Map.In_View(TdMath.CoordCell(Coord)))
            Game.Map.Flag_To_Redraw(false);

        // Retreating off the map: gone (evacuated civilians count, the cargo plane is refunded).
        if (!Game.Map.In_Radar(TdMath.CoordCell(Coord)))
        {
            if (Mission == MissionType.MISSION_RETREAT)
            {
                while (Cargo.Is_Something_Attached())
                {
                    FootClass obj = Cargo.Detach_Object()!;
                    if (obj is InfantryClass { Class.IsCivilian: true, IsTechnician: false })
                        House.IsCivEvacuated = true;
                    if (Class.Type == AircraftType.AIRCRAFT_CARGO)
                        House.Refund_Money(obj.Class_Of().Cost_Of(Game.Special));
                    obj.Delete();
                }
                Stun();
                Delete_This();
                return;
            }
        }
        else
        {
            IsLocked = true;
        }
    }

    public override bool Mark(MarkType mark = MarkType.MARK_CHANGE)
    {
        if (base.Mark(mark))
        {
            Game.Map.Refresh_Cells(TdMath.CoordCell(Coord), Occupy_List());
            Game.Map.Refresh_Cells(TdMath.CoordCell(Coord), Overlap_List());
            return true;
        }
        return false;
    }

    static readonly short[] FlyingOverlap =
    [
        -(TdMath.MapCellW - 1), -TdMath.MapCellW, -(TdMath.MapCellW + 1),
        -1, 0, 1,
        TdMath.MapCellW - 1, TdMath.MapCellW, TdMath.MapCellW + 1,
        -((TdMath.MapCellW * 2) - 1), -(TdMath.MapCellW * 2), -((TdMath.MapCellW * 2) + 1),
        -((TdMath.MapCellW * 3) - 1), -(TdMath.MapCellW * 3), -((TdMath.MapCellW * 3) + 1),
        TdMath.RefreshEol,
    ];

    public override short[] Overlap_List() => Altitude != 0 ? FlyingOverlap : Class.Overlap_List();

    /// <summary>Mission_Unload: planes air-drop at an airstrip; helicopters land on a clear LZ and unload.</summary>
    public override int Mission_Unload()
    {
        if (Class.IsFixedWing)
        {
            const int PICK_AIRSTRIP = 0, FLY_TO_AIRSTRIP = 1, BUG_OUT = 2;
            switch (Status)
            {
                case PICK_AIRSTRIP:
                    if (!Game.Target_Legal(NavCom) || !In_Radio_Contact())
                    {
                        var targetBuilding = Game.As_Building(NavCom);
                        var building = targetBuilding != null && targetBuilding.Class.Type == StructType.STRUCT_AIRSTRIP
                            ? targetBuilding
                            : Find_Docking_Bay(StructType.STRUCT_AIRSTRIP, false);
                        if (building != null && Transmit_Message(RadioMessageType.RADIO_HELLO, building) == RadioMessageType.RADIO_ROGER)
                        {
                            Set_Speed(0xFF);
                            Assign_Destination(building.As_Target());
                            if (Team != null)
                                Team.Target = NavCom;
                            Status = FLY_TO_AIRSTRIP;
                        }

                        // No airstrip to go to: circle (and leave if the house has none).
                        if (Status == PICK_AIRSTRIP)
                        {
                            if ((House.ActiveBScan & (long)TdDefines.STRUCTF_AIRSTRIP) == 0)
                                Assign_Mission(MissionType.MISSION_RETREAT);
                            PrimaryFacing.Set_Desired((byte)Game.Random_Pick((int)DirType.DIR_N, (int)DirType.DIR_MAX));
                            SecondaryFacing.Set_Desired(PrimaryFacing.Desired);
                            return TdDefines.TICKS_PER_SECOND * 3;
                        }
                    }
                    else
                    {
                        Status = FLY_TO_AIRSTRIP;
                    }
                    break;

                case FLY_TO_AIRSTRIP:
                    if (!Game.Target_Legal(NavCom) || !In_Radio_Contact())
                    {
                        Status = PICK_AIRSTRIP;
                    }
                    else
                    {
                        if (!Cargo.Is_Something_Attached())
                        {
                            Status = BUG_OUT;
                            return 1;
                        }

                        if (!PrimaryFacing.Is_Rotating())
                            PrimaryFacing.Set_Desired(Direction(Game.As_Movement_Coord(NavCom)));
                        SecondaryFacing.Set_Desired(PrimaryFacing.Desired);

                        // Descend on the approach.
                        int navdist = Distance(Game.As_Movement_Coord(NavCom));
                        Altitude = FLIGHT_LEVEL;
                        if (navdist < 0x0600)
                            Altitude = (int)TdMath.FixedToCardinal(FLIGHT_LEVEL, TdMath.CardinalToFixed(0x0600, (uint)navdist));

                        if (navdist < 0x0080)
                        {
                            FootClass? unit = Cargo.Detach_Object();
                            if (unit != null)
                            {
                                int cell = Contact_With_Whom()!.Find_Exit_Cell(unit);
                                if (cell != 0)
                                {
                                    Game.ScenarioInit++;
                                    if (!unit.Unlimbo(TdMath.CellCoord(cell)))
                                    {
                                        Cargo.Attach(unit);
                                    }
                                    else
                                    {
                                        if (Class.Type == AircraftType.AIRCRAFT_CARGO && House == Game.PlayerPtr)
                                            Game.Speak(VoxType.VOX_REINFORCEMENTS, null, TdMath.CellCoord(cell));
                                        unit.IsALoaner = false;
                                        unit.IsLocked = true;
                                        unit.Scatter(0, true);
                                    }
                                    Game.ScenarioInit--;
                                    Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                                    Assign_Target(Target.TARGET_NONE);
                                }
                                else
                                {
                                    Cargo.Attach(unit);
                                }
                            }
                            Status = BUG_OUT;
                        }
                        return 1;
                    }
                    break;

                case BUG_OUT:
                    Assign_Mission(MissionType.MISSION_RETREAT);
                    return 1;
            }
        }
        else
        {
            const int SEARCH_FOR_LZ = 0, FLY_TO_LZ = 1, LAND_ON_LZ = 2, UNLOAD_PASSENGERS = 3, TAKE_OFF = 4;
            switch (Status)
            {
                case SEARCH_FOR_LZ:
                    if (Altitude == 0 && (Game.Target_Legal(NavCom) || Coord == Game.As_Coord(NavCom)))
                    {
                        Status = UNLOAD_PASSENGERS;
                    }
                    else if (!Is_LZ_Clear(NavCom))
                    {
                        Assign_Destination(New_LZ(Target.As_Target(Game.Waypoint[(int)WaypointType.WAYPT_REINF])));
                    }
                    else
                    {
                        Status = Altitude == FLIGHT_LEVEL ? FLY_TO_LZ : TAKE_OFF;
                    }
                    break;

                case FLY_TO_LZ:
                    if (Is_LZ_Clear(NavCom))
                    {
                        int distance = Process_Fly_To(true);
                        if (distance < 0x0100)
                        {
                            SecondaryFacing.Set_Desired(Pose_Dir());
                            if (distance < 0x0010)
                                Status = LAND_ON_LZ;
                            return 1;
                        }
                        SecondaryFacing.Set_Desired(PrimaryFacing.Desired);
                        return 5;
                    }
                    Status = SEARCH_FOR_LZ;
                    break;

                case LAND_ON_LZ:
                    if (isTakingOff)
                        Status = TAKE_OFF;
                    else if (Process_Landing())
                        Status = UNLOAD_PASSENGERS;
                    return 1;

                case UNLOAD_PASSENGERS:
                    if (!IsTethered)
                    {
                        if (Cargo.Is_Something_Attached())
                        {
                            FootClass unit = Cargo.Detach_Object()!;
                            Game.Map.Pick_Up(TdMath.CoordCell(Coord), this);
                            if (Exit_Object(unit) == 0)
                                unit.Delete();
                            Game.Map.Place_Down(TdMath.CoordCell(Coord), this);
                            if (!Cargo.Is_Something_Attached())
                                Enter_Idle_Mode();
                        }
                        else
                        {
                            Enter_Idle_Mode();
                        }
                    }
                    break;

                case TAKE_OFF:
                    if (Process_Take_Off())
                    {
                        if (Cargo.Is_Something_Attached())
                        {
                            Status = SEARCH_FOR_LZ;
                            if (In_Radio_Contact() && Game.Map[TdMath.CoordCell(Coord)].Cell_Building() == Contact_With_Whom())
                                Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                        }
                        else
                        {
                            Enter_Idle_Mode();
                        }
                    }
                    return 1;
            }
        }
        return 10;
    }

    /// <summary>Is_LZ_Clear: a visible cell with nothing in it (or only us, or what we're docking with).</summary>
    public bool Is_LZ_Clear(uint target)
    {
        if (!Game.Target_Legal(target))
            return false;
        int cell = Game.As_Cell(target);
        if (!Game.Map.In_Radar(cell))
            return false;

        var obj = Game.Map[cell].Cell_Object();
        if (obj != null)
        {
            if (obj == this)
                return true;
            return In_Radio_Contact() && Contact_With_Whom() == obj;
        }
        return Game.Map[cell].Is_Generally_Clear();
    }

    public override LayerType In_Which_Layer()
    {
        if (Class.IsFixedWing)
            return LayerType.LAYER_TOP;
        return Altitude < FLIGHT_LEVEL - (FLIGHT_LEVEL / 3) ? LayerType.LAYER_GROUND : LayerType.LAYER_TOP;
    }

    public override uint Sort_Y() => TdMath.CoordAdd(Coord, 0x00800000);

    /// <summary>Mission_Retreat: fly off the map by the house's edge (planes just keep going).</summary>
    public override int Mission_Retreat()
    {
        if (Class.IsFixedWing)
        {
            if (Class.Type == AircraftType.AIRCRAFT_CARGO)
            {
                PrimaryFacing.Set_Desired((byte)DirType.DIR_W);
                SecondaryFacing.Set_Desired(PrimaryFacing.Desired);
            }
            if (Altitude < FLIGHT_LEVEL)
            {
                Altitude++;
                return 1;
            }
            return TdDefines.TICKS_PER_SECOND * 10;
        }

        const int TAKE_OFF = 0, FACE_MAP_EDGE = 1, KEEP_FLYING = 2;
        switch (Status)
        {
            case TAKE_OFF:
                if (Process_Take_Off())
                    Status = FACE_MAP_EDGE;
                return 1;
            case FACE_MAP_EDGE:
                Set_Speed(0xFF);
                PrimaryFacing.Set_Desired((byte)(((int)House.Edge & 0x03) << 6));
                SecondaryFacing.Set_Desired(PrimaryFacing.Desired);
                Status = KEEP_FLYING;
                break;
            case KEEP_FLYING:
                break;
        }
        return TdDefines.TICKS_PER_SECOND;
    }

    static readonly FacingType[] ExitToFace =
    [
        FacingType.FACING_S, FacingType.FACING_SW, FacingType.FACING_SE, FacingType.FACING_NW,
        FacingType.FACING_NE, FacingType.FACING_N, FacingType.FACING_W, FacingType.FACING_E,
    ];

    /// <summary>Exit_Object: a passenger gets out (behind the helicopter first) and walks off.</summary>
    public override int Exit_Object(TechnoClass unit)
    {
        int cell = 0;
        int face;
        for (face = 0; face < (int)FacingType.FACING_COUNT; face++)
        {
            cell = TdMath.AdjacentCellOf(TdMath.CoordCell(Coord), ExitToFace[face]);
            if (unit.Can_Enter_Cell(cell) == MoveType.MOVE_OK)
                break;
        }

        // No cell free: the original reads past the table here; the passenger faces north.
        FacingType exitFace = face < ExitToFace.Length ? ExitToFace[face] : FacingType.FACING_N;
        if (unit.Unlimbo(Coord, (byte)((int)exitFace << 5)))
        {
            unit.Assign_Mission(MissionType.MISSION_MOVE);
            unit.Assign_Destination(Target.As_Target(cell));
            if (Transmit_Message(RadioMessageType.RADIO_HELLO, unit) == RadioMessageType.RADIO_ROGER)
                Transmit_Message(RadioMessageType.RADIO_UNLOAD);
            unit.Look(false);
            return 1;
        }
        return 0;
    }

    /// <summary>Fire_At: the player's aircraft see where they shoot; bombs drop slowly.</summary>
    public override BulletClass? Fire_At(uint target, int which = 0)
    {
        var bullet = base.Fire_At(target, which);
        if (bullet != null)
        {
            var player = Game.HouseOrNull(Owner());
            if (player != null && player.IsHuman)
                Game.Map.Sight_From(player, TdMath.CoordCell(Center_Coord()), 1, false);

            var weapon = Game.Types.Weapon(which == 0 ? Class.Primary : Class.Secondary);
            Game.Sound_Effect(weapon.Sound, Coord);

            if (bullet.Class.IsDropping)
                bullet.Fly.Fly_Speed(40, bullet.Class.MaxSpeed);
        }
        return bullet;
    }

    /// <summary>Take_Damage: half damage in the air; a fireball on death.</summary>
    public override ResultType Take_Damage(ref int damage, int distance, WarheadType warhead, TechnoClass? source = null)
    {
        if (Altitude != 0)
            damage /= 2;

        ResultType res = base.Take_Damage(ref damage, distance, warhead, source);
        if (res == ResultType.RESULT_DESTROYED)
        {
            Kill_Cargo(source);
            Death_Announcement();
            uint coord = Target_Coord();
            if ((coord & 0xC000C000) == 0)
                AnimClass.Create(Game, AnimType.ANIM_FBALL1, coord);
            Delete_This();
        }
        return res;
    }

    /// <summary>Mission_Move: planes fly to the airstrip and drop; helicopters take off, fly and land.</summary>
    public override int Mission_Move()
    {
        if (Class.IsFixedWing)
        {
            if (Class.Type == AircraftType.AIRCRAFT_A10)
            {
                Assign_Mission(MissionType.MISSION_RETREAT);
                Commence();
                return 1;
            }

            const int FLY_TO_AIRSTRIP = 0, BUG_OUT = 1;
            switch (Status)
            {
                case FLY_TO_AIRSTRIP:
                    if (!Game.Target_Legal(NavCom) || !In_Radio_Contact())
                        return TdDefines.TICKS_PER_SECOND;

                    if (!Cargo.Is_Something_Attached())
                    {
                        Status = BUG_OUT;
                        return 1;
                    }

                    if (!PrimaryFacing.Is_Rotating())
                        PrimaryFacing.Set_Desired(DirectionToTarget(NavCom));
                    SecondaryFacing.Set_Desired(PrimaryFacing.Desired);

                    if (DistanceToTarget(NavCom) < 0x0080)
                    {
                        FootClass? unit = Cargo.Detach_Object();
                        if (unit != null)
                        {
                            Game.ScenarioInit++;
                            if (!unit.Unlimbo(TdMath.CoordSnap(Contact_With_Whom()!.Coord)))
                                Cargo.Attach(unit);
                            Game.ScenarioInit--;
                            Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                            Assign_Target(Target.TARGET_NONE);
                        }
                        Status = BUG_OUT;
                    }
                    return 1;

                case BUG_OUT:
                    return TdDefines.TICKS_PER_SECOND;
            }
            return 5;
        }

        const int VALIDATE_LZ = 0, TAKE_OFF = 1, FLY_TO_LZ = 2, LAND = 3;
        switch (Status)
        {
            case VALIDATE_LZ:
                if (!Game.Target_Legal(NavCom))
                    Enter_Idle_Mode();
                else if (!Is_LZ_Clear(NavCom) || !Cell_Seems_Ok(Game.As_Cell(NavCom)))
                    Assign_Destination(New_LZ(NavCom));
                else
                    Status = TAKE_OFF;
                break;

            case TAKE_OFF:
                if (!Game.Target_Legal(NavCom))
                {
                    Status = VALIDATE_LZ;
                }
                else
                {
                    if (Process_Take_Off())
                    {
                        if (In_Radio_Contact() && Game.Map[TdMath.CoordCell(Coord)].Cell_Building() == Contact_With_Whom())
                            Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                        Status = FLY_TO_LZ;
                    }
                    return 1;
                }
                break;

            case FLY_TO_LZ:
                if (Is_LZ_Clear(NavCom))
                {
                    int distance = Process_Fly_To(true);
                    if (distance < 0x0080)
                    {
                        SecondaryFacing.Set_Desired(Game.Target_Legal(TarCom) ? DirectionToTarget(TarCom) : Pose_Dir());
                        if (distance < 0x0010)
                            Status = LAND;
                        return 1;
                    }
                    SecondaryFacing.Set_Desired(TdMath.Direction(Fire_Coord(0), Game.As_Coord(NavCom)));
                }
                else
                {
                    Assign_Destination(New_LZ(NavCom));
                    if (!Game.Target_Legal(NavCom))
                        Status = LAND;
                }
                return 1;

            case LAND:
                if (isTakingOff)
                {
                    Assign_Destination(New_LZ(NavCom));
                    Status = TAKE_OFF;
                }
                if (Process_Landing() && MissionQueue == MissionType.MISSION_NONE)
                    Enter_Idle_Mode();
                return 1;
        }
        return TdDefines.TICKS_PER_SECOND;
    }

    /// <summary>Enter_Idle_Mode: unload, leave, rearm at a helipad, or land somewhere near base.</summary>
    public override void Enter_Idle_Mode(bool initial = false)
    {
        MissionType mission = MissionType.MISSION_GUARD;

        if (In_Which_Layer() == LayerType.LAYER_GROUND)
        {
            if (IsALoaner)
            {
                if (Cargo.Is_Something_Attached())
                {
                    mission = MissionType.MISSION_UNLOAD;
                }
                else
                {
                    Team?.Remove(this);
                    mission = MissionType.MISSION_RETREAT;
                }
            }
            else
            {
                Assign_Destination(Target.TARGET_NONE);
                Assign_Target(Target.TARGET_NONE);
                mission = MissionType.MISSION_GUARD;
            }
        }
        else if (Cargo.Is_Something_Attached())
        {
            if (IsALoaner)
            {
                if (Team != null)
                {
                    mission = MissionType.MISSION_GUARD;
                }
                else
                {
                    mission = MissionType.MISSION_UNLOAD;
                    Assign_Destination(Good_LZ());
                }
            }
            else
            {
                Assign_Destination(Good_LZ());
                mission = MissionType.MISSION_MOVE;
            }
        }
        else
        {
            if (IsALoaner)
                Team?.Remove(this);

            if (Class.Primary != WeaponType.WEAPON_NONE)
            {
                if (Ammo == 0 && !House.IsHuman && IsALoaner)
                {
                    mission = MissionType.MISSION_RETREAT;
                }
                else if (!In_Radio_Contact() || Contact_With_Whom() is not BuildingClass { Class.Type: StructType.STRUCT_HELIPAD })
                {
                    var building = Find_Docking_Bay(StructType.STRUCT_HELIPAD, false);
                    Assign_Destination(Target.TARGET_NONE);
                    if (building != null && Transmit_Message(RadioMessageType.RADIO_HELLO, building) == RadioMessageType.RADIO_ROGER)
                    {
                        mission = MissionType.MISSION_ENTER;
                    }
                    else
                    {
                        Assign_Destination(Good_LZ());
                        mission = MissionType.MISSION_MOVE;
                    }
                }
                else
                {
                    mission = MissionType.MISSION_ENTER;
                }
            }
            else
            {
                if (Team != null)
                    return;
                Assign_Destination(Good_LZ());
                mission = MissionType.MISSION_MOVE;
            }
        }
        Assign_Mission(mission);
        Commence();
    }

    /// <summary>Process_Fly_To: heads for NavCom (slowing near it); the distance left (0 when there).</summary>
    int Process_Fly_To(bool slowdown)
    {
        uint coord = Target.Is_Target_Building(NavCom) ? Game.As_Building(NavCom)!.Docking_Coord() : Game.As_Coord(NavCom);

        int distance = Distance(coord);
        PrimaryFacing.Set_Desired(Direction(coord));

        if (slowdown)
        {
            int speed = Math.Min(distance, 0x0300);
            speed = Math.Clamp(speed / 3, 0x0020, 0x00FF);
            // As the original: FootClass's Speed (never set for aircraft) is compared.
            if (Speed != speed)
                Set_Speed(speed);
        }

        if (distance < 0x0010)
        {
            if (slowdown)
                Set_Speed(0);
            distance = 0;
        }
        return distance;
    }

    public override void Active_Click_With(ActionType action, ObjectClass obj)
    {
        switch (action)
        {
            case ActionType.ACTION_ENTER:
                Player_Assign_Mission(MissionType.MISSION_ENTER, Target.TARGET_NONE, obj.As_Target());
                break;
            case ActionType.ACTION_SELF:
                Player_Assign_Mission(MissionType.MISSION_UNLOAD, Target.TARGET_NONE, Target.TARGET_NONE);
                break;
        }
        base.Active_Click_With(action, obj);
    }

    public override void Player_Assign_Mission(MissionType mission, uint target = Target.TARGET_NONE, uint destination = Target.TARGET_NONE)
    {
        if (Game.AllowVoice)
        {
            if (mission == MissionType.MISSION_ATTACK)
                Response_Attack();
            else
                Response_Move();
        }
        Game.Queue_Mission(As_Target(), mission, target, destination);
    }

    public override ActionType What_Action(ObjectClass target)
    {
        ActionType action = base.What_Action(target);

        if (action == ActionType.ACTION_SELF && (Cargo.How_Many() == 0 || Altitude > 0 || IsTethered))
            action = ActionType.ACTION_NONE;

        if (action == ActionType.ACTION_ATTACK && Class.Primary == WeaponType.WEAPON_NONE)
            action = ActionType.ACTION_NONE;

        if (Is_Owned_By_Player() && House.Is_Ally(target) && target.What_Am_I() == RTTIType.RTTI_BUILDING &&
            Transmit_Message(RadioMessageType.RADIO_CAN_LOAD, (TechnoClass)target) == RadioMessageType.RADIO_ROGER)
            action = ActionType.ACTION_ENTER;

        return action;
    }

    public override ActionType What_Action(int cell)
    {
        ActionType action = base.What_Action(cell);

        // Aircraft can't be sent into the shroud.
        if ((action == ActionType.ACTION_MOVE || action == ActionType.ACTION_ATTACK) && !Game.Map[cell].Is_Visible(Game.PlayerPtr))
            action = ActionType.ACTION_NOMOVE;

        if (action == ActionType.ACTION_ATTACK && Class.Primary == WeaponType.WEAPON_NONE)
            action = ActionType.ACTION_NONE;

        return action;
    }

    public byte Pose_Dir() => Class.Type == AircraftType.AIRCRAFT_TRANSPORT ? (byte)DirType.DIR_N : (byte)DirType.DIR_NE;

    /// <summary>Mission_Attack: helicopters pick a firing spot, fly there, fire twice, and repeat
    /// until out of ammo; planes hunt.</summary>
    public override int Mission_Attack()
    {
        if (Class.IsFixedWing)
        {
            Assign_Mission(MissionType.MISSION_HUNT);
            return 1;
        }

        const int VALIDATE_AZ = 0, PICK_ATTACK_LOCATION = 1, TAKE_OFF = 2, FLY_TO_POSITION = 3, FIRE_AT_TARGET = 4, FIRE_AT_TARGET2 = 5, RETURN_TO_BASE = 6;

        switch (Status)
        {
            case VALIDATE_AZ:
                Status = Game.Target_Legal(TarCom) ? PICK_ATTACK_LOCATION : RETURN_TO_BASE;
                break;

            case PICK_ATTACK_LOCATION:
                if (!Game.Target_Legal(TarCom))
                {
                    Status = RETURN_TO_BASE;
                }
                else
                {
                    Assign_Destination(Good_Fire_Location(TarCom));
                    Status = Game.Target_Legal(NavCom) ? TAKE_OFF : RETURN_TO_BASE;
                }
                break;

            case TAKE_OFF:
                if (!Game.Target_Legal(TarCom))
                {
                    Status = RETURN_TO_BASE;
                }
                else
                {
                    if (Process_Take_Off())
                    {
                        Status = FLY_TO_POSITION;
                        if (In_Radio_Contact() && Game.Map[TdMath.CoordCell(Coord)].Cell_Building() == Contact_With_Whom())
                            Transmit_Message(RadioMessageType.RADIO_OVER_OUT);

                        // Turn the body towards where it will fly.
                        int diff = SecondaryFacing.Difference(DirectionToTarget(NavCom));
                        diff = Math.Clamp(diff, -128, 128);
                        PrimaryFacing.Set((byte)(SecondaryFacing.Current + diff));
                    }
                    return 1;
                }
                break;

            case FLY_TO_POSITION:
                if (Game.Target_Legal(TarCom))
                {
                    if (!Game.Target_Legal(NavCom))
                    {
                        Status = PICK_ATTACK_LOCATION;
                        return 1;
                    }

                    int distance = Process_Fly_To(true);
                    if (distance < 0x0200)
                    {
                        SecondaryFacing.Set_Desired(DirectionToTarget(TarCom));
                        if (distance < 0x0010)
                        {
                            Status = FIRE_AT_TARGET;
                            Assign_Destination(Target.TARGET_NONE);
                        }
                    }
                    else
                    {
                        SecondaryFacing.Set_Desired(TdMath.Direction(Fire_Coord(0), Game.As_Coord(NavCom)));
                        return 1;
                    }
                }
                else
                {
                    Status = RETURN_TO_BASE;
                }
                return 1;

            case FIRE_AT_TARGET:
                if (!Game.Target_Legal(TarCom))
                {
                    Status = RETURN_TO_BASE;
                    return 1;
                }
                IsSecondShot = false;
                PrimaryFacing.Set_Desired(DirectionToTarget(TarCom));
                SecondaryFacing.Set_Desired(DirectionToTarget(TarCom));
                switch (Can_Fire(TarCom, 0))
                {
                    case FireErrorType.FIRE_CLOAKED:
                        Do_Uncloak();
                        break;
                    case FireErrorType.FIRE_OK:
                        Fire_At(TarCom, 0);
                        Game.Map[Game.As_Cell(TarCom)].Incoming(Coord, true);
                        Status = FIRE_AT_TARGET2;
                        break;
                    default:
                        Status = Ammo == 0 ? RETURN_TO_BASE : FIRE_AT_TARGET2;
                        break;
                }
                return 1;

            case FIRE_AT_TARGET2:
                if (!Game.Target_Legal(TarCom))
                {
                    Status = RETURN_TO_BASE;
                    return 1;
                }
                PrimaryFacing.Set_Desired(DirectionToTarget(TarCom));
                SecondaryFacing.Set_Desired(DirectionToTarget(TarCom));
                switch (Can_Fire(TarCom, 0))
                {
                    case FireErrorType.FIRE_CLOAKED:
                        Do_Uncloak();
                        break;
                    case FireErrorType.FIRE_REARM:
                        break;
                    case FireErrorType.FIRE_OK:
                        Fire_At(TarCom, 0);
                        Game.Map[Game.As_Cell(TarCom)].Incoming(Coord, true);
                        Status = Ammo != 0 ? PICK_ATTACK_LOCATION : RETURN_TO_BASE;
                        break;
                    default:
                        Status = Ammo == 0 ? RETURN_TO_BASE : PICK_ATTACK_LOCATION;
                        break;
                }
                break;

            case RETURN_TO_BASE:
                Assign_Destination(Target.TARGET_NONE);
                Enter_Idle_Mode();
                break;
        }
        return TdDefines.TICKS_PER_SECOND / 2;
    }

    /// <summary>New_LZ: a clear landing spot near the old one (spiralling out), or the old one.</summary>
    public uint New_LZ(uint oldlz, bool stable = false)
    {
        if (Game.Target_Legal(oldlz) && (!Is_LZ_Clear(oldlz) || !Cell_Seems_Ok(Game.As_Cell(oldlz))))
        {
            uint coord = Game.As_Coord(oldlz);
            for (int radius = 0; radius < 16; radius++)
            {
                int modifier = stable ? 0 : Game.Random_Pick((int)FacingType.FACING_N, (int)FacingType.FACING_NW);
                int lastcell = -1;
                for (int facing = 0; facing < (int)FacingType.FACING_COUNT; facing++)
                {
                    byte dir = (byte)((int)TdMath.FacingAdd((FacingType)facing, modifier) << 5);
                    int newcell = TdMath.CoordCell(TdMath.CoordMove(coord, dir, radius * (int)TdDefines.ICON_LEPTON_W));
                    if (Game.Map.In_Radar(newcell))
                    {
                        uint newtarget = Target.As_Target(newcell);
                        if (newcell != lastcell && Is_LZ_Clear(newtarget) && Cell_Seems_Ok(newcell))
                            return newtarget;
                        lastcell = newcell;
                    }
                }
            }
        }
        return oldlz;
    }

    public override uint Fire_Coord(int which) => TdMath.CoordMove(TdMath.CoordAdd(TdMath.XypCoord(0, -Altitude), Coord), SecondaryFacing.Current, 0x040);

    public override uint Target_Coord() => TdMath.CoordAdd(TdMath.XypCoord(0, -Altitude), Coord);

    public override RadioMessageType Receive_Message(RadioClass? from, RadioMessageType message, ref int param)
    {
        switch (message)
        {
            // Asked by a helipad: is this aircraft ready (loaded) for action?
            case RadioMessageType.RADIO_PREPARED:
                if (Game.Target_Legal(TarCom))
                    return RadioMessageType.RADIO_NEGATIVE;
                if ((Altitude == 0 && Ammo == Class.MaxAmmo) || (Altitude > 0 && Ammo > 0))
                    return RadioMessageType.RADIO_ROGER;
                return RadioMessageType.RADIO_NEGATIVE;

            case RadioMessageType.RADIO_RUN_AWAY:
                if (isLanding)
                {
                    isLanding = false;
                    isTakingOff = true;
                }
                Scatter(0, true);
                break;

            case RadioMessageType.RADIO_MOVE_HERE:
                base.Receive_Message(from, message, ref param);
                if (Target.Is_Target_Building((uint)param))
                {
                    if (Transmit_Message(RadioMessageType.RADIO_CAN_LOAD, Game.As_Techno((uint)param)) != RadioMessageType.RADIO_ROGER)
                        return RadioMessageType.RADIO_NEGATIVE;
                    Assign_Mission(MissionType.MISSION_ENTER);
                    Assign_Destination((uint)param);
                }
                else
                {
                    Assign_Mission(MissionType.MISSION_MOVE);
                    Assign_Destination((uint)param);
                }
                Commence();
                return RadioMessageType.RADIO_ROGER;

            case RadioMessageType.RADIO_NEED_TO_MOVE:
                base.Receive_Message(from, message, ref param);
                if (!Game.Target_Legal(NavCom) && !isTakingOff && !isLanding)
                    return RadioMessageType.RADIO_ROGER;
                return RadioMessageType.RADIO_NEGATIVE;

            case RadioMessageType.RADIO_IM_IN:
                if (Cargo.How_Many() == Class.Max_Passengers())
                    Door.Close_Door(5, 4);
                // Rescuing civilians: leave once one is aboard.
                if (from is InfantryClass { Class.IsCivilian: true, IsTechnician: false })
                    Assign_Mission(MissionType.MISSION_RETREAT);
                return RadioMessageType.RADIO_ATTACH;

            case RadioMessageType.RADIO_DOCKING:
                if (Class.IsTransporter && Cargo.How_Many() < Class.Max_Passengers())
                {
                    base.Receive_Message(from, message, ref param);
                    if (!IsTethered && !isLanding && !isTakingOff && Altitude == 0)
                    {
                        Door.Open_Door(5, 4);
                        if (Transmit_Message(RadioMessageType.RADIO_NEED_TO_MOVE, from) == RadioMessageType.RADIO_ROGER)
                        {
                            Desired_Load_Dir(from!, out int cell);
                            if (cell == 0)
                            {
                                Transmit_Message(RadioMessageType.RADIO_OVER_OUT, from);
                            }
                            else
                            {
                                param = (int)Target.As_Target(cell);
                                if (Transmit_Message(RadioMessageType.RADIO_MOVE_HERE, ref param, from) == RadioMessageType.RADIO_YEA_NOW_WHAT)
                                {
                                    param = (int)As_Target();
                                    Transmit_Message(RadioMessageType.RADIO_TETHER);
                                    if (Transmit_Message(RadioMessageType.RADIO_MOVE_HERE, ref param, from) != RadioMessageType.RADIO_ROGER)
                                        Transmit_Message(RadioMessageType.RADIO_OVER_OUT, from);
                                    else
                                        Contact_With_Whom()!.Unselect();
                                }
                            }
                        }
                    }
                    return RadioMessageType.RADIO_ROGER;
                }
                break;

            case RadioMessageType.RADIO_CAN_LOAD:
                if (!In_Radio_Contact() && Class.IsTransporter && Cargo.How_Many() < Class.Max_Passengers() && from != null && House.Class.House == from.Owner() && Altitude == 0)
                    return RadioMessageType.RADIO_ROGER;
                return RadioMessageType.RADIO_NEGATIVE;
        }
        return base.Receive_Message(from, message, ref param);
    }

    /// <summary>Desired_Load_Dir: a free cell behind (south of) the helicopter for passengers; it faces north.</summary>
    public override byte Desired_Load_Dir(ObjectClass? passenger, out int moveto)
    {
        int center = TdMath.CoordCell(Center_Coord());
        for (int sweep = (int)FacingType.FACING_N; sweep < (int)FacingType.FACING_S; sweep++)
        {
            moveto = TdMath.AdjacentCellOf(center, TdMath.FacingAdd(FacingType.FACING_S, sweep));
            if (Game.Map.In_Radar(moveto) && (TdMath.CoordCell(passenger!.Center_Coord()) == moveto || Game.Map[moveto].Is_Generally_Clear()))
                return (byte)DirType.DIR_N;

            moveto = TdMath.AdjacentCellOf(center, TdMath.FacingSub(FacingType.FACING_S, sweep));
            if (Game.Map.In_Radar(moveto) && (TdMath.CoordCell(passenger!.Center_Coord()) == moveto || Game.Map[moveto].Is_Generally_Clear()))
                return (byte)DirType.DIR_N;
        }
        // As the original: the last cell tried is left in moveto.
        moveto = TdMath.AdjacentCellOf(center, TdMath.FacingSub(FacingType.FACING_S, 3));
        return (byte)DirType.DIR_N;
    }

    /// <summary>Process_Take_Off: climbs; facing and speed change at set heights. True at flight level.</summary>
    bool Process_Take_Off()
    {
        isLanding = false;
        isTakingOff = true;

        switch (Altitude)
        {
            case 0:
                Door.Close_Door(5, 4);
                PrimaryFacing.Copy_From(SecondaryFacing);
                break;
            case FLIGHT_LEVEL / 2:
                PrimaryFacing.Set_Desired(DirectionToTarget(NavCom));
                break;
            case FLIGHT_LEVEL - (FLIGHT_LEVEL / 3):
                SecondaryFacing.Set_Desired(PrimaryFacing.Desired);
                Set_Speed(0x20);
                break;
            case FLIGHT_LEVEL - (FLIGHT_LEVEL / 5):
                Set_Speed(0x40);
                break;
            case FLIGHT_LEVEL:
                Set_Speed(0xFF);
                isTakingOff = false;
                return true;
        }
        return false;
    }

    /// <summary>Process_Landing: descends (stopping half-way down); true when on the ground.</summary>
    bool Process_Landing()
    {
        isTakingOff = false;
        isLanding = true;

        switch (Altitude)
        {
            case 0:
                isLanding = false;
                return true;
            case FLIGHT_LEVEL / 2:
                Set_Speed(0);
                break;
        }
        return false;
    }

    /// <summary>Can_Enter_Cell: aircraft land only on visible, generally clear cells.</summary>
    public override MoveType Can_Enter_Cell(int cell, FacingType facing = FacingType.FACING_NONE)
    {
        if (!Game.Map.In_Radar(cell))
            return MoveType.MOVE_NO;
        var cellptr = Game.Map[cell];
        if (!cellptr.Is_Generally_Clear(true))
            return MoveType.MOVE_NO;
        if (Game.GameToPlay == GameType.GAME_NORMAL && IsOwnedByPlayer && !cellptr.Is_Visible(Game.PlayerPtr))
            return MoveType.MOVE_NO;
        return MoveType.MOVE_OK;
    }

    /// <summary>Good_Fire_Location: the nearest of the spots in range round the target (or the
    /// second nearest, at random) where the helicopter can hover.</summary>
    uint Good_Fire_Location(uint target)
    {
        if (Game.Target_Legal(target))
        {
            int range = Weapon_Range(0);
            uint tcoord = Game.As_Coord(target);
            int bestcell = 0, best2cell = 0;
            int bestval = -1, best2val = -1;

            for (int r = range - 0x0180; r > 0x0180; r -= 0x0100)
            {
                for (int face = 0; face < 255; face += 16)
                {
                    uint newcoord = TdMath.CoordMove(tcoord, (byte)face, r);
                    int newcell = TdMath.CoordCell(newcoord);
                    int actualcell = TdMath.CoordCell(TdMath.CoordSub(newcoord, TdMath.XYPixel_Coord(0, FLIGHT_LEVEL)));

                    if (Game.Map.In_Radar(actualcell) && (Game.GameToPlay != GameType.GAME_NORMAL || Game.Map[newcell].Is_Visible(Game.PlayerPtr)) && Cell_Seems_Ok(newcell, true))
                    {
                        int dist = Distance(newcoord);
                        if (bestval == -1 || dist < bestval)
                        {
                            best2val = bestval;
                            best2cell = bestcell;
                            bestval = dist;
                            bestcell = newcell;
                        }
                    }
                }
                if (bestval != -1)
                    break;
            }

            if (best2val == -1)
                best2cell = bestcell;

            if (bestval != -1)
                return Target.As_Target(Game.Random_Pick(0, 1) == 0 ? bestcell : best2cell);
        }
        return Target.TARGET_NONE;
    }

    /// <summary>Cell_Seems_Ok: no other aircraft is in or heading for the cell.</summary>
    bool Cell_Seems_Ok(int cell, bool strict = false)
    {
        uint astarget = Target.As_Target(cell);
        for (int index = 0; index < Game.Aircraft.Count; index++)
        {
            var air = Game.Aircraft.Ptr(index);
            if ((strict || air != this) && !air.IsInLimbo)
            {
                if (TdMath.CoordCell(air.Coord) == cell || air.NavCom == astarget)
                    return false;
            }
        }
        return true;
    }

    public override int Pip_Count()
    {
        int retval = 0;
        if (Class.IsTransporter)
        {
            retval = Cargo.How_Many();
        }
        else if (Ammo != 0)
        {
            retval = (int)TdMath.CardinalToFixed((uint)Class.MaxAmmo, (uint)Ammo);
            retval = (int)TdMath.FixedToCardinal((uint)Class.Max_Pips(), (uint)retval);
            if (retval == 0)
                retval = 1;
        }
        return retval;
    }

    /// <summary>Mission_Enter: take off, fly to the helipad (or transport target), land and dock.</summary>
    public override int Mission_Enter()
    {
        const int INITIAL = 0, TAKEOFF = 1, ALTITUDE = 2, TRAVEL = 3, LANDING = 4;

        switch (Status)
        {
            case INITIAL:
                Status = Altitude < FLIGHT_LEVEL || isLanding ? TAKEOFF : ALTITUDE;
                break;

            case TAKEOFF:
                if (Process_Take_Off())
                {
                    if (In_Radio_Contact() && Game.Map[TdMath.CoordCell(Coord)].Cell_Building() == Contact_With_Whom())
                        Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                    Status = ALTITUDE;
                }
                break;

            case ALTITUDE:
                if (In_Radio_Contact())
                {
                    Status = TRAVEL;
                }
                else
                {
                    var tech = Game.As_Techno(NavCom);
                    if (tech != null && Transmit_Message(RadioMessageType.RADIO_CAN_LOAD, tech) == RadioMessageType.RADIO_ROGER)
                    {
                        Transmit_Message(RadioMessageType.RADIO_HELLO, tech);
                        Transmit_Message(RadioMessageType.RADIO_DOCKING);
                        Status = TRAVEL;
                    }
                    else
                    {
                        Assign_Destination(Target.TARGET_NONE);
                        Enter_Idle_Mode();
                    }
                }
                break;

            case TRAVEL:
                Transmit_Message(RadioMessageType.RADIO_DOCKING);
                if (!In_Radio_Contact())
                {
                    Assign_Destination(Target.TARGET_NONE);
                    Enter_Idle_Mode();
                }
                else
                {
                    int distance = Process_Fly_To(true);
                    if (distance < 0x0080)
                    {
                        SecondaryFacing.Set_Desired(Game.Target_Legal(TarCom) ? DirectionToTarget(TarCom) : Pose_Dir());
                        if (distance < 0x0010)
                            Status = LANDING;
                        break;
                    }
                    SecondaryFacing.Set_Desired(TdMath.Direction(Fire_Coord(0), Game.As_Coord(NavCom)));
                    return 3;
                }
                break;

            case LANDING:
                if (isTakingOff)
                {
                    Assign_Destination(Target.TARGET_NONE);
                    Enter_Idle_Mode();
                }
                if (Process_Landing())
                {
                    switch (Transmit_Message(RadioMessageType.RADIO_IM_IN))
                    {
                        case RadioMessageType.RADIO_ROGER:
                            Assign_Mission(MissionType.MISSION_GUARD);
                            break;
                        case RadioMessageType.RADIO_ATTACH:
                            Limbo();
                            Contact_With_Whom()!.Cargo.Attach(this);
                            break;
                        default:
                            Enter_Idle_Mode();
                            break;
                    }
                }
                break;
        }
        return 1;
    }

    /// <summary>Good_LZ: next to the nearest of the house's buildings (helipads count as 4x closer).</summary>
    uint Good_LZ()
    {
        int bestcell = 0;
        int bestdist = -1;
        for (int index = 0; index < Game.Buildings.Count; index++)
        {
            var building = Game.Buildings.Ptr(index);
            if (!building.IsInLimbo && building.House == House)
            {
                int dist = Distance(building);
                if (building.Class.Type == StructType.STRUCT_HELIPAD)
                    dist /= 4;
                if (bestdist == -1 || dist < bestdist)
                {
                    bestdist = dist;
                    bestcell = TdMath.CoordCell(building.Center_Coord());
                }
            }
        }
        if (bestdist != -1)
            return Target.As_Target(bestcell);
        return Target.As_Target(TdMath.CoordCell(Coord));
    }

    /// <summary>Set_Speed: the flying speed (FootClass's own speed is not used).</summary>
    public override void Set_Speed(int speed)
    {
        var sp = (MPHType)Math.Min((uint)((int)Class.MaxSpeed * House.AirspeedBias), (uint)MPHType.MPH_LIGHT_SPEED);
        Fly.Fly_Speed(speed, sp);
    }

    public override byte Fire_Direction() => SecondaryFacing.Current;

    /// <summary>Scatter: a landed helicopter takes off and finds something to do.</summary>
    public override void Scatter(uint threat, bool forced = false, bool nokidding = false)
    {
        if (isLanding || Altitude == 0)
        {
            isLanding = false;
            isTakingOff = true;
        }
        Enter_Idle_Mode();
    }

    public override int Rearm_Delay(bool second = true) => base.Rearm_Delay(second) / 2;

    public override int Threat_Range(int control)
    {
        if (control == -1)
            return -1;
        int range = 20 * (int)TdDefines.ICON_LEPTON_W;
        if (control == 1)
            range *= 2;
        return range;
    }

    /// <summary>Mission_Guard: in the air, land or attack; on the ground the computer repairs, rearms or attacks.</summary>
    public override int Mission_Guard()
    {
        if (Altitude == FLIGHT_LEVEL)
        {
            if (Team != null)
            {
                if (Game.Target_Legal(NavCom))
                    Assign_Mission(MissionType.MISSION_MOVE);
                return TdDefines.TICKS_PER_SECOND;
            }

            if (Class.Primary == WeaponType.WEAPON_NONE)
            {
                Assign_Destination(Target.As_Target(TdMath.CoordCell(Coord)));
                Assign_Mission(MissionType.MISSION_MOVE);
            }
            else
            {
                Enter_Idle_Mode();
            }
            return 1;
        }

        if (House.IsHuman)
            return TdDefines.TICKS_PER_SECOND;

        // GDI mission 7: GDI's own helicopters stay put.
        if (Game.GameToPlay == GameType.GAME_NORMAL && Game.Scenario == 7 && House.Class.House == HousesType.HOUSE_GOOD)
            return TdDefines.TICKS_PER_SECOND * 20;

        if (House.Available_Money() >= 100 && Health_Ratio() <= 0x0080)
        {
            if (!In_Radio_Contact() ||
                (Altitude == 0 && Contact_With_Whom() is not BuildingClass { Class.Type: StructType.STRUCT_REPAIR }))
            {
                var building = Find_Docking_Bay(StructType.STRUCT_REPAIR, true);
                if (building != null)
                {
                    Assign_Destination(building.As_Target());
                    Assign_Target(Target.TARGET_NONE);
                    Assign_Mission(MissionType.MISSION_ENTER);
                    return 1;
                }
            }
        }

        if (Ammo == 0 && Class.Primary != WeaponType.WEAPON_NONE && !In_Radio_Contact())
        {
            var building = Find_Docking_Bay(StructType.STRUCT_HELIPAD, false);
            if (building != null)
            {
                Assign_Destination(building.As_Target());
                Assign_Target(Target.TARGET_NONE);
                Assign_Mission(MissionType.MISSION_ENTER);
                return 1;
            }
        }

        if (Game.Target_Legal(TarCom))
        {
            Assign_Mission(MissionType.MISSION_ATTACK);
            return 1;
        }

        if (Class.Primary == WeaponType.WEAPON_NONE)
            return TdDefines.TICKS_PER_SECOND * 3;

        if (Altitude == 0 && !In_Radio_Contact())
        {
            Scatter(0, true);
            return TdDefines.TICKS_PER_SECOND * 3;
        }

        return base.Mission_Guard();
    }

    public override int Mission_Guard_Area()
    {
        if (Altitude == FLIGHT_LEVEL)
        {
            Enter_Idle_Mode();
            return 1;
        }

        if (House.IsHuman)
            return TdDefines.TICKS_PER_SECOND;

        if (Altitude == 0 && !In_Radio_Contact())
        {
            Scatter(0, true);
            return TdDefines.TICKS_PER_SECOND * 3;
        }

        if (Game.Target_Legal(TarCom))
        {
            Assign_Mission(MissionType.MISSION_ATTACK);
            return 1;
        }

        return base.Mission_Guard_Area();
    }

    static readonly VocType[] AttackResponses = [VocType.VOC_AFFIRM, VocType.VOC_ACKNOWL, VocType.VOC_YESSIR, VocType.VOC_YESSIR, VocType.VOC_YESSIR];
    static readonly VocType[] MoveResponses = [VocType.VOC_MOVEOUT, VocType.VOC_MOVEOUT, VocType.VOC_MOVEOUT, VocType.VOC_ACKNOWL, VocType.VOC_AFFIRM, VocType.VOC_AFFIRM];
    static readonly VocType[] SelectResponses = [VocType.VOC_VEHIC, VocType.VOC_UNIT, VocType.VOC_YESSIR, VocType.VOC_YESSIR, VocType.VOC_YESSIR, VocType.VOC_AWAIT];

    void Respond(VocType[] list)
    {
        VocType response = list[Game.Sim_Random_Pick(0, list.Length - 1)];
        if (Game.AllowVoice)
            Game.Sound_Effect(response, 0, -(Game.Aircraft.ID(this) + 1));
    }

    public override void Response_Attack() => Respond(AttackResponses);
    public override void Response_Move() => Respond(MoveResponses);
    public override void Response_Select() => Respond(SelectResponses);

    public void Set_Reinforcement_Delay(long delay) => reinforcementStart = Game.Frame + delay;

    /// <summary>Whether the cargo plane is still waiting off-map (modern balance) — Draw_It hides it then.</summary>
    public bool Is_Waiting_Off_Map() =>
        Game.Special.ModernBalance && Class.Type == AircraftType.AIRCRAFT_CARGO && !Game.Map.In_Radar(TdMath.CoordCell(Coord)) && reinforcementStart > Game.Frame;
}

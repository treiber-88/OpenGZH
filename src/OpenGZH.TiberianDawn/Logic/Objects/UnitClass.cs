using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// UnitClass (UNIT.CPP): a ground vehicle (or boat, or dinosaur). Harvesting, the APC's doors,
/// the MCV deploying, the hovercraft landing, crushing, flags (capture the flag) and the
/// vehicle's movement rules (Can_Enter_Cell).
/// </summary>
public sealed class UnitClass : TarComClass
{
    public HousesType Flagged = HousesType.HOUSE_NONE;
    readonly CountDownTimer harvestTimer;
    BuildingClass? tiberiumUnloadRefinery;

    UnitClass(TdGame game, UnitType classid, HousesType house) : base(game, classid, house)
    {
        harvestTimer = new CountDownTimer(game);
        Flagged = HousesType.HOUSE_NONE;
        Reload.Set(0);
        Ammo = Class.MaxAmmo;
        IsCloakable = Class.IsCloakable;
        tiberiumUnloadRefinery = null;
        if (Class.IsAnimating)
            Stage.Set_Rate(Game.Options.Normalize_Delay(3));
        if (Game.GameToPlay == GameType.GAME_INTERNET)
            House.UnitTotals.Increment_Unit_Total((int)classid);
        House.Tracking_Add(this);
    }

    /// <summary>new UnitClass: takes the first free slot of the unit heap (null when full).</summary>
    public static UnitClass? Create(TdGame game, UnitType classid, HousesType house)
    {
        int slot = game.Units.Allocate();
        if (slot < 0)
            return null;
        var unit = new UnitClass(game, classid, house);
        game.Units.Place(slot, unit);
        unit.Set_Active();
        return unit;
    }

    /// <summary>~UnitClass: passengers die with the unit, and it leaves the map and its team.</summary>
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
        Game.Units.Free(this);
    }

    public override RTTIType What_Am_I() => RTTIType.RTTI_UNIT;
    public override uint As_Target() => Target.Build_Target(KindType.KIND_UNIT, Game.Units.ID(this));

    public override uint Sort_Y()
    {
        if (IsTethered && Class.Type == UnitType.UNIT_HOVER)
            return TdMath.CoordAdd(Coord, 0xFF800000);
        return TdMath.CoordAdd(Coord, 0x00800000);
    }

    public override void AI()
    {
        if (!IsDriving && Door.Is_Door_Closed())
            Commence();

        base.AI();

        // A unit sent off the map on guard (and not in a team) is gone.
        if (Team == null && Mission == MissionType.MISSION_GUARD && MissionQueue == MissionType.MISSION_NONE && !Game.Map.In_Radar(TdMath.CoordCell(Coord)))
        {
            Stun();
            Delete_This();
            return;
        }

        // The mobile SAM reloads when stopped.
        if (Class.Type == UnitType.UNIT_MSAM && Ammo < Class.MaxAmmo)
        {
            if (IsDriving)
            {
                Reload.Set(Reload.Time + 1);
            }
            else if (Reload.Expired)
            {
                Ammo++;
                if (Ammo < Class.MaxAmmo)
                    Reload.Set(TdDefines.TICKS_PER_SECOND * 30);
                Mark(MarkType.MARK_CHANGE);
            }
        }

        if (Class.Type == UnitType.UNIT_HOVER)
            Mark(MarkType.MARK_CHANGE);

        // The Mammoth tank heals itself up to half, the Visceroid in Tiberium.
        if (Class.Type == UnitType.UNIT_HTANK && (Game.Frame % 16) == 0 && Health_Ratio() < 0x0080)
        {
            Strength++;
            Mark(MarkType.MARK_CHANGE);
        }
        if (Class.Type == UnitType.UNIT_VICE && Game.Map[TdMath.CoordCell(Coord)].Land_Type() == LandType.LAND_TIBERIUM && Health_Ratio() < 0x0100 && (Game.Frame % 16) == 0)
        {
            Strength++;
            Mark(MarkType.MARK_CHANGE);
        }

        if (Class.IsTransporter)
        {
            if (!Door.Is_Door_Closed() && Mission != MissionType.MISSION_UNLOAD && Transmit_Message(RadioMessageType.RADIO_TRYING_TO_LOAD) != RadioMessageType.RADIO_ROGER)
                APC_Close_Door();
        }

        if (!IsDriving && Door.Is_Door_Closed())
            Commence();

        if (IsInRecoilState)
        {
            IsInRecoilState = false;
            Mark(MarkType.MARK_CHANGE);
        }

        if (Class.IsAnimating)
        {
            if (Stage.Fetch_Rate() == 0)
                Stage.Set_Rate(2);
            Stage.Graphic_Logic();
            if (Stage.Fetch_Stage() >= Class.ImageFrames - 1)
                Stage.Set_Stage(0);
        }

        // Dinosaurs (eight-facing creatures) animate walking and biting.
        if (Class.IsPieceOfEight)
        {
            if (IsDriving || IsFiring)
            {
                if (Stage.Fetch_Rate() == 0)
                {
                    Stage.Set_Rate(Game.Options.Normalize_Delay(2));
                    Stage.Set_Stage(0);
                }
                Stage.Graphic_Logic();
                bool shortCycle = IsDriving || Class.Type == UnitType.UNIT_TREX || Class.Type == UnitType.UNIT_RAPT;
                if (Stage.Fetch_Stage() >= (shortCycle ? 8 : 12))
                {
                    Stage.Set_Stage(0);
                    if (IsFiring)
                    {
                        Stage.Set_Rate(0);
                        IsFiring = false;
                    }
                }
            }
        }

        // Don't park on a building.
        if (!IsTethered && !IsFiring && !IsDriving && !IsRotating && (Mission == MissionType.MISSION_GUARD || Mission == MissionType.MISSION_GUARD_AREA) &&
            MissionQueue == MissionType.MISSION_NONE && Game.Map[TdMath.CoordCell(Coord)].Cell_Building() != null)
            Scatter(0, true, true);

        if (Cloak == CloakType.CLOAKED && Flagged != HousesType.HOUSE_NONE)
            Do_Shimmer();
    }

    /// <summary>Can_Fire: units with a firing animation play it before the shot leaves.</summary>
    public override FireErrorType Can_Fire(uint target, int which = 0)
    {
        FireErrorType cf = base.Can_Fire(target, which);
        if (cf == FireErrorType.FIRE_OK && Class.IsFireAnim)
        {
            if (!IsFiring)
            {
                Stage.Set_Rate(Game.Options.Normalize_Delay(2));
                Stage.Set_Stage(0);
                IsFiring = true;
                cf = FireErrorType.FIRE_BUSY;
            }
            else if (Stage.Fetch_Stage() < 4)
            {
                cf = FireErrorType.FIRE_BUSY;
            }
        }
        return cf;
    }

    public override RadioMessageType Receive_Message(RadioClass? from, RadioMessageType message, ref int param)
    {
        switch (message)
        {
            case RadioMessageType.RADIO_NEED_REPAIR:
                if (!IsDriving && !Game.Target_Legal(NavCom) && Health_Ratio() >= 0x100)
                    return RadioMessageType.RADIO_NEGATIVE;
                break;

            case RadioMessageType.RADIO_CAN_LOAD:
                if (Class.IsTransporter && Cargo.How_Many() < Class.Max_Passengers() && from != null && House.Class.House == from.Owner())
                    return RadioMessageType.RADIO_ROGER;
                return RadioMessageType.RADIO_NEGATIVE;

            case RadioMessageType.RADIO_BACKUP_NOW:
                // A harvester backs into the refinery: face south-west, then reverse in.
                base.Receive_Message(from, message, ref param);
                if (!IsRotating && PrimaryFacing.Current != (byte)DirType.DIR_SW)
                {
                    Do_Turn((byte)DirType.DIR_SW);
                }
                else if (!IsDriving)
                {
                    Force_Track(BACKUP_INTO_REFINERY, TdMath.AdjacentCellCoord(Center_Coord(), TdMath.Facing.N));
                    Set_Speed(128);
                }
                return RadioMessageType.RADIO_ROGER;

            case RadioMessageType.RADIO_IM_IN:
                if (Cargo.How_Many() == Class.Max_Passengers())
                    APC_Close_Door();
                return RadioMessageType.RADIO_ATTACH;

            case RadioMessageType.RADIO_DOCKING:
                if (Contact_With_Whom() != from)
                {
                    if (Cargo.How_Many() >= Class.Max_Passengers())
                        return RadioMessageType.RADIO_NEGATIVE;
                    if (!In_Radio_Contact())
                        Transmit_Message(RadioMessageType.RADIO_HELLO, from);
                    else
                        return RadioMessageType.RADIO_ROGER;
                }

                // The APC turns its door to the infantry and waits for them.
                if (Class.IsTransporter && Class.Type == UnitType.UNIT_APC && Cargo.How_Many() < Class.Max_Passengers())
                {
                    base.Receive_Message(from, message, ref param);
                    bool isBusy = Mission == MissionType.MISSION_MOVE;

                    if ((!IsRotating || Game.Target_Legal(TarCom)) && !IsTethered)
                    {
                        if (Transmit_Message(RadioMessageType.RADIO_NEED_TO_MOVE, from) == RadioMessageType.RADIO_ROGER)
                        {
                            int cell;
                            byte dir;
                            if (Game.Target_Legal(TarCom))
                            {
                                dir = PrimaryFacing.Current;
                                cell = TdMath.AdjacentCellOf(TdMath.CoordCell(Coord), TdMath.DirFacingType(Direction(from!)));
                            }
                            else
                            {
                                dir = Desired_Load_Dir(from!, out cell);
                            }

                            if (cell == 0)
                            {
                                Transmit_Message(RadioMessageType.RADIO_OVER_OUT, from);
                            }
                            else
                            {
                                param = (int)Target.As_Target(cell);
                                if (!isBusy && !IsDriving)
                                {
                                    Do_Turn(dir);
                                    if (IsRotating)
                                    {
                                        if (!Door.Is_Door_Closed())
                                            APC_Close_Door();
                                    }
                                    else if (!Door.Is_Door_Open())
                                    {
                                        APC_Open_Door();
                                    }
                                }

                                if (Transmit_Message(RadioMessageType.RADIO_MOVE_HERE, ref param, from) == RadioMessageType.RADIO_YEA_NOW_WHAT)
                                {
                                    if (Door.Is_Door_Open() || Game.Target_Legal(TarCom))
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
                    }
                    return RadioMessageType.RADIO_ROGER;
                }
                break;

            case RadioMessageType.RADIO_RUN_AWAY:
                // A harvester told off by a busy refinery goes back to harvesting.
                if (Class.IsToHarvest && In_Radio_Contact() && Mission == MissionType.MISSION_ENTER)
                {
                    var contact = Contact_With_Whom();
                    if (contact is BuildingClass { Class.Type: StructType.STRUCT_REFINERY })
                    {
                        Assign_Mission(MissionType.MISSION_HARVEST);
                        TarCom = As_Target();
                        return RadioMessageType.RADIO_ROGER;
                    }
                }
                return base.Receive_Message(from, message, ref param);

            case RadioMessageType.RADIO_OVER_OUT:
                if (Mission == MissionType.MISSION_RETURN)
                    Assign_Mission(MissionType.MISSION_GUARD);
                base.Receive_Message(from, message, ref param);
                return RadioMessageType.RADIO_ROGER;
        }
        return base.Receive_Message(from, message, ref param);
    }

    public override bool Unlimbo(uint coord, byte dir = 0)
    {
        // Units always face one of the eight compass directions.
        dir = (byte)((int)TdMath.DirFacingType(dir) << 5);

        if (base.Unlimbo(coord, dir))
        {
            House.UScan |= 1L << (int)Class.Type;
            House.ActiveUScan |= 1L << (int)Class.Type;

            if (IsCloakable && !IsLocked)
                Cloak = CloakType.CLOAKED;

            Stage.Set_Rate(0);
            Stage.Set_Stage(0);

            if (Class.Type is UnitType.UNIT_GUNBOAT or UnitType.UNIT_HOVER)
                IsALoaner = true;

            if (Class.Type == UnitType.UNIT_GUNBOAT)
            {
                Stage.Set_Rate(2);
                Stage.Set_Stage(0);
            }
            return true;
        }
        return false;
    }

    public override ResultType Take_Damage(ref int damage, int distance, WarheadType warhead, TechnoClass? source = null)
    {
        // The gunboat is extra vulnerable to the Obelisk.
        if (Class.Type == UnitType.UNIT_GUNBOAT && warhead == WarheadType.WARHEAD_LASER)
            damage = Strength * 3;

        bool select = Is_Selected_By_Player() && Is_Owned_By_Player();
        ResultType res = base.Take_Damage(ref damage, distance, warhead, source);

        if (res == ResultType.RESULT_DESTROYED)
        {
            Death_Announcement(source);

            if (Class.Explosion != AnimType.ANIM_NONE)
            {
                AnimType anim = Class.Explosion;
                if (Class.Type == UnitType.UNIT_MSAM && Ammo != 0)
                    anim = AnimType.ANIM_NAPALM3;

                if (Class.Type is UnitType.UNIT_TRIC or UnitType.UNIT_TREX or UnitType.UNIT_RAPT or UnitType.UNIT_STEG)
                    Game.Sound_Effect(VocType.VOC_DINODIE1, Coord);

                AnimClass.Create(Game, anim, Coord);

                if (Class.Explosion == AnimType.ANIM_NAPALM3)
                    Game.Explosion_Damage(Coord, 200, source, WarheadType.WARHEAD_FIRE);

                if (Class.MaxStrength > 400)
                {
                    Game.Shake_The_Screen(3, Owner());
                    if (source != null && Owner() != source.Owner())
                        Game.Shake_The_Screen(3, source.Owner());
                }
            }

            Mark(MarkType.MARK_UP);

            // The crew may survive; transports' passengers get out.
            if (Class.IsCrew && !Class.IsTransporter)
            {
                if (Game.Random_Pick(0, 1) == 0)
                {
                    InfantryClass? i;
                    if (Class.Primary == WeaponType.WEAPON_NONE)
                    {
                        i = InfantryClass.Create(Game, InfantryType.INFANTRY_C1, House.Class.House);
                        if (i != null)
                            i.IsTechnician = true;
                    }
                    else
                    {
                        i = InfantryClass.Create(Game, InfantryType.INFANTRY_E1, House.Class.House);
                    }

                    if (i != null)
                    {
                        if (i.Unlimbo(Coord, (byte)DirType.DIR_N))
                        {
                            i.Strength = Game.Random_Pick(5, i.Class.MaxStrength / 2);
                            i.Scatter(0, true);
                            i.Assign_Mission(!House.IsHuman ? MissionType.MISSION_HUNT : MissionType.MISSION_GUARD);
                            if (select)
                                i.Select();
                        }
                        else
                        {
                            i.Delete();
                        }
                    }
                }
            }
            else if (Class.Type != UnitType.UNIT_HOVER)
            {
                while (Cargo.Is_Something_Attached())
                {
                    var obj = Cargo.Detach_Object();
                    if (obj == null)
                        break;
                    if (obj.Is_Infantry() && obj.Unlimbo(Coord, (byte)DirType.DIR_N))
                    {
                        obj.Look(false);
                        obj.Scatter(0, true);
                        if (select)
                            obj.Select();
                    }
                    else
                    {
                        obj.Record_The_Kill(source);
                        obj.Delete();
                    }
                }
            }
            else
            {
                Kill_Cargo(source);
            }

            // Losing the mobile HQ (capture the flag) loses the game.
            if (Class.Type == UnitType.UNIT_MHQ)
                House.Flag_To_Die();

            if (Class.Type == UnitType.UNIT_MCV)
                House?.Check_Pertinent_Structures();

            Delete_This();
        }
        else
        {
            // Damaged units smoke (not the living ones).
            if (Health_Ratio() < 0x0080 && !IsAnimAttached && Class.Type is not (UnitType.UNIT_VICE or UnitType.UNIT_STEG or UnitType.UNIT_TREX or UnitType.UNIT_TRIC or UnitType.UNIT_RAPT))
            {
                var anim = AnimClass.Create(Game, AnimType.ANIM_SMOKE_M, TdMath.CoordAdd(Coord, TdMath.XypCoord(0, -8)));
                anim?.Attach_To(this);
            }

            if (res == ResultType.RESULT_HALF && Class.Type == UnitType.UNIT_GUNBOAT)
            {
                var anim = AnimClass.Create(Game, AnimType.ANIM_ON_FIRE_MED, TdMath.CoordAdd(Coord, TdMath.XypCoord(Game.Random_Pick(0, 16) - 8, -2)));
                anim?.Attach_To(this);
            }

            // Computer (and smart-defense) crushers run over infantry shooting at them; damaged
            // harvesters go home.
            if (Team == null && source != null && !IsTethered && !House.Is_Ally(source) && (!House.IsHuman || Game.Special.IsSmartDefense))
            {
                bool normal = Game.GameToPlay == GameType.GAME_NORMAL;
                bool unarmedOrClose = Class.Primary == WeaponType.WEAPON_NONE ||
                    (Distance(source) < 0x0180 && Game.Types.Bullet(Game.Types.Weapon(Class.Primary).Fires).Warhead != WarheadType.WARHEAD_FIRE);
                if (unarmedOrClose &&
                    (!normal || Class.Type != UnitType.UNIT_HARVESTER || Game.BuildLevel > 8 || Game.PlayerPtr.Difficulty == DiffType.DIFF_HARD) &&
                    !(normal && Game.PlayerPtr.Difficulty == DiffType.DIFF_EASY) &&
                    Class.IsCrusher && source.Is_Techno() && source.Techno_Type_Class().IsCrushable)
                {
                    Assign_Destination(source.As_Target());
                    Assign_Mission(MissionType.MISSION_MOVE);
                }
                else if (Class.Type == UnitType.UNIT_HARVESTER && Pip_Count() != 0 && Health_Ratio() < 0x0080)
                {
                    var building = Find_Docking_Bay(StructType.STRUCT_REFINERY, false);
                    if (building != null && Transmit_Message(RadioMessageType.RADIO_HELLO, building) == RadioMessageType.RADIO_ROGER)
                        Assign_Mission(MissionType.MISSION_ENTER);
                }
            }

            if (Class.Type == UnitType.UNIT_HARVESTER && !House.IsHuman && source != null)
                Base_Is_Attacked(source);
        }
        return res;
    }

    /// <summary>Active_Click_With: an action the unit can't do here turns into one it can.</summary>
    public override void Active_Click_With(ActionType action, ObjectClass obj)
    {
        if (action != What_Action(obj))
        {
            action = action switch
            {
                ActionType.ACTION_SABOTAGE or ActionType.ACTION_CAPTURE => ActionType.ACTION_ATTACK,
                ActionType.ACTION_ENTER => ActionType.ACTION_MOVE,
                _ => ActionType.ACTION_NONE,
            };
        }
        base.Active_Click_With(action, obj);
    }

    public override void Player_Assign_Mission(MissionType mission, uint target = Target.TARGET_NONE, uint destination = Target.TARGET_NONE)
    {
        if (mission == MissionType.MISSION_HARVEST)
        {
            ArchiveTarget = Target.TARGET_NONE;
        }
        else if (mission == MissionType.MISSION_ENTER)
        {
            var building = Game.As_Building(destination);
            if (building != null && building.Class.Type == StructType.STRUCT_REFINERY && building.In_Radio_Contact())
                building.Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
        }
        base.Player_Assign_Mission(mission, target, destination);
    }

    /// <summary>Enter_Idle_Mode: harvest, unload, attack, move, guard (or hunt, for the computer in multiplayer).</summary>
    public override void Enter_Idle_Mode(bool initial = false)
    {
        MissionType order;

        if (Mission == MissionType.MISSION_MOVE && !Game.Target_Legal(NavCom))
            Transmit_Message(RadioMessageType.RADIO_OVER_OUT);

        if (Class.Primary == WeaponType.WEAPON_NONE)
        {
            if (Class.IsToHarvest)
            {
                if (!In_Radio_Contact() && Mission != MissionType.MISSION_HARVEST)
                {
                    if (initial || !House.IsHuman || Game.Map[TdMath.CoordCell(Coord)].Land_Type() == LandType.LAND_TIBERIUM)
                        order = MissionType.MISSION_HARVEST;
                    else
                        order = MissionType.MISSION_GUARD;
                    Assign_Target(Target.TARGET_NONE);
                    Assign_Destination(Target.TARGET_NONE);
                }
                else
                {
                    return;
                }
            }
            else if (IsALoaner && Class.IsTransporter && Cargo.Is_Something_Attached() && Team == null)
            {
                order = MissionType.MISSION_UNLOAD;
            }
            else
            {
                order = MissionType.MISSION_GUARD;
                Assign_Target(Target.TARGET_NONE);
                Assign_Destination(Target.TARGET_NONE);
            }
        }
        else if (Game.Target_Legal(TarCom))
        {
            order = MissionType.MISSION_ATTACK;
        }
        else if (Game.Target_Legal(NavCom))
        {
            order = MissionType.MISSION_MOVE;
        }
        else if (Game.GameToPlay == GameType.GAME_NORMAL || House.IsHuman)
        {
            order = MissionType.MISSION_GUARD;
        }
        else
        {
            // The skirmish computer's idle units guard (an area, if smart and armed and teamless).
            order = MissionType.MISSION_GUARD;
            if (Mission == MissionType.MISSION_GUARD || Mission == MissionType.MISSION_GUARD_AREA)
                return;
            if (Team == null && House.IQ >= Game.Rule.IQGuardArea && Is_Weapon_Equipped())
                order = MissionType.MISSION_GUARD_AREA;
        }
        Assign_Mission(order);
    }

    /// <summary>Find_LZ: the hovercraft heads for the beach.</summary>
    public void Find_LZ()
    {
        if (Class.Type == UnitType.UNIT_HOVER && !IsRotating && Cargo.Is_Something_Attached() && !Game.Target_Legal(NavCom))
        {
            int cell = Game.Map.Calculated_Cell(SourceType.SOURCE_BEACH, House.Class.House);
            Assign_Destination(Target.As_Target(cell));
        }
    }

    /// <summary>Unload_Hovercraft_Process: everyone gets off onto the beach at once; true when empty.</summary>
    public bool Unload_Hovercraft_Process()
    {
        if (IsTethered || IsRotating)
            return false;

        if (Cargo.Is_Something_Attached())
        {
            if (!Game.Target_Legal(NavCom))
            {
                int cell = TdMath.CoordCell(TdMath.AdjacentCellCoord(Coord, (TdMath.Facing)(int)TdMath.DirFacingType(PrimaryFacing.Current)));
                FootClass unit = Cargo.Attached_Object()!;

                Mark(MarkType.MARK_UP);
                if (Game.Map.In_Radar(cell) && Game.Map[cell].Cell_Unit() == null)
                {
                    if (unit.Can_Enter_Cell(cell, FacingType.FACING_NONE) == MoveType.MOVE_OK)
                    {
                        int count = 0;
                        bool first = true;
                        FootClass? secondary = null;
                        while (Cargo.Attached_Object() != null)
                        {
                            FootClass u = Cargo.Detach_Object()!;
                            if (!first && secondary == null)
                                secondary = u;
                            first = false;

                            Game.ScenarioInit++;
                            if (u.Unlimbo(TdMath.CoordAdd(Coord & 0xFF00FF00, TdMath.StoppingCoordAbs[count]), (byte)DirType.DIR_N))
                            {
                                u.Assign_Mission(MissionType.MISSION_MOVE);
                                u.NavCom = Target.As_Target(cell);
                                u.Path[0] = TdMath.DirFacingType(u.PrimaryFacing.Current);
                                u.Path[1] = FacingType.FACING_NONE;
                                u.Set_Speed(0x80);
                                u.IsUnloading = true;
                                u.Look(false);
                            }
                            else
                            {
                                u.Delete();
                            }
                            Game.ScenarioInit--;
                            count++;
                        }

                        if (secondary != null)
                            unit = secondary;
                        if (Transmit_Message(RadioMessageType.RADIO_HELLO, unit) == RadioMessageType.RADIO_ROGER)
                            Transmit_Message(RadioMessageType.RADIO_UNLOAD);

                        Mark(MarkType.MARK_DOWN);
                        Game.Map.Layer[(int)LayerType.LAYER_GROUND].Sort();
                        Game.Map.Layer[(int)LayerType.LAYER_GROUND].Sort();
                        Game.Map.Layer[(int)LayerType.LAYER_GROUND].Sort();
                        return false;
                    }
                    Mark(MarkType.MARK_DOWN);
                    return false;
                }
                Mark(MarkType.MARK_DOWN);
            }
        }
        else
        {
            return true;
        }
        return false;
    }

    // The remaster's (RA) search: north, south, then a little east and west, from a random start.
    // The terminating 0 is part of the array, and the random start can land on it.
    const int ClearSpotCount = 36;
    static readonly int[] ClearSpotOffsets =
    [
        -TdMath.MapCellW * 1,
        -TdMath.MapCellW * 2, -(TdMath.MapCellW * 2) + 1, -(TdMath.MapCellW * 2) - 1,
        -TdMath.MapCellW * 3, -(TdMath.MapCellW * 3) + 1, -(TdMath.MapCellW * 3) - 1, -(TdMath.MapCellW * 3) + 2, -(TdMath.MapCellW * 3) - 2,
        -TdMath.MapCellW * 4, -(TdMath.MapCellW * 4) + 1, -(TdMath.MapCellW * 4) - 1, -(TdMath.MapCellW * 4) + 2, -(TdMath.MapCellW * 4) - 2,
        TdMath.MapCellW * 1,
        TdMath.MapCellW * 2, (TdMath.MapCellW * 2) + 1, (TdMath.MapCellW * 2) - 1,
        TdMath.MapCellW * 3, (TdMath.MapCellW * 3) + 1, (TdMath.MapCellW * 3) - 1, (TdMath.MapCellW * 3) + 2, (TdMath.MapCellW * 3) - 2,
        TdMath.MapCellW * 4, (TdMath.MapCellW * 4) + 1, (TdMath.MapCellW * 4) - 1, (TdMath.MapCellW * 4) + 2, (TdMath.MapCellW * 4) - 2,
        -1, -2, -3, -4,
        1, 2, 3, 4,
        0,
    ];

    /// <summary>Goto_Clear_Spot: the computer's MCV looks north for room to deploy; true if it may deploy here.</summary>
    public bool Goto_Clear_Spot()
    {
        var yard = Game.Types.Building(StructType.STRUCT_CONST);
        Mark(MarkType.MARK_UP);
        if (!Game.Target_Legal(NavCom) && yard.Legal_Placement(Game, TdMath.CoordCell(TdMath.AdjacentCellCoord(Center_Coord(), TdMath.Facing.NW))) != 0)
        {
            Mark(MarkType.MARK_DOWN);
            return true;
        }

        if (!Game.Target_Legal(NavCom))
        {
            int offsetIndex = Game.Random_Pick(0, ClearSpotCount);
            for (int i = 0; i < ClearSpotCount; i++)
            {
                int cell = TdMath.CoordCell(Coord) + ClearSpotOffsets[offsetIndex++];
                if (offsetIndex >= ClearSpotCount || ClearSpotOffsets[offsetIndex] == 0)
                    offsetIndex = 0;
                if (yard.Legal_Placement(Game, cell) != 0)
                {
                    Assign_Destination(Target.As_Target(cell));
                    break;
                }
            }
        }
        Mark(MarkType.MARK_DOWN);
        return false;
    }

    /// <summary>Try_To_Deploy: the MCV turns south-west and becomes a Construction Yard.</summary>
    public bool Try_To_Deploy()
    {
        if (!Game.Target_Legal(NavCom) && !IsRotating && Class.Type == UnitType.UNIT_MCV)
        {
            var yard = Game.Types.Building(StructType.STRUCT_CONST);
            Mark(MarkType.MARK_UP);
            if (yard.Legal_Placement(Game, TdMath.CoordCell(TdMath.AdjacentCellCoord(Center_Coord(), TdMath.Facing.NW))) == 0)
            {
                if (Game.PlayerPtr == House)
                    Game.Speak(VoxType.VOX_DEPLOY);
                Mark(MarkType.MARK_DOWN);
                IsDeploying = false;
                return false;
            }
            Mark(MarkType.MARK_DOWN);

            if (PrimaryFacing.Current != (byte)DirType.DIR_SW)
            {
                Do_Turn((byte)DirType.DIR_SW);
                IsDeploying = true;
                return true;
            }

            Mark(MarkType.MARK_UP);
            var building = BuildingClass.Create(Game, StructType.STRUCT_CONST, House.Class.House);
            if (building != null)
            {
                if (building.Unlimbo(TdMath.AdjacentCellCoord(Coord, TdMath.Facing.NW)))
                {
                    building.Revealed(House);
                    uint ratio = Health_Ratio();
                    building.Strength = (int)TdMath.FixedToCardinal((uint)building.Class.MaxStrength, ratio);
                    Stun();
                    Delete_This();
                    return true;
                }
                building.Delete();
            }
            Mark(MarkType.MARK_DOWN);
            IsDeploying = false;
        }
        return false;
    }

    public override void Per_Cell_Process(bool center)
    {
        int cell = TdMath.CoordCell(Coord);
        TechnoClass? whom = Contact_With_Whom();

        // Entering a building we're tethered to (refinery, repair bay...).
        if (IsTethered && whom != null && center && whom.What_Am_I() == RTTIType.RTTI_BUILDING && Mission == MissionType.MISSION_ENTER)
        {
            if (whom == Game.Map[cell].Cell_Building())
            {
                switch (Transmit_Message(RadioMessageType.RADIO_IM_IN, whom))
                {
                    case RadioMessageType.RADIO_ROGER:
                        break;
                    case RadioMessageType.RADIO_ATTACH:
                        Mark(MarkType.MARK_UP);
                        Game.SpecialFlag = true;
                        Limbo();
                        Game.SpecialFlag = false;
                        whom.Cargo.Attach(this);
                        return;
                    default:
                        Scatter(0, true);
                        break;
                }
            }
        }

        // Leaving a building we were docked with.
        if (IsTethered && center &&
            (Mission != MissionType.MISSION_ENTER || (Game.As_Techno(NavCom) != null && Contact_With_Whom() != Game.As_Techno(NavCom))) &&
            Mission != MissionType.MISSION_UNLOAD)
        {
            if (Game.Map[cell].Cell_Building() != null && !Game.Target_Legal(NavCom))
            {
                Scatter(0, true, true);
            }
            else
            {
                var contact = Contact_With_Whom();
                if (Transmit_Message(RadioMessageType.RADIO_UNLOADED) == RadioMessageType.RADIO_RUN_AWAY)
                {
                    if (Class.Type == UnitType.UNIT_HARVESTER && contact is BuildingClass b && b.Class.Type != StructType.STRUCT_REPAIR)
                        Assign_Mission(MissionType.MISSION_HARVEST);
                    else if (!Game.Target_Legal(NavCom))
                        Scatter(0, true);
                }
                else if (Class.Type == UnitType.UNIT_HARVESTER)
                {
                    if (Game.Target_Legal(ArchiveTarget))
                    {
                        Assign_Mission(MissionType.MISSION_HARVEST);
                        Assign_Destination(ArchiveTarget);
                        ArchiveTarget = Target.TARGET_NONE;
                    }
                    else if (!Game.Target_Legal(NavCom))
                    {
                        Scatter(0, true);
                    }
                }
            }
        }

        if (center && IsDeploying)
        {
            Try_To_Deploy();
            if (!IsActive)
                return;
        }

        // Loaners (gunboat, hovercraft) leaving the map: the gunboat turns round, others go.
        if (center && IsALoaner && !Game.Map.In_Radar(cell))
        {
            if (IsReturning || !Cargo.Is_Something_Attached())
            {
                if (Class.Type == UnitType.UNIT_GUNBOAT)
                {
                    int here = TdMath.CoordCell(Coord);
                    var map = Game.Map;
                    Assign_Mission(MissionType.MISSION_HUNT);
                    if (TdMath.CellX(here) <= map.MapCellX)
                    {
                        Assign_Destination(Target.As_Target(TdMath.XyCell(map.MapCellX + map.MapCellWidth, TdMath.CellY(here))));
                        Set_Speed(255);
                        PrimaryFacing.Set((byte)DirType.DIR_E);
                    }
                    else
                    {
                        Assign_Destination(Target.As_Target(TdMath.XyCell(map.MapCellX - 1, TdMath.CellY(here))));
                        Set_Speed(255);
                        PrimaryFacing.Set((byte)DirType.DIR_W);
                    }
                    Mark(MarkType.MARK_DOWN);
                }
                else
                {
                    Mark(MarkType.MARK_DOWN);
                    Stun();
                    Delete_This();
                    return;
                }
            }
        }

        Overrun_Square(TdMath.CoordCell(Coord), false);

        if (IsPlanningToLook)
        {
            IsPlanningToLook = false;
            Look(false);
        }
        else
        {
            Look(true);
        }

        if (center)
            Commence();

        // Defender's advantage: artillery that stops gets a slower first shot.
        if (Game.Special.IsDefenderAdvantage && !Game.Target_Legal(NavCom) && Path[0] == FacingType.FACING_NONE)
        {
            if (Class.Type is UnitType.UNIT_MLRS or UnitType.UNIT_ARTY or UnitType.UNIT_MSAM)
                Arm = Rearm_Delay(false) * 2;
        }

        // Capture the flag: pick up an enemy flag, bring it home.
        if (center && Flagged == HousesType.HOUSE_NONE)
        {
            if (Game.Map[cell].IsFlagged && !House.Is_Ally(Game.Map[cell].Owner))
                Game.HouseFor(Game.Map[cell].Owner).Flag_Attach(this);
        }

        if (Flagged != HousesType.HOUSE_NONE)
        {
            if (!IsOwnedByPlayer && Flagged == Game.PlayerPtr.Class.House)
                Game.Map.Sight_From(House, TdMath.CoordCell(Coord), Class.SightRange, true);

            if (cell == Game.HouseFor(Owner()).FlagHome && center)
            {
                HousesType house = Flagged;
                Game.HouseFor(house).Flag_Remove(As_Target(), true);
                Game.HouseFor(house).Flag_To_Die();
            }
        }

        base.Per_Cell_Process(center);
    }

    /// <summary>Tiberium_Check: the Tiberium in a cell offset from center (0 if none or not visible).</summary>
    int Tiberium_Check(ref int center, int x, int y)
    {
        var map = Game.Map;
        if (TdMath.CellX(center) + x < map.MapCellX) return 0;
        if (TdMath.CellX(center) + x >= map.MapCellX + map.MapCellWidth) return 0;
        if (TdMath.CellY(center) + y < map.MapCellY) return 0;
        if (TdMath.CellY(center) + y >= map.MapCellY + map.MapCellHeight) return 0;

        center = TdMath.XyCell(TdMath.CellX(center) + x, TdMath.CellY(center) + y);
        if (Game.GameToPlay != GameType.GAME_NORMAL || !IsOwnedByPlayer || map[center].Is_Visible(Game.PlayerPtr))
        {
            if (map[center].Cell_Techno() == null && map[center].Land_Type() == LandType.LAND_TIBERIUM)
                return map[center].OverlayData + 1;
        }
        return 0;
    }

    /// <summary>Goto_Tiberium: true if on Tiberium; else heads for the richest Tiberium on the
    /// nearest ring that has any (the ring's sides tried in a random order).</summary>
    bool Goto_Tiberium()
    {
        if (!Game.Target_Legal(NavCom))
        {
            int center = TdMath.CoordCell(Center_Coord());
            if (Game.Map[center].Land_Type() == LandType.LAND_TIBERIUM)
                return true;

            const int RAND_MAX = 0x7FFF;
            for (int radius = 1; radius < 64; radius++)
            {
                int bestcell = 0;
                int besttiberium = 0;
                for (int x = -radius; x <= radius; x++)
                {
                    int[][] corners = [[x, -radius], [x, +radius], [-radius, x], [+radius, x]];
                    for (int i = 0; i < 3; i++)
                    {
                        int j = i + Game.Random.Rand() / (RAND_MAX / (4 - i) + 1);
                        (corners[j], corners[i]) = (corners[i], corners[j]);
                    }

                    for (int k = 0; k < 4; k++)
                    {
                        int cell = center;
                        int tiberium = Tiberium_Check(ref cell, corners[k][0], corners[k][1]);
                        if (tiberium > besttiberium)
                        {
                            bestcell = cell;
                            besttiberium = tiberium;
                        }
                    }
                }

                if (bestcell != 0)
                {
                    Assign_Destination(Target.As_Target(bestcell));
                    return false;
                }
            }
        }
        return false;
    }

    /// <summary>Find_Best_Refinery: the refinery last unloaded at, if still ours, else the nearest.</summary>
    public BuildingClass? Find_Best_Refinery()
    {
        if (tiberiumUnloadRefinery != null)
        {
            if (tiberiumUnloadRefinery.House == House && !tiberiumUnloadRefinery.IsInLimbo &&
                tiberiumUnloadRefinery.Mission != MissionType.MISSION_DECONSTRUCTION &&
                tiberiumUnloadRefinery.Class.Type == StructType.STRUCT_REFINERY)
                return tiberiumUnloadRefinery;
            tiberiumUnloadRefinery = null;
        }
        return Find_Docking_Bay(StructType.STRUCT_REFINERY, false);
    }

    /// <summary>Harvesting: takes a bite of the cell's Tiberium each second; false when full or the cell is bare.</summary>
    bool Harvesting()
    {
        int cell = TdMath.CoordCell(Coord);
        var ptr = Game.Map[cell];

        if (Game.Target_Legal(NavCom) || !harvestTimer.Expired || IsDriving || IsRotating)
            return true;

        if (Tiberium_Load() < 0x0100 && ptr.Land_Type() == LandType.LAND_TIBERIUM)
        {
            int reducer = (ptr.OverlayData % 6) + 1;
            reducer = ptr.Reduce_Tiberium(Math.Min(reducer, UnitTypeClass.STEP_COUNT - Tiberium));
            Tiberium = (byte)(Tiberium + reducer);
            Stage.Set_Stage(0);
            Stage.Set_Rate(2);
            harvestTimer.Set(TdDefines.TICKS_PER_SECOND);
        }
        else
        {
            Stage.Set_Stage(0);
            Stage.Set_Rate(0);
            return false;
        }
        return true;
    }

    /// <summary>Mission_Unload: the APC opens its door and lets everyone out; the MCV deploys; the
    /// hovercraft lands its cargo and leaves.</summary>
    public override int Mission_Unload()
    {
        const int INITIAL_CHECK = 0, MANEUVERING = 1, OPENING_DOOR = 2, UNLOADING = 3, CLOSING_DOOR = 4;

        switch (Class.Type)
        {
            case UnitType.UNIT_APC:
                switch (Status)
                {
                    case INITIAL_CHECK:
                    {
                        byte dir = Desired_Load_Dir(null, out int cell);
                        if (Cargo.How_Many() != 0 && cell != 0)
                        {
                            Do_Turn(dir);
                            Status = MANEUVERING;
                            return 1;
                        }
                        Assign_Mission(MissionType.MISSION_GUARD);
                        break;
                    }

                    case MANEUVERING:
                        if (!IsRotating)
                        {
                            APC_Open_Door();
                            if (Door.Is_Door_Opening())
                            {
                                Status = OPENING_DOOR;
                                return 1;
                            }
                        }
                        break;

                    case OPENING_DOOR:
                        if (Door.Is_Door_Open())
                        {
                            Status = UNLOADING;
                            return 1;
                        }
                        if (!Door.Is_Door_Opening())
                            Status = INITIAL_CHECK;
                        break;

                    case UNLOADING:
                        if (Cargo.How_Many() != 0)
                        {
                            var passenger = Cargo.Detach_Object();
                            if (passenger != null)
                            {
                                byte toface = (byte)((int)DirType.DIR_S + PrimaryFacing.Current);
                                bool placed = false;

                                for (int face = (int)FacingType.FACING_N; face < (int)FacingType.FACING_COUNT; face++)
                                {
                                    // DirType + FacingType: the facing (0..7) is added as a direction step.
                                    byte newface = (byte)(toface + face);
                                    int newcell = TdMath.AdjacentCellOf(TdMath.CoordCell(Coord), TdMath.DirFacingType(newface));

                                    if (passenger.Can_Enter_Cell(newcell) == MoveType.MOVE_OK)
                                    {
                                        Game.ScenarioInit++;
                                        passenger.Unlimbo(TdMath.CoordMove(Coord, newface, 0x0080), newface);
                                        Game.ScenarioInit--;
                                        passenger.Assign_Mission(MissionType.MISSION_MOVE);
                                        passenger.Assign_Destination(Target.As_Target(newcell));
                                        placed = true;
                                        break;
                                    }
                                }

                                if (!placed)
                                {
                                    Cargo.Attach(passenger);
                                    Status = CLOSING_DOOR;
                                }
                                else
                                {
                                    passenger.Look(false);
                                }
                            }
                        }
                        else
                        {
                            Status = CLOSING_DOOR;
                        }
                        break;

                    case CLOSING_DOOR:
                        if (Door.Is_Door_Open())
                            APC_Close_Door();
                        if (Door.Is_Door_Closed())
                            Assign_Mission(MissionType.MISSION_GUARD);
                        break;
                }
                break;

            case UnitType.UNIT_MCV:
                switch (Status)
                {
                    case 0:
                        Path[0] = FacingType.FACING_NONE;
                        Status = 1;
                        break;
                    case 1:
                        if (!IsDriving)
                        {
                            Try_To_Deploy();
                            if (IsDeploying)
                                Status = 2;
                            else if (!House.IsHuman && Game.GameToPlay != GameType.GAME_NORMAL)
                                Assign_Mission(MissionType.MISSION_HUNT);   // the skirmish computer goes looking for a spot
                            else
                                Assign_Mission(MissionType.MISSION_GUARD);
                        }
                        break;
                    case 2:
                        if (Game.GameToPlay != GameType.GAME_NORMAL && !IsDeploying)
                            Assign_Mission(MissionType.MISSION_GUARD);
                        break;
                }
                return 1;

            case UnitType.UNIT_HOVER:
                switch (Status)
                {
                    case 0:
                        if (Unload_Hovercraft_Process())
                            Status = 1;
                        break;
                    case 1:
                        Team?.Remove(this);
                        Exit_Map();
                        break;
                }
                break;
        }
        return TdDefines.TICKS_PER_SECOND;
    }

    /// <summary>Mission_Harvest: look for Tiberium, harvest until full, find a refinery, go in.</summary>
    public override int Mission_Harvest()
    {
        const int LOOKING = 0, HARVESTING = 1, FINDHOME = 2, HEADINGHOME = 3, GOINGTOIDLE = 4;

        if (!Class.IsToHarvest)
            return TdDefines.TICKS_PER_SECOND * 30;

        switch (Status)
        {
            case LOOKING:
                IsHarvesting = false;
                if (Game.Target_Legal(TarCom))
                {
                    Assign_Target(Target.TARGET_NONE);
                    Status = FINDHOME;
                    return 1;
                }
                if (Goto_Tiberium())
                {
                    IsHarvesting = true;
                    Stage.Set_Rate(2);
                    Stage.Set_Stage(0);
                    Status = HARVESTING;
                    ArchiveTarget = Target.As_Target(TdMath.CoordCell(Coord));
                    return 1;
                }
                if (!Game.Target_Legal(NavCom))
                {
                    if (Game.Target_Legal(ArchiveTarget))
                    {
                        Assign_Destination(ArchiveTarget);
                    }
                    else
                    {
                        Status = GOINGTOIDLE;
                        return TdDefines.TICKS_PER_SECOND * 15;
                    }
                }
                break;

            case HARVESTING:
                if (!Harvesting())
                {
                    IsHarvesting = false;
                    if (Tiberium_Load() == 0x0100)
                    {
                        Status = FINDHOME;
                    }
                    else if (!Goto_Tiberium() && !Game.Target_Legal(NavCom))
                    {
                        ArchiveTarget = Target.TARGET_NONE;
                        Status = FINDHOME;
                    }
                    else
                    {
                        Status = HARVESTING;
                        IsHarvesting = true;
                    }
                    return 1;
                }
                if (!Game.Target_Legal(NavCom) && ArchiveTarget == Target.TARGET_NONE)
                    ArchiveTarget = Target.As_Target(TdMath.CoordCell(Coord));
                break;

            case FINDHOME:
                if (!Game.Target_Legal(NavCom))
                {
                    var nearest = Find_Best_Refinery();
                    if (nearest != null && Transmit_Message(RadioMessageType.RADIO_HELLO, nearest) == RadioMessageType.RADIO_ROGER)
                    {
                        Status = HEADINGHOME;
                    }
                    else
                    {
                        // Every refinery is busy: wait near the nearest.
                        Game.ScenarioInit++;
                        nearest = Find_Best_Refinery();
                        Game.ScenarioInit--;
                        if (nearest != null)
                            Assign_Destination(Target.As_Target(nearest.Nearby_Location(this)));
                    }
                }
                break;

            case HEADINGHOME:
                Assign_Mission(MissionType.MISSION_ENTER);
                return 1;

            case GOINGTOIDLE:
                Assign_Mission(MissionType.MISSION_GUARD);
                break;
        }
        return TdDefines.TICKS_PER_SECOND;
    }

    /// <summary>Mission_Hunt: the computer's MCV deploys; the gunboat patrols side to side.</summary>
    public override int Mission_Hunt()
    {
        if (Class.Type == UnitType.UNIT_MCV)
        {
            switch (Status)
            {
                case 0:
                    if (Goto_Clear_Spot() && Try_To_Deploy())
                        Status = 1;
                    break;
                case 1:
                    if (!IsDeploying)
                        Status = 0;
                    break;
            }
        }
        else if (Class.Type == UnitType.UNIT_GUNBOAT)
        {
            var map = Game.Map;
            if (!Game.Target_Legal(NavCom))
            {
                if (PrimaryFacing.Current == (byte)DirType.DIR_W)
                    Assign_Destination(Target.As_Target(TdMath.XyCell(map.MapCellX - 1, TdMath.CellY(TdMath.CoordCell(Coord)))));
                else
                    Assign_Destination(Target.As_Target(TdMath.XyCell(map.MapCellX + map.MapCellWidth, TdMath.CellY(TdMath.CoordCell(Coord)))));
                Set_Speed(255);
            }
            if (Speed == 0)
                Set_Speed(255);
            if (!Game.Target_Legal(TarCom) || !In_Range(TarCom, 0))
                Target_Something_Nearby(ThreatType.THREAT_AREA);
        }
        else
        {
            return base.Mission_Hunt();
        }
        return TdDefines.TICKS_PER_SECOND;
    }

    /// <summary>Mission_Enter: a harvester remembers the refinery it goes into.</summary>
    public override int Mission_Enter()
    {
        if (Class.IsToHarvest)
        {
            TechnoClass? contact = Contact_With_Whom();
            contact ??= Game.As_Techno(ArchiveTarget);
            contact ??= Game.As_Techno(NavCom);
            if (contact is BuildingClass { Class.Type: StructType.STRUCT_REFINERY } refinery)
                tiberiumUnloadRefinery = refinery;
        }
        return base.Mission_Enter();
    }

    /// <summary>Look: reveals around the unit (the player's, or anyone's in multiplayer).</summary>
    public override void Look(bool incremental = false)
    {
        if (!IsInLimbo && House != null && (House.IsHuman || Game.GameToPlay != GameType.GAME_NORMAL))
        {
            int sight = Class.SightRange;
            if (sight != 0)
                Game.Map.Sight_From(House, TdMath.CoordCell(Coord), sight, Class.Type != UnitType.UNIT_GUNBOAT && incremental);
        }
    }

    static readonly short[] GunboatOverlap = [-3, -2, 2, 3, TdMath.RefreshEol];

    public override short[] Overlap_List()
    {
        if (Class.Type == UnitType.UNIT_GUNBOAT)
            return GunboatOverlap;

        int size = (int)TdDefines.ICON_PIXEL_W;
        if (Is_Selected_By_Player() || IsFiring)
            size += 24;
        if (Is_Selected_By_Player() || Class.IsGigundo || IsAnimAttached || Flagged != HousesType.HOUSE_NONE)
            size = (int)TdDefines.ICON_PIXEL_W * 2;
        // The list without its first entry (the cell itself), as the original's "+1".
        return TdMath.Coord_Spillage_List(Coord, size)[1..];
    }

    /// <summary>Can_Enter_Cell: whether (and how) a vehicle may move into a cell.</summary>
    public override MoveType Can_Enter_Cell(int cell, FacingType facing = FacingType.FACING_NONE)
    {
        if ((uint)cell >= TdDefines.MAP_CELL_TOTAL)
            return MoveType.MOVE_NO;
        var cellptr = Game.Map[cell];

        if (Class.Type == UnitType.UNIT_GUNBOAT)
            return MoveType.MOVE_OK;

        // Off the map (once on it) only for loaners and during scenario setup.
        if (IsLocked && !IsALoaner && Game.ScenarioInit == 0 && !Game.Map.In_Radar(cell))
            return MoveType.MOVE_NO;

        MoveType retval = MoveType.MOVE_OK;

        if (cellptr.Overlay != OverlayType.OVERLAY_NONE)
        {
            var optr = Game.Types.Overlay(cellptr.Overlay);

            // The computer doesn't pick up crates.
            if (optr.IsCrate && !House.IsHuman)
                return MoveType.MOVE_NO;

            // Walls: the computer may shoot through enemy walls it can destroy.
            if (optr.IsWall)
            {
                if (Class.Primary != WeaponType.WEAPON_NONE)
                {
                    var whead = Game.Types.Warhead(Game.Types.Bullet(Game.Types.Weapon(Class.Primary).Fires).Warhead);
                    if (whead.IsWallDestroyer || (whead.IsWoodDestroyer && optr.IsWooden))
                    {
                        if (!House.IsHuman && !House.Is_Ally(cellptr.Owner))
                        {
                            if (retval < MoveType.MOVE_DESTROYABLE)
                                retval = MoveType.MOVE_DESTROYABLE;
                        }
                        else
                        {
                            return MoveType.MOVE_NO;
                        }
                    }
                    else
                    {
                        return MoveType.MOVE_NO;
                    }
                }
                else
                {
                    return MoveType.MOVE_NO;
                }
            }
        }

        if (Game.Types.Ground(cellptr.Land_Type()).Cost[(int)Class.Speed] == 0)
            return MoveType.MOVE_NO;

        bool crushable = false;
        ObjectClass? obj = cellptr.Cell_Occupier();
        while (obj != null)
        {
            if (obj != this)
            {
                // What we're docking with (or a repair bay) never blocks.
                if (obj == Contact_With_Whom() && (IsTethered || obj is BuildingClass { Class.Type: StructType.STRUCT_REPAIR }))
                    return MoveType.MOVE_OK;

                bool isMoving = obj.What_Am_I() is RTTIType.RTTI_INFANTRY or RTTIType.RTTI_UNIT && Game.Target_Legal(((FootClass)obj).NavCom);

                if (House.Is_Ally(obj))
                {
                    if (isMoving)
                    {
                        // A friend coming straight at us blocks; otherwise it'll be gone.
                        int face = (int)TdMath.DirFacingType(PrimaryFacing.Current);
                        int techface = (int)TdMath.DirFacingType(((FootClass)obj).PrimaryFacing.Current) ^ 4;
                        if (face == techface && Distance(obj) <= 0x1FF)
                            return MoveType.MOVE_NO;
                        if (retval < MoveType.MOVE_MOVING_BLOCK)
                            retval = MoveType.MOVE_MOVING_BLOCK;
                    }
                    else
                    {
                        if (obj.What_Am_I() == RTTIType.RTTI_BUILDING)
                            return MoveType.MOVE_NO;
                        if (retval < MoveType.MOVE_TEMP)
                            retval = MoveType.MOVE_TEMP;
                    }
                }
                else if (!obj.Is_Techno() || ((TechnoClass)obj).Cloak != CloakType.CLOAKED)
                {
                    if (!Class.IsCrusher || !obj.Class_Of().IsCrushable)
                    {
                        if (Class.Primary == WeaponType.WEAPON_NONE)
                            return MoveType.MOVE_NO;
                        if (obj.What_Am_I() == RTTIType.RTTI_TERRAIN)
                        {
                            if (((TerrainClass)obj).Class.IsFlammable && Game.Types.Bullet(Game.Types.Weapon(Class.Primary).Fires).Warhead == WarheadType.WARHEAD_FIRE)
                            {
                                if (retval < MoveType.MOVE_DESTROYABLE)
                                    retval = MoveType.MOVE_DESTROYABLE;
                            }
                            else
                            {
                                return MoveType.MOVE_NO;
                            }
                        }
                        else if (retval < MoveType.MOVE_DESTROYABLE)
                        {
                            retval = MoveType.MOVE_DESTROYABLE;
                        }
                    }
                    else
                    {
                        crushable = true;
                    }
                }
                else if (retval < MoveType.MOVE_CLOAK)
                {
                    retval = MoveType.MOVE_CLOAK;
                }
            }
            obj = obj.Next;
        }

        // Infantry in the cell, or a vehicle heading into it.
        if (retval == MoveType.MOVE_OK && !crushable && cellptr.Flag.Composite != 0)
        {
            if (cellptr.Flag.Occupy.Vehicle)
            {
                retval = MoveType.MOVE_MOVING_BLOCK;
            }
            else if (cellptr.InfType != HousesType.HOUSE_NONE && House.Is_Ally(cellptr.InfType))
            {
                retval = MoveType.MOVE_MOVING_BLOCK;
            }
            else if (!Class.IsCrusher)
            {
                if (Class.Primary != WeaponType.WEAPON_NONE)
                    retval = MoveType.MOVE_DESTROYABLE;
                else
                    return MoveType.MOVE_NO;
            }
        }

        if (retval == MoveType.MOVE_OK && crushable && cellptr.Flag.Occupy.Vehicle)
            return MoveType.MOVE_MOVING_BLOCK;

        return retval;
    }

    public override uint Target_Coord() => Center_Coord();

    /// <summary>Scatter: move to a nearby free cell (away from a threat).</summary>
    public override void Scatter(uint threat, bool forced = false, bool nokidding = false)
    {
        if (Class.Type == UnitType.UNIT_GUNBOAT || Class.Type == UnitType.UNIT_HOVER)
            return;
        if (PrimaryFacing.Is_Rotating())
            return;
        if (Game.Target_Legal(NavCom) && !nokidding)
            return;

        // No threat: go to a nearby clear cell (Map.Nearby_Location, from the remaster's RA-AI code).
        if (threat == 0)
        {
            Assign_Destination(Target.As_Target(Game.Map.Nearby_Location(TdMath.CoordCell(Coord))));
        }
        else if ((!Game.Target_Legal(TarCom) && !Game.Target_Legal(NavCom)) || forced || nokidding || Game.Random_Pick(1, 4) == 1)
        {
            FacingType toface;
            if (threat != 0)
            {
                toface = TdMath.DirFacingType(TdMath.Direction8(threat, Coord));
                toface = TdMath.FacingAdd(toface, Game.Random_Pick(0, 2) - 1);
            }
            else
            {
                toface = TdMath.DirFacingType(PrimaryFacing.Current);
            }

            // As the original: every free cell round about is assigned in turn (the last one stays).
            for (int face = 0; face < (int)FacingType.FACING_COUNT; face++)
            {
                FacingType newface = TdMath.FacingAdd(toface, face);
                int newcell = TdMath.AdjacentCellOf(TdMath.CoordCell(Coord), newface);
                if (Game.Map.In_Radar(newcell) && Can_Enter_Cell(newcell) == MoveType.MOVE_OK)
                    Assign_Destination(Target.As_Target(newcell));
            }
        }
    }

    public override bool Stop_Driver()
    {
        if (Head_To_Coord() != 0)
        {
            bool temp = IsDown;
            if (temp)
                Mark(MarkType.MARK_UP);
            Mark_Track(Head_To_Coord(), MarkType.MARK_UP);
            if (temp)
                Mark(MarkType.MARK_DOWN);
        }
        return base.Stop_Driver();
    }

    public override bool Start_Driver(ref uint headto)
    {
        if (base.Start_Driver(ref headto))
        {
            Mark_Track(headto, MarkType.MARK_DOWN);
            return true;
        }
        return false;
    }

    public override bool Limbo()
    {
        if (!IsInLimbo)
            Stop_Driver();

        if (base.Limbo())
        {
            if (Flagged != HousesType.HOUSE_NONE)
                Game.HouseFor(Flagged).Flag_Attach(TdMath.CoordCell(Coord));
            return true;
        }
        return false;
    }

    static readonly VocType[] SelectResponses = [VocType.VOC_VEHIC, VocType.VOC_UNIT, VocType.VOC_YESSIR, VocType.VOC_YESSIR, VocType.VOC_YESSIR, VocType.VOC_AWAIT];
    static readonly VocType[] MoveResponses = [VocType.VOC_MOVEOUT, VocType.VOC_MOVEOUT, VocType.VOC_MOVEOUT, VocType.VOC_ACKNOWL, VocType.VOC_AFFIRM, VocType.VOC_AFFIRM];
    static readonly VocType[] AttackResponses = [VocType.VOC_AFFIRM, VocType.VOC_ACKNOWL, VocType.VOC_YESSIR, VocType.VOC_YESSIR, VocType.VOC_YESSIR];

    bool Is_Dinosaur() => Class.Type is UnitType.UNIT_TRIC or UnitType.UNIT_TREX or UnitType.UNIT_RAPT or UnitType.UNIT_STEG;

    public override void Response_Select()
    {
        VocType response = SelectResponses[Game.Sim_Random_Pick(0, SelectResponses.Length - 1)];
        if (Is_Dinosaur())
            response = VocType.VOC_DINOYES;
        if (Game.AllowVoice)
            Game.Sound_Effect(response, 0, -(Game.Units.ID(this) + 1));
    }

    public override void Response_Move()
    {
        VocType response = MoveResponses[Game.Sim_Random_Pick(0, MoveResponses.Length - 1)];
        if (Is_Dinosaur())
            response = VocType.VOC_DINOMOUT;
        if (Game.AllowVoice)
            Game.Sound_Effect(response, 0, -(Game.Units.ID(this) + 1));
    }

    public override void Response_Attack()
    {
        VocType response = AttackResponses[Game.Sim_Random_Pick(0, AttackResponses.Length - 1)];
        if (Is_Dinosaur())
            response = VocType.VOC_DINOMOUT;
        if (Game.AllowVoice)
            Game.Sound_Effect(response, 0, -(Game.Units.ID(this) + 1));
    }

    public override ActionType What_Action(ObjectClass obj)
    {
        ActionType action = base.What_Action(obj);

        // Unarmed units can't attack: crushers run over what they can, others just select.
        if (action == ActionType.ACTION_ATTACK && !Can_Player_Fire())
            action = Class.IsCrusher && obj.Class_Of().IsCrushable ? ActionType.ACTION_MOVE : ActionType.ACTION_SELECT;

        // Clicking on itself: unload (transports) or deploy (the MCV).
        if (action == ActionType.ACTION_SELF)
        {
            if (Class.Type != UnitType.UNIT_MCV)
            {
                if (!Class.IsTransporter || Cargo.How_Many() == 0)
                    action = ActionType.ACTION_NONE;
            }
            else
            {
                Mark(MarkType.MARK_UP);
                if (Game.Types.Building(StructType.STRUCT_CONST).Legal_Placement(Game, TdMath.CoordCell(TdMath.AdjacentCellCoord(Center_Coord(), TdMath.Facing.NW))) == 0)
                    action = ActionType.ACTION_NO_DEPLOY;
                Mark(MarkType.MARK_DOWN);
            }
        }

        if (Is_Owned_By_Player() && House.Class.House == obj.Owner() && obj.What_Am_I() == RTTIType.RTTI_BUILDING &&
            Transmit_Message(RadioMessageType.RADIO_CAN_LOAD, (TechnoClass)obj) == RadioMessageType.RADIO_ROGER)
            action = ActionType.ACTION_ENTER;

        // A free repair bay can be driven onto.
        if (Is_Owned_By_Player() && House.Class.House == obj.Owner() && action == ActionType.ACTION_SELECT && obj is BuildingClass building)
        {
            if (building.Class.Type == StructType.STRUCT_REPAIR && !building.In_Radio_Contact() && !building.Cargo.Is_Something_Attached())
                action = ActionType.ACTION_MOVE;
        }
        return action;
    }

    public override ActionType What_Action(int cell)
    {
        ActionType action = base.What_Action(cell);
        if (action == ActionType.ACTION_MOVE && Game.Map[cell].Land_Type() == LandType.LAND_TIBERIUM && Class.IsToHarvest)
            return ActionType.ACTION_HARVEST;
        return action;
    }

    public override bool Can_Player_Move() => base.Can_Player_Move() && Class.Type != UnitType.UNIT_GUNBOAT && Class.Type != UnitType.UNIT_HOVER;

    /// <summary>Read_INI: the scenario's [UNITS] — house, type, strength (0..256), cell, facing, mission, trigger.</summary>
    public static void Read_INI(TdGame game, Data.WwProfile ini)
    {
        string buf = "";
        foreach (string key in ini.Entries("UNITS"))
        {
            // The original reads into the same buffer each time (no default: a failed read keeps the last line).
            buf = ini.Get_String("UNITS", key, buf, 128 - 1);
            var tok = new Data.Strtok(buf);
            HousesType inhouse = game.Types.House_From_Name(tok.Next(","));
            if (inhouse == HousesType.HOUSE_NONE)
                continue;
            UnitType classid = game.Types.Unit_From_Name(tok.Next(","));
            if (classid == UnitType.UNIT_NONE || game.HouseOrNull(inhouse) == null)
                continue;

            var unit = Create(game, classid, inhouse);
            if (unit == null)
                continue;

            int strength = Data.WwProfile.Atoi(tok.Next(",\r\n"));
            uint coord = TdMath.CellCoord((short)Data.WwProfile.Atoi(tok.Next(",\r\n")));
            byte dir = (byte)Data.WwProfile.Atoi(tok.Next(",\r\n"));
            MissionType mission = MissionClass.Mission_From_Name(tok.Next(",\n\r"));
            unit.Trigger = Scenario.TriggerClass.As_Pointer(game, tok.Next(",\r\n"));
            if (unit.Trigger != null)
                unit.Trigger.AttachCount++;

            if (unit.Unlimbo(coord, dir))
            {
                unit.Strength = (int)TdMath.FixedToCardinal((uint)unit.Class.MaxStrength, (uint)strength);
                if (game.GameToPlay == GameType.GAME_NORMAL || unit.House.IsHuman)
                {
                    unit.Assign_Mission(mission);
                    unit.Commence();
                }
                else
                {
                    unit.Enter_Idle_Mode();
                }

                // Gunboats always patrol westwards first.
                if (unit.Class.Type == UnitType.UNIT_GUNBOAT)
                {
                    unit.PrimaryFacing.Set_Desired((byte)DirType.DIR_W);
                    unit.PrimaryFacing.Set_Current((byte)DirType.DIR_W);
                    unit.Assign_Mission(MissionType.MISSION_HUNT);
                    unit.Commence();
                    unit.Assign_Destination(Target.As_Target(TdMath.XyCell(game.Map.MapCellX - 1, TdMath.CellY(TdMath.CoordCell(unit.Coord)))));
                }
            }
            else
            {
                unit.Delete();
            }
        }
    }

    static readonly short[] ExitRepair =
    [
        (short)(-2 * TdMath.MapCellW + 0), (short)(-1 * TdMath.MapCellW + 1), 2, (short)(1 * TdMath.MapCellW + 1),
        (short)(2 * TdMath.MapCellW + 0), (short)(1 * TdMath.MapCellW - 1), -2, (short)(-1 * TdMath.MapCellW - 1),
    ];

    /// <summary>Exit_Repair: drive off the repair bay (ahead if possible).</summary>
    public void Exit_Repair()
    {
        bool found = false;
        int cell = TdMath.CoordCell(Coord) + ExitRepair[(int)TdMath.DirFacingType(PrimaryFacing.Current)];
        if (Can_Enter_Cell(cell) == MoveType.MOVE_OK)
            found = true;

        if (!found)
        {
            for (int i = 0; i < 8; i++)
            {
                cell = TdMath.CoordCell(Coord) + ExitRepair[i];
                if (Can_Enter_Cell(cell) == MoveType.MOVE_OK)
                {
                    found = true;
                    break;
                }
            }
        }

        if (found)
        {
            Assign_Mission(MissionType.MISSION_MOVE);
            Assign_Destination(Target.As_Target(cell));
        }
    }

    public override int Mission_Guard()
    {
        if (Class.Type == UnitType.UNIT_HOVER)
        {
            if (Cargo.Is_Something_Attached())
            {
                Assign_Mission(MissionType.MISSION_UNLOAD);
                Find_LZ();
            }
            else
            {
                Exit_Map();
            }
            return TdDefines.TICKS_PER_SECOND;
        }

        if (Class.Type == UnitType.UNIT_GUNBOAT)
        {
            Assign_Mission(MissionType.MISSION_HUNT);
            return TdDefines.TICKS_PER_SECOND;
        }

        if (Class.Type == UnitType.UNIT_HARVESTER && !House.IsHuman)
        {
            Assign_Mission(MissionType.MISSION_HARVEST);
            return TdDefines.TICKS_PER_SECOND;
        }

        // A base-building skirmish computer deploys its MCV.
        if (Game.GameToPlay != GameType.GAME_NORMAL && Class.Type == UnitType.UNIT_MCV && House.IsBaseBuilding && !House.IsHuman)
        {
            Assign_Mission(MissionType.MISSION_UNLOAD);
            return TdDefines.TICKS_PER_SECOND + Game.Random_Pick(0, 2);
        }

        return base.Mission_Guard();
    }

    public override int Mission_Move()
    {
        IsHarvesting = false;
        if (!Door.Is_Door_Closed())
            APC_Close_Door();
        if (Class.Type == UnitType.UNIT_GUNBOAT)
        {
            Assign_Mission(MissionType.MISSION_HUNT);
            return TdDefines.TICKS_PER_SECOND;
        }
        return base.Mission_Move();
    }

    static readonly DirType[] DesiredToActual = [DirType.DIR_S, DirType.DIR_SW, DirType.DIR_NW, DirType.DIR_NW, DirType.DIR_NE, DirType.DIR_NE, DirType.DIR_NE, DirType.DIR_SE];

    /// <summary>Desired_Load_Dir: which way to face (and the cell for the passenger) for loading or
    /// unloading through the back door.</summary>
    public override byte Desired_Load_Dir(ObjectClass? passenger, out int moveto)
    {
        byte faceto = passenger != null ? Direction(passenger) : (byte)(PrimaryFacing.Current + (int)DirType.DIR_S);

        int bestdir = 0;
        int bestval = -1;
        for (int face = (int)FacingType.FACING_N; face < (int)FacingType.FACING_COUNT; face++)
        {
            int value;
            int cellnum = TdMath.AdjacentCellOf(TdMath.CoordCell(Coord), (FacingType)face);

            if (passenger != null)
            {
                value = passenger.Can_Enter_Cell(cellnum) == MoveType.MOVE_OK || TdMath.CoordCell(passenger.Coord) == cellnum ? 128 : -128;
            }
            else
            {
                var cell = Game.Map[cellnum];
                if (Game.Types.Ground(cell.Land_Type()).Cost[(int)SpeedType.SPEED_FOOT] == 0 || cell.Flag.Occupy.Building || cell.Flag.Occupy.Vehicle ||
                    cell.Flag.Occupy.Monolith || (cell.Flag.Composite & 0x01F) == 0x01F)
                    value = -128;
                else if (cell.Cell_Techno() is { } techno && !House.Is_Ally(techno))
                    value = -128;
                else
                    value = 128;
            }

            value -= Math.Abs(TdMath.DirDiff((byte)(face << 5), faceto));
            if (face == (int)FacingType.FACING_S)
                value -= 100;
            if (face == (int)FacingType.FACING_SW || face == (int)FacingType.FACING_SE)
                value += 64;

            if (bestval == -1 || value > bestval)
            {
                bestval = value;
                bestdir = face;
            }
        }

        moveto = 0;
        if (bestval > 0)
        {
            moveto = TdMath.AdjacentCellOf(TdMath.CoordCell(Coord), (FacingType)bestdir);
            return (byte)DesiredToActual[bestdir];
        }
        return (byte)DirType.DIR_S;
    }

    public override int Mission_Attack()
    {
        if (Class.Type == UnitType.UNIT_GUNBOAT)
        {
            Assign_Mission(MissionType.MISSION_HUNT);
            return TdDefines.TICKS_PER_SECOND;
        }
        return base.Mission_Attack();
    }

    public bool Flag_Attach(HousesType house)
    {
        if (house != HousesType.HOUSE_NONE && Flagged == HousesType.HOUSE_NONE)
        {
            Flagged = house;
            Mark(MarkType.MARK_CHANGE);
            return true;
        }
        return false;
    }

    public bool Flag_Remove()
    {
        if (Flagged != HousesType.HOUSE_NONE)
        {
            Flagged = HousesType.HOUSE_NONE;
            Mark(MarkType.MARK_CHANGE);
            return true;
        }
        return false;
    }

    public override void Stun()
    {
        if (Flagged != HousesType.HOUSE_NONE)
            Game.HouseFor(Flagged).Flag_Attach(TdMath.CoordCell(Coord));
        base.Stun();
    }

    public override int Pip_Count()
    {
        if (Class.IsTransporter)
            return Cargo.How_Many();
        if (Class.IsToHarvest)
            return (int)TdMath.FixedToCardinal(UnitTypeClass.FULL_LOAD_CREDITS / 100, (uint)Tiberium_Load());
        return 0;
    }

    public void APC_Close_Door() => Door.Close_Door(10, 2);

    public void APC_Open_Door()
    {
        if (!IsDriving && !IsRotating)
        {
            if (PrimaryFacing.Current == (byte)DirType.DIR_NW || PrimaryFacing.Current == (byte)DirType.DIR_NE)
                Door.Open_Door(10, 2);
            else
                Door.Open_Door(1, 2);
        }
    }

    public override InfantryType Crew_Type()
    {
        if (Class.Primary == WeaponType.WEAPON_NONE)
            return Game.Random_Pick(0, 1) == 0 ? InfantryType.INFANTRY_C1 : InfantryType.INFANTRY_C7;
        return base.Crew_Type();
    }
}

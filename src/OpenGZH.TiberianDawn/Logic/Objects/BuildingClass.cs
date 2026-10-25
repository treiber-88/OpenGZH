using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// BuildingClass (BUILDING.CPP): structures. Their animation states (BState), construction and
/// selling, power, factories, repair, the refinery/repair bay/helipad/weapons factory docking,
/// the SAM site, the Obelisk's charge, the Temple's nuke, capture, and the crew and debris on death.
/// </summary>
public sealed class BuildingClass : TechnoClass
{
    // The SAM site's states (Status during MISSION_ATTACK).
    const int SAM_NONE = -1, SAM_UNDERGROUND = 0, SAM_RISING = 1, SAM_READY = 2, SAM_FIRING = 3, SAM_READY2 = 4, SAM_FIRING2 = 5, SAM_LOCKING = 6, SAM_LOWERING = 7;

    /// <summary>CenterOffset: the centre of each building size from its top-left corner.</summary>
    static readonly uint[] CenterOffset = [0x00800080, 0x008000FF, 0x00FF0080, 0x00FF00FF, 0x018000FF, 0x00FF0180, 0x01800180, 0x00FF0200, 0x02800280];

    public readonly BuildingTypeClass Class;
    public FactoryClass? Factory;
    public HousesType ActLike;
    public bool IsReadyToCommence, IsRepairing, IsWrenchVisible, IsGoingToBlow, IsSurvivorless, IsCharging, IsCharged, IsCaptured;
    public readonly CountDownTimer CountDown;
    public BStateType BState;
    public BStateType QueueBState;
    public HousesType WhoLastHurtMe;
    public uint WhomToRepay;
    public int LastStrength;
    public readonly CountDownTimer PlacementDelay;

    BuildingClass(TdGame game, StructType type, HousesType house) : base(game, house)
    {
        Class = game.Types.Building(type);
        CountDown = new CountDownTimer(game);
        PlacementDelay = new CountDownTimer(game);
        PlacementDelay.Set(0);
        LastStrength = 0;
        ActLike = House.ActLike;
        BState = BStateType.BSTATE_NONE;
        CountDown.Set(0);
        Factory = null;
        WhomToRepay = Target.TARGET_NONE;
        IsCaptured = false;
        IsCharged = false;
        IsCharging = false;
        IsSurvivorless = false;
        IsGoingToBlow = false;
        IsReadyToCommence = false;
        IsRepairing = false;
        IsSecondShot = !Class.IsTwoShooter;
        IsWrenchVisible = false;
        QueueBState = BStateType.BSTATE_NONE;
        Strength = Class.MaxStrength;
        WhoLastHurtMe = house;
        Ammo = Class.MaxAmmo;

        // Buildings of the other side's kind (a captured or scenario-given one) act like their makers.
        if ((type == StructType.STRUCT_AIRSTRIP || type == StructType.STRUCT_HAND) && house != HousesType.HOUSE_BAD)
        {
            ActLike = HousesType.HOUSE_BAD;
            IsCaptured = true;
        }
        if ((type == StructType.STRUCT_WEAP || type == StructType.STRUCT_BARRACKS) && house != HousesType.HOUSE_GOOD)
        {
            ActLike = HousesType.HOUSE_GOOD;
            IsCaptured = true;
        }
        if (game.GameToPlay == GameType.GAME_INTERNET)
            House.BuildingTotals.Increment_Unit_Total((int)type);

        // The house's counts (this replaced CurBuildings++ in the remaster).
        House.Tracking_Add(this);
    }

    /// <summary>new BuildingClass: the first free slot of the building heap (null when full).</summary>
    public static BuildingClass? Create(TdGame game, StructType type, HousesType house)
    {
        int slot = game.Buildings.Allocate();
        if (slot < 0)
            return null;
        var building = new BuildingClass(game, type, house);
        game.Buildings.Place(slot, building);
        building.Set_Active();
        return building;
    }

    protected override void Destruct()
    {
        if (Game.GameActive)
        {
            if (House != null)
                House.Tracking_Remove(this);
            Limbo();
        }
        base.Destruct();
    }

    protected override void Release()
    {
        IsActive = false;
        Game.Buildings.Free(this);
    }

    public override RTTIType What_Am_I() => RTTIType.RTTI_BUILDING;
    public override ObjectTypeClass Class_Of() => Class;
    public override uint As_Target() => Target.Build_Target(KindType.KIND_BUILDING, Game.Buildings.ID(this));

    public (int Start, int Count, int Rate) Fetch_Anim_Control() => Class.Anim(BState);

    public override short[] Occupy_List(bool placement = false) => Class.Occupy_List(Game.Types, Game.Special, placement);

    public override RadioMessageType Receive_Message(RadioClass? from, RadioMessageType message, ref int param)
    {
        switch (message)
        {
            // A busy refinery won't talk.
            case RadioMessageType.RADIO_HELLO:
                if (Mission == MissionType.MISSION_HARVEST)
                    return RadioMessageType.RADIO_NEGATIVE;
                break;

            // Can this object dock here?
            case RadioMessageType.RADIO_CAN_LOAD:
                base.Receive_Message(from, message, ref param);
                if (BState == BStateType.BSTATE_CONSTRUCTION || (Game.ScenarioInit == 0 && Class.Type != StructType.STRUCT_REFINERY && In_Radio_Contact()))
                    return RadioMessageType.RADIO_NEGATIVE;
                switch (Class.Type)
                {
                    case StructType.STRUCT_AIRSTRIP:
                        if (from is AircraftClass { Class.Type: AircraftType.AIRCRAFT_CARGO })
                            return RadioMessageType.RADIO_ROGER;
                        break;
                    case StructType.STRUCT_HELIPAD:
                        if (from is AircraftClass { Class.IsFixedWing: false })
                            return RadioMessageType.RADIO_ROGER;
                        break;
                    case StructType.STRUCT_REPAIR:
                        if (from!.What_Am_I() is RTTIType.RTTI_UNIT or RTTIType.RTTI_AIRCRAFT)
                            return RadioMessageType.RADIO_ROGER;
                        return RadioMessageType.RADIO_NEGATIVE;
                    case StructType.STRUCT_REFINERY:
                        if (from is UnitClass { Class.Type: UnitType.UNIT_HARVESTER } && (Game.ScenarioInit != 0 || !Cargo.Is_Something_Attached()))
                            return Contact_With_Whom() != from ? RadioMessageType.RADIO_ROGER : RadioMessageType.RADIO_NEGATIVE;
                        break;
                }
                return RadioMessageType.RADIO_NEGATIVE;

            // Docked: repair, reload or unload.
            case RadioMessageType.RADIO_IM_IN:
                if (Mission == MissionType.MISSION_DECONSTRUCTION)
                    return RadioMessageType.RADIO_NEGATIVE;
                switch (Class.Type)
                {
                    case StructType.STRUCT_REPAIR:
                        IsReadyToCommence = true;
                        Assign_Mission(MissionType.MISSION_REPAIR);
                        ((TechnoClass)from!).Assign_Mission(MissionType.MISSION_SLEEP);
                        return RadioMessageType.RADIO_ROGER;
                    case StructType.STRUCT_HELIPAD:
                        Assign_Mission(MissionType.MISSION_REPAIR);
                        ((TechnoClass)from!).Assign_Mission(MissionType.MISSION_SLEEP);
                        return RadioMessageType.RADIO_ROGER;
                    case StructType.STRUCT_REFINERY:
                        Game.ScenarioInit++;
                        Begin_Mode(BStateType.BSTATE_ACTIVE);
                        Game.ScenarioInit--;
                        Mark(MarkType.MARK_CHANGE);
                        Assign_Mission(MissionType.MISSION_HARVEST);
                        return RadioMessageType.RADIO_ATTACH;
                }
                break;

            // Guide the docking object in.
            case RadioMessageType.RADIO_DOCKING:
                base.Receive_Message(from, message, ref param);

                if (Class.Type == StructType.STRUCT_REFINERY && BState != BStateType.BSTATE_FULL && Mission != MissionType.MISSION_HARVEST)
                    Begin_Mode(BStateType.BSTATE_FULL);

                // An undamaged unit on the repair bay is sent away.
                if (Class.Type == StructType.STRUCT_REPAIR && Contact_With_Whom() != from)
                {
                    if (Transmit_Message(RadioMessageType.RADIO_ON_DEPOT) == RadioMessageType.RADIO_ROGER &&
                        Transmit_Message(RadioMessageType.RADIO_NEED_REPAIR) == RadioMessageType.RADIO_NEGATIVE)
                    {
                        Transmit_Message(RadioMessageType.RADIO_RUN_AWAY);
                        Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                        return RadioMessageType.RADIO_ROGER;
                    }
                }

                if (!In_Radio_Contact())
                    Transmit_Message(RadioMessageType.RADIO_HELLO, from);

                if (Transmit_Message(RadioMessageType.RADIO_NEED_TO_MOVE) == RadioMessageType.RADIO_ROGER)
                {
                    if (Class.Type == StructType.STRUCT_HELIPAD)
                    {
                        param = (int)As_Target();
                    }
                    else if (Class.Type == StructType.STRUCT_REPAIR)
                    {
                        Transmit_Message(RadioMessageType.RADIO_TETHER);
                        param = (int)Target.As_Target(TdMath.CoordCell(Center_Coord()));
                    }
                    else
                    {
                        // Adjacent_Cell(COORDINATE, DirType): the cell to the south-west.
                        param = (int)Target.As_Target(TdMath.CoordCell(TdMath.AdjacentCellCoord(Center_Coord(), TdMath.DirFacing((byte)DirType.DIR_SW))));
                    }

                    if (Transmit_Message(RadioMessageType.RADIO_MOVE_HERE, ref param) == RadioMessageType.RADIO_YEA_NOW_WHAT)
                    {
                        Transmit_Message(RadioMessageType.RADIO_TETHER);
                        if (Class.Type == StructType.STRUCT_REFINERY && Transmit_Message(RadioMessageType.RADIO_BACKUP_NOW, from) != RadioMessageType.RADIO_ROGER)
                            ((TechnoClass)from!).Scatter(0, true);
                    }
                }
                return RadioMessageType.RADIO_ROGER;

            // Is this a free refinery for the caller?
            case RadioMessageType.RADIO_ARE_REFINERY:
                if (Cargo.Is_Something_Attached() || In_Radio_Contact() || IsInLimbo || House.Class.House != from!.Owner() || Class.Type != StructType.STRUCT_REFINERY)
                    return RadioMessageType.RADIO_NEGATIVE;
                return RadioMessageType.RADIO_ROGER;

            // The construction yard is building something.
            case RadioMessageType.RADIO_BUILDING:
                Assign_Mission(MissionType.MISSION_REPAIR);
                base.Receive_Message(from, message, ref param);
                return RadioMessageType.RADIO_ROGER;

            case RadioMessageType.RADIO_COMPLETE:
                if (Mission != MissionType.MISSION_DECONSTRUCTION)
                    Assign_Mission(MissionType.MISSION_GUARD);
                base.Receive_Message(from, message, ref param);
                return RadioMessageType.RADIO_ROGER;

            case RadioMessageType.RADIO_OVER_OUT:
                Begin_Mode(BStateType.BSTATE_IDLE);
                base.Receive_Message(from, message, ref param);
                return RadioMessageType.RADIO_ROGER;

            // The docked object has left (or is leaving).
            case RadioMessageType.RADIO_UNLOADED:
                if (Class.Type == StructType.STRUCT_REPAIR && Distance(from!) < 0x0180)
                    return RadioMessageType.RADIO_ROGER;
                if (Class.Type == StructType.STRUCT_REFINERY)
                    Begin_Mode(BStateType.BSTATE_IDLE);
                base.Receive_Message(from, message, ref param);
                if (Class.Type is StructType.STRUCT_WEAP or StructType.STRUCT_AIRSTRIP or StructType.STRUCT_REPAIR)
                    return RadioMessageType.RADIO_RUN_AWAY;
                return RadioMessageType.RADIO_ROGER;
        }
        return base.Receive_Message(from, message, ref param);
    }

    /// <summary>The frame of the building's art to draw (Draw_It's shape number) and whether it is the build-up art.</summary>
    public (int Shape, bool Buildup) Shape_Number()
    {
        int shapenum = Stage.Fetch_Stage();

        if (BState == BStateType.BSTATE_CONSTRUCTION)
        {
            // Selling plays the build-up backwards.
            if (Mission == MissionType.MISSION_DECONSTRUCTION)
            {
                var anim = Class.Anim(BState);
                shapenum = (anim.Start + anim.Count - 1) - shapenum;
            }
            return (shapenum, true);
        }

        if (Class.Type == StructType.STRUCT_OBELISK)
            shapenum = IsCharged ? 3 : IsCharging ? Stage.Fetch_Stage() : 0;

        if (Class.IsTurretEquipped)
        {
            shapenum = TdMath.BodyShape[TdMath.Facing32[PrimaryFacing.Current]];
            if (Class.Type == StructType.STRUCT_SAM)
            {
                if (Status is SAM_READY or SAM_FIRING or SAM_READY2 or SAM_FIRING2 or SAM_LOCKING)
                    shapenum += 16;
                else
                    shapenum = Stage.Fetch_Stage();
            }
            else if (IsInRecoilState)
            {
                shapenum += 32;
            }
            if (Health_Ratio() < 0x0080)
                shapenum += 64;
        }
        else if (Strength <= 1)
        {
            // Rubble: the last frame.
            shapenum = Class.ImageFrames - 1;
        }
        else if (Class.Type == StructType.STRUCT_WEAP)
        {
            shapenum = Health_Ratio() < 0x0080 ? 1 : 0;
        }
        else if (Class.Type == StructType.STRUCT_STORAGE)
        {
            // The silo fills up with the house's Tiberium.
            int level = 0;
            if (House.Capacity != 0)
                level = (int)((House.Tiberium * 5) / House.Capacity);
            shapenum += Math.Clamp(level, 0, 4);
            if (Health_Ratio() < 0x0080)
                shapenum += 5;
        }
        else if (Health_Ratio() < 0x0080)
        {
            // Damaged frames follow the last of the normal animations.
            if (!Class.IsSimpleDamage)
            {
                var idle = Class.Anim(BStateType.BSTATE_IDLE);
                var active = Class.Anim(BStateType.BSTATE_ACTIVE);
                var aux1 = Class.Anim(BStateType.BSTATE_AUX1);
                var aux2 = Class.Anim(BStateType.BSTATE_AUX2);
                int largest = Math.Max(idle.Start + idle.Count, active.Start + active.Count);
                largest = Math.Max(largest, aux1.Start + aux1.Count);
                largest = Math.Max(largest, aux2.Start + aux2.Count);
                shapenum += largest;
            }
            else
            {
                shapenum = Class.ImageFrames - 2;
            }
        }
        return (shapenum, false);
    }

    /// <summary>Mark: buildings also place and lift their bib; walls become wall overlays.</summary>
    public override bool Mark(MarkType mark = MarkType.MARK_CHANGE)
    {
        if (base.Mark(mark))
        {
            short[] offset = Overlap_List();
            short[] occupy = Occupy_List();
            int cell = TdMath.CoordCell(Coord);

            switch (mark)
            {
                case MarkType.MARK_UP:
                {
                    Game.Map.Pick_Up(cell, this);
                    int bibcell = cell;
                    if (Class.Bib_And_Offset(Game.Special, out SmudgeType bib, ref bibcell))
                    {
                        var smudge = SmudgeClass.Create(Game, bib);
                        if (smudge != null)
                        {
                            smudge.Disown(bibcell);
                            smudge.Delete();
                        }
                    }
                    break;
                }

                case MarkType.MARK_DOWN:
                    if (Class.IsWall)
                    {
                        OverlayType wall = Class.Type switch
                        {
                            StructType.STRUCT_BRICK_WALL => OverlayType.OVERLAY_BRICK_WALL,
                            StructType.STRUCT_BARBWIRE_WALL => OverlayType.OVERLAY_BARBWIRE_WALL,
                            StructType.STRUCT_SANDBAG_WALL => OverlayType.OVERLAY_SANDBAG_WALL,
                            StructType.STRUCT_WOOD_WALL => OverlayType.OVERLAY_WOOD_WALL,
                            StructType.STRUCT_CYCLONE_WALL => OverlayType.OVERLAY_CYCLONE_WALL,
                            _ => OverlayType.OVERLAY_NONE,
                        };
                        if (wall != OverlayType.OVERLAY_NONE)
                            OverlayClass.Create(Game, wall, cell, House.Class.House);
                        Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                        Delete_This();
                    }
                    else
                    {
                        if (Can_Enter_Cell(cell) != MoveType.MOVE_OK)
                            return false;
                        int newcell = cell;
                        if (Class.Bib_And_Offset(Game.Special, out SmudgeType bib, ref newcell))
                            SmudgeClass.Create(Game, bib, TdMath.CellCoord(newcell), House.Class.House);
                        Game.Map.Place_Down(cell, this);
                    }
                    break;

                default:
                    Game.Map.Refresh_Cells(cell, offset);
                    Game.Map.Refresh_Cells(cell, occupy);
                    break;
            }
            return true;
        }
        return false;
    }

    /// <summary>Fire_At: muzzle flashes (the SAM's launch flare; the Obelisk's charge resets).</summary>
    public override BulletClass? Fire_At(uint target, int which = 0)
    {
        var weapon = Game.Types.Weapon(which == 0 ? Class.Primary : Class.Secondary);
        var bullet = base.Fire_At(target, which);
        if (bullet != null)
        {
            int face = (int)TdMath.DirFacingType(PrimaryFacing.Current);
            if (Class.Type == StructType.STRUCT_SAM)
            {
                AnimClass.Create(Game, (AnimType)((int)AnimType.ANIM_SAM_N + face), Center_Coord())?.Attach_To(this);
                Game.Sound_Effect(weapon.Sound, Coord);
            }
            else
            {
                Game.Sound_Effect(weapon.Sound, Coord);
                AnimClass? anim = null;
                if (weapon.Fires == BulletType.BULLET_BULLET)
                {
                    anim = AnimClass.Create(Game, (AnimType)((int)AnimType.ANIM_GUN_N + face), Fire_Coord(which));
                }
                else
                {
                    switch (weapon.Fires)
                    {
                        case BulletType.BULLET_SPREADFIRE:
                            break;
                        case BulletType.BULLET_LASER:
                            IsCharging = false;
                            IsCharged = false;
                            Stage.Set_Stage(0);
                            Stage.Set_Rate(0);
                            break;
                        default:
                            anim = AnimClass.Create(Game, AnimType.ANIM_MUZZLE_FLASH, Fire_Coord(which));
                            break;
                    }
                }
                anim?.Attach_To(this);
                Mark(MarkType.MARK_CHANGE);
            }
        }
        return bullet;
    }

    public override void AI()
    {
        bool stagechange = Stage.Graphic_Logic();
        bool toloop = false;

        if (Class.Type == StructType.STRUCT_SAM && stagechange)
            Mark(MarkType.MARK_CHANGE);

        // The building animation: flag the end of a one-shot state, loop the others.
        if ((!Class.IsTurretEquipped && Class.Type != StructType.STRUCT_OBELISK) || Mission == MissionType.MISSION_CONSTRUCTION || Mission == MissionType.MISSION_DECONSTRUCTION)
        {
            if (stagechange)
            {
                var ctrl = Fetch_Anim_Control();
                // MCV deploy (the yard re-packing): ready a little early, frame 23.
                if (Stage.Fetch_Stage() == ctrl.Start + ctrl.Count - 1 ||
                    (Game.Special.IsMCVDeploy && Class.Type == StructType.STRUCT_CONST && Mission == MissionType.MISSION_DECONSTRUCTION && Stage.Fetch_Stage() == (42 - 19)))
                    IsReadyToCommence = true;
                if (Stage.Fetch_Stage() >= ctrl.Start + ctrl.Count)
                    toloop = true;
                Mark(MarkType.MARK_CHANGE);
            }
            else if (BState == BStateType.BSTATE_NONE || Stage.Fetch_Rate() == 0)
            {
                IsReadyToCommence = true;
            }
        }

        if (Door.Time_To_Redraw())
        {
            Door.Clear_Redraw_Flag();
            Mark(MarkType.MARK_CHANGE);
        }

        if (toloop)
        {
            var ctrl = Fetch_Anim_Control();
            if (BState == BStateType.BSTATE_CONSTRUCTION || BState == BStateType.BSTATE_IDLE)
                Stage.Set_Rate(Game.Options.Normalize_Delay(ctrl.Rate));
            else
                Stage.Set_Rate(ctrl.Rate);
            Stage.Set_Stage(ctrl.Start);
            Mark(MarkType.MARK_CHANGE);
        }

        if (IsReadyToCommence && BState != BStateType.BSTATE_CONSTRUCTION && Commence())
            IsReadyToCommence = false;

        base.AI();

        if (IsReadyToCommence && Commence())
            IsReadyToCommence = false;

        // A queued animation state starts.
        if (QueueBState != BStateType.BSTATE_NONE)
        {
            if (BState != QueueBState)
            {
                BState = QueueBState;
                var ctrl = Fetch_Anim_Control();
                if (BState == BStateType.BSTATE_CONSTRUCTION || BState == BStateType.BSTATE_IDLE)
                    Stage.Set_Rate(Game.Options.Normalize_Delay(ctrl.Rate));
                else
                    Stage.Set_Rate(ctrl.Rate);
                Stage.Set_Stage(ctrl.Start);
            }
            QueueBState = BStateType.BSTATE_NONE;
        }

        // Power follows the building's health.
        if (Strength != LastStrength)
        {
            int oldpower = Power_Output();
            LastStrength = Strength;
            int newpower = Power_Output();
            House.Adjust_Power(newpower - oldpower);
        }

        // Destroyed: after the countdown, debris and survivors.
        if (Strength == 0)
        {
            if (CountDown.Expired)
            {
                Limbo();
                Drop_Debris(WhomToRepay);
                Delete_This();
            }
            return;
        }

        // The Obelisk charges up (with full power) before it can fire.
        if (Class.Type == StructType.STRUCT_OBELISK && BState != BStateType.BSTATE_CONSTRUCTION)
        {
            if (Game.Target_Legal(TarCom) && House.Power_Fraction() >= 0x0100)
            {
                if (!IsCharged)
                {
                    if (IsCharging)
                    {
                        if (stagechange)
                        {
                            Mark(MarkType.MARK_CHANGE);
                            if (Stage.Fetch_Stage() >= 4)
                            {
                                IsCharged = true;
                                IsCharging = false;
                                Stage.Set_Rate(0);
                            }
                        }
                    }
                    else
                    {
                        IsCharged = false;
                        IsCharging = true;
                        Stage.Set_Stage(0);
                        Stage.Set_Rate((int)TdDefines.OBELISK_ANIMATION_RATE);
                        Game.Sound_Effect(VocType.VOC_LASER_POWER, Coord);
                    }
                }
            }
            else if (IsCharging || IsCharged)
            {
                Mark(MarkType.MARK_CHANGE);
                IsCharging = false;
                IsCharged = false;
                Stage.Set_Stage(0);
                Stage.Set_Rate(0);
            }
        }

        // Repairing costs money a step at a time.
        if (IsRepairing && (Game.Frame % 15) == 0)
        {
            IsWrenchVisible = !IsWrenchVisible;
            Mark(MarkType.MARK_CHANGE);

            int cost = Class.Repair_Cost(Game.Types);
            int step = Class.Repair_Step();
            if (House.Available_Money() >= cost)
            {
                House.Spend_Money(cost);
                Strength += step;
                if (Strength >= Class.MaxStrength)
                {
                    Strength = Class.MaxStrength;
                    IsRepairing = false;
                }
            }
            else
            {
                IsRepairing = false;
            }
        }

        // A finished product leaves the factory.
        if (Factory != null && Factory.Has_Completed() && PlacementDelay.Expired)
        {
            switch (Exit_Object(Factory.Get_Object()))
            {
                case 0:
                    Factory.Abandon();
                    Factory.Delete();
                    Factory = null;
                    break;
                case 1:
                    PlacementDelay.Set(TdDefines.TICKS_PER_SECOND * 3);
                    break;
                case 2:
                    Factory.Completed();
                    Factory.Delete();
                    Factory = null;
                    break;
            }
        }

        // The computer repairs (or sells) damaged buildings and keeps its factories busy.
        if (!House.IsHuman && Mission != MissionType.MISSION_CONSTRUCTION && Mission != MissionType.MISSION_DECONSTRUCTION)
        {
            int ratio = 0x0040;
            if (Game.Scenario > 6)
                ratio = 0x0080;
            if (Game.Scenario > 10)
                ratio = 0x00C0;

            if (Class.IsRepairable && Health_Ratio() <= (uint)ratio)
            {
                if (House.Available_Money() >= TdDefines.REPAIR_THRESHHOLD)
                {
                    Repair(1);
                }
                else if (IsTickedOff && Game.Scenario > 2 && Game.Random_Pick(0, 50) < Game.Scenario && Trigger == null)
                {
                    // (Not the Temple of Nod in GDI's last mission.)
                    if (Game.GameToPlay != GameType.GAME_NORMAL || Game.Scenario != 15 || Game.PlayerPtr.ActLike != HousesType.HOUSE_GOOD || Class.Type != StructType.STRUCT_TEMPLE)
                        Sell_Back(1);
                }
            }

            if (Class.ToBuild != RTTIType.RTTI_NONE)
            {
                if (Factory != null)
                {
                    if (PlacementDelay.Expired && !Factory.Is_Building())
                    {
                        Factory.Abandon();
                        Factory.Delete();
                        Factory = null;
                    }
                }
                else if (House.IsStarted && House.Available_Money() > 10)
                {
                    var techno = House.Suggest_New_Object(Class.ToBuild);
                    if (techno != null)
                    {
                        Factory = FactoryClass.Create(Game);
                        if (Factory != null)
                        {
                            if (!Factory.Set(techno, House))
                            {
                                Factory.Delete();
                                Factory = null;
                            }
                            else
                            {
                                House.Production_Begun(Factory.Get_Object());
                                Factory.Start();
                            }
                        }
                    }
                }
            }
        }

        // C4 placed by the commando goes off.
        if (IsGoingToBlow && CountDown.Expired)
        {
            var saboteur = Game.As_Techno(WhomToRepay);
            if (saboteur != null && saboteur.IsActive && saboteur.House != null && saboteur.House.IsHuman)
                Game.On_Achievement_Event(saboteur.House, "BUILDING_DESTROYED_C4", Class.IniName);
            Game.SabotagedType = Class.Type;
            int damage = 5000;
            Take_Damage(ref damage, 0, WarheadType.WARHEAD_FIRE, Game.As_Techno(WhomToRepay));
            Mark(MarkType.MARK_CHANGE);
        }

        if (IsInRecoilState)
        {
            IsInRecoilState = false;
            Mark(MarkType.MARK_CHANGE);
        }

        // Turrets turn (the SAM faster).
        if (Class.IsTurretEquipped && Mission != MissionType.MISSION_CONSTRUCTION && Mission != MissionType.MISSION_DECONSTRUCTION && PrimaryFacing.Is_Rotating())
        {
            if (PrimaryFacing.Rotation_Adjust(Class.Type == StructType.STRUCT_SAM ? 15 : 12))
                Mark(MarkType.MARK_CHANGE);
        }
    }

    /// <summary>Unlimbo: walls become overlays; buildings count towards the house's factories and scans.</summary>
    public override bool Unlimbo(uint coord, byte dir = 0)
    {
        if (Class.IsWall)
        {
            if (Can_Enter_Cell(TdMath.CoordCell(coord), FacingType.FACING_NONE) == MoveType.MOVE_OK)
            {
                OverlayType otype = Class.Type switch
                {
                    StructType.STRUCT_SANDBAG_WALL => OverlayType.OVERLAY_SANDBAG_WALL,
                    StructType.STRUCT_CYCLONE_WALL => OverlayType.OVERLAY_CYCLONE_WALL,
                    StructType.STRUCT_BRICK_WALL => OverlayType.OVERLAY_BRICK_WALL,
                    StructType.STRUCT_BARBWIRE_WALL => OverlayType.OVERLAY_BARBWIRE_WALL,
                    StructType.STRUCT_WOOD_WALL => OverlayType.OVERLAY_WOOD_WALL,
                    _ => OverlayType.OVERLAY_NONE,
                };
                if (otype != OverlayType.OVERLAY_NONE)
                {
                    var o = OverlayClass.Create_One_Of(Game, otype, House);
                    if (o != null && o.Unlimbo(coord))
                    {
                        Game.Map[TdMath.CoordCell(coord)].Owner = House.Class.House;
                        Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                        Delete_This();
                        return true;
                    }
                }
            }
            return false;
        }

        if (base.Unlimbo(coord, dir))
        {
            House.BScan |= 1L << (int)Class.Type;
            House.ActiveBScan |= 1L << (int)Class.Type;
            House.Recalc_Center();

            switch (Class.ToBuild)
            {
                case RTTIType.RTTI_AIRCRAFTTYPE:
                    House.AircraftFactories++;
                    break;
                case RTTIType.RTTI_INFANTRYTYPE:
                    House.InfantryFactories++;
                    break;
                case RTTIType.RTTI_UNITTYPE:
                    House.UnitFactories++;
                    break;
                case RTTIType.RTTI_BUILDINGTYPE:
                    House.BuildingFactories++;
                    break;
            }
            House.IsRecalcNeeded = true;
            LastStrength = 0;

            // As the original's precedence: (not discovered && visible) || multiplayer.
            if ((!Is_Discovered_By_Player(House) && Game.Map[TdMath.CoordCell(coord)].Is_Visible(House)) || Game.GameToPlay != GameType.GAME_NORMAL)
            {
                if (House.IsHuman)
                    Revealed(House);
            }
            if (!House.IsHuman)
                Revealed(House);

            if (Is_Owned_By_Player())
                Game.Map.Flag_To_Redraw(false);
            return true;
        }
        return false;
    }

    /// <summary>Take_Damage: fire and smoke on the cells; on death, craters and fireballs, and the
    /// building waits a moment (CountDown) before collapsing.</summary>
    public override ResultType Take_Damage(ref int damage, int distance, WarheadType warhead, TechnoClass? source = null)
    {
        ResultType res = ResultType.RESULT_NONE;

        if (this != source)
        {
            if (source != null)
                Base_Is_Attacked(source);

            short[] offset = Occupy_List();

            // A lowered SAM site is armoured.
            if (Class.Type == StructType.STRUCT_SAM && Status == SAM_UNDERGROUND)
            {
                damage /= 2;
                damage++;
            }

            // The ion cannon does half again to the Temple of Nod.
            if (Game.GameToPlay == GameType.GAME_NORMAL && Class.Type == StructType.STRUCT_TEMPLE && warhead == WarheadType.WARHEAD_PB)
                damage += damage / 2;

            res = base.Take_Damage(ref damage, distance, warhead, source);

            switch (res)
            {
                case ResultType.RESULT_DESTROYED:
                {
                    while (Cargo.Attached_Object() != null)
                    {
                        FootClass obj = Cargo.Detach_Object()!;
                        Detach_All(true);
                        obj.Delete();
                    }

                    Game.Sound_Effect(VocType.VOC_XPLOBIG4, Coord);
                    foreach (short o in offset)
                    {
                        if (o == TdMath.RefreshEol)
                            break;
                        int cell = TdMath.CoordCell(Coord) + o;
                        uint center = TdMath.CellCoord(cell);
                        SmudgeClass.Create(Game, (SmudgeType)Game.Random_Pick((int)SmudgeType.SMUDGE_CRATER1, (int)SmudgeType.SMUDGE_CRATER6), center);
                        if (Game.Random_Pick(0, 1) == 0)
                        {
                            Create_Anim_Rtl(AnimType.ANIM_FIRE_SMALL, center, 0x0080, (0, 7), (1, 3));
                            if (Game.Random_Pick(0, 1) == 0)
                                Create_Anim_Rtl(AnimType.ANIM_FIRE_MED, center, 0x0040, (0, 7), (1, 3));
                        }
                        // new AnimClass(ANIM_FBALL1, Coord_Scatter(...), Random_Pick(0,3)): arguments right to left.
                        int delay = Game.Random_Pick(0, 3);
                        AnimClass.Create(Game, AnimType.ANIM_FBALL1, Game.Coord_Scatter(center, 0x0040), delay);
                    }

                    int shakes = Class.Cost_Of(Game.Types) / 400;
                    if (shakes != 0)
                    {
                        Game.Shake_The_Screen(shakes, Owner());
                        if (source != null && Owner() != source.Owner())
                            Game.Shake_The_Screen(shakes, source.Owner());
                    }
                    Game.Sound_Effect(VocType.VOC_CRUMBLE, Coord);

                    // Sold buildings vanish at once.
                    if (Mission == MissionType.MISSION_DECONSTRUCTION)
                    {
                        CountDown.Set(0);
                        Stage.Set_Rate(0);
                    }
                    else
                    {
                        CountDown.Set(8);
                    }

                    if (Class.Type == StructType.STRUCT_TEMPLE && warhead == WarheadType.WARHEAD_PB)
                    {
                        Game.TempleIoned = true;
                        if (Game.GameToPlay == GameType.GAME_NORMAL && !House.IsHuman && source?.House != null && source.House.IsHuman)
                            Game.On_Achievement_Event(source.House, "ION_DESTROYS_TEMPLE", Class.IniName);
                    }
                    else
                    {
                        Game.TempleIoned = false;
                    }

                    House?.Check_Pertinent_Structures();
                    break;
                }

                case ResultType.RESULT_HALF:
                    // The oil pump catches fire.
                    if (Class.Type == StructType.STRUCT_PUMP)
                        AnimClass.Create(Game, AnimType.ANIM_OILFIELD_BURN, TdMath.CoordAdd(Coord, 0x00400130), 1)?.Attach_To(this);
                    goto case ResultType.RESULT_MAJOR;

                case ResultType.RESULT_MAJOR:
                    Game.Sound_Effect(VocType.VOC_XPLOBIG4, Coord);
                    foreach (short o in offset)
                    {
                        if (o == TdMath.RefreshEol)
                            break;
                        int cell = TdMath.CoordCell(Coord) + o;
                        uint center = TdMath.CellCoord(cell);
                        AnimClass? anim = null;
                        if (warhead == WarheadType.WARHEAD_FIRE)
                        {
                            switch (Game.Random_Pick(0, 13))
                            {
                                case 0:
                                case 1:
                                case 2:
                                case 3:
                                case 4:
                                {
                                    int loops = Game.Random_Pick(1, 3);
                                    anim = AnimClass.Create(Game, AnimType.ANIM_ON_FIRE_SMALL, Game.Coord_Scatter(center, 0x0060), 0, loops);
                                    break;
                                }
                                case 5:
                                case 6:
                                case 7:
                                {
                                    int loops = Game.Random_Pick(1, 3);
                                    anim = AnimClass.Create(Game, AnimType.ANIM_ON_FIRE_MED, Game.Coord_Scatter(center, 0x0060), 0, loops);
                                    break;
                                }
                                case 8:
                                    anim = AnimClass.Create(Game, AnimType.ANIM_ON_FIRE_BIG, Game.Coord_Scatter(center, 0x0060), 0, 1);
                                    break;
                            }
                        }
                        else if (Game.Random_Pick(0, 1) == 0)
                        {
                            anim = Create_Anim_Rtl(AnimType.ANIM_FIRE_SMALL, center, 0x0060, (0, 7), (1, 3));
                        }
                        anim?.Attach_To(this);
                    }
                    break;
            }

            // Strike back (or look round, for the player's buildings hit by aircraft).
            if (source != null && res != ResultType.RESULT_NONE)
            {
                House.Attacked(this);
                WhoLastHurtMe = source.Owner();

                if (Class.Type != StructType.STRUCT_SAM && !House.Is_Ally(source) && Class.Primary != WeaponType.WEAPON_NONE &&
                    (!Game.Target_Legal(TarCom) || !In_Range(TarCom)))
                {
                    if (source.What_Am_I() != RTTIType.RTTI_AIRCRAFT && (!House.IsHuman || Game.Special.IsSmartDefense))
                        Assign_Target(source.As_Target());
                    else if (!PrimaryFacing.Is_Rotating())
                        PrimaryFacing.Set_Desired((byte)Game.Random_Pick((int)DirType.DIR_N, (int)DirType.DIR_MAX));
                }
            }
        }
        return res;
    }

    /// <summary>new AnimClass(type, Coord_Scatter(center, distance), Random_Pick(delay), Random_Pick(loops)),
    /// with the arguments evaluated right to left as the original's compiler does.</summary>
    AnimClass? Create_Anim_Rtl(AnimType type, uint center, int scatter, (int Lo, int Hi) delay, (int Lo, int Hi) loops)
    {
        int l = Game.Random_Pick(loops.Lo, loops.Hi);
        int d = Game.Random_Pick(delay.Lo, delay.Hi);
        uint coord = Game.Coord_Scatter(center, scatter);
        return AnimClass.Create(Game, type, coord, d, l);
    }

    public override void Look(bool incremental = false)
    {
        if (Class.SightRange != 0)
            Game.Map.Sight_From(House, TdMath.CoordCell(Center_Coord()), Class.SightRange, false);
    }

    /// <summary>Drop_Debris: the crew escape (some may attack the destroyer), smoke and craters.</summary>
    void Drop_Debris(uint source)
    {
        // Nod mission 10: Dr. Chan comes out of the mission building.
        if (Game.GameToPlay == GameType.GAME_NORMAL && Class.Type == StructType.STRUCT_MISSION && Game.PlayerPtr.ActLike == HousesType.HOUSE_BAD && Game.Scenario == 10)
        {
            var chan = InfantryClass.Create(Game, InfantryType.INFANTRY_CHAN, House.Class.House);
            Game.ScenarioInit++;
            if (chan != null && chan.Unlimbo(Center_Coord(), (byte)DirType.DIR_N))
            {
                chan.Trigger = Scenario.TriggerClass.As_Pointer(Game, "win");
                chan.Strength = Game.Random_Pick(5, chan.Class.MaxStrength);
                Game.ScenarioInit--;
                chan.Scatter(0, true);
                Game.ScenarioInit++;
                chan.Assign_Mission(MissionType.MISSION_GUARD_AREA);
            }
            else
            {
                chan?.Delete();
                Game.PlayerPtr.Flag_To_Win();
            }
            Game.ScenarioInit--;
        }

        int cell = TdMath.CoordCell(Coord);
        short[] offset = Occupy_List();
        int odds = 2;
        if (Game.Target_Legal(WhomToRepay))
            odds -= 1;
        if (IsCaptured)
            odds += 6;

        foreach (short o in offset)
        {
            if (o == TdMath.RefreshEol)
                break;
            int newcell = cell + o;

            if (!House.IsToDie && !IsSurvivorless && Game.Random_Pick(0, odds) == 1)
            {
                var i = InfantryClass.Create(Game, Crew_Type(), House.Class.House);
                if (i != null)
                {
                    if (Class.HasBuildup && i.Class.IsNominal)
                        i.IsTechnician = true;
                    Game.ScenarioInit++;
                    if (i.Unlimbo(TdMath.CellCoord(newcell), (byte)DirType.DIR_N))
                    {
                        i.Strength = Game.Random_Pick(5, i.Class.MaxStrength);
                        i.Scatter(0, true);
                        if (source != Target.TARGET_NONE && !House.Is_Ally(Game.As_Object(source)))
                        {
                            i.Assign_Mission(MissionType.MISSION_ATTACK);
                            i.Assign_Target(source);
                        }
                        else
                        {
                            i.Assign_Mission(House.IsHuman ? MissionType.MISSION_GUARD : MissionType.MISSION_HUNT);
                        }
                    }
                    else
                    {
                        i.Delete();
                    }
                    Game.ScenarioInit--;
                }
            }

            switch (Game.Random_Pick(0, 5))
            {
                case 0:
                case 1:
                case 2:
                {
                    int loops = Game.Random_Pick(1, 2);
                    int delay = Game.Random_Pick(0, 5);
                    AnimClass.Create(Game, AnimType.ANIM_SMOKE_M, Game.Coord_Scatter(TdMath.CellCoord(newcell), 0x0050, false), delay, loops);
                    break;
                }
            }

            if (Game.Random_Pick(0, 3) == 0)
            {
                SmudgeClass.Create(Game, (SmudgeType)Game.Random_Pick((int)SmudgeType.SMUDGE_SCORCH1, (int)SmudgeType.SMUDGE_SCORCH6), TdMath.CellCoord(newcell));
            }
            else
            {
                uint coord = Game.Coord_Scatter(TdMath.CellCoord(newcell), 0x0080, false);
                SmudgeClass.Create(Game, (SmudgeType)Game.Random_Pick((int)SmudgeType.SMUDGE_CRATER1, (int)SmudgeType.SMUDGE_CRATER6), coord);
            }
        }
    }

    public override void Active_Click_With(ActionType action, ObjectClass obj)
    {
        if (action == ActionType.ACTION_ATTACK)
            Player_Assign_Mission(MissionType.MISSION_ATTACK, obj.As_Target());
        if (action == ActionType.ACTION_TOGGLE_PRIMARY && Class.IsFactory)
            Game.OutList.Add(EventClass.Primary(Game, As_Target()));
    }

    public override void Active_Click_With(ActionType action, int cell)
    {
        if (action == ActionType.ACTION_ATTACK)
            Player_Assign_Mission(MissionType.MISSION_ATTACK, Target.As_Target(cell));

        // MCV deploy rules: the yard packs up and drives where clicked.
        if (action == ActionType.ACTION_MOVE && Class.Type == StructType.STRUCT_CONST)
        {
            Game.OutList.Add(EventClass.Archive(Game, As_Target(), Target.As_Target(cell)));
            Game.OutList.Add(EventClass.Sell(Game, As_Target()));
            uint coord = Game.Map.Pixel_To_Coord(Game.Input.MouseX, Game.Input.MouseY);
            var player = Game.PlayerPtr.Class.House;
            Game.OutList.Add(EventClass.Anim(Game, AnimType.ANIM_MOVE_FLASH, player, coord, 1 << (int)player));
        }
    }

    /// <summary>Assign_Target: buildings only take targets in range (the SAM any).</summary>
    public override void Assign_Target(uint target)
    {
        if (Class.Type != StructType.STRUCT_SAM && !In_Range(target, 0))
            target = Target.TARGET_NONE;
        base.Assign_Target(target);
    }

    /// <summary>Exit_Object: a product leaves the factory: 0 = can't, 1 = try later, 2 = done.</summary>
    public override int Exit_Object(TechnoClass? techno)
    {
        if (techno == null)
            return 0;

        var ttype = techno.Techno_Type_Class();
        techno.IsLocked = true;

        switch (techno.What_Am_I())
        {
            case RTTIType.RTTI_AIRCRAFT:
            {
                var air = (AircraftClass)techno;
                if (!In_Radio_Contact())
                {
                    // Lands on the free helipad.
                    air.Altitude = 0;
                    Game.ScenarioInit++;
                    if (air.Unlimbo(Docking_Coord(), air.Pose_Dir()))
                    {
                        Transmit_Message(RadioMessageType.RADIO_HELLO, air);
                        Transmit_Message(RadioMessageType.RADIO_TETHER);
                        Game.ScenarioInit--;
                        return 2;
                    }
                    Game.ScenarioInit--;
                }
                else
                {
                    // The helipad is taken: it flies in from the nearer map edge.
                    var map = Game.Map;
                    int cell;
                    if (TdMath.CellX(TdMath.CoordCell(Center_Coord())) - map.MapCellX < map.MapCellWidth / 2)
                        cell = TdMath.XyCell(map.MapCellX - 1, Game.Random_Pick(0, map.MapCellHeight - 1) + map.MapCellY);
                    else
                        cell = TdMath.XyCell(map.MapCellX + map.MapCellWidth, Game.Random_Pick(0, map.MapCellHeight - 1) + map.MapCellY);

                    Game.ScenarioInit++;
                    if (air.Unlimbo(TdMath.CellCoord(cell), (byte)DirType.DIR_N))
                    {
                        air.Assign_Destination(Target.As_Target(Nearby_Location(air)));
                        air.Assign_Mission(MissionType.MISSION_MOVE);
                        Game.ScenarioInit--;
                        return 2;
                    }
                    Game.ScenarioInit--;
                }
                break;
            }

            case RTTIType.RTTI_INFANTRY:
            case RTTIType.RTTI_UNIT:
                switch (Class.Type)
                {
                    case StructType.STRUCT_REFINERY:
                        if (techno is UnitClass unit)
                        {
                            // The harvester backs out of the refinery.
                            int cell = TdMath.AdjacentCellOf(TdMath.CoordCell(Center_Coord()), FacingType.FACING_SW);
                            Game.ScenarioInit++;
                            if (unit.Unlimbo(TdMath.CoordAdd(unit.Coord, 0x00550060), (byte)DirType.DIR_SW_X2))
                            {
                                unit.PrimaryFacing.Set((byte)DirType.DIR_SW_X2);
                                Transmit_Message(RadioMessageType.RADIO_HELLO, unit);
                                Transmit_Message(RadioMessageType.RADIO_TETHER);
                                unit.Assign_Mission(MissionType.MISSION_HARVEST);
                                unit.Force_Track(DriveClass.OUT_OF_REFINERY, TdMath.CellCoord(cell));
                                unit.Set_Speed(128);
                            }
                            Game.ScenarioInit--;
                        }
                        else
                        {
                            // As the original: Scatter(true) passes 1 as the threat coordinate.
                            techno.Scatter(1);
                        }
                        break;

                    case StructType.STRUCT_AIRSTRIP:
                        // Vehicles from the airstrip arrive by cargo plane.
                        if (Game.Create_Special_Reinforcement(House, Game.Types.Aircraft(AircraftType.AIRCRAFT_CARGO), ttype, TeamMissionType.TMISSION_UNLOAD, (int)As_Target()))
                        {
                            techno.Delete();
                            return 2;
                        }
                        return 0;

                    case StructType.STRUCT_WEAP:
                        Game.ScenarioInit++;
                        if (techno.Unlimbo(TdMath.CoordAdd(Coord, Class.ExitCoord()), (byte)DirType.DIR_SW))
                        {
                            techno.Mark(MarkType.MARK_UP);
                            techno.Coord = TdMath.CoordAdd(Coord, Class.ExitCoord());
                            techno.Mark(MarkType.MARK_DOWN);
                            Transmit_Message(RadioMessageType.RADIO_HELLO, techno);
                            Transmit_Message(RadioMessageType.RADIO_TETHER);
                            Assign_Mission(MissionType.MISSION_UNLOAD);
                            Game.ScenarioInit--;
                            return 2;
                        }
                        Game.ScenarioInit--;
                        break;

                    case StructType.STRUCT_BARRACKS:
                    case StructType.STRUCT_HAND:
                    {
                        int cell = Find_Exit_Cell(techno);
                        if (cell != 0)
                        {
                            byte dir = DirectionToCell(cell);
                            uint start = TdMath.CoordAdd(Coord, Class.ExitCoord());
                            Game.ScenarioInit++;
                            if (techno.Unlimbo(start, dir))
                            {
                                techno.Assign_Mission(MissionType.MISSION_MOVE);
                                techno.Assign_Destination(Target.As_Target(cell));
                                if (Transmit_Message(RadioMessageType.RADIO_HELLO, techno) == RadioMessageType.RADIO_ROGER)
                                    Transmit_Message(RadioMessageType.RADIO_UNLOAD);
                                Game.ScenarioInit--;
                                return 2;
                            }
                            Game.ScenarioInit--;
                        }
                        break;
                    }
                }
                break;

            case RTTIType.RTTI_BUILDING:
                // The computer places its buildings where its base plan says (in skirmish, where
                // the base needs them most).
                if (!House.IsHuman)
                {
                    var node = Game.Base.Next_Buildable(((BuildingClass)techno).Class.Type);
                    if (Game.GameToPlay == GameType.GAME_NORMAL)
                    {
                        if (node != null)
                        {
                            if (Flush_For_Placement(techno, TdMath.CoordCell(node.Coord)))
                                return 1;
                            if (techno.Unlimbo(node.Coord))
                                return 2;
                        }
                    }
                    else
                    {
                        uint coord = node != null ? node.Coord : House.Find_Build_Location((BuildingClass)techno);
                        if (coord != 0)
                        {
                            if (Flush_For_Placement(techno, TdMath.CoordCell(coord)))
                                return 1;
                            if (techno.Unlimbo(coord))
                            {
                                if (node != null && ((BuildingClass)techno).Class.Type == House.BuildStructure)
                                    House.BuildStructure = StructType.STRUCT_NONE;
                                return 2;
                            }
                        }
                    }
                }
                break;
        }
        return 0;
    }

    /// <summary>Update_Buildables: adds what this factory lets the player build to the sidebar.</summary>
    public void Update_Buildables()
    {
        if (House != Game.PlayerPtr && (Game.GameToPlay != GameType.GAME_GLYPHX_MULTIPLAYER || !House.IsHuman))
            return;

        bool buildableViaCapture = IsCaptured && ActLike != House.ActLike;

        if (!IsInLimbo && Is_Discovered_By_Player())
        {
            switch (Class.ToBuild)
            {
                case RTTIType.RTTI_BUILDINGTYPE:
                    for (var i = StructType.STRUCT_FIRST; i < StructType.STRUCT_COUNT; i++)
                        if (Game.PlayerPtr.Can_Build(Game.Types.Building(i), ActLike))
                            Game.Sidebar_Add(RTTIType.RTTI_BUILDINGTYPE, (int)i, House, buildableViaCapture);
                    break;
                case RTTIType.RTTI_UNITTYPE:
                    for (var u = UnitType.UNIT_FIRST; u < UnitType.UNIT_COUNT; u++)
                        if (Game.PlayerPtr.Can_Build(Game.Types.Unit(u), ActLike))
                            Game.Sidebar_Add(RTTIType.RTTI_UNITTYPE, (int)u, House, buildableViaCapture);
                    break;
                case RTTIType.RTTI_INFANTRYTYPE:
                    for (var f = InfantryType.INFANTRY_FIRST; f < InfantryType.INFANTRY_COUNT; f++)
                        if (Game.PlayerPtr.Can_Build(Game.Types.Infantry(f), ActLike))
                            Game.Sidebar_Add(RTTIType.RTTI_INFANTRYTYPE, (int)f, House, buildableViaCapture);
                    break;
                case RTTIType.RTTI_AIRCRAFTTYPE:
                    for (var a = AircraftType.AIRCRAFT_FIRST; a < AircraftType.AIRCRAFT_COUNT; a++)
                        if (Game.PlayerPtr.Can_Build(Game.Types.Aircraft(a), ActLike))
                            Game.Sidebar_Add(RTTIType.RTTI_AIRCRAFTTYPE, (int)a, House, buildableViaCapture);
                    break;
            }
        }
    }

    public override void Fire_Out() { }

    public override bool Limbo()
    {
        if (!IsInLimbo)
        {
            switch (Class.ToBuild)
            {
                case RTTIType.RTTI_AIRCRAFTTYPE:
                    House.AircraftFactories--;
                    break;
                case RTTIType.RTTI_INFANTRYTYPE:
                    House.InfantryFactories--;
                    break;
                case RTTIType.RTTI_UNITTYPE:
                    House.UnitFactories--;
                    break;
                case RTTIType.RTTI_BUILDINGTYPE:
                    House.BuildingFactories--;
                    break;
            }
            House.IsRecalcNeeded = true;
            House.Recalc_Center();
            House.Adjust_Power(-Power_Output());
            House.Adjust_Drain(-Class.Drain);
            House.Adjust_Capacity(-Class.Capacity, true);
            if (House == Game.PlayerPtr)
                Game.Map.Flag_To_Redraw(false);

            // The sidebar is worked out again without this building.
            if (IsOwnedByPlayer && Game.ScenarioInit == 0)
            {
                IsInLimbo = true;
                Game.Sidebar_Recalc();
                IsInLimbo = false;
            }
        }
        return base.Limbo();
    }

    public override FireData Fire_Data(int which)
    {
        uint coord = Center_Coord();
        int dist = 0;
        switch (Class.Type)
        {
            case StructType.STRUCT_OBELISK:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x00A8);
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_W, 0x0018);
                break;
            case StructType.STRUCT_SAM:
            case StructType.STRUCT_TURRET:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0030);
                dist = 0x0080;
                break;
            default:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0030);
                dist = 0x0040;
                break;
        }
        return new FireData(coord, dist);
    }

    public override uint Fire_Coord(int which)
    {
        uint coord = Center_Coord();
        switch (Class.Type)
        {
            case StructType.STRUCT_OBELISK:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x00A8);
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_W, 0x0018);
                break;
            case StructType.STRUCT_SAM:
            case StructType.STRUCT_TURRET:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0030);
                coord = TdMath.CoordMove(coord, PrimaryFacing.Current, 0x0080);
                break;
            default:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0030);
                if (Game.Target_Legal(TarCom))
                    coord = TdMath.CoordMove(coord, TdMath.Direction(coord, Game.As_Coord(TarCom)), 0x0040);
                break;
        }
        return coord;
    }

    public override uint Greatest_Threat(ThreatType threat)
    {
        if (Class.Type != StructType.STRUCT_SAM)
        {
            threat |= ThreatType.THREAT_INFANTRY | ThreatType.THREAT_BOATS | ThreatType.THREAT_VEHICLES | ThreatType.THREAT_RANGE;
            if (!House.IsHuman)
                threat |= ThreatType.THREAT_BUILDINGS;
        }
        else
        {
            threat |= ThreatType.THREAT_AREA;
        }

        if (Class.Primary != WeaponType.WEAPON_NONE && Game.Types.Bullet(Game.Types.Weapon(Class.Primary).Fires).IsAntiAircraft)
            threat |= ThreatType.THREAT_AIR;

        return base.Greatest_Threat(threat);
    }

    /// <summary>Grand_Opening: the building starts working — drain and storage, and the free
    /// harvester (refinery) or helicopter (helipad).</summary>
    public void Grand_Opening(bool captured = false)
    {
        House.Adjust_Drain(Class.Drain);
        House.Adjust_Capacity(Class.Capacity);
        House.IsRecalcNeeded = true;

        int rawCost = Class.Raw_Cost(Game.Types, Game.Special);

        if (Class.Type == StructType.STRUCT_REFINERY && Game.ScenarioInit == 0 && !captured && !Game.DebugMap &&
            (!House.IsHuman || PurchasePrice == 0 || PurchasePrice > rawCost))
        {
            int cell = TdMath.CoordCell(TdMath.AdjacentCellCoord(Center_Coord(), TdMath.DirFacing((byte)DirType.DIR_SW)));
            var unit = UnitClass.Create(Game, UnitType.UNIT_HARVESTER, House.Class.House);
            if (unit != null)
            {
                if (!unit.Unlimbo(TdMath.CellCoord(cell), (byte)DirType.DIR_SW))
                {
                    cell = Nearby_Location(unit);
                    if (!unit.Unlimbo(TdMath.CellCoord(cell), (byte)DirType.DIR_SW))
                    {
                        House.Refund_Money(unit.Class.Cost_Of(Game.Special));
                        unit.Delete();
                    }
                }
            }
            else
            {
                House.Refund_Money(Game.Types.Unit(UnitType.UNIT_HARVESTER).Cost_Of(Game.Special));
            }
        }

        if (Class.Type == StructType.STRUCT_HELIPAD && !captured && (!House.IsHuman || PurchasePrice == 0 || PurchasePrice > rawCost))
        {
            Game.ScenarioInit++;
            var air = AircraftClass.Create(Game, House.ActLike == HousesType.HOUSE_GOOD ? AircraftType.AIRCRAFT_ORCA : AircraftType.AIRCRAFT_HELICOPTER, House.Class.House);
            if (air != null)
            {
                air.Altitude = 0;
                if (air.Unlimbo(Docking_Coord(), air.Pose_Dir()))
                {
                    air.Assign_Mission(MissionType.MISSION_GUARD);
                    air.Transmit_Message(RadioMessageType.RADIO_HELLO, this);
                    Transmit_Message(RadioMessageType.RADIO_TETHER);
                }
            }
            Game.ScenarioInit--;
        }
    }

    /// <summary>Repair: -1 toggles, 1 starts, 0 stops.</summary>
    public override void Repair(int control)
    {
        switch (control)
        {
            case -1:
                IsRepairing = !IsRepairing;
                break;
            case 1:
                if (IsRepairing)
                    return;
                IsRepairing = true;
                break;
            case 0:
                if (!IsRepairing)
                    return;
                IsRepairing = false;
                break;
        }

        VocType sound;
        if (IsRepairing)
        {
            if (Strength == Class.MaxStrength)
            {
                sound = VocType.VOC_SCOLD;
            }
            else
            {
                sound = VocType.VOC_BUTTON;
                Clicked_As_Target(Game.PlayerPtr.Class.House);
                IsWrenchVisible = true;
            }
        }
        else
        {
            sound = VocType.VOC_BUTTON;
        }

        if (Is_Owned_By_Player())
            Game.Sound_Effect(sound, Coord);
    }

    /// <summary>Sell_Back: -1 toggles, 1 sells, 0 cancels (only buildings with build-up art).</summary>
    public override void Sell_Back(int control)
    {
        if (Class.HasBuildup)
        {
            bool decon = false;
            switch (control)
            {
                case -1:
                    decon = Mission != MissionType.MISSION_DECONSTRUCTION;
                    break;
                case 1:
                    if (Mission == MissionType.MISSION_DECONSTRUCTION)
                        return;
                    decon = true;
                    break;
                case 0:
                    if (Mission != MissionType.MISSION_DECONSTRUCTION)
                        return;
                    decon = false;
                    break;
            }

            if (decon)
            {
                Assign_Mission(MissionType.MISSION_DECONSTRUCTION);
                if (Is_Owned_By_Player())
                    Clicked_As_Target(Game.PlayerPtr.Class.House);
            }

            if (Is_Owned_By_Player())
                Game.Sound_Effect(VocType.VOC_BUTTON);
        }
    }

    public override ActionType What_Action(ObjectClass obj)
    {
        ActionType action = base.What_Action(obj);

        // Clicking a factory makes it primary (when there are two or more).
        if (action == ActionType.ACTION_SELF)
        {
            if (Class.IsFactory && Game.PlayerPtr == House)
            {
                action = Class.ToBuild switch
                {
                    RTTIType.RTTI_AIRCRAFTTYPE => House.AircraftFactories < 2 ? ActionType.ACTION_NONE : ActionType.ACTION_TOGGLE_PRIMARY,
                    RTTIType.RTTI_INFANTRYTYPE => House.InfantryFactories < 2 ? ActionType.ACTION_NONE : ActionType.ACTION_TOGGLE_PRIMARY,
                    RTTIType.RTTI_UNITTYPE => House.UnitFactories < 2 ? ActionType.ACTION_NONE : ActionType.ACTION_TOGGLE_PRIMARY,
                    _ => ActionType.ACTION_NONE,
                };
            }
            else
            {
                action = ActionType.ACTION_NONE;
            }
        }

        if (action == ActionType.ACTION_ATTACK && Class.Type == StructType.STRUCT_SAM)
            action = ActionType.ACTION_NONE;
        if (action == ActionType.ACTION_MOVE)
            action = ActionType.ACTION_NONE;
        return action;
    }

    public override ActionType What_Action(int cell)
    {
        ActionType action = base.What_Action(cell);
        if (action == ActionType.ACTION_MOVE && (Class.Type != StructType.STRUCT_CONST || !Game.Special.IsMCVDeploy))
            action = ActionType.ACTION_NONE;
        if (action == ActionType.ACTION_ATTACK && Class.Type == StructType.STRUCT_SAM)
            action = ActionType.ACTION_NONE;
        return action;
    }

    /// <summary>Begin_Mode: switch animation state (now if idle, building, or setting up; else next frame).</summary>
    public void Begin_Mode(BStateType bstate)
    {
        QueueBState = bstate;
        if (BState == BStateType.BSTATE_NONE || bstate == BStateType.BSTATE_CONSTRUCTION || Game.ScenarioInit != 0)
        {
            BState = bstate;
            QueueBState = BStateType.BSTATE_NONE;
            var ctrl = Fetch_Anim_Control();
            int rate = ctrl.Rate;
            if (Class.IsRegulated && bstate != BStateType.BSTATE_CONSTRUCTION)
                rate = Game.Options.Normalize_Delay(rate);
            Stage.Set_Rate(rate);
            Stage.Set_Stage(ctrl.Start);
        }
    }

    /// <summary>Read_INI: the scenario's [STRUCTURES] — house, type, strength, cell, facing, trigger.</summary>
    public static void Read_INI(TdGame game, Data.WwProfile ini)
    {
        string buf = "";
        foreach (string key in ini.Entries("STRUCTURES"))
        {
            buf = ini.Get_String("STRUCTURES", key, buf, 128 - 1);
            var tok = new Data.Strtok(buf);
            HousesType bhouse = game.Types.House_From_Name(tok.Next(","));
            StructType classid = game.Types.Building_From_Name(tok.Next(","));

            if (bhouse != HousesType.HOUSE_NONE && classid != StructType.STRUCT_NONE)
            {
                int strength = Data.WwProfile.Atoi(tok.Next(","));
                int cell = (short)Data.WwProfile.Atoi(tok.Next(","));
                byte facing = (byte)Data.WwProfile.Atoi(tok.Next(","));
                string? trigname = tok.Next(",");

                if (game.HouseOrNull(bhouse) != null)
                {
                    var b = Create(game, classid, bhouse);
                    if (b != null)
                    {
                        if (b.Unlimbo(TdMath.CellCoord(cell), facing))
                        {
                            strength = Math.Min(strength, 0x100);
                            strength = (int)TdMath.FixedToCardinal((uint)b.Class.MaxStrength, (uint)strength);
                            b.Strength = strength;
                            b.IsALemon = false;
                            b.Trigger = Scenario.TriggerClass.As_Pointer(game, trigname);
                            if (b.Trigger != null)
                                b.Trigger.AttachCount++;
                        }
                        else
                        {
                            b.Delete();
                        }
                    }
                }
            }
        }
    }

    public override uint Center_Coord() => TdMath.CoordAdd(Coord, CenterOffset[(int)Class.Size]);

    static bool Occupy_List_Contains(short[] list, int cell)
    {
        foreach (short entry in list)
        {
            if (entry == TdMath.RefreshEol)
                break;
            if (entry == cell)
                return true;
        }
        return false;
    }

    static readonly FacingType[] TargetFacings = [FacingType.FACING_S, FacingType.FACING_E, FacingType.FACING_SE];
    static readonly uint[] TargetOffsets = [0x00800000, 0x00000080, 0x00800080];

    /// <summary>Target_Coord: the centre, moved onto the building if the centre cell isn't part of it.</summary>
    public override uint Target_Coord()
    {
        uint offset = CenterOffset[(int)Class.Size];
        short[] list = Occupy_List();
        int cell = TdMath.CoordCell(offset);
        if (!Occupy_List_Contains(list, cell))
        {
            for (int i = 0; i < TargetFacings.Length; i++)
            {
                int adjcell = TdMath.AdjacentCellOf(cell, TargetFacings[i]);
                if (Occupy_List_Contains(list, adjcell))
                {
                    offset = TdMath.CoordAdd(offset, TargetOffsets[i]) & 0xFF80FF80;
                    break;
                }
            }
        }
        return TdMath.CoordAdd(Coord, offset);
    }

    public override uint Docking_Coord()
    {
        if (Class.Type == StructType.STRUCT_HELIPAD)
            return TdMath.CoordAdd(Coord, TdMath.XYP_COORD(24, 18));
        if (Class.Type == StructType.STRUCT_AIRSTRIP)
            return TdMath.CoordAdd(Coord, TdMath.XYP_COORD(18, 30));
        return base.Docking_Coord();
    }

    /// <summary>Can_Fire: turrets must face the target; the Advanced Guard Tower and Obelisk need power (and a charge).</summary>
    public override FireErrorType Can_Fire(uint target, int which = 0)
    {
        FireErrorType canfire = base.Can_Fire(target, which);
        if (canfire == FireErrorType.FIRE_OK)
        {
            if (Class.IsTurretEquipped)
            {
                if (PrimaryFacing.Is_Rotating())
                    return FireErrorType.FIRE_ROTATING;
                int diff = PrimaryFacing.Difference(DirectionToTarget(TarCom));
                if (Math.Abs(diff) > 8)
                    return FireErrorType.FIRE_FACING;
            }

            if (Class.Type == StructType.STRUCT_ATOWER && House.Power_Fraction() < 0x0100)
                return FireErrorType.FIRE_BUSY;

            if (Class.Primary == WeaponType.WEAPON_OBELISK_LASER && !IsCharged)
                return FireErrorType.FIRE_BUSY;
        }
        return canfire;
    }

    /// <summary>Toggle_Primary: make this the factory new units come out of.</summary>
    public bool Toggle_Primary()
    {
        if (IsLeader)
        {
            IsLeader = false;
        }
        else
        {
            for (int index = 0; index < Game.Buildings.Count; index++)
            {
                var building = Game.Buildings.Ptr(index);
                if (!building.IsInLimbo && building.Owner() == Owner() && building.Class.ToBuild == Class.ToBuild)
                    building.IsLeader = false;
            }
            IsLeader = true;
            if (House.IsHuman)
                Game.Speak(VoxType.VOX_PRIMARY_SELECTED, House);
        }
        Mark(MarkType.MARK_CHANGE);
        return IsLeader;
    }

    /// <summary>Captured: an engineer takes the building — power, storage, factories and bib move over.</summary>
    public override bool Captured(HouseClass newowner)
    {
        if (Can_Capture() && newowner != House)
        {
            switch (Owner())
            {
                case HousesType.HOUSE_GOOD:
                    Game.Speak(VoxType.VOX_GDI_CAPTURED);
                    break;
                case HousesType.HOUSE_BAD:
                    Game.Speak(VoxType.VOX_NOD_CAPTURED);
                    break;
            }

            if (House == Game.PlayerPtr)
                Game.Map.Flag_To_Redraw(false);

            if (newowner.IsHuman)
                Game.On_Achievement_Event(newowner, newowner.ActLike != House.ActLike ? "OPPOSING_BUILDING_CAPTURED" : "BUILDING_CAPTURED", Class.IniName);

            if (Game.GameToPlay == GameType.GAME_INTERNET)
                newowner.CapturedBuildings.Increment_Unit_Total((int)Class.Type);

            House.Adjust_Power(-Power_Output());
            LastStrength = 0;
            House.Adjust_Drain(-Class.Drain);
            int booty = House.Adjust_Capacity(-Class.Capacity, true);

            // Whatever is inside or docked comes too (if it's in the docking spot).
            TechnoClass? tech = Cargo.Attached_Object();
            tech?.Captured(newowner);

            tech = Contact_With_Whom();
            if (tech != null)
            {
                if (Transmit_Message(RadioMessageType.RADIO_NEED_TO_MOVE) == RadioMessageType.RADIO_ROGER && TdMath.Distance(tech.Center_Coord(), Docking_Coord()) < 0x0040)
                {
                    tech.Captured(newowner);
                }
                else
                {
                    Transmit_Message(RadioMessageType.RADIO_RUN_AWAY);
                    Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                }
            }

            switch (Class.ToBuild)
            {
                case RTTIType.RTTI_UNITTYPE:
                    House.UnitFactories--;
                    break;
                case RTTIType.RTTI_INFANTRYTYPE:
                    House.InfantryFactories--;
                    break;
                case RTTIType.RTTI_BUILDINGTYPE:
                    House.BuildingFactories--;
                    break;
                case RTTIType.RTTI_AIRCRAFTTYPE:
                    House.AircraftFactories--;
                    break;
            }

            House.IsRecalcNeeded = true;
            newowner.IsRecalcNeeded = true;

            HouseClass oldowner = House;
            IsCaptured = true;
            base.Captured(newowner);
            oldowner.Recalc_Center();
            House.Recalc_Center();

            // The bib changes colour (owner) too.
            int cell = TdMath.CoordCell(Coord);
            if (Class.Bib_And_Offset(Game.Special, out SmudgeType bib, ref cell))
            {
                var smudge = SmudgeClass.Create(Game, bib);
                if (smudge != null)
                {
                    smudge.Disown(cell);
                    smudge.Delete();
                }
                SmudgeClass.Create(Game, bib, TdMath.CellCoord(cell), House.Class.House);
            }

            switch (Class.ToBuild)
            {
                case RTTIType.RTTI_UNITTYPE:
                    House.UnitFactories++;
                    break;
                case RTTIType.RTTI_INFANTRYTYPE:
                    House.InfantryFactories++;
                    break;
                case RTTIType.RTTI_BUILDINGTYPE:
                    House.BuildingFactories++;
                    break;
                case RTTIType.RTTI_AIRCRAFTTYPE:
                    House.AircraftFactories++;
                    break;
            }

            IsRepairing = false;
            Grand_Opening(true);
            House.Harvested(booty);
            Mark(MarkType.MARK_CHANGE);

            if (Game.GameToPlay == GameType.GAME_GLYPHX_MULTIPLAYER && House.IsHuman)
                Look(false);
            else if (House == Game.PlayerPtr)
                Look(false);

            oldowner?.Check_Pertinent_Structures();
            return true;
        }
        return false;
    }

    public override uint Sort_Y()
    {
        if (Class.Type == StructType.STRUCT_REPAIR)
            return Coord;
        if (Class.Type is StructType.STRUCT_HELIPAD or StructType.STRUCT_BARRACKS or StructType.STRUCT_REFINERY)
            return Center_Coord();
        return TdMath.CoordAdd(Center_Coord(), TdMath.XyCoord(0, (Class.Height() * 256) / 3));
    }

    /// <summary>Can_Enter_Cell: whether the building could be placed there (a deployed MCV's yard just needs a clear cell).</summary>
    public override MoveType Can_Enter_Cell(int cell, FacingType facing = FacingType.FACING_NONE)
    {
        if (Class.Type == StructType.STRUCT_CONST && IsDown)
            return Game.Map[cell].Is_Generally_Clear() ? MoveType.MOVE_OK : MoveType.MOVE_NO;
        return Class.Legal_Placement(Game, cell) != 0 ? MoveType.MOVE_OK : MoveType.MOVE_NO;
    }

    public override bool Can_Demolish()
    {
        if (Class.IsUnsellable)
            return false;
        if (Class.HasBuildup && BState != BStateType.BSTATE_CONSTRUCTION && Mission != MissionType.MISSION_DECONSTRUCTION && Mission != MissionType.MISSION_CONSTRUCTION)
        {
            if (Class.Type == StructType.STRUCT_REFINERY && Cargo.Is_Something_Attached())
                return false;
            return true;
        }
        return false;
    }

    public override bool Can_Demolish_Unit() =>
        Class.Type == StructType.STRUCT_REPAIR && In_Radio_Contact() && Distance(Contact_With_Whom()!) < 0x0080;

    /// <summary>Can_Capture: capturable types (and, in campaigns, a computer building whose trigger wins or loses).</summary>
    public override bool Can_Capture()
    {
        bool canCapture = Class.IsCaptureable && Mission != MissionType.MISSION_DECONSTRUCTION;
        if (Game.GameToPlay == GameType.GAME_NORMAL && !House.IsHuman && Trigger != null && Trigger.Action == Scenario.TriggerClass.ActionType.ACTION_WINLOSE)
            canCapture = true;
        return canCapture;
    }

    /// <summary>Mission_Guard: armed buildings look for targets; the repair bay waits for a customer.</summary>
    public override int Mission_Guard()
    {
        if (Class.Primary != WeaponType.WEAPON_NONE)
        {
            IsReadyToCommence = true;
            if (!Game.Target_Legal(TarCom))
                Assign_Target(Greatest_Threat(ThreatType.THREAT_NORMAL));
            if (Game.Target_Legal(TarCom))
            {
                Assign_Mission(MissionType.MISSION_ATTACK);
                return 1;
            }
        }
        else
        {
            const int INITIAL_ENTRY = 0, IDLE = 1;
            switch (Status)
            {
                case INITIAL_ENTRY:
                    Begin_Mode(BStateType.BSTATE_IDLE);
                    Status = IDLE;
                    break;
                case IDLE:
                    if (Class.Type == StructType.STRUCT_REPAIR && In_Radio_Contact() && Contact_With_Whom()!.Is_Techno() &&
                        Contact_With_Whom()!.Mission == MissionType.MISSION_ENTER && Distance(Contact_With_Whom()!) < 0x0040 &&
                        Transmit_Message(RadioMessageType.RADIO_NEED_TO_MOVE) == RadioMessageType.RADIO_ROGER)
                    {
                        Assign_Mission(MissionType.MISSION_REPAIR);
                        return 1;
                    }
                    break;
            }
            return TdDefines.TICKS_PER_SECOND * 5;
        }
        return TdDefines.TICKS_PER_SECOND / 2;
    }

    /// <summary>Mission_Construction: play the build-up, then open for business.</summary>
    public override int Mission_Construction()
    {
        const int INITIAL = 0, DURING = 1;
        switch (Status)
        {
            case INITIAL:
                Begin_Mode(BStateType.BSTATE_CONSTRUCTION);
                Transmit_Message(RadioMessageType.RADIO_BUILDING);
                if (Is_Owned_By_Player())
                    Game.Sound_Effect(VocType.VOC_CONSTRUCTION, Coord);
                Status = DURING;
                break;
            case DURING:
                if (IsReadyToCommence)
                {
                    Transmit_Message(RadioMessageType.RADIO_COMPLETE);
                    Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                    Begin_Mode(BStateType.BSTATE_IDLE);
                    // (A campaign construction yard has no grand opening.)
                    if (Class.Type != StructType.STRUCT_CONST || Game.GameToPlay != GameType.GAME_NORMAL)
                        Grand_Opening();
                    Assign_Mission(MissionType.MISSION_GUARD);
                    PrimaryFacing.Set((byte)Class.StartFace);
                }
                break;
        }
        return 1;
    }

    /// <summary>Mission_Deconstruction: selling — the crew walk out, the building folds up and the money is refunded
    /// (with MCV deploy, a sold yard packs back into an MCV).</summary>
    public override int Mission_Deconstruction()
    {
        Repair(0);

        const int INITIAL = 0, HOLDING = 1, DURING = 2;
        switch (Status)
        {
            case INITIAL:
                // Selling a unit on the repair bay.
                if (Can_Demolish_Unit() && Transmit_Message(RadioMessageType.RADIO_NEED_TO_MOVE) == RadioMessageType.RADIO_ROGER)
                {
                    var tech = Contact_With_Whom()!;
                    Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                    tech.Sell_Back(1);
                    IsReadyToCommence = true;
                    Assign_Mission(MissionType.MISSION_GUARD);
                    return 1;
                }
                IsReadyToCommence = false;
                Transmit_Message(RadioMessageType.RADIO_RUN_AWAY);
                Status = HOLDING;
                break;

            case HOLDING:
                if (!IsTethered)
                {
                    if (!Game.Target_Legal(ArchiveTarget) || !Game.Special.IsMCVDeploy || Class.Type != StructType.STRUCT_CONST)
                    {
                        // One crew member per $200 (half as many from captured buildings), at most one engineer.
                        int divisor = 200;
                        if (IsCaptured)
                            divisor *= 2;
                        int count = (Class.Raw_Cost(Game.Types, Game.Special) + (divisor - 1)) / divisor;
                        bool engine = false;
                        count = Math.Clamp(count, 1, 5);

                        while (count != 0)
                        {
                            InfantryType typ = Crew_Type();
                            while (typ == InfantryType.INFANTRY_E7 && engine)
                                typ = Crew_Type();
                            if (typ == InfantryType.INFANTRY_E7)
                                engine = true;

                            var infantry = InfantryClass.Create(Game, typ, House.Class.House);
                            if (infantry != null)
                            {
                                Game.ScenarioInit++;
                                uint coord = TdMath.CoordAdd(Center_Coord(), TdMath.XYP_COORD(0, -12));
                                coord = Game.Map[TdMath.CoordCell(coord)].Closest_Free_Spot(coord, false);
                                if (infantry.Unlimbo(coord, (byte)DirType.DIR_N))
                                {
                                    if (infantry.Class.IsNominal)
                                        infantry.IsTechnician = true;
                                    Game.ScenarioInit--;
                                    infantry.Scatter(0, true);
                                    Game.ScenarioInit++;
                                    infantry.Assign_Mission(MissionType.MISSION_GUARD_AREA);
                                }
                                else
                                {
                                    infantry.Delete();
                                }
                                Game.ScenarioInit--;
                            }
                            count--;
                        }
                    }

                    Game.Sound_Effect(VocType.VOC_CASHTURN, Coord);

                    while (Cargo.Attached_Object() != null)
                    {
                        FootClass obj = Cargo.Detach_Object()!;
                        Detach_All(true);
                        obj.Delete();
                    }

                    Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                    Status = DURING;
                    Begin_Mode(BStateType.BSTATE_CONSTRUCTION);
                    IsReadyToCommence = false;
                    IsSurvivorless = true;
                    break;
                }
                Transmit_Message(RadioMessageType.RADIO_RUN_AWAY);
                break;

            case DURING:
                if (IsReadyToCommence)
                {
                    if (Game.Target_Legal(ArchiveTarget) && Game.Special.IsMCVDeploy && Class.Type == StructType.STRUCT_CONST && House.IsHuman)
                    {
                        Game.ScenarioInit++;
                        var unit = UnitClass.Create(Game, UnitType.UNIT_MCV, House.Class.House);
                        Game.ScenarioInit--;
                        if (unit != null)
                        {
                            uint ratio = Health_Ratio();
                            int money = Refund_Amount();
                            uint arch = ArchiveTarget;
                            uint place = TdMath.CoordSnap(TdMath.AdjacentCellCoord(Coord, TdMath.DirFacing((byte)DirType.DIR_SE)));
                            HouseClass house = House;
                            Delete_This();
                            if (unit.Unlimbo(place, (byte)DirType.DIR_SW))
                            {
                                unit.Strength = (int)TdMath.FixedToCardinal((uint)unit.Class_Of().MaxStrength, ratio);
                                if (Game.Target_Legal(arch))
                                {
                                    unit.Assign_Destination(arch);
                                    unit.Assign_Mission(MissionType.MISSION_MOVE);
                                }
                            }
                            else
                            {
                                house.Refund_Money(money);
                            }
                        }
                        else
                        {
                            House.Refund_Money(Refund_Amount());
                            Delete_This();
                        }
                    }
                    else
                    {
                        WhoLastHurtMe = HousesType.HOUSE_NONE;
                        Record_The_Kill(null);
                        House.Refund_Money(Refund_Amount());
                        Limbo();
                        House?.Check_Pertinent_Structures();
                        Delete_This();
                    }
                    House.IsRecalcNeeded = true;
                }
                break;
        }
        return 1;
    }

    /// <summary>Mission_Attack: the SAM rises, turns, fires twice, and lowers; other buildings just fire.</summary>
    public override int Mission_Attack()
    {
        if (Class.Type == StructType.STRUCT_SAM)
        {
            bool Target_Gone() => !Game.Target_Legal(TarCom) || !Target.Is_Target_Aircraft(TarCom) || Game.As_Aircraft(TarCom)!.Altitude == 0;

            switch (Status)
            {
                case SAM_UNDERGROUND:
                    IsReadyToCommence = true;
                    if (Game.Target_Legal(TarCom))
                    {
                        Stage.Set_Rate(2);
                        Stage.Set_Stage(0);
                        Status = SAM_RISING;
                        return 1;
                    }
                    Assign_Mission(MissionType.MISSION_GUARD);
                    break;

                case SAM_RISING:
                    if (Stage.Fetch_Stage() == 15)
                    {
                        Stage.Set_Rate(0);
                        PrimaryFacing.Set((byte)DirType.DIR_N);
                        Status = !Game.Target_Legal(TarCom) ? SAM_LOWERING : SAM_READY;
                    }
                    return 1;

                case SAM_READY:
                case SAM_READY2:
                    if (Target_Gone())
                    {
                        Assign_Target(Target.TARGET_NONE);
                        Status = SAM_LOCKING;
                        return TdDefines.TICKS_PER_SECOND;
                    }
                    if (!PrimaryFacing.Is_Rotating())
                    {
                        byte facing = DirectionToTarget(TarCom);
                        if (PrimaryFacing.Difference(facing) != 0)
                            PrimaryFacing.Set_Desired(facing);
                        else
                            Status = Status == SAM_READY ? SAM_FIRING : SAM_FIRING2;
                    }
                    return 1;

                case SAM_FIRING:
                case SAM_FIRING2:
                    if (Target_Gone())
                    {
                        Assign_Target(Target.TARGET_NONE);
                        Status = SAM_LOCKING;
                    }
                    else
                    {
                        FireErrorType error = Can_Fire(TarCom, 0);
                        if (error is FireErrorType.FIRE_ILLEGAL or FireErrorType.FIRE_CANT or FireErrorType.FIRE_RANGE)
                        {
                            Assign_Target(Target.TARGET_NONE);
                            Status = SAM_LOCKING;
                        }
                        else if (error == FireErrorType.FIRE_FACING)
                        {
                            Status = Status == SAM_FIRING ? SAM_READY : SAM_READY2;
                        }
                        else if (error == FireErrorType.FIRE_OK)
                        {
                            Fire_At(TarCom, 0);
                            if (Status == SAM_FIRING)
                            {
                                Status = SAM_READY2;
                                return 1;
                            }
                            Status = SAM_LOCKING;
                            return TdDefines.TICKS_PER_SECOND * 3;
                        }
                    }
                    return 1;

                case SAM_LOCKING:
                    if (!PrimaryFacing.Is_Rotating())
                    {
                        if (PrimaryFacing.Current == (byte)DirType.DIR_N)
                        {
                            Stage.Set_Rate(2);
                            Stage.Set_Stage(48);
                            Status = SAM_LOWERING;
                        }
                        else
                        {
                            PrimaryFacing.Set_Desired((byte)DirType.DIR_N);
                        }
                    }
                    return 1;

                case SAM_LOWERING:
                    if (Stage.Fetch_Stage() >= 63)
                    {
                        Stage.Set_Rate(0);
                        Stage.Set_Stage(0);
                        Status = SAM_UNDERGROUND;
                        return TdDefines.TICKS_PER_SECOND;
                    }
                    if (Stage.Fetch_Rate() == 0)
                        Stage.Set_Rate(2);
                    return 1;
            }
        }
        else
        {
            IsReadyToCommence = true;
            switch (Can_Fire(TarCom, 0))
            {
                case FireErrorType.FIRE_ILLEGAL:
                case FireErrorType.FIRE_CANT:
                case FireErrorType.FIRE_RANGE:
                case FireErrorType.FIRE_AMMO:
                    Assign_Target(Target.TARGET_NONE);
                    Assign_Mission(MissionType.MISSION_GUARD);
                    Commence();
                    break;
                case FireErrorType.FIRE_FACING:
                    PrimaryFacing.Set_Desired(DirectionToTarget(TarCom));
                    return 2;
                case FireErrorType.FIRE_REARM:
                case FireErrorType.FIRE_BUSY:
                    return 1;
                case FireErrorType.FIRE_CLOAKED:
                    Do_Uncloak();
                    break;
                case FireErrorType.FIRE_OK:
                    Fire_At(TarCom, 0);
                    return 1;
            }
        }
        return TdDefines.TICKS_PER_SECOND;
    }

    /// <summary>Mission_Harvest: the refinery unloads a docked harvester a bail at a time.</summary>
    public override int Mission_Harvest()
    {
        const int INITIAL = 0, WAIT_FOR_DOCK = 1, MIDDLE = 2, WAIT_FOR_UNDOCK = 3;
        switch (Status)
        {
            case INITIAL:
                Begin_Mode(BStateType.BSTATE_ACTIVE);
                Status = WAIT_FOR_DOCK;
                break;

            case WAIT_FOR_DOCK:
                if (IsReadyToCommence)
                {
                    IsReadyToCommence = false;
                    Status = MIDDLE;
                    Begin_Mode(BStateType.BSTATE_AUX1);
                }
                break;

            case MIDDLE:
                if (IsReadyToCommence)
                {
                    IsReadyToCommence = false;

                    // Clear the exit cell.
                    bool old = Game.Special.IsScatter;
                    Game.Special.IsScatter = true;
                    Game.Map[TdMath.AdjacentCellOf(TdMath.CoordCell(Center_Coord()), TdMath.DirFacingType((byte)DirType.DIR_SW))].Incoming(0, true);
                    Game.Special.IsScatter = old;

                    FootClass? techno = Cargo.Attached_Object();
                    if (techno != null)
                    {
                        int bail = techno.Offload_Tiberium_Bail();
                        if (bail != 0)
                        {
                            House.Harvested(bail);
                            if (techno.Tiberium_Load() != 0)
                                return 1;
                        }
                    }
                    Begin_Mode(BStateType.BSTATE_AUX2);
                    Status = WAIT_FOR_UNDOCK;
                }
                break;

            case WAIT_FOR_UNDOCK:
                if (IsReadyToCommence)
                {
                    Exit_Object(Cargo.Detach_Object());
                    Assign_Mission(MissionType.MISSION_GUARD);
                }
                break;
        }
        return 1;
    }

    /// <summary>Mission_Repair: the yard's crane while building; the repair bay mends the docked unit;
    /// the helipad reloads its helicopter (faster with more power).</summary>
    public override int Mission_Repair()
    {
        if (Class.Type == StructType.STRUCT_CONST)
        {
            const int INITIAL = 0, DURING = 1;
            switch (Status)
            {
                case INITIAL:
                    Begin_Mode(BStateType.BSTATE_ACTIVE);
                    Status = DURING;
                    break;
                case DURING:
                    if (!In_Radio_Contact())
                        Assign_Mission(MissionType.MISSION_GUARD);
                    break;
            }
            return 1;
        }

        if (Class.Type == StructType.STRUCT_REPAIR)
        {
            const int INITIAL = 0, IDLE = 1, DURING = 2;
            switch (Status)
            {
                case INITIAL:
                    if (!In_Radio_Contact())
                    {
                        Begin_Mode(BStateType.BSTATE_IDLE);
                        Assign_Mission(MissionType.MISSION_GUARD);
                        return 1;
                    }
                    IsReadyToCommence = false;
                    if (Transmit_Message(RadioMessageType.RADIO_NEED_TO_MOVE) == RadioMessageType.RADIO_ROGER && Distance(Contact_With_Whom()!) < 0x0010)
                    {
                        Status = IDLE;
                        return TdDefines.TICKS_PER_SECOND / 4;
                    }
                    break;

                case IDLE:
                    if (!In_Radio_Contact())
                    {
                        Assign_Mission(MissionType.MISSION_GUARD);
                        return 1;
                    }
                    if (Transmit_Message(RadioMessageType.RADIO_NEED_TO_MOVE) == RadioMessageType.RADIO_ROGER)
                    {
                        if (Contact_With_Whom()!.Health_Ratio() < 0x0100 && Transmit_Message(RadioMessageType.RADIO_REPAIR) == RadioMessageType.RADIO_ROGER)
                        {
                            if (IsOwnedByPlayer && House != null)
                                Game.Speak(VoxType.VOX_REPAIRING, House);
                            Status = DURING;
                            Begin_Mode(BStateType.BSTATE_ACTIVE);
                            IsReadyToCommence = false;
                        }
                        else if (!House.IsHuman)
                        {
                            Transmit_Message(RadioMessageType.RADIO_RUN_AWAY);
                        }
                    }
                    break;

                case DURING:
                    if (!In_Radio_Contact())
                    {
                        Begin_Mode(BStateType.BSTATE_IDLE);
                        Status = IDLE;
                        return 1;
                    }
                    if (IsReadyToCommence && Transmit_Message(RadioMessageType.RADIO_NEED_TO_MOVE) == RadioMessageType.RADIO_ROGER)
                    {
                        IsReadyToCommence = false;
                        int param = (int)Health_Ratio();
                        if (Transmit_Message(RadioMessageType.RADIO_REPAIR, ref param) != RadioMessageType.RADIO_ROGER)
                        {
                            Begin_Mode(BStateType.BSTATE_IDLE);
                            Status = IDLE;
                        }
                    }
                    break;
            }
            return TdDefines.TICKS_PER_SECOND / 2;
        }

        if (Class.Type == StructType.STRUCT_HELIPAD)
        {
            const int INITIAL = 0, DURING = 1;
            switch (Status)
            {
                case INITIAL:
                    if (Transmit_Message(RadioMessageType.RADIO_NEED_TO_MOVE) == RadioMessageType.RADIO_ROGER && Transmit_Message(RadioMessageType.RADIO_PREPARED) == RadioMessageType.RADIO_NEGATIVE)
                    {
                        Begin_Mode(BStateType.BSTATE_ACTIVE);
                        Contact_With_Whom()!.Assign_Mission(MissionType.MISSION_SLEEP);
                        Status = DURING;
                        return 1;
                    }
                    Assign_Mission(MissionType.MISSION_GUARD);
                    break;

                case DURING:
                    if (IsReadyToCommence)
                    {
                        if (!In_Radio_Contact() || Transmit_Message(RadioMessageType.RADIO_NEED_TO_MOVE) == RadioMessageType.RADIO_NEGATIVE)
                        {
                            Assign_Mission(MissionType.MISSION_GUARD);
                            return 1;
                        }
                        if (Transmit_Message(RadioMessageType.RADIO_PREPARED) == RadioMessageType.RADIO_ROGER)
                        {
                            Contact_With_Whom()!.Assign_Mission(MissionType.MISSION_GUARD);
                            Assign_Mission(MissionType.MISSION_GUARD);
                            return 1;
                        }
                        if (Transmit_Message(RadioMessageType.RADIO_RELOAD) != RadioMessageType.RADIO_ROGER)
                        {
                            Assign_Mission(MissionType.MISSION_GUARD);
                            Contact_With_Whom()!.Assign_Mission(MissionType.MISSION_GUARD);
                            return 1;
                        }
                        int time = Math.Clamp((int)TdMath.FixedToCardinal(TdDefines.TICKS_PER_SECOND, (uint)House.Power_Fraction()), 0, TdDefines.TICKS_PER_SECOND);
                        time = (TdDefines.TICKS_PER_SECOND * 3) - time;
                        IsReadyToCommence = false;
                        return time;
                    }
                    break;
            }
            return 3;
        }
        return TdDefines.TICKS_PER_SECOND;
    }

    /// <summary>Mission_Missile: the Temple of Nod opens, launches the nuke up, and drops it on NukeDest.</summary>
    public override int Mission_Missile()
    {
        const int INITIAL = 0, DOOR_OPENING = 1, LAUNCH_UP = 2, LAUNCH_DOWN = 3, DONE_LAUNCH = 4;

        if (Class.Type == StructType.STRUCT_TEMPLE)
        {
            switch (Status)
            {
                case INITIAL:
                    IsReadyToCommence = false;
                    Begin_Mode(BStateType.BSTATE_ACTIVE);
                    Status = DOOR_OPENING;
                    return 1;

                case DOOR_OPENING:
                    if (IsReadyToCommence)
                    {
                        Begin_Mode(BStateType.BSTATE_IDLE);
                        AnimClass.Create(Game, AnimType.ANIM_ATOM_DOOR, Center_Coord());
                        Status = LAUNCH_UP;
                        return 14;
                    }
                    return 1;

                case LAUNCH_UP:
                {
                    var bullet = BulletClass.Create(Game, BulletType.BULLET_NUKE_UP);
                    if (bullet != null)
                    {
                        uint launch = TdMath.CoordMove(Center_Coord(), 1, 0x1A0);
                        bullet.Assign_Target(Target.TARGET_NONE);
                        bullet.Payback = null;
                        bullet.Strength = 1;
                        if (!bullet.Unlimbo(launch, (byte)DirType.DIR_N))
                        {
                            bullet.Delete();
                            bullet = null;
                        }
                        else
                        {
                            bullet.PrimaryFacing.Set_Current((byte)DirType.DIR_N);
                            Game.Sound_Effect(VocType.VOC_NUKE_FIRE, launch);
                            Game.Speak(VoxType.VOX_NUKE_LAUNCHED);
                        }
                    }
                    if (bullet != null)
                    {
                        Status = LAUNCH_DOWN;
                        return 8 * TdDefines.TICKS_PER_SECOND;
                    }
                    return 1;
                }

                case LAUNCH_DOWN:
                {
                    var bullet = BulletClass.Create(Game, BulletType.BULLET_NUKE_DOWN);
                    if (bullet != null)
                    {
                        uint start = TdMath.CellCoord(TdMath.XyCell(TdMath.CellX(House.NukeDest), 1));
                        bullet.Assign_Target(Target.As_Target(House.NukeDest));
                        bullet.Payback = this;
                        bullet.Strength = 1;
                        if (!bullet.Unlimbo(start, (byte)DirType.DIR_S))
                            bullet.Delete();
                        else
                            bullet.PrimaryFacing.Set_Current((byte)DirType.DIR_S);
                        Game.Speak(VoxType.VOX_INCOMING_NUKE);
                        Game.Sound_Effect(VocType.VOC_NUKE_FIRE, start);
                    }
                    // As the original: the (possibly deleted) bullet still counts as launched.
                    if (bullet != null)
                    {
                        Status = DONE_LAUNCH;
                        return 7 * TdDefines.TICKS_PER_SECOND;
                    }
                    return 1;
                }

                case DONE_LAUNCH:
                    Assign_Mission(MissionType.MISSION_GUARD);
                    return 60;
            }
        }
        return 60;
    }

    /// <summary>Revealed: a newly seen building opens (in campaigns) and the sidebar is redone.</summary>
    public override bool Revealed(HouseClass? house)
    {
        if (base.Revealed(house))
        {
            if (Game.ScenarioInit == 0)
                House.JustBuilt = Class.Type;
            House.IsRecalcNeeded = true;
            if (!In_Radio_Contact() && (house == House && Game.GameToPlay == GameType.GAME_NORMAL) && Mission != MissionType.MISSION_CONSTRUCTION)
                Grand_Opening();
            return true;
        }
        return false;
    }

    /// <summary>Enter_Idle_Mode: a newly placed building (not at scenario start) builds up first.</summary>
    public override void Enter_Idle_Mode(bool initial = false)
    {
        MissionType mission;
        if (!initial || Game.ScenarioInit != 0 || Game.DebugMap)
        {
            Begin_Mode(BStateType.BSTATE_IDLE);
            mission = MissionType.MISSION_GUARD;
        }
        else
        {
            Begin_Mode(BStateType.BSTATE_CONSTRUCTION);
            mission = MissionType.MISSION_CONSTRUCTION;
        }
        Assign_Mission(mission);
    }

    public void Update_Specials() { }

    public override int Pip_Count() => (int)TdMath.FixedToCardinal((uint)Class.Max_Pips(), (uint)House.Tiberium_Fraction());

    /// <summary>Death_Announcement: "structure lost" (or the side's line with death announcements on).</summary>
    public override void Death_Announcement(TechnoClass? source = null)
    {
        if (Is_Discovered_By_Player() || Is_Owned_By_Player())
        {
            if (House != Game.PlayerPtr && Game.GameToPlay != GameType.GAME_NORMAL)
            {
                if (Game.Options.IsDeathAnnounce)
                    Game.Speak(VoxType.VOX_ENEMY_STRUCTURE);
            }
            else if (House == Game.PlayerPtr || Game.Options.IsDeathAnnounce)
            {
                if (!Game.Options.IsDeathAnnounce)
                {
                    Game.Speak(VoxType.VOX_STRUCTURE_LOST);
                }
                else
                {
                    switch (House.ActLike)
                    {
                        case HousesType.HOUSE_GOOD:
                            Game.Speak(VoxType.VOX_GDI_STRUCTURE);
                            break;
                        case HousesType.HOUSE_BAD:
                            Game.Speak(VoxType.VOX_NOD_STRUCTURE);
                            break;
                    }
                }
            }
        }
    }

    public override byte Fire_Direction() => Class.IsTurretEquipped ? PrimaryFacing.Current : DirectionToTarget(TarCom);

    /// <summary>Mission_Unload: the weapons factory clears its bib, opens the door and lets the vehicle out.</summary>
    public override int Mission_Unload()
    {
        if (Class.Type == StructType.STRUCT_WEAP)
        {
            uint coord = TdMath.AdjacentCellCoord(Center_Coord(), TdMath.Facing.SW);
            int cell = TdMath.CoordCell(coord);
            var cellptr = Game.Map[cell];
            const int INITIAL = 0, CLEAR_BIB = 1, OPEN = 2, LEAVE = 3, CLOSE = 4;
            UnitClass? unit;

            switch (Status)
            {
                case INITIAL:
                    unit = Contact_With_Whom() as UnitClass;
                    if (unit != null)
                    {
                        unit.Assign_Mission(MissionType.MISSION_GUARD);
                        unit.Commence();
                    }
                    Door.Open_Door(2, 11);
                    Status = CLEAR_BIB;
                    break;

                case CLEAR_BIB:
                    unit = Contact_With_Whom() as UnitClass;
                    if (cellptr.Cell_Unit() != null || cellptr.Cell_Infantry() != null)
                    {
                        cellptr.Incoming(0, true, true);
                        for (int f = (int)FacingType.FACING_FIRST; f < (int)FacingType.FACING_COUNT; f++)
                        {
                            var cptr = cellptr.Adjacent_Cell((FacingType)f);
                            if (cptr == null)
                                continue;
                            var cellunit = cptr.Cell_Unit();
                            if ((cellunit != null && cellunit != unit) || cptr.Cell_Infantry() != null)
                                cptr.Incoming(coord, true, true);
                        }
                    }
                    else
                    {
                        Status = OPEN;
                    }
                    break;

                case OPEN:
                    if (Door.Is_Door_Open())
                    {
                        unit = Contact_With_Whom() as UnitClass;
                        if (unit != null)
                        {
                            unit.Assign_Mission(MissionType.MISSION_MOVE);
                            unit.Force_Track(DriveClass.OUT_OF_WEAPON_FACTORY, TdMath.AdjacentCellCoord(Center_Coord(), TdMath.Facing.SW));
                            unit.Set_Speed(128);
                            Status = LEAVE;
                        }
                        else
                        {
                            Door.Close_Door(2, 11);
                            Status = CLOSE;
                        }
                    }
                    break;

                case LEAVE:
                    if (!IsTethered)
                    {
                        Door.Close_Door(2, 11);
                        Status = CLOSE;
                    }
                    break;

                case CLOSE:
                    if (Door.Is_Door_Closed())
                        Enter_Idle_Mode();
                    break;
            }
            return TdDefines.TICKS_PER_SECOND / 2;
        }

        Assign_Mission(MissionType.MISSION_GUARD);
        return TdDefines.TICKS_PER_SECOND;
    }

    /// <summary>Power_Output: the building's power, scaled by its health.</summary>
    public int Power_Output()
    {
        if (Class.Power != 0)
            return (int)TdMath.FixedToCardinal((uint)Class.Power, TdMath.CardinalToFixed((uint)Class.MaxStrength, (uint)LastStrength));
        return 0;
    }

    public override void Detach(uint target, bool all)
    {
        base.Detach(target, all);
        if (target == WhomToRepay)
            WhomToRepay = Target.TARGET_NONE;
    }

    /// <summary>Refund_Amount: (in 1.07 multiplayer, a silo's share of the stored Tiberium too).</summary>
    public override int Refund_Amount()
    {
        int cost = base.Refund_Amount();
        if (Game.IsV107 && Class.Capacity > 0)
            cost += (int)TdMath.FixedToCardinal((uint)Class.Capacity, TdMath.CardinalToFixed((uint)House.Capacity, (uint)House.Tiberium));
        return cost;
    }

    public override InfantryType Crew_Type()
    {
        switch (Class.Type)
        {
            case StructType.STRUCT_STORAGE:
                return Game.Random_Pick(0, 1) == 0 ? InfantryType.INFANTRY_C1 : InfantryType.INFANTRY_C7;
            case StructType.STRUCT_CONST:
                if (!IsCaptured && House.IsHuman && Game.Random_Pick(0, 3) == 0)
                    return InfantryType.INFANTRY_E7;
                break;
        }
        return base.Crew_Type();
    }

    /// <summary>Detach_All: production stops if no other factory can carry on.</summary>
    public override void Detach_All(bool all = true)
    {
        if (Factory != null)
        {
            Factory.Abandon();
            Factory.Delete();
            Factory = null;
        }

        if (House != null)
        {
            int fnum = Class.ToBuild switch
            {
                RTTIType.RTTI_AIRCRAFTTYPE => House.AircraftFactory,
                RTTIType.RTTI_INFANTRYTYPE => House.InfantryFactory,
                RTTIType.RTTI_UNITTYPE => House.UnitFactory,
                RTTIType.RTTI_BUILDINGTYPE => House.BuildingFactory,
                RTTIType.RTTI_SPECIAL => House.SpecialFactory,
                _ => -1,
            };

            FactoryClass? factory = fnum != -1 ? Game.Factories.RawPtr(fnum) : null;
            if (factory != null)
            {
                var obj = factory.Get_Object();
                IsInLimbo = true;
                if (obj != null && Game.Who_Can_Build_Me(obj.Techno_Type_Class(), true, false, House.Class.House) == null)
                    House.Abandon_Production(Class.ToBuild);
                IsInLimbo = false;
            }
        }
        base.Detach_All(all);
    }

    /// <summary>Flush_For_Placement: anything in the way of a new building is told to move (enemies are attacked); true if so.</summary>
    public bool Flush_For_Placement(TechnoClass? techno, int cell)
    {
        bool again = false;
        if (techno != null && cell > 0)
        {
            foreach (short offset in techno.Occupy_List(true))
            {
                if (offset == TdMath.RefreshEol)
                    break;
                int newcell = cell + offset;
                if (Game.Map.In_Radar(newcell))
                {
                    var occupier = Game.Map[newcell].Cell_Techno();
                    if (occupier != null)
                    {
                        again = true;
                        if (occupier.House.Is_Ally(this))
                            Game.Map[newcell].Incoming(0, true);
                        else
                            Base_Is_Attacked(occupier);
                    }
                }
            }
        }
        return again;
    }

    public override void Hidden() => base.Hidden();

    /// <summary>Find_Exit_Cell: the first exit cell (from the type's list) the object can move into, or 0.</summary>
    public override int Find_Exit_Cell(TechnoClass? techno)
    {
        int origin = TdMath.CoordCell(Coord);
        foreach (short offset in Class.ExitList)
        {
            if (offset == TdMath.RefreshEol)
                break;
            int cell = origin + offset;
            if (Game.Map.In_Radar(cell) && techno!.Can_Enter_Cell(cell) == MoveType.MOVE_OK)
                return cell;
        }
        return 0;
    }

    public override bool Can_Player_Move() => Class.Type == StructType.STRUCT_CONST && Mission == MissionType.MISSION_GUARD && Game.Special.IsMCVDeploy;

    /// <summary>Passes_Proximity_Check: a human's new building must touch the house's buildings or bibs.</summary>
    public bool Passes_Proximity_Check(int homecell)
    {
        if (Game.DebugMap || !House.IsHuman)
            return true;

        foreach (short offset in Occupy_List(true))
        {
            if (offset == TdMath.RefreshEol)
                break;
            int cell = homecell + offset;
            if (!Game.Map.In_Radar(cell))
                return false;
            for (int facing = 0; facing < (int)FacingType.FACING_COUNT; facing++)
            {
                int newcell = TdMath.AdjacentCellOf(cell, (FacingType)facing);
                var nearby = Game.Map[newcell].Cell_Building();
                if (Game.Map[newcell].Owner == House.Class.House)
                    return true;
                if (nearby != null && nearby.House.Class.House == House.Class.House)
                    return true;
            }
        }
        return false;
    }
}

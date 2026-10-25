using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// InfantryClass (INFANTRY.CPP): foot soldiers and civilians. Five spots per cell, fear (going
/// prone under fire), the animation actions (DoType), capturing and sabotaging buildings,
/// entering transports, Tiberium poisoning.
/// </summary>
public sealed class InfantryClass : FootClass
{
    public const int FEAR_ANXIOUS = 10, FEAR_SCARED = 100, FEAR_PANIC = 200, FEAR_MAXIMUM = 255;

    /// <summary>HumanShape: 32-step facing to the 8 infantry facings.</summary>
    public static readonly int[] HumanShape = [0, 0, 7, 7, 7, 7, 6, 6, 6, 6, 5, 5, 5, 5, 5, 4, 4, 4, 3, 3, 3, 3, 2, 2, 2, 2, 1, 1, 1, 1, 1, 0];

    /// <summary>DoStruct: per action — can it be interrupted, does it move, random start, frame rate.</summary>
    public readonly record struct DoStruct(bool Interrupt, bool IsMobile, bool RandomStart, int Rate);

    static readonly DoStruct[] MasterDoControls =
    [
        new(true, false, false, 0), new(true, false, false, 0), new(true, false, false, 0), new(true, true, true, 2),
        new(true, false, false, 1), new(false, true, false, 2), new(true, true, true, 2), new(false, false, false, 3),
        new(true, false, false, 1), new(true, false, false, 2), new(true, false, false, 2), new(false, false, false, 2),
        new(true, false, false, 2), new(false, false, false, 2), new(false, false, false, 2), new(false, false, false, 2),
        new(false, false, false, 2), new(false, false, false, 1), new(false, false, false, 2), new(false, false, false, 2),
        new(false, false, false, 1), new(false, false, false, 2), new(false, false, false, 2), new(false, false, false, 2),
        new(false, false, false, 2), new(false, false, false, 2), new(false, false, false, 2), new(false, false, false, 2),
        new(false, false, false, 2), new(false, false, false, 2), new(false, false, false, 2), new(true, false, false, 2),
        new(true, false, false, 2), new(true, false, false, 2),
    ];

    static readonly int[] InfantryKickDamage = [10, 15];
    static readonly int[] InfantryPunchDamage = [4, 7];

    public readonly InfantryTypeClass Class;
    public DoType Doing;
    public readonly CountDownTimer Comment;
    public bool IsTechnician, IsStoked, IsProne, IsBoxing;
    public long StopDriverFrame;
    public byte Fear;

    InfantryClass(TdGame game, InfantryType classid, HousesType house) : base(game, house)
    {
        Class = game.Types.Infantry(classid);
        Comment = new CountDownTimer(game);
        IsSecondShot = !Class.IsTwoShooter;
        Doing = DoType.DO_NOTHING;
        Fear = 0;
        IsProne = false;
        IsStoked = false;
        IsBoxing = false;
        IsTechnician = false;
        Strength = Class.MaxStrength;
        Ammo = Class.MaxAmmo;
        if (!Class.IsCivilian && game.GameToPlay == GameType.GAME_INTERNET)
            House.InfantryTotals.Increment_Unit_Total((int)classid);
        House.Tracking_Add(this);
        StopDriverFrame = -1;
    }

    /// <summary>new InfantryClass: the first free slot of the infantry heap (null when full).</summary>
    public static InfantryClass? Create(TdGame game, InfantryType classid, HousesType house)
    {
        int slot = game.Infantry.Allocate();
        if (slot < 0)
            return null;
        var infantry = new InfantryClass(game, classid, house);
        game.Infantry.Place(slot, infantry);
        infantry.Set_Active();
        return infantry;
    }

    protected override void Destruct()
    {
        if (Game.GameActive)
        {
            House.Tracking_Remove(this);
            Limbo();
        }
        if (Game.GameActive && Team != null)
            Team.Remove(this);
        base.Destruct();
    }

    protected override void Release()
    {
        IsActive = false;
        Game.Infantry.Free(this);
    }

    public override RTTIType What_Am_I() => RTTIType.RTTI_INFANTRY;
    public override ObjectTypeClass Class_Of() => Class;
    public override bool Is_Infantry() => true;
    public override uint As_Target() => Target.Build_Target(KindType.KIND_INFANTRY, Game.Infantry.ID(this));

    /// <summary>Take_Damage: prone soldiers take half; deaths play the warhead's death and some explode.</summary>
    public override ResultType Take_Damage(ref int damage, int distance, WarheadType warhead, TechnoClass? source = null)
    {
        IsFiring = false;

        if (IsProne && damage != 0)
            damage >>= 1;

        ResultType res = base.Take_Damage(ref damage, distance, warhead, source);

        // The flamethrower's fuel tank: a hit does 5 more.
        if (damage != 0 && res != ResultType.RESULT_DESTROYED && Class.Type == InfantryType.INFANTRY_E4)
        {
            damage = 5;
            ResultType newres = base.Take_Damage(ref damage, distance, warhead, source);
            res = (ResultType)Math.Max((int)res, (int)newres);
        }

        if (res == ResultType.RESULT_NONE)
            return res;

        if (res == ResultType.RESULT_DESTROYED)
        {
            Death_Announcement(source);
            Stop_Driver();
            Stun();
            Mission = MissionType.MISSION_NONE;
            Assign_Mission(MissionType.MISSION_GUARD);
            Commence();

            if (Class.Type == InfantryType.INFANTRY_E4)
            {
                AnimClass.Create(Game, AnimType.ANIM_NAPALM1, Coord);
                Game.Explosion_Damage(Coord, 80, null, WarheadType.WARHEAD_FIRE);
            }
            if (Class.Type == InfantryType.INFANTRY_E2)
            {
                AnimClass.Create(Game, AnimType.ANIM_ART_EXP1, Coord);
                Game.Explosion_Damage(Coord, 30, null, WarheadType.WARHEAD_HE);
            }
            if (Class.Type == InfantryType.INFANTRY_E5)
            {
                AnimClass.Create(Game, AnimType.ANIM_CHEM_BALL, Coord);
                Game.Explosion_Damage(Coord, 80, null, WarheadType.WARHEAD_HE);
            }

            VocType sound, altsound;
            if (Class.Type == InfantryType.INFANTRY_RAMBO)
            {
                sound = VocType.VOC_RAMBO_YELL;
                altsound = sound;
            }
            else
            {
                sound = (VocType)Game.Sim_Random_Pick((int)VocType.VOC_SCREAM1, (int)VocType.VOC_SCREAM5);
                altsound = VocType.VOC_YELL1;
            }

            switch (warhead)
            {
                case WarheadType.WARHEAD_FEEDME:
                    // The dinosaur's bite heals it.
                    if (source != null)
                    {
                        source.Strength += 30;
                        if (source.Strength > source.Class_Of().MaxStrength)
                            source.Strength = source.Class_Of().MaxStrength;
                    }
                    goto case WarheadType.WARHEAD_SA;
                case WarheadType.WARHEAD_HEADBUTT:
                case WarheadType.WARHEAD_SPORE:
                case WarheadType.WARHEAD_HOLLOW_POINT:
                case WarheadType.WARHEAD_SA:
                    Game.Sound_Effect(sound, Coord);
                    Do_Action(DoType.DO_GUN_DEATH, true);
                    break;
                case WarheadType.WARHEAD_HE:
                    Game.Sound_Effect(sound, Coord);
                    Do_Action(DoType.DO_EXPLOSION_DEATH, true);
                    break;
                case WarheadType.WARHEAD_AP:
                    Game.Sound_Effect(sound, Coord);
                    Do_Action(DoType.DO_GRENADE_DEATH, true);
                    break;
                case WarheadType.WARHEAD_PB:
                case WarheadType.WARHEAD_LASER:
                case WarheadType.WARHEAD_FIRE:
                    Game.Sound_Effect(altsound, Coord);
                    Do_Action(DoType.DO_FIRE_DEATH, true);
                    break;
                case WarheadType.WARHEAD_FIST:
                    Game.Sound_Effect(sound, Coord);
                    Do_Action(DoType.DO_PUNCH_DEATH, true);
                    break;
                case WarheadType.WARHEAD_FOOT:
                    Game.Sound_Effect(sound, Coord);
                    Do_Action(DoType.DO_KICK_DEATH, true);
                    break;
            }
            return res;
        }

        // Hurt: scatter away from the attacker, and become afraid.
        uint c4 = source != null ? source.Coord : 0;
        if (source != null)
            Scatter(c4);
        if (source != null && Fear < FEAR_SCARED)
        {
            Fear = Class.IsFraidyCat ? (byte)FEAR_PANIC : (byte)FEAR_SCARED;
        }
        else
        {
            int morefear = FEAR_ANXIOUS;
            if (Health_Ratio() > 0x0080)
                morefear /= 4;
            Fear = (byte)Math.Min(Fear + morefear, FEAR_MAXIMUM);
        }
        return res;
    }

    /// <summary>The frame to draw (Draw_It's shape number): the action's frames for the facing.</summary>
    public int Shape_Number(bool virtualWindow)
    {
        int facing = TdMath.Facing32[PrimaryFacing.Current];
        int facenum = HumanShape[facing];
        DoType doit = Doing;
        if (doit == DoType.DO_NOTHING)
            doit = DoType.DO_STAND_READY;

        // The virtual (GlyphX) window shows walking just after a stop if more path is left.
        if (doit == DoType.DO_STAND_READY && virtualWindow && StopDriverFrame != -1 && Game.Frame - StopDriverFrame <= 2 && Path[0] != FacingType.FACING_NONE)
            doit = DoType.DO_WALK;

        var control = Class.DoControl(doit);
        int shapenum = Stage.Fetch_Stage() % Math.Max(control.Count, 1);
        if (control.Jump != 0)
            shapenum += facenum * control.Jump;
        shapenum += control.Frame;
        return shapenum;
    }

    /// <summary>Per_Cell_Process: capturing, entering, sabotaging, unloading; Tiberium hurts.</summary>
    public override void Per_Cell_Process(bool center)
    {
        var cellptr = Game.Map[TdMath.CoordCell(Coord)];

        // An engineer reaching the building to capture.
        if (center && Mission == MissionType.MISSION_CAPTURE)
        {
            TechnoClass? tech = cellptr.Cell_Techno();
            if (tech != null && tech.As_Target() == NavCom && tech.What_Am_I() == RTTIType.RTTI_BUILDING && !tech.Can_Capture())
            {
                tech = null;
                Assign_Destination(Target.TARGET_NONE);
            }

            if (tech != null && tech.As_Target() == NavCom)
            {
                tech.Captured(House);
                Delete_This();
                return;
            }
            if (!Game.Target_Legal(NavCom))
            {
                Enter_Idle_Mode();
                if (Game.Map[TdMath.CoordCell(Coord)].Cell_Building() != null)
                    Scatter(0, true);
            }
        }

        // Boarding the transport we're talking to.
        var techno = Contact_With_Whom();
        if (center && Mission == MissionType.MISSION_ENTER && techno != null && TdMath.CoordCell(Coord) == TdMath.CoordCell(techno.Coord) && techno == Game.As_Techno(NavCom))
        {
            if (Transmit_Message(RadioMessageType.RADIO_IM_IN) == RadioMessageType.RADIO_ATTACH)
            {
                Limbo();
                techno.Cargo.Attach(this);
            }
            return;
        }

        // Commando planting C4.
        if (center && Mission == MissionType.MISSION_SABOTAGE)
        {
            var building = cellptr.Cell_Building();
            if (building != null && building.As_Target() == NavCom)
            {
                bool temp = Game.Special.IsScatter;
                building.IsGoingToBlow = true;
                building.Clicked_As_Target(Game.PlayerPtr.Class.House, 20);
                building.Clicked_As_Target(building.Owner(), 20);
                building.CountDown.Set(20);
                building.WhomToRepay = As_Target();
                Game.Special.IsScatter = true;
                NavCom = Target.TARGET_NONE;
                Do_Uncloak();
                Arm = Rearm_Delay(true);
                Scatter(building.Center_Coord(), true);
                Game.Special.IsScatter = temp;
                return;
            }
        }

        // Out of a transport: a gesture (and Rambo's quip).
        if (center && IsTethered)
        {
            Transmit_Message(RadioMessageType.RADIO_UNLOADED);
            Do_Action(House.Class.House == HousesType.HOUSE_GOOD ? DoType.DO_GESTURE1 : DoType.DO_GESTURE2);
            if (Class.Type == InfantryType.INFANTRY_RAMBO)
                Game.Sound_Effect(VocType.VOC_RAMBO_ROCK, Coord);
            if ((cellptr.Flag.Composite & 0x01F) == 0x01F)
                cellptr.Incoming(0, true);
        }

        if (center)
            Commence();

        Look(true);

        base.Per_Cell_Process(center);

        // Tiberium is poisonous (except to the chem warrior).
        if (IsActive && !IsInLimbo && center && cellptr.Land_Type() == LandType.LAND_TIBERIUM && Class.Type != InfantryType.INFANTRY_E5)
        {
            int damage = 2;
            Take_Damage(ref damage, 0, WarheadType.WARHEAD_FIRE);
        }
    }

    public override void Detach(uint target, bool all)
    {
        if (TarCom == target)
            IsFiring = false;
        base.Detach(target, all);
    }

    public override void Look(bool incremental = false)
    {
        if (!IsInLimbo && (House.IsHuman || Game.GameToPlay != GameType.GAME_NORMAL))
        {
            int sight = Class.SightRange;
            if (sight != 0)
                Game.Map.Sight_From(House, TdMath.CoordCell(Coord), sight, incremental);
        }
    }

    /// <summary>Assign_Destination: entering a transport starts the radio talk with it.</summary>
    public override void Assign_Destination(uint target)
    {
        if (Game.Target_Legal(target))
            Stop_Driver();

        // Ordered on again: a human's prone soldier gets up.
        if (House.IsHuman && Game.Target_Legal(target) && NavCom == target && IsProne && !Class.IsFraidyCat)
            Do_Action(DoType.DO_GET_UP);

        if (Mission == MissionType.MISSION_ENTER || MissionQueue == MissionType.MISSION_ENTER)
        {
            if (!In_Radio_Contact())
            {
                var techno = Game.As_Techno(target);
                if (techno != null)
                {
                    if (techno.In_Radio_Contact())
                    {
                        ArchiveTarget = target;
                    }
                    else if (Transmit_Message(RadioMessageType.RADIO_HELLO, techno) == RadioMessageType.RADIO_ROGER)
                    {
                        if (Transmit_Message(RadioMessageType.RADIO_DOCKING) != RadioMessageType.RADIO_ROGER)
                        {
                            Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                            Assign_Mission(MissionType.MISSION_MOVE);
                        }
                    }
                    else
                    {
                        Assign_Mission(MissionType.MISSION_MOVE);
                    }
                }
            }
            else
            {
                Path[0] = FacingType.FACING_NONE;
            }
        }
        else
        {
            Path[0] = FacingType.FACING_NONE;
        }
        base.Assign_Destination(target);
    }

    /// <summary>Assign_Target: an unarmed capturer (engineer) heads for a capturable building.</summary>
    public override void Assign_Target(uint target)
    {
        Path[0] = FacingType.FACING_NONE;
        base.Assign_Target(target);
        if (!Game.Target_Legal(NavCom) && Class.IsCapture && Class.Primary == WeaponType.WEAPON_NONE)
        {
            var building = Game.As_Building(target);
            if (building != null && building.Can_Capture())
                Assign_Destination(target);
        }
    }

    static readonly VocType[] RamboStoked = [VocType.VOC_RAMBO_LEFTY, VocType.VOC_RAMBO_LAUGH, VocType.VOC_RAMBO_COMIN, VocType.VOC_RAMBO_TUFF];

    public override void AI()
    {
        base.AI();

        if (IsUnloading)
            Mark(MarkType.MARK_CHANGE);

        if (IsFiring && Stage.Fetch_Rate() == 0)
            IsFiring = false;

        // Off the map on guard (and not in a team): gone.
        if (Team == null && Mission == MissionType.MISSION_GUARD && !Game.Map.In_Radar(TdMath.CoordCell(Coord)))
        {
            Stun();
            Delete_This();
            return;
        }

        if (!IsDriving && (Doing == DoType.DO_NOTHING || MasterDoControls[(int)Doing].Interrupt))
            Commence();

        // Fear wears off; a scared soldier lies down.
        if (Fear != 0)
        {
            if (Class.Type != InfantryType.INFANTRY_C10)
            {
                Fear--;
                if (Fear == 0 && Ammo == 0 && Class.Primary != WeaponType.WEAPON_NONE)
                    Ammo = Class.MaxAmmo;
            }

            if (IsProne)
            {
                if (Fear < FEAR_ANXIOUS)
                    Do_Action(DoType.DO_GET_UP);
            }
            else if (Fear >= FEAR_ANXIOUS && ((!Game.Target_Legal(NavCom) && !IsDriving) || !Game.Special.IsDefenderAdvantage))
            {
                Do_Action(DoType.DO_LIE_DOWN);
            }
        }

        if (Class.IsFraidyCat && Fear > FEAR_ANXIOUS && !IsDriving && !Game.Target_Legal(NavCom))
            Scatter(0);

        if (!IsTethered && !IsFiring && !IsDriving && !IsRotating && (Mission == MissionType.MISSION_GUARD || Mission == MissionType.MISSION_GUARD_AREA) &&
            MissionQueue == MissionType.MISSION_NONE && Game.Map[TdMath.CoordCell(Coord)].Cell_Building() != null)
            Scatter(0, true, true);

        // After a kill, a gesture (and a line from Rambo).
        if (!Game.Target_Legal(NavCom) && !IsProne && IsStoked && Comment.Expired)
        {
            IsStoked = false;
            Do_Action(Game.Random_Pick(0, 1) == 0 ? DoType.DO_GESTURE1 : DoType.DO_GESTURE2);
            if (Class.Type == InfantryType.INFANTRY_RAMBO)
                Game.Sound_Effect(RamboStoked[Game.Sim_Random_Pick(0, RamboStoked.Length - 1)], Coord);
        }

        switch (Can_Fire(TarCom, 0))
        {
            case FireErrorType.FIRE_CLOAKED:
                Do_Uncloak();
                break;
            case FireErrorType.FIRE_OK:
                Do_Action(IsProne ? DoType.DO_FIRE_PRONE : DoType.DO_FIRE_WEAPON);
                IsFiring = true;
                PrimaryFacing.Set(TdMath.Direction8(Center_Coord(), Game.As_Coord(TarCom)));
                if (TarCom == NavCom)
                {
                    NavCom = Target.TARGET_NONE;
                    Path[0] = FacingType.FACING_NONE;
                }
                break;
        }

        // The shot leaves at the firing animation's launch frame.
        int firestage = IsProne ? Class.ProneLaunch : Class.FireLaunch;
        if (IsFiring && Stage.Fetch_Stage() == firestage)
        {
            Fire_At(TarCom, 0);
            if (Class.Primary == WeaponType.WEAPON_GRENADE)
                Game.Map[Game.As_Cell(TarCom)].Incoming(Coord, true);
        }

        // An action ended: back to walking, crawling, prone or standing (or a death ends us).
        if (Doing == DoType.DO_NOTHING || Stage.Fetch_Stage() >= Class.DoControl(Doing).Count)
        {
            switch (Doing)
            {
                case DoType.DO_PUNCH_DEATH:
                case DoType.DO_KICK_DEATH:
                case DoType.DO_GUN_DEATH:
                case DoType.DO_EXPLOSION_DEATH:
                case DoType.DO_EXPLOSION2_DEATH:
                case DoType.DO_GRENADE_DEATH:
                case DoType.DO_FIRE_DEATH:
                    Delete_This();
                    return;
                default:
                    if (IsDriving)
                        Do_Action(IsProne ? DoType.DO_CRAWL : DoType.DO_WALK, true);
                    else
                        Do_Action(IsProne ? DoType.DO_PRONE : DoType.DO_STAND_READY, true);
                    break;
            }
        }

        if (!IsFiring)
        {
            if (!IsDriving)
            {
                if (Mission == MissionType.MISSION_GUARD && MissionQueue == MissionType.MISSION_NONE && Game.Target_Legal(NavCom))
                    Assign_Destination(Target.TARGET_NONE);

                if (Game.Target_Legal(NavCom) && Strength != 0 && Mission != MissionType.MISSION_GUARD)
                {
                    // Drop the planned step if it's now blocked, and don't plan beyond the destination.
                    if (Path[0] != FacingType.FACING_NONE && Can_Enter_Cell(TdMath.AdjacentCellOf(TdMath.CoordCell(Center_Coord()), Path[0])) != MoveType.MOVE_OK)
                        Path[0] = FacingType.FACING_NONE;

                    int d = TdMath.LeptonToCell(DistanceToTarget(NavCom));
                    if (d < TdDefines.CONQUER_PATH_MAX)
                        Path[d] = FacingType.FACING_NONE;

                    if (Path[0] == FacingType.FACING_NONE)
                    {
                        if (!PathDelay.Expired)
                            return;

                        if (!Basic_Path())
                        {
                            if (DistanceToTarget(NavCom) < 0x0280 && !IsTethered)
                            {
                                Assign_Destination(Target.TARGET_NONE);
                            }
                            else if (TryTryAgain != 0)
                            {
                                TryTryAgain--;
                            }
                            else
                            {
                                if (IsNewNavCom)
                                    Game.Sound_Effect(VocType.VOC_SCOLD);
                                IsNewNavCom = false;
                                if (Mission == MissionType.MISSION_ENTER)
                                {
                                    Mission = MissionType.MISSION_NONE;
                                    Assign_Mission(MissionType.MISSION_GUARD);
                                    Commence();
                                    Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                                }
                            }
                            Stop_Driver();
                            return;
                        }
                        TryTryAgain = PATH_RETRY;
                    }

                    uint acoord = TdMath.AdjacentCellCoord(Coord, (TdMath.Facing)(int)Path[0]);
                    int acell = TdMath.CoordCell(acoord);

                    if (Can_Enter_Cell(acell) != MoveType.MOVE_OK)
                    {
                        if ((Mission == MissionType.MISSION_MOVE || Mission == MissionType.MISSION_ENTER) && !IsTethered && House.IsHuman && DistanceToTarget(NavCom) < 0x0200)
                            Assign_Destination(Target.TARGET_NONE);

                        // Something destroyable in the way is attacked.
                        if (Can_Enter_Cell(acell) == MoveType.MOVE_DESTROYABLE)
                        {
                            var occupier = Game.Map[acell].Cell_Object();
                            if (occupier != null)
                            {
                                if (!House.Is_Ally(occupier))
                                    Override_Mission(MissionType.MISSION_ATTACK, occupier.As_Target(), Target.TARGET_NONE);
                            }
                            else if (Game.Map[acell].Overlay != OverlayType.OVERLAY_NONE && Game.Types.Overlay(Game.Map[acell].Overlay).IsWall)
                            {
                                Override_Mission(MissionType.MISSION_ATTACK, Target.As_Target(acell), Target.TARGET_NONE);
                            }
                        }

                        Path[0] = FacingType.FACING_NONE;
                        Stop_Driver();
                        if (IsNewNavCom)
                            Game.Sound_Effect(VocType.VOC_SCOLD);
                        IsNewNavCom = false;
                    }
                    else if (Start_Driver(ref acoord))
                    {
                        PrimaryFacing.Set(TdMath.Direction8(Center_Coord(), Head_To_Coord()));
                        Set_Speed(0xFF);
                        Do_Action(IsProne ? DoType.DO_CRAWL : DoType.DO_WALK);
                    }
                }
            }
            else
            {
                Mark(MarkType.MARK_UP);
                if (Distance(Head_To_Coord()) < 0x0010)
                {
                    // Arrived at the spot.
                    Array.Copy(Path, 1, Path, 0, Path.Length - 1);
                    Path[^1] = FacingType.FACING_NONE;
                    Coord = Head_To_Coord();
                    Stop_Driver();
                    Per_Cell_Process(true);
                    if (!IsActive || IsInLimbo)
                        return;
                    if (TdMath.CoordCell(Coord) == Game.As_Cell(NavCom))
                    {
                        NavCom = Target.TARGET_NONE;
                        if (Mission == MissionType.MISSION_MOVE)
                            Enter_Idle_Mode();
                        Path[0] = FacingType.FACING_NONE;
                    }
                }
                else
                {
                    // Prone soldiers crawl at half speed (cowards run at double).
                    int movespeed = Speed;
                    if (IsProne)
                        movespeed = Class.IsFraidyCat && !Class.IsCrawling ? Speed * 2 : Speed / 2;

                    if (IsTethered)
                        Transmit_Message(RadioMessageType.RADIO_REDRAW);

                    var maxspeed = (MPHType)Math.Min((uint)((int)Class.MaxSpeed * House.GroundspeedBias), (uint)MPHType.MPH_LIGHT_SPEED);
                    Coord = TdMath.CoordMove(Coord, Direction(Head_To_Coord()), (int)TdMath.FixedToCardinal((uint)maxspeed, (uint)movespeed));
                }
                Mark(MarkType.MARK_DOWN);
            }
            IsNewNavCom = false;
        }
    }

    /// <summary>Can_Enter_Cell: whether (and how) a soldier may move into a cell.</summary>
    public override MoveType Can_Enter_Cell(int cell, FacingType facing = FacingType.FACING_NONE)
    {
        if ((uint)cell >= TdDefines.MAP_CELL_TOTAL)
            return MoveType.MOVE_NO;

        if (IsLocked && !IsALoaner && Game.ScenarioInit == 0 && !Game.Map.In_Radar(cell))
            return MoveType.MOVE_NO;

        var cellptr = Game.Map[cell];

        if (cellptr.Overlay != OverlayType.OVERLAY_NONE)
        {
            var otype = Game.Types.Overlay(cellptr.Overlay);
            if (otype.IsCrate && !House.IsHuman)
                return MoveType.MOVE_NO;
            // Only a fully destroyed wall can be crossed.
            if (otype.IsWall && (cellptr.OverlayData / 16) != otype.DamageLevels)
                return MoveType.MOVE_NO;
        }

        MoveType retval = MoveType.MOVE_OK;
        ObjectClass? obj = cellptr.Cell_Occupier();
        while (obj != null)
        {
            if (obj != this)
            {
                bool capturing = Mission == MissionType.MISSION_CAPTURE || Mission == MissionType.MISSION_SABOTAGE;
                if (!capturing || obj.What_Am_I() != RTTIType.RTTI_AIRCRAFT || !((AircraftClass)obj).In_Radio_Contact())
                {
                    // The building (or aircraft) to capture or sabotage may be entered.
                    if (obj.What_Am_I() is RTTIType.RTTI_BUILDING or RTTIType.RTTI_AIRCRAFT &&
                        capturing && (obj.As_Target() == NavCom || obj.As_Target() == TarCom))
                        return MoveType.MOVE_OK;

                    if (Mission == MissionType.MISSION_ENTER && obj.As_Target() == NavCom && IsTethered)
                        return MoveType.MOVE_OK;

                    if (House.Is_Ally(obj))
                    {
                        switch (obj.What_Am_I())
                        {
                            case RTTIType.RTTI_UNIT:
                                var unit = (UnitClass)obj;
                                if (unit.IsDriving || Game.Target_Legal(unit.NavCom))
                                {
                                    if (retval < MoveType.MOVE_MOVING_BLOCK)
                                        retval = MoveType.MOVE_MOVING_BLOCK;
                                }
                                else if (retval < MoveType.MOVE_TEMP)
                                {
                                    retval = MoveType.MOVE_TEMP;
                                }
                                break;
                            case RTTIType.RTTI_TERRAIN:
                            case RTTIType.RTTI_AIRCRAFT:
                            case RTTIType.RTTI_BUILDING:
                                return MoveType.MOVE_NO;
                        }
                    }
                    else if (!obj.Is_Techno() || !((TechnoClass)obj).Is_Cloaked(this))
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
                    else if (retval < MoveType.MOVE_CLOAK)
                    {
                        retval = MoveType.MOVE_CLOAK;
                    }
                }
            }
            obj = obj.Next;
        }

        if (retval == MoveType.MOVE_OK && !IsTethered && Game.Types.Ground(cellptr.Land_Type()).Cost[(int)SpeedType.SPEED_FOOT] == 0)
            return MoveType.MOVE_NO;

        if (retval == MoveType.MOVE_OK && cellptr.Flag.Occupy.Vehicle)
            return MoveType.MOVE_NO;

        // Other infantry in the cell: friends block only when all five spots are taken.
        if (cellptr.InfType != HousesType.HOUSE_NONE)
        {
            if (House.Is_Ally(cellptr.InfType))
            {
                if ((cellptr.Flag.Composite & 0x1F) == 0x1F && retval < MoveType.MOVE_MOVING_BLOCK)
                    retval = MoveType.MOVE_MOVING_BLOCK;
            }
            else if (Class.Primary != WeaponType.WEAPON_NONE)
            {
                if (retval < MoveType.MOVE_DESTROYABLE)
                    retval = MoveType.MOVE_DESTROYABLE;
            }
            else
            {
                return MoveType.MOVE_NO;
            }
        }

        if (retval == MoveType.MOVE_OK && (cellptr.Flag.Composite & 0x1F) == 0x1F)
            return MoveType.MOVE_NO;

        return retval;
    }

    public override short[] Overlap_List() =>
        TdMath.Coord_Spillage_List(Coord, 24 + ((Doing > DoType.DO_WALK || Is_Selected_By_Player()) ? 12 : 0));

    /// <summary>Can_Fire: not mid-action, and (without defender's advantage) only roughly ahead while walking.</summary>
    public override FireErrorType Can_Fire(uint target, int which = 0)
    {
        if (IsFiring)
            return FireErrorType.FIRE_REARM;
        if (!Game.Target_Legal(target))
            return FireErrorType.FIRE_ILLEGAL;
        if ((IsDriving && Game.Special.IsDefenderAdvantage) || (Doing != DoType.DO_NOTHING && !MasterDoControls[(int)Doing].Interrupt))
            return FireErrorType.FIRE_MOVING;
        if (!Game.Special.IsDefenderAdvantage && IsDriving)
        {
            int diff = PrimaryFacing.Difference(DirectionToTarget(TarCom));
            if (Math.Abs(diff) >= 32)
                return FireErrorType.FIRE_MOVING;
        }
        return base.Can_Fire(target, which);
    }

    public override void Enter_Idle_Mode(bool initial = false)
    {
        MissionType order;
        if (Game.Target_Legal(TarCom))
            order = MissionType.MISSION_ATTACK;
        else if (Game.Target_Legal(NavCom))
            order = MissionType.MISSION_MOVE;
        else if (Game.GameToPlay == GameType.GAME_NORMAL || House.IsHuman)
            order = MissionType.MISSION_GUARD;
        else
        {
            // The skirmish computer's idle soldiers guard (an area, if smart and armed and teamless).
            order = MissionType.MISSION_GUARD;
            if (Mission == MissionType.MISSION_GUARD || Mission == MissionType.MISSION_GUARD_AREA)
                return;
            if (Team == null && House.IQ >= Game.Rule.IQGuardArea && Is_Weapon_Equipped())
                order = MissionType.MISSION_GUARD_AREA;
        }
        Assign_Mission(order);
    }

    static readonly VocType[] MoebiusQuips = [VocType.VOC_EXCELLENT3, VocType.VOC_QUIP1];

    /// <summary>Random_Animate: idle soldiers salute, gesture, look round (Mobius and Rambo talk).</summary>
    public override void Random_Animate()
    {
        if (!IsDriving && !IsProne && (Doing == DoType.DO_STAND_GUARD || Doing == DoType.DO_STAND_READY) && !IsFiring)
        {
            if (Class.IsFraidyCat && !House.IsHuman && Fear > FEAR_ANXIOUS)
            {
                Scatter(0, true);
                return;
            }

            if (Class.Type == InfantryType.INFANTRY_C10 && Game.Random_Pick(0, 3) == 0)
                Do_Action(DoType.DO_IDLE2);

            switch (Game.Random_Pick(0, 55))
            {
                case 10:
                    Do_Action(DoType.DO_SALUTE1);
                    break;
                case 11:
                    Do_Action(DoType.DO_SALUTE2);
                    break;
                case 12:
                    Do_Action(DoType.DO_GESTURE1);
                    break;
                case 13:
                    Do_Action(DoType.DO_GESTURE2);
                    break;
                case 4:
                case 3:
                case 0:
                    PrimaryFacing.Set((byte)(Game.Random_Pick((int)FacingType.FACING_N, (int)FacingType.FACING_NW) << 5));
                    Mark(MarkType.MARK_CHANGE);
                    break;
                case 1:
                    Do_Action(DoType.DO_IDLE1);
                    PrimaryFacing.Set((byte)(Game.Random_Pick((int)FacingType.FACING_N, (int)FacingType.FACING_NW) << 5));
                    if (Game.Sim_Random_Pick(1, 20) == 1 && !Is_Selected_By_Player() && Class.Type == InfantryType.INFANTRY_MOEBIUS && IsDiscoveredByPlayer)
                        Game.Sound_Effect(MoebiusQuips[Game.Sim_Random_Pick(0, MoebiusQuips.Length - 1)], Coord);
                    break;
                case 2:
                    Do_Action(DoType.DO_IDLE2);
                    PrimaryFacing.Set((byte)(Game.Random_Pick((int)FacingType.FACING_N, (int)FacingType.FACING_NW) << 5));
                    if (!Is_Selected_By_Player() && IsOwnedByPlayer && Class.Type == InfantryType.INFANTRY_RAMBO && Game.Sim_Random_Pick(0, 2) == 0)
                        Game.Sound_Effect(VocType.VOC_RAMBO_CMON, Coord);
                    break;
                case 5:
                case 6:
                case 7:
                case 8:
                case 9:
                    if (!House.IsHuman && Class.IsFraidyCat)
                        Scatter(0, true);
                    break;
            }
        }
    }

    /// <summary>Scatter: move to a free nearby cell, away from a threat (forced, or for cowards).</summary>
    public override void Scatter(uint threat, bool forced = false, bool nokidding = false)
    {
        if (IsDriving || Game.Target_Legal(NavCom))
            forced = false;
        if (!Class.IsFraidyCat && Game.Target_Legal(TarCom) && !forced)
            return;
        if (Doing != DoType.DO_NOTHING && !MasterDoControls[(int)Doing].Interrupt)
            return;
        if (!Game.Special.IsScatter && !nokidding && House.IsHuman && !forced && Team == null)
            return;

        if (forced || Class.IsFraidyCat)
        {
            FacingType toface;
            if (threat != 0)
            {
                toface = TdMath.DirFacingType(TdMath.Direction8(threat, Coord));
                toface = TdMath.FacingAdd(toface, Game.Random_Pick(0, 4) - 2);
            }
            else
            {
                // Away from the cell centre (or the way it faces if in the middle).
                uint coord = Coord & 0x00FF00FF;
                if (coord != 0x00800080)
                    toface = TdMath.DirFacingType(TdMath.DesiredFacing8(0x0080, 0x0080, TdMath.CoordX(coord), TdMath.CoordY(coord)));
                else
                    toface = TdMath.DirFacingType(PrimaryFacing.Current);
                toface = TdMath.FacingAdd(toface, Game.Random_Pick(0, 4) - 2);
            }

            // As the original: each free cell round about is assigned in turn (the last one stays).
            for (int face = 0; face < (int)FacingType.FACING_COUNT; face++)
            {
                FacingType newface = TdMath.FacingAdd(toface, face);
                int newcell = TdMath.AdjacentCellOf(TdMath.CoordCell(Coord), newface);

                if (Game.Map.In_Radar(newcell) && Can_Enter_Cell(newcell) == MoveType.MOVE_OK)
                {
                    if (!Class.IsAvoidingTiberium || Game.Map[newcell].Overlay == OverlayType.OVERLAY_NONE ||
                        Game.Types.Overlay(Game.Map[newcell].Overlay).Land != LandType.LAND_TIBERIUM)
                    {
                        Assign_Mission(MissionType.MISSION_MOVE);
                        Assign_Destination(Target.As_Target(newcell));
                    }
                }
            }
        }
    }

    /// <summary>Do_Action: start an animation action (if the current one may be interrupted).</summary>
    public bool Do_Action(DoType todo, bool force = false)
    {
        if (todo != Doing && (Doing == DoType.DO_NOTHING || force || MasterDoControls[(int)Doing].Interrupt))
        {
            Mark(MarkType.MARK_CHANGE);
            Doing = todo;
            if (todo == DoType.DO_IDLE1 || todo == DoType.DO_IDLE2)
                Stage.Set_Rate(Game.Options.Normalize_Delay(MasterDoControls[(int)Doing].Rate));
            else
                Stage.Set_Rate(MasterDoControls[(int)Doing].Rate);
            Stage.Set_Stage(0);

            // A dying soldier stops.
            if (Strength == 0)
                Stop_Driver();

            switch (todo)
            {
                case DoType.DO_LIE_DOWN:
                    IsProne = true;
                    break;
                case DoType.DO_GET_UP:
                    IsProne = false;
                    break;
                case DoType.DO_READY_WEAPON:
                    IsBoxing = false;
                    break;
                case DoType.DO_ON_GUARD:
                    IsBoxing = true;
                    PrimaryFacing.Set(TdMath.Direction8(Center_Coord(), Game.As_Coord(TarCom)));
                    Path[0] = FacingType.FACING_NONE;
                    break;
            }
            return true;
        }
        return false;
    }

    public override bool Stop_Driver()
    {
        if (Head_To_Coord() != 0)
            Clear_Occupy_Bit(Head_To_Coord());
        Set_Occupy_Bit(Coord);

        if (Doing != DoType.DO_STAND_READY)
            StopDriverFrame = Game.Frame;

        Do_Action(IsProne ? DoType.DO_PRONE : DoType.DO_STAND_READY);
        return base.Stop_Driver();
    }

    /// <summary>Start_Driver: aims for the closest free spot in the next cell.</summary>
    public override bool Start_Driver(ref uint headto)
    {
        uint old = headto;
        headto = Game.Map[TdMath.CoordCell(headto)].Closest_Free_Spot(TdMath.CoordMove(headto, (byte)(Direction(headto) + (int)DirType.DIR_S), 0x007C));
        // As the original: the second try works out the direction from the (now zero) headto.
        if (headto == 0 && Can_Enter_Cell(TdMath.CoordCell(old)) == MoveType.MOVE_OK)
            headto = Game.Map[TdMath.CoordCell(old)].Closest_Free_Spot(TdMath.CoordMove(old, (byte)(Direction(headto) + (int)DirType.DIR_S), 0x0080), true);

        if (headto != 0 && base.Start_Driver(ref headto))
        {
            Clear_Occupy_Bit(Coord);
            Set_Occupy_Bit(headto);
            return true;
        }
        return false;
    }

    public override bool Limbo()
    {
        if (!IsInLimbo)
        {
            Stop_Driver();
            Clear_Occupy_Bit(Coord);
        }
        return base.Limbo();
    }

    /// <summary>Fire_At: sound; a coward out of ammo panics.</summary>
    public override BulletClass? Fire_At(uint target, int which = 0)
    {
        var weapon = Game.Types.Weapon(which == 0 ? Class.Primary : Class.Secondary);
        IsFiring = false;
        var bullet = base.Fire_At(target, which);
        if (bullet != null)
        {
            if (Class.IsFraidyCat && Ammo == 0)
            {
                Fear = FEAR_MAXIMUM;
                if (Mission == MissionType.MISSION_ATTACK || Mission == MissionType.MISSION_HUNT)
                    Assign_Mission(MissionType.MISSION_GUARD);
            }
            Game.Sound_Effect(weapon.Sound, Coord);
        }
        return bullet;
    }

    /// <summary>Unlimbo: into the closest free spot of the cell.</summary>
    public override bool Unlimbo(uint coord, byte facing = 0)
    {
        coord = Game.Map[TdMath.CoordCell(coord)].Closest_Free_Spot(coord, Game.ScenarioInit != 0);
        if (coord == 0)
            return false;

        if (base.Unlimbo(coord, facing))
        {
            House.IScan |= 1L << (int)Class.Type;
            House.ActiveIScan |= 1L << (int)Class.Type;

            // Hidden computer civilians stay undiscovered.
            if (Class.SightRange == 0 && Game.GameToPlay == GameType.GAME_NORMAL && !House.IsHuman && !Game.Map[TdMath.CoordCell(coord)].Is_Visible(Game.PlayerPtr))
            {
                IsDiscoveredByPlayerMask &= ~(1 << (int)Game.PlayerPtr.Class.House);
                IsDiscoveredByPlayer = false;
            }

            Set_Occupy_Bit(coord);
            return true;
        }
        return false;
    }

    /// <summary>Greatest_Threat: engineers look for buildings; riflemen guarding for the player don't hunt.</summary>
    public override uint Greatest_Threat(ThreatType threat)
    {
        if (Class.IsCapture && Class.Primary == WeaponType.WEAPON_NONE)
            threat |= ThreatType.THREAT_CAPTURE;

        switch (Class.Primary)
        {
            case WeaponType.WEAPON_NONE:
                if (Class.Type != InfantryType.INFANTRY_E7)
                    return Target.TARGET_NONE;
                break;
            case WeaponType.WEAPON_RIFLE:
                if (House.IsHuman && (Mission == MissionType.MISSION_GUARD || Mission == MissionType.MISSION_GUARD_AREA))
                    return Target.TARGET_NONE;
                // TechnoClass::Greatest_Threat directly (FootClass's is skipped).
                return Techno_Greatest_Threat(threat | ThreatType.THREAT_INFANTRY | ThreatType.THREAT_BUILDINGS);
            default:
                if (Game.Types.Bullet(Game.Types.Weapon(Class.Primary).Fires).IsAntiAircraft)
                    threat |= ThreatType.THREAT_AIR;
                break;
        }
        return base.Greatest_Threat(threat);
    }

    static readonly VocType[] RamboSelect = [VocType.VOC_RAMBO_YEA, VocType.VOC_RAMBO_YES, VocType.VOC_RAMBO_YO];
    static readonly VocType[] MoebiusSelect = [VocType.VOC_YES, VocType.VOC_COMMANDER, VocType.VOC_HELLO, VocType.VOC_HMMM];
    static readonly VocType[] SoldierSelect = [VocType.VOC_ACKNOWL, VocType.VOC_REPORT, VocType.VOC_REPORT, VocType.VOC_YESSIR, VocType.VOC_YESSIR, VocType.VOC_READY, VocType.VOC_AWAIT];
    static readonly VocType[] RamboMove = [VocType.VOC_RAMBO_UGOTIT, VocType.VOC_RAMBO_ONIT, VocType.VOC_RAMBO_NOPROB];
    static readonly VocType[] MoebiusMove = [VocType.VOC_OF_COURSE, VocType.VOC_YESYES];
    static readonly VocType[] SoldierMove = [VocType.VOC_MOVEOUT, VocType.VOC_MOVEOUT, VocType.VOC_MOVEOUT, VocType.VOC_ROGER, VocType.VOC_RIGHT_AWAY, VocType.VOC_UGOTIT, VocType.VOC_AFFIRM, VocType.VOC_AFFIRM];
    static readonly VocType[] RamboAttack = [VocType.VOC_RAMBO_NOPROB, VocType.VOC_RAMBO_UGOTIT, VocType.VOC_RAMBO_NOPROB, VocType.VOC_RAMBO_ONIT];
    static readonly VocType[] SoldierAttack = [VocType.VOC_RIGHT_AWAY, VocType.VOC_AFFIRM, VocType.VOC_AFFIRM, VocType.VOC_UGOTIT, VocType.VOC_NO_PROB, VocType.VOC_YESSIR, VocType.VOC_YESSIR, VocType.VOC_YESSIR];

    VocType Pick(VocType[] list) => list[Game.Sim_Random_Pick(0, list.Length - 1)];

    void Respond(VocType response)
    {
        if (Game.AllowVoice)
            Game.Sound_Effect(response, 0, Game.Infantry.ID(this) + 1);
    }

    public override void Response_Select()
    {
        VocType response;
        if (Class.Type == InfantryType.INFANTRY_RAMBO)
            response = Pick(RamboSelect);
        else if (Class.IsCivilian)
            response = Class.Type == InfantryType.INFANTRY_MOEBIUS ? Pick(MoebiusSelect) : Class.IsFemale ? VocType.VOC_GIRL_YEAH : VocType.VOC_GUY_YEAH;
        else
            response = Pick(SoldierSelect);
        Respond(response);
    }

    public override void Response_Move()
    {
        VocType response;
        if (Class.Type == InfantryType.INFANTRY_RAMBO)
            response = Pick(RamboMove);
        else if (Class.IsCivilian)
            response = Class.Type == InfantryType.INFANTRY_MOEBIUS ? Pick(MoebiusMove) : Class.IsFemale ? VocType.VOC_GIRL_OKAY : VocType.VOC_GUY_OKAY;
        else
            response = Pick(SoldierMove);
        Respond(response);
    }

    public override void Response_Attack()
    {
        VocType response;
        if (Class.Type == InfantryType.INFANTRY_RAMBO)
            response = Pick(RamboAttack);
        else if (Class.IsCivilian)
            response = Class.IsFemale ? VocType.VOC_GIRL_OKAY : VocType.VOC_GUY_OKAY;
        else
            response = Pick(SoldierAttack);
        Respond(response);
    }

    /// <summary>Fire_Coord: from the gun (the flamethrower's flame starts at the feet).</summary>
    public override uint Fire_Coord(int which) =>
        Class.Type == InfantryType.INFANTRY_E4 ? Coord : TdMath.CoordAdd(Coord, TdMath.XYP_COORD(0, -5));

    public override RadioMessageType Receive_Message(RadioClass? from, RadioMessageType message, ref int param)
    {
        int damage;
        switch (message)
        {
            case RadioMessageType.RADIO_OVER_OUT:
                break;

            // Boxing (never started in this build: nothing sends these).
            case RadioMessageType.RADIO_PREPARE_TO_BOX:
                if (Contact_With_Whom() == from)
                {
                    Do_Action(DoType.DO_ON_GUARD, true);
                    Assign_Target(Contact_With_Whom()!.As_Target());
                    return RadioMessageType.RADIO_ROGER;
                }
                return RadioMessageType.RADIO_NEGATIVE;

            case RadioMessageType.RADIO_KICK:
                // Random_Pick(0, count) can read one past the table; the original then reads the
                // next variable (the punch table's first entry).
                damage = Boxing_Damage(InfantryKickDamage, InfantryPunchDamage[0], Game.Random_Pick(0, InfantryKickDamage.Length));
                if (Take_Damage(ref damage, 0, WarheadType.WARHEAD_FOOT, this) == ResultType.RESULT_DESTROYED)
                    return RadioMessageType.RADIO_STATIC;
                return RadioMessageType.RADIO_ROGER;

            case RadioMessageType.RADIO_PUNCH:
                damage = Boxing_Damage(InfantryPunchDamage, 0, Game.Random_Pick(0, InfantryPunchDamage.Length));
                if (Take_Damage(ref damage, 0, WarheadType.WARHEAD_FIST, this) == ResultType.RESULT_DESTROYED)
                    return RadioMessageType.RADIO_STATIC;
                return RadioMessageType.RADIO_ROGER;
        }
        return base.Receive_Message(from, message, ref param);
    }

    static int Boxing_Damage(int[] table, int pastTheEnd, int index) => index < table.Length ? table[index] : pastTheEnd;

    public override void Assign_Mission(MissionType order)
    {
        if (order == MissionType.MISSION_SABOTAGE)
            Game.Sound_Effect(VocType.VOC_RAMBO_PRESENT, Coord);
        IsBoxing = false;
        base.Assign_Mission(order);
    }

    public override ActionType What_Action(ObjectClass obj)
    {
        ActionType action = base.What_Action(obj);

        // Commandos sabotage buildings.
        if (Class.Type == InfantryType.INFANTRY_RAMBO && action == ActionType.ACTION_ATTACK && obj.What_Am_I() == RTTIType.RTTI_BUILDING)
            return ActionType.ACTION_SABOTAGE;

        if (action == ActionType.ACTION_SELF)
            action = ActionType.ACTION_NONE;

        if (House.Is_Ally(obj) && Is_Owned_By_Player() && obj.Is_Techno() && Transmit_Message(RadioMessageType.RADIO_CAN_LOAD, (TechnoClass)obj) == RadioMessageType.RADIO_ROGER)
            action = ActionType.ACTION_ENTER;

        // Engineers capture buildings (and empty transport helicopters).
        if (Class.IsCapture && action == ActionType.ACTION_ATTACK)
        {
            if (obj.Owner() != Owner() &&
                ((obj is AircraftClass aircraft && aircraft.Pip_Count() == 0 && aircraft.Class.Type == AircraftType.AIRCRAFT_TRANSPORT) ||
                 (obj is BuildingClass building && building.Can_Capture())))
                action = ActionType.ACTION_CAPTURE;
            else if (Class.Primary == WeaponType.WEAPON_NONE)
                action = ActionType.ACTION_NONE;
        }
        return action;
    }

    /// <summary>Read_INI: the scenario's [INFANTRY] — house, type, strength, cell, spot, mission, facing, trigger.</summary>
    public static void Read_INI(TdGame game, Data.WwProfile ini)
    {
        string buf = "";
        foreach (string key in ini.Entries("INFANTRY"))
        {
            buf = ini.Get_String("INFANTRY", key, buf, 128 - 1);
            var tok = new Data.Strtok(buf);
            HousesType inhouse = game.Types.House_From_Name(tok.Next(",\n\r"));
            if (inhouse == HousesType.HOUSE_NONE)
                continue;

            InfantryType classid = game.Types.Infantry_From_Name(tok.Next(",\n\r"));

            // GDI mission 8B: the civilian (C7) is a C5 instead.
            if (game.GameToPlay == GameType.GAME_NORMAL && game.PlayerPtr.ActLike == HousesType.HOUSE_GOOD && game.Scenario == 8 && game.ScenVar == ScenarioVarType.SCEN_VAR_B && classid == InfantryType.INFANTRY_C7)
                classid = InfantryType.INFANTRY_C5;

            if (classid == InfantryType.INFANTRY_NONE || game.HouseOrNull(inhouse) == null)
                continue;

            var infantry = Create(game, classid, inhouse);
            if (infantry == null)
                continue;

            int strength = Data.WwProfile.Atoi(tok.Next(",\n\r"));
            uint coord = TdMath.CellCoord((short)Data.WwProfile.Atoi(tok.Next(",\n\r")));
            coord = TdMath.CoordAdd(coord & 0xFF00FF00, TdMath.StoppingCoordAbs[Data.WwProfile.Atoi(tok.Next(","))]);
            MissionType mission = MissionClass.Mission_From_Name(tok.Next(",\n\r"));
            byte dir = (byte)Data.WwProfile.Atoi(tok.Next(",\n\r"));
            infantry.Trigger = Scenario.TriggerClass.As_Pointer(game, tok.Next(",\n\r"));
            if (infantry.Trigger != null)
                infantry.Trigger.AttachCount++;

            // Nod mission 10A: Chan isn't placed.
            bool isScb10eaChan = game.GameToPlay == GameType.GAME_NORMAL && game.PlayerPtr.ActLike == HousesType.HOUSE_BAD && game.Scenario == 10 &&
                                 game.ScenVar == ScenarioVarType.SCEN_VAR_A && infantry.Class.Type == InfantryType.INFANTRY_CHAN;

            if (!isScb10eaChan && infantry.Unlimbo(coord, dir))
            {
                infantry.Strength = (int)TdMath.FixedToCardinal((uint)infantry.Class.MaxStrength, (uint)strength);
                if (game.GameToPlay == GameType.GAME_NORMAL || infantry.House.IsHuman)
                {
                    infantry.Assign_Mission(mission);
                    infantry.Commence();
                }
                else
                {
                    infantry.Enter_Idle_Mode();
                }
            }
            else
            {
                infantry.Delete();
            }
        }
    }

    public override void Active_Click_With(ActionType action, ObjectClass obj)
    {
        if (What_Action(obj) != action)
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

    /// <summary>Made_A_Kill: Rambo (and veterans, more likely) get stoked and gesture.</summary>
    public override int Made_A_Kill()
    {
        if (Class.Type == InfantryType.INFANTRY_RAMBO || Game.Random_Pick(0, 5) < Kills)
        {
            IsStoked = true;
            Comment.Set(TdDefines.TICKS_PER_SECOND * 2);
        }
        return base.Made_A_Kill();
    }

    public void Set_Occupy_Bit(uint coord) => Set_Occupy_Bit(TdMath.CoordCell(coord), CellClass.Spot_Index(coord));
    public void Clear_Occupy_Bit(uint coord) => Clear_Occupy_Bit(TdMath.CoordCell(coord), CellClass.Spot_Index(coord));

    public void Set_Occupy_Bit(int cell, int spotIndex)
    {
        if ((uint)cell >= TdDefines.MAP_CELL_TOTAL)
            return;
        Game.Map[cell].Flag.Composite |= (byte)(1 << spotIndex);
        Game.Map[cell].InfType = Owner();
    }

    public void Clear_Occupy_Bit(int cell, int spotIndex)
    {
        if ((uint)cell >= TdDefines.MAP_CELL_TOTAL)
            return;
        Game.Map[cell].Flag.Composite &= (byte)~(1 << spotIndex);
        if ((Game.Map[cell].Flag.Composite & 0x1F) == 0)
            Game.Map[cell].InfType = HousesType.HOUSE_NONE;
    }

    public override int Full_Name() => IsTechnician ? (int)TdDefines.TXT_TECHNICIAN : Class.Full_Name(Game.Special);

    /// <summary>Mission_Attack: an engineer told to attack a building captures it.</summary>
    public override int Mission_Attack()
    {
        if (Class.Type == InfantryType.INFANTRY_E7 && Game.As_Building(TarCom) != null)
        {
            Assign_Destination(TarCom);
            Assign_Mission(MissionType.MISSION_CAPTURE);
            return 1;
        }
        return base.Mission_Attack();
    }
}

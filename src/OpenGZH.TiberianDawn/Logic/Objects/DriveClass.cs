using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// DriveClass (DRIVE.CPP): ground vehicles. They move cell to cell along pre-computed tracks
/// (DriveClass.Tracks.g.cs) that give the smooth curves of a turn, one pixel step at a time.
/// </summary>
public abstract partial class DriveClass : FootClass
{
    public const int BACKUP_INTO_REFINERY = 64;
    public const int OUT_OF_REFINERY = 65;
    public const int OUT_OF_WEAPON_FACTORY = 66;

    [Flags]
    public enum TrackControlType : byte { F_ = 0x00, F_T = 0x01, F_X = 0x02, F_Y = 0x04, F_D = 0x08 }

    public readonly record struct TurnTrackType(int Track, int StartTrack, byte Facing, TrackControlType Flag);
    public readonly record struct TrackType(uint Offset, byte Facing);
    public readonly record struct RawTrackType(TrackType[] Track, int Jump, int Entry, int Cell);

    public readonly UnitTypeClass Class;
    public short SimLeptonX, SimLeptonY;
    public byte Tiberium;
    public bool IsHarvesting, IsReturning, IsTurretLockedDown, IsOnShortTrack;
    byte speedAccum;
    protected int TrackNumber;
    protected int TrackIndex;

    protected DriveClass(TdGame game, UnitType classid, HousesType house) : base(game, house)
    {
        Class = game.Types.Unit(classid);
        IsSecondShot = !Class.IsTwoShooter;
        IsHarvesting = false;
        IsTurretLockedDown = false;
        IsOnShortTrack = false;
        IsReturning = false;
        TrackNumber = -1;
        TrackIndex = 0;
        speedAccum = 0;
        Tiberium = 0;
        Strength = Class.MaxStrength;
    }

    public override ObjectTypeClass Class_Of() => Class;

    /// <summary>Do_Turn: turn to a direction (wheeled units with three-point turns drive a short track).</summary>
    public void Do_Turn(byte dir)
    {
        if (dir != PrimaryFacing.Current)
        {
            if (Game.Special.IsThreePoint && TrackNumber == -1 && Class.Speed == SpeedType.SPEED_WHEEL)
            {
                int facediff = PrimaryFacing.Difference(dir) >> 5;
                facediff = Math.Clamp(facediff, -2, 2);
                if (facediff != 0)
                {
                    FacingType face = TdMath.DirFacingType(PrimaryFacing.Current);
                    IsOnShortTrack = true;
                    Force_Track((int)face * (int)FacingType.FACING_COUNT + (int)TdMath.FacingAdd(face, facediff), Coord);
                    Path[0] = FacingType.FACING_NONE;
                    Set_Speed(0xFF);
                }
            }
            else
            {
                PrimaryFacing.Set_Desired(dir);
                if (Class.IsPieceOfEight)
                    PrimaryFacing.Set_Current(dir);
            }
        }
    }

    public void Force_Track(int track, uint coord)
    {
        TrackNumber = track;
        TrackIndex = 0;
        Start_Driver(ref coord);
    }

    public override int Tiberium_Load() =>
        Class.Type == UnitType.UNIT_HARVESTER ? (int)TdMath.CardinalToFixed(UnitTypeClass.STEP_COUNT, Tiberium) : 0;

    /// <summary>Approach_Target: computer crushers drive over crushable targets.</summary>
    public override void Approach_Target()
    {
        if (!House.IsHuman && Game.Target_Legal(TarCom) && !Game.Target_Legal(NavCom))
        {
            var target = Game.As_Techno(TarCom);
            if (Class.Primary != WeaponType.WEAPON_FLAME_TONGUE && Class.IsCrusher && DistanceToTarget(TarCom) < 0x0180 && target != null && target.Techno_Type_Class().IsCrushable)
            {
                Assign_Destination(TarCom);
                return;
            }
        }
        base.Approach_Target();
    }

    /// <summary>Overrun_Square: threaten (warn the infantry of) or crush the cell's contents.</summary>
    public virtual void Overrun_Square(int cell, bool threaten = true)
    {
        var cellptr = Game.Map[cell];

        if (Class.IsCrusher)
        {
            if (threaten)
            {
                // Infantry in the way may scatter (on hard, scatter mode, or after mission 8).
                if ((cellptr.Flag.Composite & 0x1F) != 0)
                {
                    bool normal = Game.GameToPlay == GameType.GAME_NORMAL;
                    if (((normal && Game.PlayerPtr.Difficulty == DiffType.DIFF_HARD) || Game.Special.IsScatter || Game.Scenario > 8) &&
                        !(normal && Game.PlayerPtr.Difficulty == DiffType.DIFF_EASY))
                        cellptr.Incoming(0, true);
                }
            }
            else
            {
                ObjectClass? obj = cellptr.Cell_Occupier();
                bool crushed = false;
                while (obj != null)
                {
                    if (obj.Class_Of().IsCrushable && !House.Is_Ally(obj) && Distance(obj.Center_Coord()) < 0x80)
                    {
                        ObjectClass? next = obj.Next;
                        crushed = true;
                        Game.Sound_Effect(VocType.VOC_SQUISH2, Coord);
                        obj.Record_The_Kill(this);
                        obj.Mark(MarkType.MARK_UP);
                        obj.Limbo();
                        obj.Delete();
                        OverlayClass.Create(Game, OverlayType.OVERLAY_SQUISH, TdMath.CoordCell(Coord));
                        obj = next;
                    }
                    else
                    {
                        obj = obj.Next;
                    }
                }
                if (crushed)
                    Do_Uncloak();
            }
        }
    }

    /// <summary>Exit_Map: the hovercraft leaves by the bottom edge once nothing is below it.</summary>
    public void Exit_Map()
    {
        if (Class.Type == UnitType.UNIT_HOVER && !Game.Target_Legal(NavCom))
        {
            Mark(MarkType.MARK_UP);
            int cx = TdMath.CellX(TdMath.CoordCell(Center_Coord()));
            int cy = TdMath.CellY(TdMath.CoordCell(Center_Coord()));
            for (int x = cx - 1; x <= cx + 1; x++)
            {
                for (int y = cy + 1; y < Game.Map.MapCellY + Game.Map.MapCellHeight; y++)
                {
                    int c = TdMath.XyCell(x, y);
                    if (Game.Map[c].Cell_Techno() != null)
                    {
                        Mark(MarkType.MARK_DOWN);
                        return;
                    }
                }
            }
            Mark(MarkType.MARK_DOWN);
            int cell = TdMath.XyCell(TdMath.CellX(TdMath.CoordCell(Coord)), Game.Map.MapCellY + Game.Map.MapCellHeight);
            IsReturning = true;
            Assign_Destination(Target.As_Target(cell));
        }
    }

    /// <summary>Smooth_Turn: a track offset turned (transposed/mirrored) for this track, from the cell being driven to.</summary>
    uint Smooth_Turn(uint adj, ref byte dir)
    {
        byte workdir = dir;
        var flags = TrackControl[TrackNumber].Flag;
        int x = TdMath.CoordX(adj);
        int y = TdMath.CoordY(adj);

        if ((flags & TrackControlType.F_T) != 0)
        {
            (x, y) = (y, x);
            workdir = (byte)((int)DirType.DIR_W - workdir);
        }
        if ((flags & TrackControlType.F_X) != 0)
        {
            x = -x;
            workdir = (byte)-workdir;
        }
        if ((flags & TrackControlType.F_Y) != 0)
        {
            y = -y;
            workdir = (byte)((int)DirType.DIR_S - workdir);
        }

        dir = workdir;
        return TdMath.XyCoord(TdMath.CoordX(Head_To_Coord()) + x, TdMath.CoordY(Head_To_Coord()) + y);
    }

    /// <summary>Assign_Destination: a repair bay is contacted (and docked with) on the way.</summary>
    public override void Assign_Destination(uint target)
    {
        if (target == NavCom)
            return;

        var b = Game.As_Building(target);

        // Transports drop contact with infantry (they were loading).
        if (In_Radio_Contact() && Class.IsTransporter && Contact_With_Whom()!.Is_Infantry())
            Transmit_Message(RadioMessageType.RADIO_OVER_OUT);

        if (b != null && b.Class.Type == StructType.STRUCT_REPAIR)
        {
            if (b.In_Radio_Contact() && b.Contact_With_Whom() != this)
            {
                ArchiveTarget = target;
            }
            else if (Transmit_Message(RadioMessageType.RADIO_HELLO, b) == RadioMessageType.RADIO_ROGER)
            {
                int cell = TdMath.CoordCell(b.Center_Coord()) + (TdMath.MapCellW - 1);
                if (Game.Types.Ground(Game.Map[cell].Land_Type()).Cost[(int)Class.Speed] != 0)
                {
                    if (Transmit_Message(RadioMessageType.RADIO_DOCKING) == RadioMessageType.RADIO_ROGER)
                    {
                        base.Assign_Destination(target);
                        Path[0] = FacingType.FACING_NONE;
                        return;
                    }
                    Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                }
            }
        }

        base.Assign_Destination(target);
        Path[0] = FacingType.FACING_NONE;
        if (!IsDriving)
            Start_Of_Move();
    }

    /// <summary>While_Moving: advances along the track by this frame's speed, a pixel per step, and
    /// switches to the next cell's track mid-way when the path turns.</summary>
    bool While_Moving()
    {
        int actual;

        if (!IsDriving || TrackNumber == -1 || (IsRotating && !Class.IsTurretEquipped))
        {
            speedAccum = 0;
            return false;
        }

        // Flag carriers move at half speed.
        var maxspeed = (MPHType)Math.Min((int)((int)Class.MaxSpeed * House.GroundspeedBias), (int)MPHType.MPH_LIGHT_SPEED);
        if (((UnitClass)this).Flagged != HousesType.HOUSE_NONE)
            actual = speedAccum + (int)TdMath.FixedToCardinal((uint)maxspeed / 2, Speed);
        else
            actual = speedAccum + (int)TdMath.FixedToCardinal((uint)maxspeed, Speed);

        const int pixelLepton = (int)TdDefines.PIXEL_LEPTON_W;
        if (actual > pixelLepton)
        {
            TurnTrackType track = TrackControl[TrackNumber];
            int tracknum = IsOnShortTrack ? track.StartTrack : track.Track;
            TrackType[] ptr = RawTracks[tracknum - 1].Track;
            FacingType nextface = Path[0];
            bool adj = nextface != FacingType.FACING_NONE && TdMath.DirFacingType(track.Facing) != nextface;

            Mark(MarkType.MARK_UP);
            while (actual > pixelLepton)
            {
                actual -= pixelLepton;

                uint offset = ptr[TrackIndex].Offset;
                if (offset != 0 || TrackIndex == 0)
                {
                    byte dir = ptr[TrackIndex].Facing;
                    Coord = Smooth_Turn(offset, ref dir);
                    PrimaryFacing.Set(dir);

                    // Reached the middle of the next cell.
                    if (TrackIndex != 0 && RawTracks[tracknum - 1].Cell == TrackIndex)
                    {
                        Per_Cell_Process(false);
                        if (!IsActive)
                            return false;
                    }

                    // The jump point: switch to the next cell's track if the path turns.
                    if (Class.Type != UnitType.UNIT_GUNBOAT && nextface != FacingType.FACING_NONE && adj && RawTracks[tracknum - 1].Jump == TrackIndex && TrackIndex != 0)
                    {
                        int tnum = (int)TdMath.DirFacingType(track.Facing) * (int)FacingType.FACING_COUNT + (int)nextface;
                        TurnTrackType newtrack = TrackControl[tnum];

                        if (newtrack.Track != 0 && RawTracks[newtrack.Track - 1].Entry != 0)
                        {
                            uint c = Head_To_Coord();
                            int oldspeed = Speed;

                            c = TdMath.AdjacentCellCoord(c, (TdMath.Facing)(int)nextface);

                            switch (Can_Enter_Cell(TdMath.CoordCell(c), nextface))
                            {
                                case MoveType.MOVE_OK:
                                    IsOnShortTrack = false;
                                    TrackNumber = tnum;
                                    track = newtrack;
                                    tracknum = track.Track;
                                    TrackIndex = RawTracks[tracknum - 1].Entry - 1;
                                    ptr = RawTracks[tracknum - 1].Track;
                                    adj = false;

                                    Stop_Driver();
                                    Per_Cell_Process(true);
                                    if (Start_Driver(ref c))
                                    {
                                        Set_Speed(oldspeed);
                                        Array.Copy(Path, 1, Path, 0, TdDefines.CONQUER_PATH_MAX - 1);
                                        Path[TdDefines.CONQUER_PATH_MAX - 1] = FacingType.FACING_NONE;
                                    }
                                    else
                                    {
                                        Path[0] = FacingType.FACING_NONE;
                                        TrackNumber = -1;
                                        actual = 0;
                                    }
                                    break;

                                case MoveType.MOVE_CLOAK:
                                    Game.Map[TdMath.CoordCell(c)].Shimmer();
                                    break;

                                case MoveType.MOVE_TEMP:
                                    if (Class.Type == UnitType.UNIT_HARVESTER || !House.IsHuman)
                                    {
                                        bool old = Game.Special.IsScatter;
                                        Game.Special.IsScatter = true;
                                        Game.Map[TdMath.CoordCell(c)].Incoming(0, true);
                                        Game.Special.IsScatter = old;
                                    }
                                    break;
                            }
                        }
                    }
                    TrackIndex++;
                }
                else
                {
                    // End of the track.
                    actual = 0;
                    Coord = Head_To_Coord();
                    Stop_Driver();
                    TrackNumber = -1;
                    TrackIndex = 0;
                    Per_Cell_Process(true);
                    break;
                }
            }
            if (IsActive)
                Mark(MarkType.MARK_DOWN);
        }

        // The part-step left over (for smooth drawing between steps).
        if (actual != 0 && actual <= pixelLepton)
        {
            TurnTrackType track = TrackControl[TrackNumber];
            int tracknum = IsOnShortTrack ? track.StartTrack : track.Track;
            TrackType[] ptr = RawTracks[tracknum - 1].Track;

            uint simulatedPos = Coord;
            uint offset = ptr[TrackIndex].Offset;
            if (offset != 0 || TrackIndex == 0)
            {
                byte dir = ptr[TrackIndex].Facing;
                simulatedPos = Smooth_Turn(offset, ref dir);
            }
            int xDiff = TdMath.CoordX(simulatedPos) - TdMath.CoordX(Coord);
            int yDiff = TdMath.CoordY(simulatedPos) - TdMath.CoordY(Coord);
            SimLeptonX = (short)((xDiff * actual) / pixelLepton);
            SimLeptonY = (short)((yDiff * actual) / pixelLepton);
        }
        else
        {
            SimLeptonX = 0;
            SimLeptonY = 0;
        }

        speedAccum = (byte)actual;
        return true;
    }

    public override void Per_Cell_Process(bool center)
    {
        int cell = TdMath.CoordCell(Coord);

        // Arrived: the destination is reached.
        if (center && Game.As_Cell(NavCom) == cell)
        {
            IsTurretLockedDown = false;
            NavCom = Target.TARGET_NONE;
            Path[0] = FacingType.FACING_NONE;
        }

        Lay_Track();
        base.Per_Cell_Process(center);
    }

    /// <summary>A blocking friend at a cell is asked to move aside (scatter forced on for it).</summary>
    void Nudge_Friend(int cell)
    {
        if (Game.Map.In_Radar(cell) && Can_Enter_Cell(cell) == MoveType.MOVE_TEMP)
        {
            var cellptr = Game.Map[cell];
            var blockage = cellptr.Cell_Techno();
            if (blockage != null && House.Is_Ally(blockage))
            {
                bool old = Game.Special.IsScatter;
                Game.Special.IsScatter = true;
                cellptr.Incoming(0, true);
                Game.Special.IsScatter = old;
            }
        }
    }

    void Force_Scatter(int cell)
    {
        bool old = Game.Special.IsScatter;
        Game.Special.IsScatter = true;
        Game.Map[cell].Incoming(0, true);
        Game.Special.IsScatter = old;
    }

    /// <summary>A destroyable blocker is attacked: an enemy object, or else a wall.</summary>
    void Attack_Blocker(int destcell)
    {
        var occupier = Game.Map[destcell].Cell_Object();
        if (occupier != null)
        {
            if (!House.Is_Ally(occupier))
                Override_Mission(MissionType.MISSION_ATTACK, occupier.As_Target(), Target.TARGET_NONE);
        }
        else if (Game.Map[destcell].Overlay != OverlayType.OVERLAY_NONE && Game.Types.Overlay(Game.Map[destcell].Overlay).IsWall)
        {
            Override_Mission(MissionType.MISSION_ATTACK, Target.As_Target(destcell), Target.TARGET_NONE);
        }
    }

    /// <summary>Start_Of_Move: at a cell's centre — plan a path if needed, turn to face the next
    /// cell, check it can be entered, set the speed for the ground, and pick the track.</summary>
    bool Start_Of_Move()
    {
        FacingType facing = Path[0];

        if (!Game.Target_Legal(NavCom) && facing == FacingType.FACING_NONE)
        {
            IsTurretLockedDown = false;
            Stop_Driver();
            if (Mission == MissionType.MISSION_MOVE)
                Enter_Idle_Mode();
            return false;
        }

        // Moving after a unit: don't plan further than it is.
        if (facing != FacingType.FACING_NONE)
        {
            if (Target.Is_Target_Unit(NavCom) || Target.Is_Target_Infantry(NavCom))
            {
                int dist = TdMath.LeptonToCell(DistanceToTarget(NavCom));
                if (dist < TdDefines.CONQUER_PATH_MAX)
                {
                    Path[dist] = FacingType.FACING_NONE;
                    facing = Path[0];
                }
            }
        }

        if (facing == FacingType.FACING_NONE)
        {
            if (!PathDelay.Expired)
                return false;

            if (!Basic_Path())
            {
                if (DistanceToTarget(NavCom) < 0x0280 && (Mission == MissionType.MISSION_MOVE || Mission == MissionType.MISSION_GUARD_AREA))
                {
                    Assign_Destination(Target.TARGET_NONE);
                }
                else
                {
                    // No path: ask a friend in the way to move, and retry a few times.
                    int ourcell = TdMath.CoordCell(Center_Coord());
                    int navcell = Game.As_Cell(NavCom);
                    int cell = TdMath.CellDistance(ourcell, navcell) < 2
                        ? navcell
                        : TdMath.AdjacentCellOf(ourcell, TdMath.DirFacingType(PrimaryFacing.Current));
                    Nudge_Friend(cell);

                    if (TryTryAgain != 0)
                    {
                        TryTryAgain--;
                    }
                    else
                    {
                        Assign_Destination(Target.TARGET_NONE);
                        if (IsNewNavCom)
                            Game.Sound_Effect(VocType.VOC_SCOLD);
                        IsNewNavCom = false;
                    }
                }
                Stop_Driver();
                TrackNumber = -1;
                IsTurretLockedDown = false;
                return false;
            }

            Nudge_Friend(TdMath.AdjacentCellOf(TdMath.CoordCell(Center_Coord()), Path[0]));
            TryTryAgain = PATH_RETRY;
            facing = Path[0];
        }

        if (Class.IsLockTurret || !Class.IsTurretEquipped)
            IsTurretLockedDown = true;

        uint dest = TdMath.AdjacentCellCoord(Coord, (TdMath.Facing)(int)facing);
        byte dir = (byte)((int)facing << 5);

        int facediff = PrimaryFacing.Difference(dir);
        if (facediff != 0)
        {
            Do_Turn(dir);
            return true;
        }

        int destcell = TdMath.CoordCell(dest);
        Mark(MarkType.MARK_UP);
        MoveType cando = Can_Enter_Cell(destcell, facing);
        Mark(MarkType.MARK_DOWN);

        if (cando != MoveType.MOVE_OK)
        {
            if (Mission == MissionType.MISSION_MOVE && House.IsHuman && DistanceToTarget(NavCom) < 0x0200)
                Assign_Destination(Target.TARGET_NONE);

            if (cando == MoveType.MOVE_TEMP)
                Force_Scatter(destcell);
            if (cando == MoveType.MOVE_CLOAK)
                Game.Map[destcell].Shimmer();

            Stop_Driver();
            if (cando != MoveType.MOVE_MOVING_BLOCK)
                Path[0] = FacingType.FACING_NONE;

            if (cando == MoveType.MOVE_DESTROYABLE)
                Attack_Blocker(destcell);
            else if (IsNewNavCom)
                Game.Sound_Effect(VocType.VOC_SCOLD);
            IsNewNavCom = false;
            TrackNumber = -1;
            return true;
        }

        // Speed for the ground; damaged units are slower, and they slow down near the end.
        LandType ground = Game.Map[destcell].Land_Type();
        int speed = Game.Types.Ground(ground).Cost[(int)Class.Speed];
        if (speed == 0)
            speed = 128;
        if ((Class.MaxStrength >> 1) > Strength)
            speed -= speed >> 2;
        if (speed != Speed)
            Set_Speed(speed);

        // As the original: this slowed speed is worked out but not used.
        int distNav = DistanceToTarget(NavCom);
        if (distNav < 0x0200)
            speed = (int)TdMath.FixedToCardinal((uint)speed, 0x00A0);
        else if (distNav < 0x0700)
            speed = (int)TdMath.FixedToCardinal((uint)speed, 0x00D0);

        if (cando != MoveType.MOVE_OK)
        {
            Path[0] = FacingType.FACING_NONE;
            TrackNumber = -1;
            dest = 0;
        }
        else
        {
            Overrun_Square(TdMath.CoordCell(dest), true);

            FacingType nextface = Path[1];
            if (nextface == FacingType.FACING_NONE)
                nextface = facing;

            IsOnShortTrack = false;
            TrackNumber = (int)facing * (int)FacingType.FACING_COUNT + (int)nextface;
            if (TrackControl[TrackNumber].Track == 0)
            {
                Path[0] = FacingType.FACING_NONE;
                TrackNumber = -1;
                return true;
            }

            if ((TrackControl[TrackNumber].Flag & TrackControlType.F_D) != 0)
            {
                // A diagonal track covers two cells: check the second one too.
                if (!Game.Map[destcell].Goodie_Check(this))
                {
                    cando = MoveType.MOVE_NO;
                }
                else
                {
                    dest = TdMath.AdjacentCellCoord(dest, (TdMath.Facing)(int)nextface);
                    destcell = TdMath.CoordCell(dest);
                    cando = Can_Enter_Cell(destcell);
                }

                if (cando != MoveType.MOVE_OK)
                {
                    if (cando == MoveType.MOVE_TEMP)
                        Force_Scatter(destcell);
                    if (cando == MoveType.MOVE_CLOAK)
                        Game.Map[destcell].Shimmer();

                    Path[0] = FacingType.FACING_NONE;
                    TrackNumber = -1;
                    dest = 0;
                    if (cando == MoveType.MOVE_DESTROYABLE)
                    {
                        Attack_Blocker(destcell);
                        IsNewNavCom = false;
                        TrackIndex = 0;
                        return true;
                    }
                }
                else
                {
                    Array.Copy(Path, 2, Path, 0, TdDefines.CONQUER_PATH_MAX - 2);
                    Path[TdDefines.CONQUER_PATH_MAX - 2] = FacingType.FACING_NONE;
                    IsPlanningToLook = true;
                }
            }
            else
            {
                Array.Copy(Path, 1, Path, 0, TdDefines.CONQUER_PATH_MAX - 1);
            }
            Path[TdDefines.CONQUER_PATH_MAX - 1] = FacingType.FACING_NONE;
        }

        IsNewNavCom = false;
        TrackIndex = 0;
        if (!Start_Driver(ref dest))
        {
            TrackNumber = -1;
            Path[0] = FacingType.FACING_NONE;
            Set_Speed(0);
        }
        return false;
    }

    public override void AI()
    {
        base.AI();

        if (TrackNumber != -1)
        {
            While_Moving();
            if (!IsActive)
                return;
            if (TrackNumber == -1 && (Game.Target_Legal(NavCom) || Path[0] != FacingType.FACING_NONE))
            {
                Start_Of_Move();
                While_Moving();
                if (!IsActive)
                    return;
            }
        }
        else
        {
            // Units that turn on the spot.
            if ((Class.Speed == SpeedType.SPEED_FLOAT || Class.Speed == SpeedType.SPEED_HOVER || Class.Speed == SpeedType.SPEED_TRACK ||
                 (Class.Speed == SpeedType.SPEED_WHEEL && !Game.Special.IsThreePoint)) && PrimaryFacing.Is_Rotating())
            {
                if (PrimaryFacing.Rotation_Adjust((int)(Class.ROT * House.GroundspeedBias)))
                    Mark(MarkType.MARK_CHANGE);

                if (!IsRotating)
                {
                    Per_Cell_Process(true);
                    if (!IsActive)
                        return;
                }
            }
            else if (Mission != MissionType.MISSION_GUARD || NavCom != Target.TARGET_NONE)
            {
                if (Game.Target_Legal(NavCom) || Path[0] != FacingType.FACING_NONE)
                {
                    Start_Of_Move();
                    While_Moving();
                    if (!IsActive)
                        return;
                }
                else
                {
                    Stop_Driver();
                }
            }
        }
    }

    static readonly int[,] ThreePointPath =
    {
        { 2, 0, 2, 0, 0, 0 },
        { 3, 0, 2, 2, 0, 0 },
        { 4, 0, 2, 2, 0, 0 },
        { 4, 0, 2, 2, 1, 0 },
    };

    static readonly int[,] ThreePointDiagonalPath =
    {
        { 0, 0, 0, 0, 0, 0 },
        { 3, 0, 2, 2, 0, 0 },
        { 4, 0, 2, 2, 1, 0 },
        { 5, 0, 2, 2, 1, 0 },
    };

    /// <summary>Fixup_Path: with three-point turns, wheeled units start the path with the turn's moves.</summary>
    public override void Fixup_Path(PathType path)
    {
        var stage = new FacingType[6];

        if (path == null || path.Command[0] == FacingType.FACING_NONE)
            return;
        if (!Game.Special.IsThreePoint || Class.Speed != SpeedType.SPEED_WHEEL)
            return;

        int facediff = PrimaryFacing.Difference((byte)((int)path.Command[0] << 5)) >> 5;
        if (facediff == 0)
            return;

        int row = Math.Abs(facediff) - (int)FacingType.FACING_NE;
        int[,] table = ((int)TdMath.DirFacingType(PrimaryFacing.Current) & (int)FacingType.FACING_NE) != 0 ? ThreePointDiagonalPath : ThreePointPath;
        int counter = table[row, 0];

        bool ok = true;
        int cell = TdMath.CoordCell(Coord);
        FacingType nextpath = TdMath.DirFacingType(PrimaryFacing.Current);
        int index;
        for (index = 0; index < counter; index++)
        {
            int step = table[row, 1 + index];
            nextpath = facediff > 0 ? TdMath.FacingAdd(nextpath, step) : TdMath.FacingSub(nextpath, step);
            stage[index] = nextpath;
            cell = TdMath.AdjacentCellOf(cell, nextpath);
            if (Can_Enter_Cell(cell, nextpath) != MoveType.MOVE_OK)
            {
                ok = false;
                break;
            }
        }

        // A full reversal may turn the other way instead.
        if (!ok && Math.Abs(facediff) == 4)
        {
            facediff = -facediff;
            ok = true;
            cell = TdMath.CoordCell(Coord);
            nextpath = TdMath.DirFacingType(PrimaryFacing.Current);
            for (index = 0; index < counter; index++)
            {
                int step = table[row, 1 + index];
                nextpath = facediff > 0 ? TdMath.FacingAdd(nextpath, step) : TdMath.FacingSub(nextpath, step);
                stage[index] = nextpath;
                cell = TdMath.CoordCell(TdMath.AdjacentCellCoord(TdMath.CellCoord(cell), (TdMath.Facing)(int)nextpath));
                if (Can_Enter_Cell(cell, nextpath) != MoveType.MOVE_OK)
                {
                    ok = false;
                    break;
                }
            }
        }

        if (ok)
        {
            if (path.Length <= 1)
            {
                Array.Copy(stage, 0, path.Command, 0, Math.Max(counter, 1));
                path.Length = counter;
            }
            else
            {
                if (counter != 0)
                {
                    counter--;
                    path.Command[0] = stage[counter];
                    Optimize_Moves(path, MoveType.MOVE_OK);
                }
                if (counter != 0)
                {
                    Array.Copy(path.Command, 0, path.Command, counter, 40 - counter);
                    Array.Copy(stage, 0, path.Command, 0, counter);
                    path.Length += counter;
                }
            }
            path.Command[path.Length] = FacingType.FACING_NONE;
        }
    }

    /// <summary>Lay_Track: tracks in the sand are not drawn in this version.</summary>
    void Lay_Track()
    {
    }

    /// <summary>Mark_Track: flags the cells of the track being driven as holding a vehicle.</summary>
    public void Mark_Track(uint headto, MarkType type)
    {
        bool value = type != MarkType.MARK_UP;

        if (headto != 0)
        {
            if (!IsOnShortTrack && TrackNumber != -1)
            {
                // The cell the track passes through on its way (a diagonal's middle cell).
                int tracknum = TrackControl[TrackNumber].Track;
                if (tracknum != 0)
                {
                    TrackType[] ptr = RawTracks[tracknum - 1].Track;
                    int cellidx = RawTracks[tracknum - 1].Cell;
                    if (cellidx > -1)
                    {
                        byte dir = ptr[cellidx].Facing;
                        if (TrackIndex < cellidx && cellidx != -1)
                        {
                            uint offset = Smooth_Turn(ptr[cellidx].Offset, ref dir);
                            int c = TdMath.CoordCell(offset);
                            if ((uint)c < TdDefines.MAP_CELL_TOTAL)
                                Game.Map[c].Flag.Occupy.Vehicle = value;
                        }
                    }
                }
            }
            int cell = TdMath.CoordCell(headto);
            if ((uint)cell < TdDefines.MAP_CELL_TOTAL)
                Game.Map[cell].Flag.Occupy.Vehicle = value;
        }
    }

    /// <summary>Offload_Tiberium_Bail: one step of the load as credits (the computer gets a third more).</summary>
    public override int Offload_Tiberium_Bail()
    {
        if (Tiberium != 0)
        {
            Tiberium--;
            if (House.IsHuman)
                return UnitTypeClass.FULL_LOAD_CREDITS / UnitTypeClass.STEP_COUNT;
            // As the original: in campaigns the precedence makes this a whole load plus a little.
            if (Game.GameToPlay == GameType.GAME_NORMAL)
                return UnitTypeClass.FULL_LOAD_CREDITS + (UnitTypeClass.FULL_LOAD_CREDITS / 3) / UnitTypeClass.STEP_COUNT;
            return (UnitTypeClass.FULL_LOAD_CREDITS + (UnitTypeClass.FULL_LOAD_CREDITS / 3)) / UnitTypeClass.STEP_COUNT;
        }
        return 0;
    }

    public virtual bool Ok_To_Move(byte dir) => true;
}

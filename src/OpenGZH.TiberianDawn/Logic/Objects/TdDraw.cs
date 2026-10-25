namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>How a layer is drawn (the CC_Draw_Shape flags that matter to the client).</summary>
[Flags]
public enum TdShapeFlags
{
    None = 0,
    /// <summary>SHAPE_PREDATOR|SHAPE_FADING: an aircraft's shadow on the ground.</summary>
    Shadow = 1,
    /// <summary>SHAPE_GHOST: drawn translucent (a parked helicopter's rotor).</summary>
    Ghost = 2,
    /// <summary>Drawn in the house's colours.</summary>
    Remap = 4,
}

/// <summary>One shape to draw for an object: which art file (null = the object's own), which frame,
/// and the pixel offset from the object's screen position (centred, as SHAPE_CENTER).</summary>
public readonly record struct TdShapeLayer(string? File, int Frame, int Dx, int Dy, TdShapeFlags Flags = TdShapeFlags.Remap);

/// <summary>
/// The shape choices of UnitClass::Draw_It and AircraftClass::Draw_It (UNIT.CPP / AIRCRAFT.CPP),
/// split from the drawing itself: the logic decides frames, the client puts them on screen.
/// </summary>
public static class TdDraw
{
    static readonly (sbyte X, sbyte Y)[] RecoilAdjust =
    [
        (0, 1), (0, 1), (0, 1), (-1, 1), (-1, 1), (-1, 1), (-1, 0), (-1, 0),
        (-1, 0), (-1, 0), (-1, -1), (-1, -1), (-1, -1), (-1, -1), (-1, -1), (0, -1),
        (0, -1), (0, -1), (0, -1), (1, -1), (1, -1), (1, -1), (1, 0), (1, 0),
        (1, 0), (1, 0), (1, 1), (1, 1), (1, 1), (1, 1), (0, 1), (0, 1),
    ];

    static readonly (sbyte X, sbyte Y)[] TurretAdjust =
    [
        (1, 2), (-1, 1), (-2, 0), (-3, 0), (-3, 1), (-4, -1), (-4, -1), (-5, -2),
        (-5, -3), (-5, -3), (-3, -3), (-3, -4), (-3, -4), (-3, -5), (-2, -5), (-1, -5),
        (0, -5), (1, -6), (2, -5), (3, -5), (4, -5), (6, -4), (6, -3), (6, -3),
        (6, -3), (5, -1), (5, -1), (4, 0), (3, 0), (2, 0), (2, 1), (1, 2),
    ];

    static void Recoil(byte dir, ref int x, ref int y)
    {
        var a = RecoilAdjust[TdMath.Facing32[dir]];
        x += a.X;
        y += a.Y;
    }

    static void Turret(byte dir, ref int x, ref int y)
    {
        var a = TurretAdjust[TdMath.Facing32[dir]];
        x += a.X;
        y += a.Y;
    }

    static readonly int[] HarvestStage = [0, 1, 2, 3, 2, 1];

    /// <summary>UnitClass::Draw_It: the body (or harvesting, APC door, dinosaur, gunboat damage
    /// frames), the gunboat's wake, the radar dish, and the turret.</summary>
    public static void Unit_Layers(UnitClass u, List<TdShapeLayer> layers)
    {
        var cls = u.Class;
        int facing = TdMath.Facing32[u.PrimaryFacing.Current];
        int tfacing = TdMath.Facing32[u.SecondaryFacing.Current];
        int shapenum;
        int shapestart;
        int xx = 0, yy = 0;

        if (cls.IsPieceOfEight)
            facing = (int)TdMath.DirFacing(u.PrimaryFacing.Current);

        switch (TdMath.DirFacing(u.PrimaryFacing.Current))
        {
            case TdMath.Facing.NE:
            case TdMath.Facing.E:
            case TdMath.Facing.SE:
                shapenum = TdMath.BodyShape[tfacing] + 96;
                shapestart = 0;
                break;
            default:
                shapenum = TdMath.BodyShape[tfacing];
                shapestart = 6;
                xx += 4;
                break;
        }

        if (cls.IsChunkyShape)
        {
            if (cls.Type == UnitType.UNIT_GUNBOAT)
            {
                layers.Add(new TdShapeLayer("WAKE", shapestart + (u.Stage.Fetch_Stage() % 6), xx - 1, yy + 3, TdShapeFlags.None));
                if (u.Health_Ratio() < 0x0080) shapenum += 32;
                if (u.Health_Ratio() < 0x0040) shapenum += 32;
            }
            else
            {
                shapenum = 0;
            }
        }
        else if (u.IsHarvesting && !u.PrimaryFacing.Is_Rotating() && u.NavCom == Target.TARGET_NONE && !u.IsDriving)
        {
            shapenum = 32 + (((TdMath.BodyShape[facing] + 2) / 4) * 4) + HarvestStage[u.Stage.Fetch_Stage() % HarvestStage.Length];
        }
        else
        {
            shapenum = TdMath.BodyShape[facing];
            if (cls.IsAnimating)
                shapenum = u.Stage.Fetch_Stage();
            if (cls.IsPieceOfEight)
            {
                shapenum = 0;
                int numshapes = u.IsDriving || cls.Type == UnitType.UNIT_TREX || cls.Type == UnitType.UNIT_RAPT ? 8 : 12;
                if (facing != 0) shapenum = TdMath.BodyShape[24 + facing];
                if (u.IsDriving) shapenum = (u.Stage.Fetch_Stage() % numshapes) + 16 + (shapenum * numshapes);
                else if (u.IsFiring) shapenum = (u.Stage.Fetch_Stage() % numshapes) + 80 + (shapenum * numshapes);
            }
            else if (!u.Door.Is_Door_Closed() && (u.PrimaryFacing.Current == (byte)DirType.DIR_NW || u.PrimaryFacing.Current == (byte)DirType.DIR_NE))
            {
                shapenum = u.PrimaryFacing.Current == (byte)DirType.DIR_NE ? 32 : 35;
                shapenum += u.Door.Door_Stage();
            }
        }

        int x = 0, y = 0;
        if (cls.Type == UnitType.UNIT_ARTY && u.IsInRecoilState)
            Recoil(u.PrimaryFacing.Current, ref x, ref y);
        layers.Add(new TdShapeLayer(null, shapenum, x, y));

        if (cls.IsRadarEquipped)
            layers.Add(new TdShapeLayer(null, 32 + (int)(u.Game.Frame % 32), x, y - 5));

        if (!cls.IsChunkyShape && cls.IsTurretEquipped)
        {
            int x1 = x, y1 = y;
            shapenum = TdMath.BodyShape[tfacing] + 32;
            if (cls.Type == UnitType.UNIT_MSAM)
            {
                if (u.Ammo == 0) shapenum += 64;
                if (u.Ammo == 1) shapenum += 32;
            }
            if (u.IsInRecoilState)
                Recoil(u.SecondaryFacing.Current, ref x1, ref y1);
            if (cls.Type == UnitType.UNIT_MSAM || cls.Type == UnitType.UNIT_MLRS)
                Turret(u.PrimaryFacing.Current, ref x1, ref y1);
            if (cls.Type == UnitType.UNIT_JEEP || cls.Type == UnitType.UNIT_BUGGY)
                y1 -= 4;
            layers.Add(new TdShapeLayer(null, shapenum, x1, y1));
        }
    }

    static readonly int[] RotorStretch = [8, 9, 10, 9, 8, 9, 10, 9];
    static readonly int[] JitterTable = [0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0, -1, -1, -1, 0];

    /// <summary>AircraftClass::Draw_It: the shadow, the body (lifted by its altitude, helicopters
    /// bobbing), and the rotors (ghostly when parked). Returns false when not drawn at all.</summary>
    public static bool Aircraft_Layers(AircraftClass a, List<TdShapeLayer> layers)
    {
        if (a.Is_Waiting_Off_Map())
            return false;
        var cls = a.Class;
        int facing = TdMath.Facing32[a.SecondaryFacing.Current];
        int shapenum = TdMath.BodyShape[facing];
        if (cls.Type == AircraftType.AIRCRAFT_ORCA && a.Fly.Get_Speed() >= MPHType.MPH_MEDIUM_FAST)
            shapenum += 32;
        if (!a.Door.Is_Door_Closed())
            shapenum = 32 + a.Door.Door_Stage();

        int jitter = 0;
        if (a.Altitude == AircraftClass.FLIGHT_LEVEL && !cls.IsFixedWing)
        {
            a.Jitter++;
            jitter = JitterTable[a.Jitter % 16];
        }

        if (a.Visual_Character() <= VisualType.VISUAL_DARKEN)
            layers.Add(new TdShapeLayer(null, shapenum, 1, 2, TdShapeFlags.Shadow));
        layers.Add(new TdShapeLayer(null, shapenum, 0, -a.Altitude + jitter));

        if (cls.IsRotorEquipped)
        {
            TdShapeFlags flags;
            if (a.Altitude == 0)
            {
                shapenum = (a.Stage.Fetch_Stage() % 8) + 4;
                flags = TdShapeFlags.Ghost;
            }
            else
            {
                shapenum = a.Stage.Fetch_Stage() % 4;
                flags = TdShapeFlags.Shadow;
            }
            if (cls.Type == AircraftType.AIRCRAFT_TRANSPORT)
            {
                int face = (int)TdMath.DirFacing(a.SecondaryFacing.Current);
                short xx = 0, yy = (short)-a.Altitude;
                TdMath.MovePoint(ref xx, ref yy, a.SecondaryFacing.Current, RotorStretch[face]);
                layers.Add(new TdShapeLayer("RROTOR", shapenum, xx, yy - 2, flags));
                TdMath.MovePoint(ref xx, ref yy, (byte)(a.SecondaryFacing.Current + (int)DirType.DIR_S), RotorStretch[face] * 2);
                layers.Add(new TdShapeLayer("LROTOR", shapenum, xx, yy - 2, flags));
            }
            else
            {
                layers.Add(new TdShapeLayer("RROTOR", shapenum, 0, -a.Altitude - 2, flags));
            }
        }
        return true;
    }
}

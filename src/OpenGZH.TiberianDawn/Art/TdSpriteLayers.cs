using OpenGZH.TiberianDawn.Logic;
using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Art;

/// <summary>What a Tiberian Dawn object is doing, as another engine sees it (for choosing its frames).</summary>
public readonly record struct TdSpriteState
{
    /// <summary>TD direction (0 = up the screen, 64 = right; 256 steps clockwise) of the body and the turret.</summary>
    public byte Dir { get; init; }
    public byte TurretDir { get; init; }
    /// <summary>Health as TD's Health_Ratio (256 = full).</summary>
    public int HealthRatio { get; init; }
    /// <summary>Seconds since the object appeared (drives the TD animation stages at 15 ticks a second).</summary>
    public float Age { get; init; }
    public bool Moving { get; init; }
    /// <summary>Fired within the last few ticks (recoil, infantry firing).</summary>
    public bool Firing { get; init; }
    public bool Harvesting { get; init; }
    public bool Dying { get; init; }
    /// <summary>Height above the ground in TD pixels (aircraft).</summary>
    public int Altitude { get; init; }
    /// <summary>Missiles left (the MSAM / SAM site art shows them).</summary>
    public int Ammo { get; init; }
}

/// <summary>
/// The frames TD's Draw_It and Shape_Number functions pick (BUILDING.CPP, UNIT.CPP, INFANTRY.CPP,
/// AIRCRAFT.CPP), from a <see cref="TdSpriteState"/> instead of TD's object classes, so TD factions in
/// Zero Hour engine matches look as they do in TD. The animation "stage" is the age in TD ticks over the
/// sequence's rate. The layers' offsets are TD pixels from the object's centre (SHAPE_CENTER).
/// </summary>
public static class TdSpriteLayers
{
    public const int TicksPerSecond = 15;

    static int Ticks(TdSpriteState s) => (int)(s.Age * TicksPerSecond);

    static int Stage(TdSpriteState s, int rate) => rate <= 0 ? 0 : Ticks(s) / rate;

    /// <summary>A building: the build-up (NAMEMAKE) while new, then BuildingClass::Shape_Number.</summary>
    public static void Building(BuildingTypeClass b, TdSpriteState s, int buildupFrames, int imageFrames, List<TdShapeLayer> layers)
    {
        // The build-up animation plays once, a frame a tick (TD's BSTATE_CONSTRUCTION).
        if (buildupFrames > 0 && Ticks(s) < buildupFrames)
        {
            layers.Add(new TdShapeLayer(b.IniName + "MAKE", Ticks(s), 0, 0));
            return;
        }
        var idle = b.Anim(BStateType.BSTATE_IDLE);
        var active = b.Anim(BStateType.BSTATE_ACTIVE);
        int shapenum = idle.Start + (idle.Count > 1 ? Stage(s, idle.Rate) % idle.Count : 0);

        if (b.Type == StructType.STRUCT_OBELISK)
            shapenum = s.Firing ? 3 : 0;

        if (b.IsTurretEquipped)
        {
            shapenum = TdMath.BodyShape[TdMath.Facing32[s.TurretDir]];
            if (b.Type == StructType.STRUCT_SAM)
                shapenum += 16; // raised and ready (TD lowers it when idle; ZH has no such state)
            else if (s.Firing)
                shapenum += 32;
            if (s.HealthRatio < 0x0080)
                shapenum += 64;
        }
        else if (b.Type == StructType.STRUCT_WEAP)
        {
            shapenum = s.HealthRatio < 0x0080 ? 1 : 0;
        }
        else if (b.Type == StructType.STRUCT_STORAGE)
        {
            if (s.HealthRatio < 0x0080)
                shapenum += 5;
        }
        else if (s.HealthRatio < 0x0080)
        {
            if (!b.IsSimpleDamage)
            {
                var aux1 = b.Anim(BStateType.BSTATE_AUX1);
                var aux2 = b.Anim(BStateType.BSTATE_AUX2);
                int largest = Math.Max(idle.Start + idle.Count, active.Start + active.Count);
                largest = Math.Max(largest, aux1.Start + aux1.Count);
                largest = Math.Max(largest, aux2.Start + aux2.Count);
                shapenum += largest;
            }
            else
            {
                shapenum = imageFrames - 2;
            }
        }
        layers.Add(new TdShapeLayer(null, shapenum, 0, 0));

        // The war factory's door and roof (WEAP2) go over the body.
        if (b.Type == StructType.STRUCT_WEAP)
            layers.Add(new TdShapeLayer("WEAP2", s.HealthRatio < 0x0080 ? 10 : 0, 0, 0));
    }

    static readonly int[] HarvestStage = [0, 1, 2, 3, 2, 1];

    /// <summary>A vehicle: UnitClass::Draw_It's body, harvesting, radar and turret frames.</summary>
    public static void Unit(UnitTypeClass cls, TdSpriteState s, List<TdShapeLayer> layers)
    {
        int facing = TdMath.Facing32[s.Dir];
        int tfacing = TdMath.Facing32[s.TurretDir];
        int shapenum;
        if (cls.IsChunkyShape)
            shapenum = 0;
        else if (s.Harvesting && !s.Moving)
            shapenum = 32 + (((TdMath.BodyShape[facing] + 2) / 4) * 4) + HarvestStage[Stage(s, 2) % HarvestStage.Length];
        else
        {
            shapenum = TdMath.BodyShape[facing];
            if (cls.IsAnimating)
                shapenum = Stage(s, 2);
            if (cls.IsPieceOfEight)
            {
                int numshapes = s.Moving ? 8 : 12;
                int f8 = facing != 0 ? TdMath.BodyShape[24 + facing] : 0;
                shapenum = f8;
                if (s.Moving) shapenum = (Stage(s, 2) % numshapes) + 16 + f8 * numshapes;
                else if (s.Firing) shapenum = (Stage(s, 2) % numshapes) + 80 + f8 * numshapes;
            }
        }
        layers.Add(new TdShapeLayer(null, shapenum, 0, 0));

        if (cls.IsRadarEquipped)
            layers.Add(new TdShapeLayer(null, 32 + Ticks(s) % 32, 0, -5));

        if (!cls.IsChunkyShape && cls.IsTurretEquipped)
        {
            int y1 = 0;
            shapenum = TdMath.BodyShape[tfacing] + 32;
            if (cls.Type == UnitType.UNIT_MSAM)
            {
                if (s.Ammo == 0) shapenum += 64;
                if (s.Ammo == 1) shapenum += 32;
            }
            if (cls.Type == UnitType.UNIT_JEEP || cls.Type == UnitType.UNIT_BUGGY)
                y1 -= 4;
            layers.Add(new TdShapeLayer(null, shapenum, 0, y1));
        }
    }

    /// <summary>A soldier: InfantryClass::Shape_Number with the DoControls of what it is doing.</summary>
    public static void Infantry(InfantryTypeClass cls, TdSpriteState s, List<TdShapeLayer> layers)
    {
        var doit = s.Dying ? DoType.DO_GUN_DEATH : s.Firing ? DoType.DO_FIRE_WEAPON : s.Moving ? DoType.DO_WALK : DoType.DO_STAND_READY;
        // INFANTRY.CPP's MasterDoControls rates (TD ticks a frame).
        int rate = doit switch { DoType.DO_WALK => 2, DoType.DO_FIRE_WEAPON => 1, DoType.DO_GUN_DEATH => 2, _ => 0 };
        var control = cls.DoControl(doit);
        int count = Math.Max(control.Count, 1);
        int stage = Stage(s, rate);
        int shapenum = s.Dying ? Math.Min(stage, count - 1) : stage % count;
        if (control.Jump != 0)
            shapenum += InfantryClass.HumanShape[TdMath.Facing32[s.Dir]] * control.Jump;
        shapenum += control.Frame;
        layers.Add(new TdShapeLayer(null, shapenum, 0, 0));
    }

    /// <summary>An aircraft: AircraftClass::Draw_It's shadow, body (lifted by its altitude) and rotors.</summary>
    public static void Aircraft(AircraftTypeClass cls, TdSpriteState s, List<TdShapeLayer> layers)
    {
        int shapenum = TdMath.BodyShape[TdMath.Facing32[s.Dir]];
        if (cls.Type == AircraftType.AIRCRAFT_ORCA && s.Moving)
            shapenum += 32;
        layers.Add(new TdShapeLayer(null, shapenum, 1, 2 + s.Altitude, TdShapeFlags.Shadow));
        layers.Add(new TdShapeLayer(null, shapenum, 0, 0));
        if (cls.IsRotorEquipped)
        {
            // TD's in-flight frames (0-3) are faint blurs (frame 0 is empty) meant for its predator effect;
            // the blade frames (4-11, TD's landed ones) are used throughout so the rotors stay visible.
            int rotor = (Stage(s, 1) % 8) + 4;
            // The blades are drawn solid: TD's see-through ghost and predator effects made them nearly vanish
            // over the house-coloured body at Zero Hour's zoom.
            var flags = TdShapeFlags.None;
            if (cls.Type == AircraftType.AIRCRAFT_TRANSPORT)
            {
                // The Chinook's two rotors, front and back along its facing (MovePoint by RotorStretch).
                int stretch = RotorStretch[(s.Dir + 16) / 32 % 8];
                var (fx, fy) = Along(s.Dir, stretch);
                layers.Add(new TdShapeLayer("RROTOR", rotor, fx, fy - 2, flags));
                layers.Add(new TdShapeLayer("LROTOR", rotor, -fx, -fy - 2, flags));
            }
            else
                layers.Add(new TdShapeLayer("RROTOR", rotor, 0, -2, flags));
        }
    }

    static readonly int[] RotorStretch = [8, 9, 10, 9, 8, 9, 10, 9];

    /// <summary>Move_Point: <paramref name="distance"/> pixels in a TD direction (0 = up the screen, clockwise).</summary>
    static (int X, int Y) Along(byte dir, int distance)
    {
        double a = dir / 256.0 * 2 * Math.PI;
        return ((int)Math.Round(Math.Sin(a) * distance), (int)Math.Round(-Math.Cos(a) * distance));
    }

    /// <summary>A Zero Hour angle (radians, counter-clockwise from +X) as a TD direction on a screen whose "up" is <paramref name="screenUp"/> radians.</summary>
    public static byte Dir(float angle, float screenUp)
    {
        float clockwise = screenUp - angle; // TD turns clockwise from screen-up
        float steps = clockwise / (2 * MathF.PI) * 256;
        return (byte)((int)MathF.Round(steps) & 0xFF);
    }
}

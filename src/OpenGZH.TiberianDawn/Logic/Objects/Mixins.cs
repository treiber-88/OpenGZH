namespace OpenGZH.TiberianDawn.Logic.Objects;

// The small classes TechnoClass inherits from in the original (FLASHER, STAGE, CARGO, DOOR, CREW,
// FACING). C# has no multiple inheritance, so TechnoClass holds one of each.

/// <summary>FlasherClass: makes an object flash (white) for a number of frames, per player too.</summary>
public sealed class FlasherClass
{
    public int FlashCount;
    public bool IsBlushing;
    public readonly int[] FlashCountPerPlayer = new int[(int)HousesType.HOUSE_COUNT];

    public bool Process()
    {
        for (int i = 0; i < FlashCountPerPlayer.Length; i++)
            if (FlashCountPerPlayer[i] != 0)
                FlashCountPerPlayer[i]--;
        if (FlashCount != 0)
        {
            FlashCount--;
            IsBlushing = (FlashCount & 0x01) != 0;
            return true;
        }
        return false;
    }

    public int Get_Flashing_Flags()
    {
        int flags = 0;
        for (int i = 0; i < FlashCountPerPlayer.Length; i++)
            if (FlashCountPerPlayer[i] > 0)
                flags |= 1 << i;
        return flags;
    }
}

/// <summary>StageClass: an animation stage that advances every Rate frames.</summary>
public sealed class StageClass
{
    int stage;
    byte stageTimer, rate;

    public int Fetch_Stage() => stage;
    public int Fetch_Rate() => rate;
    public void Set_Stage(int value) => stage = value;
    public void Set_Rate(int value) => rate = stageTimer = (byte)value;

    /// <summary>Graphic_Logic: true when the stage advanced this frame.</summary>
    public bool Graphic_Logic()
    {
        if (rate != 0)
        {
            stageTimer--;
            if (stageTimer == 0)
            {
                stage++;
                stageTimer = rate;
                return true;
            }
        }
        return false;
    }
}

/// <summary>CargoClass: the passengers a transport carries, as a linked list through Next.</summary>
public sealed class CargoClass
{
    int quantity;
    FootClass? cargoHold;

    public int How_Many() => quantity;
    public bool Is_Something_Attached() => cargoHold != null;
    public FootClass? Attached_Object() => Is_Something_Attached() ? cargoHold : null;

    /// <summary>Attach: the object (and anything linked behind it) goes into the hold, in front.</summary>
    public void Attach(FootClass? obj)
    {
        if (obj == null)
            return;
        obj.Limbo();
        ObjectClass? o = obj.Next;
        while (o != null)
        {
            if (o.Next == null)
                break;
            o = o.Next;
        }
        if (o != null)
            o.Next = cargoHold;
        else
            obj.Next = cargoHold;
        cargoHold = obj;
        quantity = 0;
        for (ObjectClass? item = cargoHold; item != null; item = item.Next)
            quantity++;
    }

    public FootClass? Detach_Object()
    {
        var unit = Attached_Object();
        if (unit != null)
        {
            cargoHold = unit.Next as FootClass;
            unit.Next = null;
            quantity--;
        }
        return unit;
    }
}

/// <summary>DoorClass: a door that opens and closes over a number of animation stages.</summary>
public sealed class DoorClass
{
    enum DoorState { IsClosed, IsOpening, IsOpen, IsClosing }

    readonly StageClass control = new();
    int stages;
    DoorState state = DoorState.IsClosed;
    bool isToRedraw;

    public bool Time_To_Redraw() => isToRedraw;
    public void Clear_Redraw_Flag() => isToRedraw = false;
    public bool Is_Door_Opening() => state == DoorState.IsOpening;
    public bool Is_Door_Closing() => state == DoorState.IsClosing;
    public bool Is_Door_Open() => state == DoorState.IsOpen;
    public bool Is_Door_Closed() => state == DoorState.IsClosed;

    public void AI()
    {
        if (control.Graphic_Logic())
        {
            if (control.Fetch_Stage() >= stages)
            {
                control.Set_Rate(0);
                if (state == DoorState.IsOpening)
                    state = DoorState.IsOpen;
                else if (state == DoorState.IsClosing)
                    state = DoorState.IsClosed;
            }
            isToRedraw = true;
        }
    }

    public bool Open_Door(int rate, int stageCount)
    {
        if (state is DoorState.IsClosed or DoorState.IsClosing)
        {
            state = DoorState.IsOpening;
            stages = stageCount - 1;
            control.Set_Stage(0);
            control.Set_Rate(rate);
            return true;
        }
        return false;
    }

    public bool Close_Door(int rate, int stageCount)
    {
        if (state is DoorState.IsOpen or DoorState.IsOpening)
        {
            state = DoorState.IsClosing;
            stages = stageCount - 1;
            control.Set_Stage(0);
            control.Set_Rate(rate);
            return true;
        }
        return false;
    }

    /// <summary>Door_Stage: the frame of the door to draw (as the original computes it).</summary>
    public int Door_Stage() => state switch
    {
        DoorState.IsClosing => (stages - 1) - control.Fetch_Stage(),
        DoorState.IsClosed => 0,
        DoorState.IsOpening => control.Fetch_Stage(),
        DoorState.IsOpen => stages - 1,
        _ => 0,
    };
}

/// <summary>CrewClass: the kill count of a crewed object.</summary>
public sealed class CrewClass
{
    public int Kills;
    public int Made_A_Kill() => ++Kills;
}

/// <summary>FacingClass: a current and a desired direction, turned at a rate.</summary>
public sealed class FacingClass
{
    byte current, desired;

    public FacingClass(byte dir = 0) => current = desired = dir;

    public byte Current => current;
    public byte Desired => desired;

    public bool Set_Desired(byte facing)
    {
        if (desired != facing)
        {
            desired = facing;
            return true;
        }
        return false;
    }

    public bool Set_Current(byte facing)
    {
        if (current != facing)
        {
            current = facing;
            return true;
        }
        return false;
    }

    /// <summary>The original's FacingClass assignment: both facings copied.</summary>
    public void Copy_From(FacingClass other)
    {
        current = other.current;
        desired = other.desired;
    }

    public void Set(byte facing)
    {
        Set_Current(facing);
        Set_Desired(facing);
    }

    public bool Is_Rotating() => desired != current;
    public int Difference() => (sbyte)(byte)(desired - current);
    public int Difference(byte facing) => (sbyte)(byte)((sbyte)facing - (sbyte)current);

    /// <summary>Rotation_Adjust: turns towards the desired facing; true if the 32-step frame changed.</summary>
    public bool Rotation_Adjust(int rate)
    {
        if (Is_Rotating())
        {
            rate = Math.Min(rate, 127);
            byte old = current;
            int diff = Difference();
            if (Math.Abs(diff) < rate)
                current = desired;
            else if (diff < 0)
                current = (byte)(current - (byte)rate);
            else
                current = (byte)(current + (byte)rate);
            return TdMath.Facing32[current] != TdMath.Facing32[old];
        }
        return false;
    }
}

/// <summary>ImpactType (FLY.H): what moving through the air did this frame.</summary>
public enum ImpactType : byte { IMPACT_NONE, IMPACT_NORMAL, IMPACT_EDGE }

/// <summary>FlyClass (FLY.CPP): straight-line flight at a speed, a pixel step at a time.</summary>
public sealed class FlyClass
{
    uint speedAccum;
    MPHType speedAdd = MPHType.MPH_IMMOBILE;

    public MPHType Get_Speed() => speedAdd;

    /// <summary>Fly_Speed: the speed as a fraction (0..255) of the maximum.</summary>
    public void Fly_Speed(int speed, MPHType maximum) => speedAdd = (MPHType)TdMath.FixedToCardinal((uint)maximum, (uint)speed);

    /// <summary>Physics: moves the coordinate along the facing; stops at the map's edge.</summary>
    public ImpactType Physics(ref uint coord, byte facing)
    {
        if (speedAdd != MPHType.MPH_IMMOBILE)
        {
            int actual = (int)speedAdd + (int)speedAccum;
            int pixel = (int)TdDefines.PIXEL_LEPTON_W;
            int quot = actual / pixel, rem = actual % pixel;
            speedAccum = (uint)rem;
            actual -= rem;
            uint old = coord;

            if (quot != 0)
            {
                uint newcoord = TdMath.CoordMove(coord, facing, actual);
                if (newcoord == coord)
                    return ImpactType.IMPACT_NONE;
                coord = newcoord;
                if ((newcoord & 0xC000C000) != 0)
                {
                    coord = old;
                    return ImpactType.IMPACT_EDGE;
                }
                return ImpactType.IMPACT_NORMAL;
            }
        }
        return ImpactType.IMPACT_NONE;
    }
}

/// <summary>FuseClass (FUSE.CPP): a projectile's fuse — a timer, an arming delay, and proximity to the target.</summary>
public sealed class FuseClass
{
    public byte Timer;
    byte arming;
    uint headTo;
    short proximity;

    public uint Fuse_Target() => headTo;

    public void Arm_Fuse(uint location, uint target, int timeto = 0xFF, int armingTime = 0)
    {
        timeto = Math.Max(timeto, armingTime);
        Timer = (byte)Math.Min(timeto, 0xFF);
        arming = (byte)Math.Min(armingTime, 0xFF);
        headTo = target;
        proximity = (short)TdMath.Distance(location, target);
    }

    /// <summary>Fuse_Checkup: true when the fuse goes off (time up, very close, or passing the target).</summary>
    public bool Fuse_Checkup(uint newlocation)
    {
        if (Timer != 0)
            Timer--;
        if (arming != 0)
        {
            arming--;
        }
        else
        {
            if (Timer == 0)
                return true;
            int prox = TdMath.Distance(newlocation, headTo);
            if (prox < 0x0010)
                return true;
            if (prox < (int)TdDefines.ICON_LEPTON_W && prox > proximity)
                return true;
            proximity = (short)prox;
        }
        return false;
    }
}

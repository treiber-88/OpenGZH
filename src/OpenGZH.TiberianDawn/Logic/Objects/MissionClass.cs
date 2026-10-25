namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// TCountDownTimerClass (FTIMER.H): counts down in game frames from when it was set. A cleared
/// timer has started at -1, exactly as the original (it still reports a time).
/// </summary>
public sealed class CountDownTimer(TdGame game)
{
    long started, delay;

    public void Set(long time)
    {
        started = game.Frame;
        delay = time;
    }

    public void Clear()
    {
        started = -1;
        delay = 0;
    }

    public long Start => started;
    public long Delay => delay;
    public bool Active => started != -1;
    public bool Expired => Time == 0;

    public long Time
    {
        get
        {
            long remain = delay - (game.Frame - started);
            return remain < 0 ? 0 : remain;
        }
    }
}

/// <summary>
/// MissionClass (MISSION.CPP): an object's current order (guard, attack, move, hunt...) and its
/// queued and suspended orders. Each mission is a method that returns how many frames to wait
/// before it runs again.
/// </summary>
public abstract class MissionClass : ObjectClass
{
    public MissionType Mission = MissionType.MISSION_NONE;
    public MissionType SuspendedMission = MissionType.MISSION_NONE;
    public MissionType MissionQueue = MissionType.MISSION_NONE;
    public int Status;
    protected readonly CountDownTimer MissionTimer;

    protected MissionClass(TdGame game) : base(game)
    {
        MissionTimer = new CountDownTimer(game);
        MissionTimer.Set(0);
    }

    const int ThirtySeconds = (int)TdDefines.TICKS_PER_SECOND * 30;
    public virtual int Mission_Sleep() => ThirtySeconds;
    public virtual int Mission_Ambush() => ThirtySeconds;
    public virtual int Mission_Attack() => ThirtySeconds;
    public virtual int Mission_Capture() => ThirtySeconds;
    public virtual int Mission_Guard() => ThirtySeconds;
    public virtual int Mission_Guard_Area() => ThirtySeconds;
    public virtual int Mission_Harvest() => ThirtySeconds;
    public virtual int Mission_Hunt() => ThirtySeconds;
    public virtual int Mission_Timed_Hunt() => ThirtySeconds;
    public virtual int Mission_Move() => ThirtySeconds;
    public virtual int Mission_Retreat() => ThirtySeconds;
    public virtual int Mission_Return() => ThirtySeconds;
    public virtual int Mission_Stop() => ThirtySeconds;
    public virtual int Mission_Unload() => ThirtySeconds;
    public virtual int Mission_Enter() => ThirtySeconds;
    public virtual int Mission_Construction() => ThirtySeconds;
    public virtual int Mission_Deconstruction() => ThirtySeconds;
    public virtual int Mission_Repair() => ThirtySeconds;
    public virtual int Mission_Missile() => ThirtySeconds;

    public virtual void Set_Mission(MissionType mission)
    {
        Mission = mission;
        MissionQueue = MissionType.MISSION_NONE;
    }

    public override MissionType Get_Mission() => Mission == MissionType.MISSION_NONE ? MissionQueue : Mission;

    public override void AI()
    {
        base.AI();
        if (MissionTimer.Expired && Strength > 0)
        {
            int next = Mission switch
            {
                MissionType.MISSION_GUARD => Mission_Guard(),
                MissionType.MISSION_ENTER => Mission_Enter(),
                MissionType.MISSION_CONSTRUCTION => Mission_Construction(),
                MissionType.MISSION_DECONSTRUCTION => Mission_Deconstruction(),
                MissionType.MISSION_CAPTURE or MissionType.MISSION_SABOTAGE => Mission_Capture(),
                MissionType.MISSION_MOVE => Mission_Move(),
                MissionType.MISSION_ATTACK => Mission_Attack(),
                MissionType.MISSION_RETREAT => Mission_Retreat(),
                MissionType.MISSION_HARVEST => Mission_Harvest(),
                MissionType.MISSION_GUARD_AREA => Mission_Guard_Area(),
                MissionType.MISSION_RETURN => Mission_Return(),
                MissionType.MISSION_STOP => Mission_Stop(),
                MissionType.MISSION_AMBUSH => Mission_Ambush(),
                MissionType.MISSION_HUNT or MissionType.MISSION_RESCUE => Mission_Hunt(),
                MissionType.MISSION_TIMED_HUNT => Mission_Timed_Hunt(),
                MissionType.MISSION_UNLOAD => Mission_Unload(),
                MissionType.MISSION_REPAIR => Mission_Repair(),
                MissionType.MISSION_MISSILE => Mission_Missile(),
                _ => Mission_Sleep(), // MISSION_STICKY, MISSION_SLEEP and anything else
            };
            MissionTimer.Set(next);
        }
    }

    /// <summary>Commence: starts the queued mission, if any.</summary>
    public virtual bool Commence()
    {
        if (MissionQueue != MissionType.MISSION_NONE)
        {
            Mission = MissionQueue;
            MissionQueue = MissionType.MISSION_NONE;
            MissionTimer.Set(0);
            Status = 0;
            return true;
        }
        return false;
    }

    public virtual void Assign_Mission(MissionType order)
    {
        if (order == MissionType.MISSION_NONE || Mission == order)
            return;
        MissionQueue = order;
    }

    static readonly string[] Missions =
    [
        "Sleep", "Attack", "Move", "Retreat", "Guard", "Sticky", "Enter", "Capture", "Harvest", "Area Guard", "Return",
        "Stop", "Ambush", "Hunt", "Timed Hunt", "Unload", "Sabotage", "Construction", "Selling", "Repair", "Rescue", "Missile",
    ];

    public static MissionType Mission_From_Name(string? name)
    {
        if (name != null)
            for (int i = 0; i < Missions.Length; i++)
                if (Missions[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return (MissionType)i;
        return MissionType.MISSION_NONE;
    }

    public static string Mission_Name(MissionType mission) => mission == MissionType.MISSION_NONE ? "None" : Missions[(int)mission];

    /// <summary>Override_Mission: switches to a new mission, remembering the current one.</summary>
    public virtual void Override_Mission(MissionType mission, uint tarcom, uint navcom)
    {
        SuspendedMission = MissionQueue != MissionType.MISSION_NONE ? MissionQueue : Mission;
        Assign_Mission(mission);
    }

    public virtual bool Restore_Mission()
    {
        if (SuspendedMission != MissionType.MISSION_NONE)
        {
            Assign_Mission(SuspendedMission);
            SuspendedMission = MissionType.MISSION_NONE;
            return true;
        }
        return false;
    }
}

/// <summary>
/// RadioClass (RADIO.CPP): one radio link between two objects (a harvester and its refinery, a unit
/// and a transport or repair bay), over which they negotiate with RadioMessageType messages.
/// </summary>
public abstract class RadioClass : MissionClass
{
    RadioMessageType lastMessage = RadioMessageType.RADIO_STATIC;
    RadioClass? radio;

    protected RadioClass(TdGame game) : base(game) { }

    public bool In_Radio_Contact() => radio != null;
    public void Radio_Off() => radio = null;
    public TechnoClass? Contact_With_Whom() => radio as TechnoClass;
    public RadioMessageType LastMessage => lastMessage;

    public override RadioMessageType Receive_Message(RadioClass? from, RadioMessageType message, ref int param)
    {
        lastMessage = message;
        if (from == radio && message == RadioMessageType.RADIO_OVER_OUT)
        {
            base.Receive_Message(from, message, ref param);
            Radio_Off();
            return RadioMessageType.RADIO_ROGER;
        }
        if (message == RadioMessageType.RADIO_HELLO && Strength != 0)
        {
            if (radio == from || radio == null)
            {
                radio = from;
                return RadioMessageType.RADIO_ROGER;
            }
            return RadioMessageType.RADIO_NEGATIVE;
        }
        return base.Receive_Message(from, message, ref param);
    }

    public virtual RadioMessageType Transmit_Message(RadioMessageType message, ref int param, RadioClass? to = null)
    {
        to ??= Contact_With_Whom();
        if (to == null)
            return RadioMessageType.RADIO_STATIC;
        if (to == radio && message == RadioMessageType.RADIO_OVER_OUT)
            radio = null;
        if (message == RadioMessageType.RADIO_HELLO)
        {
            Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
            if (to.Receive_Message(this, message, ref param) == RadioMessageType.RADIO_ROGER)
            {
                radio = to;
                return RadioMessageType.RADIO_ROGER;
            }
            return RadioMessageType.RADIO_NEGATIVE;
        }
        return to.Receive_Message(this, message, ref param);
    }

    /// <summary>Transmit_Message with the shared LParam (the original's default parameter).</summary>
    public RadioMessageType Transmit_Message(RadioMessageType message, RadioClass? to = null) =>
        Transmit_Message(message, ref Game.LParam, to);

    public override bool Limbo()
    {
        if (!IsInLimbo)
            Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
        return base.Limbo();
    }
}

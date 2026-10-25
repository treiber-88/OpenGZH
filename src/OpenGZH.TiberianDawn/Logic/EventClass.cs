using OpenGZH.TiberianDawn.Logic.Objects;

namespace OpenGZH.TiberianDawn.Logic;

/// <summary>
/// EventClass (EVENT.CPP): a player command. Commands are not carried out where they are given;
/// they go into the OutList and are executed by the queue (Queue_AI) on the frame they are due,
/// which is what keeps multiplayer games in step. Each event records the issuing house and frame.
/// </summary>
public sealed class EventClass
{
    public enum EventType : byte
    {
        EMPTY, ALLY, MEGAMISSION, IDLE, SCATTER, DESTRUCT, DEPLOY, PLACE, OPTIONS, GAMESPEED, PRODUCE,
        SUSPEND, ABANDON, PRIMARY, SPECIAL_PLACE, EXIT, ANIMATION, REPAIR, SELL, SPECIAL, FRAMESYNC,
        MESSAGE, RESPONSE_TIME, FRAMEINFO, ARCHIVE, TIMING, PROCESS_TIME, LAST_EVENT,
    }

    public EventType Type;
    public long Frame;
    public int ID;                 // the issuing house's heap slot
    public bool IsExecuted;

    // The original's Data union, as separate fields.
    public uint Whom, Target, Destination;          // Target / NavCom / MegaMission
    public MissionType Mission;
    public int Value;                               // General
    public RTTIType ObjectType;                     // Specific / Place
    public int ObjectId;                            // Specific
    public int Cell;                                // Place / Special
    public int SpecialId;                           // Special
    public AnimType What;                           // Anim
    public HousesType Owner;
    public uint Where;
    public int Visible = -1;

    EventClass(TdGame game, EventType type)
    {
        ID = game.Houses.ID(game.PlayerPtr);
        Type = type;
        Frame = game.Frame;
    }

    public static EventClass Of(TdGame game, EventType type) => new(game, type);
    public static EventClass Of(TdGame game, EventType type, uint target) => new(game, type) { Whom = target };
    public static EventClass Of(TdGame game, EventType type, int val) => new(game, type) { Value = val };
    public static EventClass Of(TdGame game, EventType type, uint src, uint dest) => new(game, type) { Whom = src, Where = dest };
    public static EventClass Specific(TdGame game, EventType type, RTTIType obj, int id) => new(game, type) { ObjectType = obj, ObjectId = id };
    public static EventClass Place(TdGame game, RTTIType obj, int cell) => new(game, EventType.PLACE) { ObjectType = obj, Cell = cell };
    public static EventClass Special_Place(TdGame game, int id, int cell) => new(game, EventType.SPECIAL_PLACE) { SpecialId = id, Cell = cell };

    public static EventClass MegaMission(TdGame game, uint whom, MissionType mission, uint target = Logic.Target.TARGET_NONE, uint destination = Logic.Target.TARGET_NONE) =>
        new(game, EventType.MEGAMISSION) { Whom = whom, Mission = mission, Target = target, Destination = destination };

    public static EventClass Anim(TdGame game, AnimType anim, HousesType owner, uint coord, int visible = -1) =>
        new(game, EventType.ANIMATION) { What = anim, Owner = owner, Where = coord, Visible = visible };

    // The callers' names for the one-target events.
    public static EventClass Primary(TdGame game, uint target) => Of(game, EventType.PRIMARY, target);
    public static EventClass Sell(TdGame game, uint target) => Of(game, EventType.SELL, target);
    public static EventClass Archive(TdGame game, uint src, uint dest) => Of(game, EventType.ARCHIVE, src, dest);

    /// <summary>Execute: carry out the command.</summary>
    public void Execute(TdGame game)
    {
        TechnoClass? techno;
        var issuer = game.Houses.RawPtr(ID);
        switch (Type)
        {
            case EventType.ARCHIVE:
                techno = game.As_Techno(Whom);
                if (techno != null && techno.IsActive)
                    techno.ArchiveTarget = Where;
                break;

            case EventType.ALLY:
            {
                var house = game.Houses.RawPtr(Value);
                if (issuer!.Is_Ally(house))
                    issuer.Make_Enemy((HousesType)Value);
                else
                    issuer.Make_Ally((HousesType)Value);
                break;
            }

            case EventType.DESTRUCT:
                issuer!.Flag_To_Die();
                issuer.Resigned = true;
                break;

            case EventType.SPECIAL:
                // The special options are part of the event in the original; changing them in a
                // running game comes with multiplayer.
                break;

            case EventType.REPAIR:
                techno = game.As_Techno(Whom);
                if (techno != null && techno.IsActive)
                    techno.Repair(-1);
                break;

            case EventType.SELL:
                techno = game.As_Techno(Whom);
                if (techno != null && techno.IsActive && techno.House == issuer)
                    techno.Sell_Back(-1);
                else if (Logic.Target.Is_Target_Cell(Whom))
                    issuer!.Sell_Wall(game.As_Cell(Whom));
                break;

            case EventType.ANIMATION:
            {
                var anim = AnimClass.Create(game, What, Where);
                if (anim != null)
                {
                    anim.Set_Owner(Owner);
                    anim.Set_Visible_Flags(game.Special.IsVisibleTarget ? uint.MaxValue : (uint)Visible);
                    // A beacon lasts thirty seconds of real time.
                    if (What == AnimType.ANIM_BEACON)
                        anim.Kill_At((ulong)DateTime.UtcNow.ToFileTimeUtc() + 300000000UL);
                }
                break;
            }

            case EventType.PLACE:
                issuer!.Place_Object(ObjectType, Cell);
                break;

            case EventType.PRODUCE:
                issuer!.Begin_Production(ObjectType, ObjectId);
                break;

            case EventType.SUSPEND:
                issuer!.Suspend_Production(ObjectType);
                break;

            case EventType.ABANDON:
                issuer!.Abandon_Production(ObjectType);
                break;

            case EventType.PRIMARY:
            {
                var building = game.As_Building(Whom);
                if (building != null && building.IsActive)
                    building.Toggle_Primary();
                break;
            }

            case EventType.MEGAMISSION:
                techno = game.As_Techno(Whom);
                if (techno != null && techno.IsActive)
                {
                    ObjectClass? obj = game.Target_Legal(Target) ? game.As_Object(Target) : game.As_Object(Destination);

                    if (!techno.IsTethered)
                        techno.Transmit_Message(RadioMessageType.RADIO_OVER_OUT);

                    // An ordered unit leaves its team.
                    if (techno is FootClass { Team: { } team } foot && techno.What_Am_I() is RTTIType.RTTI_INFANTRY or RTTIType.RTTI_UNIT)
                        team.Remove(foot);

                    if (obj != null)
                    {
                        bool isAllied = issuer != null && issuer.Is_Ally(techno);
                        if (isAllied || game.Special.IsVisibleTarget)
                            obj.Clicked_As_Target((HousesType)ID);
                    }

                    techno.Assign_Mission(Mission);
                    if (Mission == MissionType.MISSION_GUARD_AREA &&
                        techno.What_Am_I() is RTTIType.RTTI_INFANTRY or RTTIType.RTTI_UNIT or RTTIType.RTTI_AIRCRAFT)
                    {
                        techno.ArchiveTarget = Target;
                        techno.Assign_Target(Logic.Target.TARGET_NONE);
                        techno.Assign_Destination(Target);
                    }
                    else if (Mission == MissionType.MISSION_ENTER && obj is BuildingClass building &&
                             techno.Transmit_Message(RadioMessageType.RADIO_HELLO, building) == RadioMessageType.RADIO_ROGER)
                    {
                        techno.Assign_Destination(Logic.Target.TARGET_NONE);
                    }
                    else
                    {
                        techno.Assign_Target(Target);
                        techno.Assign_Destination(Destination);
                    }
                }
                break;

            case EventType.IDLE:
                techno = game.As_Techno(Whom);
                if (techno != null && techno.IsActive && !techno.IsInLimbo && !techno.IsTethered)
                {
                    techno.Assign_Destination(Logic.Target.TARGET_NONE);
                    techno.Assign_Target(Logic.Target.TARGET_NONE);
                    techno.Enter_Idle_Mode();
                }
                break;

            case EventType.SCATTER:
                techno = game.As_Techno(Whom);
                if (techno != null && techno.IsActive && !techno.IsInLimbo && !techno.IsTethered)
                    techno.Scatter(0, true);
                break;

            case EventType.SPECIAL_PLACE:
                issuer!.Place_Special_Blast((SpecialWeaponType)SpecialId, Cell);
                break;

            case EventType.EXIT:
                game.Speak(VoxType.VOX_CONTROL_EXIT);
                game.GameActive = false;
                break;

            case EventType.GAMESPEED:
                game.Options.GameSpeed = Value;
                break;
        }
    }
}

// QUEUE.CPP: the single-player event queue.
public sealed partial class TdGame
{
    const int MAX_EVENTS = 256;

    /// <summary>The events this player has issued, waiting for the queue (QueueClass: full = dropped).</summary>
    public readonly EventQueue OutList = new(MAX_EVENTS);
    public readonly EventQueue DoList = new(MAX_EVENTS * 8);

    /// <summary>Queue_AI (GAME_NORMAL: Queue_AI_Normal): move this player's events to the DoList,
    /// execute those that are due, and drop the executed ones.</summary>
    public void Queue_AI()
    {
        while (OutList.Count > 0)
        {
            var e = OutList.First();
            e.IsExecuted = false;
            DoList.Add(e);
            OutList.Next();
        }

        // Execute_DoList: in a single-player game every due event runs.
        for (int i = 0; i < DoList.Count; i++)
        {
            var e = DoList[i];
            if (Frame >= e.Frame && !e.IsExecuted)
            {
                e.Execute(this);
                e.IsExecuted = true;
            }
        }

        // Clean_DoList: discard executed (or overdue) events from the front.
        while (DoList.Count > 0 && (DoList.First().IsExecuted || Frame > DoList.First().Frame))
            DoList.Next();
    }
}

/// <summary>QueueClass: a fixed-size ring of events; adding to a full queue fails.</summary>
public sealed class EventQueue(int size)
{
    readonly EventClass?[] array = new EventClass?[size];
    int head, tail;

    public int Count { get; private set; }

    public bool Add(EventClass e)
    {
        if (Count >= array.Length)
            return false;
        array[tail] = e;
        tail = (tail + 1) % array.Length;
        Count++;
        return true;
    }

    public EventClass First() => array[head]!;

    public void Next()
    {
        if (Count > 0)
        {
            array[head] = null;
            head = (head + 1) % array.Length;
            Count--;
        }
    }

    public EventClass this[int index] => array[(head + index) % array.Length]!;
}

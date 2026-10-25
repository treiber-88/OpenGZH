using OpenGZH.TiberianDawn.Logic.Objects;

namespace OpenGZH.TiberianDawn.Logic.Scenario;

/// <summary>
/// TriggerClass (TRIGGER.CPP): a scenario trigger — an event (a cell entered, an object destroyed,
/// a house's state, a time) and an action (win, lose, teams, reinforcements, special weapons...).
/// Triggers sit on cells, objects or houses; volatile ones go after firing once.
/// </summary>
public sealed class TriggerClass
{
    public enum ActionType
    {
        ACTION_NONE = -1, ACTION_WIN, ACTION_LOSE, ACTION_BEGIN_PRODUCTION, ACTION_CREATE_TEAM, ACTION_DESTROY_TEAM, ACTION_ALL_HUNT,
        ACTION_REINFORCEMENTS, ACTION_DZ, ACTION_AIRSTRIKE, ACTION_NUKE, ACTION_ION, ACTION_DESTROY_XXXX, ACTION_DESTROY_YYYY,
        ACTION_DESTROY_ZZZZ, ACTION_AUTOCREATE, ACTION_WINLOSE, ACTION_ALLOWWIN, ACTION_COUNT,
    }

    public enum PersistantType { VOLATILE = 0, SEMIPERSISTANT = 1, PERSISTANT = 2 }

    static readonly string[] EventText =
    [
        "None", "Player Enters", "Discovered", "Attacked", "Destroyed", "Any", "House Discov.", "Units Destr.", "Bldgs Destr.",
        "All Destr.", "Credits", "Time", "# Bldgs Dstr.", "# Units Dstr.", "No Factories", "Civ. Evac.", "Built It",
    ];

    static readonly string[] ActionText =
    [
        "None", "Win", "Lose", "Production", "Create Team", "Dstry Teams", "All to Hunt", "Reinforce.", "DZ at 'Z'", "Airstrike",
        "Nuclear Missile", "Ion Cannon", "Dstry Trig 'XXXX'", "Dstry Trig 'YYYY'", "Dstry Trig 'ZZZZ'", "Autocreate", "Cap=Win/Des=Lose", "Allow Win",
    ];

    readonly TdGame game;
    public TeamTypeClass? Team;
    public bool IsActive;
    public PersistantType IsPersistant = PersistantType.VOLATILE;
    public int AttachCount;
    public EventType Event = EventType.EVENT_NONE;
    public ActionType Action = ActionType.ACTION_NONE;
    public HousesType House = HousesType.HOUSE_NONE;
    public int Data, DataCopy;
    string name = "";

    TriggerClass(TdGame game) => this.game = game;

    /// <summary>new TriggerClass: a slot in the trigger heap (null when full).</summary>
    public static TriggerClass? Create(TdGame game)
    {
        int slot = game.Triggers.Allocate();
        if (slot < 0)
            return null;
        var t = new TriggerClass(game);
        game.Triggers.Place(slot, t);
        t.IsActive = true;
        return t;
    }

    /// <summary>delete: an "allow win" trigger releases its hold on winning.</summary>
    public void Delete()
    {
        if (game.GameActive && House != HousesType.HOUSE_NONE && Action == ActionType.ACTION_ALLOWWIN)
        {
            // As the original: the house is looked up by list position (the houses are made in order).
            var house = (int)House < game.Houses.Count ? game.Houses.Ptr((int)House) : null;
            if (house != null)
            {
                if (house.Blockage != 0)
                    house.Blockage--;
                house.BorrowedTime.Set(TdDefines.TICKS_PER_SECOND * 4);
            }
        }
        IsActive = false;
        game.Triggers.Free(this);
    }

    public string Get_Name() => name;
    public void Set_Name(string buf) => name = buf.Length > 4 ? buf[..4] : buf;
    public uint As_Target() => Target.Build_Target(KindType.KIND_TRIGGER, game.Triggers.ID(this));

    public static bool Event_Need_Object(EventType e) =>
        e is EventType.EVENT_PLAYER_ENTERED or EventType.EVENT_DISCOVERED or EventType.EVENT_ATTACKED or EventType.EVENT_DESTROYED or EventType.EVENT_ANY;

    public static bool Event_Need_House(EventType e) =>
        e is EventType.EVENT_PLAYER_ENTERED or EventType.EVENT_HOUSE_DISCOVERED or EventType.EVENT_UNITS_DESTROYED or EventType.EVENT_BUILDINGS_DESTROYED
            or EventType.EVENT_ALL_DESTROYED or EventType.EVENT_CREDITS or EventType.EVENT_TIME or EventType.EVENT_NBUILDINGS_DESTROYED
            or EventType.EVENT_NUNITS_DESTROYED or EventType.EVENT_NOFACTORIES or EventType.EVENT_EVAC_CIVILIAN or EventType.EVENT_BUILD;

    public static bool Event_Need_Data(EventType e) =>
        e is EventType.EVENT_CREDITS or EventType.EVENT_TIME or EventType.EVENT_NBUILDINGS_DESTROYED or EventType.EVENT_NUNITS_DESTROYED or EventType.EVENT_BUILD;

    public static bool Action_Need_Team(ActionType a) =>
        a is ActionType.ACTION_CREATE_TEAM or ActionType.ACTION_DESTROY_TEAM or ActionType.ACTION_REINFORCEMENTS;

    /// <summary>A time trigger counts down (true while still waiting).</summary>
    bool Time_Not_Yet()
    {
        if (Event == EventType.EVENT_TIME)
        {
            Data--;
            if (Data > 0)
                return true;
            Data = DataCopy;
        }
        return false;
    }

    /// <summary>"Destroy trigger XXXX": as the original, Remove deletes it and then it is deleted again.</summary>
    void Destroy_Named(string triggerName)
    {
        var trig = As_Pointer(game, triggerName);
        if (trig != null)
        {
            trig.Remove();
            trig.Delete();
        }
    }

    void Superweapon_Action()
    {
        bool playerBad = game.PlayerPtr.Class.House == HousesType.HOUSE_BAD;
        if (Action == ActionType.ACTION_NUKE)
        {
            var bad = game.HouseFor(HousesType.HOUSE_BAD);
            bad.NukeStrike.Enable(true, false);
            bad.NukeStrike.Forced_Charge(playerBad);
        }
        else
        {
            var good = game.HouseFor(HousesType.HOUSE_GOOD);
            good.IonCannon.Enable(true, false);
            good.IonCannon.Forced_Charge(game.PlayerPtr.Class.House == HousesType.HOUSE_GOOD);
        }
    }

    void Finish(bool success)
    {
        if (!success && Event == EventType.EVENT_TIME)
            Data = 1;
        if (success && IsPersistant == PersistantType.VOLATILE)
            Remove();
    }

    /// <summary>Spring (object event): discovered, attacked, destroyed, any.</summary>
    public bool Spring(EventType e, ObjectClass? obj)
    {
        if (e != Event && Event != EventType.EVENT_ANY)
            return false;
        if (Time_Not_Yet())
            return false;

        if (IsPersistant == PersistantType.SEMIPERSISTANT)
        {
            obj!.Trigger = null;
            AttachCount--;
            if (AttachCount > 0)
                return false;
            IsPersistant = PersistantType.VOLATILE;
        }

        bool success = true;
        var player = game.PlayerPtr;
        switch (Action)
        {
            case ActionType.ACTION_NUKE:
            case ActionType.ACTION_ION:
                Superweapon_Action();
                break;
            case ActionType.ACTION_WINLOSE:
                switch (e)
                {
                    case EventType.EVENT_DESTROYED:
                        if (!player.IsToWin || player.Blockage > 0)
                            player.Flag_To_Lose();
                        break;
                    case EventType.EVENT_PLAYER_ENTERED:
                        if (!player.IsToLose)
                            player.Flag_To_Win();
                        break;
                    default:
                        success = false;
                        break;
                }
                break;
            case ActionType.ACTION_DESTROY_XXXX:
                Destroy_Named("XXXX");
                break;
            case ActionType.ACTION_DESTROY_YYYY:
                Destroy_Named("YYYY");
                break;
            case ActionType.ACTION_DESTROY_ZZZZ:
                Destroy_Named("ZZZZ");
                break;
            case ActionType.ACTION_AIRSTRIKE:
                player.IsAirstrikePending = true;
                break;
            case ActionType.ACTION_DZ:
                AnimClass.Create(game, AnimType.ANIM_LZ_SMOKE, TdMath.CellCoord(game.Waypoint[25]));
                break;
            case ActionType.ACTION_WIN:
                player.Flag_To_Win();
                break;
            case ActionType.ACTION_LOSE:
                player.Flag_To_Lose();
                break;
            case ActionType.ACTION_BEGIN_PRODUCTION:
                game.HouseFor(House).Begin_Production();
                break;
            case ActionType.ACTION_AUTOCREATE:
                if (obj is TechnoClass techno)
                    techno.House.IsAlerted = true;
                break;
            default:
                success = Team_Action(success);
                break;
        }
        Finish(success);
        return true;
    }

    /// <summary>The team actions and "all to hunt", shared by the three Spring forms.</summary>
    bool Team_Action(bool success)
    {
        switch (Action)
        {
            case ActionType.ACTION_CREATE_TEAM:
                if (Team != null)
                {
                    game.ScenarioInit++;
                    Team.Create_One_Of();
                    game.ScenarioInit--;
                }
                break;
            case ActionType.ACTION_DESTROY_TEAM:
                Team?.Destroy_All_Of();
                break;
            case ActionType.ACTION_REINFORCEMENTS:
                if (Team != null)
                    success = game.Do_Reinforcements(Team);
                break;
            case ActionType.ACTION_ALL_HUNT:
                Do_All_To_Hunt();
                break;
        }
        return success;
    }

    /// <summary>Spring (cell event): a unit entered the cell.</summary>
    public bool Spring(EventType e, int cell)
    {
        if (e != Event)
            return false;
        if (Time_Not_Yet())
            return false;

        if (IsPersistant == PersistantType.SEMIPERSISTANT)
        {
            game.Map[cell].IsTrigger = false;
            AttachCount--;
            if (AttachCount > 0)
                return false;
            IsPersistant = PersistantType.VOLATILE;
        }

        bool success = true;
        var player = game.PlayerPtr;
        switch (Action)
        {
            case ActionType.ACTION_NUKE:
            case ActionType.ACTION_ION:
                Superweapon_Action();
                break;
            case ActionType.ACTION_AUTOCREATE:
                for (int index = 0; index < game.Houses.Count; index++)
                    game.Houses.Ptr(index).IsAlerted = true;
                break;
            case ActionType.ACTION_DESTROY_XXXX:
                Destroy_Named("XXXX");
                break;
            case ActionType.ACTION_DESTROY_YYYY:
                Destroy_Named("YYYY");
                break;
            case ActionType.ACTION_DESTROY_ZZZZ:
                Destroy_Named("ZZZZ");
                break;
            case ActionType.ACTION_AIRSTRIKE:
                game.HouseFor(House).AirStrike.Enable(false, true);
                if (House == player.Class.House)
                {
                    player.AirStrike.Forced_Charge(true);
                    game.Sidebar_Add(RTTIType.RTTI_SPECIAL, (int)SpecialWeaponType.SPC_AIR_STRIKE, player, false);
                }
                break;
            case ActionType.ACTION_DZ:
                AnimClass.Create(game, AnimType.ANIM_LZ_SMOKE, TdMath.CellCoord(game.Waypoint[25]));
                break;
            case ActionType.ACTION_WIN:
                player.Flag_To_Win();
                break;
            case ActionType.ACTION_LOSE:
                player.Flag_To_Lose();
                break;
            case ActionType.ACTION_BEGIN_PRODUCTION:
                // Entering a cell starts the enemy's production.
                game.HouseFor(player.Class.House == HousesType.HOUSE_GOOD ? HousesType.HOUSE_BAD : HousesType.HOUSE_GOOD).Begin_Production();
                break;
            default:
                success = Team_Action(success);
                break;
        }
        Finish(success);
        return true;
    }

    /// <summary>Spring (house event): credits, time, losses, no factories, built something...</summary>
    public bool Spring(EventType e, HousesType house, int data = 0)
    {
        if (e != Event || house != House)
            return false;
        if (Event == EventType.EVENT_CREDITS && data < Data)
            return false;
        if (Event == EventType.EVENT_BUILD && data != Data)
            return false;
        if ((Event == EventType.EVENT_NBUILDINGS_DESTROYED || Event == EventType.EVENT_NUNITS_DESTROYED) && data < Data)
            return false;
        if (Time_Not_Yet())
            return false;

        bool success = true;
        var player = game.PlayerPtr;
        switch (Action)
        {
            case ActionType.ACTION_NUKE:
            case ActionType.ACTION_ION:
                Superweapon_Action();
                break;
            case ActionType.ACTION_ALLOWWIN:
                break;
            case ActionType.ACTION_AUTOCREATE:
                game.HouseFor(House).IsAlerted = true;
                break;
            case ActionType.ACTION_DESTROY_XXXX:
                Destroy_Named("XXXX");
                break;
            case ActionType.ACTION_DESTROY_YYYY:
                Destroy_Named("YYYY");
                break;
            case ActionType.ACTION_DESTROY_ZZZZ:
                Destroy_Named("ZZZZ");
                break;
            case ActionType.ACTION_AIRSTRIKE:
                player.AirStrike.Enable(false, true);
                if (House == player.Class.House)
                {
                    player.AirStrike.Forced_Charge(true);
                    game.Sidebar_Add(RTTIType.RTTI_SPECIAL, (int)SpecialWeaponType.SPC_AIR_STRIKE, player, false);
                }
                break;
            case ActionType.ACTION_DZ:
                AnimClass.Create(game, AnimType.ANIM_LZ_SMOKE, TdMath.CellCoord(game.Waypoint[25]));
                break;
            case ActionType.ACTION_WIN:
                player.Flag_To_Win();
                break;
            case ActionType.ACTION_LOSE:
                player.Flag_To_Lose();
                break;
            case ActionType.ACTION_BEGIN_PRODUCTION:
                game.HouseFor(House).Begin_Production();
                break;
            default:
                success = Team_Action(success);
                break;
        }
        Finish(success);
        return true;
    }

    /// <summary>Remove: detached from every cell, object and house, then deleted.</summary>
    public bool Remove()
    {
        for (int cell = 0; cell < TdDefines.MAP_CELL_TOTAL; cell++)
        {
            if (game.Map[cell].IsTrigger && game.CellTriggers[cell] == this)
            {
                game.Map[cell].IsTrigger = false;
                game.CellTriggers[cell] = null;
            }
        }
        for (int index = 0; index < game.Infantry.Count; index++)
            if (game.Infantry.Ptr(index).Trigger == this)
                game.Infantry.Ptr(index).Trigger = null;
        for (int index = 0; index < game.Buildings.Count; index++)
            if (game.Buildings.Ptr(index).Trigger == this)
                game.Buildings.Ptr(index).Trigger = null;
        for (int index = 0; index < game.Units.Count; index++)
            if (game.Units.Ptr(index).Trigger == this)
                game.Units.Ptr(index).Trigger = null;
        for (int index = 0; index < game.Terrains.Count; index++)
            if (game.Terrains.Ptr(index).Trigger == this)
                game.Terrains.Ptr(index).Trigger = null;
        foreach (var list in game.HouseTriggers)
            list.Remove(this);
        Delete();
        return true;
    }

    /// <summary>Read_INI: [Triggers] name=event,action,data,house,team,persistence.</summary>
    public static void Read_INI(TdGame game, OpenGZH.TiberianDawn.Data.WwProfile ini)
    {
        string buf = "";
        foreach (string key in ini.Entries("Triggers"))
        {
            var trigger = Create(game);
            if (trigger == null)
                continue;
            trigger.Set_Name(key);
            buf = ini.Get_String("Triggers", key, buf, 128 - 1);
            trigger.Fill_In(key, buf);
            if (trigger.House != HousesType.HOUSE_NONE)
            {
                if (trigger.Action == ActionType.ACTION_ALLOWWIN)
                    game.HouseFor(trigger.House).Blockage++;
                game.HouseTriggers[(int)trigger.House].Add(trigger);
                trigger.AttachCount++;
            }
        }
    }

    public void Fill_In(string triggerName, string entry)
    {
        Set_Name(triggerName);
        var tok = new OpenGZH.TiberianDawn.Data.Strtok(entry);
        Event = Event_From_Name(tok.Next(","));
        Action = Action_From_Name(tok.Next(","));
        DataCopy = Data = Data_Atol(tok.Next(","));
        House = game.Types.House_From_Name(tok.Next(","));
        if (House == HousesType.HOUSE_NONE && Event == EventType.EVENT_PLAYER_ENTERED)
            House = game.PlayerPtr.Class.House;
        Team = TeamTypeClass.As_Pointer(game, tok.Next(","));
        string? p = tok.Next(",");
        IsPersistant = p != null ? (PersistantType)OpenGZH.TiberianDawn.Data.WwProfile.Atoi(p) : PersistantType.VOLATILE;
    }

    static int Data_Atol(string? s) => OpenGZH.TiberianDawn.Data.WwProfile.Atoi(s);

    public static TriggerClass? As_Pointer(TdGame game, string? triggerName)
    {
        if (triggerName == null)
            return null;
        for (int i = 0; i < game.Triggers.Count; i++)
        {
            var trigger = game.Triggers.Ptr(i);
            if (string.Equals(triggerName, trigger.name, StringComparison.OrdinalIgnoreCase))
                return trigger;
        }
        return null;
    }

    public static EventType Event_From_Name(string? n)
    {
        if (n == null)
            return EventType.EVENT_NONE;
        for (int i = (int)EventType.EVENT_NONE; i < (int)EventType.EVENT_COUNT; i++)
            if (string.Equals(n, EventText[i + 1], StringComparison.OrdinalIgnoreCase))
                return (EventType)i;
        return EventType.EVENT_NONE;
    }

    public static string Name_From_Event(EventType e) => EventText[(int)e + 1];

    public static ActionType Action_From_Name(string? n)
    {
        if (n == null)
            return ActionType.ACTION_NONE;
        for (int i = (int)ActionType.ACTION_NONE; i < (int)ActionType.ACTION_COUNT; i++)
            if (string.Equals(n, ActionText[i + 1], StringComparison.OrdinalIgnoreCase))
                return (ActionType)i;
        return ActionType.ACTION_NONE;
    }

    public static string Name_From_Action(ActionType a) => ActionText[(int)a + 1];

    /// <summary>Do_All_To_Hunt: every computer unit and soldier on the map hunts.</summary>
    void Do_All_To_Hunt()
    {
        for (int index = 0; index < game.Units.Count; index++)
        {
            var unit = game.Units.Ptr(index);
            if (!unit.House.IsHuman && unit.IsDown && !unit.IsInLimbo)
            {
                unit.Team?.Remove(unit);
                unit.Assign_Mission(MissionType.MISSION_HUNT);
            }
        }
        for (int index = 0; index < game.Infantry.Count; index++)
        {
            var infantry = game.Infantry.Ptr(index);
            if (!infantry.House.IsHuman && infantry.IsDown && !infantry.IsInLimbo)
            {
                infantry.Team?.Remove(infantry);
                infantry.Assign_Mission(MissionType.MISSION_HUNT);
            }
        }
    }
}

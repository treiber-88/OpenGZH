using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Scenario;

/// <summary>A team's order and its argument (TeamMissionStruct).</summary>
public struct TeamMissionStruct
{
    public TeamMissionType Mission;
    public int Argument;
}

/// <summary>
/// TeamTypeClass (TEAMTYPE.CPP): a scenario's team template — owner, the units it wants and how
/// many, its list of orders, and how the computer recruits and creates it.
/// </summary>
public sealed class TeamTypeClass
{
    public const int MAX_TEAM_CLASSCOUNT = 5;
    public const int MAX_TEAM_MISSIONS = 20;

    static readonly string[] TMissions =
    [
        "Attack Base", "Attack Units", "Attack Civil.", "Rampage", "Defend Base", "Move", "Move to Cell",
        "Retreat", "Guard", "Loop", "Attack Tarcom", "Unload",
    ];

    readonly TdGame game;
    public string IniName = "";
    public bool IsActive, IsRoundAbout, IsLearning, IsSuicide, IsAutocreate, IsMercenary, IsPrebuilt = true, IsReinforcable = true, IsTransient;
    public int RecruitPriority = 7;
    public int InitNum, MaxAllowed, Fear;
    public HousesType House = HousesType.HOUSE_NONE;
    public int MissionCount;
    public readonly TeamMissionStruct[] MissionList = new TeamMissionStruct[MAX_TEAM_MISSIONS];
    public int ClassCount;
    public readonly TechnoTypeClass?[] Class = new TechnoTypeClass?[MAX_TEAM_CLASSCOUNT];
    public readonly int[] DesiredNum = new int[MAX_TEAM_CLASSCOUNT];

    TeamTypeClass(TdGame game) => this.game = game;

    /// <summary>new TeamTypeClass: a slot in the team type heap (null when full).</summary>
    public static TeamTypeClass? Create(TdGame game)
    {
        int slot = game.TeamTypes.Allocate();
        if (slot < 0)
            return null;
        var t = new TeamTypeClass(game);
        game.TeamTypes.Place(slot, t);
        t.IsActive = true;
        return t;
    }

    public void Delete()
    {
        IsActive = false;
        game.TeamTypes.Free(this);
    }

    /// <summary>Set_Name: up to eight characters, as the original's IniName[9].</summary>
    public void Set_Name(string name) => IniName = name.Length > 8 ? name[..8] : name;

    /// <summary>Read_INI: [TeamTypes] (or the old [Teams] format if there are none).</summary>
    public static void Read_INI(TdGame game, Data.WwProfile ini)
    {
        string buf = "";
        foreach (string key in ini.Entries("TeamTypes"))
        {
            var team = Create(game);
            buf = ini.Get_String("TeamTypes", key, buf, 500 - 1);
            team?.Fill_In(key, buf);
        }
        if (game.TeamTypes.Count == 0)
            Read_Old_INI(game, ini);
    }

    TechnoTypeClass? Type_From_Name(string? name)
    {
        TechnoTypeClass? otype = null;
        var i = game.Types.Infantry_From_Name(name);
        if (i != InfantryType.INFANTRY_NONE)
            otype = game.Types.Infantry(i);
        var u = game.Types.Unit_From_Name(name);
        if (u != UnitType.UNIT_NONE)
            otype = game.Types.Unit(u);
        var a = game.Types.Aircraft_From_Name(name);
        if (a != AircraftType.AIRCRAFT_NONE)
            otype = game.Types.Aircraft(a);
        return otype;
    }

    /// <summary>Fill_In: house, flags, priority, counts, the unit types:numbers, the missions:arguments,
    /// then (optionally) reinforcable and prebuilt.</summary>
    public void Fill_In(string name, string entry)
    {
        static int A(string? s) => Data.WwProfile.Atoi(s);

        Set_Name(name);
        var tok = new Data.Strtok(entry);
        House = game.Types.House_From_Name(tok.Next(","));
        IsRoundAbout = A(tok.Next(",")) != 0;
        IsLearning = A(tok.Next(",")) != 0;
        IsSuicide = A(tok.Next(",")) != 0;
        IsAutocreate = A(tok.Next(",")) != 0;
        IsMercenary = A(tok.Next(",")) != 0;
        RecruitPriority = A(tok.Next(","));
        MaxAllowed = (byte)A(tok.Next(","));
        InitNum = (byte)A(tok.Next(","));
        Fear = (byte)A(tok.Next(","));
        int numClasses = A(tok.Next(","));

        ClassCount = 0;
        for (int i = 0; i < numClasses; i++)
        {
            string? p1 = tok.Next(",:");
            string? p2 = tok.Next(",:");
            var otype = Type_From_Name(p1);
            if (otype != null && ClassCount < MAX_TEAM_CLASSCOUNT)
            {
                Class[ClassCount] = otype;
                DesiredNum[ClassCount] = (byte)A(p2);
                ClassCount++;
            }
        }

        MissionCount = A(tok.Next(","));
        for (int i = 0; i < MissionCount; i++)
        {
            string? p1 = tok.Next(",:");
            string? p2 = tok.Next(",:");
            if (i < MAX_TEAM_MISSIONS)
                MissionList[i] = new TeamMissionStruct { Mission = Mission_From_Name(p1), Argument = A(p2) };
        }

        string? ptr = tok.Next(",");
        if (ptr != null)
            IsReinforcable = A(ptr) != 0;
        ptr = tok.Next(",");
        if (ptr != null)
            IsPrebuilt = A(ptr) != 0;
    }

    /// <summary>Read_Old_INI: the early [Teams] format (no missions).</summary>
    static void Read_Old_INI(TdGame game, Data.WwProfile ini)
    {
        string buf = "";
        foreach (string key in ini.Entries("Teams"))
        {
            var team = Create(game);
            if (team == null)
                continue;
            team.Set_Name(key);
            buf = ini.Get_String("Teams", key, buf, 256 - 1);
            var tok = new Data.Strtok(buf);
            team.House = game.Types.House_From_Name(tok.Next(","));
            team.IsRoundAbout = Data.WwProfile.Atoi(tok.Next(",")) != 0;
            team.IsLearning = Data.WwProfile.Atoi(tok.Next(",")) != 0;
            team.IsSuicide = Data.WwProfile.Atoi(tok.Next(",")) != 0;
            team.IsAutocreate = Data.WwProfile.Atoi(tok.Next(",")) != 0;
            team.IsMercenary = Data.WwProfile.Atoi(tok.Next(",")) != 0;
            team.RecruitPriority = Data.WwProfile.Atoi(tok.Next(","));
            team.MaxAllowed = (byte)Data.WwProfile.Atoi(tok.Next(","));
            team.InitNum = (byte)Data.WwProfile.Atoi(tok.Next(","));
            tok.Next(",");

            int index = 0;
            string? p1 = tok.Next(",:");
            string? p2 = tok.Next(",:");
            while (p1 != null && p2 != null)
            {
                var otype = team.Type_From_Name(p1);
                if (otype != null && index < MAX_TEAM_CLASSCOUNT)
                {
                    team.Class[index] = otype;
                    team.DesiredNum[index] = (byte)Data.WwProfile.Atoi(p2);
                    index++;
                    team.ClassCount = index;
                }
                p1 = tok.Next(",:");
                p2 = tok.Next(",:");
            }
            team.Fear = 0;
        }
    }

    public static TeamTypeClass? As_Pointer(TdGame game, string? name)
    {
        if (name == null)
            return null;
        for (int i = 0; i < game.TeamTypes.Count; i++)
            if (string.Equals(name, game.TeamTypes.Ptr(i).IniName, StringComparison.OrdinalIgnoreCase))
                return game.TeamTypes.Ptr(i);
        return null;
    }

    /// <summary>Remove: triggers forget it, then it is deleted.</summary>
    public void Remove()
    {
        for (int i = 0; i < game.Triggers.Count; i++)
        {
            var trigger = game.Triggers.Ptr(i);
            if (trigger.Team == this)
                trigger.Team = null;
        }
        Delete();
    }

    public static TeamMissionType Mission_From_Name(string? name)
    {
        if (name != null)
            for (int order = 0; order < TMissions.Length; order++)
                if (string.Equals(TMissions[order], name, StringComparison.OrdinalIgnoreCase))
                    return (TeamMissionType)order;
        return TeamMissionType.TMISSION_NONE;
    }

    public static string Name_From_Mission(TeamMissionType order) =>
        order <= TeamMissionType.TMISSION_NONE || order >= TeamMissionType.TMISSION_COUNT ? "None" : TMissions[(int)order];

    /// <summary>Create_One_Of: a new team of this type (while under its maximum, or during setup).</summary>
    public TeamClass? Create_One_Of()
    {
        if (game.ScenarioInit != 0 || TeamClass.Number(game, game.TeamTypes.ID(this)) < MaxAllowed)
            return TeamClass.Create(game, this, game.HouseOrNull(House));
        return null;
    }

    public uint As_Target() => Target.Build_Target(KindType.KIND_TEAMTYPE, game.TeamTypes.ID(this));

    public void Destroy_All_Of()
    {
        for (int index = 0; index < game.Teams.Count; index++)
        {
            var team = game.Teams.Ptr(index);
            if (team.Class == this)
            {
                team.Delete();
                index--;
            }
        }
    }

    /// <summary>Suggested_New_Team: the house's highest-priority team type still below its limit
    /// (autocreate teams only when alerted); half priority if it needs no unit the house has.</summary>
    public static TeamTypeClass? Suggested_New_Team(TdGame game, HouseClass house, long utypes, long itypes, bool alerted)
    {
        TeamTypeClass? best = null;
        int bestvalue = 0;
        for (int index = 0; index < game.TeamTypes.Count; index++)
        {
            var ttype = game.TeamTypes.Ptr(index);
            int limit = (alerted || !ttype.IsAutocreate) ? ttype.MaxAllowed : 0;
            // As the original: Number[] is indexed by the list position here (elsewhere by heap ID).
            if (ttype.House == house.Class.House && TeamClass.Number(game, index) < limit)
            {
                long uneeded = 0, ineeded = 0;
                for (int ctype = 0; ctype < ttype.ClassCount; ctype++)
                {
                    switch (ttype.Class[ctype])
                    {
                        case InfantryTypeClass it:
                            ineeded |= 1L << (int)it.Type;
                            break;
                        case UnitTypeClass ut:
                            uneeded |= 1L << (int)ut.Type;
                            break;
                    }
                }

                int value = (ineeded & itypes) != 0 || (uneeded & utypes) != 0 ? ttype.RecruitPriority : ttype.RecruitPriority / 2;
                if (best == null || bestvalue < value)
                {
                    bestvalue = value;
                    best = ttype;
                }
            }
        }
        return best;
    }
}

using System.Numerics;
using OpenGZH.Core.Rules;

namespace OpenGZH.Logic.AI;

/// <summary>One structure in a skirmish build list (the original's BuildListInfo as read from AIData).</summary>
public sealed record AIBuildListEntry(string TemplateName, Vector2 Location, float Angle, int Rebuilds, bool InitiallyBuilt, bool AutomaticallyBuild);

/// <summary>Per-side computer player settings (the original's AISideInfo).</summary>
public sealed class AISideInfo
{
    public required string Side { get; init; }
    public int ResourceGatherersEasy { get; init; }
    public int ResourceGatherersNormal { get; init; }
    public int ResourceGatherersHard { get; init; }
    public string BaseDefenseStructure1 { get; init; } = "";
    /// <summary>Up to five skill sets of sciences the general buys, in order.</summary>
    public List<List<string>> SkillSets { get; } = new();
}

/// <summary>
/// The computer player's tuning values (the original's TAiData, from AIData.ini): build timing,
/// guard ranges, attack priorities, per-side info and the skirmish base layouts.
/// </summary>
public sealed class AIData
{
    public float StructureSeconds { get; }
    public float TeamSeconds { get; }
    public int Wealthy { get; }
    public int Poor { get; }
    public float StructuresWealthyRate { get; }
    public float StructuresPoorRate { get; }
    public float TeamsWealthyRate { get; }
    public float TeamsPoorRate { get; }
    public float TeamResourcesToStart { get; }
    public float GuardInnerModifierAI { get; }
    public float GuardOuterModifierAI { get; }
    public float GuardInnerModifierHuman { get; }
    public float GuardOuterModifierHuman { get; }
    public int GuardChaseUnitsDurationFrames { get; }
    public int GuardEnemyScanRateFrames { get; }
    public int GuardEnemyReturnScanRateFrames { get; }
    public float AlertRangeModifier { get; }
    public float AggressiveRangeModifier { get; }
    public float AttackPriorityDistanceModifier { get; }
    public float MaxRecruitRadius { get; }
    public float SkirmishBaseDefenseExtraDistance { get; }
    public bool ForceSkirmishAI { get; }
    public bool RotateSkirmishBases { get; }
    public bool AttackUsesLineOfSight { get; }
    public bool EnableRepulsors { get; }
    public float RepulsedDistance { get; }
    public float WallHeight { get; }
    public bool AttackIgnoreInsignificantBuildings { get; }
    public float SkirmishGroupFudgeValue { get; }
    public float SupplyCenterSafeRadius { get; }
    public int RebuildDelaySeconds { get; }
    public float AIDozerBoredRadiusModifier { get; }
    public bool AICrushesInfantry { get; }
    public float MaxRetaliationDistance { get; }
    public float RetaliationFriendsRadius { get; }

    readonly Dictionary<string, AISideInfo> sides = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<AIBuildListEntry>> buildLists = new(StringComparer.OrdinalIgnoreCase);

    public AIData(RuleSet rules)
    {
        var d = rules.Section("AIData");
        float F(string key, float fallback) => RuleValue.Float(d.GetString(key), fallback);
        int I(string key, int fallback) => RuleValue.Int(d.GetString(key), fallback);
        bool B(string key, bool fallback) => RuleValue.Bool(d.GetString(key), fallback);
        StructureSeconds = F("StructureSeconds", 0);
        TeamSeconds = F("TeamSeconds", 10);
        Wealthy = I("Wealthy", 7000);
        Poor = I("Poor", 2000);
        StructuresWealthyRate = F("StructuresWealthyRate", 2);
        StructuresPoorRate = F("StructuresPoorRate", 0.6f);
        TeamsWealthyRate = F("TeamsWealthyRate", 2);
        TeamsPoorRate = F("TeamsPoorRate", 0.6f);
        TeamResourcesToStart = F("TeamResourcesToStart", 0.1f);
        GuardInnerModifierAI = F("GuardInnerModifierAI", 1.1f);
        GuardOuterModifierAI = F("GuardOuterModifierAI", 1.333f);
        GuardInnerModifierHuman = F("GuardInnerModifierHuman", 1.8f);
        GuardOuterModifierHuman = F("GuardOuterModifierHuman", 2.2f);
        GuardChaseUnitsDurationFrames = Core.LogicTime.Frames(F("GuardChaseUnitsDuration", 10000));
        GuardEnemyScanRateFrames = Core.LogicTime.Frames(F("GuardEnemyScanRate", 500));
        GuardEnemyReturnScanRateFrames = Core.LogicTime.Frames(F("GuardEnemyReturnScanRate", 1000));
        AlertRangeModifier = F("AlertRangeModifier", 1.1f);
        AggressiveRangeModifier = F("AggressiveRangeModifier", 1.5f);
        AttackPriorityDistanceModifier = F("AttackPriorityDistanceModifier", 100);
        MaxRecruitRadius = F("MaxRecruitRadius", 500);
        SkirmishBaseDefenseExtraDistance = F("SkirmishBaseDefenseExtraDistance", 150);
        ForceSkirmishAI = B("ForceSkirmishAI", false);
        RotateSkirmishBases = B("RotateSkirmishBases", false);
        AttackUsesLineOfSight = B("AttackUsesLineOfSight", true);
        EnableRepulsors = B("EnableRepulsors", true);
        RepulsedDistance = F("RepulsedDistance", 150);
        WallHeight = F("WallHeight", 43);
        AttackIgnoreInsignificantBuildings = B("AttackIgnoreInsignificantBuildings", true);
        SkirmishGroupFudgeValue = F("SkirmishGroupFudgeDistance", 5);
        SupplyCenterSafeRadius = F("SupplyCenterSafeRadius", 300);
        RebuildDelaySeconds = I("RebuildDelayTimeSeconds", 30);
        AIDozerBoredRadiusModifier = F("AIDozerBoredRadiusModifier", 2);
        AICrushesInfantry = B("AICrushesInfantry", true);
        MaxRetaliationDistance = F("MaxRetaliationDistance", 200);
        RetaliationFriendsRadius = F("RetaliationFriendsRadius", 120);

        if (d["SideInfo"] is { IsMapping: true } sideInfo)
            foreach (var (side, node) in sideInfo.Entries)
            {
                var info = new AISideInfo
                {
                    Side = side,
                    ResourceGatherersEasy = RuleValue.Int(node.GetString("ResourceGatherersEasy"), 0),
                    ResourceGatherersNormal = RuleValue.Int(node.GetString("ResourceGatherersNormal"), 0),
                    ResourceGatherersHard = RuleValue.Int(node.GetString("ResourceGatherersHard"), 0),
                    BaseDefenseStructure1 = node.GetString("BaseDefenseStructure1"),
                };
                for (int i = 1; i <= 5; i++)
                {
                    if (node[$"SkillSet{i}"] is not { } set)
                        continue;
                    var sciences = new List<string>();
                    foreach (var science in set["Science"]?.AsList() ?? [])
                        sciences.AddRange(RuleValue.Words(science.Value));
                    info.SkillSets.Add(sciences);
                }
                sides[side] = info;
            }

        if (d["SkirmishBuildList"] is { IsMapping: true } lists)
            foreach (var (side, node) in lists.Entries)
            {
                var list = new List<AIBuildListEntry>();
                foreach (var s in node["Structure"]?.AsList() ?? [])
                    list.Add(new AIBuildListEntry(
                        s.GetString("Header"),
                        ParseLocation(s.GetString("Location")),
                        RuleValue.Float(s.GetString("Angle"), 0) * MathF.PI / 180f,
                        RuleValue.Int(s.GetString("Rebuilds"), 0),
                        RuleValue.Bool(s.GetString("InitiallyBuilt"), false),
                        RuleValue.Bool(s.GetString("AutomaticallyBuild"), true)));
                buildLists[side] = list;
            }
    }

    /// <summary>"X:501.22 Y:546.25"</summary>
    static Vector2 ParseLocation(string text)
    {
        float x = 0, y = 0;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith("X:", StringComparison.OrdinalIgnoreCase))
                x = RuleValue.Float(word[2..], 0);
            else if (word.StartsWith("Y:", StringComparison.OrdinalIgnoreCase))
                y = RuleValue.Float(word[2..], 0);
        }
        return new Vector2(x, y);
    }

    public AISideInfo? SideInfo(string side) => sides.GetValueOrDefault(side);

    /// <summary>The skirmish base layout for a side (positions relative to the start waypoint).</summary>
    public IReadOnlyList<AIBuildListEntry>? SkirmishBuildList(string side) => buildLists.GetValueOrDefault(side);
}

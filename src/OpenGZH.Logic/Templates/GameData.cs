using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Templates;

/// <summary>Global game constants from the "GameData" rules entry (rules/game-data.yaml).</summary>
public sealed class GameData
{
    readonly RuleNode node;

    public GameData(RuleSet rules)
    {
        node = rules.Section("GameData");
        UnitDamagedThreshold = Float("UnitDamagedThreshold", 0.7f);
        UnitReallyDamagedThreshold = Float("UnitReallyDamagedThreshold", 0.35f);
        HealthBonus = [1f, Float("HealthBonus_Veteran", 1f), Float("HealthBonus_Elite", 1f), Float("HealthBonus_Heroic", 1f)];
        BaseRegenHealthPercentPerSecond = Float("BaseRegenHealthPercentPerSecond", 0.02f);
        BaseRegenDelayFrames = LogicTime.Frames(Float("BaseRegenDelay", 3000));
        StandardMinefieldDensity = Float("StandardMinefieldDensity", 0.01f);
        PartitionCellSize = Float("PartitionCellSize", 40);
        ClearAlpha = (int)Float("ClearAlpha", 255);
        FogAlpha = (int)Float("FogAlpha", 127);
        ShroudAlpha = (int)Float("ShroudAlpha", 0);
        UnlookPersistFrames = LogicTime.Frames(Float("UnlookPersistDuration", 5000));
        StandardMinefieldDistance = Float("StandardMinefieldDistance", 40);
        SellPercentage = Float("SellPercentage", 0.5f);
        RefundPercentOnStructureDestroyedWhileBuilding = Float("RefundPercentOnStructureDestroyedWhileBuilding", 0f);
        LowEnergyPenaltyModifier = Float("LowEnergyPenaltyModifier", 0.6f);
        MaxRankLevel = RuleValue.Int(node.GetString("MaxRankLevel"), 5);
        DefaultStartingCash = RuleValue.Int(node.GetString("DefaultStartingCash"), 10000);
        StructureBuildSpeedRate = Float("BuildSpeed", 1f);
        MinDistanceFromMapEdgeForStructures = Float("MinDistFromEdgeOfMapForBuild", 0f);
        SupplyBuildBorder = Float("SupplyBuildBorder", 0f);
        MaxBuildDistanceFromCommandCenter = Float("MaxBuildDistanceFromCommandCenter", 0f);
        GroundStiffness = Float("GroundStiffness", 0.5f);
        StructureStiffness = Float("StructureStiffness", 0.5f);
        MaxTunnelCapacity = (int)Float("MaxTunnelCapacity", 10);
        DefaultStructureRubbleHeight = Float("DefaultStructureRubbleHeight", 1f);
        Gravity = Float("Gravity", -64f) / (LogicTime.FramesPerSecond * LogicTime.FramesPerSecond);
    }

    float Float(string key, float fallback) => RuleValue.Float(node.GetString(key), fallback);

    public string Get(string key, string fallback = "") => node.GetString(key, fallback);

    public float UnitDamagedThreshold { get; }
    public float UnitReallyDamagedThreshold { get; }

    /// <summary>Max-health multiplier per veterancy level (REGULAR, VETERAN, ELITE, HEROIC).</summary>
    public float[] HealthBonus { get; }

    public float BaseRegenHealthPercentPerSecond { get; }
    public int BaseRegenDelayFrames { get; }
    public float StandardMinefieldDensity { get; }
    public float PartitionCellSize { get; }
    public int ClearAlpha { get; }
    public int FogAlpha { get; }
    public int ShroudAlpha { get; }
    public int UnlookPersistFrames { get; }
    public float StandardMinefieldDistance { get; }
    public float SellPercentage { get; }
    public float RefundPercentOnStructureDestroyedWhileBuilding { get; }
    public float LowEnergyPenaltyModifier { get; }
    public int MaxRankLevel { get; }
    public int DefaultStartingCash { get; }
    public float StructureBuildSpeedRate { get; }
    public float MinDistanceFromMapEdgeForStructures { get; }
    public float SupplyBuildBorder { get; }

    /// <summary>OpenGZH addition: how far from a Command Center human players may build (0 = anywhere).</summary>
    public float MaxBuildDistanceFromCommandCenter { get; }
    public float GroundStiffness { get; }
    public float StructureStiffness { get; }
    public int MaxTunnelCapacity { get; }
    public float DefaultStructureRubbleHeight { get; }

    /// <summary>Gravity in world units per frame² (negative = down).</summary>
    public float Gravity { get; }
}

namespace OpenGZH.TiberianDawn.Logic;

/// <summary>DifficultyClass (RULES.H): the remaster's per-difficulty biases (all 1 by default).</summary>
public sealed class DifficultyClass
{
    public float FirepowerBias = 1, GroundspeedBias = 1, AirspeedBias = 1, ArmorBias = 1, ROFBias = 1, CostBias = 1, BuildSpeedBias = 1;
    public float RepairDelay = 0.02f, BuildDelay = 0.03f;
    public bool IsBuildSlowdown, IsWallDestroyer = true, IsContentScan;
}

/// <summary>RulesClass (RULES.CPP): the remaster's rule constants, with the original defaults.</summary>
public sealed class RulesClass
{
    public int AttackInterval = 3, BaseSizeAdd = 3, PowerSurplus = 50, AttackDelay = 5;
    public ushort PowerEmergencyFraction = 0x00C0, HelipadRatio = 0x1E, AARatio = 0x0024, DefenseRatio = 0x0066, WarRatio = 0x0019,
        BarracksRatio = 0x0028, RefineryRatio = 0x0028, AirstripRatio = 0x001E, TeslaRatio;
    public int HelipadLimit = 5, AALimit = 10, DefenseLimit = 40, WarLimit = 2, BarracksLimit = 2, RefineryLimit = 4, AirstripLimit = 5, TeslaLimit;
    public int MaxIQ = 5, IQSuperWeapons = 4, IQProduction = 5, IQGuardArea = 4, IQRepairSell = 1, IQCrush = 2, IQScatter = 3,
        IQContentScan = 4, IQAircraft = 4, IQHarvester = 2, IQSellBack = 2;
    public int InfantryReserve = 3000, InfantryBaseMult = 1;
    public bool IsComputerParanoid = true, AllowSuperWeapons = true;
    public readonly DifficultyClass[] Diff = [new(), new(), new()];
}

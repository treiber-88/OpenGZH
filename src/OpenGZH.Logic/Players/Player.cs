using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Players;

public enum Relationship { Enemies = 0, Neutral = 1, Allies = 2 }

/// <summary>
/// A player: human, computer, or the map's built-in players (civilians, neutral).
/// Owns money, power, upgrades, sciences and teams.
/// </summary>
public sealed partial class Player
{
    public GameLogic Game { get; }
    public int Index { get; }

    /// <summary>Internal name from the map, e.g. "player0" or "PlyrCivilian". Empty for the neutral player.</summary>
    public string Name { get; }
    public string DisplayName { get; set; }

    /// <summary>The faction ("PlayerTemplate"), e.g. FactionAmericaAirForceGeneral. Null for neutral.</summary>
    public RuleNode? Faction { get; }
    public string FactionName { get; }

    /// <summary>The faction's side, e.g. "America" or "AmericaAirForceGeneral" (matches objects' Side).</summary>
    public string Side { get; }

    /// <summary>The general's base side, e.g. "USA", "China", "GLA".</summary>
    public string BaseSide { get; }

    public bool IsHuman { get; set; }
    public bool IsComputer => !IsHuman && Name.Length > 0;
    public bool IsNeutral => Name.Length == 0;
    public uint Color { get; set; } = 0xFFFFFF;

    public Money Money { get; } = new();

    bool brownedOut;

    /// <summary>
    /// Low power (or sabotage) switches off POWERED buildings (DISABLED_UNDERPOWERED); restored power
    /// switches them back on. Checked whenever power changes, and every second as a catch-all.
    /// </summary>
    public void UpdatePowerBrownOut(bool force = false)
    {
        bool now = !Energy.HasSufficientPower;
        if (now == brownedOut && !force)
            return;
        brownedOut = now;
        foreach (var obj in Game.ObjectsOwnedBy(this).ToList())
        {
            if (!obj.IsKindOf("POWERED") || obj.IsUnderConstruction)
                continue;
            if (obj.IsDisabledBy("DISABLED_UNDERPOWERED") != now)
                obj.SetDisabled("DISABLED_UNDERPOWERED", now);
        }
    }

    /// <summary>The player's tunnel network (GLA): one set of occupants shared by all tunnels.</summary>
    public Modules.Contain.TunnelTracker Tunnels { get; }
    public Energy Energy { get; } = new();

    public Team DefaultTeam { get; private set; }

    /// <summary>The player's team definitions (the original's player team list).</summary>
    public List<TeamPrototype> TeamPrototypes { get; } = new();

    /// <summary>Every team instance the player owns.</summary>
    public IEnumerable<Team> Teams => TeamPrototypes.SelectMany(p => p.Instances);

    /// <summary>The team named "team" + script name becomes the default team (the original's setDefaultTeam).</summary>
    public void SetDefaultTeam()
    {
        if (Game.Teams.FindTeam("team" + ScriptName) is { } team)
        {
            DefaultTeam = team;
            team.SetActive();
        }
    }

    /// <summary>
    /// The name scripts know the player by: a skirmish computer player becomes e.g. "SkirmishAmerica2"
    /// (its side's skirmish template plus start index) while <see cref="Name"/> stays "player2".
    /// </summary>
    public string ScriptName { get; set; }

    /// <summary>The multiplayer start position (0-based; waypoint Player_N_Start is N = index + 1).</summary>
    public int MpStartIndex { get; set; }

    /// <summary>The AI's difficulty (skirmish players have their own; others use the mission's).</summary>
    public Scripting.Difficulty Difficulty { get; set; } = Scripting.Difficulty.Normal;

    /// <summary>A computer player run by the skirmish AI.</summary>
    public bool IsSkirmishAIPlayer { get; set; }

    /// <summary>The computer player's chosen enemy (the AI picks one; scripts can set it).</summary>
    public Player? CurrentEnemy { get; set; }

    /// <summary>Has this player been attacked by player N (skirmish AI conditions)?</summary>
    public bool[] AttackedBy { get; } = new bool[16];

    /// <summary>The AI player brain (skirmish or campaign computer), if any.</summary>
    public AI.AIPlayer? AI { get; set; }

    /// <summary>The map's build list for this side (the campaign AI rebuilds it).</summary>
    public List<AI.BuildListInfo> BuildList { get; } = new();

    public bool IsDefeated { get; set; }

    // Radar comes from buildings with RadarUpgrade (e.g. a Command Center). Low power switches it off,
    // unless one of the radars is "DisableProof".
    public int RadarCount { get; set; }
    public int DisableProofRadarCount { get; set; }
    public bool HasRadar => DisableProofRadarCount > 0 || (RadarCount > 0 && Energy.HasSufficientPower);
    public Vector3? StartPosition { get; set; }

    // Upgrades, sciences and the general's rank.
    readonly HashSet<string> completedUpgrades = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> sciences = new(StringComparer.OrdinalIgnoreCase);
    public int RankLevel { get; set; } = 1;
    public int SkillPoints { get; private set; }
    public int SciencePurchasePoints { get; set; }

    // Shared special power timers: one ready frame per power for all of the player's objects with it.
    readonly Dictionary<string, int> specialPowerReadyFrames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extra cash for each kill, as a fraction of the victim's cost (GLA's Cash Bounty).</summary>
    public float CashBounty { get; set; }

    /// <summary>
    /// Spy vision: players who currently see what this player's units of certain kinds see
    /// (spy player index -> KindOf filters, one entry per active spy). Read by the shroud.
    /// </summary>
    public Dictionary<int, List<string>> VisionSpiedBy { get; } = new();

    public void SetUnitsVisionSpied(bool on, string kindOf, int spyPlayer)
    {
        if (!VisionSpiedBy.TryGetValue(spyPlayer, out var list))
            VisionSpiedBy[spyPlayer] = list = new();
        if (on)
            list.Add(kindOf);
        else
            list.Remove(kindOf);
    }

    // Relationships override the default (players are enemies unless allied).
    readonly Dictionary<Player, Relationship> playerRelations = new();

    /// <summary>This player's statistics for the score screen.</summary>
    public ScoreKeeper Score { get; }

    public Player(GameLogic game, int index, string name, string factionName, RuleNode? faction)
    {
        Game = game;
        Index = index;
        Score = new ScoreKeeper(game, index);
        Name = name;
        DisplayName = name;
        FactionName = factionName;
        Faction = faction;
        Side = faction?.GetString("Side", "Civilian") ?? "Civilian";
        BaseSide = faction?.GetString("BaseSide", Side) ?? Side;
        ScriptName = name;
        DefaultTeam = game.Teams.CreateTeam(game.Teams.AddPrototype($"team{name}", this, isSingleton: true).Name);
        Tunnels = new Modules.Contain.TunnelTracker(game);
        Energy.Clock = () => game.Frame;

        if (faction != null)
        {
            Money.Deposit(RuleValue.Int(faction.GetString("StartMoney")));
            foreach (string science in RuleValue.Words(faction.GetString("IntrinsicSciences")))
                if (!science.Equals("None", StringComparison.OrdinalIgnoreCase))
                    sciences.Add(science);
            var preferred = RuleColor.Parse(faction.GetString("PreferredColor"), Vector4.One);
            Color = (uint)(preferred.X * 255) << 16 | (uint)(preferred.Y * 255) << 8 | (uint)(preferred.Z * 255);
        }
    }

    public override string ToString() => IsNeutral ? "(neutral)" : $"{DisplayName} [{FactionName}]";

    // ---- Relationships ----

    public Relationship RelationshipTo(Player other)
    {
        if (other == this)
            return Relationship.Allies;
        if (playerRelations.TryGetValue(other, out var relation))
            return relation;
        // (As the original: players are neutral to each other unless the map or the game setup says otherwise.)
        return Relationship.Neutral;
    }

    public void SetRelationship(Player other, Relationship relation) => playerRelations[other] = relation;

    public bool IsEnemyOf(Player other) => RelationshipTo(other) == Relationship.Enemies;
    public bool IsAllyOf(Player other) => RelationshipTo(other) == Relationship.Allies;

    // ---- Upgrades and sciences ----

    public bool HasUpgrade(string upgrade) => completedUpgrades.Contains(upgrade);
    public IEnumerable<string> Upgrades => completedUpgrades;

    public void CompleteUpgrade(string upgrade)
    {
        if (!completedUpgrades.Add(upgrade))
            return;
        foreach (var obj in Game.ObjectsOwnedBy(this).ToList())
            obj.CheckUpgradeModules();
        Game.Events.UpgradeCompleted(this, upgrade);
    }

    public void RemoveUpgrade(string upgrade) => completedUpgrades.Remove(upgrade);

    public bool HasScience(string science) => sciences.Contains(science);
    public IEnumerable<string> Sciences => sciences;

    /// <summary>Generals' experience: skill points raise the rank, which gives points to buy generals' powers.</summary>
    public void AddSkillPoints(int points)
    {
        points = (int)MathF.Ceiling(SkillPointsModifier * points);
        if (points == 0)
            return;
        int levelCap = Math.Min(Game.RankLevelLimit, Game.Ranks.MaxRank);
        SkillPoints = Math.Min(Game.Ranks.SkillPointsForRank(Math.Max(1, levelCap)), SkillPoints + points);
        Game.Ranks.CheckRankUp(this);
    }

    // ---- Shared special power timers (the original's quirks kept: a new timer starts out ready) ----

    public int GetOrStartSpecialPowerReadyFrame(Templates.SpecialPowerTemplate power)
    {
        if (specialPowerReadyFrames.TryGetValue(power.Name, out int frame))
            return frame;
        specialPowerReadyFrames[power.Name] = Game.Frame;
        return Game.Frame;
    }

    public void ResetOrStartSpecialPowerReadyFrame(Templates.SpecialPowerTemplate power) =>
        specialPowerReadyFrames[power.Name] = specialPowerReadyFrames.ContainsKey(power.Name) ? Game.Frame + power.ReloadFrames : Game.Frame;

    public void ExpressSpecialPowerReadyFrame(Templates.SpecialPowerTemplate power, int frame) => specialPowerReadyFrames[power.Name] = frame;

    // ---- Score ----

    /// <summary>A unit was finished and is ready to control (the original's onUnitCreated).</summary>
    public void OnUnitCreated(GameObject? factory, GameObject unit)
    {
        Score.AddObjectBuilt(unit);
        AI?.OnUnitProduced(factory, unit);
    }

    /// <summary>Skill points (general's promotion) for killing an enemy (the original's addSkillPointsForKill).</summary>
    public void AddSkillPointsForKill(GameObject victim)
    {
        int skill = RuleValue.Int(victim.Template.Rules.GetString("SkillPointValue"), -1);
        AddSkillPoints(skill >= 0 ? skill : victim.ExperienceValue);
    }

    /// <summary>Cash Bounty: a share of what the victim cost, paid when this player kills it.</summary>
    public void DoBountyForKill(GameObject killer, GameObject victim)
    {
        if (victim.IsUnderConstruction || CashBounty <= 0)
            return;
        int bounty = (int)MathF.Ceiling(Game.Costs.BuildCost(victim.Owner, victim.Template) * CashBounty);
        if (bounty <= 0)
            return;
        Money.Deposit(bounty);
        Game.Events.MoneyEarned(killer, bounty);
    }
}

/// <summary>A player's cash. Spending fails (returns false) if there is not enough.</summary>
public sealed class Money
{
    public int Amount { get; private set; }

    public void Deposit(int amount)
    {
        if (amount > 0)
            Amount += amount;
    }

    public bool TryWithdraw(int amount)
    {
        if (amount < 0 || amount > Amount)
            return false;
        Amount -= amount;
        return true;
    }

    /// <summary>Withdraws up to <paramref name="amount"/>, returning what was actually taken (for gradual build payment).</summary>
    public int WithdrawUpTo(int amount)
    {
        int taken = Math.Clamp(amount, 0, Amount);
        Amount -= taken;
        return taken;
    }
}

/// <summary>A player's power: buildings produce or consume it; low power slows production and disables powered buildings.</summary>
public sealed class Energy
{
    public int Production { get; private set; }
    public int Consumption { get; private set; }

    /// <summary>Extra multiplier on production (e.g. China's Nuclear Reactor overcharge).</summary>
    public float ProductionBonus { get; set; } = 1f;

    /// <summary>Extra power from upgrades on power plants (PowerPlantUpgrade adds the plant's EnergyBonus).</summary>
    public int Bonus { get; private set; }

    /// <summary>A saboteur blacked out the power plants until this frame.</summary>
    public int SabotagedUntilFrame { get; set; }

    /// <summary>The game's frame counter (for sabotage timing).</summary>
    public Func<int> Clock { get; set; } = () => 0;

    /// <summary>Production over consumption (1 = enough; the original's getEnergySupplyRatio).</summary>
    public float SupplyRatio =>
        Clock() < SabotagedUntilFrame ? 0 : Consumption == 0 ? (Production + Bonus) * ProductionBonus : (Production + Bonus) * ProductionBonus / Consumption;

    public bool HasSufficientPower =>
        SabotagedUntilFrame <= Clock() && (Production + Bonus) * ProductionBonus >= Consumption;

    public void AdjustBonus(int amount, bool adding) => Bonus += adding ? amount : -amount;

    /// <summary>Applies an object's EnergyProduction value: positive produces, negative consumes.</summary>
    public void Adjust(int energyProduction, bool adding)
    {
        int sign = adding ? 1 : -1;
        if (energyProduction > 0)
            Production += sign * energyProduction;
        else
            Consumption += sign * -energyProduction;
    }
}

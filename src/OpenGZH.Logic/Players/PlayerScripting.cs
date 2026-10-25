using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Players;

/// <summary>What scripts and the computer player do with a whole player (the original Player.cpp's script half).</summary>
public sealed partial class Player
{
    // ---- Relationships with teams ----

    readonly Dictionary<int, Relationship> teamRelations = new();

    /// <summary>
    /// How this player regards a team: a script override for the team, else for its player, else the
    /// normal player relationship.
    /// </summary>
    public Relationship RelationshipTo(Team? that)
    {
        if (that == null)
            return Relationship.Neutral;
        if (teamRelations.Count > 0 && teamRelations.TryGetValue(that.Id, out var r))
            return r;
        return RelationshipTo(that.ControllingPlayer);
    }

    public void SetTeamRelationship(Team that, Relationship r) => teamRelations[that.Id] = r;

    /// <summary>Removes one team override, or all of them when <paramref name="that"/> is null.</summary>
    public bool RemoveTeamRelationship(Team? that)
    {
        if (teamRelations.Count == 0)
            return false;
        if (that == null)
        {
            teamRelations.Clear();
            return true;
        }
        return teamRelations.Remove(that.Id);
    }

    public bool RemovePlayerRelationship(Player? that)
    {
        if (that == null)
        {
            bool any = playerRelations.Count > 0;
            playerRelations.Clear();
            return any;
        }
        return playerRelations.Remove(that);
    }

    /// <summary>One bit per player (the original's PlayerMaskType).</summary>
    public uint PlayerMask => 1u << Index;

    // ---- Script switches ----

    public bool UnitsShouldHunt { get; private set; }
    public bool CanBuildBase { get; set; } = true;
    public bool CanBuildUnits { get; set; } = true;
    public bool ListInScoreScreen { get; set; } = true;
    public bool IsPlayerDead { get; private set; }

    /// <summary>Scales experience the general earns (PLAYER_AFFECT_RECEIVING_EXPERIENCE).</summary>
    public float SkillPointsModifier { get; set; } = 1f;

    /// <summary>The original's allowedToBuild: scripts can stop base or unit construction.</summary>
    public bool AllowedToBuild(Templates.ObjectTemplate template) =>
        template.Is("STRUCTURE") ? CanBuildBase : CanBuildUnits;

    IEnumerable<GameObject> TeamMembers() => Teams.ToList().SelectMany(t => t.AllMembers.ToList());

    /// <summary>PLAYER_HUNT: every unit except dozers, harvesters and IGNORES_SELECT_ALL things hunts (or goes idle).</summary>
    public void SetUnitsShouldHunt(bool hunt, CommandSource source)
    {
        UnitsShouldHunt = hunt;
        foreach (var obj in TeamMembers())
        {
            if (obj.IsKindOf(Kind.Dozer) || obj.IsKindOf(Kind.Harvester) || obj.IsKindOf("IGNORES_SELECT_ALL"))
                continue;
            obj.LeaveGroup();
            if (obj.AI is { } ai)
            {
                if (hunt)
                    ai.Hunt(source);
                else
                    ai.Stop(source);
            }
        }
    }

    /// <summary>The original's killPlayer: everyone out of their transports, then everything dies, and the money goes.</summary>
    public void KillPlayer()
    {
        foreach (var team in Teams.ToList())
            team.EvacuateTeam();
        IsPlayerDead = true;
        foreach (var team in Teams.ToList())
            team.KillTeam();
        if (Game.IsSinglePlayer && !IsHuman)
        {
            IsPlayerDead = false;
            return;
        }
        Money.WithdrawUpTo(Money.Amount);
    }

    /// <summary>PLAYER_ENABLE/DISABLE_FACTORIES: switches every object of a type on or off.</summary>
    public void SetObjectsEnabled(string templateName, bool enable)
    {
        foreach (var obj in TeamMembers())
            if (obj.Template.Name.Equals(templateName, StringComparison.Ordinal))
                obj.SetScriptStatus("SCRIPT_DISABLED", !enable);
    }

    /// <summary>Takes over everything another player has (except its radar beacons) and all its money.</summary>
    public void TransferAssetsFromThat(Player that)
    {
        var defaultTeam = DefaultTeam;
        string beacon = that.Faction?.GetString("BeaconName") ?? "";
        var toTransfer = that.TeamMembers().Where(o => !o.Template.Name.Equals(beacon, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var obj in toTransfer)
            obj.SetTeam(defaultTeam);
        int all = that.Money.Amount;
        that.Money.WithdrawUpTo(all);
        Money.Deposit(all);
    }

    /// <summary>Every unit tries to get into every building it can (nearest buildings first).</summary>
    public void GarrisonAllUnits(CommandSource source)
    {
        var anchor = new Vector3(50, 50, 50);
        var buildings = Game.Objects.Where(o => o.IsKindOf(Kind.Structure) && !o.IsDestroyed)
            .OrderBy(o => Vector3.DistanceSquared(o.Position, anchor)).ToList();
        foreach (var obj in TeamMembers())
        {
            if (obj.AI is not { } ai)
                continue;
            foreach (var building in buildings)
            {
                if (building.Contain is { } contain)
                {
                    uint entered = contain.PlayerWhoEntered;
                    if (!(entered == 0 || entered == obj.Owner.PlayerMask))
                        continue;
                    if (!contain.IsValidContainerFor(obj, checkCapacity: true))
                        continue;
                }
                else
                    continue;
                ai.Enter(building, source);
            }
        }
    }

    /// <summary>Every building of the player empties out.</summary>
    public void UngarrisonAllUnits(CommandSource source)
    {
        foreach (var obj in TeamMembers())
            if (obj.AI is { } ai && obj.IsKindOf(Kind.Structure))
                ai.Evacuate(source);
    }

    /// <summary>IDLE_ALL_UNITS / RESUME_SUPPLY_TRUCKING: units stop where they are, or idle supply trucks go back to work.</summary>
    public void SetUnitsShouldIdleOrResume(bool idle)
    {
        foreach (var obj in TeamMembers())
        {
            if (obj.IsKindOf(Kind.Structure) || obj.AI is not { } ai)
                continue;
            if (idle)
                ai.MoveTo(obj.Position, CommandSource.Script);
            else if (ai.IsIdle && Modules.Supply.SupplyGatherer.Of(obj) is { } gatherer)
                gatherer.ForceWanting = true;
        }
    }

    /// <summary>Sells every faction building, command center and power plant.</summary>
    public void SellEverythingUnderTheSun()
    {
        foreach (var obj in Game.ObjectsOwnedBy(this).ToList())
            if (obj.IsFactionStructure || obj.IsKindOf("COMMANDCENTER") || obj.IsKindOf("FS_POWER"))
                Game.Building.Sell(obj);
    }

    // ---- Computer player hooks (do nothing for humans) ----

    public void BuildSpecificTeam(TeamPrototype proto) => AI?.BuildSpecificAITeam(proto, priorityBuild: true);
    public void BuildBaseDefense(bool flank) => AI?.BuildAIBaseDefense(flank);
    public void BuildBaseDefenseStructure(string thingName, bool flank) => AI?.BuildAIBaseDefenseStructure(thingName, flank);
    public void BuildSpecificBuilding(string thingName) => AI?.BuildSpecificAIBuilding(thingName);
    public void BuildBySupplies(int minimumCash, string thingName) => AI?.BuildBySupplies(minimumCash, thingName);
    public void BuildSpecificBuildingNearestTeam(string thingName, Team team) => AI?.BuildSpecificBuildingNearestTeam(thingName, team);
    public void BuildUpgrade(string upgrade) => AI?.BuildUpgrade(upgrade);
    public void RecruitSpecificTeam(TeamPrototype proto, float recruitRadius) => AI?.RecruitSpecificAITeam(proto, recruitRadius);
    public void RepairStructure(uint structureId) => AI?.RepairStructure(structureId);
    public void SetTeamDelaySeconds(int delay) => AI?.SetTeamDelaySeconds(delay);
    public void GuardSupplyCenter(Team team, int minSupplies) => AI?.GuardSupplyCenter(team, minSupplies);
    public bool CheckBridges(GameObject unit, Terrain.Waypoint way) => AI?.CheckBridges(unit, way) ?? false;
    public bool IsSupplySourceSafe(int minSupplies) => AI?.IsSupplySourceSafe(minSupplies) ?? true;
    public bool IsSupplySourceAttacked() => AI?.IsSupplySourceAttacked() ?? false;
    public void SelectSkillset(int skillset) => AI?.SelectSkillset(skillset);

    public bool ComputeSuperweaponTarget(Templates.SpecialPowerTemplate power, out Vector3 position, int playerIndex, float weaponRadius)
    {
        position = default;
        return AI?.ComputeSuperweaponTarget(power, out position, playerIndex, weaponRadius) ?? false;
    }

    public bool CalcClosestConstructionZoneLocation(Templates.ObjectTemplate template, ref Vector3 location) =>
        AI?.CalcClosestConstructionZoneLocation(template, ref location) ?? false;

    /// <summary>Cash for one supply box (GameData ValuePerSupplyBox).</summary>
    public int SupplyBoxValue => RuleValue.Int(Game.Rules.Section("GameData").GetString("ValuePerSupplyBox"), 75);

    // ---- Sciences ----

    readonly HashSet<string> sciencesDisabled = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> sciencesHidden = new(StringComparer.OrdinalIgnoreCase);

    public bool IsScienceDisabled(string science) => sciencesDisabled.Contains(science);
    public bool IsScienceHidden(string science) => sciencesHidden.Contains(science);

    /// <summary>PLAYER_SCIENCE_AVAILABILITY: Available, Disabled (shown greyed) or Hidden.</summary>
    public void SetScienceAvailability(string science, string availability)
    {
        if (!sciencesDisabled.Remove(science))
            sciencesHidden.Remove(science);
        if (availability.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            sciencesDisabled.Add(science);
        else if (availability.Equals("Hidden", StringComparison.OrdinalIgnoreCase))
            sciencesHidden.Add(science);
    }

    public static bool IsValidScienceAvailability(string name) =>
        name.Equals("Available", StringComparison.OrdinalIgnoreCase) || name.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Hidden", StringComparison.OrdinalIgnoreCase);

    public int SciencePurchaseCost(string science) => RuleValue.Int(Game.Catalog.Science(science)?.GetString("SciencePurchasePointCost"), 0);

    public bool HasPrereqsForScience(string science)
    {
        foreach (string prereq in RuleValue.Words(Game.Catalog.Science(science)?.GetString("PrerequisiteSciences")))
            if (!prereq.Equals("None", StringComparison.OrdinalIgnoreCase) && !HasScience(prereq))
                return false;
        return true;
    }

    /// <summary>The original's isCapableOfPurchasingScience.</summary>
    public bool IsCapableOfPurchasingScience(string science)
    {
        if (Game.Catalog.Science(science) == null || HasScience(science))
            return false;
        if (IsScienceDisabled(science) || IsScienceHidden(science))
            return false;
        if (!HasPrereqsForScience(science))
            return false;
        int cost = SciencePurchaseCost(science);
        return cost != 0 && cost <= SciencePurchasePoints;
    }

    public bool AttemptToPurchaseScience(string science)
    {
        if (!IsCapableOfPurchasingScience(science))
            return false;
        SciencePurchasePoints = Math.Max(0, SciencePurchasePoints - SciencePurchaseCost(science));
        AddScience(science);
        return true;
    }

    /// <summary>
    /// The original's addScience: the player now has it; powers that needed it start working (ready
    /// at once), and scripts hear about it.
    /// </summary>
    public bool AddScience(string science)
    {
        if (!sciences.Add(science))
            return false;
        foreach (var obj in TeamMembers())
            foreach (var module in obj.FindModules<Modules.SpecialPowers.SpecialPowerModule>().ToList())
                if (module.Power is { } power && power.RequiredScience.Equals(science, StringComparison.OrdinalIgnoreCase))
                {
                    module.OnSpecialPowerCreation();
                    module.SetReadyFrame(Game.Frame);
                }
        Game.Events.ScienceGranted(this, science);
        Game.Scripts?.NotifyOfAcquiredScience(Index, science);
        return true;
    }

    /// <summary>The original's grantScience: only sciences marked IsGrantable can be given away.</summary>
    public bool GrantScience(string science)
    {
        var node = Game.Catalog.Science(science);
        if (node != null && !RuleValue.Bool(node.GetString("IsGrantable"), true))
        {
            Log.Warning($"Cannot grant science {science}, since it is marked as nonGrantable.");
            return false;
        }
        return AddScience(science);
    }

    /// <summary>
    /// The original's setRankLevel: going down starts again from rank 1; each rank gained gives its
    /// purchase points and sciences, and raises the skill points to that rank's.
    /// </summary>
    public bool SetRankLevel(int newLevel)
    {
        var ranks = Game.Ranks;
        newLevel = Math.Clamp(newLevel, 1, Math.Max(1, ranks.MaxRank));
        newLevel = Math.Min(newLevel, Game.RankLevelLimit);
        if (newLevel == RankLevel)
            return false;
        if (newLevel < RankLevel)
            ResetRank();
        for (int i = RankLevel + 1; i <= newLevel; i++)
        {
            var rank = ranks.Rank(i);
            SciencePurchasePoints = Math.Max(0, SciencePurchasePoints + rank.SciencePurchasePointsGranted);
            if (SkillPoints < rank.SkillPointsNeeded)
                SkillPoints = rank.SkillPointsNeeded;
            foreach (string science in rank.SciencesGranted)
                AddScience(science);
        }
        RankLevel = newLevel;
        if (IsLocalPlayer)
            Game.Events.Eva("GeneralLevelUp", this);
        return true;
    }

    void ResetRank()
    {
        RankLevel = 1;
        SkillPoints = 0;
        sciences.Clear();
        SciencePurchasePoints = RuleValue.Int(Faction?.GetString("IntrinsicSciencePurchasePoints"), 0) + Game.Ranks.Rank(1).SciencePurchasePointsGranted;
        if (Faction != null)
            foreach (string science in RuleValue.Words(Faction.GetString("IntrinsicSciences")))
                if (!science.Equals("None", StringComparison.OrdinalIgnoreCase))
                    sciences.Add(science);
        foreach (string science in Game.Ranks.Rank(1).SciencesGranted)
            AddScience(science);
    }

    public bool IsLocalPlayer => Game.Players.Local == this;
}

public sealed partial class Player
{
    /// <summary>The last frame something of this player's was attacked (the original's m_attackedFrame).</summary>
    public int AttackedFrame { get; private set; }

    /// <summary>Notes who attacked this player (skirmish conditions ask).</summary>
    public void SetAttackedBy(int playerIndex)
    {
        if (playerIndex >= 0 && playerIndex < AttackedBy.Length)
            AttackedBy[playerIndex] = true;
        AttackedFrame = Game.Frame;
    }

    /// <summary>A factory the player already has joins its build list (the original's addToBuildList).</summary>
    public void AddToBuildList(GameObject obj) => BuildList.Insert(0, new AI.BuildListInfo
    {
        ObjectId = obj.Id,
        TemplateName = obj.Template.Name,
        Location = obj.Position,
        Angle = obj.Orientation,
        NumRebuilds = 0,
    });

    /// <summary>A script-requested structure, built before the rest (the original's addToPriorityBuildList).</summary>
    public void AddToPriorityBuildList(string templateName, Vector3 pos, float angle)
    {
        var info = new AI.BuildListInfo { TemplateName = templateName, Location = pos, Angle = angle, NumRebuilds = 1 };
        info.MarkPriorityBuild();
        BuildList.Insert(0, info);
    }

    public bool HasAnyObjects() => TeamPrototypes.Any(p => p.HasAnyObjects());
    public bool HasAnyUnits() => TeamPrototypes.Any(p => p.HasAnyUnits());
    public bool HasAnyBuildings() => TeamPrototypes.Any(p => p.HasAnyBuildings());
    public bool HasAnyBuildFacility() => TeamPrototypes.Any(p => p.HasAnyBuildFacility());

    public bool HasAnyBuildings(string kindOf) => TeamPrototypes.Any(p => p.Instances.Any(t => t.HasAnyBuildings(kindOf)));

    /// <summary>
    /// The original's setPlayerType: a computer player gets a brain (the skirmish one for skirmish
    /// slots, or when AIData's ForceSkirmishAI is on).
    /// </summary>
    public void SetPlayerType(bool human, bool skirmish)
    {
        IsHuman = human;
        AI = null;
        if (!human)
            AI = OpenGZH.Core.Rules.RuleValue.Bool(Faction?.GetString("TdFaction") ?? "") ? new AI.TdFactionAI(this) // OpenGZH: a Tiberian Dawn faction
                : skirmish || Game.AIData.ForceSkirmishAI ? new AI.AISkirmishPlayer(this) : new AI.AIPlayer(this);
    }

    /// <summary>The original's Player::update: the brain thinks, then every team's generic scripts run.</summary>
    public void Update()
    {
        AI?.Update();
        foreach (var team in Teams.ToList())
            team.UpdateGenericScripts();
    }

    /// <summary>The original's canBuild: allowed by scripts, buildable, prerequisites met, and not at the maximum.</summary>
    public bool CanBuild(Templates.ObjectTemplate template)
    {
        if (!AllowedToBuild(template))
            return false;
        string buildable = Game.BuildableStatusOverrides.GetValueOrDefault(template.Name) ?? template.Rules.GetString("Buildable", "Yes");
        if (buildable.Equals("No", StringComparison.OrdinalIgnoreCase))
            return false;
        if (buildable.Equals("Ignore_Prerequisites", StringComparison.OrdinalIgnoreCase))
            return true;
        if (buildable.Equals("Only_By_AI", StringComparison.OrdinalIgnoreCase) && IsHuman)
            return false;
        if (!Modules.Production.Prerequisites.AreMet(Game, this, template.Rules))
            return false;
        return CanBuildMoreOfType(template);
    }

    /// <summary>MaxSimultaneousOfType (counting what is queued, for units).</summary>
    public bool CanBuildMoreOfType(Templates.ObjectTemplate template)
    {
        int max = Game.MaxSimultaneousOfType(template);
        if (max == 0)
            return true;
        int count = Game.ObjectsOwnedBy(this).Count(o => !o.IsEffectivelyDead && o.Template.IsEquivalentTo(template));
        if (!template.Is("STRUCTURE"))
            count += Game.ObjectsOwnedBy(this).Sum(o => o.FindModule<Modules.Production.ProductionUpdate>()?.Queue.Count(e => e.Unit == template) ?? 0);
        return count < max;
    }

    /// <summary>A faction a person can pick (the original's isPlayableSide): not civilians or observers.</summary>
    public bool IsPlayableSide => Faction != null && OpenGZH.Core.Rules.RuleValue.Bool(Faction.GetString("PlayableSide"));
}

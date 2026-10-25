using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Construction;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Behaviors;
using OpenGZH.Logic.Modules.Production;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.AI;

/// <summary>
/// OpenGZH addition: the computer player for a Tiberian Dawn faction in a Zero Hour engine match.
/// The Zero Hour skirmish AI is driven by map scripts and build lists made for ZH factions, so a TD
/// faction gets Tiberian Dawn's own skirmish brain instead (the remaster's USE_RA_AI code in HOUSE.CPP):
/// <list type="bullet">
/// <item>it unfolds its MCV (UnitClass::Mission_Guard deploys a computer MCV);</item>
/// <item>HouseClass::AI_Building picks the next structure by urgency, with TD's RULES ratios and
/// limits, and the yard's finished structure is placed next to the base (Find_Build_Location);</item>
/// <item>AI_Unit / AI_Infantry: a harvester per refinery, then a weighted random pick (armed units 20, others 1);</item>
/// <item>attacks: TD sends its autocreated teams out; here, once AttackDelay minutes have passed, every
/// AttackInterval minutes the idle army attack-moves on the nearest enemy structure.</item>
/// </list>
/// TD's object types are recognised by the TdType each exported object carries (STRUCT_POWER, UNIT_HARVESTER...).
/// </summary>
public sealed class TdFactionAI : AIPlayer
{
    // RULES.INI defaults of Tiberian Dawn's RuleClass (ratios are fixed point, 256 = 1).
    const int PowerSurplus = 50, AttackIntervalMinutes = 3, AttackDelayMinutes = 5;
    const int RefineryRatio = 0x28, BarracksRatio = 0x28, WarRatio = 0x19, DefenseRatio = 0x66, AARatio = 0x24, HelipadRatio = 0x1E, AirstripRatio = 0x1E;
    const int RefineryLimit = 4, BarracksLimit = 2, WarLimit = 2, DefenseLimit = 40, AALimit = 10, HelipadLimit = 5, AirstripLimit = 5;
    const int ThinkFrames = LogicTime.FramesPerSecond; // AI_Building returns TICKS_PER_SECOND
    const float TdCell = 36;

    enum Urgency { None, Low, Medium, High, Critical }

    int nextThinkFrame;
    int nextAttackFrame;

    public TdFactionAI(Player p) : base(p)
    {
        p.CanBuildUnits = true;
        nextAttackFrame = game.Frame + AttackDelayMinutes * 60 * LogicTime.FramesPerSecond;
    }

    public override bool IsSkirmishAI => true;

    /// <summary>A Tiberian Dawn object type name (STRUCT_POWER, UNIT_MTANK...) from the exported Draw module, or "".</summary>
    public static string TdTypeOf(ObjectTemplate template)
    {
        foreach (var module in template.Modules)
            if (module.ClassName.Equals("TDSpriteDraw", StringComparison.OrdinalIgnoreCase))
                return module.Settings.GetString("TdType");
        return "";
    }

    public override void Update()
    {
        if (game.Frame < nextThinkFrame)
            return;
        nextThinkFrame = game.Frame + ThinkFrames;

        var mine = game.ObjectsOwnedBy(player).Where(o => !o.IsEffectivelyDead).ToList();
        DeployMcv(mine);
        PlaceReadyStructures(mine);
        BuildStructure(mine);
        BuildUnits(mine);
        Attack(mine);
    }

    public override void NewMap() => ComputeBase();

    void ComputeBase()
    {
        var yard = game.ObjectsOwnedBy(player).FirstOrDefault(o => o.IsKindOf("COMMANDCENTER"));
        if (yard != null)
        {
            baseCenter = yard.Position;
            baseCenterSet = true;
        }
        else if (player.StartPosition is { } start)
        {
            baseCenter = start;
            baseCenterSet = true;
        }
    }

    // ---- MCV ----

    /// <summary>A computer MCV unfolds where it stands, or drives a little way off if the yard doesn't fit.</summary>
    void DeployMcv(List<GameObject> mine)
    {
        foreach (var obj in mine)
        {
            if (obj.FindModule<TdDeployToStructure>() is not { } deploy || obj.AI is not { } ai || ai.IsMoving)
                continue;
            if (deploy.CanDeploy())
            {
                deploy.Deploy();
                ComputeBase();
            }
            else
                ai.MoveTo(obj.Position + MathUtil.Forward(game.Random.Float(0, MathF.Tau)) * TdCell * 2, Commands.CommandSource.AI);
        }
    }

    // ---- Structures ----

    static int Count(List<GameObject> mine, params string[] types) =>
        mine.Count(o => Array.IndexOf(types, TdTypeOf(o.Template)) >= 0 && !o.IsUnderConstruction);

    /// <summary>TD's Round_Up(Ratio(ratio, CurBuildings)).</summary>
    static int Wanted(int ratio, int buildings) => (ratio * buildings + 255) / 256;

    /// <summary>HouseClass::AI_Building: the most urgent structure the yard should make next.</summary>
    void BuildStructure(List<GameObject> mine)
    {
        var yard = mine.FirstOrDefault(o => o.FindModule<ProductionUpdate>() is { PlacesStructures: true });
        if (yard?.FindModule<ProductionUpdate>() is not { } production || production.Queue.Count > 0)
            return;

        var buildable = CommandSets.BuildableObjects(game.Catalog, yard.CommandSetName, "UNIT_BUILD")
            .Select(n => game.Templates.Find(n)).OfType<ObjectTemplate>()
            .ToDictionary(t => TdTypeOf(t), StringComparer.OrdinalIgnoreCase);
        int money = player.Money.Amount;
        int buildings = mine.Count(o => o.IsKindOf(Kind.Structure));
        bool hasIncome = Count(mine, "STRUCT_REFINERY") > 0 && Count(mine, "UNIT_HARVESTER") > 0;
        var choices = new List<(Urgency Urgency, ObjectTemplate Template)>();

        bool CanBuild(string type, out ObjectTemplate template) =>
            buildable.TryGetValue(type, out template!) && production.CanQueueUnit(template) is null or "NotEnoughMoney" && player.CanBuild(template);
        bool Affordable(ObjectTemplate t) => t.BuildCost < money || hasIncome;
        void Choose(Urgency urgency, ObjectTemplate template) => choices.Add((urgency, template));

        int refineries = Count(mine, "STRUCT_REFINERY");
        var energy = player.Energy;
        if (energy.Production + energy.Bonus <= energy.Consumption + PowerSurplus)
        {
            if (CanBuild("STRUCT_ADVANCED_POWER", out var power) && power.BuildCost < money
                || CanBuild("STRUCT_POWER", out power) && power.BuildCost < money)
                Choose(refineries == 0 ? Urgency.Low : Urgency.Medium, power);
        }

        if (refineries < Wanted(RefineryRatio, buildings) && refineries < RefineryLimit
            && CanBuild("STRUCT_REFINERY", out var refinery) && (money > refinery.BuildCost || hasIncome))
            Choose(refineries == 0 ? Urgency.High : Urgency.Medium, refinery);

        int barracks = Count(mine, "STRUCT_BARRACKS", "STRUCT_HAND");
        if (barracks < Wanted(BarracksRatio, buildings) && barracks < BarracksLimit && (money > 300 || hasIncome)
            && (CanBuild("STRUCT_BARRACKS", out var rax) || CanBuild("STRUCT_HAND", out rax)) && Affordable(rax))
            Choose(barracks > 0 ? Urgency.Low : Urgency.Medium, rax);

        int factories = Count(mine, "STRUCT_WEAP");
        if (factories < Wanted(WarRatio, buildings) && factories < WarLimit && (money > 2000 || hasIncome)
            && CanBuild("STRUCT_WEAP", out var weap) && Affordable(weap))
            Choose(factories > 0 ? Urgency.Low : Urgency.Medium, weap);

        // TD's AI only knows the Obelisk and the Nod turret; GDI's towers stand in for them here.
        int defenses = Count(mine, "STRUCT_TURRET", "STRUCT_OBELISK", "STRUCT_GTOWER", "STRUCT_ATOWER");
        if (defenses < Wanted(DefenseRatio, buildings) && defenses < DefenseLimit
            && (CanBuild("STRUCT_OBELISK", out var defense) || CanBuild("STRUCT_TURRET", out defense)
                || CanBuild("STRUCT_ATOWER", out defense) || CanBuild("STRUCT_GTOWER", out defense)) && Affordable(defense))
            Choose(Urgency.Medium, defense);

        int sams = Count(mine, "STRUCT_SAM");
        int enemyAircraft = game.Players.Active.Where(IsEnemy).Sum(p => game.ObjectsOwnedBy(p).Count(o => o.IsKindOf(Kind.Aircraft)));
        if (sams < Wanted(AARatio, buildings) && sams < AALimit && enemyAircraft > 0)
        {
            if (Count(mine, "STRUCT_RADAR") == 0 && CanBuild("STRUCT_RADAR", out var radar) && Affordable(radar))
                Choose(Urgency.High, radar);
            if (CanBuild("STRUCT_SAM", out var sam) && Affordable(sam))
                Choose(sams < enemyAircraft ? Urgency.High : Urgency.Medium, sam);
        }

        int ownAircraft = mine.Count(o => o.IsKindOf(Kind.Aircraft));
        int helipads = Count(mine, "STRUCT_HELIPAD");
        if (helipads < Wanted(HelipadRatio, buildings) && helipads < HelipadLimit && CanBuild("STRUCT_HELIPAD", out var pad) && Affordable(pad))
            Choose(ownAircraft < enemyAircraft ? Urgency.High : Urgency.Medium, pad);

        int airstrips = Count(mine, "STRUCT_AIRSTRIP");
        if (airstrips < Wanted(AirstripRatio, buildings) && airstrips < AirstripLimit && CanBuild("STRUCT_AIRSTRIP", out var strip) && Affordable(strip))
            Choose(ownAircraft < enemyAircraft ? Urgency.High : Urgency.Medium, strip);

        // The most urgent choice (the first one, on a tie).
        var best = choices.Where(c => c.Urgency != Urgency.None).OrderByDescending(c => c.Urgency).Select(c => c.Template).FirstOrDefault();
        if (best != null)
            production.QueueUnit(best);
    }

    /// <summary>A finished structure goes next to the base: the nearest legal spot to the base centre (Find_Build_Location).</summary>
    void PlaceReadyStructures(List<GameObject> mine)
    {
        foreach (var factory in mine)
        {
            if (factory.FindModule<ProductionUpdate>() is not { } production || production.ReadyStructure is not { Unit: { } template } entry)
                continue;
            if (FindBuildLocation(template) is { } spot)
                production.PlaceReadyStructure(entry.Id, spot, 0);
        }
    }

    Vector3? FindBuildLocation(ObjectTemplate template)
    {
        if (!baseCenterSet)
            ComputeBase();
        var center = baseCenter;
        // Refineries go toward Tiberium, as TD's AI places them near the richest field.
        if (TdTypeOf(template) == "STRUCT_REFINERY")
        {
            var field = game.Objects.Where(o => o.IsKindOf(Kind.SupplySource) && MathUtil.Distance2D(o.Position, center) < 600)
                .OrderBy(o => MathUtil.Distance2D(o.Position, center)).FirstOrDefault();
            if (field != null)
                center += Vector3.Normalize((field.Position - center) with { Z = 0 } + new Vector3(0.001f, 0, 0)) * 100;
        }
        float start = game.Random.Float(0, MathF.Tau);
        for (float r = 0; r <= TdCell * 16; r += TdCell / 2)
        {
            int steps = Math.Max(1, (int)(r * MathF.Tau / (TdCell / 2)));
            for (int i = 0; i < steps; i++)
            {
                float a = start + i * MathF.Tau / steps;
                var p = center + new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * r;
                p.Z = game.Terrain.GroundHeight(p);
                if (game.Building.CheckLocation(template, p, 0, player) == BuildLocation.Ok)
                    return p;
            }
        }
        return null;
    }

    // ---- Units ----

    /// <summary>AI_Unit / AI_Infantry / AI_Aircraft: each idle factory gets something to make.</summary>
    void BuildUnits(List<GameObject> mine)
    {
        int harvesters = Count(mine, "UNIT_HARVESTER");
        int refineries = Count(mine, "STRUCT_REFINERY");
        foreach (var factory in mine)
        {
            if (factory.IsUnderConstruction || factory.FindModule<ProductionUpdate>() is not { PlacesStructures: false } production || production.Queue.Count > 0)
                continue;
            var options = CommandSets.BuildableObjects(game.Catalog, factory.CommandSetName, "UNIT_BUILD")
                .Select(n => game.Templates.Find(n)).OfType<ObjectTemplate>()
                .Where(t => production.CanQueueUnit(t) == null && player.CanBuild(t)).ToList();
            if (options.Count == 0)
                continue;

            // A harvester for each refinery first.
            if (refineries > harvesters && options.FirstOrDefault(t => TdTypeOf(t) == "UNIT_HARVESTER") is { } harvester)
            {
                production.QueueUnit(harvester);
                harvesters++;
                continue;
            }

            // Then a weighted random pick: armed units 20, others 1 (never a harvester or an MCV).
            var weighted = options.Where(t => TdTypeOf(t) is not ("UNIT_HARVESTER" or "UNIT_MCV"))
                .Select(t => (Template: t, Weight: t.Rules.TryGet("WeaponSet", out _) ? 20 : 1)).ToList();
            int total = weighted.Sum(w => w.Weight);
            if (total == 0)
                continue;
            int choice = game.Random.Int(0, total - 1);
            foreach (var (template, weight) in weighted)
            {
                if (choice < weight)
                {
                    production.QueueUnit(template);
                    break;
                }
                choice -= weight;
            }
        }
    }

    // ---- Attacks ----

    bool IsEnemy(Player other) => other != player && !other.IsNeutral && player.RelationshipTo(other) == Relationship.Enemies;

    void Attack(List<GameObject> mine)
    {
        if (game.Frame < nextAttackFrame)
            return;
        nextAttackFrame = game.Frame + AttackIntervalMinutes * 60 * LogicTime.FramesPerSecond;

        var army = mine.Where(o => o.IsMobile && o.AI is { IsIdle: true } && o.Template.Rules.TryGet("WeaponSet", out _)
            && TdTypeOf(o.Template) is not ("UNIT_HARVESTER" or "UNIT_MCV")).ToList();
        if (army.Count < 4)
            return;
        var from = baseCenterSet ? baseCenter : army[0].Position;
        var target = game.Players.Active.Where(IsEnemy).SelectMany(p => game.ObjectsOwnedBy(p))
            .Where(o => !o.IsEffectivelyDead && (o.IsKindOf(Kind.Structure) || o.IsMobile))
            .OrderBy(o => o.IsKindOf(Kind.Structure) ? 0 : 1).ThenBy(o => MathUtil.Distance2D(o.Position, from)).FirstOrDefault();
        if (target == null)
            return;
        foreach (var unit in army)
            unit.AI!.AttackMoveTo(target.Position, Commands.CommandSource.AI);
    }
}

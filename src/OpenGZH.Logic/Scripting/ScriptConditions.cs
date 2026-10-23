using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;
using OpenGZH.Logic.Vision;

namespace OpenGZH.Logic.Scripting;

/// <summary>
/// Script conditions, by their original internal names (the original's ScriptConditions). Each
/// reads its parameters as the original does; players, teams and units are looked up through the
/// engine so "&lt;This Player&gt;", "&lt;This Team&gt;" and skirmish names work.
/// Unknown conditions are reported once and are false.
/// </summary>
public sealed class ScriptConditions
{
    readonly GameLogic game;
    readonly ScriptEngine engine;
    readonly Dictionary<string, Func<ScriptCondition, bool>> handlers = new(StringComparer.Ordinal);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> reported = new();

    sealed class TransportStatus
    {
        public int FrameNumber, UnitCount;
    }

    readonly Dictionary<uint, TransportStatus> transportStatuses = new();
    readonly Dictionary<(int Player, string Type), int> objectCounts = new();

    /// <summary>Has a music track finished playing N times? (The client knows; without it, never.)</summary>
    public static Func<string, int, bool> MusicTrackCompleted { get; set; } = (_, _) => false;

    /// <summary>How long a sound or speech event lasts in milliseconds (the client knows; without it, 0).</summary>
    public static Func<string, float> AudioLengthMs { get; set; } = _ => 0;

    public ScriptConditions(GameLogic game, ScriptEngine engine)
    {
        this.game = game;
        this.engine = engine;
        Register();
    }

    public static IEnumerable<string> Missing => reported.Keys;

    public void Update() { }

    public bool Evaluate(ScriptCondition condition)
    {
        if (handlers.TryGetValue(condition.Name, out var handler))
            return handler(condition);
        if (reported.TryAdd(condition.Name, 0))
            Log.Warning($"Script condition '{condition.Name}' is not implemented yet.");
        return false;
    }

    // ---- Parameter helpers ----

    Player? PlayerFrom(ScriptParam p) => engine.GetPlayer(p.String);
    Team? TeamFrom(ScriptParam p) => engine.GetTeamNamed(p.String);
    GameObject? UnitFrom(ScriptParam p) => engine.GetUnitNamed(p.String);
    Terrain.TriggerArea? AreaFrom(ScriptParam p) => engine.GetQualifiedTriggerArea(p.String);

    /// <summary>The object types a parameter means: a script list of types, or one type.</summary>
    HashSet<string> TypesFrom(ScriptParam p)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (p.String.Length == 0)
            return set;
        foreach (var name in engine.ObjectTypeNames(p.String))
            set.Add(name);
        return set;
    }

    List<ObjectTemplate> TemplatesFrom(HashSet<string> types) =>
        types.Select(n => game.Templates.Find(n)).Where(t => t != null).Select(t => t!).ToList();

    static bool Cmp(int value, ScriptParam comparison, int against) => ScriptEngine.Compare(value, (Comparison)comparison.Int, against);

    /// <summary>The player's team members (the original iterates the player's teams).</summary>
    static IEnumerable<GameObject> MembersOf(Player player) => player.Teams.SelectMany(t => t.AllMembers);

    /// <summary>The original's countObjectsByThingTemplate: under-construction objects never count.</summary>
    static int CountByTemplates(Player player, List<ObjectTemplate> templates, bool ignoreDead) =>
        MembersOf(player).Count(o => templates.Any(t => o.Template.IsEquivalentTo(t)) && !(ignoreDead && o.IsEffectivelyDead) && !o.IsUnderConstruction);

    static bool HasAnyObjects(Player p) => p.Teams.Any(t => t.HasAnyObjects());
    static bool HasAnyBuildFacility(Player p) => p.Teams.Any(t => t.HasAnyBuildFacility());

    void Register()
    {
        handlers["PLAYER_ALL_DESTROYED"] = c => PlayerFrom(c[0]) is not { } p || !HasAnyObjects(p);
        handlers["PLAYER_ALL_BUILDFACILITIES_DESTROYED"] = c => PlayerFrom(c[0]) is not { } p || !HasAnyBuildFacility(p);
        handlers["TEAM_DESTROYED"] = c => TeamFrom(c[0]) is { } t && !t.HasAnyObjects();
        handlers["BRIDGE_BROKEN"] = c => false;
        handlers["BRIDGE_REPAIRED"] = c => false;
        handlers["NAMED_DESTROYED"] = c => UnitFrom(c[0]) is { } u ? u.IsEffectivelyDead : engine.DidUnitExist(c[0].String);
        handlers["NAMED_NOT_DESTROYED"] = c => UnitFrom(c[0]) is { } u && !u.IsEffectivelyDead;
        handlers["NAMED_DYING"] = c => UnitFrom(c[0]) is { } u && u.IsEffectivelyDead;
        handlers["NAMED_TOTALLY_DEAD"] = c => UnitFrom(c[0]) == null && engine.DidUnitExist(c[0].String);
        handlers["TEAM_HAS_UNITS"] = EvaluateHasUnits;
        handlers["TEAM_INSIDE_AREA_PARTIALLY"] = c => TeamFrom(c[0]) is { } t && AreaFrom(c[1]) is { } a
            && (t.SomeInsideSomeOutside(a, c[2].Int) || t.AllInside(a, c[2].Int));
        handlers["NAMED_INSIDE_AREA"] = c => UnitFrom(c[0]) is { } u && AreaFrom(c[1]) is { } a && a.Contains(u.Position);
        handlers["NAMED_OUTSIDE_AREA"] = c => !handlers["NAMED_INSIDE_AREA"](c);
        handlers["PLAYER_HAS_COMPARISON_UNIT_TYPE_IN_TRIGGER_AREA"] = c => c.Parameters.Count >= 5 && PlayerHasUnitTypeInArea(c);
        handlers["PLAYER_HAS_COMPARISON_UNIT_KIND_IN_TRIGGER_AREA"] = c => c.Parameters.Count >= 5 && PlayerHasUnitKindInArea(c);
        handlers["TEAM_STATE_IS"] = c => TeamFrom(c[0]) is { } t && t.State == c[1].String;
        handlers["TEAM_STATE_IS_NOT"] = c => TeamFrom(c[0]) is { } t && t.State != c[1].String;
        handlers["TEAM_INSIDE_AREA_ENTIRELY"] = c => TeamFrom(c[0]) is { } t && AreaFrom(c[1]) is { } a && t.AllInside(a, c[2].Int);
        handlers["TEAM_OUTSIDE_AREA_ENTIRELY"] = c => !(handlers["TEAM_INSIDE_AREA_ENTIRELY"](c) || handlers["TEAM_INSIDE_AREA_PARTIALLY"](c));
        handlers["NAMED_ATTACKED_BY_OBJECTTYPE"] = c => UnitFrom(c[0]) is { } u && AttackedByType(u, TypesFrom(c[1]));
        handlers["TEAM_ATTACKED_BY_OBJECTTYPE"] = c => TeamFrom(c[0]) is { } t && t.AllMembers.Any(o => AttackedByType(o, TypesFrom(c[1])));
        handlers["NAMED_ATTACKED_BY_PLAYER"] = c => UnitFrom(c[0]) is { } u && AttackedByPlayer(u, c[1]);
        handlers["TEAM_ATTACKED_BY_PLAYER"] = c => TeamFrom(c[0]) is { } t && t.AllMembers.Any(o =>
            o.Body?.LastDamage is { } d && game.FindObject(d.SourceId) is { } attacker && attacker.Owner == PlayerFrom(c[1]));
        handlers["BUILT_BY_PLAYER"] = c => PlayerFrom(c[1]) is { } p && game.Templates.Find(c[0].String) != null
            && TemplatesFrom(TypesFrom(c[0])) is { Count: > 0 } ts && CountByTemplates(p, ts, false) != 0;
        handlers["NAMED_CREATED"] = c => UnitFrom(c[0]) != null;
        handlers["TEAM_CREATED"] = c => TeamFrom(c[0]) is { IsCreated: true };
        handlers["UNIT_HEALTH"] = c =>
        {
            if (UnitFrom(c[0]) is not { Body: { } body })
                return false;
            float initial = body.InitialHealth;
            int percent = (int)((body.Health * 100 + initial / 2) / initial);
            return Cmp(percent, c[1], c[2].Int);
        };
        // (The original compares the parameter against the money: "credits < money" reads "more money than".)
        handlers["PLAYER_HAS_CREDITS"] = c => PlayerFrom(c[2]) is { } p && Cmp(c[0].Int, c[1], p.Money.Amount);
        handlers["BUILDING_ENTERED_BY_PLAYER"] = c => UnitFrom(c[1]) is { Contain: { } contain } && contain.PlayerWhoEntered != 0
            && PlayerFrom(c[0]) is { } p && (contain.PlayerWhoEntered & p.PlayerMask) != 0;
        handlers["NAMED_BUILDING_IS_EMPTY"] = c => UnitFrom(c[0]) is { Contain: { } contain } && contain.Count == 0;
        handlers["ENEMY_SIGHTED"] = c => c.Parameters.Count >= 3 && EnemySighted(c);
        handlers["TYPE_SIGHTED"] = TypeSighted;
        handlers["NAMED_DISCOVERED"] = c => UnitFrom(c[0]) is { } u && PlayerFrom(c[1]) is { } p && IsDiscovered(u, p);
        handlers["TEAM_DISCOVERED"] = c => TeamFrom(c[0]) is { } t && PlayerFrom(c[1]) is { } p && t.AllMembers.Any(o => IsDiscovered(o, p));
        handlers["MISSION_ATTEMPTS"] = c => false;
        handlers["NAMED_OWNED_BY_PLAYER"] = c => PlayerFrom(c[1]) is { } p && UnitFrom(c[0]) is { } u && u.Owner == p;
        handlers["TEAM_OWNED_BY_PLAYER"] = c => PlayerFrom(c[1]) is { } p && TeamFrom(c[0]) is { } t && t.ControllingPlayer == p;
        handlers["PLAYER_HAS_N_OR_FEWER_BUILDINGS"] = c => PlayerFrom(c[0]) is { } p && c[1].Int >= p.Teams.Sum(t => t.CountBuildings());
        handlers["PLAYER_HAS_N_OR_FEWER_FACTION_BUILDINGS"] = c => PlayerFrom(c[0]) is { } p
            && c[1].Int >= MembersOf(p).Count(o => o.IsKindOf(Kind.Structure) && o.IsKindOf("MP_COUNT_FOR_VICTORY"));
        handlers["PLAYER_HAS_POWER"] = c => PlayerFrom(c[0])?.Energy.HasSufficientPower ?? false;
        handlers["PLAYER_HAS_NO_POWER"] = c => !(PlayerFrom(c[0])?.Energy.HasSufficientPower ?? false);
        handlers["NAMED_REACHED_WAYPOINTS_END"] = c => UnitFrom(c[0])?.AI?.CompletedWaypoint is { } w && w.PathLabels.Contains(c[1].String);
        handlers["TEAM_REACHED_WAYPOINTS_END"] = c => TeamFrom(c[0]) is { } t && t.AllMembers.Any(o => o.AI?.CompletedWaypoint is { } w && w.PathLabels.Contains(c[1].String));
        handlers["NAMED_SELECTED"] = c => game.IsNamedObjectSelected?.Invoke(c[0].String) ?? false;
        handlers["HAS_FINISHED_VIDEO"] = c => engine.IsVideoComplete(c[0].String, true);
        handlers["HAS_FINISHED_SPEECH"] = c => engine.IsSpeechComplete(c[0].String, true);
        handlers["HAS_FINISHED_AUDIO"] = c => engine.IsAudioComplete(c[0].String, true);
        handlers["PLAYER_TRIGGERED_SPECIAL_POWER"] = c => SpecialPowerFromUnit(c, null, engine.IsSpecialPowerTriggered);
        handlers["PLAYER_TRIGGERED_SPECIAL_POWER_FROM_NAMED"] = c => SpecialPowerFromUnit(c, c[2], engine.IsSpecialPowerTriggered);
        handlers["PLAYER_MIDWAY_SPECIAL_POWER"] = c => SpecialPowerFromUnit(c, null, engine.IsSpecialPowerMidway);
        handlers["PLAYER_MIDWAY_SPECIAL_POWER_FROM_NAMED"] = c => SpecialPowerFromUnit(c, c[2], engine.IsSpecialPowerMidway);
        handlers["PLAYER_COMPLETED_SPECIAL_POWER"] = c => SpecialPowerFromUnit(c, null, engine.IsSpecialPowerComplete);
        handlers["PLAYER_COMPLETED_SPECIAL_POWER_FROM_NAMED"] = c => SpecialPowerFromUnit(c, c[2], engine.IsSpecialPowerComplete);
        handlers["PLAYER_BUILT_UPGRADE"] = c => SpecialPowerFromUnit(c, null, engine.IsUpgradeComplete);
        handlers["PLAYER_BUILT_UPGRADE_FROM_NAMED"] = c => SpecialPowerFromUnit(c, c[2], engine.IsUpgradeComplete);
        handlers["PLAYER_ACQUIRED_SCIENCE"] = c => PlayerFrom(c[0]) is { } p && game.Catalog.Science(c[1].String) != null && engine.IsScienceAcquired(p.Index, c[1].String, true);
        handlers["PLAYER_CAN_PURCHASE_SCIENCE"] = c => PlayerFrom(c[0]) is { } p && game.Catalog.Science(c[1].String) != null && p.IsCapableOfPurchasingScience(c[1].String);
        handlers["PLAYER_HAS_SCIENCEPURCHASEPOINTS"] = c => PlayerFrom(c[0]) is { } p && p.SciencePurchasePoints >= c[1].Int;
        handlers["NAMED_HAS_FREE_CONTAINER_SLOTS"] = c => UnitFrom(c[0]) is { Contain: { } contain } && contain.Count < contain.ContainMax;
        handlers["NAMED_ENTERED_AREA"] = c => UnitFrom(c[0]) is { } u && !u.IsKindOf("INERT") && AreaFrom(c[1]) is { } a && u.DidEnter(a);
        handlers["NAMED_EXITED_AREA"] = c => UnitFrom(c[0]) is { } u && AreaFrom(c[1]) is { } a && u.DidExit(a);
        handlers["TEAM_ENTERED_AREA_ENTIRELY"] = c => TeamFrom(c[0]) is { } t && AreaFrom(c[1]) is { } a && t.DidAllEnter(a, c[2].Int);
        handlers["TEAM_ENTERED_AREA_PARTIALLY"] = c => TeamFrom(c[0]) is { } t && AreaFrom(c[1]) is { } a && t.DidPartialEnter(a, c[2].Int);
        handlers["TEAM_EXITED_AREA_ENTIRELY"] = c => TeamFrom(c[0]) is { } t && AreaFrom(c[1]) is { } a && t.DidAllExit(a, c[2].Int);
        handlers["TEAM_EXITED_AREA_PARTIALLY"] = c => TeamFrom(c[0]) is { } t && AreaFrom(c[1]) is { } a && t.DidPartialExit(a, c[2].Int);
        handlers["MULTIPLAYER_ALLIED_VICTORY"] = c => game.Victory.IsLocalAlliedVictory;
        handlers["MULTIPLAYER_ALLIED_DEFEAT"] = c => game.Victory.IsLocalAlliedDefeat;
        handlers["MULTIPLAYER_PLAYER_DEFEAT"] = c => game.Victory.IsLocalDefeat && !game.Victory.IsLocalAlliedDefeat;
        handlers["CAMERA_MOVEMENT_FINISHED"] = c => game.IsCameraMovementFinished?.Invoke() ?? true;
        handlers["PLAYER_HAS_OBJECT_COMPARISON"] = c => PlayerFrom(c[0]) is { } p
            && Cmp(TemplatesFrom(TypesFrom(c[3])) is { Count: > 0 } ts ? CountByTemplates(p, ts, false) : 0, c[1], c[2].Int);
        handlers["PLAYER_DESTROYED_N_BUILDINGS_PLAYER"] = c => false;
        handlers["UNIT_EMPTIED"] = UnitHasEmptied;
        handlers["PLAYER_POWER_COMPARE_PERCENT"] = c => PlayerFrom(c[0]) is { } p
            && ScriptEngine.Compare(p.Energy.SupplyRatio, (Comparison)c[1].Int, c[2].Int / 100f);
        handlers["PLAYER_EXCESS_POWER_COMPARE_VALUE"] = c => PlayerFrom(c[0]) is { } p
            && Cmp(p.Energy.Production - p.Energy.Consumption, c[1], c[2].Int);
        handlers["SKIRMISH_SPECIAL_POWER_READY"] = SkirmishSpecialPowerIsReady;
        handlers["UNIT_HAS_OBJECT_STATUS"] = c => UnitFrom(c[0]) is { } u && HasAnyStatus(u, c[1]);
        handlers["TEAM_ALL_HAS_OBJECT_STATUS"] = c => TeamHasObjectStatus(c, true);
        handlers["TEAM_SOME_HAVE_OBJECT_STATUS"] = c => TeamHasObjectStatus(c, false);
        handlers["SKIRMISH_VALUE_IN_AREA"] = c => PlayerFrom(c[0]) is { } p && AreaFrom(c[3]) is { } a
            && Cmp(MembersOf(p).Where(o => !o.IsKindOf("INERT") && o.IsInside(a) && !o.IsEffectivelyDead).Sum(o => o.Template.BuildCost), c[1], c[2].Int);
        handlers["SKIRMISH_PLAYER_FACTION"] = c => PlayerFrom(c[0]) is { } p && p.Side == c[1].String;
        handlers["SKIRMISH_SUPPLIES_VALUE_WITHIN_DISTANCE"] = SuppliesWithinDistance;
        handlers["SKIRMISH_TECH_BUILDING_WITHIN_DISTANCE"] = TechBuildingWithinDistance;
        handlers["SKIRMISH_COMMAND_BUTTON_READY_ALL"] = c => EvaluateSkirmishCommandButtonIsReady(c[0], c[1], c[2], true);
        handlers["SKIRMISH_COMMAND_BUTTON_READY_PARTIAL"] = c => EvaluateSkirmishCommandButtonIsReady(c[0], c[1], c[2], false);
        handlers["SKIRMISH_UNOWNED_FACTION_UNIT_EXISTS"] = c => Cmp(MembersOf(game.Players.Neutral).Count(o => o.IsDisabledBy("DISABLED_UNMANNED")), c[1], c[2].Int);
        handlers["SKIRMISH_PLAYER_HAS_PREREQUISITE_TO_BUILD"] = c => PlayerFrom(c[0]) is { } p
            && TemplatesFrom(TypesFrom(c[1])).Any(t => p.CanBuild(t));
        handlers["SKIRMISH_PLAYER_HAS_COMPARISON_GARRISONED"] = c => PlayerFrom(c[0]) is { } p
            && Cmp(MembersOf(p).Count(o => o.Contain is { IsGarrisonable: true, Count: > 0 }), c[1], c[2].Int);
        handlers["SKIRMISH_PLAYER_HAS_COMPARISON_CAPTURED_UNITS"] = c => PlayerFrom(c[0]) is { } p
            && Cmp(MembersOf(p).Count(o => o.IsCaptured), c[1], c[2].Int);
        handlers["SKIRMISH_NAMED_AREA_EXIST"] = c => AreaFrom(c[1]) != null;
        handlers["SKIRMISH_PLAYER_HAS_UNITS_IN_AREA"] = PlayerHasUnitsInArea;
        handlers["SKIRMISH_PLAYER_HAS_BEEN_ATTACKED_BY_PLAYER"] = c => PlayerFrom(c[0]) is { } p && PlayerFrom(c[1]) is { } src
            && src.Index < p.AttackedBy.Length && p.AttackedBy[src.Index];
        handlers["SKIRMISH_PLAYER_IS_OUTSIDE_AREA"] = c => PlayerFrom(c[0]) != null && AreaFrom(c[1]) != null && !PlayerHasUnitsInArea(c);
        handlers["SKIRMISH_PLAYER_HAS_DISCOVERED_PLAYER"] = c => PlayerFrom(c[0]) is { } p && PlayerFrom(c[1]) is { } by
            && MembersOf(p).Any(o => game.Shroud.StatusOf(o, by) is ObjectShroud.Clear or ObjectShroud.PartialClear);
        handlers["MUSIC_TRACK_HAS_COMPLETED"] = c => MusicTrackCompleted(c[0].String, c[1].Int);
        handlers["SUPPLY_SOURCE_SAFE"] = c => PlayerFrom(c[0]) is { AI: { } ai } && ai.IsSupplySourceSafe(c[1].Int);
        handlers["SUPPLY_SOURCE_ATTACKED"] = c => PlayerFrom(c[0]) is { AI: { } ai } && ai.IsSupplySourceAttacked();
        handlers["START_POSITION_IS"] = c => PlayerFrom(c[0]) is { } p && c[1].Int - 1 == p.MpStartIndex;
        handlers["PLAYER_LOST_OBJECT_TYPE"] = PlayerLostObjectType;
    }

    bool EvaluateHasUnits(ScriptCondition c)
    {
        string name = c[0].String;
        if (name == ScriptEngine.ThisTeam)
            return engine.GetTeamNamed(name) is { } t && t.HasAnyUnits();
        if (engine.GetTeamNamed(ScriptEngine.ThisTeam) is { } thisTeam && thisTeam.Name == name)
            return thisTeam.HasAnyUnits();
        return game.Teams.FindTeamPrototype(name) is { } proto && proto.Instances.Any(t => t.HasAnyUnits());
    }

    bool PlayerHasUnitTypeInArea(ScriptCondition c)
    {
        if (AreaFrom(c[4]) is not { } area || PlayerFrom(c[0]) is not { } p)
            return false;
        var types = TypesFrom(c[3]);
        int count = MembersOf(p).Count(o => types.Contains(o.Template.Name) && o.IsInside(area)
            && (!(o.IsEffectivelyDead || o.IsKindOf("INERT")) || o.IsKindOf("CRATE")));
        return Cmp(count, c[1], c[2].Int);
    }

    bool PlayerHasUnitKindInArea(ScriptCondition c)
    {
        if (AreaFrom(c[4]) is not { } area || PlayerFrom(c[0]) is not { } p)
            return false;
        int kind = c[3].Int;
        int count = MembersOf(p).Count(o => o.KindOf.Has(kind) && o.IsInside(area) && !(o.IsEffectivelyDead || o.IsKindOf("INERT")));
        return Cmp(count, c[1], c[2].Int);
    }

    bool PlayerHasUnitsInArea(ScriptCondition c)
    {
        if (AreaFrom(c[1]) is not { } area || PlayerFrom(c[0]) is not { } p)
            return false;
        return MembersOf(p).Any(o => o.IsInside(area) && !(o.IsEffectivelyDead || o.IsKindOf("INERT") || o.IsKindOf(Kind.Projectile)));
    }

    bool AttackedByType(GameObject obj, HashSet<string> types)
    {
        if (obj.Body?.LastDamage is not { } d)
            return false;
        if (d.SourceTemplate is { } template)
            return types.Contains(template);
        return game.FindObject(d.SourceId) is { } attacker && types.Contains(attacker.Template.Name);
    }

    bool AttackedByPlayer(GameObject obj, ScriptParam playerParam)
    {
        if (obj.Body?.LastDamage is not { } d)
            return false;
        var attacker = game.FindObject(d.SourceId);
        var sourcePlayer = d.SourcePlayerIndex >= 0 ? game.Players.All.ElementAtOrDefault(d.SourcePlayerIndex) : null;
        if (sourcePlayer == null && attacker == null)
            return false;
        var victimPlayer = PlayerFrom(playerParam);
        if (sourcePlayer == victimPlayer)
            return true;
        return attacker != null && attacker.Owner == victimPlayer;
    }

    static bool IsHiddenByStealth(GameObject o) =>
        o.HasStatus(Status.Stealthed) && !o.HasStatus(Status.Detected) && !o.HasStatus("DISGUISED");

    bool IsDiscovered(GameObject o, Player p)
    {
        if (o.IsDisabledBy("DISABLED_HELD") || IsHiddenByStealth(o))
            return false;
        return game.Shroud.StatusOf(o, p) is ObjectShroud.Clear or ObjectShroud.PartialClear;
    }

    IEnumerable<GameObject> InVision(GameObject obj) =>
        game.Partition.Query(obj.Position, obj.VisionRange).Where(o => o != obj && !o.IsEffectivelyDead && !IsHiddenByStealth(o)
            && o.IsOffMap == obj.IsOffMap && MathUtil.Distance2D(o.Position, obj.Position) <= obj.VisionRange);

    bool EnemySighted(ScriptCondition c)
    {
        if (UnitFrom(c[0]) is not { } obj || PlayerFrom(c[2]) is not { } p)
            return false;
        // Relation: 0 friend, 1 neutral, 2 enemy (the original's REL_ values).
        var wanted = c[1].Int switch { 0 => Relationship.Allies, 1 => Relationship.Neutral, _ => Relationship.Enemies };
        return InVision(obj).Any(o => obj.RelationshipTo(o) == wanted && o.Owner == p);
    }

    bool TypeSighted(ScriptCondition c)
    {
        if (UnitFrom(c[0]) is not { } obj || PlayerFrom(c[2]) is not { } p)
            return false;
        var types = TypesFrom(c[1]);
        return InVision(obj).Any(o => o.Owner == p && types.Contains(o.Template.Name));
    }

    bool SpecialPowerFromUnit(ScriptCondition c, ScriptParam? unitParam, Func<int, string, bool, uint, bool> check)
    {
        var p = PlayerFrom(c[0]);
        uint source = 0;
        if (unitParam != null)
        {
            if (UnitFrom(unitParam) is not { } unit)
                return false;
            source = unit.Id;
        }
        return p != null && check(p.Index, c[1].String, true, source);
    }

    bool UnitHasEmptied(ScriptCondition c)
    {
        if (UnitFrom(c[0]) is not { } obj)
            return false;
        int count = obj.Contain?.Count ?? 0;
        int frame = game.Frame;
        if (!transportStatuses.TryGetValue(obj.Id, out var stats))
        {
            transportStatuses[obj.Id] = new TransportStatus { FrameNumber = frame, UnitCount = count };
            return false;
        }
        if (stats.FrameNumber == frame - 1 && stats.UnitCount > 0 && count == 0)
            return true;
        stats.FrameNumber = frame;
        stats.UnitCount = count;
        return false;
    }

    /// <summary>The original stores the next frame worth checking in the parameter itself.</summary>
    bool SkirmishSpecialPowerIsReady(ScriptCondition c)
    {
        var powerParam = c[1];
        if (powerParam.Int == -1)
            return false;
        if (powerParam.Int > 0 && powerParam.Int > game.Frame)
            return false;
        int nextFrame = game.Frame + 10 * LogicTime.FramesPerSecond;
        if (game.Catalog.SpecialPower(powerParam.String) is not { } power)
        {
            powerParam.Int = -1;
            return false;
        }
        if (PlayerFrom(c[0]) is not { } p)
            return false;
        foreach (var obj in MembersOf(p))
        {
            if (obj.IsUnderConstruction || obj.IsDisabled)
                continue;
            if (Modules.SpecialPowers.SpecialPowerRules.ModuleFor(obj, power) is not { } module)
                continue;
            if (!Modules.SpecialPowers.SpecialPowerRules.CanUseSpecialPower(obj, power))
                continue;
            if (module.IsReady)
                return true;
            if (module.ReadyFrame < nextFrame)
                nextFrame = module.ReadyFrame;
        }
        powerParam.Int = nextFrame;
        return false;
    }

    /// <summary>OBJECT_STATUS parameters hold the status names; any of them counts.</summary>
    static bool HasAnyStatus(GameObject o, ScriptParam p) =>
        RuleWords(p.String).Any(s => o.HasStatus(s));

    static string[] RuleWords(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    bool TeamHasObjectStatus(ScriptCondition c, bool entireTeam)
    {
        if (TeamFrom(c[0]) is not { } t)
            return false;
        foreach (var o in t.AllMembers)
        {
            bool has = HasAnyStatus(o, c[1]);
            if (entireTeam && !has)
                return false;
            if (!entireTeam && has)
                return true;
        }
        return entireTeam;
    }

    bool SuppliesWithinDistance(ScriptCondition c)
    {
        if (PlayerFrom(c[0]) is not { } p || AreaFrom(c[2]) is not { } area)
            return false;
        var center = new System.Numerics.Vector3(area.Center, 0);
        float distance = area.Radius + c[1].Real;
        float maxValue = 0;
        foreach (var o in game.Partition.Query(center, distance))
        {
            if (!o.IsKindOf(Kind.Structure) || MathUtil.Distance2D(o.Position, center) > distance || o.IsOffMap)
                continue;
            if (!(o.Owner == p || o.Owner.IsNeutral || p.RelationshipTo(o.Owner) == Relationship.Neutral))
                continue;
            if (o.FindModule<Modules.Supply.SupplyWarehouseDockUpdate>() is not { } warehouse)
                continue;
            maxValue = Math.Max(maxValue, p.SupplyBoxValue * warehouse.Boxes);
        }
        return maxValue > c[3].Real;
    }

    bool TechBuildingWithinDistance(ScriptCondition c)
    {
        if (PlayerFrom(c[0]) is not { } p || AreaFrom(c[2]) is not { } area)
            return false;
        var center = new System.Numerics.Vector3(area.Center, 0);
        float radius = area.Radius + c[1].Real;
        return game.Partition.Query(center, radius).Any(o => o.IsKindOf("TECH_BUILDING") && !o.IsOffMap
            && MathUtil.Distance2D(o.Position, center) <= radius && o.Owner != p && !p.IsAllyOf(o.Owner));
    }

    /// <summary>Is a team's command button (a special power or upgrade) ready on one / all members?</summary>
    public bool EvaluateSkirmishCommandButtonIsReady(ScriptParam? unused, ScriptParam teamParam, ScriptParam buttonParam, bool allReady)
    {
        if (TeamFrom(teamParam) is not { } team || game.Catalog.CommandButton(buttonParam.String) is not { } button)
            return false;
        var power = game.Catalog.SpecialPower(button.GetString("SpecialPower"));
        string upgrade = button.GetString("Upgrade");
        foreach (var obj in team.AllMembers)
        {
            if (power != null)
            {
                if (Modules.SpecialPowers.SpecialPowerRules.ModuleFor(obj, power) == null)
                    continue;
            }
            else if (upgrade.Length == 0)
                continue;
            bool ready = (power != null && Modules.SpecialPowers.SpecialPowerRules.ModuleFor(obj, power) is { PercentReady: 1f })
                || (upgrade.Length > 0 && obj.IsAffectedByUpgrade(upgrade) && !obj.HasUpgrade(upgrade));
            if (ready && !allReady)
                return true;
            if (!ready && allReady)
                return false;
        }
        return allReady;
    }

    /// <summary>Is the team in transports (all of it / any of it)?</summary>
    public bool EvaluateTeamIsContained(ScriptParam teamParam, bool allContained)
    {
        if (TeamFrom(teamParam) is not { } team)
            return false;
        bool any = false;
        foreach (var obj in team.AllMembers)
        {
            bool contained = obj.IsContained;
            if (contained && !allContained)
                return true;
            if (!contained && allContained)
                return false;
            any = true;
        }
        return any && allContained;
    }

    bool PlayerLostObjectType(ScriptCondition c)
    {
        if (PlayerFrom(c[0]) is not { } p)
            return false;
        var templates = TemplatesFrom(TypesFrom(c[1]));
        if (templates.Count == 0)
            return false;
        int sum = CountByTemplates(p, templates, true);
        var key = (p.Index, c[1].String);
        int current = objectCounts.GetValueOrDefault(key);
        if (sum != current)
            objectCounts[key] = sum;
        return sum < current;
    }
}

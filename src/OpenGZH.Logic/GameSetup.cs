using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Maps;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Scripting;
using OpenGZH.Logic.Terrain;
using ScriptList = OpenGZH.Logic.Scripting.ScriptList;

namespace OpenGZH.Logic;

/// <summary>
/// One player slot in a skirmish or multiplayer game. <paramref name="StartPosition"/> is 1-based
/// (waypoint Player_N_Start); <paramref name="Difficulty"/> is the computer player's.
/// </summary>
public sealed record PlayerSlot(string Name, string Faction, bool IsHuman, int StartPosition, uint Color, int Team = -1,
    Difficulty Difficulty = Difficulty.Normal);

/// <summary>
/// Builds a <see cref="GameLogic"/> from a map, in the original's order (GameLogic::startNewGame):
/// the sides list (with the skirmish preparation and one side per slot), the players and their
/// script copies, the teams, the scripts, every placed object, each slot's starting base, and
/// finally the computer players' first look at the map.
/// </summary>
public static class GameSetup
{
    /// <summary>A side being set up (the original's SidesInfo): properties, scripts and build list.</summary>
    sealed class SetupSide
    {
        public required Dictionary<string, object> Props;
        public ScriptList? Scripts;
        public List<BuildListEntry> BuildList = new();

        public string Name => Props.TryGetValue("playerName", out var v) ? v?.ToString() ?? "" : "";
        public string Faction => Props.TryGetValue("playerFaction", out var v) ? v?.ToString() ?? "" : "";

        public SetupSide Copy() => new()
        {
            Props = new Dictionary<string, object>(Props, StringComparer.OrdinalIgnoreCase),
            Scripts = Scripts,
            BuildList = BuildList,
        };
    }

    static Dictionary<string, object> CopyDict(Dictionary<string, object> d) => new(d, StringComparer.OrdinalIgnoreCase);

    static string S(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    /// <summary>
    /// Starts a game on a map. Without slots it is a mission (the map's own players); with slots, a
    /// skirmish or multiplayer game. <paramref name="readFile"/> gives the game's data files
    /// (Data/Scripts/SkirmishScripts.scb and MultiplayerScripts.scb); without it there are no
    /// standard skirmish scripts.
    /// </summary>
    public static GameLogic FromMap(MapFile map, RuleSet rules, ulong seed, IReadOnlyList<PlayerSlot>? slots = null,
        int startingCash = 10000, Difficulty difficulty = Difficulty.Normal, Formats.W3d.W3dLibrary? models = null,
        Func<string, byte[]?>? readFile = null, Func<Core.GameRandom, IReadOnlyList<PlayerSlot>>? resolveSlots = null)
    {
        var terrain = new TerrainLogic(map);
        var game = new GameLogic(rules, terrain, seed)
        {
            Waypoints = new WaypointList(map, terrain),
            Bones = new Art.ModelBones(models),
        };
        var scriptEngine = new ScriptEngine(game) { Difficulty = difficulty };
        game.Scripts = scriptEngine;
        // Random factions, colours and start positions are settled with the game's own random numbers.
        if (slots == null && resolveSlots != null)
            slots = resolveSlots(game.Random);
        bool multiplayer = slots is { Count: > 0 };
        game.IsSinglePlayer = !multiplayer;

        var sides = map.Sides.Sides.Select(s => new SetupSide
        {
            Props = CopyDict(s.Properties),
            Scripts = s.Scripts.Scripts.Count > 0 || s.Scripts.Groups.Count > 0 ? ScriptList.From(s.Scripts) : null,
            BuildList = s.BuildList.ToList(),
        }).ToList();
        var teams = map.Sides.Teams.Select(CopyDict).ToList();
        var skirmishSides = new List<SetupSide>();
        var skirmishTeams = new List<Dictionary<string, object>>();

        if (multiplayer)
        {
            PrepareForMpOrSkirmish(sides, teams, skirmishSides, skirmishTeams, readFile);
            AddSlotSides(sides, teams, slots!);
        }
        ValidateSides(sides, teams);

        // The players (the original's PlayerList::newGame and Player::initFromDict).
        var sidePlayers = new List<Player>();
        Player? local = null;
        foreach (var side in sides)
        {
            if (side.Name.Length == 0)
            {
                sidePlayers.Add(game.Players.Neutral);
                continue;
            }
            var faction = rules.Get("PlayerTemplates", side.Faction);
            var player = game.Players.Add(side.Name, side.Faction, faction);
            InitFromSide(game, player, side, skirmishSides, skirmishTeams, teams, difficulty);
            if (side.Props.TryGetValue("multiplayerIsLocal", out var isLocal) && isLocal is true)
                local = player;
            else if (local == null && player.IsHuman)
                local = player;
            player.BuildList.Clear();
            player.BuildList.AddRange(side.BuildList.Select(AI.BuildListInfo.From));
            sidePlayers.Add(player);
        }
        if (local == null)
        {
            local = sidePlayers.FirstOrDefault(p => !p.IsNeutral);
            local?.SetPlayerType(human: true, skirmish: false);
        }
        game.Players.Local = local;

        game.Teams.InitFromSides(teams);
        for (int i = 0; i < sides.Count; i++)
        {
            var p = sidePlayers[i];
            foreach (string name in S(sides[i].Props, "playerEnemies").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (game.Players.FindByNameKey(name) is { } other)
                    p.SetRelationship(other, Relationship.Enemies);
            foreach (string name in S(sides[i].Props, "playerAllies").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (game.Players.FindByNameKey(name) is { } other)
                    p.SetRelationship(other, Relationship.Allies);
            p.SetRelationship(p, Relationship.Allies);
            if (!p.IsNeutral)
                p.SetRelationship(game.Players.Neutral, Relationship.Neutral);
            p.SetDefaultTeam();
        }

        // The scripts (side i runs as player i).
        for (int i = 0; i < sides.Count; i++)
            scriptEngine.SetSideScripts(sidePlayers[i].Index, sides[i].Scripts);
        if (multiplayer && CountTeams(slots!) > 1 && readFile?.Invoke("Data/Scripts/MultiplayerScripts.scb") is { } mpData)
        {
            var mp = ScriptFile.Read(mpData);
            if (mp.ScriptLists.Count > 0)
            {
                var neutralScripts = scriptEngine.Sides.Count > 0 && scriptEngine.Sides[0] is { } existing ? existing : new ScriptList();
                foreach (var script in ScriptList.From(mp.ScriptLists[0]).Scripts)
                    neutralScripts.Scripts.Add(script.Duplicate());
                scriptEngine.SetSideScripts(0, neutralScripts);
            }
        }
        scriptEngine.NewMap();

        Terrain.BridgeSetup.LoadMapBridges(game, map);
        PlaceMapObjects(game, map);

        if (multiplayer)
        {
            game.Victory.IsMultiplayer = true;
            for (int i = 0; i < slots!.Count; i++)
                if (game.Players.FindByNameKey($"player{i}") is { } player)
                {
                    player.Money.Deposit(startingCash);
                    PlaceNetworkBuildingsForPlayer(game, player, slots[i]);
                }
        }

        game.Victory.CachePlayers();
        game.Shroud.Update();
        game.Players.NewMap();
        return game;
    }

    /// <summary>
    /// Starts a skirmish or network game set up in the menus: the slots' random choices are settled
    /// first (GameInfo.ToPlayerSlots), then the game is built as for any slot list.
    /// </summary>
    public static GameLogic FromGameInfo(MapFile map, RuleSet rules, Multiplayer.GameInfo info, Multiplayer.MapCache? mapCache = null,
        Formats.W3d.W3dLibrary? models = null, Func<string, byte[]?>? readFile = null)
    {
        var meta = mapCache?.Find(info.Map);
        var locked = rules.Section("ChallengeGenerals").Entries
            .Where(e => !RuleValue.Bool(e.Value.GetString("StartsEnabled"), true))
            .Select(e => e.Value.GetString("PlayerTemplate")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var game = FromMap(map, rules, info.Seed, slots: null, info.StartingCash, Difficulty.Normal, models, readFile,
            random => info.ToPlayerSlots(random, rules, meta, locked.Contains));
        game.SuperweaponRestriction = info.SuperweaponRestriction ? 1 : 0;
        return game;
    }

    static int CountTeams(IReadOnlyList<PlayerSlot> slots)
    {
        int numTeams = 0, lastTeam = -1;
        foreach (var slot in slots)
            if (slot.Team == -1 || slot.Team != lastTeam)
            {
                numTeams++;
                lastTeam = slot.Team;
            }
        return numTeams;
    }

    /// <summary>
    /// The original's SidesList::prepareForMP_or_Skirmish: the map's sides become the skirmish
    /// templates (only civilians stay as real sides), and if they have no scripts of their own the
    /// standard SkirmishScripts.scb provides them and their teams.
    /// </summary>
    static void PrepareForMpOrSkirmish(List<SetupSide> sides, List<Dictionary<string, object>> teams,
        List<SetupSide> skirmishSides, List<Dictionary<string, object>> skirmishTeams, Func<string, byte[]?>? readFile)
    {
        skirmishTeams.AddRange(teams);
        teams.Clear();
        for (int i = 0; i < sides.Count; i++)
        {
            skirmishSides.Add(sides[i]);
            if (sides[i].Faction == "FactionCivilian")
                continue;
            if (sides.Count == 1)
                break;
            sides.RemoveAt(i);
            i--;
        }
        bool gotScripts = skirmishSides.Any(s => s.Faction != "FactionCivilian" && s.Scripts is { IsEmpty: false });
        if (gotScripts || readFile?.Invoke("Data/Scripts/SkirmishScripts.scb") is not { } data)
            return;
        skirmishTeams.Clear();
        var file = ScriptFile.Read(data);
        foreach (var team in file.Teams)
            if (skirmishSides.Any(s => s.Name == S(team, "teamOwner")))
                skirmishTeams.Add(CopyDict(team));
        for (int i = 0; i < file.ScriptLists.Count && i < file.PlayerNames.Count; i++)
            if (skirmishSides.FirstOrDefault(s => s.Name == file.PlayerNames[i]) is { } side)
                side.Scripts = ScriptList.From(file.ScriptLists[i]);
    }

    /// <summary>One side per occupied slot ("player0" ...) and its singleton default team (GameLogic::startNewGame).</summary>
    static void AddSlotSides(List<SetupSide> sides, List<Dictionary<string, object>> teams, IReadOnlyList<PlayerSlot> slots)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            string playerName = $"player{i}";
            var allies = new List<string>();
            var enemies = new List<string>();
            for (int j = 0; j < slots.Count; j++)
            {
                if (i == j)
                    continue;
                bool isEnemy = slot.Team == -1 || slots[j].Team != slot.Team;
                (isEnemy ? enemies : allies).Add($"player{j}");
            }
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["playerName"] = playerName,
                ["playerIsHuman"] = slot.IsHuman,
                ["playerDisplayName"] = slot.Name,
                ["playerFaction"] = slot.Faction,
                ["playerAllies"] = string.Join(' ', allies),
                ["playerEnemies"] = string.Join(' ', enemies),
                ["playerColor"] = (int)slot.Color,
                ["multiplayerStartIndex"] = slot.StartPosition - 1,
                ["multiplayerIsLocal"] = slot.IsHuman && slots.First(s => s.IsHuman) == slot,
                ["playerIsSkirmish"] = true,
                ["skirmishDifficulty"] = (int)slot.Difficulty,
            };
            sides.Add(new SetupSide { Props = d });
            teams.Add(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["teamName"] = "team" + playerName,
                ["teamOwner"] = playerName,
                ["teamIsSingleton"] = true,
            });
        }
    }

    /// <summary>The original's validateSides: a neutral side comes first, and every side has its "team" + name default team.</summary>
    static void ValidateSides(List<SetupSide> sides, List<Dictionary<string, object>> teams)
    {
        int neutral = sides.FindIndex(s => s.Name.Length == 0);
        if (neutral < 0)
            sides.Insert(0, new SetupSide { Props = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["playerName"] = "", ["playerFaction"] = "" } });
        else if (neutral > 0)
        {
            var n = sides[neutral];
            sides.RemoveAt(neutral);
            sides.Insert(0, n);
        }
        foreach (var side in sides)
        {
            string teamName = "team" + side.Name;
            if (!teams.Any(t => S(t, "teamName") == teamName))
                teams.Add(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["teamName"] = teamName,
                    ["teamOwner"] = side.Name,
                    ["teamIsSingleton"] = true,
                });
        }
    }

    static string FactionSide(RuleSet rules, string faction) => rules.Get("PlayerTemplates", faction)?.GetString("Side", "") ?? "";

    /// <summary>
    /// The original's Player::initFromDict: a skirmish computer player takes its side's skirmish
    /// scripts and teams, copied with its start index appended to every name ("SkirmishAmerica" →
    /// script name "SkirmishAmerica2"; team "teamSkirmishAmerica" → "teamSkirmishAmerica2"), and its
    /// side references pointed at itself. The first human gets the civilian skirmish side's scripts
    /// (the music), qualified with index 0.
    /// </summary>
    static void InitFromSide(GameLogic game, Player player, SetupSide side, List<SetupSide> skirmishSides,
        List<Dictionary<string, object>> skirmishTeams, List<Dictionary<string, object>> teams, Difficulty globalDifficulty)
    {
        var d = side.Props;
        var rules = game.Rules;
        string pname = side.Name;
        bool skirmish = false, forceHuman = false;
        if (d.ContainsKey("playerIsSkirmish"))
        {
            skirmish = skirmishSides.Any(s => FactionSide(rules, s.Faction) == player.Side);
            // OpenGZH: a Tiberian Dawn faction has no skirmish scripts; its computer player is TdFactionAI.
            bool tdFaction = OpenGZH.Core.Rules.RuleValue.Bool(player.Faction?.GetString("TdFaction") ?? "");
            if (!skirmish && !tdFaction)
                forceHuman = true;
        }
        bool isHuman = (d.TryGetValue("playerIsHuman", out var h) && h is true) || forceHuman;
        if (S(d, "playerDisplayName") is { Length: > 0 } display)
            player.DisplayName = display;
        if (isHuman)
        {
            player.SetPlayerType(human: true, skirmish: false);
            if (skirmishSides.Count > 0 && skirmishSides.FirstOrDefault(s => FactionSide(rules, s.Faction) == "Civilian") is { } civ)
            {
                string qualifier = player.MpStartIndex.ToString();
                string qualTemplate = civ.Name + player.MpStartIndex;
                if (civ.Scripts != null)
                {
                    side.Scripts = civ.Scripts.DuplicateAndQualify(qualifier, qualTemplate, pname);
                    civ.Scripts = null;
                }
            }
            skirmish = false;
        }
        else
            player.SetPlayerType(human: false, skirmish);
        player.MpStartIndex = d.TryGetValue("multiplayerStartIndex", out var start) && start is int si ? si : 0;

        if (skirmish && skirmishSides.FirstOrDefault(s => FactionSide(rules, s.Faction) == player.Side) is { } template)
        {
            string qualifier = player.MpStartIndex.ToString();
            string qualTemplate = template.Name + player.MpStartIndex;
            var diff = d.TryGetValue("skirmishDifficulty", out var dv) && dv is int di ? (Difficulty)di : globalDifficulty;
            player.Difficulty = diff;
            if (player.AI != null)
                player.AI.AIDifficulty = diff;
            player.IsSkirmishAIPlayer = true;
            player.ScriptName = qualTemplate;
            side.Scripts = template.Scripts?.DuplicateAndQualify(qualifier, qualTemplate, pname);
            teams.RemoveAll(t => S(t, "teamOwner") == pname);
            foreach (var skirmishTeam in skirmishTeams.Where(t => S(t, "teamOwner") == template.Name).ToList())
            {
                var team = CopyDict(skirmishTeam);
                string newName = S(team, "teamName") + player.MpStartIndex;
                if (teams.Any(t => S(t, "teamName") == newName))
                    continue;
                team["teamOwner"] = pname;
                team["teamName"] = newName;
                foreach (string key in new[] { "teamOnCreateScript", "teamOnIdleScript", "teamOnUnitDestroyedScript", "teamOnDestroyedScript",
                    "teamEnemySightedScript", "teamAllClearScript", "teamProductionCondition" })
                    if (S(team, key) is { Length: > 0 } value)
                        team[key] = value + player.MpStartIndex;
                for (int j = 0; j < Players.TeamTemplateInfo.MaxGenericScripts; j++)
                    if (S(team, $"teamGenericScriptHook{j}") is { Length: > 0 } hook)
                        team[$"teamGenericScriptHook{j}"] = hook + player.MpStartIndex;
                teams.Add(team);
            }
        }

        if (d.TryGetValue("playerColor", out var color) && color is int argb)
            player.Color = (uint)argb & 0xFFFFFF;
        if (d.TryGetValue("playerStartMoney", out var money) && money is int m)
            player.Money.Deposit(m);
    }

    static void PlaceMapObjects(GameLogic game, MapFile map)
    {
        foreach (var mapObject in map.Objects)
        {
            if (mapObject.IsWaypoint || mapObject.TemplateName.StartsWith('*') || (mapObject.Flags & 0x36) != 0)
                continue;

            var template = game.Templates.Find(mapObject.TemplateName);
            if (template == null)
                continue;

            // "originalOwner" names the team the object starts on (the original's validateTeam).
            Team? team = null;
            if (mapObject.Get("originalOwner") is { Length: > 0 } teamName)
                team = game.Teams.FindTeam(teamName) ?? game.Teams.FindTeam("team" + teamName);
            team ??= game.Players.Neutral.DefaultTeam;
            var owner = team.ControllingPlayer;

            var position = mapObject.Position;
            position.Z += game.Terrain.GroundHeight(position);
            var obj = game.CreateObject(template, owner, position, mapObject.Angle, team);
            // Things placed on the map count as already built (the original calls onBuildComplete).
            game.TreatAsBuilt(obj);
            team.SetActive();

            if (mapObject.Get("objectName") is { Length: > 0 } name)
                game.SetObjectName(obj, name);
            ApplyMapProperties(obj, mapObject);
            if (obj.IsKindOf("LANDMARK_BRIDGE"))
                Terrain.BridgeSetup.AddLandmarkBridge(game, obj);
        }
    }

    /// <summary>Per-object settings from the map editor (health, flags, ...).</summary>
    internal static void ApplyMapProperties(GameObject obj, MapObject mapObject)
    {
        var properties = mapObject.Properties;
        if (properties.TryGetValue("objectInitialHealth", out var health) && health is int percent && obj.Body != null && percent != 100)
            obj.Body.SetHealth(obj.MaxHealth * percent / 100f);
        if (properties.TryGetValue("objectIndestructible", out var indestructible) && indestructible is true && obj.Body != null)
            obj.Body.Indestructible = true;
        if (properties.TryGetValue("objectUnsellable", out var unsellable) && unsellable is true)
            obj.IsUnsellable = true;
        if (properties.TryGetValue("objectEnabled", out var enabled) && enabled is false)
            obj.SetScriptStatus("SCRIPT_DISABLED", true);
        if (properties.TryGetValue("objectSelectable", out var selectable) && selectable is false)
            obj.SetSelectable(false);
        if (properties.TryGetValue("objectVeterancy", out var veterancy) && veterancy is int level && level > 0)
            obj.SetVeterancy((VeterancyLevel)Math.Clamp(level, 0, 3));
        if (properties.TryGetValue("objectAggressiveness", out var mood) && mood is int attitude && obj.AI != null)
            obj.AI.Attitude = (Attitude)attitude;
        if (properties.TryGetValue("objectRecruitableAI", out var recruitable) && recruitable is false && obj.AI != null)
            obj.AI.IsRecruitable = false;
    }

    /// <summary>
    /// The original's placeNetworkBuildingsForPlayer: the faction's StartingBuilding on Player_N_Start
    /// (facing its PlacementViewAngle), then each StartingUnit on a free spot around the Player_N_Rally
    /// waypoint (or just in front of the building), 0.7 to 1.3 of its size away.
    /// </summary>
    static void PlaceNetworkBuildingsForPlayer(GameLogic game, Player player, PlayerSlot slot)
    {
        if (player.Faction is not { } faction)
            return;
        if (game.Waypoints?.Find($"Player_{slot.StartPosition}_Start") is not { } waypoint)
            return;
        var pos = waypoint.Position with { Z = game.Terrain.GroundHeight(waypoint.Position) };
        player.StartPosition = pos;
        string building = faction.GetString("StartingBuilding");
        if (building.Length == 0 || game.Templates.Find(building) is not { } buildingTemplate)
        {
            // OpenGZH: a Tiberian Dawn faction starts with no building, only its units (an MCV),
            // gathered on the start position.
            PlaceStartingUnits(game, player, faction, pos, 0, 60);
            player.DefaultTeam.SetActive();
            return;
        }
        var conYard = game.CreateObject(buildingTemplate, player, pos, buildingTemplate.PlacementViewAngle, player.DefaultTeam);
        game.TreatAsBuilt(conYard);
        player.DefaultTeam.SetActive();
        pos.Y -= conYard.BoundingSphereRadius / 2;
        pos.Z = game.Terrain.GroundHeight(pos);
        if (game.Waypoints.Find($"Player_{slot.StartPosition}_Rally") is { } rally)
            pos = rally.Position with { Z = game.Terrain.GroundHeight(rally.Position) };
        for (int i = 0; i < 10; i++)
        {
            string unit = faction.GetString($"StartingUnit{i}");
            if (unit.Length == 0 || game.Templates.Find(unit) is not { } unitTemplate)
                continue;
            if (Spatial.PositionFinder.FindPositionAround(game, pos, conYard.BoundingSphereRadius * 0.7f, conYard.BoundingSphereRadius * 1.3f) is not { } at)
                continue;
            var obj = game.CreateObject(unitTemplate, player, at, unitTemplate.PlacementViewAngle, player.DefaultTeam);
            game.TreatAsBuilt(obj);
            player.OnUnitCreated(null, obj);
        }
    }

    static void PlaceStartingUnits(GameLogic game, Player player, OpenGZH.Core.Rules.RuleNode faction, System.Numerics.Vector3 pos, float minRadius, float maxRadius)
    {
        for (int i = 0; i < 10; i++)
        {
            string unit = faction.GetString($"StartingUnit{i}");
            if (unit.Length == 0 || game.Templates.Find(unit) is not { } unitTemplate)
                continue;
            var at = i == 0 ? pos : Spatial.PositionFinder.FindPositionAround(game, pos, minRadius + 30, maxRadius + 30);
            if (at is not { } spot)
                continue;
            var obj = game.CreateObject(unitTemplate, player, spot, unitTemplate.PlacementViewAngle, player.DefaultTeam);
            game.TreatAsBuilt(obj);
            player.OnUnitCreated(null, obj);
        }
    }
}

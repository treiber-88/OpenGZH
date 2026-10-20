using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Maps;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Scripting;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic;

/// <summary>One player slot in a skirmish or multiplayer game.</summary>
public sealed record PlayerSlot(string Name, string Faction, bool IsHuman, int StartPosition, uint Color, int Team = -1);

/// <summary>
/// Builds a <see cref="GameLogic"/> from a map: terrain, the map's players and teams, every placed
/// object, waypoints, scripts, and (for skirmish) each slot's starting base.
/// </summary>
public static class GameSetup
{
    public static GameLogic FromMap(MapFile map, RuleSet rules, ulong seed, IReadOnlyList<PlayerSlot>? slots = null,
        int startingCash = 10000, Difficulty difficulty = Difficulty.Normal, Formats.W3d.W3dLibrary? models = null)
    {
        var terrain = new TerrainLogic(map);
        var game = new GameLogic(rules, terrain, seed)
        {
            Waypoints = new WaypointList(map, terrain),
            Bones = new Art.ModelBones(models),
        };

        game.Players.LoadFromMap(map.Sides, rules);
        PlaceMapObjects(game, map);

        if (slots != null && slots.Count > 0)
            SetUpSkirmish(game, slots, startingCash);

        var scripts = new ScriptEngine(game) { Difficulty = difficulty };
        scripts.Load(map.Sides);
        game.Scripts = scripts;
        return game;
    }

    static void PlaceMapObjects(GameLogic game, MapFile map)
    {
        foreach (var mapObject in map.Objects)
        {
            if (mapObject.IsWaypoint || mapObject.TemplateName.StartsWith('*'))
                continue;

            var template = game.Templates.Find(mapObject.TemplateName);
            if (template == null)
                continue;

            // "originalOwner" names the team the object starts on.
            Team? team = null;
            if (mapObject.Get("originalOwner") is { Length: > 0 } teamName)
                team = game.Players.FindTeam(teamName) ?? game.Players.FindTeam("team" + teamName);
            var owner = team?.Owner ?? game.Players.Neutral;

            var position = mapObject.Position;
            position.Z += game.Terrain.GroundHeight(position);
            var obj = game.CreateObject(template, owner, position, mapObject.Angle, team);

            if (mapObject.Get("objectName") is { Length: > 0 } name)
                game.SetObjectName(obj, name);
            ApplyMapProperties(obj, mapObject);
        }
    }

    /// <summary>Per-object settings from the map editor (health, flags, ...).</summary>
    static void ApplyMapProperties(GameObject obj, MapObject mapObject)
    {
        var properties = mapObject.Properties;
        if (properties.TryGetValue("objectInitialHealth", out var health) && health is int percent && obj.Body != null && percent != 100)
            obj.Body.SetHealth(obj.MaxHealth * percent / 100f);
        if (properties.TryGetValue("objectIndestructible", out var indestructible) && indestructible is true && obj.Body != null)
            obj.Body.Indestructible = true;
        if (properties.TryGetValue("objectUnsellable", out var unsellable) && unsellable is true)
            obj.IsUnsellable = true;
        if (properties.TryGetValue("objectEnabled", out var enabled) && enabled is false)
            obj.SetDisabled("DISABLED_SCRIPT_DISABLED");
        if (properties.TryGetValue("objectSelectable", out var selectable) && selectable is false)
            obj.SetStatus(Core.Status.Unselectable);
        if (properties.TryGetValue("objectVeterancy", out var veterancy) && veterancy is int level && level > 0)
            obj.SetVeterancy((VeterancyLevel)Math.Clamp(level, 0, 3));
    }

    static void SetUpSkirmish(GameLogic game, IReadOnlyList<PlayerSlot> slots, int startingCash)
    {
        var created = new List<(Player Player, PlayerSlot Slot)>();
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var faction = game.Rules.Get("PlayerTemplates", slot.Faction);
            var player = game.Players.Add(slot.Name, slot.Faction, faction);
            player.DisplayName = slot.Name;
            player.IsHuman = slot.IsHuman;
            player.Color = slot.Color;
            player.Money.Deposit(startingCash);
            created.Add((player, slot));
        }

        // Teams: same team number = allies; everyone else is an enemy.
        foreach (var (a, slotA) in created)
            foreach (var (b, slotB) in created)
                if (a != b)
                    a.SetRelationship(b, slotA.Team >= 0 && slotA.Team == slotB.Team ? Relationship.Allies : Relationship.Enemies);

        // Civilian buildings and units on the map stay neutral to everyone.
        foreach (var (player, slot) in created)
        {
            var start = game.Waypoints?.Find($"Player_{slot.StartPosition}_Start")?.Position;
            if (start == null)
                continue;
            player.StartPosition = start;
            PlaceStartingBase(game, player, start.Value);
        }

        game.Players.Local = created.FirstOrDefault(c => c.Slot.IsHuman).Player;
    }

    /// <summary>Each faction starts with its StartingBuilding (command center) and StartingUnit0.. (dozer).</summary>
    static void PlaceStartingBase(GameLogic game, Player player, Vector3 start)
    {
        var faction = player.Faction;
        if (faction == null)
            return;

        // The command center faces the middle of the map.
        var center = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        float facing = Core.MathUtil.AngleTo(start, center);

        string building = faction.GetString("StartingBuilding");
        if (building.Length > 0)
            game.CreateObject(building, player, start with { Z = game.Terrain.GroundHeight(start) }, facing);

        for (int i = 0; i < 10; i++)
        {
            string unit = faction.GetString($"StartingUnit{i}");
            if (unit.Length == 0)
                continue;
            var offset = Core.MathUtil.Forward(facing + 0.4f * i) * (60 + 15 * i);
            var position = game.Pathfinder.NearestPassable(start + offset, Locomotion.Surfaces.Ground) ?? start + offset;
            game.CreateObject(unit, player, position with { Z = game.Terrain.GroundHeight(position) }, facing);
        }
    }
}

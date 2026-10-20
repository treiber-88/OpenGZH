using OpenGZH.Core.Rules;
using OpenGZH.Formats.Maps;

namespace OpenGZH.Logic.Players;

/// <summary>All players in the game, including the always-present neutral player.</summary>
public sealed class PlayerList
{
    readonly GameLogic game;
    readonly List<Player> players = new();
    readonly Dictionary<string, Team> teamsByName = new(StringComparer.OrdinalIgnoreCase);

    public Player Neutral { get; }
    public Player? Local { get; set; }

    public PlayerList(GameLogic game)
    {
        this.game = game;
        Neutral = Add("", "", null);
    }

    public IReadOnlyList<Player> All => players;
    public IEnumerable<Player> Active => players.Where(p => !p.IsNeutral && !p.IsDefeated);

    public Player Add(string name, string factionName, RuleNode? faction)
    {
        var player = new Player(game, players.Count, name, factionName, faction);
        players.Add(player);
        RegisterTeam(player.DefaultTeam);
        if (!player.IsNeutral)
            game.Ranks.CheckRankUp(player); // apply rank 1
        return player;
    }

    public Player? Find(string name) =>
        players.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public Team? FindTeam(string name) => teamsByName.GetValueOrDefault(name);

    public void RegisterTeam(Team team) => teamsByName[team.Name] = team;

    /// <summary>
    /// Creates the map's players and teams from its sides list. Multiplayer slots are separate
    /// (see <see cref="GameSetup"/>); this covers the map's own players (campaign sides, civilians).
    /// </summary>
    public void LoadFromMap(SidesList sides, RuleSet rules)
    {
        foreach (var side in sides.Sides)
        {
            if (side.Name.Length == 0)
                continue; // the neutral player already exists
            var faction = rules.Get("PlayerTemplates", side.Faction);
            var player = Find(side.Name) ?? Add(side.Name, side.Faction, faction);
            player.IsHuman = side.Properties.TryGetValue("playerIsHuman", out var human) && human is true;
            if (side.Properties.TryGetValue("playerDisplayName", out var display) && display is string displayName && displayName.Length > 0)
                player.DisplayName = displayName;
            if (side.Properties.TryGetValue("playerColor", out var color) && color is int argb)
                player.Color = (uint)argb & 0xFFFFFF;
        }

        // Alliances are lists of player names.
        foreach (var side in sides.Sides)
        {
            var player = Find(side.Name);
            if (player == null)
                continue;
            ApplyRelations(player, side.Properties, "playerAllies", Relationship.Allies);
            ApplyRelations(player, side.Properties, "playerEnemies", Relationship.Enemies);
        }

        foreach (var teamProperties in sides.Teams)
        {
            string name = teamProperties.TryGetValue("teamName", out var n) ? n.ToString() ?? "" : "";
            string ownerName = teamProperties.TryGetValue("teamOwner", out var o) ? o.ToString() ?? "" : "";
            var owner = Find(ownerName) ?? Neutral;
            if (name.Length == 0)
                continue;

            var team = FindTeam(name);
            if (team == null)
            {
                team = new Team(owner, name);
                owner.Teams.Add(team);
                RegisterTeam(team);
            }
            foreach (var (key, value) in teamProperties)
                team.Properties[key] = value;
            team.IsSingleton = !teamProperties.TryGetValue("teamIsSingleton", out var singleton) || singleton is true;
        }
    }

    void ApplyRelations(Player player, Dictionary<string, object> properties, string key, Relationship relation)
    {
        if (!properties.TryGetValue(key, out var value))
            return;
        foreach (string otherName in (value.ToString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (Find(otherName) is { } other)
                player.SetRelationship(other, relation);
    }

    /// <summary>A player is defeated when they have no buildings and no units that count for victory.</summary>
    public void CheckVictoryConditions()
    {
        if (game.Scripts is { HandlesVictory: true })
            return;
        foreach (var player in Active.Where(p => p.StartPosition != null).ToList())
        {
            bool alive = game.ObjectsOwnedBy(player).Any(o =>
                !o.IsEffectivelyDead && (o.IsKindOf(Core.Kind.Structure) || o.IsKindOf("MP_COUNT_FOR_VICTORY") || o.AI != null && o.IsKindOf(Core.Kind.Selectable)));
            if (!alive)
            {
                player.IsDefeated = true;
                game.Events.Message($"{player.DisplayName} has been defeated.");
            }
        }
    }
}

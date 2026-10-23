using OpenGZH.Core.Rules;
using OpenGZH.Formats.Maps;

namespace OpenGZH.Logic.Players;

/// <summary>All players in the game, including the always-present neutral player.</summary>
public sealed class PlayerList
{
    readonly GameLogic game;
    readonly List<Player> players = new();

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
        if (!player.IsNeutral)
            game.Ranks.CheckRankUp(player); // apply rank 1
        return player;
    }

    /// <summary>A player by its name in the map ("player2", "PlyrCivilian").</summary>
    public Player? Find(string name) =>
        players.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? (name.Length > 0 ? players.FirstOrDefault(p => p.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)) : null);

    /// <summary>
    /// The original's findPlayerWithNameKey: the map name, which a skirmish computer player keeps even
    /// though its script name becomes its skirmish side's ("SkirmishAmerica2").
    /// </summary>
    public Player? FindByNameKey(string name) =>
        name.Length == 0 ? Neutral : players.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first instance of a team, by name (kept for older callers).</summary>
    public Team? FindTeam(string name) => game.Teams.FindTeam(name);

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
            ApplySideProperties(player, side.Properties);
            player.BuildList.Clear();
            player.BuildList.AddRange(side.BuildList.Select(AI.BuildListInfo.From));
        }

        // Alliances are lists of player names.
        foreach (var side in sides.Sides)
        {
            if (Find(side.Name) is not { } player)
                continue;
            ApplyRelations(player, side.Properties, "playerAllies", Relationship.Allies);
            ApplyRelations(player, side.Properties, "playerEnemies", Relationship.Enemies);
        }

        game.Teams.InitFromSides(sides.Teams);
        foreach (var player in players)
            player.SetDefaultTeam();
    }

    public static void ApplySideProperties(Player player, Dictionary<string, object> properties)
    {
        player.IsHuman = properties.TryGetValue("playerIsHuman", out var human) && human is true;
        if (properties.TryGetValue("playerDisplayName", out var display) && display is string displayName && displayName.Length > 0)
            player.DisplayName = displayName;
        if (properties.TryGetValue("playerColor", out var color) && color is int argb)
            player.Color = (uint)argb & 0xFFFFFF;
    }

    public void ApplyRelations(Player player, Dictionary<string, object> properties, string key, Relationship relation)
    {
        if (!properties.TryGetValue(key, out var value))
            return;
        foreach (string otherName in (value.ToString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (Find(otherName) is { } other)
                player.SetRelationship(other, relation);
    }

    /// <summary>Every player's computer brain and teams' generic scripts run (the original's PlayerList::update).</summary>
    public void Update()
    {
        foreach (var player in players.ToList())
            player.Update();
    }

    /// <summary>Calls each computer player's NewMap once the map is loaded.</summary>
    public void NewMap()
    {
        foreach (var player in players.ToList())
            player.AI?.NewMap();
    }

    /// <summary>The players whose relationship to the given player is one of the allowed kinds, as a mask.</summary>
    public uint PlayersWithRelationship(int playerIndex, bool enemies, bool neutral, bool allies)
    {
        if (playerIndex < 0 || playerIndex >= players.Count)
            return 0;
        var me = players[playerIndex];
        uint mask = 0;
        foreach (var p in players)
        {
            if (p == me)
                continue;
            var r = me.RelationshipTo(p.DefaultTeam);
            if ((enemies && r == Relationship.Enemies) || (neutral && r == Relationship.Neutral) || (allies && r == Relationship.Allies))
                mask |= p.PlayerMask;
        }
        return mask;
    }

    /// <summary>Victory checks moved to <see cref="VictoryConditions"/>; kept for callers.</summary>
    public void CheckVictoryConditions() => game.Victory.Update();
}

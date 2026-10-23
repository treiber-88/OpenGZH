namespace OpenGZH.Logic.Players;

/// <summary>
/// Multiplayer and skirmish defeat (the original's VictoryConditions): a player with no objects left
/// is defeated (the map is revealed to them and the rest of what they own dies); once only one
/// alliance remains the game is over. Missions decide victory with scripts instead, which read
/// <see cref="IsLocalAlliedVictory"/> and friends through conditions.
/// </summary>
public sealed class VictoryConditions
{
    readonly GameLogic game;
    readonly List<Player> players = new();
    readonly HashSet<Player> defeated = new();
    Player? local;
    bool localPlayerDefeated, singleAllianceRemaining, isObserver, cached;

    /// <summary>A multiplayer or skirmish game (the original checks the recorder's multiplayer flag).</summary>
    public bool IsMultiplayer { get; set; }

    public int EndFrame { get; private set; }
    public bool AmIObserver => isObserver;

    public VictoryConditions(GameLogic game) => this.game = game;

    static bool AreAllies(Player p1, Player p2) =>
        p1 != p2 && p1.RelationshipTo(p2.DefaultTeam) == Relationship.Allies && p2.RelationshipTo(p1.DefaultTeam) == Relationship.Allies;

    /// <summary>The players that count: everyone except the neutral player, civilians and observers.</summary>
    public void CachePlayers()
    {
        cached = true;
        players.Clear();
        local = null;
        if (!IsMultiplayer)
            return;
        foreach (var p in game.Players.All)
        {
            if (p.IsNeutral || p.Faction == null || p.FactionName.Equals("FactionCivilian", StringComparison.OrdinalIgnoreCase)
                || p.FactionName.Equals("FactionObserver", StringComparison.OrdinalIgnoreCase))
                continue;
            players.Add(p);
            if (p.IsLocalPlayer)
                local = p;
        }
        if (local == null)
        {
            localPlayerDefeated = true;
            isObserver = true;
        }
    }

    public void Update()
    {
        if (!cached)
            CachePlayers();
        if (!IsMultiplayer || (local == null && !isObserver))
            return;
        if (!singleAllianceRemaining)
        {
            bool multipleAlliances = false;
            Player? alive = null;
            foreach (var p in players)
            {
                if (HasSinglePlayerBeenDefeated(p))
                    continue;
                if (alive == null)
                    alive = p;
                else if (!AreAllies(alive, p))
                {
                    multipleAlliances = true;
                    break;
                }
            }
            if (!multipleAlliances)
            {
                singleAllianceRemaining = true;
                EndFrame = game.Frame;
            }
        }
        foreach (var p in players)
        {
            if (defeated.Contains(p) || !HasSinglePlayerBeenDefeated(p))
                continue;
            defeated.Add(p);
            p.IsDefeated = true;
            if (game.Frame > 1)
            {
                game.Shroud.RevealMapForPlayerPermanently(p);
                game.Events.Message($"{p.DisplayName} has been defeated.");
                game.Events.PlaySound("GUIMessageReceived");
            }
            p.KillPlayer();
        }
        if (!localPlayerDefeated && !isObserver && local != null && HasSinglePlayerBeenDefeated(local))
            localPlayerDefeated = true;
    }

    public bool HasAchievedVictory(Player? player)
    {
        if (player == null || !singleAllianceRemaining)
            return false;
        return players.Any(p => !HasSinglePlayerBeenDefeated(p) && (p == player || AreAllies(p, player)));
    }

    public bool HasBeenDefeated(Player? player) => player != null && singleAllianceRemaining && !HasAchievedVictory(player);

    /// <summary>No units and no buildings left (the default NOUNITS | NOBUILDINGS rule).</summary>
    public static bool HasSinglePlayerBeenDefeated(Player? player) => player != null && !player.HasAnyObjects();

    public bool IsLocalAlliedVictory => !isObserver && HasAchievedVictory(local);
    public bool IsLocalAlliedDefeat => isObserver ? singleAllianceRemaining : HasBeenDefeated(local);
    public bool IsLocalDefeat => !isObserver && localPlayerDefeated;
}

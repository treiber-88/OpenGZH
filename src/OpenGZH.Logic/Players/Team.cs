using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Players;

/// <summary>
/// A group of objects owned by one player. Every object is on a team; maps and scripts create named
/// teams (e.g. "teamGLA_Attackers") and give them orders. Each player has a default team.
/// </summary>
public sealed class Team
{
    public Player Owner { get; }
    public string Name { get; }
    public bool IsDefault { get; }

    /// <summary>Settings from the map's team definition (unit lists, AI behaviour, ...).</summary>
    public Dictionary<string, object> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A team prototype may be instanced several times by scripts; a singleton only once.</summary>
    public bool IsSingleton { get; set; } = true;

    /// <summary>Script-visible state name (e.g. "Attacking").</summary>
    public string State { get; set; } = "";

    readonly Dictionary<Team, Relationship> teamRelations = new();

    public Team(Player owner, string name, bool isDefault = false)
    {
        Owner = owner;
        Name = name;
        IsDefault = isDefault;
    }

    /// <summary>The living members of this team.</summary>
    public IEnumerable<GameObject> Members => Owner.Game.Objects.Where(o => o.Team == this && !o.IsEffectivelyDead);

    public bool HasAnyMembers => Members.Any();

    public void SetRelationship(Team other, Relationship relation) => teamRelations[other] = relation;

    public Relationship RelationshipTo(Team other) =>
        teamRelations.TryGetValue(other, out var relation) ? relation : Owner.RelationshipTo(other.Owner);

    public override string ToString() => $"{Name} ({Owner})";
}

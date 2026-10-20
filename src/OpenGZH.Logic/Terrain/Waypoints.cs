using System.Numerics;
using OpenGZH.Formats.Maps;

namespace OpenGZH.Logic.Terrain;

/// <summary>A named point on the map; waypoints link into paths that scripts send units along.</summary>
public sealed class Waypoint
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required Vector3 Position { get; init; }
    public List<string> PathLabels { get; } = new();
    public List<Waypoint> Next { get; } = new();
}

/// <summary>All of a map's waypoints and the links between them.</summary>
public sealed class WaypointList
{
    readonly Dictionary<int, Waypoint> byId = new();
    readonly Dictionary<string, Waypoint> byName = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<Waypoint> All => byId.Values;

    public WaypointList(MapFile map, TerrainLogic terrain)
    {
        foreach (var obj in map.Objects.Where(o => o.IsWaypoint))
        {
            int id = obj.Properties.TryGetValue("waypointID", out var v) && v is int i ? i : 0;
            string name = obj.Get("waypointName") ?? $"waypoint{id}";
            var position = obj.Position with { Z = terrain.GroundHeight(obj.Position) };
            var waypoint = new Waypoint { Id = id, Name = name, Position = position };
            foreach (string key in new[] { "waypointPathLabel1", "waypointPathLabel2", "waypointPathLabel3" })
                if (obj.Get(key) is { Length: > 0 } label)
                    waypoint.PathLabels.Add(label);
            byId[id] = waypoint;
            byName.TryAdd(name, waypoint);
        }
        foreach (var (from, to) in map.WaypointLinks)
            if (byId.TryGetValue(from, out var a) && byId.TryGetValue(to, out var b))
                a.Next.Add(b);
    }

    public Waypoint? Find(string name) => byName.GetValueOrDefault(name);

    /// <summary>
    /// The route starting at a waypoint, following links until the end (or a loop). Where a path
    /// branches, <paramref name="pickBranch"/> chooses (scripts use the game's random generator).
    /// </summary>
    public List<Vector3> RouteFrom(Waypoint start, Func<int, int> pickBranch)
    {
        var route = new List<Vector3>();
        var visited = new HashSet<Waypoint>();
        for (var current = start; current != null && visited.Add(current);)
        {
            route.Add(current.Position);
            current = current.Next.Count switch
            {
                0 => null,
                1 => current.Next[0],
                _ => current.Next[pickBranch(current.Next.Count)],
            };
        }
        return route;
    }

    /// <summary>The start of a labelled path: a waypoint with that label that no other labelled waypoint links to.</summary>
    public Waypoint? PathStart(string label)
    {
        var labelled = byId.Values.Where(w => w.PathLabels.Contains(label, StringComparer.OrdinalIgnoreCase)).ToList();
        return labelled.FirstOrDefault(w => !labelled.Any(other => other.Next.Contains(w))) ?? labelled.FirstOrDefault();
    }
}

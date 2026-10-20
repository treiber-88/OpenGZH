using System.Numerics;

namespace OpenGZH.Formats.Maps;

/// <summary>
/// The players ("sides") and teams defined by a map, plus each side's scripts.
/// Mission logic in the campaign lives entirely in these scripts.
/// </summary>
public sealed class SidesList
{
    public List<MapSide> Sides { get; } = new();

    /// <summary>Team definitions (dictionaries with teamName, teamOwner, unit lists, behaviour...).</summary>
    public List<Dictionary<string, object>> Teams { get; } = new();

    internal static SidesList Read(DataChunkReader chunks, DataChunkReader.Chunk chunk)
    {
        var reader = chunks.Reader;
        var list = new SidesList();

        int sideCount = reader.ReadInt32();
        for (int i = 0; i < sideCount; i++)
        {
            var side = new MapSide { Properties = chunks.ReadDict() };
            int buildCount = reader.ReadInt32();
            for (int j = 0; j < buildCount; j++)
            {
                var entry = new BuildListEntry
                {
                    BuildingName = chunks.ReadAsciiString(),
                    TemplateName = chunks.ReadAsciiString(),
                    Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()) with { Z = 0 },
                    Angle = reader.ReadSingle(),
                    InitiallyBuilt = reader.ReadByte() != 0,
                    Rebuilds = reader.ReadInt32(),
                };
                if (chunk.Version >= 3)
                {
                    entry.Script = chunks.ReadAsciiString();
                    entry.Health = reader.ReadInt32();
                    entry.Whiner = reader.ReadByte() != 0;
                    entry.Unsellable = reader.ReadByte() != 0;
                    entry.Repairable = reader.ReadByte() != 0;
                }
                side.BuildList.Add(entry);
            }
            list.Sides.Add(side);
        }

        if (chunk.Version >= 2)
        {
            int teamCount = reader.ReadInt32();
            for (int i = 0; i < teamCount; i++)
                list.Teams.Add(chunks.ReadDict());
        }

        // Nested: PlayerScriptsList → one ScriptList per side.
        foreach (var sub in chunks.Chunks(chunk.End))
        {
            if (sub.Label != "PlayerScriptsList")
                continue;
            int sideIndex = 0;
            foreach (var scriptListChunk in chunks.SubChunks(sub))
            {
                if (scriptListChunk.Label != "ScriptList")
                    continue;
                var scripts = ScriptList.Read(chunks, scriptListChunk);
                if (sideIndex < list.Sides.Count)
                    list.Sides[sideIndex].Scripts = scripts;
                sideIndex++;
            }
        }

        return list;
    }
}

public sealed class MapSide
{
    /// <summary>playerName, playerFaction, playerIsHuman, playerDisplayName, playerAllies, playerEnemies, ...</summary>
    public Dictionary<string, object> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BuildListEntry> BuildList { get; } = new();
    public ScriptList Scripts { get; set; } = new();

    public string Name => Properties.TryGetValue("playerName", out var v) ? v.ToString() ?? "" : "";
    public string Faction => Properties.TryGetValue("playerFaction", out var v) ? v.ToString() ?? "" : "";
}

/// <summary>A structure the AI (or the map) should build or rebuild at a fixed spot.</summary>
public sealed class BuildListEntry
{
    public string BuildingName { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public Vector3 Position { get; set; }
    public float Angle { get; set; }
    public bool InitiallyBuilt { get; set; }
    public int Rebuilds { get; set; }
    public string Script { get; set; } = "";
    public int Health { get; set; } = 100;
    public bool Whiner { get; set; }
    public bool Unsellable { get; set; }
    public bool Repairable { get; set; }
}

/// <summary>All scripts of one side, loose or in groups.</summary>
public sealed class ScriptList
{
    public List<MapScript> Scripts { get; } = new();
    public List<ScriptGroup> Groups { get; } = new();

    public IEnumerable<MapScript> AllScripts => Scripts.Concat(Groups.SelectMany(g => g.Scripts));

    internal static ScriptList Read(DataChunkReader chunks, DataChunkReader.Chunk chunk)
    {
        var list = new ScriptList();
        foreach (var sub in chunks.SubChunks(chunk))
        {
            if (sub.Label == "Script")
            {
                list.Scripts.Add(MapScript.Read(chunks, sub));
            }
            else if (sub.Label == "ScriptGroup")
            {
                var group = new ScriptGroup
                {
                    Name = chunks.ReadAsciiString(),
                    IsActive = chunks.Reader.ReadByte() != 0,
                };
                if (sub.Version == 2)
                    group.IsSubroutine = chunks.Reader.ReadByte() != 0;
                foreach (var scriptChunk in chunks.Chunks(sub.End))
                    if (scriptChunk.Label == "Script")
                        group.Scripts.Add(MapScript.Read(chunks, scriptChunk));
                list.Groups.Add(group);
            }
        }
        return list;
    }
}

public sealed class ScriptGroup
{
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool IsSubroutine { get; set; }
    public List<MapScript> Scripts { get; } = new();
}

/// <summary>
/// One script: "if (any OR-clause, each an AND of conditions) then actions else false-actions".
/// Scripts run once (one-shot) or every evaluation, optionally only on some difficulties.
/// </summary>
public sealed class MapScript
{
    public string Name { get; set; } = "";
    public string Comment { get; set; } = "";
    public string ConditionComment { get; set; } = "";
    public string ActionComment { get; set; } = "";
    public bool IsActive { get; set; }
    public bool IsOneShot { get; set; }
    public bool Easy { get; set; }
    public bool Normal { get; set; }
    public bool Hard { get; set; }
    public bool IsSubroutine { get; set; }
    public int DelayEvaluationSeconds { get; set; }

    /// <summary>The script is true when any inner list is true; an inner list is true when all its conditions are.</summary>
    public List<List<ScriptCondition>> OrConditions { get; } = new();
    public List<ScriptAction> Actions { get; } = new();
    public List<ScriptAction> FalseActions { get; } = new();

    internal static MapScript Read(DataChunkReader chunks, DataChunkReader.Chunk chunk)
    {
        var reader = chunks.Reader;
        var script = new MapScript
        {
            Name = chunks.ReadAsciiString(),
            Comment = chunks.ReadAsciiString(),
            ConditionComment = chunks.ReadAsciiString(),
            ActionComment = chunks.ReadAsciiString(),
            IsActive = reader.ReadByte() != 0,
            IsOneShot = reader.ReadByte() != 0,
            Easy = reader.ReadByte() != 0,
            Normal = reader.ReadByte() != 0,
            Hard = reader.ReadByte() != 0,
            IsSubroutine = reader.ReadByte() != 0,
        };
        if (chunk.Version >= 2)
            script.DelayEvaluationSeconds = reader.ReadInt32();

        foreach (var sub in chunks.Chunks(chunk.End))
        {
            switch (sub.Label)
            {
                case "OrCondition":
                    var and = new List<ScriptCondition>();
                    foreach (var conditionChunk in chunks.SubChunks(sub))
                        if (conditionChunk.Label == "Condition")
                            and.Add(ScriptCondition.Read(chunks, conditionChunk));
                    script.OrConditions.Add(and);
                    break;
                case "ScriptAction":
                    script.Actions.Add(ScriptAction.Read(chunks, sub, nameKeyVersion: 2));
                    break;
                case "ScriptActionFalse":
                    script.FalseActions.Add(ScriptAction.Read(chunks, sub, nameKeyVersion: 2));
                    break;
            }
        }
        return script;
    }
}

/// <summary>A script condition, identified by its internal name (e.g. "TEAM_DESTROYED").</summary>
public sealed class ScriptCondition
{
    public int TypeId { get; set; }
    public string Name { get; set; } = "";
    public List<ScriptParameter> Parameters { get; } = new();

    internal static ScriptCondition Read(DataChunkReader chunks, DataChunkReader.Chunk chunk)
    {
        var reader = chunks.Reader;
        var condition = new ScriptCondition { TypeId = reader.ReadInt32() };
        if (chunk.Version >= 4)
            condition.Name = chunks.NameOf((uint)(reader.ReadInt32() >> 8));
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
            condition.Parameters.Add(ScriptParameter.Read(chunks));
        return condition;
    }

    public override string ToString() => $"{Name}({string.Join(", ", Parameters)})";
}

/// <summary>A script action, identified by its internal name (e.g. "VICTORY").</summary>
public sealed class ScriptAction
{
    public int TypeId { get; set; }
    public string Name { get; set; } = "";
    public List<ScriptParameter> Parameters { get; } = new();

    internal static ScriptAction Read(DataChunkReader chunks, DataChunkReader.Chunk chunk, int nameKeyVersion)
    {
        var reader = chunks.Reader;
        var action = new ScriptAction { TypeId = reader.ReadInt32() };
        if (chunk.Version >= nameKeyVersion)
            action.Name = chunks.NameOf((uint)(reader.ReadInt32() >> 8));
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
            action.Parameters.Add(ScriptParameter.Read(chunks));
        return action;
    }

    public override string ToString() => $"{Name}({string.Join(", ", Parameters)})";
}

/// <summary>A script parameter. Which field is meaningful depends on <see cref="Type"/>.</summary>
public sealed class ScriptParameter
{
    /// <summary>Original ParameterType enum value (0 = INT, 1 = REAL, 3 = TEAM, 7 = WAYPOINT, 16 = COORD3D, ...).</summary>
    public int Type { get; set; }
    public int Int { get; set; }
    public float Real { get; set; }
    public string String { get; set; } = "";
    public Vector3 Coord { get; set; }

    const int Coord3DType = 16;
    const int ObjectType = 15;
    const int UpgradeType = 32;

    internal static ScriptParameter Read(DataChunkReader chunks)
    {
        var reader = chunks.Reader;
        var parameter = new ScriptParameter { Type = reader.ReadInt32() };
        if (parameter.Type == Coord3DType)
        {
            parameter.Coord = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
        else
        {
            parameter.Int = reader.ReadInt32();
            parameter.Real = reader.ReadSingle();
            parameter.String = chunks.ReadAsciiString();
        }

        // Fix-ups the original loader applied to old data.
        if (parameter.Type == ObjectType && parameter.String.StartsWith("Fundamentalist", StringComparison.Ordinal))
            parameter.String = "GLA" + parameter.String["Fundamentalist".Length..];
        if (parameter.Type == UpgradeType && parameter.String is "Upgrade_AmericaRangerCaptureBuilding"
                or "Upgrade_ChinaRedguardCaptureBuilding" or "Upgrade_GLARebelCaptureBuilding")
            parameter.String = "Upgrade_InfantryCaptureBuilding";

        return parameter;
    }

    public override string ToString() => Type == Coord3DType ? Coord.ToString() : String.Length > 0 ? String : Int != 0 ? Int.ToString() : Real.ToString();
}

/// <summary>A named area on the map, used by scripts ("unit entered area") and for water.</summary>
public sealed class PolygonTrigger
{
    public string Name { get; set; } = "";
    public string Layer { get; set; } = "";
    public int Id { get; set; }
    public bool IsWater { get; set; }
    public bool IsRiver { get; set; }
    public int RiverStart { get; set; }
    public List<Vector3> Points { get; } = new();

    /// <summary>True if a point (x, y) lies inside the polygon.</summary>
    public bool Contains(float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var a = Points[i];
            var b = Points[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    internal static IEnumerable<PolygonTrigger> ReadAll(DataChunkReader chunks, DataChunkReader.Chunk chunk)
    {
        var reader = chunks.Reader;
        int count = reader.ReadInt32();
        var list = new List<PolygonTrigger>(count);
        for (int t = 0; t < count; t++)
        {
            var trigger = new PolygonTrigger { Name = chunks.ReadAsciiString() };
            if (chunk.Version >= 4)
                trigger.Layer = chunks.ReadAsciiString();
            trigger.Id = reader.ReadInt32();
            if (chunk.Version >= 2)
                trigger.IsWater = reader.ReadByte() != 0;
            if (chunk.Version >= 3)
            {
                trigger.IsRiver = reader.ReadByte() != 0;
                trigger.RiverStart = reader.ReadInt32();
            }
            int points = reader.ReadInt32();
            for (int i = 0; i < points; i++)
                trigger.Points.Add(new Vector3(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
            if (points >= 2)
                list.Add(trigger);
        }
        return list;
    }
}

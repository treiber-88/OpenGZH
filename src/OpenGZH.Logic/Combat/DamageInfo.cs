using System.Numerics;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Combat;

/// <summary>One hit: who caused it, how much, of which kind. Filled in with the outcome after it is applied.</summary>
public sealed class DamageInfo
{
    public uint SourceId { get; init; }
    public string DamageType { get; init; } = "EXPLOSION";
    public string DeathType { get; init; } = "NORMAL";
    public float Amount { get; init; }

    /// <summary>Instantly kill regardless of health (e.g. "kill" script actions, sold structures).</summary>
    public bool Kill { get; init; }

    /// <summary>For DamageType STATUS: which status to apply (e.g. "FAERIE_FIRE") and for how long (Amount ms).</summary>
    public string? DamageStatusType { get; init; }

    /// <summary>Where the damage came from (for directional effects).</summary>
    public Vector3? SourcePosition { get; init; }

    /// <summary>Set after applying: damage actually dealt after armor.</summary>
    public float ActualDamageDealt { get; set; }

    /// <summary>The original's "kill it for sure" amount (crushing, drowning).</summary>
    public const float HugeDamage = 999999f;

    public static DamageInfo Killing(string deathType = "NORMAL", uint sourceId = 0) =>
        new() { Kill = true, DamageType = "UNRESISTABLE", DeathType = deathType, SourceId = sourceId };

    public override string ToString() => Kill ? $"kill ({DeathType})" : $"{Amount} {DamageType}";
}

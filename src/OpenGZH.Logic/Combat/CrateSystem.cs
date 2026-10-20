using OpenGZH.Core.Rules;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Combat;

/// <summary>
/// Crates dropped by destroyed units (rules "Crates"): each CrateData entry has a chance to spawn,
/// conditions on who killed the unit, and a list of crate objects with weights.
/// </summary>
public sealed class CrateSystem
{
    readonly GameLogic game;

    public CrateSystem(GameLogic game) => this.game = game;

    public void TrySpawn(string crateDataNames, GameObject victim, DamageInfo killingBlow)
    {
        foreach (string name in RuleValue.Words(crateDataNames))
        {
            var data = game.Rules.Get("Crates", name);
            if (data == null)
                continue;

            float chance = RuleValue.Float(data.GetString("CreationChance"), 1f);
            if (!game.Random.Chance(chance))
                continue;

            var killer = game.FindObject(killingBlow.SourceId);
            string requiredScience = data.GetString("KillerScience");
            if (requiredScience.Length > 0 && (killer == null || !killer.Owner.HasScience(requiredScience)))
                continue;
            string killedByType = data.GetString("KilledByType");
            if (killedByType.Length > 0 && (killer == null || !RuleValue.Words(killedByType).Any(killer.IsKindOf)))
                continue;
            if (data.GetString("VeterancyLevel") is { Length: > 0 } level && !victim.Veterancy.ToString().Equals(level, StringComparison.OrdinalIgnoreCase))
                continue;

            string? crate = PickCrate(data);
            if (crate == null)
                continue;
            var position = victim.Position;
            position.Z = game.Terrain.GroundHeight(position);
            game.CreateObject(crate, game.Players.Neutral, position);
            return; // one crate per death
        }
    }

    /// <summary>"CrateObject: Name weight" lines; weights are relative.</summary>
    string? PickCrate(RuleNode data)
    {
        if (!data.TryGet("CrateObject", out var entries))
            return null;
        var options = entries.AsList()
            .Select(e => RuleValue.Words(e.Value))
            .Where(w => w.Length >= 1)
            .Select(w => (Name: w[0], Weight: w.Length > 1 ? RuleValue.Float(w[1], 1) : 1f))
            .ToList();
        float total = options.Sum(o => o.Weight);
        float roll = game.Random.Float(0, total);
        foreach (var (crateName, weight) in options)
        {
            roll -= weight;
            if (roll <= 0)
                return crateName;
        }
        return options.LastOrDefault().Name;
    }
}

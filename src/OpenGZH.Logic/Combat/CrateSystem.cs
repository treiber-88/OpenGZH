using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;
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

    /// <summary>
    /// The original's CreateCrateDie: each listed crate entry is tried in turn (no crates for an
    /// ally's kill). An entry needs its CreationChance roll, the victim's VeterancyLevel, a killer of
    /// KilledByType and a killer with KillerScience; its crate is then picked by running total of the
    /// CrateObject chances. OwnedByMaker crates belong to the victim's player.
    /// </summary>
    public void TrySpawn(IEnumerable<string> crateDataNames, GameObject victim, DamageInfo killingBlow)
    {
        var killer = game.FindObject(killingBlow.SourceId);
        if (killer != null && killer.Owner.IsAllyOf(victim.Owner))
            return;
        foreach (string name in crateDataNames)
        {
            var data = game.Rules.Get("Crates", name);
            if (data == null)
                continue;
            if (!(game.Random.Float(0, 1) < RuleValue.Float(data.GetString("CreationChance"), 0)))
                continue;
            if (data.GetString("VeterancyLevel") is { Length: > 0 } level && !victim.Veterancy.ToString().Equals(level, StringComparison.OrdinalIgnoreCase)
                && !(level.Equals("HEROIC", StringComparison.OrdinalIgnoreCase) && victim.Veterancy == VeterancyLevel.Heroic))
                continue;
            string killedByType = data.GetString("KilledByType");
            if (killedByType.Length > 0 && (killer == null || !RuleValue.Words(killedByType).Any(killer.IsKindOf)))
                continue;
            string requiredScience = data.GetString("KillerScience");
            if (requiredScience.Length > 0 && (killer == null || !killer.Owner.HasScience(requiredScience)))
                continue;
            var crate = CreateCrate(data, victim);
            if (crate != null && RuleValue.Bool(data.GetString("OwnedByMaker")))
                crate.SetTeam(victim.Owner.DefaultTeam);
        }
    }

    GameObject? CreateCrate(RuleNode data, GameObject victim)
    {
        if (!data.TryGet("CrateObject", out var entries))
            return null;
        float pick = game.Random.Float(0, 1);
        float total = 0;
        string crateName = "";
        foreach (var words in entries.AsList().Select(e => RuleValue.Words(e.Value)).Where(w => w.Length >= 1))
        {
            total += words.Length > 1 ? RuleValue.Float(words[1], 0) : 0;
            if (total > pick)
            {
                crateName = words[0];
                break;
            }
        }
        if (game.Templates.Find(crateName) == null)
            return null;
        var spot = victim.IsAboveTerrain && victim.HeightAboveTerrain > 5 ? victim.Position
            : game.Pathfinder.NearestPassable(victim.Position, Locomotion.Surfaces.Ground) ?? victim.Position;
        if (MathUtil.Distance2D(spot, victim.Position) > 125)
            return null;
        spot.Z = game.Terrain.LayerHeight(spot.X, spot.Y);
        return game.CreateObject(crateName, game.Players.Neutral, spot, game.Random.Float(0, 2 * MathF.PI));
    }
}

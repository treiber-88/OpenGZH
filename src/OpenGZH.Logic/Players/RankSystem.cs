using OpenGZH.Core.Rules;

namespace OpenGZH.Logic.Players;

/// <summary>
/// The general's promotion system (rules "Ranks"): skill points from kills raise the rank,
/// and each new rank grants science purchase points for generals' powers.
/// </summary>
public sealed class RankSystem
{
    readonly GameLogic game;
    readonly List<(int SkillPointsNeeded, int SciencePurchasePointsGranted, string[] SciencesGranted)> ranks = new();

    public RankSystem(GameLogic game)
    {
        this.game = game;
        // Ranks are named "1".."5" in the original data.
        var section = game.Rules.Section("Ranks");
        foreach (var (_, rank) in section.Entries.OrderBy(e => RuleValue.Int(e.Key)))
            ranks.Add((
                RuleValue.Int(rank.GetString("SkillPointsNeeded")),
                RuleValue.Int(rank.GetString("SciencePurchasePointsGranted")),
                RuleValue.Words(rank.GetString("SciencesGranted"))));
    }

    public int MaxRank => ranks.Count;

    /// <summary>Rank <paramref name="level"/> (1-based): skill points needed, purchase points and sciences it gives.</summary>
    public (int SkillPointsNeeded, int SciencePurchasePointsGranted, string[] SciencesGranted) Rank(int level) =>
        level >= 1 && level <= ranks.Count ? ranks[level - 1] : (0, 0, []);

    public int SkillPointsForRank(int rank) => rank >= 1 && rank <= ranks.Count ? ranks[rank - 1].SkillPointsNeeded : int.MaxValue;

    public void CheckRankUp(Player player)
    {
        while (player.RankLevel < ranks.Count && player.RankLevel < game.RankLevelLimit && player.SkillPoints >= ranks[player.RankLevel].SkillPointsNeeded)
        {
            var next = ranks[player.RankLevel];
            player.RankLevel++;
            player.SciencePurchasePoints += next.SciencePurchasePointsGranted;
            foreach (string science in next.SciencesGranted)
                player.AddScience(science);
            game.Events.Eva("GeneralLevelUp", player);
        }
    }
}

namespace OpenGZH.Logic.Core;

/// <summary>
/// Picks the option whose condition flags best match the current flags, the way the original game
/// chooses weapon sets, armor sets and model condition states:
///   1. the most flags in common wins;
///   2. on a tie, the option with the fewest flags that are NOT currently set wins.
/// (So with DAMAGED set, "DAMAGED" beats "DAMAGED NIGHT", and "NONE" beats "PLAYER_UPGRADE" when nothing is set.)
/// </summary>
public static class SparseMatch
{
    public static T? Best<T>(IEnumerable<T> options, Func<T, IEnumerable<FlagSet>> conditionsOf, FlagSet current) where T : class
    {
        T? best = null;
        int bestMatch = 0;
        int bestExtraneous = int.MaxValue;
        foreach (var option in options)
        {
            foreach (var conditions in conditionsOf(option))
            {
                int match = conditions.CountInCommon(current);
                int extraneous = conditions.Count - match;
                if (match > bestMatch || match >= bestMatch && extraneous < bestExtraneous)
                {
                    best = option;
                    bestMatch = match;
                    bestExtraneous = extraneous;
                }
            }
        }
        return best;
    }
}

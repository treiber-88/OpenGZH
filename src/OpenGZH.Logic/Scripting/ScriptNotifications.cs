namespace OpenGZH.Logic.Scripting;

/// <summary>
/// Things that happened in the game that mission scripts can ask about later: special powers that
/// fired, reached their midpoint or finished, and upgrades that completed. Like the original, each
/// is a list per player; a condition can consume the entry it matched so it only fires once.
/// </summary>
public sealed partial class ScriptEngine
{
    readonly record struct NamedEvent(string Name, uint SourceId);

    readonly Dictionary<int, List<NamedEvent>> triggeredSpecialPowers = new();
    readonly Dictionary<int, List<NamedEvent>> midwaySpecialPowers = new();
    readonly Dictionary<int, List<NamedEvent>> finishedSpecialPowers = new();
    readonly Dictionary<int, List<NamedEvent>> completedUpgrades = new();

    public void NotifyOfTriggeredSpecialPower(int player, string power, uint sourceId) => Add(triggeredSpecialPowers, player, power, sourceId);
    public void NotifyOfMidwaySpecialPower(int player, string power, uint sourceId) => Add(midwaySpecialPowers, player, power, sourceId);
    public void NotifyOfCompletedSpecialPower(int player, string power, uint sourceId) => Add(finishedSpecialPowers, player, power, sourceId);
    public void NotifyOfCompletedUpgrade(int player, string upgrade, uint sourceId) => Add(completedUpgrades, player, upgrade, sourceId);

    /// <summary>sourceId 0 matches any source.</summary>
    public bool IsSpecialPowerTriggered(int player, string power, bool remove, uint sourceId = 0) => Find(triggeredSpecialPowers, player, power, remove, sourceId);
    public bool IsSpecialPowerMidway(int player, string power, bool remove, uint sourceId = 0) => Find(midwaySpecialPowers, player, power, remove, sourceId);
    public bool IsSpecialPowerComplete(int player, string power, bool remove, uint sourceId = 0) => Find(finishedSpecialPowers, player, power, remove, sourceId);
    public bool IsUpgradeComplete(int player, string upgrade, bool remove, uint sourceId = 0) => Find(completedUpgrades, player, upgrade, remove, sourceId);

    static void Add(Dictionary<int, List<NamedEvent>> lists, int player, string name, uint sourceId)
    {
        if (!lists.TryGetValue(player, out var list))
            lists[player] = list = new();
        list.Add(new NamedEvent(name, sourceId));
    }

    static bool Find(Dictionary<int, List<NamedEvent>> lists, int player, string name, bool remove, uint sourceId)
    {
        if (!lists.TryGetValue(player, out var list))
            return false;
        int index = list.FindIndex(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (sourceId == 0 || e.SourceId == sourceId));
        if (index < 0)
            return false;
        if (remove)
            list.RemoveAt(index);
        return true;
    }
}

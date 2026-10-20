using OpenGZH.Core.Rules;

namespace OpenGZH.Logic.Templates;

/// <summary>
/// A command button from the rules ("CommandButtons" section): what clicking it does (Command),
/// what it builds or researches, and how it looks (ButtonImage, TextLabel, DescriptLabel).
/// The same buttons drive both the player's control bar and the computer player.
/// </summary>
public sealed record CommandButton(
    string Name,
    string Command,
    string Object,
    string Upgrade,
    string SpecialPower,
    string Science,
    string WeaponSlot,
    string ButtonImage,
    string TextLabel,
    string DescriptLabel,
    string[] Options,
    string CursorName,
    string InvalidCursorName,
    string RadiusCursorType,
    string UnitSpecificSound)
{
    public static CommandButton FromRules(string name, RuleNode node) => new(
        name,
        node.GetString("Command"),
        node.GetString("Object"),
        node.GetString("Upgrade"),
        node.GetString("SpecialPower"),
        node.GetString("Science"),
        node.GetString("WeaponSlot"),
        node.GetString("ButtonImage"),
        node.GetString("TextLabel"),
        node.GetString("DescriptLabel"),
        RuleValue.Words(node.GetString("Options")),
        node.GetString("CursorName"),
        node.GetString("InvalidCursorName"),
        node.GetString("RadiusCursorType"),
        node.GetString("UnitSpecificSound"));

    public bool HasOption(string option) => Options.Contains(option, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Reads command sets: up to 18 numbered slots, each naming a command button.</summary>
public static class CommandSets
{
    public static List<(int Slot, CommandButton Button)> Buttons(RulesCatalog catalog, string commandSetName)
    {
        var result = new List<(int, CommandButton)>();
        var set = catalog.CommandSet(commandSetName);
        if (set == null)
            return result;
        foreach (var (key, value) in set.Entries)
        {
            if (!int.TryParse(key, out int slot) || value.Value == null)
                continue;
            var button = catalog.CommandButton(value.Value);
            if (button != null)
                result.Add((slot, CommandButton.FromRules(value.Value, button)));
        }
        return result;
    }

    /// <summary>Everything an object can construct (DOZER_CONSTRUCT buttons) or produce (UNIT_BUILD buttons).</summary>
    public static IEnumerable<string> BuildableObjects(RulesCatalog catalog, string commandSetName, string command) =>
        Buttons(catalog, commandSetName).Where(b => b.Button.Command.Equals(command, StringComparison.OrdinalIgnoreCase))
            .Select(b => b.Button.Object).Where(o => o.Length > 0);
}

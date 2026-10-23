using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Locomotion;

namespace OpenGZH.Logic.Templates;

/// <summary>
/// Typed, cached access to the non-object rule definitions: weapons, armor, locomotors,
/// upgrades, command sets and buttons, object creation lists, special powers, sciences.
/// </summary>
public sealed class RulesCatalog
{
    readonly RuleSet rules;
    readonly Dictionary<string, WeaponTemplate?> weapons = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ArmorTemplate> armors = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, LocomotorTemplate?> locomotors = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SpecialPowerTemplate?> specialPowers = new(StringComparer.OrdinalIgnoreCase);

    public RulesCatalog(RuleSet rules)
    {
        this.rules = rules;
        GameData = new GameData(rules);
    }

    /// <summary>Global constants (damage thresholds, sell percentage, gravity, ...).</summary>
    public GameData GameData { get; }

    public RuleSet Rules => rules;

    public WeaponTemplate? Weapon(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Equals("None", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!weapons.TryGetValue(name, out var weapon))
            weapons[name] = weapon = rules.Get("Weapons", name) is { } node ? new WeaponTemplate(name, node) : null;
        return weapon;
    }

    public ArmorTemplate Armor(string name)
    {
        if (string.IsNullOrEmpty(name))
            return ArmorTemplate.None;
        if (!armors.TryGetValue(name, out var armor))
            armors[name] = armor = new ArmorTemplate(name, rules.Get("Armor", name));
        return armor;
    }

    public LocomotorTemplate? Locomotor(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (!locomotors.TryGetValue(name, out var locomotor))
            locomotors[name] = locomotor = rules.Get("Locomotors", name) is { } node ? new LocomotorTemplate(name, node) : null;
        return locomotor;
    }

    public RuleNode? Upgrade(string name) => rules.Get("Upgrades", name);

    /// <summary>A sound named in MiscAudio (wheel screech, lock-on tick...), or "".</summary>
    public string MiscAudio(string key) => rules.Section("MiscAudio").GetString(key);
    public RuleNode? CommandSet(string name) => rules.Get("CommandSets", name);
    public RuleNode? CommandButton(string name) => rules.Get("CommandButtons", name);

    readonly Dictionary<string, CommandButton?> commandButtons = new(StringComparer.Ordinal);

    /// <summary>A command button by name (the original's ControlBar::findCommandButton).</summary>
    public CommandButton? CommandButtonTemplate(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (!commandButtons.TryGetValue(name, out var button))
            commandButtons[name] = button = CommandButton(name) is { } node ? Templates.CommandButton.FromRules(name, node) : null;
        return button;
    }
    public RuleNode? ObjectCreationList(string name) => rules.Get("ObjectCreationLists", name);
    public SpecialPowerTemplate? SpecialPower(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Equals("None", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!specialPowers.TryGetValue(name, out var power))
            specialPowers[name] = power = rules.Get("SpecialPowers", name) is { } node ? new SpecialPowerTemplate(name, node) : null;
        return power;
    }
    public RuleNode? Science(string name) => rules.Get("Sciences", name);
    public RuleNode? FxList(string name) => rules.Get("FXLists", name);
    public RuleNode? ParticleSystem(string name) => rules.Get("ParticleSystems", name);
    public RuleNode? Faction(string name) => rules.Get("PlayerTemplates", name);
}

using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Templates;

/// <summary>
/// A special power from the rules ("SpecialPowers" section): a general's power, a superweapon or a
/// unit ability. The power's type (Enum, e.g. SPECIAL_DAISY_CUTTER) decides what targets it accepts;
/// the module on the object that owns it decides what it actually does.
/// </summary>
public sealed class SpecialPowerTemplate
{
    public string Name { get; }
    public RuleNode Rules { get; }

    /// <summary>The power's type, e.g. SPECIAL_PARADROP_AMERICA. Several powers can share a type.</summary>
    public string Type { get; }
    public int ReloadFrames { get; }

    /// <summary>The science the player must have bought to use it (empty if none).</summary>
    public string RequiredScience { get; }

    /// <summary>Everyone sees the countdown (superweapons).</summary>
    public bool PublicTimer { get; }

    /// <summary>One timer per player shared by all objects with the power (generals' powers).</summary>
    public bool SharedSyncedTimer { get; }
    public int DetectionFrames { get; }
    public int ViewObjectFrames { get; }
    public float ViewObjectRange { get; }
    public float RadiusCursorRadius { get; }

    /// <summary>Shown on the generals' power shortcut bar.</summary>
    public bool ShortcutPower { get; }
    public string InitiateSound { get; }
    public string InitiateAtLocationSound { get; }

    public SpecialPowerTemplate(string name, RuleNode rules)
    {
        Name = name;
        Rules = rules;
        Type = rules.GetString("Enum");
        ReloadFrames = LogicTime.Frames(RuleValue.Float(rules.GetString("ReloadTime")));
        string science = rules.GetString("RequiredScience");
        RequiredScience = science.Equals("None", StringComparison.OrdinalIgnoreCase) ? "" : science;
        PublicTimer = RuleValue.Bool(rules.GetString("PublicTimer"));
        SharedSyncedTimer = RuleValue.Bool(rules.GetString("SharedSyncedTimer"));
        DetectionFrames = LogicTime.Frames(RuleValue.Float(rules.GetString("DetectionTime")));
        ViewObjectFrames = LogicTime.Frames(RuleValue.Float(rules.GetString("ViewObjectDuration")));
        ViewObjectRange = RuleValue.Float(rules.GetString("ViewObjectRange"));
        RadiusCursorRadius = RuleValue.Float(rules.GetString("RadiusCursorRadius"));
        ShortcutPower = RuleValue.Bool(rules.GetString("ShortcutPower"));
        InitiateSound = rules.GetString("InitiateSound");
        InitiateAtLocationSound = rules.GetString("InitiateAtLocationSound");
    }

    public bool IsType(params string[] types) => types.Contains(Type, StringComparer.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

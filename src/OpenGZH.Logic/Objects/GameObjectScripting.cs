using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Objects;

/// <summary>Object state that mission scripts and the computer player read and change.</summary>
public sealed partial class GameObject
{
    /// <summary>
    /// How this object regards another (the original's Object::getRelationship): through the teams,
    /// so script overrides count. An undetected defector is neutral to everyone and looks like an ally.
    /// </summary>
    public Relationship RelationshipTo(GameObject? that)
    {
        if (Team == null || that == null)
            return Relationship.Neutral;
        if (UndetectedDefectorUntilFrame > Game.Frame)
            return Relationship.Neutral;
        if (that.UndetectedDefectorUntilFrame > Game.Frame)
            return Relationship.Allies;
        return Team.RelationshipTo(that.Team);
    }

    public bool IsEnemyOf(GameObject that) => RelationshipTo(that) == Relationship.Enemies;

    /// <summary>Outside the map's playable area (reinforcements waiting off the edge).</summary>
    public bool IsOffMap => Position.X < 0 || Position.Y < 0 || Position.X > Game.Terrain.WorldWidth || Position.Y > Game.Terrain.WorldHeight;

    public void HealCompletely()
    {
        if (Body != null && !IsEffectivelyDead)
            Body.AttemptHealing(Body.MaxHealth - Body.Health);
    }

    /// <summary>Would this upgrade switch on one of the object's upgrade modules (the original's affectedByUpgrade)?</summary>
    public bool IsAffectedByUpgrade(string upgrade)
    {
        bool Has(string u) => u.Equals(upgrade, StringComparison.OrdinalIgnoreCase) || HasUpgrade(u);
        foreach (var module in FindModules<Modules.UpgradeModule>())
        {
            if (module.IsActive || module.TriggeredBy.Count == 0)
                continue;
            bool triggered = module.RequiresAllTriggers ? module.TriggeredBy.All(Has) : module.TriggeredBy.Any(Has);
            if (triggered && !module.ConflictsWith.Any(HasUpgrade))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Script statuses (the original's setScriptStatus): SCRIPT_DISABLED and SCRIPT_UNPOWERED also
    /// switch the object off (DISABLED_SCRIPT_DISABLED / DISABLED_SCRIPT_UNDERPOWERED).
    /// </summary>
    public void SetScriptStatus(string status, bool set)
    {
        bool old = HasStatus(status);
        if (old == set)
            return;
        SetStatus(status, set);
        if (status == "SCRIPT_DISABLED")
            SetDisabled("DISABLED_SCRIPT_DISABLED", set);
        else if (status == "SCRIPT_UNPOWERED")
            SetDisabled("DISABLED_SCRIPT_UNDERPOWERED", set);
    }

    bool selectable = true;

    /// <summary>The original's isSelectable: ALWAYS_SELECTABLE things always are; otherwise not when switched off, unselectable or dead.</summary>
    public bool IsSelectable => IsKindOf("ALWAYS_SELECTABLE") || (selectable && !HasStatus(Status.Unselectable) && !IsEffectivelyDead);

    public void SetSelectable(bool value) => selectable = value;

    /// <summary>A script-chosen house color for this object (NAMED_CUSTOM_COLOR); null = the player's color.</summary>
    public uint? CustomIndicatorColor { get; set; }

    public uint IndicatorColor => CustomIndicatorColor ?? Owner.Color;

    /// <summary>Script-requested flashing (NAMED_FLASH): the drawable blinks this many times in this color.</summary>
    public int ScriptFlashCount { get; set; }
    public uint ScriptFlashColor { get; set; }

    /// <summary>Script-chosen emoticon over the unit, and the frame it goes away.</summary>
    public string Emoticon { get; private set; } = "";
    public int EmoticonUntilFrame { get; private set; }

    public void SetEmoticon(string name, int frames)
    {
        Emoticon = name;
        EmoticonUntilFrame = Game.Frame + frames;
    }

    /// <summary>Objects get the difficulty handicap bonuses unless scripts turn them off (OBJECT_ALLOW_BONUSES).</summary>
    public bool IsReceivingDifficultyBonus { get; set; } = true;

    /// <summary>The command set the object shows (an upgrade or script may have replaced the template's).</summary>
    public string CommandSetName => CommandSetOverride ?? Template.CommandSet;

    /// <summary>Transport slots this object takes up (TransportSlotCount on its definition).</summary>
    public int TransportSlotCount => RuleValue.Int(Template.Rules.GetString("TransportSlotCount"), 0);

    /// <summary>
    /// The original removes a unit from its selection group before giving it a script order. Groups are
    /// made fresh for each order here, so there is nothing to leave.
    /// </summary>
    public void LeaveGroup() { }

    /// <summary>A faction building (FS_ kinds: barracks, war factory, power plant ...).</summary>
    public bool IsFactionStructure => FactionStructureKinds.HasAny(KindOf);

    static readonly FlagSet FactionStructureKinds =
        Flags.KindOf.Parse(string.Join(' ', Enumerable.Range(0, Flags.KindOf.Count).Select(i => Flags.KindOf[i]).Where(n => n.StartsWith("FS_", StringComparison.Ordinal))));

    public bool HasAnySpecialPower => FindModules<Modules.SpecialPowers.SpecialPowerModule>().Any();
}

using OpenGZH.Core.Rules;

namespace OpenGZH.Logic.Templates;

/// <summary>
/// A fully resolved object definition (the original "ThingTemplate"): defaults applied,
/// reskins resolved, with typed access to the commonly used fields.
/// The raw rules node stays available so modules can read their own settings.
/// </summary>
public sealed class ObjectTemplate
{
    public string Name { get; }
    public RuleNode Rules { get; }

    public string Side { get; }
    public HashSet<string> KindOf { get; }
    public string EditorSorting { get; }
    public int BuildCost { get; }
    public float BuildTimeSeconds { get; }
    public float VisionRange { get; }

    /// <summary>Everyone (enemies too) sees this far around it (e.g. a nuke going off).</summary>
    public float ShroudRevealToAllRange { get; }

    /// <summary>Covers this far around it in shroud for its enemies (GPS scrambler).</summary>
    public float ShroudRange { get; }
    public float ShroudClearingRange { get; }
    public string DisplayName { get; }
    public string CommandSet { get; }
    public string ButtonImage { get; }
    public string SelectPortrait { get; }
    public float GeometryMajorRadius { get; }
    public float GeometryMinorRadius { get; }
    public float GeometryHeight { get; }
    /// <summary>"SPHERE", "CYLINDER" or "BOX".</summary>
    public string GeometryName { get; }

    /// <summary>The collision shape.</summary>
    public Core.Geometry Geometry { get; }
    public bool IsBuildable { get; }
    public int EnergyProduction { get; }

    /// <summary>What it can crush (higher crushes lower), and how hard it is to crush (255 = never).</summary>
    public int CrusherLevel { get; }
    public int CrushableLevel { get; }

    /// <summary>Extra power once a PowerPlantUpgrade (e.g. Control Rods) is done.</summary>
    public int EnergyBonus { get; }

    /// <summary>Modules in definition order (Draw, Body, Behavior, ClientUpdate...).</summary>
    public IReadOnlyList<ModuleDefinition> Modules { get; }

    /// <summary>A sound from the object's UnitSpecificSounds list (e.g. "VoiceCaptureBuildingComplete"), or "".</summary>
    public string UnitSound(string key) =>
        Rules.TryGet("UnitSpecificSounds", out var sounds) ? sounds.GetString(key) : "";

    /// <summary>An effect from the object's UnitSpecificFX list (e.g. "CombatDropKillFX"), or "".</summary>
    public string UnitFx(string key) =>
        Rules.TryGet("UnitSpecificFX", out var fx) ? fx.GetString(key) : "";

    public ObjectTemplate(string name, RuleNode rules)
    {
        Name = name;
        Rules = rules;
        Side = rules.GetString("Side", "Civilian");
        KindOf = new HashSet<string>(RuleValue.Words(rules.GetString("KindOf")), StringComparer.OrdinalIgnoreCase);
        EditorSorting = rules.GetString("EditorSorting", "NONE");
        BuildCost = RuleValue.Int(rules.GetString("BuildCost"));
        BuildTimeSeconds = RuleValue.Float(rules.GetString("BuildTime"), 1);
        VisionRange = RuleValue.Float(rules.GetString("VisionRange"));
        ShroudClearingRange = RuleValue.Float(rules.GetString("ShroudClearingRange"), VisionRange);
        ShroudRevealToAllRange = RuleValue.Float(rules.GetString("ShroudRevealToAllRange"));
        ShroudRange = RuleValue.Float(rules.GetString("ShroudRange"));
        DisplayName = rules.GetString("DisplayName", name);
        CommandSet = rules.GetString("CommandSet");
        ButtonImage = rules.GetString("ButtonImage");
        SelectPortrait = rules.GetString("SelectPortrait");
        GeometryName = rules.GetString("Geometry", "SPHERE");
        Geometry = Core.Geometry.FromRules(rules);
        GeometryMajorRadius = RuleValue.Float(rules.GetString("GeometryMajorRadius"), 1);
        GeometryMinorRadius = RuleValue.Float(rules.GetString("GeometryMinorRadius"), GeometryMajorRadius);
        GeometryHeight = RuleValue.Float(rules.GetString("GeometryHeight"), 1);
        IsBuildable = RuleValue.Bool(rules.GetString("Buildable"), true);
        EnergyProduction = RuleValue.Int(rules.GetString("EnergyProduction"));
        EnergyBonus = RuleValue.Int(rules.GetString("EnergyBonus"));
        CrusherLevel = RuleValue.Int(rules.GetString("CrusherLevel"));
        CrushableLevel = RuleValue.Int(rules.GetString("CrushableLevel"), 255);
        PlacementViewAngle = RuleValue.Float(rules.GetString("PlacementViewAngle"), 0) * MathF.PI / 180f;
        BuildVariations = RuleValue.Words(rules.GetString("BuildVariations"));

        var modules = new List<ModuleDefinition>();
        if (rules.TryGet("Modules", out var moduleMap) && moduleMap.IsMapping)
            foreach (var (tag, module) in moduleMap.Entries)
                if (module.IsMapping)
                    modules.Add(new ModuleDefinition(tag, module.GetString("Module"), module.GetString("Class"), module));
        Modules = modules;
    }

    public bool Is(string kind) => KindOf.Contains(kind);

    /// <summary>The angle a building is placed at by default (radians).</summary>
    public float PlacementViewAngle { get; }

    /// <summary>Templates built in place of this one at random (e.g. civilian building variants).</summary>
    public string[] BuildVariations { get; }

    /// <summary>The same object, or a reskin of it (the original's isEquivalentTo).</summary>
    public bool IsEquivalentTo(ObjectTemplate other) =>
        Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase)
        || Rules.GetString("ReskinOf").Equals(other.Name, StringComparison.OrdinalIgnoreCase)
        || other.Rules.GetString("ReskinOf").Equals(Name, StringComparison.OrdinalIgnoreCase);

    public IEnumerable<ModuleDefinition> ModulesOfType(string moduleType) =>
        Modules.Where(m => m.ModuleType.Equals(moduleType, StringComparison.OrdinalIgnoreCase));

    public ModuleDefinition? FindModule(string className) =>
        Modules.FirstOrDefault(m => m.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name;
}

/// <summary>One module of an object: e.g. tag "ModuleTag_03", type "Behavior", class "AIUpdateInterface".</summary>
public sealed record ModuleDefinition(string Tag, string ModuleType, string ClassName, RuleNode Settings)
{
    public string Get(string key, string fallback = "") => Settings.GetString(key, fallback);
    public float GetFloat(string key, float fallback = 0) => RuleValue.Float(Settings.GetString(key), fallback);
    public int GetInt(string key, int fallback = 0) => RuleValue.Int(Settings.GetString(key), fallback);
    public bool GetBool(string key, bool fallback = false) => RuleValue.Bool(Settings.GetString(key), fallback);
}

using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Art;

/// <summary>How an animation in a condition state plays (original AnimationMode values).</summary>
public enum AnimationMode { Loop, Once, OnceBackwards, LoopBackwards, Manual, LoopPingPong }

/// <summary>One animation choice: "Animation: Skeleton.AnimName [distance-per-cycle]".</summary>
public sealed record AnimationChoice(string Name, float DistanceCovered);

/// <summary>
/// One condition state of a W3D draw module: which model, animation, turret bones and sub-objects
/// to use while the object's model conditions match <see cref="Conditions"/>.
/// </summary>
public sealed class ModelConditionState
{
    /// <summary>The flag combinations this state matches (its own plus any AliasConditionState).</summary>
    public List<FlagSet> Conditions { get; } = new();

    public string Name { get; set; } = "";
    public string? Model { get; set; }
    public List<AnimationChoice> Animations { get; set; } = new();
    public List<AnimationChoice> IdleAnimations { get; set; } = new();
    public AnimationMode AnimationMode { get; set; } = AnimationMode.Loop;
    public HashSet<string> Flags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Turret { get; set; }
    public string? TurretPitch { get; set; }
    public string? AltTurret { get; set; }
    public string? AltTurretPitch { get; set; }
    public float TurretArtAngle { get; set; }
    public List<string> HideSubObjects { get; set; } = new();
    public List<string> ShowSubObjects { get; set; } = new();

    /// <summary>Per weapon slot (PRIMARY, SECONDARY, TERTIARY): the muzzle flash sub-object, recoil bone, fire bone.</summary>
    public Dictionary<string, string> MuzzleFlash { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> RecoilBone { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FireFxBone { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per weapon slot: the bone projectiles leave from (numbered per barrel: WeaponA01, WeaponA02, ...).</summary>
    public Dictionary<string, string> LaunchBone { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public float TurretArtPitch { get; set; }

    /// <summary>"ParticleSysBone: BoneName ParticleSystem" pairs (smoke stacks, wakes, ...).</summary>
    public List<(string Bone, string ParticleSystem)> ParticleBones { get; set; } = new();

    public (float Min, float Max) AnimationSpeedRange { get; set; } = (1, 1);

    public ModelConditionState Clone() => new()
    {
        Name = Name,
        Model = Model,
        Animations = Animations.ToList(),
        IdleAnimations = IdleAnimations.ToList(),
        AnimationMode = AnimationMode,
        Flags = new HashSet<string>(Flags, StringComparer.OrdinalIgnoreCase),
        Turret = Turret,
        TurretPitch = TurretPitch,
        AltTurret = AltTurret,
        AltTurretPitch = AltTurretPitch,
        TurretArtAngle = TurretArtAngle,
        HideSubObjects = HideSubObjects.ToList(),
        ShowSubObjects = ShowSubObjects.ToList(),
        MuzzleFlash = new(MuzzleFlash, StringComparer.OrdinalIgnoreCase),
        RecoilBone = new(RecoilBone, StringComparer.OrdinalIgnoreCase),
        FireFxBone = new(FireFxBone, StringComparer.OrdinalIgnoreCase),
        LaunchBone = new(LaunchBone, StringComparer.OrdinalIgnoreCase),
        ParticleBones = ParticleBones.ToList(),
        AnimationSpeedRange = AnimationSpeedRange,
    };
}

/// <summary>
/// The parsed settings of one W3D draw module (W3DModelDraw, W3DTankDraw, W3DTreeDraw, ...).
/// Follows the original rules: every ConditionState starts as a copy of the DefaultConditionState
/// and overrides what it lists; the state that best matches the object's conditions is shown.
/// </summary>
public sealed class ModelDrawData
{
    public string ClassName { get; }
    public string Tag { get; }

    /// <summary>The draw module's own settings (tread, wheel and dust settings for tank/truck draws).</summary>
    public RuleNode Settings { get; }
    public List<ModelConditionState> States { get; } = new();
    public FlagSet IgnoreConditions { get; }
    public bool OkToChangeModelColor { get; }

    /// <summary>For trees: the model and texture.</summary>
    public string? TreeModel { get; }

    // Shared by every game in the process (tests and AI run games side by side).
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Template, string Tag), ModelDrawData> cache = new();

    public static ModelDrawData For(ObjectTemplate template, ModuleDefinition module)
    {
        var key = (template.Name, module.Tag);
        return cache.GetOrAdd(key, _ => new ModelDrawData(module));
    }

    ModelDrawData(ModuleDefinition module)
    {
        ClassName = module.ClassName;
        Tag = module.Tag;
        var settings = module.Settings;
        Settings = settings;
        OkToChangeModelColor = RuleValue.Bool(settings.GetString("OkToChangeModelColor"));
        IgnoreConditions = Logic.Core.Flags.ModelCondition.Parse(settings.GetString("IgnoreConditionStates"));

        if (ClassName.Equals("W3DTreeDraw", StringComparison.OrdinalIgnoreCase))
        {
            TreeModel = settings.GetString("ModelName");
            var tree = new ModelConditionState { Model = TreeModel, Name = "TREE" };
            tree.Conditions.Add(FlagSet.Empty);
            States.Add(tree);
            return;
        }

        ModelConditionState? defaultState = null;
        if (settings.TryGet("DefaultConditionState", out var defaultNode))
        {
            defaultState = new ModelConditionState { Name = "DEFAULT" };
            Apply(defaultState, defaultNode.IsSequence ? defaultNode.Items[0] : defaultNode);
            defaultState.Conditions.Add(FlagSet.Empty);
            States.Add(defaultState);
        }

        if (settings.TryGet("ConditionState", out var states))
        {
            if (states.IsMapping)
            {
                // Keyed by the condition names: "REALLYDAMAGED RUBBLE: { ... }".
                foreach (var (header, node) in states.Entries)
                    AddState(header, node, defaultState);
            }
            else
            {
                foreach (var node in states.AsList())
                    AddState(node.GetString("Header", node.GetString("ConditionState")), node, defaultState);
            }
        }
    }

    void AddState(string header, RuleNode node, ModelConditionState? defaultState)
    {
        var state = defaultState?.Clone() ?? new ModelConditionState();
        state.Name = header;
        state.Conditions.Clear();
        state.Conditions.Add(Logic.Core.Flags.ModelCondition.Parse(header));
        foreach (var alias in node["Aliases"]?.AsList() ?? [])
            state.Conditions.Add(Logic.Core.Flags.ModelCondition.Parse(alias.Value));
        Apply(state, node);
        States.Add(state);
    }

    static void Apply(ModelConditionState state, RuleNode node)
    {
        if (node.Has("Model"))
            state.Model = Usable(node.GetString("Model"));
        if (node.TryGet("Animation", out var animations))
            state.Animations = ParseAnimations(animations);
        if (node.TryGet("IdleAnimation", out var idle))
            state.IdleAnimations = ParseAnimations(idle);
        if (node.Has("AnimationMode"))
            state.AnimationMode = node.GetString("AnimationMode").ToUpperInvariant() switch
            {
                "ONCE" => AnimationMode.Once,
                "ONCE_BACKWARDS" => AnimationMode.OnceBackwards,
                "LOOP_BACKWARDS" => AnimationMode.LoopBackwards,
                "MANUAL" => AnimationMode.Manual,
                "LOOP_PINGPONG" => AnimationMode.LoopPingPong,
                _ => AnimationMode.Loop,
            };
        if (node.Has("Flags"))
            state.Flags = new HashSet<string>(RuleValue.Words(node.GetString("Flags")), StringComparer.OrdinalIgnoreCase);
        if (node.Has("Turret")) state.Turret = Usable(node.GetString("Turret"));
        if (node.Has("TurretPitch")) state.TurretPitch = Usable(node.GetString("TurretPitch"));
        if (node.Has("AltTurret")) state.AltTurret = Usable(node.GetString("AltTurret"));
        if (node.Has("AltTurretPitch")) state.AltTurretPitch = Usable(node.GetString("AltTurretPitch"));
        if (node.Has("TurretArtAngle")) state.TurretArtAngle = RuleUnits.Angle(node.GetString("TurretArtAngle"));
        if (node.TryGet("HideSubObject", out var hide))
            state.HideSubObjects = hide.AsList().SelectMany(n => RuleValue.Words(n.Value)).ToList();
        if (node.TryGet("ShowSubObject", out var show))
            state.ShowSubObjects = show.AsList().SelectMany(n => RuleValue.Words(n.Value)).ToList();
        ReadPerSlot(node, "WeaponMuzzleFlash", state.MuzzleFlash);
        ReadPerSlot(node, "WeaponRecoilBone", state.RecoilBone);
        ReadPerSlot(node, "WeaponFireFXBone", state.FireFxBone);
        ReadPerSlot(node, "WeaponLaunchBone", state.LaunchBone);
        if (node.Has("TurretArtPitch")) state.TurretArtPitch = RuleUnits.Angle(node.GetString("TurretArtPitch"));
        if (node.TryGet("ParticleSysBone", out var particles))
        {
            state.ParticleBones = particles.AsList()
                .Select(n => RuleValue.Words(n.Value)).Where(w => w.Length >= 2)
                .Select(w => (w[0], w[1])).ToList();
        }
        if (node.Has("AnimationSpeedFactorRange"))
        {
            string[] range = RuleValue.Words(node.GetString("AnimationSpeedFactorRange"));
            if (range.Length >= 2)
                state.AnimationSpeedRange = (RuleValue.Float(range[0], 1), RuleValue.Float(range[1], 1));
        }
    }

    static void ReadPerSlot(RuleNode node, string key, Dictionary<string, string> target)
    {
        if (!node.TryGet(key, out var entries))
            return;
        target.Clear();
        foreach (var entry in entries.AsList())
        {
            string[] words = RuleValue.Words(entry.Value);
            if (words.Length >= 2)
                target[words[0]] = words[1];
        }
    }

    static List<AnimationChoice> ParseAnimations(RuleNode node) =>
        node.AsList()
            .Select(n => RuleValue.Words(n.Value))
            .Where(w => w.Length >= 1 && !w[0].Equals("NONE", StringComparison.OrdinalIgnoreCase))
            .Select(w => new AnimationChoice(w[0], w.Length > 1 ? RuleValue.Float(w[1]) : 0))
            .ToList();

    static string? Usable(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? null : value;

    /// <summary>The state to show for an object's current model conditions.</summary>
    public ModelConditionState? StateFor(FlagSet conditions)
    {
        var relevant = conditions & ~IgnoreConditions;
        return SparseMatch.Best(States, s => s.Conditions, relevant) ?? States.FirstOrDefault();
    }
}

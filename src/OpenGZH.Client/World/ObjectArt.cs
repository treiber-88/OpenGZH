using OpenGZH.Core.Rules;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Client.World;

/// <summary>
/// Reads an object's art settings from its Draw modules: which W3D model it shows in each
/// model condition state, and which sub-objects are hidden.
/// </summary>
public static class ObjectArt
{
    /// <summary>Draw module classes that show a W3D model through condition states.</summary>
    static readonly HashSet<string> ModelDrawClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "W3DModelDraw", "W3DTankDraw", "W3DTruckDraw", "W3DOverlordTankDraw", "W3DOverlordTruckDraw",
        "W3DOverlordAircraftDraw", "W3DPoliceCarDraw", "W3DSupplyDraw", "W3DDependencyModelDraw",
        "W3DScienceModelDraw", "W3DTankTruckDraw", "W3DProjectileStreamDraw", "W3DPropDraw", "W3DLaserDraw",
    };

    /// <summary>The draw module that defines the object's main model (the first model-based one).</summary>
    public static ModuleDefinition? MainDraw(ObjectTemplate template) =>
        template.ModulesOfType("Draw").FirstOrDefault(m => ModelDrawClasses.Contains(m.ClassName) || m.ClassName.Equals("W3DTreeDraw", StringComparison.OrdinalIgnoreCase));

    /// <summary>The model shown when the object is in its normal (no special condition) state.</summary>
    public static string? DefaultModel(ObjectTemplate template)
    {
        var draw = MainDraw(template);
        if (draw == null)
            return null;

        if (draw.ClassName.Equals("W3DTreeDraw", StringComparison.OrdinalIgnoreCase))
            return Usable(draw.Get("ModelName"));

        var state = DefaultConditionState(draw.Settings);
        return Usable(state?.GetString("Model"));
    }

    /// <summary>
    /// The default condition state of a draw module: "DefaultConditionState" if present, otherwise the
    /// condition state with no conditions ("NONE"), otherwise the first one.
    /// </summary>
    public static RuleNode? DefaultConditionState(RuleNode draw)
    {
        if (draw.TryGet("DefaultConditionState", out var defaultState))
            return defaultState.IsSequence ? defaultState.Items.FirstOrDefault() : defaultState;

        if (!draw.TryGet("ConditionState", out var states))
            return null;
        if (states.IsMapping)
            return states["NONE"] ?? states.Entries.FirstOrDefault().Value;
        return states.IsSequence ? states.Items.FirstOrDefault() : states;
    }

    static string? Usable(string? model) =>
        string.IsNullOrWhiteSpace(model) || model.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? null : model;
}

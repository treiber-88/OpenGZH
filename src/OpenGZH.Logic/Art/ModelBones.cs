using System.Numerics;
using OpenGZH.Formats.W3d;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Art;

/// <summary>
/// Bone positions the game logic needs from the art: where passengers leave a transport
/// (ExitStart/ExitEnd), where garrisoned soldiers stand (FIREPOINTxx), where a hacker's beam starts.
/// Like the original's "pristine bone positions", these come from each model's rest pose, for the
/// model shown in the object's current condition state, so the logic stays deterministic.
///
/// Without art (some headless tests) every bone is missing and callers fall back to the object's position.
/// </summary>
public sealed class ModelBones
{
    readonly W3dLibrary? library;
    readonly Dictionary<string, Dictionary<string, Matrix4x4>> bonesByModel = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string Template, string Tag), ModelDrawData> drawData = new();

    public ModelBones(W3dLibrary? library) => this.library = library;

    public bool HasArt => library != null;

    /// <summary>The model the object shows now (first W3D draw module with a model).</summary>
    public string? CurrentModel(GameObject obj)
    {
        foreach (var module in obj.Template.ModulesOfType("Draw"))
        {
            if (!module.ClassName.StartsWith("W3D", StringComparison.OrdinalIgnoreCase) || !module.ClassName.EndsWith("Draw", StringComparison.OrdinalIgnoreCase))
                continue;
            var key = (obj.Template.Name, module.Tag);
            if (!drawData.TryGetValue(key, out var data))
                drawData[key] = data = ModelDrawData.For(obj.Template, module);
            string? model = obj.ModelOverride ?? data.StateFor(obj.ModelConditions)?.Model;
            if (model != null)
                return model;
        }
        return null;
    }

    /// <summary>Rest-pose transforms of every bone of a model, relative to the model's origin. Names are upper case.</summary>
    Dictionary<string, Matrix4x4> BonesOf(string model)
    {
        if (bonesByModel.TryGetValue(model, out var bones))
            return bones;
        bones = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        bonesByModel[model] = bones;
        var file = library?.GetFile(model);
        var hierarchy = file?.Hierarchy ?? (file?.HLod != null ? library!.GetHierarchy(file.HLod.HierarchyName) : null);
        if (hierarchy == null)
            return bones;

        var world = new Matrix4x4[hierarchy.Pivots.Count];
        for (int i = 0; i < hierarchy.Pivots.Count; i++)
        {
            var pivot = hierarchy.Pivots[i];
            // Pivot 0 is the root; children are placed relative to their parent.
            world[i] = i == 0 ? Matrix4x4.Identity
                : pivot.BaseTransform * (pivot.ParentIndex >= 0 && pivot.ParentIndex < i ? world[pivot.ParentIndex] : Matrix4x4.Identity);
            bones.TryAdd(pivot.Name, world[i]);
        }
        return bones;
    }

    /// <summary>The object's transform in the world (position and facing; buildings don't tilt).</summary>
    static Matrix4x4 ObjectTransform(GameObject obj) =>
        Matrix4x4.CreateRotationY(-obj.Pitch) * Matrix4x4.CreateRotationX(obj.Roll)
        * Matrix4x4.CreateRotationZ(obj.Orientation) * Matrix4x4.CreateTranslation(obj.Position);

    /// <summary>A bone's world transform, if the object's current model has it.</summary>
    public bool TryGetBone(GameObject obj, string bone, out Matrix4x4 world)
    {
        world = default;
        if (CurrentModel(obj) is not { } model || !BonesOf(model).TryGetValue(bone, out var local))
            return false;
        world = local * ObjectTransform(obj);
        return true;
    }

    /// <summary>A bone's world position, or the object's own position if the bone is missing.</summary>
    public Vector3 BonePosition(GameObject obj, string bone) =>
        TryGetBone(obj, bone, out var world) ? world.Translation : obj.Position;

    /// <summary>Numbered bones "PREFIX01", "PREFIX02", ... up to <paramref name="max"/>, stopping at the first gap (the original's multi-bone lookup).</summary>
    public List<Matrix4x4> NumberedBones(GameObject obj, string prefix, int max)
    {
        var result = new List<Matrix4x4>();
        for (int i = 1; i <= max; i++)
        {
            if (!TryGetBone(obj, $"{prefix}{i:00}", out var world))
                break;
            result.Add(world);
        }
        return result;
    }
}

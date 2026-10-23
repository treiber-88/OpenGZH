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
    readonly Dictionary<(string Model, string Animation, bool FinalFrame), Dictionary<string, Matrix4x4>> bonesByPose = new();
    readonly Dictionary<(string Template, string Tag), ModelDrawData> drawData = new();

    public ModelBones(W3dLibrary? library) => this.library = library;

    public bool HasArt => library != null;

    /// <summary>The model library (bridge geometry is measured from it).</summary>
    public W3dLibrary? Library => library;

    /// <summary>The condition state the object's model shows now (first W3D draw module with a model).</summary>
    public ModelConditionState? CurrentState(GameObject obj)
    {
        foreach (var module in obj.Template.ModulesOfType("Draw"))
        {
            if (!module.ClassName.StartsWith("W3D", StringComparison.OrdinalIgnoreCase) || !module.ClassName.EndsWith("Draw", StringComparison.OrdinalIgnoreCase))
                continue;
            var key = (obj.Template.Name, module.Tag);
            if (!drawData.TryGetValue(key, out var data))
                drawData[key] = data = ModelDrawData.For(obj.Template, module);
            if (data.StateFor(obj.ModelConditions) is { } state && (obj.ModelOverride ?? state.Model) != null)
                return state;
        }
        return null;
    }

    readonly Dictionary<(string Model, ModelConditionState State, Combat.WeaponSlot Slot), List<Matrix4x4>> barrelsByState = new();

    /// <summary>
    /// The weapon barrels of a slot in the current state, as projectile launch transforms relative to
    /// the model (the original's validateWeaponBarrelInfo): numbered bones of the launch, fire-FX, recoil
    /// and muzzle-flash names, stopping at the first number with none of them; failing that, the
    /// unnumbered bones. A barrel without a launch bone launches from the model's origin.
    /// </summary>
    public List<Matrix4x4> Barrels(GameObject obj, Combat.WeaponSlot slot)
    {
        if (CurrentState(obj) is not { } state || (obj.ModelOverride ?? state.Model) is not { } model)
            return [];
        if (barrelsByState.TryGetValue((model, state, slot), out var cached))
            return cached;
        var bones = PristineBones(model, state);
        string slotName = slot.ToString().ToUpperInvariant();
        string? Name(Dictionary<string, string> names) => names.TryGetValue(slotName, out var n) && n.Length > 0 ? n : null;
        string? launch = Name(state.LaunchBone), fx = Name(state.FireFxBone), recoil = Name(state.RecoilBone), muzzle = Name(state.MuzzleFlash);
        bool Has(string? name, string suffix) => name != null && bones.ContainsKey(name + suffix);
        var result = new List<Matrix4x4>();
        if (launch != null || fx != null || recoil != null || muzzle != null)
        {
            for (int i = 1; i <= 99; i++)
            {
                string n = $"{i:00}";
                bool hasLaunch = Has(launch, n);
                if (!hasLaunch && !Has(fx, n) && !Has(recoil, n) && !Has(muzzle, n))
                    break;
                result.Add(hasLaunch ? bones[launch + n] : Matrix4x4.Identity);
            }
            if (result.Count == 0)
            {
                bool hasLaunch = Has(launch, "");
                if (hasLaunch || Has(fx, "") || Has(recoil, "") || Has(muzzle, ""))
                    result.Add(hasLaunch ? bones[launch!] : Matrix4x4.Identity);
            }
        }
        barrelsByState[(model, state, slot)] = result;
        return result;
    }

    /// <summary>
    /// Where a projectile leaves from, in the world (the original's calcProjectileLaunchPosition):
    /// the barrel's launch bone, turned with the turret that aims the slot.
    /// Returns false when the model has no barrels for the slot.
    /// </summary>
    public bool TryGetLaunchTransform(GameObject obj, Combat.WeaponSlot slot, int barrel, Modules.AI.Turret? turret, out Matrix4x4 world)
    {
        world = ObjectTransform(obj);
        var barrels = Barrels(obj, slot);
        if (barrels.Count == 0)
            return false;
        var launch = barrels[barrel >= 0 && barrel < barrels.Count ? barrel : 0];
        if (turret != null && CurrentState(obj) is { } state && (obj.ModelOverride ?? state.Model) is { } model)
        {
            var bones = PristineBones(model, state);
            string? turnBone = turret.IsAlt ? state.AltTurret : state.Turret;
            string? pitchBone = turret.IsAlt ? state.AltTurretPitch : state.TurretPitch;
            var turnPivot = turnBone != null && bones.TryGetValue(turnBone, out var t) ? t.Translation : Vector3.Zero;
            var pitchPivot = pitchBone != null && bones.TryGetValue(pitchBone, out var p) ? p.Translation : Vector3.Zero;
            launch = launch * Matrix4x4.CreateRotationZ(state.TurretArtAngle) * Matrix4x4.CreateRotationY(-state.TurretArtPitch);
            var pitchAdjust = Matrix4x4.CreateTranslation(-pitchPivot) * Matrix4x4.CreateRotationY(-turret.Pitch) * Matrix4x4.CreateTranslation(pitchPivot);
            var turnAdjust = Matrix4x4.CreateTranslation(-turnPivot) * Matrix4x4.CreateRotationZ(turret.Angle) * Matrix4x4.CreateTranslation(turnPivot);
            launch = launch * pitchAdjust * turnAdjust;
        }
        world = launch * ObjectTransform(obj);
        return true;
    }

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

    /// <summary>
    /// The "pristine" bone transforms of a model in a condition state, relative to the model's origin:
    /// the pose at the first frame of the state's first animation (the last frame with
    /// PRISTINE_BONE_POS_IN_FINAL_FRAME), or the rest pose without an animation - as the original's
    /// validateCachedBones. Names are case-insensitive.
    /// </summary>
    Dictionary<string, Matrix4x4> PristineBones(string model, ModelConditionState? state)
    {
        string animation = state?.Animations.FirstOrDefault()?.Name ?? "";
        bool finalFrame = state?.Flags.Contains("PRISTINE_BONE_POS_IN_FINAL_FRAME") == true;
        if (bonesByPose.TryGetValue((model, animation, finalFrame), out var bones))
            return bones;
        bones = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        bonesByPose[(model, animation, finalFrame)] = bones;
        var file = library?.GetFile(model);
        var hierarchy = file?.Hierarchy ?? (file?.HLod != null ? library!.GetHierarchy(file.HLod.HierarchyName) : null);
        if (hierarchy == null)
            return bones;

        var anim = animation.Length > 0 ? library!.GetAnimation(animation) : null;
        var pose = new W3dPose(hierarchy.Pivots.Count);
        pose.Update(hierarchy, Matrix4x4.Identity, anim, anim == null ? 0 : finalFrame ? Math.Max(0, anim.FrameCount - 1) : 0);
        for (int i = 0; i < hierarchy.Pivots.Count && i < pose.Transforms.Length; i++)
            bones.TryAdd(hierarchy.Pivots[i].Name, pose.Transforms[i]);
        return bones;
    }

    /// <summary>The object's transform in the world (position and facing; buildings don't tilt).</summary>
    public static Matrix4x4 ObjectTransform(GameObject obj) =>
        Matrix4x4.CreateFromQuaternion(obj.Tilt) * Matrix4x4.CreateRotationY(-obj.Pitch) * Matrix4x4.CreateRotationX(obj.Roll)
        * Matrix4x4.CreateRotationZ(obj.Orientation) * Matrix4x4.CreateTranslation(obj.Position);

    /// <summary>A bone's world transform, if the object's current model has it.</summary>
    public bool TryGetBone(GameObject obj, string bone, out Matrix4x4 world)
    {
        world = default;
        var state = CurrentState(obj);
        string? model = state == null ? null : obj.ModelOverride ?? state.Model;
        if (model == null || !PristineBones(model, state).TryGetValue(bone, out var local))
            return false;
        world = local * ObjectTransform(obj);
        return true;
    }

    /// <summary>A bone's position relative to the object (the original's getPristineBonePositions), if the current model has it.</summary>
    public bool TryGetLocalBone(GameObject obj, string bone, out Vector3 local)
    {
        local = default;
        var state = CurrentState(obj);
        string? model = state == null ? null : obj.ModelOverride ?? state.Model;
        if (model == null || !PristineBones(model, state).TryGetValue(bone, out var transform))
            return false;
        local = transform.Translation;
        return true;
    }

    /// <summary>An object-relative point in the world (the original's convertBonePosToWorldPos).</summary>
    public static Vector3 ToWorld(GameObject obj, Vector3 local) => Vector3.Transform(local, ObjectTransform(obj));

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

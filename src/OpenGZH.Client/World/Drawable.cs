using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Formats.W3d;
using OpenGZH.Logic.Art;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Client.World;

/// <summary>
/// The visible side of one game object: one model per W3D draw module, kept in step with the
/// object's model conditions (damage, moving, attacking, night...), position, turret and weapons.
/// </summary>
public sealed class Drawable
{
    public GameObject Object { get; }
    public List<DrawPart> Parts { get; } = new();

    /// <summary>When each weapon slot last fired, in seconds of client time (for muzzle flashes and recoil).</summary>
    readonly float[] lastFireTime = [-10, -10, -10];
    float clock;

    const float MuzzleFlashSeconds = 0.08f;

    public Drawable(GameObject obj)
    {
        Object = obj;
        foreach (var module in obj.Template.ModulesOfType("Draw"))
        {
            if (module.ClassName is "W3DDefaultDraw" or "W3DLaserDraw" or "W3DTracerDraw" or "W3DRopeDraw" or "W3DProjectileStreamDraw")
                continue;
            Parts.Add(new DrawPart(ModelDrawData.For(obj.Template, module)));
        }
    }

    public void OnWeaponFired(int slot)
    {
        if (slot >= 0 && slot < lastFireTime.Length)
            lastFireTime[slot] = clock;
    }

    /// <summary>Brings every part up to date with the object; returns the instances to draw.</summary>
    public void Update(float seconds, ModelCache models)
    {
        clock += seconds;
        var world = WorldTransform();
        uint houseColor = Object.Owner.IsNeutral ? 0 : Object.Owner.Color;

        foreach (var part in Parts)
        {
            // Debris and similar objects choose their model at runtime.
            var state = part.Data.StateFor(Object.ModelConditions);
            string? modelName = Object.ModelOverride ?? state?.Model;
            if (state != part.State || modelName != part.ModelName)
                part.Switch(state, modelName, models, Object);
            else if (part.SubObjectsVersion != Object.SubObjectsVersion && part.Instance != null)
                part.ApplySubObjectOverrides(Object);

            if (part.Instance == null)
                continue;

            part.Instance.World = world;
            part.Instance.HouseColor = houseColor;
            ApplyTurrets(part);
            ApplyMuzzleFlashes(part);
            part.Advance(seconds, Object);
        }
    }

    Matrix4x4 WorldTransform()
    {
        var position = Object.Position;
        // Buildings rise out of the ground as they are built.
        if (Object.IsUnderConstruction)
            position.Z -= (1 - Object.ConstructionPercent / 100f) * Object.Height;

        return Matrix4x4.CreateRotationY(-Object.Pitch)
             * Matrix4x4.CreateRotationX(Object.Roll)
             * Matrix4x4.CreateRotationZ(Object.Orientation)
             * Matrix4x4.CreateTranslation(position);
    }

    void ApplyTurrets(DrawPart part)
    {
        var instance = part.Instance!;
        var state = part.State;
        var turrets = Object.AI?.Turrets;
        instance.Pose.Overrides.Clear();
        if (state == null || turrets == null || turrets.Count == 0)
            return;

        void Aim(string? yawBone, string? pitchBone, int turretIndex)
        {
            if (turretIndex >= turrets.Count)
                return;
            var turret = turrets[turretIndex];
            if (part.FindPivot(yawBone) is int yaw)
                instance.Pose.Overrides[yaw] = Matrix4x4.CreateRotationZ(turret.Angle - state.TurretArtAngle);
            if (part.FindPivot(pitchBone) is int pitch)
                instance.Pose.Overrides[pitch] = Matrix4x4.CreateRotationY(-turret.Pitch);
        }

        Aim(state.Turret, state.TurretPitch, 0);
        Aim(state.AltTurret, state.AltTurretPitch, 1);
    }

    void ApplyMuzzleFlashes(DrawPart part)
    {
        var instance = part.Instance!;
        var state = part.State;
        if (state == null)
            return;
        string[] slots = ["PRIMARY", "SECONDARY", "TERTIARY"];
        for (int slot = 0; slot < 3; slot++)
        {
            if (!state.MuzzleFlash.TryGetValue(slots[slot], out string? flash))
                continue;
            bool firing = clock - lastFireTime[slot] < MuzzleFlashSeconds;
            foreach (var mesh in part.FlashMeshes(flash))
            {
                if (firing)
                    instance.HiddenSubObjects.Remove(mesh);
                else
                    instance.HiddenSubObjects.Add(mesh);
            }
        }
    }
}

/// <summary>One draw module's model on one object.</summary>
public sealed class DrawPart
{
    public ModelDrawData Data { get; }
    public ModelConditionState? State { get; private set; }
    public string? ModelName { get; private set; }
    public ModelInstance? Instance { get; private set; }

    /// <summary>The object's <see cref="GameObject.SubObjectsVersion"/> when this part last applied its sub-object overrides.</summary>
    public int SubObjectsVersion { get; private set; } = -1;

    W3dLibrary? library;
    float animationSpeed = 1;
    bool playingIdle;
    readonly Dictionary<string, List<string>> flashMeshCache = new(StringComparer.OrdinalIgnoreCase);

    public DrawPart(ModelDrawData data) => Data = data;

    internal void Switch(ModelConditionState? state, string? modelName, ModelCache models, GameObject obj)
    {
        State = state;
        library = models.Library;
        if (modelName != ModelName || Instance == null)
        {
            ModelName = modelName;
            var model = models.Get(modelName);
            Instance = model == null ? null : new ModelInstance(model);
            flashMeshCache.Clear();
        }
        if (Instance == null || state == null)
            return;

        Instance.HiddenSubObjects.Clear();
        Instance.ShownSubObjects.Clear();
        foreach (string name in state.HideSubObjects)
            Instance.HiddenSubObjects.Add(name);
        foreach (string name in state.ShowSubObjects)
            Instance.ShownSubObjects.Add(name);
        ApplySubObjectOverrides(obj);

        float speedMin = state.AnimationSpeedRange.Min, speedMax = state.AnimationSpeedRange.Max;
        animationSpeed = speedMin + (float)Random.Shared.NextDouble() * (speedMax - speedMin);
        StartAnimation(state, obj);
    }

    /// <summary>Sub-objects the logic shows or hides regardless of model condition (SubObjectsUpgrade).</summary>
    internal void ApplySubObjectOverrides(GameObject obj)
    {
        SubObjectsVersion = obj.SubObjectsVersion;
        foreach (var (name, shown) in obj.SubObjectOverrides)
        {
            if (shown)
            {
                Instance!.HiddenSubObjects.Remove(name);
                Instance.ShownSubObjects.Add(name);
            }
            else
            {
                Instance!.ShownSubObjects.Remove(name);
                Instance.HiddenSubObjects.Add(name);
            }
        }
    }

    void StartAnimation(ModelConditionState state, GameObject obj)
    {
        var choices = state.Animations.Count > 0 ? state.Animations : state.IdleAnimations;
        playingIdle = state.Animations.Count == 0;
        if (choices.Count == 0 || library == null)
        {
            Instance!.Animation = null;
            return;
        }

        // The original picks one of the listed animations at random (cosmetic, so not the game's random).
        var choice = choices[Random.Shared.Next(choices.Count)];
        Instance!.Animation = library.GetAnimation(choice.Name);
        Instance.Loop = state.AnimationMode is AnimationMode.Loop or AnimationMode.LoopBackwards or AnimationMode.LoopPingPong;
        int frames = Instance.Animation?.FrameCount ?? 1;
        Instance.Frame = state.Flags.Contains("START_FRAME_LAST") ? frames - 1
            : state.Flags.Contains("RANDOMSTART") ? Random.Shared.Next(Math.Max(1, frames))
            : 0;
    }

    internal void Advance(float seconds, GameObject obj)
    {
        if (Instance == null)
            return;
        if (State?.AnimationMode is AnimationMode.OnceBackwards or AnimationMode.LoopBackwards && Instance.Animation != null)
        {
            Instance.Frame = Math.Max(0, Instance.Frame - seconds * Instance.Animation.FrameRate * animationSpeed);
            if (Instance.Frame <= 0 && Instance.Loop)
                Instance.Frame = Instance.Animation.FrameCount - 1;
            Instance.UpdatePose();
        }
        else
        {
            Instance.Advance(seconds * animationSpeed);
        }

        // Idle animations: pick another one when the current one finishes.
        if (playingIdle && State != null && State.IdleAnimations.Count > 1 && Instance.Animation != null
            && Instance.Frame >= Instance.Animation.FrameCount - 1.5f)
            StartAnimation(State, obj);
    }

    public int? FindPivot(string? boneName)
    {
        if (boneName == null || Instance?.Model.Hierarchy == null)
            return null;
        int index = Instance.Model.Hierarchy.FindPivot(boneName);
        return index >= 0 ? index : null;
    }

    /// <summary>Meshes making up a muzzle flash: those hanging from bones named like the flash ("TurretFX", "TurretFX01"...).</summary>
    public IReadOnlyList<string> FlashMeshes(string flashName)
    {
        if (flashMeshCache.TryGetValue(flashName, out var cached))
            return cached;
        var result = new List<string>();
        if (Instance != null)
        {
            var hierarchy = Instance.Model.Hierarchy;
            foreach (var mesh in Instance.Model.Meshes)
            {
                string bone = hierarchy != null && mesh.BoneIndex < hierarchy.Pivots.Count ? hierarchy.Pivots[mesh.BoneIndex].Name : "";
                if (bone.StartsWith(flashName, StringComparison.OrdinalIgnoreCase) || mesh.Name.StartsWith(flashName, StringComparison.OrdinalIgnoreCase))
                    result.Add(mesh.Name);
            }
        }
        flashMeshCache[flashName] = result;
        return result;
    }
}

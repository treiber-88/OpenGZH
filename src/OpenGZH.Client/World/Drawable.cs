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

    /// <summary>Vehicle body motion on springs (the original's client physics transform).</summary>
    readonly ClientPhysics clientPhysics = new();

    /// <summary>Finds another object's drawable (an Overlord add-on rides its carrier's bone).</summary>
    public Func<GameObject, Drawable?>? FindDrawable { get; set; }

    /// <summary>The player watching (science-only models show only to players with the science).</summary>
    public Func<Logic.Players.Player?>? LocalPlayer { get; set; }

    /// <summary>The transform used last frame.</summary>
    public Matrix4x4 LastWorld { get; private set; } = Matrix4x4.Identity;

    // Tints (the original's color tint and selection flash envelopes), stepped 30 times a second.
    readonly TintEnvelope tintEnvelope = new(), selectionFlash = new();
    Logic.Objects.TintStatus prevTintStatus;
    Vector3? prevDrawColorTint;
    float tintStepTime;

    static readonly Vector3 DarkGrayDisabledColor = new(-0.5f, -0.5f, -0.5f);
    static readonly Vector3 SubdualDamageColor = new(-0.2f, -0.2f, 0.8f);
    static readonly Vector3 FrenzyColor = new(0.2f, -0.2f, -0.2f);
    static readonly Vector3 FrenzyColorInfantry = new(0, -0.7f, -0.7f);

    /// <summary>A short flash when selected (white, darkened by SelectionFlashSaturationFactor).</summary>
    public void FlashAsSelected(float saturation = 0.5f)
    {
        float c = 1 * saturation - saturation * 0.5f;
        selectionFlash.Play(new Vector3(c, c, c), 0, 4);
    }

    /// <summary>Starts or ends tints when the object's tint status changes, then steps the envelopes.</summary>
    Vector3 UpdateTint(float seconds)
    {
        var status = Object.Tint;
        if (status != prevTintStatus)
        {
            if (status.HasFlag(Logic.Objects.TintStatus.Disabled))
                tintEnvelope.Play(DarkGrayDisabledColor, 30, 30, TintEnvelope.SustainIndefinitely);
            else if (status.HasFlag(Logic.Objects.TintStatus.GainingSubdualDamage))
                tintEnvelope.Play(SubdualDamageColor, 150, 150, TintEnvelope.SustainIndefinitely);
            else if (status.HasFlag(Logic.Objects.TintStatus.Frenzy))
                tintEnvelope.Play(Object.IsKindOf("INFANTRY") ? FrenzyColorInfantry : FrenzyColor, 30, 30, TintEnvelope.SustainIndefinitely);
            else
                tintEnvelope.Release();
            prevTintStatus = status;
        }
        // A color the logic sets directly (an EMP pulse) holds while it is set.
        if (Object.DrawColorTint != prevDrawColorTint)
        {
            if (Object.DrawColorTint is { } color)
                tintEnvelope.Play(color, 0, 0, TintEnvelope.SustainIndefinitely);
            else
                tintEnvelope.Release();
            prevDrawColorTint = Object.DrawColorTint;
        }
        tintStepTime += seconds;
        for (int i = 0; tintStepTime >= 1f / 30 && i < 4; i++)
        {
            tintStepTime -= 1f / 30;
            tintEnvelope.Update();
            selectionFlash.Update();
        }
        if (tintStepTime > 1f / 30)
            tintStepTime = 0;
        var tint = Vector3.Zero;
        if (tintEnvelope.IsEffective)
            tint += tintEnvelope.Color;
        if (selectionFlash.IsEffective)
            tint += selectionFlash.Color;
        return tint;
    }

    // W3DSupplyDraw: boxes currently hidden; W3DPoliceCarDraw: light bar frame.
    readonly HashSet<string> supplyHidden = new(StringComparer.OrdinalIgnoreCase);
    float policeFrame = Random.Shared.NextSingle() * 10;

    /// <summary>A bone's current world transform in any of this drawable's models.</summary>
    public bool TryGetBoneWorld(string bone, out Matrix4x4 world)
    {
        foreach (var part in Parts)
        {
            if (part.Hidden || part.FindPivot(bone) is not int pivot)
                continue;
            world = part.Instance!.Pose.Transforms[Math.Min(pivot, part.Instance.Pose.Transforms.Length - 1)];
            return true;
        }
        world = default;
        return false;
    }

    /// <summary>
    /// W3DDependencyModelDraw: an add-on sitting on an open container (an Overlord's gattling cannon,
    /// bunker or propaganda tower) is drawn at the container's AttachToBoneInContainer bone, so it
    /// moves with the container's model, body motion included.
    /// </summary>
    bool TryAttachToContainer(out Matrix4x4 world)
    {
        world = default;
        if (Object.ContainedById == 0 || FindDrawable == null)
            return false;
        var container = Object.Game.FindObject(Object.ContainedById);
        if (container?.Contain is not { } contain || contain.IsEnclosingContainerFor(Object))
            return false;
        foreach (var part in Parts)
        {
            if (part.Data.ClassName != "W3DDependencyModelDraw" || part.Data.Settings.GetString("AttachToBoneInContainer") is not { Length: > 0 } bone)
                continue;
            if (FindDrawable(container) is not { } theirs)
                return false;
            world = theirs.TryGetBoneWorld(bone, out var boneWorld) ? boneWorld : theirs.LastWorld;
            return true;
        }
        return false;
    }

    readonly SwayClientUpdate? sway;
    readonly StealthLookState stealthLook = new();

    /// <summary>Hidden from the local player by stealth this frame.</summary>
    public bool HiddenByStealth => stealthLook.HiddenByStealth;

    /// <summary>GameData StealthFriendlyOpacity, set by the world view.</summary>
    public float StealthFriendlyOpacity { get; set; } = 0.5f;
    readonly BeaconClientUpdate? beacon;
    readonly Effects.ParticleSystemManager? particles;

    public Drawable(GameObject obj, Effects.ParticleSystemManager? particles = null)
    {
        Object = obj;
        this.particles = particles;
        foreach (var module in obj.Template.ModulesOfType("ClientUpdate"))
        {
            if (module.ClassName == "SwayClientUpdate")
                sway = new SwayClientUpdate();
            else if (module.ClassName == "BeaconClientUpdate")
                beacon = new BeaconClientUpdate(module.Settings);
        }
        foreach (var module in obj.Template.ModulesOfType("Draw"))
        {
            if (module.ClassName is "W3DDefaultDraw" or "W3DLaserDraw" or "W3DTracerDraw" or "W3DRopeDraw" or "W3DProjectileStreamDraw")
                continue;
            var part = new DrawPart(ModelDrawData.For(obj.Template, module));
            if (VehicleDraw.Handles(module.ClassName))
                part.Vehicle = new VehicleDraw(module.ClassName, module.Settings, particles);
            Parts.Add(part);
        }
    }

    /// <summary>Seconds since any of its weapons last fired (sprite art shows recoil and firing frames).</summary>
    public float SecondsSinceFired => clock - Math.Max(lastFireTime[0], Math.Max(lastFireTime[1], lastFireTime[2]));

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
        if (clientPhysics.Update(Object, seconds))
            world = clientPhysics.LocalTransform() * world;
        if (TryAttachToContainer(out var attached))
            world = attached;
        LastWorld = world;
        var tint = UpdateTint(seconds);
        stealthLook.Update(Object, LocalPlayer?.Invoke(), StealthFriendlyOpacity, seconds);
        bool hidden = Object.DrawableHidden || Object.IsHiddenInContainer;
        if (sway != null)
            world = sway.Update(Object, seconds, !hidden) * world;
        beacon?.Update(Object, particles, hidden);
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
            part.Instance.Tint = tint;
            part.Instance.Opacity = stealthLook.EffectiveOpacity;
            part.Instance.HeatVision = stealthLook.SecondMaterialPassOpacity;
            part.Instance.HeatVisionOnly = stealthLook.HeatVisionOnly;
            part.Instance.HouseColor = houseColor;
            ApplyTurrets(part);
            ApplyMuzzleFlashes(part);
            part.Vehicle?.Update(part, Object, clientPhysics, seconds, hidden);
            switch (part.Data.ClassName)
            {
                case "W3DScienceModelDraw":
                {
                    // Shown only to a player who has RequiredScience.
                    string science = part.Data.Settings.GetString("RequiredScience");
                    var viewer = LocalPlayer?.Invoke();
                    part.Hidden = science.Length == 0 || science.Equals("None", StringComparison.OrdinalIgnoreCase)
                        || viewer != null && !viewer.HasScience(science);
                    break;
                }
                case "W3DSupplyDraw":
                    ApplySupplyBoxes(part);
                    break;
                case "W3DPoliceCarDraw":
                    if (part.Instance.Animation is { } anim)
                    {
                        // The light bar runs at its own pace: a quarter frame per drawn frame.
                        policeFrame += 0.25f * seconds * 30;
                        if (policeFrame > anim.FrameCount - 1)
                            policeFrame = 0;
                        part.Instance.Frame = policeFrame;
                        part.Instance.UpdatePose();
                        continue;
                    }
                    break;
            }
            part.Advance(seconds, Object);
        }
    }

    Matrix4x4 WorldTransform()
    {
        var position = Object.Position;
        position.Z += Object.DrawHeightOffset;
        // Buildings rise out of the ground as they are built.
        if (Object.IsUnderConstruction)
            position.Z -= (1 - Object.ConstructionPercent / 100f) * Object.Height;

        position += Object.DrawJitter;
        return Matrix4x4.CreateScale(Object.DrawScale) * Matrix4x4.CreateFromQuaternion(Object.Tilt) * Matrix4x4.CreateRotationY(-Object.Pitch)
             * Matrix4x4.CreateRotationX(Object.Roll)
             * Matrix4x4.CreateRotationZ(Object.Orientation)
             * Matrix4x4.CreateTranslation(position);
    }

    /// <summary>
    /// W3DSupplyDraw: a supply pile shows as many of its SupplyBonePrefix01.. boxes as its share of
    /// boxes left (rounded up).
    /// </summary>
    void ApplySupplyBoxes(DrawPart part)
    {
        if (Object.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>() is not { StartingBoxes: > 0 } dock)
            return;
        string prefix = part.Data.Settings.GetString("SupplyBonePrefix");
        if (prefix.Length == 0 || part.Instance?.Model.Hierarchy is not { } hierarchy)
            return;
        int total = 0;
        while (hierarchy.FindPivot($"{prefix}{total + 1:00}") >= 0)
            total++;
        int show = Math.Min(total, (int)MathF.Ceiling(total * (dock.Boxes / (float)dock.StartingBoxes)));
        for (int i = 1; i <= total; i++)
        {
            string name = $"{prefix}{i:00}";
            if (i > show)
            {
                if (supplyHidden.Add(name))
                    part.Instance.HiddenSubObjects.Add(name);
                else
                    part.Instance.HiddenSubObjects.Add(name);   // (a model state change resets the set)
            }
            else if (supplyHidden.Remove(name))
                part.Instance.HiddenSubObjects.Remove(name);
        }
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
    /// <summary>How far the object moves in one play of the current animation (its "Animation = name distance"), or 0.</summary>
    float distanceCovered;
    bool playingIdle;
    readonly Dictionary<string, List<string>> flashMeshCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Treads, wheels and dust for tank and truck draws.</summary>
    public VehicleDraw? Vehicle { get; set; }

    /// <summary>Not drawn this frame (a science-only model the viewer cannot see).</summary>
    public bool Hidden { get; set; }

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
        distanceCovered = choice.DistanceCovered;
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
            Instance.Advance(seconds * SpeedFor(obj));
        }

        // Idle animations: pick another one when the current one finishes.
        if (playingIdle && State != null && State.IdleAnimations.Count > 1 && Instance.Animation != null
            && Instance.Frame >= Instance.Animation.FrameCount - 1.5f)
            StartAnimation(State, obj);
    }

    /// <summary>
    /// The original's W3DModelDraw::adjustAnimSpeedToMovementSpeed: an animation that says how far it
    /// carries the object (walking and running infantry) plays at the rate that covers that distance at
    /// the object's current speed, so feet step instead of sliding. Other animations keep their own rate.
    /// </summary>
    float SpeedFor(GameObject obj)
    {
        if (distanceCovered <= 0 || Instance?.Animation is not { FrameCount: > 1, FrameRate: > 0 } animation
            || obj.FindModule<Logic.Modules.Updates.PhysicsBehavior>() is not { } physics)
            return animationSpeed;
        float framesPerLogicFrame = physics.VelocityMagnitude * animation.FrameCount / distanceCovered;
        return framesPerLogicFrame * Logic.Core.LogicTime.FramesPerSecond / animation.FrameRate;
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

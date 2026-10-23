using System.Numerics;
using OpenGZH.Client.Effects;
using OpenGZH.Client.Graphics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Locomotion;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Client.World;

/// <summary>
/// What W3DTankDraw, W3DTruckDraw and W3DTankTruckDraw add to a model: scrolling treads (meshes named
/// TREADS*, L/R for each side) and tread debris for tanks; spinning and steering wheels on suspension,
/// a swinging cab and trailer, and dust, dirt and powerslide spray for trucks.
/// </summary>
public sealed class VehicleDraw
{
    readonly bool treads, wheels;
    readonly RuleNode settings;
    readonly ParticleSystemManager? particles;

    // Treads
    readonly float treadAnimationRate, treadPivotSpeedFraction, treadDriveSpeedFraction;
    readonly List<(RenderMesh Mesh, int Side)> treadMeshes = new();   // side: -1 left, 1 right, 0 middle
    ModelInstance? treadsFor;
    Vector3 lastDirection = Vector3.UnitX;
    ParticleSystem? treadDebrisLeft, treadDebrisRight;

    // Wheels
    readonly float rotationMultiplier, powerslideRotationAddition, cabRotationFactor, trailerRotationFactor, rotationDamping;
    float frontWheelRotation, rearWheelRotation, curCabRotation, curTrailerRotation;
    bool isPowersliding;
    ParticleSystem? dust, dirt, powerslide;

    public VehicleDraw(string className, RuleNode settings, ParticleSystemManager? particles)
    {
        this.settings = settings;
        this.particles = particles;
        treads = className is "W3DTankDraw" or "W3DTankTruckDraw" or "W3DOverlordTankDraw";
        wheels = className is "W3DTruckDraw" or "W3DTankTruckDraw" or "W3DOverlordTruckDraw" or "W3DPoliceCarDraw";
        treadAnimationRate = Float("TreadAnimationRate", 0);
        treadPivotSpeedFraction = Float("TreadPivotSpeedFraction", 0.6f);
        treadDriveSpeedFraction = Float("TreadDriveSpeedFraction", 0.3f);
        rotationMultiplier = Float("TireRotationMultiplier", 0);
        powerslideRotationAddition = Float("PowerslideRotationAddition", 0);
        cabRotationFactor = Float("CabRotationMultiplier", 0);
        trailerRotationFactor = Float("TrailerRotationMultiplier", 0);
        rotationDamping = Float("RotationDamping", 0);
    }

    public static bool Handles(string className) => className is "W3DTankDraw" or "W3DTruckDraw" or "W3DTankTruckDraw"
        or "W3DOverlordTankDraw" or "W3DOverlordTruckDraw" or "W3DPoliceCarDraw";

    float Float(string key, float fallback) =>
        settings.GetString(key) is { Length: > 0 } text ? RuleValue.Float(text, fallback) : fallback;

    string Name(string key, string fallback = "") => settings.GetString(key) is { Length: > 0 } v ? v : fallback;

    public void Update(DrawPart part, GameObject obj, ClientPhysics clientPhysics, float seconds, bool hidden)
    {
        var instance = part.Instance;
        var physics = obj.FindModule<PhysicsBehavior>();
        if (instance == null || physics == null)
            return;
        float frames = seconds * 30;   // the original advanced these once per drawn frame at 30 a second
        if (treads)
            UpdateTreads(part, instance, obj, physics, frames, hidden);
        if (wheels)
            UpdateWheels(part, instance, obj, physics, clientPhysics, frames, hidden);
    }

    // ---- Treads ----

    void FindTreadMeshes(ModelInstance instance)
    {
        treadsFor = instance;
        treadMeshes.Clear();
        instance.MeshUvOffsets = null;
        if (treadAnimationRate == 0)
            return;
        foreach (var mesh in instance.Model.Meshes)
        {
            if (!mesh.Name.StartsWith("TREADS", StringComparison.OrdinalIgnoreCase))
                continue;
            bool linearOffset = mesh.Source.VertexMaterials.Any(m => m.StageMapping == (int)TextureMapper.MappingType.LinearOffset);
            if (!linearOffset)
                continue;
            int side = mesh.Name.Length > 6 ? char.ToUpperInvariant(mesh.Name[6]) switch { 'L' => -1, 'R' => 1, _ => 0 } : 0;
            treadMeshes.Add((mesh, side));
        }
        if (treadMeshes.Count > 0)
            instance.MeshUvOffsets = treadMeshes.ToDictionary(t => t.Mesh, _ => Vector2.Zero);
    }

    void ScrollTread(ModelInstance instance, RenderMesh mesh, float delta)
    {
        var offsets = instance.MeshUvOffsets!;
        float u = offsets[mesh].X + delta;
        offsets[mesh] = new Vector2(u - MathF.Floor(u), 0);
    }

    void UpdateTreads(DrawPart part, ModelInstance instance, GameObject obj, PhysicsBehavior physics, float frames, bool hidden)
    {
        if (!ReferenceEquals(treadsFor, instance))
            FindTreadMeshes(instance);

        // Debris kicked up by moving tracks.
        var vel = physics.Velocity;
        float velMagSqr = vel.X * vel.X + vel.Y * vel.Y;
        treadDebrisLeft ??= Create(Name("TreadDebrisLeft", "TrackDebrisDirtLeft"), obj);
        treadDebrisRight ??= Create(Name("TreadDebrisRight", "TrackDebrisDirtRight"), obj);
        bool moving = velMagSqr > 0.00001f && !hidden;
        float velMag = MathF.Sqrt(velMagSqr);
        float xy = Math.Min(1, 0.5f * velMag + 0.1f), z = Math.Min(1, velMag + 0.1f);
        foreach (var system in new[] { treadDebrisLeft, treadDebrisRight })
        {
            if (system == null)
                continue;
            if (moving) system.Start(); else system.Stop();
            system.VelocityMultiplier = new Vector3(xy, xy, z);
            system.BurstCountMultiplier = z;
        }

        if (treadMeshes.Count == 0)
            return;
        float rate = treadAnimationRate / 30 * frames;
        float maxSpeed = obj.AI?.CurrentLocomotor?.MaxSpeedFor(obj) ?? 0;
        if (maxSpeed <= 0)
            return;
        var turn = physics.Turning;
        float speedFraction = physics.VelocityMagnitude / maxSpeed;
        if (turn != Turning.None && speedFraction < treadPivotSpeedFraction)
        {
            // Pivoting on the spot: the treads run in opposite directions.
            var dir = new Vector3(MathF.Cos(obj.Orientation), MathF.Sin(obj.Orientation), 0);
            float angleToGoal = dir.X * lastDirection.X + dir.Y * lastDirection.Y;
            if (MathF.Abs(1 - angleToGoal) > 0.00001f)
            {
                float delta = turn == Turning.Negative ? -rate : rate;
                foreach (var (mesh, side) in treadMeshes)
                    if (side != 0)
                        ScrollTread(instance, mesh, side < 0 ? delta : -delta);
            }
            lastDirection = dir;
        }
        else if (physics.IsMotive && speedFraction >= treadDriveSpeedFraction)
        {
            foreach (var (mesh, _) in treadMeshes)
                ScrollTread(instance, mesh, -rate);
        }
    }

    // ---- Wheels ----

    void UpdateWheels(DrawPart part, ModelInstance instance, GameObject obj, PhysicsBehavior physics, ClientPhysics clientPhysics, float frames, bool hidden)
    {
        var wheelInfo = clientPhysics.Wheels;
        float speed = physics.VelocityMagnitude;
        var ai = obj.AI;

        // The cab turns into the corner and the trailer swings out.
        if (part.FindPivot(Name("CabBone", "")) is int cab)
        {
            float desired = wheelInfo.WheelAngle * cabRotationFactor;
            if (ai != null && ai.Path.Count > 0)
            {
                var next = ai.Path[^1];
                float angleToGoal = Logic.Core.MathUtil.AngleDifference(MathF.Atan2(next.Y - obj.Position.Y, next.X - obj.Position.X), obj.Orientation);
                if (angleToGoal < 0)
                {
                    if (desired < angleToGoal) desired = angleToGoal;
                    if (desired > 0) desired = 0;
                }
                else
                {
                    if (desired > angleToGoal) desired = angleToGoal;
                    if (desired < 0) desired = 0;
                }
            }
            curCabRotation += (desired - curCabRotation) * rotationDamping;
            instance.Pose.Overrides[cab] = Matrix4x4.CreateRotationZ(curCabRotation);
            if (part.FindPivot(Name("TrailerBone", "")) is int trailer)
            {
                float trailerDesired = -wheelInfo.WheelAngle * trailerRotationFactor;
                curTrailerRotation += (trailerDesired - curTrailerRotation) * rotationDamping;
                instance.Pose.Overrides[trailer] = Matrix4x4.CreateRotationZ(curTrailerRotation);
            }
        }

        int? Bone(string key) => Name(key) is { Length: > 0 } name ? part.FindPivot(name) : null;
        var frontLeft = Bone("LeftFrontTireBone");
        var rearLeft = Bone("LeftRearTireBone");
        if (frontLeft != null || rearLeft != null)
        {
            float addition = powerslideRotationAddition;
            if (ai?.CurrentLocomotor is { IsMovingBackwards: true })
            {
                speed = -speed;
                addition = -addition;
            }
            frontWheelRotation += rotationMultiplier * speed * frames;
            rearWheelRotation += rotationMultiplier * (isPowersliding ? speed + addition : speed) * frames;

            // Front wheels steer; all wheels ride their suspension.
            Matrix4x4 Front(float height) => Matrix4x4.CreateRotationY(frontWheelRotation) * Matrix4x4.CreateRotationZ(wheelInfo.WheelAngle) * Matrix4x4.CreateTranslation(0, 0, height);
            Matrix4x4 Rear(float height) => Matrix4x4.CreateRotationY(rearWheelRotation) * Matrix4x4.CreateTranslation(0, 0, height);
            void Set(int? bone, Matrix4x4 m)
            {
                if (bone is int b)
                    instance.Pose.Overrides[b] = m;
            }
            if (frontLeft != null)
            {
                Set(frontLeft, Front(wheelInfo.FrontLeftHeightOffset));
                Set(Bone("RightFrontTireBone"), Front(wheelInfo.FrontRightHeightOffset));
            }
            if (rearLeft != null)
            {
                Set(rearLeft, Rear(wheelInfo.RearLeftHeightOffset));
                Set(Bone("RightRearTireBone"), Rear(wheelInfo.RearRightHeightOffset));
            }
            // Middle axles (a pair only counts if both sides exist).
            if (Bone("MidLeftFrontTireBone") is int mlf && Bone("MidRightFrontTireBone") is int mrf)
            {
                Set(mlf, Front(wheelInfo.FrontLeftHeightOffset));
                Set(mrf, Front(wheelInfo.FrontRightHeightOffset));
            }
            if (Bone("MidLeftRearTireBone") is int mlr && Bone("MidRightRearTireBone") is int mrr)
            {
                Set(mlr, Rear(wheelInfo.RearLeftHeightOffset));
                Set(mrr, Rear(wheelInfo.RearRightHeightOffset));
            }
            if (Bone("MidLeftMidTireBone") is int mlm && Bone("MidRightMidTireBone") is int mrm)
            {
                Set(mlm, Rear(wheelInfo.RearLeftHeightOffset));
                Set(mrm, Rear(wheelInfo.RearRightHeightOffset));
            }
        }

        // Dust behind, dirt when pulling away, spray when sliding round corners.
        isPowersliding = false;
        if (physics.IsMotive && !obj.IsSignificantlyAboveTerrain && !hidden)
        {
            dust ??= Create(Name("Dust"), obj);
            dirt ??= Create(Name("DirtSpray"), obj);
            powerslide ??= Create(Name("PowerslideSpray"), obj);
            dust?.Start();
            dirt?.Start();
            var accel = physics.Acceleration with { Z = 0 };
            var vel = physics.Velocity;
            bool accelerating = accel.Length() > 0.01f && accel.X * vel.X + accel.Y * vel.Y >= 0;
            float size = Math.Min(Math.Abs(speed), 2);
            if (dust != null)
                dust.SizeMultiplier = size;
            if (dirt != null)
            {
                if (wheelInfo.FramesAirborne > 3)
                {
                    // Landing after a jump throws up a big cloud.
                    float factor = Math.Min(2, 1 + wheelInfo.FramesAirborne / 16);
                    if (dust != null)
                    {
                        dust.SizeMultiplier = factor * 2;
                        dust.Trigger();
                    }
                }
                else if (!accelerating || size > 2)
                    dirt.Stop();
            }
            if (powerslide != null)
            {
                if (physics.Turning == Turning.None)
                    powerslide.Stop();
                else
                {
                    isPowersliding = true;
                    powerslide.Start();
                }
            }
        }
        else
        {
            dust?.Stop();
            dirt?.Stop();
            powerslide?.Stop();
        }
    }

    ParticleSystem? Create(string template, GameObject obj)
    {
        if (particles == null || template.Length == 0 || particles.CreateSystem(template) is not { } system)
            return null;
        system.AttachToObject(obj);
        system.Stop();
        return system;
    }

    /// <summary>The object is gone: end its effects.</summary>
    public void Dispose()
    {
        foreach (var system in new[] { treadDebrisLeft, treadDebrisRight, dust, dirt, powerslide })
            system?.Destroy();
    }
}

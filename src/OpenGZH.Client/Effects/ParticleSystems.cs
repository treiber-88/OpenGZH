using System.Numerics;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Client.Effects;

/// <summary>What a particle system needs from the world it lives in.</summary>
public interface IParticleWorld
{
    /// <summary>The logic frame (particles step once per logic frame, as in the original).</summary>
    int Frame { get; }
    GameObject? FindObject(uint id);
    /// <summary>The object as drawn: position and orientation.</summary>
    Matrix4x4 ObjectTransform(GameObject obj);
    /// <summary>Is the object fogged or shrouded for the local player? (Attached systems stop emitting.)</summary>
    bool IsHiddenFromLocalPlayer(GameObject obj);
    float GroundHeight(float x, float y);
}

/// <summary>The starting values of one particle (the original's ParticleInfo).</summary>
public struct ParticleInfo
{
    public Vector3 Velocity, Position, EmitterPosition;
    public float VelocityDamping, AngleZ, AngularRateZ, AngularDamping;
    public int Lifetime;
    public float Size, SizeRate, SizeRateDamping;
    public Keyframe[] AlphaKeys;
    public ColorKeyframe[] ColorKeys;
    public float ColorScale, WindRandomness;
    public bool ParticleUpTowardsEmitter;
}

public struct Keyframe
{
    public float Value;
    public int Frame;
}

/// <summary>One particle (the original's Particle): moves, spins, grows and fades through its keyframes.</summary>
public sealed class Particle
{
    const int MaxKeys = ParticleSystemTemplate.MaxKeyframes;

    internal readonly ParticleSystem System;
    internal LinkedListNode<Particle>? SystemNode, OverallNode;
    public uint Personality { get; internal set; }

    Vector3 velocity, position, emitterPosition, acceleration;
    readonly float velocityDamping, angularDamping, windRandomness, colorScale;
    float angleZ, angularRateZ, size, sizeRate, sizeRateDamping;
    int lifetimeLeft;
    readonly int createTimestamp;
    readonly bool upTowardsEmitter;
    readonly Keyframe[] alphaKeys = new Keyframe[MaxKeys];
    readonly ColorKeyframe[] colorKeys = new ColorKeyframe[MaxKeys];
    float alpha, alphaRate;
    int alphaTargetKey;
    Vector3 color, colorRate;
    int colorTargetKey;

    /// <summary>The system this particle carries along (PerParticleAttachedSystem).</summary>
    internal ParticleSystem? SystemUnderControl;

    public Vector3 Position => position;
    public float Size => size;
    public float Angle => angleZ;
    public float Alpha => alpha;
    public Vector3 Color => color;

    internal Particle(ParticleSystem system, in ParticleInfo info, int now)
    {
        System = system;
        velocity = info.Velocity;
        position = info.Position;
        angleZ = info.AngleZ;
        windRandomness = info.WindRandomness;
        upTowardsEmitter = info.ParticleUpTowardsEmitter;
        emitterPosition = info.EmitterPosition;
        angularRateZ = info.AngularRateZ;
        angularDamping = info.AngularDamping;
        velocityDamping = info.VelocityDamping;
        lifetimeLeft = info.Lifetime;
        createTimestamp = now;
        size = info.Size;
        sizeRate = info.SizeRate;
        sizeRateDamping = info.SizeRateDamping;
        Array.Copy(info.AlphaKeys, alphaKeys, MaxKeys);
        alpha = alphaKeys[0].Value;
        alphaTargetKey = 1;
        ComputeAlphaRate();
        Array.Copy(info.ColorKeys, colorKeys, MaxKeys);
        color = colorKeys[0].Color;
        colorTargetKey = 1;
        ComputeColorRate();
        colorScale = info.ColorScale;
    }

    void ComputeAlphaRate()
    {
        if (alphaTargetKey >= MaxKeys || alphaKeys[alphaTargetKey].Frame == 0)
        {
            alphaRate = 0;
            return;
        }
        float delta = alphaKeys[alphaTargetKey].Value - alphaKeys[alphaTargetKey - 1].Value;
        int time = alphaKeys[alphaTargetKey].Frame - alphaKeys[alphaTargetKey - 1].Frame;
        alphaRate = time != 0 ? delta / time : 0;
    }

    void ComputeColorRate()
    {
        if (colorTargetKey >= MaxKeys || colorKeys[colorTargetKey].Frame == 0)
        {
            colorRate = Vector3.Zero;
            return;
        }
        int time = colorKeys[colorTargetKey].Frame - colorKeys[colorTargetKey - 1].Frame;
        colorRate = time != 0 ? (colorKeys[colorTargetKey].Color - colorKeys[colorTargetKey - 1].Color) / time : Vector3.Zero;
    }

    public void ApplyForce(Vector3 force) => acceleration += force;

    /// <summary>One frame; false when the particle is used up.</summary>
    internal bool Update(int now)
    {
        velocity += acceleration;
        velocity *= velocityDamping;
        position += velocity + System.DriftVelocity;
        if (System.WindMotion != WindMotion.NotUsed)
            DoWindMotion();
        angleZ += angularRateZ;
        angularRateZ *= angularDamping;
        if (upTowardsEmitter)
            angleZ = AngleBetween(new Vector2(0, 1), new Vector2(position.X - emitterPosition.X, position.Y - emitterPosition.Y)) + MathF.PI;
        size += sizeRate;
        sizeRate *= sizeRateDamping;

        if (System.Shader != ParticleShader.Additive)
        {
            alpha += alphaRate;
            if (alphaTargetKey < MaxKeys && alphaKeys[alphaTargetKey].Frame != 0)
            {
                if (now - createTimestamp >= alphaKeys[alphaTargetKey].Frame)
                {
                    alpha = alphaKeys[alphaTargetKey].Value;
                    alphaTargetKey++;
                    ComputeAlphaRate();
                }
            }
            else
                alphaRate = 0;
            alpha = Math.Clamp(alpha, 0, 1);
        }

        color += colorRate;
        if (colorTargetKey < MaxKeys && colorKeys[colorTargetKey].Frame != 0)
        {
            if (now - createTimestamp >= colorKeys[colorTargetKey].Frame)
            {
                colorTargetKey++;
                ComputeColorRate();
            }
        }
        else
            colorRate = Vector3.Zero;
        color += new Vector3(colorScale);
        // (The original clamps green only from above because of a typo; kept.)
        color.X = Math.Clamp(color.X, 0, 1);
        if (color.X < 0) color.Y = 0; else if (color.Y > 1) color.Y = 1;
        color.Z = Math.Clamp(color.Z, 0, 1);

        acceleration = Vector3.Zero;
        if (lifetimeLeft != 0 && --lifetimeLeft == 0)
            return false;
        return !IsInvisible;
    }

    void DoWindMotion()
    {
        float windAngle = System.WindAngle;
        var systemPos = System.LocalPosition;
        if (System.AttachedToObject is { } attached)
            systemPos += attached.Position;
        var v = position - systemPos;
        const float fullForceDistance = 75f, noForceDistance = 200f;
        float distance = v.Length();
        if (distance < noForceDistance)
        {
            float strength = 2f * windRandomness;
            if (distance > fullForceDistance)
                strength *= 1f - (distance - fullForceDistance) / (noForceDistance - fullForceDistance);
            position.X += MathF.Cos(windAngle) * strength;
            position.Y += MathF.Sin(windAngle) * strength;
        }
    }

    bool IsInvisible => System.Shader switch
    {
        ParticleShader.Additive => (colorTargetKey >= MaxKeys || colorKeys[colorTargetKey].Frame == 0) && color.X + color.Y + color.Z <= 0.06f,
        ParticleShader.Alpha => alpha < 0.02f,
        ParticleShader.AlphaTest => false,
        ParticleShader.Multiply => (colorTargetKey >= MaxKeys || colorKeys[colorTargetKey].Frame == 0) && color.X * color.Y * color.Z > 0.95f,
        _ => true,
    };

    static float AngleBetween(Vector2 a, Vector2 b)
    {
        if (a.LengthSquared() == 0 || b.LengthSquared() == 0)
            return 0;
        float dot = a.X * b.X + a.Y * b.Y;
        float cos = dot / (a.Length() * b.Length());
        if (dot == 0)
            return b.X > 0 ? MathF.PI : 0;
        float theta = MathF.Acos(Math.Clamp(cos, -1, 1));
        return b.X > 0 ? theta : -theta;
    }
}

/// <summary>
/// An emitter (the original's ParticleSystem): after InitialDelay it emits BurstCount particles every
/// BurstDelay frames for SystemLifetime frames (0 = forever) from its emission volume, optionally
/// attached to an object, riding another system's particle, or feeding a slave system.
/// </summary>
public sealed class ParticleSystem
{
    const float MaxSizeBonus = 50;

    public int Id { get; }
    public ParticleSystemTemplate Template { get; }
    readonly ParticleSystemManager manager;
    internal readonly LinkedList<Particle> Particles = new();

    // Copied from the template (systems can be tweaked after creation).
    internal readonly ParticleShader Shader;
    internal Vector3 DriftVelocity;
    internal WindMotion WindMotion;
    public ParticleType Type { get; }
    public string ParticleName { get; }
    public bool IsGroundAligned { get; }
    public ParticlePriority Priority { get; private set; }
    RandomVariable burstCount, burstDelay;
    EmissionVelocity velocityType;
    EmissionVolume volumeType;
    float sphereRadius, cylinderRadius;
    RandomVariable startSize, sizeRate, sizeRateDamping, colorScale, lifetime;

    Matrix4x4 localTransform = Matrix4x4.Identity, transform = Matrix4x4.Identity;
    bool isLocalIdentity = true, isIdentity = true, skipParentTransform;
    int burstDelayLeft, delayLeft;
    int startTimestamp, systemLifetimeLeft;
    uint personalityStore;
    float accumulatedSizeBonus;
    public Vector3 VelocityMultiplier = Vector3.One;
    public float BurstCountMultiplier = 1, BurstDelayMultiplier = 1, SizeMultiplier = 1;
    Vector3 pos, lastPos;
    bool isFirstPos = true;
    readonly bool isForever;
    bool isStopped;
    public bool IsDestroyed { get; private set; }

    internal ParticleSystem? Slave, Master;
    Particle? controlParticle;
    uint attachedToObjectId;

    // Wind
    internal float WindAngle;
    float windAngleChange, windStartAngle, windEndAngle;
    bool windMovingToEnd;

    readonly Keyframe[] alphaKeysScratch = new Keyframe[ParticleSystemTemplate.MaxKeyframes];

    internal ParticleSystem(ParticleSystemManager manager, ParticleSystemTemplate template, int id, bool createSlaves)
    {
        this.manager = manager;
        Template = template;
        Id = id;
        Shader = template.Shader;
        Type = template.Type;
        ParticleName = template.ParticleName;
        IsGroundAligned = template.IsGroundAligned;
        Priority = template.Priority;
        DriftVelocity = template.DriftVelocity;
        WindMotion = template.WindMotion;
        burstCount = template.BurstCount;
        burstDelay = template.BurstDelay;
        velocityType = template.VelocityType;
        volumeType = template.VolumeType;
        sphereRadius = template.VolSphereRadius;
        cylinderRadius = template.VolCylinderRadius;
        startSize = template.StartSize;
        sizeRate = template.SizeRate;
        sizeRateDamping = template.SizeRateDamping;
        lifetime = template.Lifetime;
        colorScale = new RandomVariable(template.ColorScale.Low / 255f, template.ColorScale.High / 255f);
        delayLeft = (int)template.InitialDelay.Value;
        startTimestamp = manager.World.Frame;
        systemLifetimeLeft = template.SystemLifetime;
        isForever = template.SystemLifetime == 0;
        // The template's own change rate is never read from the rules, so it is always the default 0.15.
        windAngleChange = 0.15f;
        windMovingToEnd = true;
        windStartAngle = ClientRandom.Real(template.WindPingPongStartAngleMin, template.WindPingPongStartAngleMax);
        windEndAngle = ClientRandom.Real(template.WindPingPongEndAngleMin, template.WindPingPongEndAngleMax);
        WindAngle = ClientRandom.Real(windStartAngle, windEndAngle);
        if (createSlaves && template.SlaveSystem.Length > 0 && manager.CreateSystem(template.SlaveSystem, true) is { } slave)
        {
            Slave = slave;
            slave.Master = this;
        }
    }

    public int ParticleCount => Particles.Count;
    public uint AttachedObjectId => attachedToObjectId;
    internal GameObject? AttachedToObject => attachedToObjectId != 0 ? manager.World.FindObject(attachedToObjectId) : null;
    public Vector3 LocalPosition => localTransform.Translation;

    public void SetPosition(Vector3 position)
    {
        localTransform.Translation = position;
        isLocalIdentity = false;
    }

    public void SetLocalTransform(Matrix4x4 matrix)
    {
        localTransform = matrix;
        isLocalIdentity = false;
    }

    // W3D's Rotate_X/Y/Z turn the matrix about its own axes (post-multiply); in row-vector form that is a pre-multiply.
    public void RotateLocalTransformX(float x) { localTransform = Matrix4x4.CreateRotationX(x) * localTransform; isLocalIdentity = false; }
    public void RotateLocalTransformY(float y) { localTransform = Matrix4x4.CreateRotationY(y) * localTransform; isLocalIdentity = false; }
    public void RotateLocalTransformZ(float z) { localTransform = Matrix4x4.CreateRotationZ(z) * localTransform; isLocalIdentity = false; }

    public void SkipParentTransform(bool skip) => skipParentTransform = skip;
    public void AttachToObject(GameObject? obj) => attachedToObjectId = obj?.Id ?? 0;
    public void SetInitialDelay(int frames) => delayLeft = frames;
    public void SetSystemLifetime(int frames) => systemLifetimeLeft = frames;
    public void Start() => isStopped = false;
    public void Stop() => isStopped = true;
    public void Trigger() { burstDelayLeft = 0; delayLeft = 0; }

    public void SetEmissionVolumeSphereRadius(float radius) { if (volumeType == EmissionVolume.Sphere) sphereRadius = radius; }
    public void SetEmissionVolumeCylinderRadius(float radius) { if (volumeType == EmissionVolume.Cylinder) cylinderRadius = radius; }
    public EmissionVolume VolumeType => volumeType;

    /// <summary>Stops emitting; the system goes away once its particles are gone.</summary>
    public void Destroy()
    {
        IsDestroyed = true;
        Slave?.Destroy();
    }

    internal void SetControlParticle(Particle? p) => controlParticle = p;

    static Vector3 PointOnUnitSphere()
    {
        Vector3 point;
        do
            point = new Vector3(ClientRandom.Real(-1, 1), ClientRandom.Real(-1, 1), ClientRandom.Real(-1, 1));
        while (point == Vector3.Zero);
        return Vector3.Normalize(point);
    }

    Vector3 ComputeVelocity(Vector3 position)
    {
        var t = Template;
        Vector3 v;
        switch (velocityType)
        {
            case EmissionVelocity.Ortho:
                v = new Vector3(t.VelOrthoX.Value, t.VelOrthoY.Value, t.VelOrthoZ.Value);
                break;
            case EmissionVelocity.Cylindrical:
            {
                float radial = t.VelCylindricalRadial.Value;
                float angle = ClientRandom.Real(0, MathF.Tau);
                v = new Vector3(radial * MathF.Cos(angle), radial * MathF.Sin(angle), t.VelCylindricalNormal.Value);
                break;
            }
            case EmissionVelocity.Outward:
            {
                float speed = t.VelOutward.Value;
                float other = t.VelOutwardOther.Value;
                switch (volumeType)
                {
                    case EmissionVolume.Cylinder:
                    {
                        var disk = new Vector2(position.X, position.Y);
                        disk = disk.LengthSquared() > 0 ? Vector2.Normalize(disk) : Vector2.Zero;
                        v = new Vector3(speed * disk.X, speed * disk.Y, other);
                        break;
                    }
                    case EmissionVolume.Box:
                    case EmissionVolume.Sphere:
                        v = position.LengthSquared() > 0 ? Vector3.Normalize(position) * speed : Vector3.Zero;
                        break;
                    case EmissionVolume.Line:
                    {
                        var along = t.VolLineEnd - t.VolLineStart;
                        along = along.LengthSquared() > 0 ? Vector3.Normalize(along) : Vector3.UnitX;
                        var perp = Vector3.Cross(Vector3.UnitZ, along);
                        var up = Vector3.Cross(along, perp);
                        v = speed * perp + other * up;
                        break;
                    }
                    case EmissionVolume.Point:
                        v = PointOnUnitSphere() * speed;
                        break;
                    default:
                        v = Vector3.Zero;
                        break;
                }
                break;
            }
            case EmissionVelocity.Spherical:
                v = PointOnUnitSphere() * t.VelSpherical.Value;
                break;
            case EmissionVelocity.Hemispherical:
            {
                // (The original reads the spherical speed here: both share one union slot.)
                float speed = t.VelHemispherical.Low != 0 || t.VelHemispherical.High != 0 ? t.VelHemispherical.Value : t.VelSpherical.Value;
                Vector3 dir;
                do
                    dir = new Vector3(ClientRandom.Real(-1, 1), ClientRandom.Real(-1, 1), ClientRandom.Real(0, 1));
                while (dir == Vector3.Zero);
                v = Vector3.Normalize(dir) * speed;
                break;
            }
            default:
                v = Vector3.Zero;
                break;
        }
        float scale = 0.5f + manager.ParticleScale / 2f;
        return v * VelocityMultiplier * scale;
    }

    Vector3 ComputePosition()
    {
        var t = Template;
        Vector3 p;
        switch (volumeType)
        {
            case EmissionVolume.Cylinder:
            {
                float angle = ClientRandom.Real(0, MathF.Tau);
                float radius = t.IsHollow ? cylinderRadius : ClientRandom.Real(0, cylinderRadius);
                float half = t.VolCylinderLength / 2f;
                p = new Vector3(radius * MathF.Cos(angle), radius * MathF.Sin(angle), ClientRandom.Real(-half, half));
                break;
            }
            case EmissionVolume.Sphere:
            {
                float radius = t.IsHollow ? sphereRadius : ClientRandom.Real(0, sphereRadius);
                p = PointOnUnitSphere() * radius;
                break;
            }
            case EmissionVolume.Box:
            {
                var h = t.VolBoxHalfSize;
                if (t.IsHollow)
                {
                    int side = ClientRandom.Int(0, 6);
                    p = Vector3.Zero;
                    if (side % 3 == 0)
                    {
                        p.X = ClientRandom.Real(-h.X, h.X);
                        p.Y = ClientRandom.Real(-h.Y, h.Y);
                        p.Z = side == 0 ? -h.Z : h.Z;
                    }
                    else if (side % 3 == 1)
                    {
                        p.Y = ClientRandom.Real(-h.Y, h.Y);
                        p.Z = ClientRandom.Real(-h.Z, h.Z);
                        p.X = side == 1 ? -h.X : h.Y; // (sic)
                    }
                    else
                    {
                        p.X = ClientRandom.Real(-h.X, h.X);
                        p.Z = ClientRandom.Real(-h.Z, h.Z);
                        p.Y = side == 2 ? -h.Y : h.Y;
                    }
                    // The original falls through into LINE here for hollow boxes.
                    var delta = t.VolLineEnd - t.VolLineStart;
                    p = t.VolLineStart + ClientRandom.Real(0, 1) * delta;
                }
                else
                    p = new Vector3(ClientRandom.Real(-h.X, h.X), ClientRandom.Real(-h.Y, h.Y), ClientRandom.Real(-h.Z, h.Z));
                break;
            }
            case EmissionVolume.Line:
                p = t.VolLineStart + ClientRandom.Real(0, 1) * (t.VolLineEnd - t.VolLineStart);
                break;
            default:
                p = Vector3.Zero;
                break;
        }
        return p * (0.5f + manager.ParticleScale / 2f);
    }

    internal ParticleInfo GenerateParticleInfo(int particleNum, int particleCount)
    {
        var info = new ParticleInfo
        {
            AlphaKeys = new Keyframe[ParticleSystemTemplate.MaxKeyframes],
            ColorKeys = new ColorKeyframe[ParticleSystemTemplate.MaxKeyframes],
        };
        if (particleCount == 0)
            return info;
        info.Position = ComputePosition();
        info.Velocity = ComputeVelocity(info.Position);
        if (!isIdentity)
        {
            if (isFirstPos)
            {
                lastPos = pos;
                isFirstPos = false;
            }
            // Spread a moving emitter's burst back along the path it moved this frame.
            var adjustment = (1 - (float)particleNum / particleCount) * (pos - lastPos);
            info.Position = Vector3.Transform(info.Position, transform) - adjustment;
            info.Velocity = Vector3.TransformNormal(info.Velocity, transform);
        }
        info.VelocityDamping = Template.VelocityDamping.Value;
        info.AngularDamping = Template.AngularDamping.Value;
        info.AngleZ = Template.AngleZ.Value;
        info.AngularRateZ = Template.AngularRateZ.Value;
        info.Lifetime = (int)lifetime.Value;
        info.Size = startSize.Value * SizeMultiplier * manager.ParticleScale;
        info.SizeRate = sizeRate.Value * SizeMultiplier * manager.ParticleScale;
        info.SizeRateDamping = sizeRateDamping.Value;
        info.Size += accumulatedSizeBonus;
        accumulatedSizeBonus += Template.StartSizeRate.Value;
        if (accumulatedSizeBonus != 0)
            accumulatedSizeBonus = Math.Min(accumulatedSizeBonus, MaxSizeBonus);
        for (int i = 0; i < ParticleSystemTemplate.MaxKeyframes; i++)
        {
            info.AlphaKeys[i] = new Keyframe { Value = Template.AlphaKeys[i].Value.Value, Frame = Template.AlphaKeys[i].Frame };
            info.ColorKeys[i] = Template.ColorKeys[i];
        }
        info.ColorScale = colorScale.Value;
        info.EmitterPosition = transform.Translation;
        info.ParticleUpTowardsEmitter = Template.IsParticleUpTowardsEmitter;
        info.WindRandomness = ClientRandom.Real(0.7f, 1.3f);
        return info;
    }

    /// <summary>A slave particle takes the master's position and motion but its own looks.</summary>
    internal static ParticleInfo MergeRelated(ParticleSystem master, ParticleSystem slave)
    {
        var merge = master.GenerateParticleInfo(1, 1);
        var info = slave.GenerateParticleInfo(1, 1);
        merge.Lifetime = info.Lifetime;
        merge.Size *= info.Size;
        merge.SizeRate *= info.SizeRate;
        merge.SizeRateDamping *= info.SizeRateDamping;
        merge.AngleZ = info.AngleZ;
        merge.AngularRateZ = info.AngularRateZ;
        merge.AngularDamping = info.AngularDamping;
        merge.AlphaKeys = info.AlphaKeys;
        merge.ColorKeys = info.ColorKeys;
        merge.ColorScale = info.ColorScale;
        merge.Position += slave.Template.SlavePosOffset;
        return merge;
    }

    internal Particle? CreateParticle(in ParticleInfo info, ParticlePriority priority, bool force = false)
    {
        if (!force && !manager.MayCreateParticle(this, priority))
            return null;
        var p = new Particle(this, info, manager.World.Frame) { Personality = personalityStore++ };
        p.SystemNode = Particles.AddLast(p);
        manager.AddParticle(p, Priority);
        return p;
    }

    internal void RemoveParticle(Particle p)
    {
        if (p.SystemNode != null)
        {
            Particles.Remove(p.SystemNode);
            p.SystemNode = null;
        }
        manager.RemoveParticle(p);
        if (p.SystemUnderControl is { } controlled)
        {
            controlled.SetControlParticle(null);
            controlled.Destroy();
            p.SystemUnderControl = null;
        }
    }

    void UpdateWindMotion()
    {
        switch (WindMotion)
        {
            case WindMotion.PingPong:
            {
                float total = windEndAngle - windStartAngle;
                float half = total / 2f;
                float diff = MathF.Abs(half - WindAngle + windStartAngle);
                float change = half != 0 ? (1f - diff / half) * windAngleChange : windAngleChange;
                if (change < 0.005f)
                    change = 0.005f;
                if (windMovingToEnd)
                {
                    WindAngle += change;
                    if (WindAngle >= windEndAngle)
                    {
                        windMovingToEnd = false;
                        PickWindLimits();
                    }
                }
                else
                {
                    WindAngle -= change;
                    if (WindAngle <= windStartAngle)
                    {
                        windMovingToEnd = true;
                        PickWindLimits();
                    }
                }
                break;
            }
            case WindMotion.Circular:
                if (windAngleChange == 0)
                    windAngleChange = ClientRandom.Real(Template.WindAngleChangeMin, Template.WindAngleChangeMax);
                WindAngle += windAngleChange;
                if (WindAngle > MathF.Tau)
                    WindAngle -= MathF.Tau;
                else if (WindAngle < 0)
                    WindAngle += MathF.Tau;
                break;
        }
    }

    void PickWindLimits()
    {
        windAngleChange = ClientRandom.Real(Template.WindAngleChangeMin, Template.WindAngleChangeMax);
        windStartAngle = ClientRandom.Real(Template.WindPingPongStartAngleMin, Template.WindPingPongStartAngleMax);
        windEndAngle = ClientRandom.Real(Template.WindPingPongEndAngleMin, Template.WindPingPongEndAngleMax);
    }

    /// <summary>One logic frame: follow what it is attached to, emit, move the particles. False when it is finished.</summary>
    internal bool Update()
    {
        var world = manager.World;
        int now = world.Frame;
        if (delayLeft > 0)
        {
            if (--delayLeft == 0)
                startTimestamp = now;
            return true;
        }
        if (WindMotion != WindMotion.NotUsed)
            UpdateWindMotion();

        bool transformSet = false, isShrouded = false;
        Matrix4x4? parent = null;
        if (attachedToObjectId != 0)
        {
            if (world.FindObject(attachedToObjectId) is { IsDestroyed: false } obj)
            {
                isShrouded = world.IsHiddenFromLocalPlayer(obj);
                parent = world.ObjectTransform(obj);
                lastPos = pos;
                pos = obj.Position;
            }
            else
            {
                attachedToObjectId = 0;
                Destroy();
            }
        }
        if (parent is { } parentTransform)
        {
            transform = skipParentTransform ? localTransform : isLocalIdentity ? parentTransform : localTransform * parentTransform;
            isIdentity = false;
            transformSet = true;
        }
        if (!transformSet)
        {
            if (!isLocalIdentity)
            {
                transform = localTransform;
                isIdentity = false;
            }
            else
                isIdentity = true;
        }
        if (controlParticle != null)
        {
            transform.Translation = controlParticle.Position;
            isIdentity = false;
            lastPos = pos;
            pos = controlParticle.Position;
        }

        if (!IsDestroyed && (isForever || systemLifetimeLeft > 0) && !isShrouded && !isStopped && Master == null)
        {
            if (burstDelayLeft == 0)
            {
                int count = (int)(burstCount.Value * BurstCountMultiplier);
                for (int i = 0; i < count; i++)
                {
                    var info = GenerateParticleInfo(i, count);
                    if (Template.IsEmitAboveGroundOnly && info.Position.Z < world.GroundHeight(info.Position.X, info.Position.Y))
                        continue;
                    var p = CreateParticle(info, Priority);
                    if (p == null)
                        continue;
                    if (Template.PerParticleAttachedSystem.Length > 0 && manager.CreateSystem(Template.PerParticleAttachedSystem, true) is { } riding)
                    {
                        riding.SetControlParticle(p);
                        p.SystemUnderControl = riding;
                    }
                    if (Slave != null)
                    {
                        var merged = MergeRelated(this, Slave);
                        Slave.CreateParticle(merged, Priority);
                    }
                }
                burstDelayLeft = (int)(burstDelay.Value * BurstDelayMultiplier);
            }
            else
                burstDelayLeft--;
        }

        var node = Particles.First;
        while (node != null)
        {
            var next = node.Next;
            var p = node.Value;
            if (Template.Gravity != 0)
                p.ApplyForce(new Vector3(0, 0, Template.Gravity));
            if (!p.Update(now))
                RemoveParticle(p);
            node = next;
        }

        if (IsDestroyed && Particles.Count == 0)
            return false;
        if (!isForever)
        {
            if (systemLifetimeLeft > 0)
                systemLifetimeLeft--;
            if (Particles.Count > 0)
                return true;
            if (systemLifetimeLeft == 0)
                return false;
        }
        return true;
    }

    internal void Release()
    {
        if (Slave != null)
        {
            Slave.Master = null;
            Slave = null;
        }
        if (Master != null)
        {
            Master.Slave = null;
            Master = null;
        }
        while (Particles.First is { } first)
            RemoveParticle(first.Value);
        if (controlParticle != null)
            controlParticle.SystemUnderControl = null;
        controlParticle = null;
    }
}

/// <summary>
/// All live particle systems (the original's ParticleSystemManager): creates them from templates,
/// steps them once per logic frame, and keeps the particle count under MaxParticleCount by removing
/// the oldest lower-priority particles first.
/// </summary>
public sealed class ParticleSystemManager
{
    readonly Func<string, Core.Rules.RuleNode?> findTemplate;
    readonly Dictionary<string, ParticleSystemTemplate?> templates = new(StringComparer.OrdinalIgnoreCase);
    readonly List<ParticleSystem> systems = new();
    readonly Dictionary<int, ParticleSystem> byId = new();
    readonly LinkedList<Particle>[] byPriority;
    int nextId;
    int lastLogicFrame = -1;
    int particleCount;

    public IParticleWorld World { get; }
    public float ParticleScale { get; set; } = 1;
    public int MaxParticleCount { get; set; } = 2500;
    public int MaxFieldParticleCount { get; set; } = 30;

    /// <summary>Ground-aligned area-effect particles seen last frame (the renderer counts them).</summary>
    public int FieldParticleCount { get; set; }

    public IReadOnlyList<ParticleSystem> Systems => systems;
    public int ParticleCount => particleCount;

    public ParticleSystemManager(IParticleWorld world, Func<string, Core.Rules.RuleNode?> findTemplate)
    {
        World = world;
        this.findTemplate = findTemplate;
        byPriority = new LinkedList<Particle>[Enum.GetValues<ParticlePriority>().Length];
        for (int i = 0; i < byPriority.Length; i++)
            byPriority[i] = new LinkedList<Particle>();
    }

    public ParticleSystemTemplate? FindTemplate(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (!templates.TryGetValue(name, out var template))
        {
            template = findTemplate(name) is { } node ? ParticleSystemTemplate.Parse(name, node) : null;
            templates[name] = template;
        }
        return template;
    }

    public ParticleSystem? CreateSystem(string templateName, bool createSlaves = true) =>
        FindTemplate(templateName) is { } template ? CreateSystem(template, createSlaves) : null;

    public ParticleSystem CreateSystem(ParticleSystemTemplate template, bool createSlaves = true)
    {
        var system = new ParticleSystem(this, template, ++nextId, createSlaves);
        systems.Add(system);
        byId[system.Id] = system;
        return system;
    }

    public ParticleSystem? Find(int id) => byId.GetValueOrDefault(id);

    public void Destroy(int id) => Find(id)?.Destroy();

    public void DestroyAttachedSystems(GameObject obj)
    {
        foreach (var system in systems)
            if (system.AttachedObjectId == obj.Id)
                system.Destroy();
    }

    /// <summary>Steps every system once per logic frame.</summary>
    public void Update()
    {
        if (World.Frame == lastLogicFrame)
            return;
        lastLogicFrame = World.Frame;
        for (int i = 0; i < systems.Count; i++)
        {
            var system = systems[i];
            if (!system.Update())
            {
                system.Release();
                byId.Remove(system.Id);
                systems.RemoveAt(i);
                i--;
            }
        }
    }

    internal bool MayCreateParticle(ParticleSystem system, ParticlePriority priority)
    {
        if (system.ParticleCount > 0 && priority == ParticlePriority.AreaEffect && system.IsGroundAligned && FieldParticleCount > MaxFieldParticleCount)
            return false;
        if (priority != ParticlePriority.AlwaysRender)
        {
            int excess = particleCount - MaxParticleCount;
            if (excess > 0 && RemoveOldestParticles(excess, priority) != excess)
                return false;
            if (MaxParticleCount == 0)
                return false;
        }
        return true;
    }

    internal void AddParticle(Particle p, ParticlePriority priority)
    {
        p.OverallNode = byPriority[(int)priority].AddLast(p);
        particleCount++;
    }

    internal void RemoveParticle(Particle p)
    {
        if (p.OverallNode == null)
            return;
        p.OverallNode.List?.Remove(p.OverallNode);
        p.OverallNode = null;
        particleCount--;
    }

    int RemoveOldestParticles(int count, ParticlePriority priorityCap)
    {
        int toRemove = count;
        while (count > 0 && particleCount > 0)
        {
            bool removed = false;
            for (int i = (int)ParticlePriority.WeaponExplosion; i < (int)priorityCap; i++)
            {
                if (byPriority[i].First is { } oldest)
                {
                    oldest.Value.System.RemoveParticle(oldest.Value);
                    removed = true;
                    break;
                }
            }
            count--;
            if (!removed)
                break;
        }
        return toRemove - count;
    }

    public void Clear()
    {
        foreach (var system in systems)
            system.Release();
        systems.Clear();
        byId.Clear();
    }
}

using System.Numerics;
using OpenGZH.Logic.Locomotion;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Client.World;

/// <summary>Wheel state for truck draws (the original's TWheelInfo).</summary>
public struct WheelInfo
{
    public float FrontLeftHeightOffset, FrontRightHeightOffset, RearLeftHeightOffset, RearRightHeightOffset;
    public float WheelAngle;
    public int FramesAirborneCounter, FramesAirborne;
}

/// <summary>
/// The cosmetic body motion of a vehicle (the original's Drawable::calcPhysicsXform and DrawableLocoInfo):
/// tanks and trucks settle onto slopes on springs, rock when they speed up, brake and drive over things,
/// trucks bounce and steer their front wheels, aircraft bank and pitch, missiles wobble. It runs on the
/// client only, at the original's 30 steps a second, and gives an extra transform for the model.
/// </summary>
public sealed class ClientPhysics
{
    float pitch, pitchRate, roll, rollRate, yaw;
    float accelerationPitch, accelerationPitchRate, accelerationRoll, accelerationRollRate;
    float overlapZ, overlapZVel;
    float wobble = 1;
    float yawModulator, pitchModulator;
    float stepTime;
    WheelInfo wheelInfo;

    public float TotalPitch { get; private set; }
    public float TotalRoll { get; private set; }
    public float TotalYaw { get; private set; }
    public float TotalZ { get; private set; }
    public bool HasTransform { get; private set; }
    public WheelInfo Wheels => wheelInfo;

    const float StepSeconds = 1f / 30;

    /// <summary>Advances by real time; returns whether there is a transform to apply.</summary>
    public bool Update(GameObject obj, float seconds)
    {
        stepTime += seconds;
        int steps = 0;
        while (stepTime >= StepSeconds && steps < 4)
        {
            stepTime -= StepSeconds;
            Step(obj);
            steps++;
        }
        if (steps == 4)
            stepTime = 0;
        return HasTransform;
    }

    /// <summary>The transform to put in front of the object's own (original order: Z, pitch, roll, yaw).</summary>
    public Matrix4x4 LocalTransform() =>
        Matrix4x4.CreateRotationZ(TotalYaw) * Matrix4x4.CreateRotationX(-TotalRoll) * Matrix4x4.CreateRotationY(TotalPitch)
        * Matrix4x4.CreateTranslation(0, 0, TotalZ);

    void Step(GameObject obj)
    {
        HasTransform = false;
        if (obj.IsDisabledBy("DISABLED_HELD") || obj.AI?.CurrentLocomotor is not { } locomotor)
            return;
        var physics = obj.FindModule<PhysicsBehavior>();
        if (physics == null)
            return;
        float totalPitch = 0, totalRoll = 0, totalYaw = 0, totalZ = 0;
        switch (locomotor.Template.Appearance)
        {
            case Appearance.FourWheels:
                Wheels4(obj, locomotor, physics, ref totalPitch, ref totalRoll, ref totalZ, motorcycle: false);
                break;
            case Appearance.Motorcycle:
                Wheels4(obj, locomotor, physics, ref totalPitch, ref totalRoll, ref totalZ, motorcycle: true);
                break;
            case Appearance.Treads:
                Treads(obj, locomotor, physics, ref totalPitch, ref totalRoll, ref totalZ);
                break;
            case Appearance.Hover:
            case Appearance.Wings:
                HoverOrWings(obj, locomotor, physics, ref totalPitch, ref totalRoll, ref totalYaw);
                break;
            case Appearance.Thrust:
                Thrust(locomotor, ref totalPitch, ref totalRoll, ref totalYaw);
                break;
            default:
                return;
        }
        static float Denormal(float v) => v > -1e-20f && v < 1e-20f ? 0 : v;
        TotalPitch = Denormal(totalPitch);
        TotalRoll = Denormal(totalRoll);
        TotalYaw = Denormal(totalYaw);
        TotalZ = Denormal(totalZ);
        HasTransform = true;
    }

    static Vector3 Direction(GameObject obj) => new(MathF.Cos(obj.Orientation), MathF.Sin(obj.Orientation), 0);

    static Vector3 Normal(GameObject obj) =>
        obj.Layer > 0 ? Vector3.UnitZ : obj.Game.Terrain.GroundNormal(obj.Position.X, obj.Position.Y);

    void Thrust(Locomotor locomotor, ref float totalPitch, ref float totalRoll, ref float totalYaw)
    {
        var t = locomotor.Template;
        float rate = t.ThrustWobbleRate, max = t.ThrustMaxWobble, min = t.ThrustMinWobble;
        if (rate != 0)
        {
            if (wobble >= 1)
            {
                float d = pitch < max - rate * 2 ? rate : rate / 2;
                pitch += d;
                yaw += d;
                if (pitch >= max)
                    wobble = -1;
            }
            else
            {
                float d = pitch >= min + rate * 2 ? rate : rate / 2;
                pitch -= d;
                yaw -= d;
                if (pitch <= min)
                    wobble = 1;
            }
            totalPitch = pitch;
            totalYaw = yaw;
        }
        if (t.ThrustRoll != 0)
        {
            roll += t.ThrustRoll;
            totalRoll = roll;
        }
    }

    void SpringAccelerationAxes(LocomotorTemplate t)
    {
        accelerationPitchRate += -t.PitchStiffness * accelerationPitch + -t.PitchDamping * accelerationPitchRate;
        accelerationPitch += accelerationPitchRate;
        accelerationRollRate += -t.RollStiffness * accelerationRoll + -t.RollDamping * accelerationRollRate;
        accelerationRoll += accelerationRollRate;
    }

    void ReactToAcceleration(LocomotorTemplate t, Vector3 dir, Vector3 accel)
    {
        float forwardAccel = dir.X * accel.X + dir.Y * accel.Y;
        accelerationPitchRate += -(t.ForwardAccelerationPitchFactor * forwardAccel);
        float lateralAccel = -dir.Y * accel.X + dir.X * accel.Y;
        accelerationRollRate += -(t.LateralAccelerationRollFactor * lateralAccel);
    }

    void LimitAcceleration(LocomotorTemplate t)
    {
        float accelLimit = t.AccelerationPitchLimit, decelLimit = t.DecelerationPitchLimit;
        accelerationPitch = accelerationPitch > decelLimit ? decelLimit : accelerationPitch < -accelLimit ? -accelLimit : accelerationPitch;
        accelerationRoll = accelerationRoll > decelLimit ? decelLimit : accelerationRoll < -accelLimit ? -accelLimit : accelerationRoll;
    }

    void HoverOrWings(GameObject obj, Locomotor locomotor, PhysicsBehavior physics, ref float totalPitch, ref float totalRoll, ref float totalYaw)
    {
        var t = locomotor.Template;
        var dir = Direction(obj);
        var accel = physics.Acceleration;
        var vel = physics.Velocity;

        pitchRate += -t.PitchStiffness * pitch + -t.PitchDamping * pitchRate;
        rollRate += -t.RollStiffness * roll + -t.RollDamping * rollRate;
        pitch += pitchRate * t.UniformAxialDamping;
        roll += rollRate * t.UniformAxialDamping;
        SpringAccelerationAxes(t);
        totalPitch = pitch + accelerationPitch;
        totalRoll = roll + accelerationRoll;

        if (physics.IsMotive)
        {
            if (t.PitchInDirectionOfZVelFactor != 0 && MathF.Abs(vel.Z) > 0.001f)
                pitch -= t.PitchInDirectionOfZVelFactor * MathF.Atan2(vel.Z, MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y));
            float forwardVel = dir.X * vel.X + dir.Y * vel.Y;
            pitch += -(t.ForwardVelocityPitchFactor * forwardVel);
            float lateralVel = -dir.Y * vel.X + dir.X * vel.Y;
            roll += -(t.LateralVelocityRollFactor * lateralVel);
            ReactToAcceleration(t, dir, accel);
        }
        LimitAcceleration(t);

        totalYaw = t.RudderCorrectionDegree * MathF.Sin(yawModulator += t.RudderCorrectionRate);
        totalPitch += t.ElevatorCorrectionDegree * MathF.Cos(pitchModulator += t.ElevatorCorrectionRate);
    }

    void Treads(GameObject obj, Locomotor locomotor, PhysicsBehavior physics, ref float totalPitch, ref float totalRoll, ref float totalZ)
    {
        const float overlapShrinkFactor = 0.8f, flattenedObjectHeight = 0.5f, leaveOverlapPitchKick = MathF.PI / 128;
        const float overlapRoughVibrationFactor = 5, maxRoughVibration = 0.5f;
        var t = locomotor.Template;
        var pos = obj.Position;
        var dir = Direction(obj);
        var accel = physics.Acceleration;
        var vel = physics.Velocity;
        var perp = new Vector3(-dir.Y, dir.X, 0);
        var normal = Normal(obj);

        // Driving over something (a car, a wall) lifts and tips the tank.
        float newOverlapZ = 0;
        var overlapped = obj.Game.FindObject(physics.CurrentOverlap);
        if (overlapped != null && overlapped.IsKindOf("SHRUBBERY"))
            overlapped = null;
        if (overlapped != null)
        {
            var overPos = overlapped.Position;
            float dx = overPos.X - pos.X, dy = overPos.Y - pos.Y;
            float centerDistSqr = dx * dx + dy * dy;
            float maxCenterDist = (overlapped.Geometry.BoundingCircleRadius + obj.Geometry.BoundingCircleRadius) * overlapShrinkFactor;
            if (centerDistSqr < maxCenterDist * maxCenterDist)
            {
                float centerDist = MathF.Sqrt(centerDistSqr);
                float amount = Math.Clamp(1 - centerDist / maxCenterDist, 0, 1);
                float rough = Math.Min((vel.X * vel.X + vel.Y * vel.Y) * overlapRoughVibrationFactor, maxRoughVibration);
                float height = overlapped.Geometry.MaxHeightAbovePosition;
                bool flat = false;
                if (overlapped.IsKindOf("LOW_OVERLAPPABLE") || overlapped.IsKindOf("INFANTRY")
                    || overlapped.Body is { FrontCrushed: true, BackCrushed: true })
                {
                    flat = true;
                    height = flattenedObjectHeight;
                }
                if (amount < flattenedObjectHeight && !flat)
                {
                    newOverlapZ = height * 2 * amount;
                    var v = new Vector3(dx / centerDist, dy / centerDist, 0.2f);
                    var up = Vector3.Normalize(new Vector3(Rand(-rough, rough), Rand(-rough, rough), 1));
                    var prp = Vector3.Cross(v, up);
                    normal = Vector3.Normalize(Vector3.Cross(prp, v));
                }
                else
                {
                    newOverlapZ = height;
                    normal = Vector3.Normalize(new Vector3(Rand(-rough, rough), Rand(-rough, rough), 1));
                }
            }
        }
        else if (physics.PreviousOverlap != 0 && overlapZ > 0)
            pitchRate += leaveOverlapPitchKick;

        float groundPitch = (normal.X * dir.X + normal.Y * dir.Y) * (MathF.PI / 2);
        float groundRoll = (normal.X * perp.X + normal.Y * perp.Y) * (MathF.PI / 2);

        if (overlapped != null || overlapZ <= 0)
        {
            pitchRate += -t.PitchStiffness * (pitch - groundPitch) + -t.PitchDamping * pitchRate;
            if (pitchRate > 0)
                pitchRate *= 0.5f;
            rollRate += -t.RollStiffness * (roll - groundRoll) + -t.RollDamping * rollRate;
        }
        pitch += pitchRate * t.UniformAxialDamping;
        roll += rollRate * t.UniformAxialDamping;
        SpringAccelerationAxes(t);
        totalPitch = pitch + accelerationPitch;
        totalRoll = roll + accelerationRoll;
        if (physics.IsMotive)
            ReactToAcceleration(t, dir, accel);
        LimitAcceleration(t);

        if (newOverlapZ > overlapZ)
        {
            overlapZ = newOverlapZ;
            overlapZVel = 0;
        }
        float ztmp = overlapZ / 2;
        if (overlapZ > 0)
        {
            overlapZVel -= 0.2f;
            overlapZ += overlapZVel;
        }
        if (overlapZ <= 0)
        {
            overlapZ = 0;
            overlapZVel = 0;
        }
        totalZ = ztmp;
    }

    void Wheels4(GameObject obj, Locomotor locomotor, PhysicsBehavior physics, ref float totalPitch, ref float totalRoll, ref float totalZ, bool motorcycle)
    {
        var t = locomotor.Template;
        float bounceKick = t.BounceAmount, maxExtension = t.MaximumWheelExtension, wheelTurnAngle = t.FrontWheelTurnAngle;
        bool doWheels = t.HasSuspension;
        var pos = obj.Position;
        var dir = Direction(obj);
        var accel = physics.Acceleration;
        var perp = new Vector3(-dir.Y, dir.X, 0);
        var normal = Normal(obj);
        float hheight = obj.Game.Terrain.LayerHeight(pos.X, pos.Y, obj.Layer);
        float groundPitch = (normal.X * dir.X + normal.Y * dir.Y) * (MathF.PI / 2);
        float groundRoll = (normal.X * perp.X + normal.Y * perp.Y) * (MathF.PI / 2);
        float length = obj.Geometry.MajorRadius, width = obj.Geometry.MinorRadius;

        bool airborne = obj.IsSignificantlyAboveTerrain;
        if (airborne)
        {
            if (doWheels)
            {
                wheelInfo.FramesAirborne = 0;
                wheelInfo.FramesAirborneCounter++;
                float target = pos.Z - hheight > -maxExtension ? maxExtension : 0;
                wheelInfo.RearLeftHeightOffset += (target - wheelInfo.RearLeftHeightOffset) / 2;
                wheelInfo.RearRightHeightOffset = motorcycle ? wheelInfo.RearLeftHeightOffset
                    : wheelInfo.RearRightHeightOffset + (target - wheelInfo.RearRightHeightOffset) / 2;
            }
            float ph = length * MathF.Sin(pitch + accelerationPitch - groundPitch);
            float rh = width * MathF.Sin(roll + accelerationRoll - groundRoll);
            totalZ = MathF.Abs(ph) / 4 + (motorcycle ? 0 : MathF.Abs(rh) / 4);
            if (!motorcycle)
                return;   // (the four-wheel version stops here while airborne)
        }

        // Bumps in the road.
        float curSpeed = physics.VelocityMagnitude;
        float maxSpeed = locomotor.MaxSpeedFor(obj);
        if (!airborne && maxSpeed > 0 && curSpeed > maxSpeed / 10)
        {
            float factor = curSpeed / maxSpeed;
            if (MathF.Abs(pitchRate) < factor * bounceKick / 4 && MathF.Abs(rollRate) < factor * bounceKick / 8)
            {
                switch (Random.Shared.Next(4))
                {
                    case 0: pitchRate -= bounceKick * factor; rollRate -= bounceKick * factor / 2; break;
                    case 1: pitchRate += bounceKick * factor; rollRate -= bounceKick * factor / 2; break;
                    case 2: pitchRate -= bounceKick * factor; rollRate += bounceKick * factor / 2; break;
                    default: pitchRate += bounceKick * factor; rollRate += bounceKick * factor / 2; break;
                }
            }
        }

        if (!airborne)
        {
            pitchRate += -t.PitchStiffness * (pitch - groundPitch) + -t.PitchDamping * pitchRate;
            if (!motorcycle && pitchRate > 0)
                pitchRate *= 0.5f;
            rollRate += -t.RollStiffness * (roll - groundRoll) + -t.RollDamping * rollRate;
        }
        else
        {
            pitchRate += -t.PitchStiffness * pitch + -t.PitchDamping * pitchRate;
            rollRate += -t.RollStiffness * roll + -t.RollDamping * rollRate;
        }
        pitch += pitchRate * t.UniformAxialDamping;
        roll += rollRate * t.UniformAxialDamping;
        SpringAccelerationAxes(t);
        totalPitch = pitch + accelerationPitch;
        if (motorcycle)
        {
            // (The original's clamp can never pass, so motorcycles never roll.)
            totalRoll = 0;
        }
        else
            totalRoll = roll + accelerationRoll;
        if (physics.IsMotive)
            ReactToAcceleration(t, dir, accel);
        LimitAcceleration(t);

        totalZ = 0;
        float pitchHeight = length * MathF.Sin(totalPitch - groundPitch);
        float rollHeight = width * MathF.Sin(totalRoll - groundRoll);
        if (doWheels)
            UpdateSuspension(physics, wheelTurnAngle, maxExtension, pitchHeight, rollHeight, motorcycle);

        float divisor = 4;
        float p = MathF.Abs(totalPitch - groundPitch);
        if (p > MathF.PI / 8)
            divisor = (4 * MathF.PI / 8 + (p - MathF.PI / 8)) / p;
        if (!airborne)
        {
            totalZ += MathF.Abs(pitchHeight) / divisor;
            totalZ += MathF.Abs(rollHeight) / divisor;
        }
    }

    void UpdateSuspension(PhysicsBehavior physics, float wheelTurnAngle, float maxExtension, float pitchHeight, float rollHeight, bool motorcycle)
    {
        const float springFactor = 0.9f, wheelSmoothness = 10;
        wheelInfo.FramesAirborne = wheelInfo.FramesAirborneCounter;
        wheelInfo.FramesAirborneCounter = 0;
        var newInfo = wheelInfo;
        newInfo.WheelAngle = physics.Turning switch
        {
            Turning.Negative => -wheelTurnAngle,
            Turning.Positive => wheelTurnAngle,
            _ => 0,
        };
        if (physics.ForwardSpeed2D < 0)
            newInfo.WheelAngle = -newInfo.WheelAngle;
        wheelInfo.WheelAngle += (newInfo.WheelAngle - wheelInfo.WheelAngle) / wheelSmoothness;

        if (pitchHeight < 0)
        {
            newInfo.FrontLeftHeightOffset = newInfo.FrontRightHeightOffset = springFactor * (pitchHeight / 3 + pitchHeight / 2);
            newInfo.RearLeftHeightOffset = newInfo.RearRightHeightOffset = -pitchHeight / 2 + pitchHeight / 4;
        }
        else
        {
            newInfo.FrontLeftHeightOffset = newInfo.FrontRightHeightOffset = -pitchHeight / 4 + pitchHeight / 2;
            newInfo.RearLeftHeightOffset = newInfo.RearRightHeightOffset = springFactor * (-pitchHeight / 2 + -pitchHeight / 3);
        }
        if (!motorcycle)
        {
            if (rollHeight > 0)
            {
                newInfo.FrontRightHeightOffset += -springFactor * (rollHeight / 3 + rollHeight / 2);
                newInfo.RearRightHeightOffset += -springFactor * (rollHeight / 3 + rollHeight / 2);
                newInfo.RearLeftHeightOffset += rollHeight / 2 - rollHeight / 4;
                newInfo.FrontLeftHeightOffset += rollHeight / 2 - rollHeight / 4;
            }
            else
            {
                newInfo.FrontRightHeightOffset += -rollHeight / 2 + rollHeight / 4;
                newInfo.RearRightHeightOffset += -rollHeight / 2 + rollHeight / 4;
                newInfo.RearLeftHeightOffset += springFactor * (rollHeight / 3 + rollHeight / 2);
                newInfo.FrontLeftHeightOffset += springFactor * (rollHeight / 3 + rollHeight / 2);
            }
        }

        // Wheels drop quickly (half way each step) but compress at once.
        static float Settle(float current, float target) => target < current ? current + (target - current) / 2 : target;
        wheelInfo.FrontLeftHeightOffset = Settle(wheelInfo.FrontLeftHeightOffset, newInfo.FrontLeftHeightOffset);
        wheelInfo.RearLeftHeightOffset = Settle(wheelInfo.RearLeftHeightOffset, newInfo.RearLeftHeightOffset);
        if (motorcycle)
        {
            wheelInfo.FrontRightHeightOffset = wheelInfo.FrontLeftHeightOffset;
            wheelInfo.RearRightHeightOffset = wheelInfo.RearLeftHeightOffset;
        }
        else
        {
            wheelInfo.FrontRightHeightOffset = Settle(wheelInfo.FrontRightHeightOffset, newInfo.FrontRightHeightOffset);
            wheelInfo.RearRightHeightOffset = Settle(wheelInfo.RearRightHeightOffset, newInfo.RearRightHeightOffset);
        }
        wheelInfo.FrontLeftHeightOffset = Math.Max(wheelInfo.FrontLeftHeightOffset, maxExtension);
        wheelInfo.FrontRightHeightOffset = Math.Max(wheelInfo.FrontRightHeightOffset, maxExtension);
        wheelInfo.RearLeftHeightOffset = Math.Max(wheelInfo.RearLeftHeightOffset, maxExtension);
        wheelInfo.RearRightHeightOffset = Math.Max(wheelInfo.RearRightHeightOffset, maxExtension);
    }

    static float Rand(float min, float max) => min + (float)Random.Shared.NextDouble() * (max - min);
}

using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Locomotion;

/// <summary>Which surfaces a locomotor can travel on (combinable flags, as in the rules).</summary>
[Flags]
public enum Surfaces
{
    None = 0,
    Ground = 1 << 0,
    Water = 1 << 1,
    Cliff = 1 << 2,
    Air = 1 << 3,
    Rubble = 1 << 4,
}

/// <summary>How the unit drives: each appearance steers and accelerates its own way.</summary>
public enum Appearance { TwoLegs, FourWheels, Treads, Hover, Thrust, Wings, Climber, Other, Motorcycle }

/// <summary>How height is handled while moving.</summary>
public enum ZAxisBehavior
{
    NoZMotiveForce, SeaLevel, SurfaceRelativeHeight, AbsoluteHeight, FixedSurfaceRelativeHeight,
    FixedAbsoluteHeight, RelativeToGroundAndBuildings, RelativeToHighestLayer,
}

/// <summary>Where a unit goes in a group move.</summary>
public enum MovePriority { MovesBack, MovesMiddle, MovesFront }

/// <summary>
/// A locomotor definition from the "Locomotors" rules section, with the original's defaults and
/// units: speeds per frame, accelerations per frame², turn rates in radians per frame, angles in
/// radians, friction per frame. Missing damaged values default to the undamaged ones.
/// </summary>
public sealed class LocomotorTemplate
{
    /// <summary>The original's "very large" value for unlimited braking and turn speed.</summary>
    public const float BigNumber = 99999f;

    public string Name { get; }
    public Surfaces Surfaces { get; }
    public float Speed { get; }
    public float SpeedDamaged { get; }
    public float TurnRate { get; }
    public float TurnRateDamaged { get; }
    public float Acceleration { get; }
    public float AccelerationDamaged { get; }
    public float Lift { get; }
    public float LiftDamaged { get; }
    public float Braking { get; }
    public float MinSpeed { get; }
    public float MinTurnSpeed { get; }
    public float PreferredHeight { get; }
    public float PreferredHeightDamping { get; }
    public float CirclingRadius { get; }
    public float Extra2DFriction { get; }
    public float SpeedLimitZ { get; }
    public float MaxThrustAngle { get; }
    public ZAxisBehavior ZAxisBehavior { get; }
    public Appearance Appearance { get; }
    public MovePriority MovePriority { get; }
    public float AccelerationPitchLimit { get; }
    public float DecelerationPitchLimit { get; }
    public float BounceAmount { get; }
    public float PitchStiffness { get; }
    public float RollStiffness { get; }
    public float PitchDamping { get; }
    public float RollDamping { get; }
    public float ThrustRoll { get; }
    public float ThrustWobbleRate { get; }
    public float ThrustMinWobble { get; }
    public float ThrustMaxWobble { get; }
    public float PitchInDirectionOfZVelFactor { get; }
    public float ForwardVelocityPitchFactor { get; }
    public float LateralVelocityRollFactor { get; }
    public float ForwardAccelerationPitchFactor { get; }
    public float LateralAccelerationRollFactor { get; }
    public float UniformAxialDamping { get; }
    public float TurnPivotOffset { get; }
    public bool Apply2DFrictionWhenAirborne { get; }
    public bool DownhillOnly { get; }
    public bool AllowAirborneMotiveForce { get; }
    public bool WorksWhenDead { get; }
    public float AirborneTargetingHeight { get; }
    public bool StickToGround { get; }
    public bool CanMoveBackwards { get; }
    public bool HasSuspension { get; }
    public float FrontWheelTurnAngle { get; }
    public float MaximumWheelExtension { get; }
    public float MaximumWheelCompression { get; }
    public float CloseEnoughDistance { get; }
    public bool CloseEnoughDist3D { get; }
    /// <summary>SlideIntoPlaceTime, in frames.</summary>
    public float SlideIntoPlaceFactor { get; }
    public float WanderWidthFactor { get; }
    public float WanderLengthFactor { get; }
    public float WanderAboutPointRadius { get; }
    public float RudderCorrectionDegree { get; }
    public float RudderCorrectionRate { get; }
    public float ElevatorCorrectionDegree { get; }
    public float ElevatorCorrectionRate { get; }

    public LocomotorTemplate(string name, RuleNode rules)
    {
        Name = name;
        string S(string key) => rules.GetString(key);
        bool Has(string key) => rules.Has(key);
        float F(string key, float fallback = 0) => RuleValue.Float(S(key), fallback);
        float Velocity(string key, float fallbackPerFrame = 0) => Has(key) ? RuleUnits.Velocity(S(key)) : fallbackPerFrame;
        float Accel(string key, float fallbackPerFrame2 = 0) => Has(key) ? RuleUnits.Acceleration(S(key)) : fallbackPerFrame2;
        float Turn(string key, float fallback = 0) => Has(key) ? RuleUnits.AngularVelocity(S(key)) : fallback;
        float Angle(string key) => RuleUnits.Angle(S(key));

        foreach (string surface in RuleValue.Words(S("Surfaces")))
            if (Enum.TryParse<Surfaces>(surface, ignoreCase: true, out var value))
                Surfaces |= value;

        Speed = Velocity("Speed");
        SpeedDamaged = Velocity("SpeedDamaged", Speed);
        TurnRate = Turn("TurnRate");
        TurnRateDamaged = Turn("TurnRateDamaged", TurnRate);
        Acceleration = Accel("Acceleration");
        AccelerationDamaged = Accel("AccelerationDamaged", Acceleration);
        Lift = Accel("Lift");
        LiftDamaged = Accel("LiftDamaged", Lift);
        Braking = Accel("Braking", BigNumber);
        MinSpeed = Velocity("MinSpeed");
        MinTurnSpeed = Velocity("MinTurnSpeed", BigNumber);
        PreferredHeight = F("PreferredHeight");
        PreferredHeightDamping = F("PreferredHeightDamping", 1);
        CirclingRadius = F("CirclingRadius");
        Extra2DFriction = F("Extra2DFriction") * LogicTime.SecondsPerFrame;
        SpeedLimitZ = Velocity("SpeedLimitZ", 999999f);
        MaxThrustAngle = Angle("MaxThrustAngle");
        ZAxisBehavior = S("ZAxisBehavior").ToUpperInvariant() switch
        {
            "SEA_LEVEL" => ZAxisBehavior.SeaLevel,
            "SURFACE_RELATIVE_HEIGHT" => ZAxisBehavior.SurfaceRelativeHeight,
            "ABSOLUTE_HEIGHT" => ZAxisBehavior.AbsoluteHeight,
            "FIXED_SURFACE_RELATIVE_HEIGHT" => ZAxisBehavior.FixedSurfaceRelativeHeight,
            "FIXED_ABSOLUTE_HEIGHT" => ZAxisBehavior.FixedAbsoluteHeight,
            "FIXED_RELATIVE_TO_GROUND_AND_BUILDINGS" => ZAxisBehavior.RelativeToGroundAndBuildings,
            "RELATIVE_TO_HIGHEST_LAYER" => ZAxisBehavior.RelativeToHighestLayer,
            _ => ZAxisBehavior.NoZMotiveForce,
        };
        Appearance = S("Appearance").ToUpperInvariant() switch
        {
            "TWO_LEGS" => Appearance.TwoLegs,
            "FOUR_WHEELS" => Appearance.FourWheels,
            "TREADS" => Appearance.Treads,
            "HOVER" => Appearance.Hover,
            "THRUST" => Appearance.Thrust,
            "WINGS" => Appearance.Wings,
            "CLIMBER" => Appearance.Climber,
            "MOTORCYCLE" => Appearance.Motorcycle,
            _ => Appearance.Other,
        };
        MovePriority = S("GroupMovementPriority").ToUpperInvariant() switch
        {
            "MOVES_BACK" => MovePriority.MovesBack,
            "MOVES_FRONT" => MovePriority.MovesFront,
            _ => MovePriority.MovesMiddle,
        };
        AccelerationPitchLimit = Angle("AccelerationPitchLimit");
        DecelerationPitchLimit = Angle("DecelerationPitchLimit");
        BounceAmount = Turn("BounceAmount");
        PitchStiffness = F("PitchStiffness", 0.1f);
        RollStiffness = F("RollStiffness", 0.1f);
        PitchDamping = F("PitchDamping", 0.9f);
        RollDamping = F("RollDamping", 0.9f);
        ThrustRoll = F("ThrustRoll");
        ThrustWobbleRate = F("ThrustWobbleRate");
        ThrustMinWobble = F("ThrustMinWobble");
        ThrustMaxWobble = F("ThrustMaxWobble");
        PitchInDirectionOfZVelFactor = F("PitchInDirectionOfZVelFactor");
        ForwardVelocityPitchFactor = F("ForwardVelocityPitchFactor");
        LateralVelocityRollFactor = F("LateralVelocityRollFactor");
        ForwardAccelerationPitchFactor = F("ForwardAccelerationPitchFactor");
        LateralAccelerationRollFactor = F("LateralAccelerationRollFactor");
        UniformAxialDamping = F("UniformAxialDamping", 1);
        TurnPivotOffset = F("TurnPivotOffset");
        Apply2DFrictionWhenAirborne = RuleValue.Bool(S("Apply2DFrictionWhenAirborne"));
        DownhillOnly = RuleValue.Bool(S("DownhillOnly"));
        AllowAirborneMotiveForce = RuleValue.Bool(S("AllowAirborneMotiveForce"));
        WorksWhenDead = RuleValue.Bool(S("LocomotorWorksWhenDead"));
        AirborneTargetingHeight = Has("AirborneTargetingHeight") ? RuleValue.Int(S("AirborneTargetingHeight")) : int.MaxValue;
        StickToGround = RuleValue.Bool(S("StickToGround"));
        CanMoveBackwards = RuleValue.Bool(S("CanMoveBackwards"));
        HasSuspension = RuleValue.Bool(S("HasSuspension"));
        FrontWheelTurnAngle = Angle("FrontWheelTurnAngle");
        MaximumWheelExtension = F("MaximumWheelExtension");
        MaximumWheelCompression = F("MaximumWheelCompression");
        CloseEnoughDistance = F("CloseEnoughDist", 1);
        CloseEnoughDist3D = RuleValue.Bool(S("CloseEnoughDist3D"));
        SlideIntoPlaceFactor = F("SlideIntoPlaceTime") / LogicTime.MillisecondsPerFrame;
        WanderWidthFactor = F("WanderWidthFactor");
        WanderLengthFactor = F("WanderLengthFactor", 1);
        WanderAboutPointRadius = F("WanderAboutPointRadius");
        RudderCorrectionDegree = F("RudderCorrectionDegree");
        RudderCorrectionRate = F("RudderCorrectionRate");
        ElevatorCorrectionDegree = F("ElevatorCorrectionDegree");
        ElevatorCorrectionRate = F("ElevatorCorrectionRate");

        // The original's validation: wings always need some speed; thrust always moves.
        if (Appearance == Appearance.Wings)
        {
            if (MinSpeed <= 0) MinSpeed = 0.01f;
            if (MinTurnSpeed <= 0) MinTurnSpeed = 0.01f;
        }
        if (Appearance == Appearance.Thrust)
        {
            if (Speed <= 0) Speed = 0.01f;
            if (SpeedDamaged <= 0) SpeedDamaged = 0.01f;
            if (MinSpeed <= 0) MinSpeed = 0.01f;
        }
    }

    public bool IsAirborne => (Surfaces & Surfaces.Air) != 0;
    public bool CanUseWater => (Surfaces & Surfaces.Water) != 0;
    public bool CanUseGround => (Surfaces & Surfaces.Ground) != 0;

    public override string ToString() => Name;
}

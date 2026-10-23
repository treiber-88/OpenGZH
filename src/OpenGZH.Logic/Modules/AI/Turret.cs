using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// A turret on a unit or building (the "Turret" / "AltTurret" blocks of an AI module).
/// It aims the weapons in its ControlledWeaponSlots independently of the body. Angles are relative
/// to the body's facing; the client turns the model's turret bone to match.
/// </summary>
public sealed class Turret
{
    public float Angle { get; private set; }
    public float Pitch { get; private set; }

    public float TurnRate { get; }
    public float PitchRate { get; }
    public float NaturalAngle { get; }
    public float NaturalPitch { get; }
    public float FirePitch { get; }
    public bool AllowsPitch { get; }
    public int RecenterFrames { get; }
    public IReadOnlyList<WeaponSlot> ControlledSlots { get; }

    int lastAimFrame;

    /// <summary>The AltTurret block (it turns the model's AltTurret bones).</summary>
    public bool IsAlt { get; }

    public Turret(RuleNode settings, bool isAlt = false)
    {
        IsAlt = isAlt;
        string S(string key) => settings.GetString(key);
        TurnRate = RuleUnits.AngularVelocity(S("TurretTurnRate"), 90);
        PitchRate = RuleUnits.AngularVelocity(S("TurretPitchRate"), 90);
        NaturalAngle = RuleUnits.Angle(S("NaturalTurretAngle"));
        NaturalPitch = RuleUnits.Angle(S("NaturalTurretPitch"));
        FirePitch = RuleUnits.Angle(S("FirePitch"));
        AllowsPitch = RuleValue.Bool(S("AllowsPitch"));
        RecenterFrames = RuleUnits.Duration(S("RecenterTime"), 3000);
        ControlledSlots = RuleValue.Words(S("ControlledWeaponSlots"))
            .Select(w => Enum.TryParse<WeaponSlot>(w, true, out var slot) ? slot : WeaponSlot.Primary)
            .DefaultIfEmpty(WeaponSlot.Primary).ToList();
        Enabled = !RuleValue.Bool(S("InitiallyDisabled"));
        Angle = NaturalAngle;
        Pitch = NaturalPitch;
    }

    /// <summary>A disabled turret doesn't aim (deploy-style units switch theirs on once set up).</summary>
    public bool Enabled { get; set; }

    bool recentering;

    /// <summary>Swing back to the natural angle now (before packing up).</summary>
    public void Recenter() => recentering = true;

    public bool IsInNaturalPosition =>
        MathF.Abs(MathUtil.AngleDifference(Angle, NaturalAngle)) < 0.001f && (!AllowsPitch || MathF.Abs(Pitch - NaturalPitch) < 0.001f);

    public bool Controls(WeaponSlot slot) => ControlledSlots.Contains(slot);

    /// <summary>Turns towards a world-space angle; returns true when on target (within <paramref name="tolerance"/>).</summary>
    public bool AimAt(float worldAngle, float bodyOrientation, float tolerance, int frame, float targetPitch = 0)
    {
        if (!Enabled)
            return false;
        recentering = false;
        lastAimFrame = frame;
        float desired = MathUtil.NormalizeAngle(worldAngle - bodyOrientation);
        Angle = MathUtil.TurnTowards(Angle, desired, TurnRate);
        if (AllowsPitch)
            Pitch = MathUtil.TurnTowards(Pitch, targetPitch, PitchRate);
        return MathF.Abs(MathUtil.AngleDifference(Angle, desired)) <= tolerance;
    }

    /// <summary>Returns to the natural angle after a while without a target.</summary>
    public void Idle(int frame)
    {
        if (!recentering && frame - lastAimFrame < RecenterFrames)
            return;
        if (IsInNaturalPosition)
            recentering = false;
        Angle = MathUtil.TurnTowards(Angle, NaturalAngle, TurnRate);
        Pitch = MathUtil.TurnTowards(Pitch, NaturalPitch, PitchRate);
    }

    public bool IsTurning(float previousAngle) => MathF.Abs(previousAngle - Angle) > 0.0001f;
}

using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// An EMP pulse (the original's EMPUpdate): the glowing sphere grows toward a random target scale;
/// at StartFadeTime it disables vehicles and faction structures within EffectRadius for
/// DisabledDuration (sparking), knocks flying aircraft out of the sky (unless EMP_HARDENED), and is
/// gone after Lifetime.
/// </summary>
public sealed class EMPUpdate : Module, IUpdateModule, ICreateModule
{
    float currentScale = 1, targetScale = 1;
    int dieFrame, tintEnvPlayFrame;
    Vector3 startColor, endColor;

    static readonly int AffectsAllies = Flags.WeaponAffects.IndexOf("ALLIES");

    public void OnCreate()
    {
        int now = Frame;
        currentScale = FloatSetting("StartScale", 1);
        dieFrame = now + (Definition.Settings.Has("Lifetime") ? FramesSetting("Lifetime") : 1);
        tintEnvPlayFrame = now + FramesSetting("StartFadeTime");
        targetScale = Game.Random.Float(FloatSetting("TargetScaleMin", 1), FloatSetting("TargetScaleMax", 1));
        Owner.SetOrientation(Game.Random.Float(-MathF.PI, MathF.PI));
        var start = OpenGZH.Core.Rules.RuleColor.Parse(Setting("StartColor"), Vector4.Zero);
        var end = OpenGZH.Core.Rules.RuleColor.Parse(Setting("EndColor"), Vector4.Zero);
        startColor = Saturate(new Vector3(start.X, start.Y, start.Z), 2);
        endColor = Saturate(new Vector3(end.X, end.Y, end.Z), 5);
    }

    /// <summary>The original's saturateRGB.</summary>
    static Vector3 Saturate(Vector3 color, float factor) => color * factor - new Vector3(factor * 0.5f);

    public int Update()
    {
        var obj = Owner;
        int now = Frame;
        currentScale += (targetScale - currentScale) * 0.05f;
        obj.DrawScale = currentScale;
        if (now < tintEnvPlayFrame)
            obj.DrawColorTint = startColor;
        if (now == tintEnvPlayFrame)
        {
            obj.DrawColorTint = endColor; // (the original flashes it and fades out over the rest of the life)
            DoDisableAttack();
        }
        if (now >= dieFrame)
            obj.Kill();
        return 0;
    }

    void DoDisableAttack()
    {
        var obj = Owner;
        float radius = FloatSetting("EffectRadius", 200);
        int disabledDuration = FramesSetting("DisabledDuration");
        var rejectMask = Flags.WeaponAffects.Parse(Setting("DoesNotAffect"));
        bool notMyBuildings = BoolSetting("DoesNotAffectMyOwnBuildings");
        var pos = obj.Position;
        var producer = Game.FindObject(obj.ProducerId);
        GameObject? intendedVictim = null;
        bool onlyAirborne = false, intendedVictimProcessed = false;
        if (producer?.AI is { } producerAi)
        {
            intendedVictim = Game.FindObject(producerAi.TargetId);
            onlyAirborne = intendedVictim is { IsAirborneTarget: true };
        }
        if (radius > 0)
        {
            foreach (var victim in Game.ObjectsInRadius(pos, radius + 50).ToList())
            {
                if (victim == obj)
                    continue;
                // FROM_BOUNDINGSPHERE_3D
                if (Vector3.Distance(victim.Position, pos) - victim.BoundingSphereRadius > radius)
                    continue;
                if (onlyAirborne && !victim.IsAirborneTarget)
                    continue;
                if (notMyBuildings && victim.IsKindOf(Kind.Structure) && victim.Owner == obj.Owner)
                    continue;
                if (!victim.IsKindOf("VEHICLE") && !victim.IsKindOf(Kind.Structure) && !victim.IsKindOf("SPAWNS_ARE_THE_WEAPONS"))
                    continue;
                if (victim.IsKindOf("AIRCRAFT") && victim.IsAirborneTarget)
                {
                    if (victim.IsKindOf("EMP_HARDENED"))
                        continue;
                    victim.Kill();
                    victim.SetTint(TintStatus.Disabled, true);
                    continue;
                }
                if (victim.IsKindOf(Kind.Structure))
                {
                    if (!victim.IsFactionStructure)
                        continue;
                }
                else if (rejectMask.Has(AffectsAllies) && victim.RelationshipTo(obj) == Relationship.Allies)
                    continue;
                victim.SetDisabledFor("DISABLED_EMP", disabledDuration);
                if (victim == intendedVictim)
                    intendedVictimProcessed = true;
                Spark(victim, disabledDuration);
            }
        }
        if (intendedVictim != null && !intendedVictimProcessed && intendedVictim.IsKindOf("AIRCRAFT") && !intendedVictim.IsKindOf("EMP_HARDENED"))
        {
            // (The original compares a squared length against the radius itself.)
            float lengthSqr = (intendedVictim.Position - pos).LengthSquared();
            if (lengthSqr <= radius * 2 || lengthSqr <= 40 * 40)
                intendedVictim.SetDisabledFor("DISABLED_EMP", disabledDuration);
        }
    }

    void Spark(GameObject victim, int disabledDuration)
    {
        string template = Setting("DisableFXParticleSystem");
        if (template.Length == 0)
            return;
        var geometry = victim.Template.Geometry;
        float victimHeight = geometry.MaxHeightAbovePosition;
        float volume = geometry.FootprintArea * Math.Min(victimHeight, 10);
        int emitters = Math.Max(15, (int)MathF.Ceiling(FloatSetting("SparksPerCubicFoot", 0.001f) * volume));
        for (int e = 0; e < emitters; e++)
        {
            var offset = geometry.RandomOffsetWithinFootprint(Game.Random);
            offset.Z = Game.Random.Int(3, Math.Max(3, (int)victimHeight));
            if (offset.Length() > victimHeight)
            {
                float x = offset.X, y = offset.Y;
                offset = Vector3.Normalize(offset);
                offset.Z *= victimHeight;
                offset.X = x;
                offset.Y = y;
            }
            Game.Events.AttachParticleSystem(template, offset, victim, Math.Max(0, disabledDuration - 30), Game.Random.Int(1, 100));
        }
    }
}

/// <summary>
/// China's propaganda leaflet drop (the original's LeafletDropBehavior): after Delay (or when it
/// dies) it disables enemy infantry and vehicles within AffectRadius for DisabledDuration.
/// </summary>
public sealed class LeafletDropBehavior : Module, IUpdateModule, ICreateModule, IDieModule
{
    int startFrame;
    bool fxFired;

    public void OnCreate() => startFrame = Frame + (Definition.Settings.Has("Delay") ? FramesSetting("Delay") : 1);

    public int Update()
    {
        if (!fxFired)
        {
            Game.Events.AttachParticleSystem(Setting("LeafletFXParticleSystem"), Vector3.Zero, Owner);
            fxFired = true;
        }
        // (The original sleeps forever here, so the leaflets only take effect when the object dies.)
        if (Frame < startFrame)
            return LogicTime.SleepForever;
        DoDisableAttack();
        return 0;
    }

    public void OnDie(DamageInfo killingBlow) => DoDisableAttack();

    void DoDisableAttack()
    {
        float radius = FloatSetting("AffectRadius", 60);
        if (radius <= 0)
            return;
        var pos = Owner.Position;
        foreach (var victim in Game.ObjectsInRadius(pos, radius + 50).ToList())
        {
            if (victim == Owner || Vector3.Distance(victim.Position, pos) - victim.BoundingSphereRadius > radius)
                continue;
            if (!(victim.IsKindOf(Kind.Infantry) || victim.IsKindOf("VEHICLE")))
                continue;
            if (victim.RelationshipTo(Owner) != Relationship.Enemies)
                continue;
            victim.SetDisabledFor("DISABLED_EMP", FramesSetting("DisabledDuration"));
        }
    }
}

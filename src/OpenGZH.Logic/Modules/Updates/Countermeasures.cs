using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// Decoy flares (the USA Countermeasures upgrade). When a small missile is fired at the plane, it is
/// fooled with chance EvasionRate: after ReactionLaunchLatency the plane fires a volley of VolleySize
/// flares (FlareTemplateName) spread over VolleyArcAngle, then more every DelayBetweenVolleys, and
/// MissileDecoyDelay after launch the fooled missile turns to the nearest flare and does no damage.
/// NumberOfVolleys volleys, reloaded after ReloadTime (or on the airfield, MustReloadAtAirfield).
/// Only works once the upgrade (TriggeredBy) is in.
/// </summary>
public sealed class CountermeasuresBehavior : UpgradeModule, IUpdateModule
{
    string flareTemplate = "";
    int volleySize, numberOfVolleys, framesBetweenVolleys, reloadFrames, missileDecoyFrames, reactionFrames;
    float volleyArcAngle, volleyVelocityFactor, evasionRate;

    readonly List<uint> countermeasures = new();
    int availableCountermeasures, activeCountermeasures;
    int reactionFrame, nextVolleyFrame, reloadFrame;
    int divertedMissiles, incomingMissiles;

    protected override void InitializeUpgrade()
    {
        flareTemplate = Setting("FlareTemplateName");
        volleySize = IntSetting("VolleySize");
        volleyArcAngle = RuleUnits.Angle(Setting("VolleyArcAngle"));
        volleyVelocityFactor = FloatSetting("VolleyVelocityFactor");
        framesBetweenVolleys = FramesSetting("DelayBetweenVolleys");
        numberOfVolleys = IntSetting("NumberOfVolleys");
        reloadFrames = FramesSetting("ReloadTime");
        evasionRate = FloatSetting("EvasionRate");
        missileDecoyFrames = FramesSetting("MissileDecoyDelay");
        reactionFrames = FramesSetting("ReactionLaunchLatency");
        availableCountermeasures = numberOfVolleys * volleySize;
    }

    protected override void OnUpgrade() => Owner.WakeUpdate(this);

    bool IsActive_ => IsActive;

    /// <summary>A small missile was fired at us: maybe it will be fooled.</summary>
    public void ReportMissile(GameObject missile)
    {
        incomingMissiles++;
        if (availableCountermeasures + activeCountermeasures <= 0)
            return;
        if (Game.Random.Float(0, 1) >= evasionRate)
            return;
        if (missile.AI is not AI.MissileAIUpdate guided)
            return;
        guided.SetFramesTillCountermeasureDiversion(missileDecoyFrames);
        divertedMissiles++;
        if (activeCountermeasures == 0 && reactionFrame == 0)
            reactionFrame = Frame + reactionFrames;
    }

    /// <summary>The flare a fooled missile goes for (as in the original, it looks at the latest volley's end of the list).</summary>
    public GameObject? FlareToDivertTo(GameObject missile)
    {
        int iterations = Math.Max(volleySize, 1);
        float closest = 1e15f;
        GameObject? best = null;
        int index = countermeasures.Count - 1;
        while (iterations-- > 0 && index >= 0)
        {
            if (Game.FindObject(countermeasures[index]) is { } flare)
            {
                float dist = MathUtil.DistanceSquared2D(flare.Position, Owner.Position);
                if (dist < closest)
                {
                    closest = dist;
                    best = flare;
                }
            }
            else
                index--;
        }
        return best;
    }

    public void ReloadCountermeasures()
    {
        availableCountermeasures = numberOfVolleys * volleySize;
        reloadFrame = 0;
    }

    public int Update()
    {
        int now = Frame;
        if (Owner.IsEffectivelyDead || !IsActive_)
            return LogicTime.SleepForever;

        for (int i = countermeasures.Count - 1; i >= 0; i--)
        {
            if (Game.FindObject(countermeasures[i]) == null)
            {
                countermeasures.RemoveAt(i);
                activeCountermeasures--;
            }
        }

        if (Owner.IsAirborneTarget && availableCountermeasures > 0)
        {
            if (reactionFrame != 0 && reactionFrame == now)
            {
                LaunchVolley();
                nextVolleyFrame = now + framesBetweenVolleys;
                reactionFrame = 0;
            }
            if (nextVolleyFrame == now)
            {
                LaunchVolley();
                nextVolleyFrame = now + framesBetweenVolleys;
            }
        }

        if (availableCountermeasures == 0 && reloadFrames != 0)
        {
            if (reloadFrame != 0)
            {
                if (reloadFrame <= now)
                    ReloadCountermeasures();
            }
            else
                reloadFrame = now + reloadFrames;
        }
        return 0;
    }

    void LaunchVolley()
    {
        var physics = Owner.FindModule<PhysicsBehavior>();
        for (int i = 0; i < volleySize; i++)
        {
            float ratio = volleySize != 1 ? i / (float)(volleySize - 1) * 2 - 1 : 0;
            float angle = ratio * volleyArcAngle + Owner.Orientation;
            float speed = physics?.VelocityMagnitude ?? 0;
            if (speed < 1)
                speed = -10;
            var push = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * speed * volleyVelocityFactor;
            var flare = Game.CreateObject(flareTemplate, Owner.Owner, Owner.Position, Owner.Orientation);
            if (flare == null)
                continue;
            if (physics != null && flare.FindModule<PhysicsBehavior>() is { } flarePhysics)
            {
                physics.TransferVelocityTo(flarePhysics);
                flarePhysics.ApplyMotiveForce(push);
            }
            activeCountermeasures++;
            availableCountermeasures--;
            countermeasures.Add(flare.Id);
        }
    }
}

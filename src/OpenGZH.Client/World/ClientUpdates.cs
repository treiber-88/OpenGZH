using System.Numerics;
using OpenGZH.Client.Effects;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Scripting;

namespace OpenGZH.Client.World;

/// <summary>
/// Trees and props swaying in the wind (the original's SwayClientUpdate): each sways at its own
/// slightly random rate and strength around a lean, in the breeze's direction, until it is burned.
/// SET_TREE_SWAY scripts change the breeze.
/// </summary>
public sealed class SwayClientUpdate
{
    float curDelta, curValue, curAngle, curAngleLimit, leanAngle;
    bool swaying = true;
    int curVersion = -1;
    float stepTime;

    static float Rand(float min, float max) => min + Random.Shared.NextSingle() * (max - min);

    void UpdateSway(BreezeInfo info)
    {
        if (info.Randomness == 0)
            curValue = 0;
        float delta = info.Randomness * 0.5f;
        curAngleLimit = info.Intensity * Rand(1 - delta, 1 + delta);
        curDelta = 2 * MathF.PI / info.BreezePeriod * Rand(1 - delta, 1 + delta);
        leanAngle = info.Lean * Rand(1 - delta, 1 + delta);
        curVersion = info.Version;
    }

    /// <summary>Steps at 30 a second; returns the tilt to put in front of the model's transform.</summary>
    public Matrix4x4 Update(GameObject obj, float seconds, bool visible)
    {
        var info = obj.Game.Scripts?.Breeze ?? BreezeInfo.Default;
        if (swaying)
        {
            if (info.Version != curVersion)
                UpdateSway(info);
            stepTime += seconds;
            for (int i = 0; stepTime >= 1f / 30 && i < 4; i++)
            {
                stepTime -= 1f / 30;
                if (!visible)
                    continue;
                curValue += curDelta;
                if (curValue > 2 * MathF.PI)
                    curValue -= 2 * MathF.PI;
                curAngle = MathF.Cos(curValue) * curAngleLimit + leanAngle;
            }
            if (stepTime > 1f / 30)
                stepTime = 0;
            if (obj.HasStatus("BURNED"))
                swaying = false;   // (the original stops the sway where it is)
        }
        var dir = info.DirectionVec;
        return Matrix4x4.CreateRotationY(curAngle * dir.Y) * Matrix4x4.CreateRotationX(-curAngle * dir.X);
    }
}

/// <summary>
/// A multiplayer beacon (the original's BeaconClientUpdate): smoke in its owner's color
/// (BeaconSmoke + the color, or BeaconSmokeFFFFFF) and a radar pulse every RadarPulseFrequency.
/// </summary>
public sealed class BeaconClientUpdate(Core.Rules.RuleNode settings)
{
    ParticleSystem? smoke;
    int lastRadarPulse = -1;

    public void Update(GameObject obj, ParticleSystemManager? particles, bool hidden)
    {
        if (smoke == null && particles != null)
        {
            smoke = particles.CreateSystem($"BeaconSmoke{obj.Owner.Color & 0xFFFFFF:X6}") ?? particles.CreateSystem("BeaconSmokeFFFFFF");
            smoke?.AttachToObject(obj);
        }
        if (hidden)
            return;
        int frequency = Logic.Core.LogicTime.Frames(Core.Rules.RuleValue.Float(settings.GetString("RadarPulseFrequency"), 1000));
        if (lastRadarPulse < 0)
            lastRadarPulse = obj.Game.Frame;
        if (obj.Game.Frame > lastRadarPulse + frequency)
        {
            obj.Game.Events.RadarEvent("BEACON_PULSE", obj.Position, obj.Owner);
            lastRadarPulse = obj.Game.Frame;
        }
    }
}

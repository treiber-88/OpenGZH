using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// A dam-break flood wave (the original's WaveGuideUpdate). It waits disabled until DamDie lets it go,
/// then after WaveDelay rides the WaveGuide1 waypoint path: a bent front of shape points (YSize wide,
/// LinearWaveSpacing apart, bent by WaveBendMagnitude) carries spray, splashes on the shore, and soaks,
/// topples and damages everything low enough that it reaches, washing away bridges into WaterWaveBridge.
/// </summary>
public sealed class WaveGuideUpdate : Module, IUpdateModule
{
    const int MaxShapePoints = 64;
    const float PathExtraDistance = 10 * Formats.Maps.MapFile.CellSize;

    bool needDisable = true, initialized;
    int activeFrame, splashSoundFrame;
    Vector3 finalDestination;
    readonly List<Vector3> shapePoints = new();
    Vector3[] transformed = [];

    float PreferredHeight => FloatSetting("PreferredHeight");

    /// <summary>Starts the wave down its waypoint path (the original's startMoving).</summary>
    bool StartMoving()
    {
        var wave = Owner;
        Game.Events.PlayHandledSound(Setting("LoopingSound"), wave);
        if (Game.Waypoints?.Find("WaveGuide1") is not { } waypoint)
            return true;
        var visited = new HashSet<Terrain.Waypoint>();
        for (var verify = waypoint; verify != null && visited.Add(verify); verify = verify.Next.FirstOrDefault())
        {
            if (verify.Next.Count > 1)
                return false;   // the path cannot branch
            finalDestination = verify.Position;
        }
        if (waypoint.Next.FirstOrDefault() is not { } next)
            return false;
        wave.SetOrientation(MathF.Atan2(next.Position.Y - waypoint.Position.Y, next.Position.X - waypoint.Position.X));
        if (wave.AI is { } ai)
        {
            wave.SetPosition(waypoint.Position with { Z = Game.Terrain.GroundHeight(waypoint.Position) });
            ai.FollowWaypointPath(waypoint, Commands.CommandSource.AI);
        }
        return true;
    }

    bool InitWaveGuide()
    {
        if (!StartMoving())
            return false;
        ComputeWaveShapePoints();
        for (int i = 0; i < shapePoints.Count; i++)
        {
            Game.Events.AttachParticleSystem("WaveSpray01", shapePoints[i], Owner);
            Game.Events.AttachParticleSystem("WaveSpray02", shapePoints[i], Owner);
            if (i % 5 == 0)
                Game.Events.AttachParticleSystem("WaveSpray03", shapePoints[i], Owner);
        }
        return true;
    }

    void ComputeWaveShapePoints()
    {
        int halfY = (int)(FloatSetting("YSize") / 2);
        float spacing = FloatSetting("LinearWaveSpacing");
        float bend = FloatSetting("WaveBendMagnitude");
        if (spacing <= 0)
            spacing = 1;
        for (float y = -halfY; y < halfY && shapePoints.Count < MaxShapePoints; y += spacing)
        {
            int iy = (int)y;    // the original walks an Int
            shapePoints.Add(new Vector3(bend != 0 ? -(iy * iy) / bend : 0, iy, 0));
        }
        transformed = new Vector3[shapePoints.Count];
    }

    Vector3 TransformPoint(Vector3 local)
    {
        var wave = Owner;
        float c = MathF.Cos(wave.Orientation), s = MathF.Sin(wave.Orientation);
        return wave.Position + new Vector3(local.X * c - local.Y * s, local.X * s + local.Y * c, local.Z);
    }

    void TransformWaveShape()
    {
        for (int i = 0; i < shapePoints.Count; i++)
        {
            var p = TransformPoint(shapePoints[i]);
            transformed[i] = p with { Z = Game.Terrain.GroundHeight(p) };
        }
    }

    /// <summary>Splashes where the front meets the shore, every other frame.</summary>
    void DoShoreEffects()
    {
        if ((Frame & 1) != 0)
            return;
        float shoreDistance = FloatSetting("ShorelineEffectDistance");
        var points = shapePoints.Select(p => TransformPoint(p with { X = p.X - shoreDistance })).ToArray();
        bool underWater = true;
        for (int i = 0; i < points.Length; i++)
        {
            if (Game.Terrain.GroundHeight(points[i]) > PreferredHeight)
            {
                if (underWater && i != 0)
                    Game.Events.ParticleSystem("WaveSplashRight01", points[i - 1]);
                underWater = false;
            }
            else
            {
                if (!underWater && i != 0)
                    Game.Events.ParticleSystem("WaveSplashLeft01", points[i]);
                underWater = true;
            }
        }
    }

    void DoDamage()
    {
        var wave = Owner;
        float radius = FloatSetting("DamageRadius");
        for (int i = 0; i < shapePoints.Count; i++)
        {
            var point = transformed[i];
            foreach (var obj in Game.ObjectsInRadius(point, radius).ToList())
            {
                if (obj.IsKindOf("WAVEGUIDE") || obj.IsKindOf("BRIDGE_TOWER"))
                    continue;
                if (MathUtil.Distance2D(obj.Position, point) > radius)
                    continue;
                bool isBridge = obj.IsKindOf("BRIDGE");
                if (obj.Position.Z > PreferredHeight && !isBridge)
                    continue;
                var v = new Vector3(obj.Position.X - point.X, obj.Position.Y - point.Y, 0);
                if (v.LengthSquared() > 0)
                    v = Vector3.Normalize(v);
                // The original dots the direction with the point's world position (not the wave's heading).
                if (Vector3.Dot(v, point) >= 0 || obj.HasStatus("WET"))
                    continue;
                Game.Events.AttachParticleSystem("WaveHit01", shapePoints[i] with { Z = obj.Position.Z }, wave);
                obj.SetStatus("WET");
                var toppleVector = new Vector3(obj.Position.X - point.X, obj.Position.Y - point.Y, 0);
                obj.FindModule<ToppleUpdate>()?.Topple(toppleVector, FloatSetting("ToppleForce"), ToppleUpdate.Options.NoBounce | ToppleUpdate.Options.NoFx);
                obj.Damage(new DamageInfo { DamageType = "WATER", DeathType = "FLOODED", SourceId = wave.Id, Amount = FloatSetting("DamageAmount") });
                obj.SetCondition("FLOODED");
                if (isBridge)
                    WashAwayBridge(obj);
            }
        }
    }

    void WashAwayBridge(GameObject obj)
    {
        var oldBridge = Game.Terrain.Bridges.FindBridgeAt(obj.Position);
        float angle = 0;
        if (oldBridge != null)
            angle = MathF.Atan2(oldBridge.Info.To.Y - oldBridge.Info.From.Y, oldBridge.Info.To.X - oldBridge.Info.From.X);
        if (Game.CreateObject("WaterWaveBridge", Game.Players.Neutral, obj.Position, angle) == null)
            return;
        float particleAngle = angle + FloatSetting("BridgeParticleAngleFudge") * MathF.PI / 180;
        Game.Events.ParticleSystem(Setting("BridgeParticle"), obj.Position);
        _ = particleAngle;  // the client particle has no orientation input yet
        Game.Terrain.Bridges.Delete(oldBridge, Game);
    }

    public int Update()
    {
        var wave = Owner;
        if (needDisable)
        {
            needDisable = false;
            wave.SetDisabled("DEFAULT");
            return 0;
        }
        if (wave.IsDisabled)
            return 0;
        if (activeFrame == 0)
            activeFrame = Frame;
        if (Frame - activeFrame < FramesSetting("WaveDelay"))
            return 0;
        if (!initialized)
        {
            if (!InitWaveGuide())
            {
                Game.DestroyObject(wave);
                return 0;
            }
            initialized = true;
        }
        if (Frame - splashSoundFrame > LogicTime.FramesPerSecond / 2f)
        {
            splashSoundFrame = Frame;
            if (Game.Random.Int(1, 100) > IntSetting("RandomSplashSoundFrequency"))
                Game.Events.PlaySound(Setting("RandomSplashSound"), wave.Position, source: wave);
        }
        TransformWaveShape();
        if (MathUtil.DistanceSquared2D(finalDestination, wave.Position) <= PathExtraDistance * PathExtraDistance)
        {
            Game.Events.ParticleSystem("WaveSplash01", wave.Position);
            Game.DestroyObject(wave);
            return 0;
        }
        DoShoreEffects();
        DoDamage();
        return 0;
    }
}

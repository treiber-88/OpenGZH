using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Client.Effects;

/// <summary>What playing FX lists needs from the client (sound, camera, terrain, bones, fog).</summary>
public interface IFxHost
{
    ParticleSystemManager Particles { get; }
    void PlaySound(string name, Vector3? position, GameObject? source);
    void Shake(Vector3 epicenter, string type);
    void Scorch(Vector3 position, float radius, int type);
    void LightPulse(Vector3 position, Vector3 color, float radius, int increaseFrames, int decreaseFrames);
    void Tracer(Vector3 from, Vector3 to, float speed, float length, float width, Vector3 color, float decayAt);
    /// <summary>Position FX are skipped where the local player's view is not clear.</summary>
    bool IsPointVisible(Vector3 position);
    /// <summary>Object FX are skipped when the object is fogged or shrouded for the local player.</summary>
    bool IsObjectVisible(GameObject obj);
    Matrix4x4 ObjectTransform(GameObject obj);
    float LayerHeight(float x, float y);
    /// <summary>A bone's world transform on the object as drawn, if it has it.</summary>
    bool TryGetBone(GameObject obj, string bone, out Matrix4x4 world);
}

/// <summary>
/// Plays FX lists from fx-lists.yaml (the original's FXList): each is a list of nuggets — sounds,
/// particle systems, camera shakes, scorch marks, light pulses, tracers, and FX at bones.
/// </summary>
public sealed class FxPlayer
{
    readonly IFxHost host;
    readonly Func<string, RuleNode?> findFxList;
    readonly Dictionary<string, List<RuleNode>?> lists = new(StringComparer.OrdinalIgnoreCase);

    public FxPlayer(IFxHost host, Func<string, RuleNode?> findFxList)
    {
        this.host = host;
        this.findFxList = findFxList;
    }

    List<RuleNode>? Nuggets(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Equals("None", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!lists.TryGetValue(name, out var nuggets))
        {
            nuggets = findFxList(name) is { } node && node.TryGet("Nuggets", out var list) ? list.AsList().ToList() : null;
            lists[name] = nuggets;
        }
        return nuggets;
    }

    /// <summary>The original's FXList::doFXPos: at a point, oriented by a matrix, towards a second point.</summary>
    public void DoFxPos(string name, Vector3 primary, Matrix4x4? transform = null, float speed = 0, Vector3? secondary = null, float overrideRadius = 0)
    {
        if (Nuggets(name) is not { } nuggets || !host.IsPointVisible(primary))
            return;
        foreach (var nugget in nuggets)
            DoNuggetPos(nugget, primary, transform, speed, secondary, overrideRadius);
    }

    /// <summary>The original's FXList::doFXObj: at an object (nuggets may attach to it).</summary>
    public void DoFxObj(string name, GameObject primary, GameObject? secondary = null)
    {
        if (Nuggets(name) is not { } nuggets || !host.IsObjectVisible(primary))
            return;
        foreach (var nugget in nuggets)
            DoNuggetObj(nugget, primary, secondary);
    }

    /// <summary>A logic request: object form when it is at the object itself, position form otherwise.</summary>
    public void Play(FxRequest request)
    {
        if (request.Object is { } obj && request.Secondary == null && Vector3.DistanceSquared(obj.Position, request.Position) < 0.01f)
            DoFxObj(request.Name, obj, request.SecondaryObject);
        else
            DoFxPos(request.Name, request.Position, request.Transform ?? (request.Object != null ? host.ObjectTransform(request.Object) : null),
                request.Speed, request.Secondary, request.OverrideRadius);
    }

    static string Kind(RuleNode nugget) => nugget.GetString("Type");

    void DoNuggetPos(RuleNode n, Vector3 primary, Matrix4x4? transform, float speed, Vector3? secondary, float overrideRadius)
    {
        switch (Kind(n))
        {
            case "Sound":
                host.PlaySound(n.GetString("Name"), primary, null);
                break;
            case "Tracer":
                DoTracer(n, primary, speed, secondary);
                break;
            case "LightPulse":
                host.LightPulse(primary, ParticleSystemTemplate.ParseColor(n.GetString("Color")), RuleValue.Float(n.GetString("Radius")),
                    RuleUnits.Duration(n.GetString("IncreaseTime")), RuleUnits.Duration(n.GetString("DecreaseTime")));
                break;
            case "ViewShake":
                host.Shake(primary, n.GetString("Shake", "NORMAL"));
                break;
            case "TerrainScorch":
                DoScorch(n, primary);
                break;
            case "ParticleSystem":
                DoParticleSystem(n, primary, transform, null, overrideRadius);
                break;
            // FXListAtBonePos only works on objects.
        }
    }

    void DoNuggetObj(RuleNode n, GameObject primary, GameObject? secondary)
    {
        switch (Kind(n))
        {
            case "Sound":
                host.PlaySound(n.GetString("Name"), primary.Position, primary);
                break;
            case "LightPulse":
            {
                float radius = RuleValue.Float(n.GetString("Radius"));
                float pct = ParsePercent(n.GetString("RadiusAsPercentOfObjectSize"));
                if (pct > 0)
                    radius = primary.BoundingCircleRadius * pct;
                host.LightPulse(primary.Position, ParticleSystemTemplate.ParseColor(n.GetString("Color")), radius,
                    RuleUnits.Duration(n.GetString("IncreaseTime")), RuleUnits.Duration(n.GetString("DecreaseTime")));
                break;
            }
            case "ParticleSystem":
                if (RuleValue.Bool(n.GetString("Ricochet")) && secondary != null)
                {
                    float aim = MathF.Atan2(primary.Position.Y - secondary.Position.Y, primary.Position.X - secondary.Position.X);
                    DoParticleSystem(n, primary.Position, Matrix4x4.CreateRotationZ(aim), primary, 0);
                }
                else
                    DoParticleSystem(n, primary.Position, host.ObjectTransform(primary), primary, 0);
                break;
            case "FXListAtBonePos":
                DoFxAtBones(n, primary);
                break;
            default:
                // Everything else behaves as at the object's position with its orientation.
                DoNuggetPos(n, primary.Position, host.ObjectTransform(primary), 0, secondary?.Position, 0);
                break;
        }
    }

    static float ParsePercent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        return RuleValue.Float(text.Trim().TrimEnd('%')) / 100f;
    }

    static RandomVariable Random(RuleNode n, string key, float fallback)
    {
        var w = RuleValue.Words(n.GetString(key));
        if (w.Length == 0)
            return new RandomVariable(fallback, fallback);
        float low = RuleValue.Float(w[0]);
        return new RandomVariable(low, w.Length > 1 ? RuleValue.Float(w[1]) : low);
    }

    void DoParticleSystem(RuleNode n, Vector3 primary, Matrix4x4? transform, GameObject? attachTo, float overrideRadius)
    {
        var offset = ParticleSystemTemplate.ParseCoord(n.GetString("Offset"));
        if (transform is { } m)
            offset = Vector3.TransformNormal(offset, m);
        var template = host.Particles.FindTemplate(n.GetString("Name"));
        if (template == null)
            return;
        int count = Math.Max(0, RuleValue.Int(n.GetString("Count"), 1));
        var radiusVar = Random(n, "Radius", 0);
        var heightVar = Random(n, "Height", 0);
        var delayVar = Random(n, "InitialDelay", -1);
        float rotateX = RuleUnits.Angle(n.GetString("RotateX"));
        float rotateY = RuleUnits.Angle(n.GetString("RotateY"));
        float rotateZ = RuleUnits.Angle(n.GetString("RotateZ"));
        bool orient = RuleValue.Bool(n.GetString("OrientToObject"));
        bool attach = RuleValue.Bool(n.GetString("AttachToObject"));
        bool atGround = RuleValue.Bool(n.GetString("CreateAtGroundHeight"));
        bool callersRadius = RuleValue.Bool(n.GetString("UseCallersRadius"));
        for (int i = 0; i < count; i++)
        {
            var system = host.Particles.CreateSystem(template);
            float radius = radiusVar.Value;
            float angle = ClientRandom.Real(0, MathF.Tau);
            var pos = new Vector3(primary.X + offset.X + radius * MathF.Cos(angle), primary.Y + offset.Y + radius * MathF.Sin(angle), 0);
            pos.Z = atGround ? host.LayerHeight(pos.X, pos.Y) : primary.Z + offset.Z + heightVar.Value;
            if (orient && transform is { } t)
                system.SetLocalTransform(t);
            if (rotateX != 0) system.RotateLocalTransformX(rotateX);
            if (rotateY != 0) system.RotateLocalTransformY(rotateY);
            if (rotateZ != 0) system.RotateLocalTransformZ(rotateZ);
            if (attach && attachTo != null)
                system.AttachToObject(attachTo);
            else
                system.SetPosition(pos);
            float delayMs = delayVar.Value;
            if (delayMs >= 0)
                system.SetInitialDelay((int)MathF.Ceiling(delayMs * LogicTime.FramesPerSecond / 1000f));
            if (callersRadius && overrideRadius != 0)
            {
                if (system.VolumeType == EmissionVolume.Sphere)
                    system.SetEmissionVolumeSphereRadius(overrideRadius);
                else if (system.VolumeType == EmissionVolume.Cylinder)
                    system.SetEmissionVolumeCylinderRadius(overrideRadius);
            }
        }
    }

    void DoScorch(RuleNode n, Vector3 primary)
    {
        int type = n.GetString("Scorch", "RANDOM").ToUpperInvariant() switch
        {
            "SCORCH_1" => 0,
            "SCORCH_2" => 1,
            "SCORCH_3" => 2,
            "SCORCH_4" => 3,
            "SHADOW_SCORCH" => 4,
            _ => ClientRandom.Int(0, 3),
        };
        host.Scorch(primary, RuleValue.Float(n.GetString("Radius")), type);
    }

    void DoTracer(RuleNode n, Vector3 primary, float primarySpeed, Vector3? secondary)
    {
        float probability = RuleValue.Float(n.GetString("Probability"), 1);
        if (probability <= ClientRandom.Real(0, 1) || secondary is not { } target)
            return;
        float speed = n.Has("Speed") ? RuleUnits.Velocity(n.GetString("Speed")) : 0;
        if (speed == 0)
            speed = primarySpeed;
        var color = n.Has("Color") ? ParticleSystemTemplate.ParseColor(n.GetString("Color")) : Vector3.One;
        host.Tracer(primary, target, speed, RuleValue.Float(n.GetString("Length"), 10), RuleValue.Float(n.GetString("Width"), 1), color,
            RuleValue.Float(n.GetString("DecayAt"), 1));
    }

    void DoFxAtBones(RuleNode n, GameObject obj)
    {
        string fx = n.GetString("FX");
        string bone = n.GetString("BoneName");
        // The unnumbered bone, then BONE01, BONE02, ... (up to 40) as the original does.
        if (host.TryGetBone(obj, bone, out var single))
            DoFxPos(fx, single.Translation, single);
        for (int i = 1; i <= 40; i++)
        {
            if (!host.TryGetBone(obj, $"{bone}{i:00}", out var world))
                break;
            DoFxPos(fx, world.Translation, world);
        }
    }
}

using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Combat;

/// <summary>
/// Runs Object Creation Lists ("OCL_..." in the rules): ordered lists of "nuggets" that create objects,
/// debris or weapon blasts. Used by deaths (wreckage, flying turrets), upgrades (drones), special powers
/// (paradrops, airstrikes) and weapons.
/// </summary>
public sealed class ObjectCreationSystem
{
    readonly GameLogic game;

    static readonly FlagNames Dispositions = new("Disposition",
        ["LIKE_EXISTING", "ON_GROUND_ALIGNED", "SEND_IT_FLYING", "SEND_IT_UP", "SEND_IT_OUT", "RANDOM_FORCE", "FLOATING", "INHERIT_VELOCITY", "WHIRLING"]);

    public ObjectCreationSystem(GameLogic game) => this.game = game;

    /// <summary>
    /// Runs an OCL. <paramref name="source"/> is usually the dying or upgrading object. Objects appear
    /// at <paramref name="position"/>; nuggets that go somewhere (DeliverPayload, Attack) head for
    /// <paramref name="secondary"/>, which defaults to the same point. With <paramref name="createOwner"/>
    /// false, DeliverPayload uses the source itself as the delivery vehicle (the original's USE_OWNER_OBJECT).
    /// </summary>
    public List<GameObject> Run(string oclName, GameObject? source, Vector3 position, GameObject? target = null,
        Vector3? secondary = null, bool createOwner = true)
    {
        var created = new List<GameObject>();
        var ocl = game.Catalog.ObjectCreationList(oclName);
        if (ocl == null)
        {
            if (!string.IsNullOrEmpty(oclName) && !oclName.Equals("None", StringComparison.OrdinalIgnoreCase))
                Log.Warning($"Unknown object creation list '{oclName}'.");
            return created;
        }

        if (!ocl.TryGet("Nuggets", out var nuggets))
            return created;

        foreach (var nugget in nuggets.AsList())
        {
            switch (nugget.GetString("Type"))
            {
                case "CreateObject":
                    created.AddRange(CreateObjects(nugget, source, position, isDebris: false));
                    break;
                case "CreateDebris":
                    created.AddRange(CreateObjects(nugget, source, position, isDebris: true));
                    break;
                case "FireWeapon":
                    game.Damage.FireTemporaryWeapon(nugget.GetString("Weapon"), source, position);
                    break;
                case "ApplyRandomForce":
                    ApplyRandomForce(nugget, source);
                    break;
                case "DeliverPayload":
                    created.AddRange(game.PayloadDelivery.Deliver(nugget, source, position, secondary, target, createOwner));
                    break;
                case "Attack":
                    game.PayloadDelivery.Attack(nugget, source, secondary ?? position);
                    break;
            }
        }
        return created;
    }

    IEnumerable<GameObject> CreateObjects(RuleNode nugget, GameObject? source, Vector3 position, bool isDebris)
    {
        if (RuleValue.Bool(nugget.GetString("RequiresLivePlayer")) && source != null && source.Owner.IsDefeated)
            yield break;
        if (RuleValue.Bool(nugget.GetString("SkipIfSignificantlyAirborne")) && source != null && source.IsAirborneTarget)
            yield break;

        string[] names = RuleValue.Words(nugget.GetString(isDebris ? "ModelNames" : "ObjectNames"));
        if (names.Length == 0)
            yield break;

        int count = Math.Max(1, RuleValue.Int(nugget.GetString("Count"), 1));
        var disposition = Dispositions.Parse(nugget.GetString("Disposition", "ON_GROUND_ALIGNED"));
        float intensity = RuleValue.Float(nugget.GetString("DispositionIntensity"), 0);
        var offset = RuleValue.Coord(nugget.GetString("Offset"));
        var owner = source?.Owner ?? game.Players.Neutral;
        float orientation = source?.Orientation ?? 0;

        // Offsets are relative to the source's facing.
        var rotated = Vector3.Transform(offset, Matrix4x4.CreateRotationZ(orientation));
        var basePosition = position + rotated;

        for (int i = 0; i < count; i++)
        {
            string name = names[game.Random.Int(0, names.Length - 1)];
            var obj = isDebris
                ? game.CreateObject("GenericDebris", owner, basePosition, orientation)
                : game.CreateObject(name, owner, basePosition, orientation);
            if (obj == null)
                continue;

            if (isDebris)
                obj.ModelOverride = name;
            if (source != null)
                obj.ProducerId = source.Id;
            if (RuleValue.Bool(nugget.GetString("InheritsVeterancy")) && source != null)
                obj.SetVeterancy(source.Veterancy);

            ApplyLifetime(nugget, obj);
            ApplyDisposition(nugget, disposition, intensity, source, obj, basePosition, orientation);

            if (RuleValue.Bool(nugget.GetString("ContainInsideSourceObject")) && source?.FindModule<Modules.Contain.ContainModule>() is { } contain)
                contain.AddPassenger(obj);

            yield return obj;
        }
    }

    void ApplyLifetime(RuleNode nugget, GameObject obj)
    {
        if (!nugget.Has("MinLifetime") && !nugget.Has("MaxLifetime"))
            return;
        int min = RuleUnits.Duration(nugget.GetString("MinLifetime"));
        int max = RuleUnits.Duration(nugget.GetString("MaxLifetime"), RuleValue.Float(nugget.GetString("MinLifetime")));
        obj.FindModule<LifetimeUpdate>()?.SetLifetime(game.Random.Int(min, Math.Max(min, max)));
    }

    void ApplyDisposition(RuleNode nugget, FlagSet disposition, float intensity, GameObject? source, GameObject obj, Vector3 position, float orientation)
    {
        bool Has(string name) => disposition.Has(Dispositions.IndexOf(name));
        var physics = obj.FindModule<PhysicsBehavior>();
        float mass = RuleValue.Float(nugget.GetString("Mass"), 0);
        if (physics != null && mass > 0)
            physics.Mass = mass;

        if (Has("INHERIT_VELOCITY") && source?.FindModule<PhysicsBehavior>() is { } sourcePhysics && physics != null)
            physics.ApplyForce(sourcePhysics.Velocity);

        if (Has("LIKE_EXISTING"))
        {
            obj.SetOrientation(orientation);
            obj.SetPosition(position);
            if (source != null && source.Position.Z > game.Terrain.GroundHeight(source.Position) + 1 && physics != null)
                physics.AllowToFall = true;
        }

        if (Has("ON_GROUND_ALIGNED"))
        {
            obj.SetOrientation(game.Random.Float(0, MathF.Tau));
            obj.SetPosition(position with { Z = game.Terrain.LayerHeight(position.X, position.Y) });
        }

        if (Has("SEND_IT_OUT"))
        {
            obj.SetOrientation(game.Random.Float(0, MathF.Tau));
            obj.SetPosition(position with { Z = game.Terrain.GroundHeight(position) });
            if (physics != null)
            {
                float horizontal = 4f * intensity;
                physics.ApplyForce(new Vector3(game.Random.Float(-horizontal, horizontal), game.Random.Float(-horizontal, horizontal), 0));
            }
        }

        if ((Has("SEND_IT_FLYING") || Has("SEND_IT_UP") || Has("RANDOM_FORCE")) && physics != null)
        {
            obj.SetPosition(position);
            physics.AllowBouncing = true;
            physics.ExtraBounciness = RuleValue.Float(nugget.GetString("ExtraBounciness"));

            float spin = RuleValue.Float(nugget.GetString("SpinRate"), -1);
            spin = spin >= 0 ? spin : MathF.PI / 32f * intensity;
            physics.YawRate = game.Random.Float(-YawLike(nugget, "YawRate", spin), YawLike(nugget, "YawRate", spin));
            physics.RollRate = game.Random.Float(-YawLike(nugget, "RollRate", spin), YawLike(nugget, "RollRate", spin));
            physics.PitchRate = game.Random.Float(-YawLike(nugget, "PitchRate", spin), YawLike(nugget, "PitchRate", spin));

            Vector3 force;
            if (Has("SEND_IT_FLYING"))
            {
                float horizontal = 4f * intensity, vertical = 3f * intensity;
                force = new Vector3(game.Random.Float(-horizontal, horizontal), game.Random.Float(-horizontal, horizontal), game.Random.Float(vertical * 0.33f, vertical));
            }
            else if (Has("SEND_IT_UP"))
            {
                float horizontal = 2f * intensity, vertical = 4f * intensity;
                force = new Vector3(game.Random.Float(-horizontal, horizontal), game.Random.Float(-horizontal, horizontal), game.Random.Float(vertical * 0.75f, vertical));
            }
            else
            {
                force = RandomForce(nugget);
            }
            physics.ApplyForce(force);
            if (RuleValue.Bool(nugget.GetString("OrientInForceDirection")))
                obj.SetOrientation(MathF.Atan2(force.Y, force.X));
        }

        if (Has("WHIRLING") && physics != null)
        {
            physics.YawRate = game.Random.Float(-intensity, intensity);
            physics.RollRate = game.Random.Float(-intensity, intensity);
            physics.PitchRate = game.Random.Float(-intensity, intensity);
        }
    }

    static float YawLike(RuleNode nugget, string key, float fallback)
    {
        float value = RuleValue.Float(nugget.GetString(key), -1);
        return value >= 0 ? value : fallback;
    }

    Vector3 RandomForce(RuleNode nugget)
    {
        float min = RuleValue.Float(nugget.GetString("MinForceMagnitude"));
        float max = RuleValue.Float(nugget.GetString("MaxForceMagnitude"), min);
        float minPitch = RuleUnits.Angle(nugget.GetString("MinForcePitch"));
        float maxPitch = RuleUnits.Angle(nugget.GetString("MaxForcePitch"), RuleValue.Float(nugget.GetString("MinForcePitch")));
        float magnitude = game.Random.Float(min, max);
        float pitch = game.Random.Float(minPitch, maxPitch);
        float yaw = game.Random.Float(0, MathF.Tau);
        return new Vector3(MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch)) * magnitude;
    }

    void ApplyRandomForce(RuleNode nugget, GameObject? source)
    {
        if (source?.FindModule<PhysicsBehavior>() is not { } physics)
            return;
        float spin = RuleValue.Float(nugget.GetString("SpinRate"));
        physics.YawRate = game.Random.Float(-spin, spin);
        physics.ApplyForce(RandomForce(nugget));
    }
}

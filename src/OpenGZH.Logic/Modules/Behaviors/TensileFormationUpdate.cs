using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// A chunk of an avalanche (the original's TensileFormationUpdate): chunks block the way until one is
/// damaged; then it cracks (CrackSound), slides down the slope pulled by its four nearest neighbours,
/// knocks trees over, keeps loosening the chunks around it, and after 300 frames settles as rubble.
/// </summary>
public sealed class TensileFormationUpdate : Module, IUpdateModule, ICreateModule
{
    readonly (uint Id, Vector3 Tensor)[] links = new (uint, Vector3)[4];
    bool linksInited, enabled;
    Vector3 inertia;
    int life;
    float lowestSlideElevation = 255;

    public void OnCreate()
    {
        enabled = BoolSetting("Enabled");
        Game.Pathfinder.AddObstacle(Owner);
    }

    public void SetEnabled(bool on) => enabled = on;

    static TensileFormationUpdate? Tfu(GameObject obj) => obj.FindModule<TensileFormationUpdate>();

    /// <summary>Remembers the nearest other chunks and where they sat (the original's initLinks).</summary>
    void InitLinks()
    {
        linksInited = true;
        var obj = Owner;
        float closest = 99999.9f;
        foreach (var other in Game.ObjectsInRadius(obj.Position, 1000).Where(o => o != obj && Tfu(o) != null))
        {
            var delta = other.Position - obj.Position;
            float d = delta.Length();
            if (closest > d)
            {
                closest = d;
                for (int t = 3; t > 0; --t)
                    links[t] = links[t - 1];
                links[0] = (other.Id, delta);
            }
        }
        obj.SetOrientation(Game.Random.Float(-MathF.PI, MathF.PI));
    }

    public int Update()
    {
        if (!linksInited)
            InitLinks();
        var obj = Owner;
        if (!enabled)
        {
            if (obj.Body is { DamageState: >= BodyDamageState.Damaged })
            {
                enabled = true;
                Game.Pathfinder.RemoveObstacle(obj);
                Game.Events.PlaySound(Setting("CrackSound"), obj.Position, source: obj);
            }
            else
                return 30;
        }
        if (++life > 300)
        {
            obj.SetCondition("MOVING", false);
            obj.SetCondition("FREEFALL", false);
            obj.SetCondition("POST_COLLAPSE", false);
            obj.Body?.SetDamageState(BodyDamageState.Rubble);
            Game.Pathfinder.AddObstacle(obj);
            return LogicTime.SleepForever;
        }
        if (life % 30 == 29)
            PropagateDislodgement();

        var pos = obj.Position;
        var normal = Game.Terrain.GroundNormal(pos.X, pos.Y);
        var slope = new Vector3(normal.X, normal.Y, 0) * (0.3f + (1 - normal.Z));
        inertia = (inertia + slope) * 0.95f;
        var newPos = new Vector3(pos.X + inertia.X, pos.Y + inertia.Y, 0);
        newPos.Z = Game.Terrain.GroundHeight(newPos);

        // Trees in the way are knocked over.
        var tree = Game.ObjectsInRadius(newPos, obj.Template.GeometryMajorRadius)
            .Where(o => o != obj).OrderBy(o => MathUtil.DistanceSquared2D(o.Position, newPos)).FirstOrDefault();
        if (tree != null && tree.IsKindOf(Kind.Shrubbery))
            tree.FindModule<ToppleUpdate>()?.Topple(inertia, inertia.Length(), ToppleUpdate.Options.NoBounce);

        foreach (var (id, tensor) in links)
        {
            if (Game.FindObject(id) is not { } other)
                continue;
            var desiredPos = other.Position - tensor;
            newPos.X = newPos.X * 0.93f + desiredPos.X * 0.07f;
            newPos.Y = newPos.Y * 0.93f + desiredPos.Y * 0.07f;
            newPos.Z = Math.Min(lowestSlideElevation, Game.Terrain.GroundHeight(newPos));
        }

        obj.SetCondition("POST_COLLAPSE");
        obj.SetCondition("MOVING", life < 200);
        obj.SetCondition("FREEFALL", MathF.Abs(pos.Z - newPos.Z) > 0.2f && life < 100);
        lowestSlideElevation = newPos.Z;
        obj.SetPosition(newPos);
        return 0;
    }

    /// <summary>Nearby chunks and the linked ones come loose too.</summary>
    void PropagateDislodgement()
    {
        foreach (var other in Game.ObjectsInRadius(Owner.Position, 100).Where(o => Tfu(o) != null).ToList())
            other.Body?.SetDamageState(BodyDamageState.Damaged);
        foreach (var (id, _) in links)
            Game.FindObject(id)?.Body?.SetDamageState(BodyDamageState.Damaged);
    }
}

using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// China's horde bonus (the original's HordeUpdate): a unit with Count - 1 or more like members
/// (KindOf, allied, ExactMatch template) within Radius is a true horde member; one that is not but
/// stands within RubOffRadius of a true member counts as in the horde too. The AI then sets the
/// HORDE / NATIONALISM / FANATICISM weapon bonuses.
/// </summary>
public sealed class HordeUpdate : Module, IUpdateModule, ICreateModule
{
    FlagSet kindOf;
    int lastHordeRefreshFrame;

    public bool IsInHorde { get; private set; }
    public bool IsTrueHordeMember { get; private set; }
    public bool IsAllowedNationalism => BoolSetting("AllowedNationalism", true);

    int UpdateRate => Definition.Settings.Has("UpdateRate") ? FramesSetting("UpdateRate") : LogicTime.FramesPerSecond;

    public void OnCreate()
    {
        kindOf = Flags.KindOf.Parse(Setting("KindOf"));
        lastHordeRefreshFrame = Frame;
        Owner.SleepUpdate(this, Game.Random.Int(1, UpdateRate));
    }

    bool IsHordeMember(GameObject other)
    {
        if (other == Owner)
            return false;
        if (BoolSetting("ExactMatch") && other.Template != Owner.Template)
            return false;
        if (other.FindModule<HordeUpdate>() == null || !other.KindOf.HasAll(kindOf))
            return false;
        if (BoolSetting("AlliesOnly", true) && Owner.RelationshipTo(other) != Relationship.Allies)
            return false;
        return Owner.IsOffMap == other.IsOffMap;
    }

    public int Update()
    {
        var obj = Owner;
        bool isInfantry = obj.IsKindOf(Kind.Infantry);
        int rate = UpdateRate;
        if (isInfantry || Frame > lastHordeRefreshFrame + rate)
        {
            lastHordeRefreshFrame = Frame;
            float minDist = FloatSetting("Radius");
            // FROM_BOUNDINGSPHERE_3D: the gap between the two bounding spheres.
            var members = new List<GameObject>();
            foreach (var other in Game.ObjectsInRadius(obj.Position, minDist + obj.BoundingSphereRadius + 50))
            {
                if (!IsHordeMember(other))
                    continue;
                float gap = Vector3.Distance(obj.Position, other.Position) - obj.BoundingSphereRadius - other.BoundingSphereRadius;
                if (gap <= minDist)
                    members.Add(other);
            }
            if (members.Count >= IntSetting("Count") - 1)
            {
                IsInHorde = true;
                IsTrueHordeMember = true;
            }
            else
            {
                IsInHorde = false;
                IsTrueHordeMember = false;
                float rubOff = FloatSetting("RubOffRadius", 20);
                foreach (var other in members)
                {
                    if (other.FindModule<HordeUpdate>() is { IsTrueHordeMember: true }
                        && MathUtil.DistanceSquared2D(obj.Position, other.Position) <= rubOff * rubOff)
                    {
                        IsInHorde = true;
                        break;
                    }
                }
            }
            obj.AI?.EvaluateMoraleBonus();
        }
        return isInfantry ? rate : 0;
    }
}

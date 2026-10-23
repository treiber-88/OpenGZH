using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// Computer-controlled infantry that are idle and below NeverHeal health walk to the closest heal pad
/// within ScanRange, checking every ScanRate (the original's AutoFindHealingUpdate). Human players'
/// units never do this.
/// </summary>
public sealed class AutoFindHealingUpdate : Module, IUpdateModule
{
    int nextScanFrames;

    public int Update()
    {
        var obj = Owner;
        if (obj.Owner.IsHuman)
            return 0;
        if (nextScanFrames > 0)
        {
            nextScanFrames--;
            return 0;
        }
        nextScanFrames = FramesSetting("ScanRate");
        if (obj.AI is not { } ai || obj.Body is not { } body)
            return 0;
        if (body.Health > body.MaxHealth * FloatSetting("NeverHeal", 0.95f))
            return 0;
        // (The original returns here for busy units before its AlwaysHeal test, so AlwaysHeal never matters.)
        if (!ai.IsIdle)
            return 0;
        if (ScanClosestTarget() is { } healUnit && CanGetHealedAt(healUnit))
            ai.Enter(healUnit, Commands.CommandSource.AI);
        return 0;
    }

    GameObject? ScanClosestTarget()
    {
        GameObject? best = null;
        float closest = 0;
        foreach (var other in Game.ObjectsInRadius(Owner.Position, FloatSetting("ScanRange")))
        {
            if (!other.IsKindOf("HEAL_PAD"))
                continue;
            float d = MathUtil.DistanceSquared2D(Owner.Position, other.Position);
            if (best == null || d < closest)
            {
                best = other;
                closest = d;
            }
        }
        return best;
    }

    /// <summary>The original's ActionManager::canGetHealedAt.</summary>
    bool CanGetHealedAt(GameObject depot) =>
        Owner.RelationshipTo(depot) == Players.Relationship.Allies && !depot.IsEffectivelyDead
        && !Owner.IsUnderConstruction && !depot.IsUnderConstruction && !depot.HasStatus(Flags.ObjectStatus.IndexOf("SOLD"))
        && Owner.IsKindOf(Kind.Infantry) && depot.IsKindOf("HEAL_PAD")
        && !(Owner.Body is { } body && body.Health == body.MaxHealth);
}

using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// AI for delivery aircraft (airstrikes, paradrops, supply drops): fly to the target, drop the payload
/// objects (each dropped one is given the weapon/parachute behaviour from its own definition),
/// then fly off the map and disappear.
/// </summary>
public sealed class DeliverPayloadAIUpdate : AIUpdateInterface
{
    Vector3 target;
    string[] payload = [];
    int payloadCount;
    int dropDelay;
    int nextDropFrame;
    int dropped;
    bool configured;
    bool leaving;
    float deliveryDistance;

    /// <summary>Called by the DeliverPayload nugget right after the aircraft is created.</summary>
    public void Configure(RuleNode nugget, Vector3 moveTo, Vector3 aimAt, GameObject? targetObject, bool firstInFormation)
    {
        target = moveTo;
        // "Payload: ObjectName count" (possibly several lines).
        var items = new List<string>();
        if (nugget.TryGet("Payload", out var lines))
        {
            foreach (var line in lines.AsList())
            {
                string[] words = RuleValue.Words(line.Value);
                if (words.Length == 0)
                    continue;
                int count = words.Length > 1 ? RuleValue.Int(words[1], 1) : 1;
                for (int i = 0; i < count; i++)
                    items.Add(words[0]);
            }
        }
        payload = items.ToArray();
        payloadCount = payload.Length;
        dropDelay = RuleUnits.Duration(nugget.GetString("DropDelay"), 100);
        deliveryDistance = RuleValue.Float(nugget.GetString("DeliveryDistance"), 0);
        configured = true;
        MoveTo(target, CommandSource.AI);
    }

    public override int Update()
    {
        if (!configured)
            return base.Update();

        float distance = MathUtil.Distance2D(Owner.Position, target);
        if (!leaving && distance <= Math.Max(deliveryDistance, 30) && dropped < payloadCount && Frame >= nextDropFrame)
        {
            var obj = Game.CreateObject(payload[dropped], Owner.Owner, Owner.Position - new Vector3(0, 0, 5), Owner.Orientation);
            if (obj != null)
                obj.ProducerId = Owner.ProducerId;
            dropped++;
            nextDropFrame = Frame + dropDelay;
        }

        if (!leaving && (dropped >= payloadCount || IsIdle && distance < 60))
        {
            leaving = true;
            var away = Owner.Position + MathUtil.Forward(Owner.Orientation) * 3000;
            MoveTo(away, CommandSource.AI);
        }

        if (leaving && !Game.Terrain.IsInsidePlayableArea(Owner.Position.X, Owner.Position.Y)
            && (Owner.Position.X < -200 || Owner.Position.Y < -200 || Owner.Position.X > Game.Terrain.WorldWidth + 200 || Owner.Position.Y > Game.Terrain.WorldHeight + 200))
        {
            Game.DestroyObject(Owner);
            return 1000;
        }

        base.Update();
        if (leaving && IsIdle)
            Game.DestroyObject(Owner);
        return 0;
    }
}

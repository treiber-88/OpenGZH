using System.Numerics;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// AI of vehicles that carry troops (Humvee, Ambulance, Troop Crawler, Battle Bus...). When the
/// player or a script orders an attack, passengers allowed to shoot out join in (portable structures
/// such as Overlord add-ons don't while hacked, EMP'd, subdued or paralyzed). Passengers can always get out.
/// </summary>
public class TransportAIUpdate : AIUpdateInterface
{
    public override void AttackObject(GameObject target, CommandSource source = CommandSource.Player, int maxShots = -1)
    {
        foreach (var passenger in PassengersWhoJoinIn(source))
            passenger.AI!.AttackObject(target, source, maxShots);
        base.AttackObject(target, source, maxShots);
    }

    public override void AttackPosition(Vector3 position, CommandSource source = CommandSource.Player, int maxShots = -1)
    {
        foreach (var passenger in PassengersWhoJoinIn(source))
            passenger.AI!.AttackPosition(position, source, maxShots);
        base.AttackPosition(position, source, maxShots);
    }

    public override bool IsFreeToExit(GameObject rider) => true;

    List<GameObject> PassengersWhoJoinIn(CommandSource source)
    {
        if (Owner.Contain is not { } contain || source is not (CommandSource.Player or CommandSource.Script))
            return [];
        return contain.Passengers
            .Where(p => contain.IsPassengerAllowedToFire(p) && p.AI != null)
            .Where(p => !p.IsKindOf("PORTABLE_STRUCTURE")
                || !(p.IsDisabledBy("DISABLED_HACKED") || p.IsDisabledBy("DISABLED_EMP") || p.IsDisabledBy("DISABLED_SUBDUED") || p.IsDisabledBy("DISABLED_PARALYZED")))
            .ToList();
    }
}

/// <summary>Ambles about at random whenever it has nothing to do (civilian animals and crowds).</summary>
public sealed class WanderAIUpdate : AIUpdateInterface
{
    public override int Update()
    {
        if (IsIdle)
        {
            var destination = Owner.Position;
            destination.X += Game.Random.Int(5, 50);
            destination.Y += Game.Random.Int(5, 50);
            MoveTo(destination, CommandSource.AI);
        }
        base.Update();
        return 0;
    }
}

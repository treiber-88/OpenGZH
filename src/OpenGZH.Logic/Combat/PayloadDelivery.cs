using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Combat;

/// <summary>
/// The "DeliverPayload" and "Attack" OCL nuggets. DeliverPayload creates a formation of aircraft
/// (Transport) at the start point, which fly to the target, drop their Payload (bombs, paratroopers,
/// supplies) and fly away; used by airstrikes, paradrops and supply drops. Attack makes the source
/// fire a number of shots at the target point (e.g. the Battleship's bombardment).
/// The formation layout copies the original's arithmetic exactly.
/// </summary>
public sealed class PayloadDelivery
{
    readonly GameLogic game;

    public PayloadDelivery(GameLogic game) => this.game = game;

    public IEnumerable<GameObject> Deliver(RuleNode nugget, GameObject? source, Vector3 start, Vector3? targetPoint, GameObject? targetObject, bool createOwner)
    {
        if (source == null)
            yield break;
        // Called without a separate target (e.g. from a death), the aircraft come in from the nearest edge.
        var target = targetPoint ?? start;
        if (targetPoint == null)
            start = game.Terrain.ClosestEdgePoint(target);

        var owner = source.Owner;
        string transportName = RuleWords.First(nugget.GetString("Transport"));
        int formationSize = Math.Max(1, RuleValue.Int(nugget.GetString("FormationSize"), 1));
        float spacing = RuleValue.Float(nugget.GetString("FormationSpacing"), 25);
        float convergence = RuleValue.Float(nugget.GetString("WeaponConvergenceFactor"));
        float errorRadius = RuleValue.Float(nugget.GetString("WeaponErrorRadius"));
        int delayMax = RuleUnits.Duration(nugget.GetString("DelayDeliveryMax"));
        float distToTarget = RuleValue.Float(nugget.GetString("DeliveryDistance"));
        bool startAtPreferredHeight = RuleValue.Bool(nugget.GetString("StartAtPreferredHeight"), true);
        bool startAtMaxSpeed = RuleValue.Bool(nugget.GetString("StartAtMaxSpeed"));

        // Sideways offsets for the formation: the direction from target to start, turned 90 degrees
        // each way and added to itself (as the original does).
        float ccwX = 0, ccwY = 0, cwX = 0, cwY = 0;
        if (formationSize > 1)
        {
            float dx = start.X - target.X, dy = start.Y - target.Y;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (length > 0)
            {
                dx /= length;
                dy /= length;
            }
            ccwX = -dy + dx;
            ccwY = dx + dy;
            cwX = dy + dx;
            cwY = -dx + dy;
        }

        GameObject? firstTransport = null;
        for (int index = 0; index < formationSize; index++)
        {
            int multiplier = (int)((index + 1) / 2 * spacing);
            var offset = index % 2 == 1 ? new Vector3(ccwX * multiplier, ccwY * multiplier, 0) : new Vector3(cwX * multiplier, cwY * multiplier, 0);

            var startPos = start + offset;
            var moveTo = target + offset;
            var aimAt = target + offset * (1 - convergence);
            if (errorRadius > 1 && index > 0)
            {
                float radius = game.Random.Float(0, errorRadius), angle = game.Random.Float(0, MathF.Tau);
                aimAt += new Vector3(radius * MathF.Cos(angle), radius * MathF.Sin(angle), 0);
            }

            float orientation = MathF.Atan2(moveTo.Y - startPos.Y, moveTo.X - startPos.X);
            if (distToTarget > 0)
            {
                const float Slop = 1.5f;
                startPos.X -= MathF.Cos(orientation) * distToTarget * Slop;
                startPos.Y -= MathF.Sin(orientation) * distToTarget * Slop;
            }

            GameObject? transport;
            if (createOwner)
            {
                transport = game.CreateObject(transportName, owner, startPos, orientation);
                if (transport == null)
                    yield break;
                transport.ProducerId = source.Id;
                transport.SetStatus("SCRIPT_TARGETABLE");
                if (delayMax > 0)
                    transport.SetDisabledFor("DEFAULT", game.Random.Int(0, delayMax));
            }
            else
            {
                transport = source;
            }
            firstTransport ??= transport;

            transport.FindModule<Modules.SpecialPowers.SpecialPowerCompletionDie>()?.SetCreator(index == 0 ? source.Id : 0);

            if (transport.FindModule<Modules.AI.DeliverPayloadAIUpdate>() is { } delivery)
            {
                if (startAtMaxSpeed && createOwner && transport.FindModule<Modules.Updates.PhysicsBehavior>() is { } physics
                    && delivery.CurrentLocomotor is { } speedLocomotor)
                {
                    float factor = speedLocomotor.MaxSpeedFor(transport) * physics.Mass;
                    physics.ApplyMotiveForce(MathUtil.Forward(transport.Orientation) * factor);
                }
                var data = Modules.AI.DeliverPayloadData.FromRules(nugget, game.Catalog);
                if (index != 0)
                    data.DeliveryDecalRadius = 0; // only the lead plane marks the target
                delivery.DeliverPayload(moveTo, aimAt, data);
                if (startAtPreferredHeight && createOwner && delivery.CurrentLocomotor is { } locomotor)
                    transport.SetPosition(startPos with { Z = game.Terrain.GroundHeight(startPos) + locomotor.PreferredHeight });
                LoadPayload(nugget, transport, owner, startPos, source, firstInFormation: index == 0);
            }
            else
            {
                transport.AI?.MoveTo(moveTo, Commands.CommandSource.AI);
            }
            yield return transport;
        }
    }

    /// <summary>
    /// Fills the plane: each "Payload: Name count" object, put in a PutInContainer (e.g. a parachute)
    /// if the nugget names one, then into the plane's own container.
    /// </summary>
    void LoadPayload(RuleNode nugget, GameObject transport, Players.Player owner, Vector3 startPos, GameObject source, bool firstInFormation)
    {
        var containerTemplate = nugget.GetString("PutInContainer") is { Length: > 0 } name ? game.Templates.Find(name) : null;
        if (!nugget.TryGet("Payload", out var lines))
            return;
        bool first = true;
        foreach (var line in lines.AsList())
        {
            string[] words = RuleValue.Words(line.Value);
            if (words.Length == 0 || game.Templates.Find(words[0]) is not { } template)
                continue;
            int count = words.Length > 1 ? RuleValue.Int(words[1], 1) : 1;
            for (int i = 0; i < count; i++)
            {
                var payload = game.CreateObject(template, owner, startPos, transport.Orientation, owner.DefaultTeam);
                payload.ProducerId = transport.Id;
                payload.FindModule<Modules.SpecialPowers.SpecialPowerCompletionDie>()?.SetCreator(firstInFormation && first ? source.Id : 0);
                if (containerTemplate != null)
                {
                    var container = game.CreateObject(containerTemplate, owner, startPos, transport.Orientation, owner.DefaultTeam);
                    container.ProducerId = transport.Id;
                    container.FindModule<Modules.SpecialPowers.SpecialPowerCompletionDie>()?.SetCreator(firstInFormation && first ? source.Id : 0);
                    if (container.Contain is { } inner && inner.IsValidContainerFor(payload, true))
                    {
                        inner.AddToContain(payload);
                        payload = container;
                    }
                    else
                    {
                        Log.Warning($"DeliverPayload: {containerTemplate.Name} can't hold {template.Name}.");
                        game.DestroyObject(container);
                    }
                }
                if (transport.Contain is { } hold && hold.IsValidContainerFor(payload, true))
                    hold.AddToContain(payload);
                else
                    Log.Warning($"DeliverPayload: {transport.Template.Name} is full, or can't carry {payload.Template.Name}.");
                first = false;
            }
        }
    }

    public void Attack(RuleNode nugget, GameObject? source, Vector3 target)
    {
        if (source?.AI == null)
            return;
        var slot = nugget.GetString("WeaponSlot").ToUpperInvariant() switch
        {
            "SECONDARY" => WeaponSlot.Secondary,
            "TERTIARY" => WeaponSlot.Tertiary,
            _ => WeaponSlot.Primary,
        };
        source.Weapons.LockSlot(slot);
        source.AI.AttackPosition(target, Commands.CommandSource.AI, Math.Max(1, RuleValue.Int(nugget.GetString("NumberOfShots"), 1)));
        if (source.FindModule<Modules.Updates.RadiusDecalUpdate>() is { } decal)
        {
            decal.CreateRadiusDecal(Effects.RadiusDecalTemplate.Parse(nugget.TryGet("DeliveryDecal", out var d) ? d : null),
                RuleValue.Float(nugget.GetString("DeliveryDecalRadius")), target);
            decal.KillWhenNoLongerAttacking = true;
        }
    }
}

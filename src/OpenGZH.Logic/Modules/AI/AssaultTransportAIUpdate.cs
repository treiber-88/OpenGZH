using System.Numerics;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// The Troop Crawler. Its "gun" does DEPLOY damage: firing it at a target starts an assault, and
/// the healthy passengers jump out and attack that target (chasing it if need be). Members whose
/// health falls below MembersGetHealedAtLifeRatio climb back in to heal, and get out again once
/// fully healed. Passengers that boarded during the assault stay in. When the target dies, an
/// attack-move carries on to its goal; an attack order calls everyone back in. A player order to
/// a member takes it out of the squad. If the crawler dies, the members finish its last order.
/// </summary>
public sealed class AssaultTransportAIUpdate : AIUpdateInterface
{
    const int MaxTransportSlots = 10;

    sealed class Member
    {
        public uint Id;
        public bool Healing;
        public bool New;
    }

    float healAtLifeRatio;
    readonly List<Member> members = new();
    Vector3 attackMoveGoal;
    uint designatedTargetId;
    bool isAttackMove, isAttackObject, newOccupantsAreNewMembers;

    protected override void Initialize()
    {
        base.Initialize();
        healAtLifeRatio = FloatSetting("MembersGetHealedAtLifeRatio");
    }

    void Reset()
    {
        members.Clear();
        attackMoveGoal = default;
        designatedTargetId = 0;
        isAttackMove = isAttackObject = newOccupantsAreNewMembers = false;
    }

    protected override bool OnOrder(AIOrder order, Action replay)
    {
        if (order.Source != CommandSource.AI)
        {
            switch (order.Type)
            {
                case AIOrderType.AttackMoveTo:
                    Reset();
                    attackMoveGoal = order.Position ?? Owner.Position;
                    isAttackMove = true;
                    break;
                case AIOrderType.AttackObject:
                    Reset();
                    isAttackObject = true;
                    break;
                case AIOrderType.Stop:
                    designatedTargetId = 0;
                    RetrieveMembers();
                    Reset();
                    break;
                default:
                    Reset();
                    break;
            }
        }
        return false;
    }

    /// <summary>The crawler's DEPLOY weapon fired at a target: everybody out.</summary>
    public void BeginAssault(GameObject? designatedTarget)
    {
        if (designatedTarget != null)
            designatedTargetId = designatedTarget.Id;
    }

    public override int Update()
    {
        var transport = Owner;
        if (transport.IsEffectivelyDead)
        {
            GiveFinalOrders();
            return LogicTime.SleepForever;
        }

        // Members that died or were given orders by their player leave the squad.
        for (int i = members.Count - 1; i >= 0; i--)
        {
            var member = Game.FindObject(members[i].Id);
            var ai = member?.AI;
            if (member == null || member.IsEffectivelyDead || ai == null || ai.LastCommandSource != CommandSource.AI)
            {
                if (ai != null)
                    ai.AllowedToChase = false;
                members.RemoveAt(i);
            }
        }

        // New passengers join the squad.
        if (transport.Contain is { } contain)
        {
            foreach (var passenger in contain.Passengers.ToList())
            {
                if (members.Any(m => m.Id == passenger.Id) || members.Count >= MaxTransportSlots)
                    continue;
                if (passenger.AI != null)
                    passenger.AI.AllowedToChase = true;
                members.Add(new Member { Id = passenger.Id, Healing = IsWounded(passenger), New = newOccupantsAreNewMembers });
            }
            newOccupantsAreNewMembers = true;
        }

        if (IsAttackPointless())
        {
            Stop(CommandSource.AI);
            return 0;
        }

        var designatedTarget = Game.FindObject(designatedTargetId);
        if (designatedTarget is { IsEffectivelyDead: true })
            designatedTarget = null;

        if (designatedTarget != null)
        {
            foreach (var m in members)
            {
                var member = Game.FindObject(m.Id);
                if (member?.AI is not { } ai)
                    continue;
                bool contained = member.IsContained;
                if (contained && IsHealthy(member) && !m.New)
                    ai.ExitContainer(CommandSource.AI);
                if (contained)
                    continue;
                if (IsWounded(member))
                {
                    if (ai.State != AIState.Entering)
                        ai.Enter(transport, CommandSource.AI);
                }
                else if (!ai.IsMoving && ai.TargetId != designatedTarget.Id)
                {
                    ai.AttackObject(designatedTarget, CommandSource.AI);
                }
            }
        }
        else if (isAttackMove && State != AIState.AttackMoving)
        {
            AttackMoveTo(attackMoveGoal, CommandSource.AI);
        }
        else if (isAttackObject)
        {
            RetrieveMembers();
        }

        base.Update();
        return 0;
    }

    /// <summary>Attacking, but every squad member boarded during the assault: nobody to send.</summary>
    bool IsAttackPointless() =>
        Owner.HasStatus(Core.Status.IsAttacking) && members.All(m => m.New);

    bool IsWounded(GameObject member) => member.MaxHealth > 0 && member.Health / member.MaxHealth < healAtLifeRatio;

    static bool IsHealthy(GameObject member) => member.Health == member.MaxHealth;

    void RetrieveMembers()
    {
        foreach (var m in members)
        {
            var member = Game.FindObject(m.Id);
            if (member?.AI is { } ai && !member.IsContained && ai.State != AIState.Entering)
                ai.Enter(Owner, CommandSource.AI);
        }
    }

    void GiveFinalOrders()
    {
        var designatedTarget = Game.FindObject(designatedTargetId);
        foreach (var m in members)
        {
            if (Game.FindObject(m.Id)?.AI is not { } ai)
                continue;
            if (isAttackObject && designatedTarget != null)
                ai.AttackObject(designatedTarget, CommandSource.Player);
            else if (isAttackMove)
                ai.AttackMoveTo(attackMoveGoal, CommandSource.Player);
            ai.AllowedToChase = false;
        }
        members.Clear();
    }
}

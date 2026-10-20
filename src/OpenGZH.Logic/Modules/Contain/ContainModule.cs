using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Contain;

/// <summary>
/// Base class for objects that hold other objects: transports, garrisonable buildings, tunnels,
/// healing structures... (the original "OpenContain"). Passengers are removed from the world
/// while inside and come back out when evacuated.
/// </summary>
public class ContainModule : Module, IDieModule, ICreateModule
{
    readonly List<uint> passengers = new();

    public int ContainMax { get; protected set; }
    public IReadOnlyList<uint> PassengerIds => passengers;
    public int Count => passengers.Count;
    public IEnumerable<GameObject> Passengers => passengers.Select(Game.FindObject).Where(o => o != null)!;

    /// <summary>Buildings infantry can garrison (they shoot from the windows and can be cleared out).</summary>
    public virtual bool IsGarrisonable => false;

    /// <summary>Weapons that clear garrisons (e.g. a Dragon Tank's flames) can't empty this container.</summary>
    public virtual bool IsImmuneToClearBuildingAttacks => true;

    /// <summary>A vehicle whose look depends on its rider (e.g. the Combat Cycle).</summary>
    public virtual bool IsRiderChangeContain => false;

    /// <summary>How many of the passengers are stealthy (for detectors and stealth garrisons).</summary>
    public int StealthUnitsContained => Passengers.Count(p => p.HasStatus(Core.Status.CanStealth));

    /// <summary>Passengers may shoot out (PassengersAllowedToFire, or later a PassengersFireUpgrade).</summary>
    public bool PassengersAllowedToFire { get; set; }

    protected bool DamagePercentToUnits;
    protected bool AllowInsideKindOfAll;
    string[] allowInsideKindOf = [];
    string[] forbidInsideKindOf = [];
    string enterSound = "", exitSound = "";
    int slotsUsed;

    protected override void Initialize()
    {
        ContainMax = IntSetting("ContainMax", IntSetting("Slots", 10));
        allowInsideKindOf = Setting("AllowInsideKindOf").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        forbidInsideKindOf = Setting("ForbidInsideKindOf").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        PassengersAllowedToFire = BoolSetting("PassengersAllowedToFire");
        enterSound = Setting("EnterSound");
        exitSound = Setting("ExitSound");
        InitializeContain();
    }

    protected virtual void InitializeContain() { }

    public virtual void OnCreate() { }

    /// <summary>Transport slots a passenger takes (TransportSlotCount on its definition, default 1).</summary>
    protected static int SlotsFor(GameObject passenger) =>
        Math.Max(1, RuleValue.Int(passenger.Template.Rules.GetString("TransportSlotCount"), 1));

    public virtual bool CanContain(GameObject passenger)
    {
        if (passenger == Owner || passenger.IsContained)
            return false;
        if (slotsUsed + SlotsFor(passenger) > ContainMax)
            return false;
        if (allowInsideKindOf.Length > 0 && !allowInsideKindOf.Any(passenger.IsKindOf))
            return false;
        if (forbidInsideKindOf.Any(passenger.IsKindOf))
            return false;
        return true;
    }

    public bool AddPassenger(GameObject passenger)
    {
        if (!CanContain(passenger))
            return false;
        passengers.Add(passenger.Id);
        slotsUsed += SlotsFor(passenger);
        passenger.ContainedById = Owner.Id;
        passenger.AI?.Stop(Commands.CommandSource.AI);
        passenger.SetStatus(Core.Status.Unselectable);
        Game.Partition.Remove(passenger);
        Game.Events.PlaySound(enterSound, Owner.Position);
        OnPassengerAdded(passenger);
        return true;
    }

    protected virtual void OnPassengerAdded(GameObject passenger) { }
    protected virtual void OnPassengerRemoved(GameObject passenger) { }

    /// <summary>Lets one passenger out next to the container.</summary>
    public void Release(GameObject passenger, Vector3? exitPosition = null)
    {
        if (!passengers.Remove(passenger.Id))
            return;
        slotsUsed -= SlotsFor(passenger);
        passenger.ContainedById = 0;
        passenger.SetStatus(Core.Status.Unselectable, false);
        var position = exitPosition ?? ExitPoint();
        passenger.SetPosition(position);
        Game.Partition.Add(passenger);
        Game.Events.PlaySound(exitSound, Owner.Position);
        OnPassengerRemoved(passenger);
    }

    public void EvacuateAll()
    {
        foreach (var passenger in Passengers.ToList())
            Release(passenger);
    }

    protected Vector3 ExitPoint()
    {
        var offset = MathUtil.Forward(Owner.Orientation) * (Owner.Radius + 10);
        var point = Owner.Position + offset;
        var passable = Game.Pathfinder.NearestPassable(point, Locomotion.Surfaces.Ground) ?? point;
        return passable with { Z = Game.Terrain.GroundHeight(passable) };
    }

    /// <summary>When the container dies, passengers either escape or die with it.</summary>
    public virtual void OnDie(DamageInfo killingBlow)
    {
        bool killPassengers = BoolSetting("DamagePercentToUnits") || Owner.IsKindOf(Kind.Aircraft) && Owner.IsAirborneTarget;
        foreach (var passenger in Passengers.ToList())
        {
            Release(passenger);
            if (killPassengers)
                passenger.Kill(killingBlow.DeathType);
        }
    }
}

using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Stealth;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Modules.Contain;

/// <summary>
/// Base of everything that holds other objects (the original "OpenContain"): transports, garrisoned
/// buildings, tunnels, parachutes, the Overlord's add-ons. Units get in by touching the container
/// while their AI is set to enter it; they get out through the container's door
/// (ExitStart/ExitEnd bones, NumberOfExitPaths of them), walking to the rally point if there is one.
///
/// Enclosing containers take their riders out of the world (hidden, not collidable); non-enclosing
/// ones (a parachute's soldier, an Overlord's bunker) keep them visible at the container.
/// </summary>
public class ContainModule : Module, IUpdateModule, IDieModule, ICreateModule, ICollideModule, IOwnershipModule, IDamageModule
{
    protected readonly List<uint> ContainList = new();

    // Settings
    int containMax;
    FlagSet allowInsideKindOf, forbidInsideKindOf;
    bool allowAllInside;
    float damagePercentToUnits;
    bool burnedDeathToUnits;
    bool passengersInTurret;
    int numberOfExitPaths;
    int doorOpenFrames;
    bool weaponBonusPassedToPassengers;
    bool allowAlliesInside, allowEnemiesInside, allowNeutralInside;
    string enterSound = "", exitSound = "";

    // State
    int stealthUnitsContained;
    int lastLoadSoundFrame = -1, lastUnloadSoundFrame = -1;
    int doorCloseCountdown;
    Vector3? rallyPoint;
    int whichExitPath = 1;
    bool loadSoundsEnabled = true;
    FlagSet lastConditions;
    readonly List<Matrix4x4> firePoints = new();
    bool firePointsLoaded, noFirePointsInArt;
    int firePointNext;

    /// <summary>Passengers may shoot out (PassengersAllowedToFire, or a PassengersFireUpgrade).</summary>
    public bool PassengersAllowedToFire { get; set; }

    public virtual int ContainMax => containMax;
    public virtual int ContainCount => ContainList.Count;
    public int Count => ContainCount;
    public virtual IEnumerable<GameObject> Passengers => ContainList.Select(Game.FindObject).Where(o => o != null)!;
    public int StealthUnitsContained => stealthUnitsContained;
    public float DamagePercentToUnits => damagePercentToUnits;
    public bool WeaponBonusPassedToPassengers => weaponBonusPassedToPassengers;

    /// <summary>
    /// Units are on their way in, or passengers are waiting to get out (a Chinook lands for them).
    /// </summary>
    public bool HasObjectsWantingToEnterOrExit =>
        Passengers.Any(p => p.AI is { IsWaitingToExit: true })
        || Game.Objects.Any(o => o.AI is { State: Modules.AI.AIState.Entering } ai && ai.EnterTargetId == Owner.Id && !o.IsEffectivelyDead);

    public virtual bool IsGarrisonable => false;
    public virtual bool IsHealContain => false;
    public virtual bool IsTunnelContain => false;
    public virtual bool IsRiderChangeContain => false;
    public virtual bool IsSpecialZeroSlotContainer => false;
    public virtual bool IsSpecialOverlordStyleContainer => false;
    public virtual bool IsImmuneToClearBuildingAttacks => true;
    public virtual bool IsKickOutOnCapture => true;
    public virtual bool IsDisplayedOnControlBar => false;
    public virtual int ExtraSlotsInUse => 0;

    /// <summary>The first rider (for rider-dependent vehicles).</summary>
    public virtual GameObject? Rider => Passengers.FirstOrDefault();

    protected override void Initialize()
    {
        containMax = IntSetting("ContainMax", int.MaxValue);
        var allow = Setting("AllowInsideKindOf");
        allowAllInside = allow.Length == 0;
        allowInsideKindOf = Flags.KindOf.Parse(allow);
        forbidInsideKindOf = Flags.KindOf.Parse(Setting("ForbidInsideKindOf"));
        damagePercentToUnits = FloatSetting("DamagePercentToUnits");
        burnedDeathToUnits = BoolSetting("BurnedDeathToUnits", true);
        PassengersAllowedToFire = BoolSetting("PassengersAllowedToFire");
        passengersInTurret = BoolSetting("PassengersInTurret");
        numberOfExitPaths = IntSetting("NumberOfExitPaths", 1);
        doorOpenFrames = Definition.Settings.Has("DoorOpenTime") ? FramesSetting("DoorOpenTime") : 1;
        weaponBonusPassedToPassengers = BoolSetting("WeaponBonusPassedToPassengers");
        allowAlliesInside = BoolSetting("AllowAlliesInside", true);
        allowEnemiesInside = BoolSetting("AllowEnemiesInside", true);
        allowNeutralInside = BoolSetting("AllowNeutralInside", true);
        enterSound = Setting("EnterSound");
        exitSound = Setting("ExitSound");
        InitializeContain();
    }

    protected virtual void InitializeContain() { }

    public virtual void OnCreate() { }

    public virtual void OnBuildComplete() { }

    public virtual FlagSet DisabledTypesToProcess => default;

    // ---- Who may get in ----

    /// <summary>Transport slots a passenger takes (TransportSlotCount on its definition).</summary>
    protected static int SlotsFor(GameObject passenger) =>
        RuleValue.Int(passenger.Template.Rules.GetString("TransportSlotCount"), 1);

    public virtual bool IsValidContainerFor(GameObject obj, bool checkCapacity)
    {
        if (!allowAllInside && !obj.KindOf.HasAny(allowInsideKindOf))
            return false;
        if (obj.KindOf.HasAny(forbidInsideKindOf))
            return false;
        var relation = obj.Owner.RelationshipTo(Owner.Owner);
        return relation switch
        {
            Relationship.Allies => allowAlliesInside,
            Relationship.Enemies => allowEnemiesInside,
            _ => allowNeutralInside,
        };
    }

    /// <summary>Whether this container hides its riders (and takes them out of the world).</summary>
    public virtual bool IsEnclosingContainerFor(GameObject? obj) => true;

    // ---- Getting in ----

    /// <summary>Puts a rider inside (booby traps go off first, as in the original).</summary>
    public virtual void AddToContain(GameObject rider)
    {
        if (Owner.CheckAndDetonateBoobyTrap(rider) && (Owner.IsEffectivelyDead || rider.IsEffectivelyDead))
            return;
        if (rider.IsContained)
            return;
        AddToContainList(rider);
        PlayerWhoEntered = rider.Owner.PlayerMask;
        if (IsEnclosingContainerFor(rider))
            SetInWorld(rider, false);
        rider.OnContainedBy(Owner);
        RedeployOccupants();
        OnContaining(rider);
        DoLoadSound();
    }

    protected virtual void AddToContainList(GameObject rider)
    {
        ContainList.Add(rider.Id);
        if (rider.IsKindOf("STEALTH_GARRISON"))
            stealthUnitsContained++;
    }

    /// <summary>Takes an object out of (or back into) the world: hidden, no collisions, not found by searches.</summary>
    protected void SetInWorld(GameObject obj, bool inWorld)
    {
        obj.IsHiddenInContainer = !inWorld;
        if (inWorld)
            Game.Partition.Add(obj);
        else
            Game.Partition.Remove(obj);
        if (obj.Contain is { } inner)
            foreach (var nested in inner.Passengers.ToList())
                if (!inner.IsEnclosingContainerFor(nested))
                    inner.SetInWorld(nested, inWorld);
    }

    void DoLoadSound()
    {
        if (!loadSoundsEnabled || Frame == lastLoadSoundFrame)
            return;
        Game.Events.PlaySound(enterSound, Owner.Position, source: Owner);
        lastLoadSoundFrame = Frame;
    }

    protected void DoUnloadSound()
    {
        if (Frame == lastUnloadSoundFrame)
            return;
        Game.Events.PlaySound(exitSound, Owner.Position, source: Owner);
        lastUnloadSoundFrame = Frame;
    }

    public void EnableLoadSounds(bool enabled) => loadSoundsEnabled = enabled;

    /// <summary>Hook: a rider just got in.</summary>
    protected virtual void OnContaining(GameObject rider)
    {
        if (loadSoundsEnabled)
            Game.Events.PlaySound(Owner.Template.Rules.GetString("SoundEnter"), Owner.Position, source: Owner);
    }

    /// <summary>Hook: a rider is getting out.</summary>
    protected virtual void OnRemoving(GameObject rider)
    {
        Game.Events.PlaySound(Owner.Template.Rules.GetString("SoundExit"), Owner.Position, source: Owner);
        Game.Events.PlaySound(rider.Template.Rules.GetString("SoundFalling"), rider.Position, source: rider);
    }

    // ---- Getting out ----

    /// <summary>The "system" removal: the rider is put back in the world at the container (no door, no walking).</summary>
    public virtual void RemoveFromContain(GameObject rider, bool exposeStealthUnits = false)
    {
        if (rider.ContainedById != Owner.Id || !ContainList.Remove(rider.Id))
            return;
        if (rider.IsKindOf("STEALTH_GARRISON"))
        {
            stealthUnitsContained--;
            if (exposeStealthUnits)
                rider.FindModule<StealthUpdate>()?.MarkAsDetected();
        }
        if (IsEnclosingContainerFor(rider))
        {
            SetInWorld(rider, true);
            rider.SetPosition(Owner.Position);
        }
        DoUnloadSound();
        OnRemoving(rider);
        rider.OnRemovedFrom(Owner);
    }

    public virtual void RemoveAllContained(bool exposeStealthUnits = false)
    {
        foreach (var rider in Passengers.ToList())
            RemoveFromContain(rider, exposeStealthUnits);
    }

    /// <summary>Can a bunker buster flush the occupants out (garrisons, tunnels, caves)?</summary>
    public virtual bool IsBustable => false;

    public virtual void KillAllContained()
    {
        foreach (var rider in Passengers.ToList())
        {
            ContainList.Remove(rider.Id);
            OnRemoving(rider);
            rider.OnRemovedFrom(Owner);
            rider.Kill();
        }
    }

    public void HarmAndForceExitAllContained(DamageInfo damage)
    {
        foreach (var rider in Passengers.ToList())
        {
            RemoveFromContain(rider, true);
            rider.Damage(damage);
        }
    }

    /// <summary>Whether the door is free right now (transports space out their exits).</summary>
    public virtual bool IsExitBusy => false;

    /// <summary>Whether this rider can get out now (e.g. not from a plane in mid-air). False keeps it waiting.</summary>
    public virtual bool ReserveDoorForExit(GameObject rider) => true;

    /// <summary>
    /// The "game" exit: out of the door at the ExitStart bone, then a walk to ExitEnd (and the rally
    /// point). Containers with NumberOfExitPaths 0 just let the rider out where it is.
    /// </summary>
    public virtual void ExitObjectViaDoor(GameObject rider)
    {
        RemoveFromContain(rider);
        doorCloseCountdown = doorOpenFrames;
        if (doorCloseCountdown > 0)
        {
            Owner.SetCondition("DOOR_1_CLOSING", false);
            Owner.SetCondition("DOOR_1_OPENING");
        }
        if (numberOfExitPaths <= 0)
            return;

        string startBone = "ExitStart", endBone = "ExitEnd";
        if (numberOfExitPaths > 1)
        {
            startBone += $"{whichExitPath:00}";
            endBone += $"{whichExitPath:00}";
            whichExitPath = whichExitPath % numberOfExitPaths + 1; // cycle 1..n
        }
        var start = Game.Bones.BonePosition(Owner, startBone);
        var end = Game.Bones.BonePosition(Owner, endBone);
        rider.SetPosition(start);
        rider.SetOrientation(Owner.Orientation);
        if (rider.AI is { } ai)
        {
            ai.IgnoreObstacle(null);
            ai.SetIgnoreCollisionTime(LogicTime.FramesPerSecond);
            var physics = rider.FindModule<PhysicsBehavior>();
            bool canFall = physics?.AllowToFall ?? false;
            if (physics != null)
                physics.AllowToFall = false;
            var route = new List<Vector3> { end, end };
            if (rallyPoint is { } rally)
                route.Add(rally);
            ai.FollowWaypoints(route, CommandSource.AI);
            if (physics != null)
                physics.AllowToFall = canFall;
        }
    }

    /// <summary>Tells every passenger to get out (an evacuate order, or selling the building).</summary>
    public void OrderAllPassengersToExit(CommandSource source, bool instantly)
    {
        foreach (var rider in Passengers.ToList())
        {
            if (rider.AI is not { } ai)
                continue;
            if (instantly)
                ai.ExitContainerInstantly(source);
            else
                ai.ExitContainer(source);
        }
    }

    public void SetRallyPoint(Vector3 point) => rallyPoint = point;

    /// <summary>Puts a rider back in the world somewhere near the container (when the container dies).</summary>
    protected void ScatterToNearbyPosition(GameObject rider)
    {
        float angle = Game.Random.Float(0, MathF.Tau);
        float minRadius = Owner.BoundingCircleRadius;
        float distance = Game.Random.Float(minRadius, minRadius * 1.5f);
        var pos = Owner.Position + new Vector3(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance, 0);
        pos.Z = Game.Terrain.LayerHeight(pos.X, pos.Y);
        rider.SetOrientation(angle);
        if (rider.AI is { } ai)
        {
            rider.SetPosition(Owner.Position);
            ai.IgnoreObstacle(Owner);
            ai.MoveTo(pos, CommandSource.AI);
        }
        else
        {
            rider.SetPosition(pos);
        }
    }

    // ---- Firing from inside ----

    public virtual bool IsPassengerAllowedToFire(GameObject passenger)
    {
        if (!PassengersAllowedToFire)
            return false;
        if (Game.FindObject(Owner.ContainedById)?.Contain is { } outer)
            return outer.IsPassengerAllowedToFire(passenger);
        return true;
    }

    /// <summary>Garrisons move a shooter to the window nearest its target (false if it then can't reach it).</summary>
    public virtual bool AttemptBestFirePointPosition(GameObject source, Weapon weapon, GameObject? victim, Vector3? position) => false;

    // ---- Keeping riders in place ----

    /// <summary>The container moved or turned: riders go with it.</summary>
    public virtual void ContainReactToTransformChange() => RedeployOccupants();

    /// <summary>Places riders at the FIREPOINT bones in turn (or at the container if it has none).</summary>
    public virtual void RedeployOccupants()
    {
        firePointsLoaded = false;
        noFirePointsInArt = false;
        firePointNext = 0;
        var riders = Passengers.ToList();
        for (int i = riders.Count - 1; i >= 0; i--)
            PutObjAtNextFirePoint(riders[i]);
    }

    void PutObjAtNextFirePoint(GameObject obj)
    {
        if (!firePointsLoaded && !noFirePointsInArt)
        {
            firePoints.Clear();
            firePoints.AddRange(Game.Bones.NumberedBones(Owner, "FIREPOINT", 40));
            firePointsLoaded = true;
            noFirePointsInArt = firePoints.Count == 0;
        }
        if (noFirePointsInArt)
        {
            obj.SetPosition(Owner.Position);
            return;
        }
        var matrix = firePoints[firePointNext];
        if (passengersInTurret && Game.Bones.TryGetBone(Owner, $"FIREPOINT{firePointNext + 1:00}", out var turretBone))
            matrix = turretBone;
        obj.SetPosition(matrix.Translation);
        if (!IsEnclosingContainerFor(obj))
            obj.SetOrientation(MathF.Atan2(matrix.M12, matrix.M11));
        firePointNext = (firePointNext + 1) % firePoints.Count;
    }

    // ---- Per-frame ----

    /// <summary>The mask of the player whose unit got in since the last update (the original's m_playerEnteredMask).</summary>
    public uint PlayerWhoEntered { get; private set; }

    /// <summary>Which side of the building occupants come out of (NAMED_SET_EVAC_LEFT_OR_RIGHT; 0 = the usual exit).</summary>
    public int EvacDisposition { get; set; }

    public virtual int Update()
    {
        PlayerWhoEntered = 0;
        // Riders are placed for the container's current model: re-place them when it changes (damage etc.).
        if (Owner.ModelConditions != lastConditions)
        {
            lastConditions = Owner.ModelConditions;
            RedeployOccupants();
        }
        if (doorCloseCountdown > 0 && --doorCloseCountdown == 0)
        {
            Owner.SetCondition("DOOR_1_OPENING", false);
            Owner.SetCondition("DOOR_1_CLOSING");
        }
        return 0;
    }

    // ---- Touching: units entering ----

    public virtual void OnCollide(GameObject? other, Vector3 location, Vector3 normal)
    {
        if (other?.AI is not { } ai || ai.EnterTargetId != Owner.Id)
            return;
        TryEnter(other);
    }

    /// <summary>
    /// A unit that wants in reached the container: riders of another player are kicked out first
    /// (a garrison being taken over), then it gets in if there's room.
    /// </summary>
    public void TryEnter(GameObject other)
    {
        foreach (var rider in Passengers.ToList())
        {
            if (rider.Owner == other.Owner)
                continue;
            if (rider.AI is { } riderAi)
            {
                if (rider.IsKindOf("STEALTH_GARRISON"))
                    rider.FindModule<StealthUpdate>()?.MarkAsDetected();
                riderAi.ExitContainer(CommandSource.AI);
            }
            else
            {
                RemoveFromContain(rider, true);
            }
        }
        if (!IsValidContainerFor(other, true))
            return;
        AddToContain(other);
        other.AI?.OnEnteredContainer();
    }

    // ---- Death, deletion, capture, selling ----

    public virtual void OnDie(DamageInfo killingBlow)
    {
        if (damagePercentToUnits > 0)
            ProcessDamageToContained(damagePercentToUnits);
        KillRidersWhoAreNotFreeToExit();
        RemoveAllContained();
    }

    protected virtual void KillRidersWhoAreNotFreeToExit() { }

    public void ProcessDamageToContained(float percent)
    {
        foreach (var rider in Passengers.ToList())
        {
            if (rider.Body is not { } body)
                continue;
            rider.Damage(new DamageInfo
            {
                DamageType = "UNRESISTABLE",
                DeathType = burnedDeathToUnits ? "BURNED" : "NORMAL",
                SourceId = Owner.Id,
                Amount = body.MaxHealth * percent,
            });
            if (!rider.IsEffectivelyDead && percent == 1)
                rider.Kill(); // flame-proof troops still die when the container is told to kill them all
        }
    }

    public virtual void OnDelete()
    {
        foreach (var rider in Passengers.ToList())
            Game.DestroyObject(rider);
    }

    public virtual void OnCapture(Player oldOwner, Player newOwner) { }

    public virtual void OnSelling() => OrderAllPassengersToExit(CommandSource.AI, false);

    public virtual void OnBodyDamageStateChange(BodyDamageState oldState, BodyDamageState newState) { }
}

/// <summary>A GLA cave: caves with the same index share one network (the original's CaveInterface).</summary>
public interface ICaveContain
{
    void TryToSetCaveIndex(int index);
}

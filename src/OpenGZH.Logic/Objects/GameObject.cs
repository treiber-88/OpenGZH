using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules;
using OpenGZH.Logic.Modules.AI;
using OpenGZH.Logic.Modules.Bodies;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Objects;

public enum VeterancyLevel { Regular = 0, Veteran = 1, Elite = 2, Heroic = 3 }

/// <summary>
/// Anything that exists in the game world: units, buildings, projectiles, trees, crates...
/// Its behaviour comes from its modules (see <see cref="Module"/>); this class holds the shared state.
/// </summary>
public sealed partial class GameObject
{
    public GameLogic Game { get; }
    public uint Id { get; }
    public ObjectTemplate Template { get; private set; }

    /// <summary>The controlling player. Changes when captured or hijacked.</summary>
    public Player Owner { get; private set; }
    public Team? Team { get; private set; }

    /// <summary>Unique name given by the map or a script (used by mission scripts). Empty if none.</summary>
    public string Name { get; set; } = "";

    public Vector3 Position { get; private set; }

    /// <summary>Facing angle in radians (0 = +X, counter-clockwise).</summary>
    public float Orientation { get; private set; }

    /// <summary>Tilt for display (radians): nose up/down and side to side. Set by physics and slopes.</summary>
    public float Pitch { get; set; }
    public float Roll { get; set; }

    /// <summary>Replaces the model the client draws (debris pieces pick their model this way).</summary>
    public string? ModelOverride { get; set; }

    public FlagSet KindOf { get; }
    public FlagSet StatusFlags { get; private set; }
    public FlagSet ModelConditions { get; private set; }
    public FlagSet DisabledFlags { get; private set; }

    /// <summary>Weapon set conditions (VETERAN, PLAYER_UPGRADE, ...) that choose which WeaponSet is used.</summary>
    public FlagSet WeaponSetFlags { get; private set; }
    public FlagSet ArmorSetFlags { get; private set; }

    /// <summary>Active weapon bonus conditions (VETERAN, HORDE, GARRISONED, ...).</summary>
    public FlagSet WeaponBonusFlags { get; private set; }

    public VeterancyLevel Veterancy { get; private set; }
    public int ExperiencePoints { get; private set; }
    public float ExperienceScalar { get; set; } = 1f;

    public int CreatedFrame { get; }

    /// <summary>0-100 while under construction.</summary>
    public float ConstructionPercent { get; set; } = 100f;

    /// <summary>What was paid to start building this (refunded if construction is cancelled).</summary>
    public int ConstructionCost { get; set; }

    /// <summary>The object that produced or built this one (0 if none).</summary>
    public uint ProducerId { get; set; }
    public uint BuilderId { get; set; }

    /// <summary>Replaces the template's command set (CommandSetUpgrade, scripts). Null = use the template's.</summary>
    public string? CommandSetOverride { get; set; }

    /// <summary>
    /// Sub-objects (named meshes) shown or hidden regardless of model condition, e.g. by SubObjectsUpgrade
    /// (true = shown). <see cref="SubObjectsVersion"/> changes whenever this does, so the client can redraw.
    /// </summary>
    public IReadOnlyDictionary<string, bool> SubObjectOverrides => subObjectOverrides;
    public int SubObjectsVersion { get; private set; }
    readonly Dictionary<string, bool> subObjectOverrides = new(StringComparer.OrdinalIgnoreCase);

    public void ShowSubObject(string name, bool shown)
    {
        subObjectOverrides[name] = shown;
        SubObjectsVersion++;
    }

    /// <summary>A colour the client briefly flashes the object in (e.g. a building being captured); 0 = none. Cleared by the client.</summary>
    public uint FlashColor { get; set; }

    /// <summary>Experience this object earns goes to another object instead (e.g. a laser to the unit that made it); 0 = itself.</summary>
    public uint ExperienceSinkId { get; set; }

    /// <summary>How far this object reveals the map for its owner (starts as the template's; special power view objects change it).</summary>
    public float ShroudClearingRange { get; set; }

    /// <summary>Set by the map or scripts: the owner may not sell this building.</summary>
    public bool IsUnsellable { get; set; }

    /// <summary>The object this one is inside (a transport, building, tunnel). 0 if none.</summary>
    public uint ContainedById { get; private set; }

    /// <summary>The frame it got in (healing containers count from here).</summary>
    public int ContainedByFrame { get; private set; }

    /// <summary>Inside an enclosing container: hidden and out of the world.</summary>
    public bool IsHiddenInContainer { get; set; }

    /// <summary>This object's container module, if it is a container.</summary>
    public Modules.Contain.ContainModule? Contain { get; private set; }

    public void OnContainedBy(GameObject container)
    {
        ContainedById = container.Id;
        ContainedByFrame = Game.Frame;
    }

    public void OnRemovedFrom(GameObject container)
    {
        if (ContainedById == container.Id)
            ContainedById = 0;
        IsHiddenInContainer = false;
    }

    public IReadOnlyList<Module> Modules => modules;
    public Body? Body { get; private set; }
    public AIUpdateInterface? AI { get; private set; }
    public WeaponSet Weapons { get; }

    readonly List<Module> modules = new();
    readonly List<UpdateSlot> updates = new();
    readonly HashSet<string> upgrades = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> statusTimers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> disabledTimers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An update module plus the frame it next wants to run.</summary>
    internal sealed class UpdateSlot(IUpdateModule module)
    {
        public readonly IUpdateModule Module = module;
        public readonly UpdatePhase Phase = module.Phase;
        public int NextFrame;
    }

    internal GameObject(GameLogic game, uint id, ObjectTemplate template, Player owner, Vector3 position, float orientation)
    {
        Game = game;
        Id = id;
        Template = template;
        Owner = owner;
        Position = position;
        Orientation = orientation;
        CreatedFrame = game.Frame;
        KindOf = Flags.KindOf.Parse(template.Rules.GetString("KindOf"));
        ShroudClearingRange = template.ShroudClearingRange;
        Weapons = new WeaponSet(this);
    }

    /// <summary>Creates the modules described by the template. Called once by <see cref="GameLogic"/>.</summary>
    internal void CreateModules()
    {
        foreach (var definition in Template.Modules)
        {
            var module = ModuleRegistry.Create(definition, this);
            if (module != null)
                AddModule(module);
        }
        Weapons.LoadFromTemplate();
    }

    void AddModule(Module module)
    {
        modules.Add(module);
        if (module is Body body)
            Body ??= body;
        if (module is AIUpdateInterface ai)
            AI ??= ai;
        if (module is Modules.Contain.ContainModule contain)
            Contain ??= contain;
        if (module is IUpdateModule update)
            updates.Add(new UpdateSlot(update) { NextFrame = Game.Frame });
    }

    // ---- Queries ----

    public bool IsKindOf(int kindBit) => KindOf.Has(kindBit);
    public bool IsKindOf(string kind) => KindOf.Has(Flags.KindOf.IndexOf(kind));

    /// <summary>Whether the object has any of these space-separated KindOf flags.</summary>
    public bool IsAnyKindOf(string kinds)
    {
        foreach (var kind in kinds.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (IsKindOf(kind))
                return true;
        return false;
    }

    // ---- Healing that only one healer may give at a time (ambulances, propaganda towers) ----

    uint soleHealingBenefactorId;
    int soleHealingBenefactorExpirationFrame;

    /// <summary>Heals if no other area healer is already healing this object; returns whether it did.</summary>
    public bool AttemptHealingFromSoleBenefactor(float amount, GameObject source, int durationFrames)
    {
        int now = Game.Frame;
        if (now > soleHealingBenefactorExpirationFrame || soleHealingBenefactorId == source.Id)
        {
            soleHealingBenefactorId = source.Id;
            soleHealingBenefactorExpirationFrame = now + durationFrames;
            Body?.AttemptHealing(amount, source.Id);
            return true;
        }
        return false;
    }

    /// <summary>Colour tints the client draws over the object (poisoned green, EMP'd, frenzied...).</summary>
    public TintStatus Tint { get; private set; }

    public void SetTint(TintStatus tint, bool on) => Tint = on ? Tint | tint : Tint & ~tint;
    public bool HasStatus(int statusBit) => StatusFlags.Has(statusBit);
    public bool HasStatus(string status) => HasStatus(Flags.ObjectStatus.IndexOf(status));

    /// <summary>Drawn this much higher than its position (parked jets sit on their wheels: JetAIUpdate MinHeight).</summary>
    public float DrawHeightOffset { get; set; }

    float? visionRange;

    /// <summary>How far it sees (a hijacked vehicle takes on its hijacker's).</summary>
    public float VisionRange
    {
        get => visionRange ?? Template.VisionRange;
        set => visionRange = value;
    }

    /// <summary>
    /// Taken out of the world's spatial index: nothing finds, hits or touches it (a hijacker inside
    /// the vehicle he stole).
    /// </summary>
    public bool IsRemovedFromPartition
    {
        get => removedFromPartition;
        set
        {
            if (value == removedFromPartition)
                return;
            removedFromPartition = value;
            if (value)
                Game.Partition.Remove(this);
            else
                Game.Partition.Add(this);
        }
    }
    bool removedFromPartition;

    public bool IsTrainable => RuleValue.Bool(Template.Rules.GetString("IsTrainable"), false);

    /// <summary>Raises the rank to at least this level (VeterancyGainCreate), with that level's experience.</summary>
    public void SetMinVeterancyLevel(VeterancyLevel level)
    {
        if (Veterancy >= level)
            return;
        var required = ExperienceRequired;
        if ((int)level < required.Length)
            ExperiencePoints = required[(int)level];
        SetVeterancy(level);
    }

    // ---- Temporary weapon bonus (the original's TempWeaponBonusHelper: Frenzy, Battle Plans' boosts) ----

    string? tempWeaponBonus;
    int tempWeaponBonusUntil;

    public void DoTempWeaponBonus(string condition, int frames)
    {
        if (tempWeaponBonus != condition)
            ClearTempWeaponBonus();
        SetWeaponBonus(condition);
        tempWeaponBonus = condition;
        tempWeaponBonusUntil = Game.Frame + frames;
        SetTint(TintStatus.Frenzy, true);
    }

    void ClearTempWeaponBonus()
    {
        if (tempWeaponBonus == null)
            return;
        SetWeaponBonus(tempWeaponBonus, false);
        tempWeaponBonus = null;
        tempWeaponBonusUntil = 0;
        SetTint(TintStatus.Frenzy, false);
    }

    internal void UpdateTempWeaponBonus()
    {
        if (tempWeaponBonus != null && Game.Frame >= tempWeaponBonusUntil)
            ClearTempWeaponBonus();
    }

    /// <summary>The frame any of its weapons last fired.</summary>
    public int LastShotFiredFrame => Weapons.All.Select(w => w.LastFireFrame).DefaultIfEmpty(-1000).Max();

    public bool HasWeaponSetFlag(string flag) => WeaponSetFlags.Has(Flags.WeaponSet.IndexOf(flag));
    public bool HasArmorSetFlag(string flag) => ArmorSetFlags.Has(Flags.ArmorSet.IndexOf(flag));

    int[] ExperienceRequired => Template.Rules.GetString("ExperienceRequired").Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(w => RuleValue.Int(w)).ToArray();

    /// <summary>Whether gaining this many ranks is possible (not past Heroic).</summary>
    public bool CanGainExpForLevel(int levels) => (int)Veterancy + levels <= (int)VeterancyLevel.Heroic;

    /// <summary>Gains enough experience for <paramref name="levels"/> more ranks (veterancy crates, salvage).</summary>
    public void GainExpForLevel(int levels, bool canScaleForBonus = true)
    {
        int newLevel = Math.Min((int)Veterancy + levels, (int)VeterancyLevel.Heroic);
        var required = ExperienceRequired;
        if (newLevel >= required.Length)
            return;
        int points = required[newLevel] - ExperiencePoints;
        if (points <= 0)
            return;
        if (canScaleForBonus)
            AddExperience((int)MathF.Ceiling(points / Math.Max(0.01f, ExperienceScalar)));
        else
        {
            float scalar = ExperienceScalar;
            ExperienceScalar = 1;
            AddExperience(points);
            ExperienceScalar = scalar;
        }
    }

    /// <summary>Extra lean applied after facing and pitch (a toppled tree).</summary>
    public Quaternion Tilt { get; set; } = Quaternion.Identity;

    /// <summary>A purely visual shake offset (the Nuke shuddering out of its silo).</summary>
    public Vector3 DrawJitter { get; set; }

    /// <summary>Draw-only size (the original's instance scale: the EMP pulse grows).</summary>
    public float DrawScale { get; set; } = 1;

    /// <summary>Draw-only colour wash, if any (the original's colorTint: the EMP pulse's glow).</summary>
    public Vector3? DrawColorTint { get; set; }

    /// <summary>Not drawn (a missile that has gone off but lingers a few frames).</summary>
    public bool DrawableHidden { get; set; }
    public bool HasCondition(int conditionBit) => ModelConditions.Has(conditionBit);
    public bool HasCondition(string condition) => HasCondition(Flags.ModelCondition.IndexOf(condition));
    public bool IsDisabled => !DisabledFlags.IsEmpty;
    public bool IsDisabledBy(string type) => DisabledFlags.Has(Flags.Disabled.IndexOf(type));

    public bool IsDestroyed => HasStatus(Core.Status.Destroyed);
    public bool IsEffectivelyDead => IsDestroyed || Body is { IsDead: true };
    public bool IsUnderConstruction => HasStatus(Core.Status.UnderConstruction);
    public bool IsContained => ContainedById != 0;
    public bool IsAirborneTarget => HasStatus(Core.Status.AirborneTarget);
    public bool IsMobile => !IsKindOf(Kind.Immobile) && AI != null && !IsKindOf(Kind.Structure);

    public float Health => Body?.Health ?? 0;
    public float MaxHealth => Body?.MaxHealth ?? 0;

    /// <summary>Bounding radius on the ground (the larger geometry radius).</summary>
    public float Radius => Math.Max(Geometry.MajorRadius, Geometry.MinorRadius);

    public float Height => Template.GeometryHeight;

    /// <summary>Radius of the circle around the footprint (a box's corner distance; a sphere or cylinder's radius).</summary>
    public float BoundingCircleRadius => Geometry.BoundingCircleRadius;
    public float BoundingSphereRadius => Geometry.BoundingSphereRadius;
    Core.Geometry? geometryOverride;

    /// <summary>The object's shape; a few things change theirs (a checkpoint gate narrowing as it opens).</summary>
    public Core.Geometry Geometry => geometryOverride ?? Template.Geometry;

    public void SetGeometry(Core.Geometry geometry) => geometryOverride = geometry;

    /// <summary>Height above the ground (or bridge) beneath it.</summary>
    public float HeightAboveTerrain => Position.Z - Game.Terrain.LayerHeight(Position.X, Position.Y, Layer);

    /// <summary>The pathfinding layer the object is on: 0 = ground, 1 and up = a bridge deck (the original's getLayer).</summary>
    public int Layer { get; set; }

    /// <summary>Height above the ground or the water surface.</summary>
    public float HeightAboveTerrainOrWater =>
        Game.Terrain.IsUnderwater(Position.X, Position.Y, out float waterZ) ? Position.Z - waterZ : HeightAboveTerrain;

    public bool IsAboveTerrain => HeightAboveTerrain > 0;

    /// <summary>High enough that falling back would take more than 3 frames (the original's "significantly airborne").</summary>
    public bool IsSignificantlyAboveTerrain => HeightAboveTerrain > -(3 * 3) * Game.Catalog.GameData.Gravity;

    /// <summary>Something touched this object (other = null: the ground). Every collide module hears about it.</summary>
    public void OnCollide(GameObject? other, Vector3 location, Vector3 normal)
    {
        foreach (var module in modules.OfType<ICollideModule>().ToList())
        {
            if (HasStatus(Core.Status.NoCollisions))
                break;
            module.OnCollide(other, location, normal);
        }
    }

    /// <summary>Whether this object rolls over the other (crushOnly: by crusher level alone, not SquishCollide).</summary>
    public bool CanCrushOrSquish(GameObject other, bool crushOnly)
    {
        if (IsDisabledBy("DISABLED_UNMANNED") || Owner.IsAllyOf(other.Owner))
            return false;
        int crusherLevel = Template.CrusherLevel;
        if (crusherLevel == 0)
            return false;
        if (!crushOnly && other.Template.FindModule("SquishCollide") != null)
            return true;
        return crusherLevel > other.Template.CrushableLevel;
    }

    /// <summary>
    /// If this object is booby-trapped, sets the trap off (unless <paramref name="victim"/> is the
    /// trapper's ally) and returns true. Booby traps call the object they sit on their producer.
    /// </summary>
    public bool CheckAndDetonateBoobyTrap(GameObject? victim)
    {
        if (!HasStatus(Core.Status.BoobyTrapped))
            return false;
        const float BoobyTrapScanRange = 25;
        var trap = Game.Partition.Query(Position, BoobyTrapScanRange + BoundingCircleRadius)
            .Where(o => o.IsKindOf("BOOBY_TRAP") && o.ProducerId == Id)
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, Position))
            .FirstOrDefault();
        if (trap?.FindModule<Modules.SpecialPowers.StickyBombUpdate>() is not { } bomb)
            return false;
        if (victim != null && trap.Owner.IsAllyOf(victim.Owner))
            return false;
        bomb.Detonate();
        return true;
    }

    public T? FindModule<T>() where T : class => modules.OfType<T>().FirstOrDefault();
    public IEnumerable<T> FindModules<T>() where T : class => modules.OfType<T>();

    public override string ToString() => Name.Length > 0 ? $"{Template.Name} '{Name}' #{Id}" : $"{Template.Name} #{Id}";

    // ---- Changes ----

    bool movingRiders;

    public void SetPosition(Vector3 position)
    {
        bool moved = (int)position.X != (int)Position.X || (int)position.Y != (int)Position.Y;
        Position = position;
        Game.Partition.Moved(this);
        MoveRiders();
        if (moved)
            SetTriggerAreaFlagsForChangeInPosition();
    }

    public void SetOrientation(float radians)
    {
        Orientation = MathUtil.NormalizeAngle(radians);
        MoveRiders();
    }

    /// <summary>A container that moves takes its riders along.</summary>
    void MoveRiders()
    {
        if (Contain == null || movingRiders || Contain.Count == 0)
            return;
        movingRiders = true;
        Contain.ContainReactToTransformChange();
        movingRiders = false;
    }

    /// <summary>Fires the current weapon at a point straight away (delivery planes, strafing); respects its reload.</summary>
    public bool FireCurrentWeapon(Vector3 position) => Weapons.Current?.FireAtPosition(position) ?? false;

    /// <summary>Replaces all model conditions at once (used when reading bones for other damage states).</summary>
    public void ReplaceModelConditions(FlagSet conditions) => ModelConditions = conditions;

    /// <summary>Back to no experience and regular rank (a Combat Cycle's rider while riding).</summary>
    public void ResetExperience()
    {
        ExperiencePoints = 0;
        SetVeterancy(VeterancyLevel.Regular);
    }

    public void SetStatus(int statusBit, bool on = true) => StatusFlags = StatusFlags.With(statusBit, on);
    public void SetStatus(string status, bool on = true) => SetStatus(Flags.ObjectStatus.IndexOf(status), on);
    public void SetCondition(int conditionBit, bool on = true) => ModelConditions = ModelConditions.With(conditionBit, on);
    public void SetCondition(string condition, bool on = true) => SetCondition(Flags.ModelCondition.IndexOf(condition), on);
    public void SetDisabled(string type, bool on = true)
    {
        bool wasDisabled = IsDisabled;
        bool wasThisType = IsDisabledBy(type);
        DisabledFlags = DisabledFlags.With(Flags.Disabled.IndexOf(type), on);
        if (on && !wasThisType)
        {
            // The rider on top (Overlord add-ons) is disabled along with its carrier.
            if (Contain?.Rider is { } rider && Contain.IsSpecialOverlordStyleContainer && rider != this)
                rider.SetDisabled(type);
            if (type == "DISABLED_UNMANNED" && !IsKindOf("DRONE"))
                OnPilotKilled();
        }
        // The original tints disabled things dark gray, except when held, script-disabled or unmanned.
        string[] noTint = ["DISABLED_HELD", "DISABLED_SCRIPT_DISABLED", "DISABLED_UNMANNED"];
        if (on && IsDisabled && !noTint.Contains(type))
            SetTint(TintStatus.Disabled, true);
        else if (!on && !Enumerable.Range(0, Flags.Disabled.Count).Any(i => DisabledFlags.Has(i) && !noTint.Contains(Flags.Disabled[i])))
            SetTint(TintStatus.Disabled, false);
        if (wasDisabled != IsDisabled)
            foreach (var module in modules.OfType<Modules.IDisabledEdgeModule>().ToList())
                module.OnDisabledEdge(IsDisabled);
    }

    /// <summary>
    /// The driver was shot: a car bomb blows up (scored to the sniper); anything else loses its
    /// experience and its veteran self-repair.
    /// </summary>
    void OnPilotKilled()
    {
        Game.Events.PlaySound(Game.Catalog.MiscAudio("SplatterVehiclePilotsBrain"), Position);
        if (Template.Rules.TryGet("WeaponSet", out var sets)
            && sets.AsList().Any(s => RuleValue.Words(s.GetString("Conditions")).Contains("CARBOMB", StringComparer.OrdinalIgnoreCase)))
        {
            if (Game.FindObject(Body?.LastDamageSourceId ?? 0) is { } sniper)
                sniper.ScoreTheKill(this);
            Kill();
            return;
        }
        ResetExperience();
        foreach (var autoHeal in FindModules<Modules.Updates.AutoHealBehavior>())
            autoHeal.UndoUpgrade();
    }

    /// <summary>Sets a status for a limited time (e.g. FAERIE_FIRE from a weapon). Cleared by the logic loop.</summary>
    public void SetStatusFor(string status, int frames)
    {
        SetStatus(status);
        statusTimers[status] = Game.Frame + frames;
    }

    /// <summary>Disables the object for a while (EMP, hacking, a delayed delivery). A longer existing time is kept.</summary>
    public void SetDisabledFor(string type, int frames)
    {
        SetDisabled(type);
        int until = Game.Frame + frames;
        if (!disabledTimers.TryGetValue(type, out int existing) || existing < until)
            disabledTimers[type] = until;
    }

    public void SetWeaponSetFlag(string flag, bool on = true)
    {
        WeaponSetFlags = WeaponSetFlags.With(Flags.WeaponSet.IndexOf(flag), on);
        Weapons.UpdateFromFlags();
    }

    public void SetWeaponBonus(string condition, bool on = true) =>
        WeaponBonusFlags = WeaponBonusFlags.With(Flags.WeaponBonusCondition.IndexOf(condition), on);

    public void SetArmorSetFlag(string flag, bool on = true)
    {
        ArmorSetFlags = ArmorSetFlags.With(Flags.ArmorSet.IndexOf(flag), on);
        Body?.UpdateArmor();
    }

    public void SetOwner(Player newOwner, Team? newTeam = null) => SetTeam(newTeam ?? newOwner.DefaultTeam);

    /// <summary>
    /// Joins a team (the original's setOrRestoreTeam): the team's player now controls the object, and
    /// an AI takes the team's starting attitude and attack priorities.
    /// </summary>
    public void SetTeam(Team team)
    {
        if (Team == team)
            return;
        var oldOwner = Owner;
        Team?.RemoveMember(this);
        Team = team;
        team.AddMember(this);
        Owner = team.ControllingPlayer;
        if (AI != null)
        {
            AI.Attitude = team.Prototype.TemplateInfo.InitialTeamAttitude;
            if (team.Prototype.AttackPriorityName.Length > 0 && Game.Scripts?.GetAttackInfo(team.Prototype.AttackPriorityName) is { Name.Length: > 0 } info)
                AI.AttackInfo = info;
        }
        if (oldOwner != Owner && oldOwner != null)
            Game.OnOwnerChanged(this, oldOwner);
    }

    /// <summary>The team changed hands: follow its new controlling player.</summary>
    internal void SetOwnerFromTeam()
    {
        if (Team == null || Team.ControllingPlayer == Owner)
            return;
        var old = Owner;
        Owner = Team.ControllingPlayer;
        Game.OnOwnerChanged(this, old);
    }

    /// <summary>Leaves its team (being removed from the game, or the team being deleted).</summary>
    internal void ClearTeam()
    {
        Team?.RemoveMember(this);
        Team = null;
    }

    /// <summary>Taken over by another player (hijacked, captured): skirmish AI conditions count these.</summary>
    public bool IsCaptured { get; set; }

    // ---- Trigger areas (the original's trigger area flags) ----

    struct TriggerInfo
    {
        public Terrain.TriggerArea Area;
        public bool Entered, Exited, IsInside;
    }

    const int MaxTriggerAreaInfos = 5;
    readonly TriggerInfo[] triggerInfo = new TriggerInfo[MaxTriggerAreaInfos];
    int numTriggerAreasActive;
    int enteredOrExitedFrame;
    (int X, int Y) iPos = (int.MinValue, int.MinValue);

    public bool DidEnterOrExit
    {
        get
        {
            if (IsKindOf("INERT"))
                return false;
            int now = Game.Frame;
            return enteredOrExitedFrame == now || enteredOrExitedFrame == now - 1;
        }
    }

    public bool DidEnter(Terrain.TriggerArea area)
    {
        if (!DidEnterOrExit)
            return false;
        for (int i = 0; i < numTriggerAreasActive; i++)
            if (triggerInfo[i].Entered && triggerInfo[i].Area == area)
                return true;
        return false;
    }

    public bool DidExit(Terrain.TriggerArea area)
    {
        if (!DidEnterOrExit)
            return false;
        for (int i = 0; i < numTriggerAreasActive; i++)
            if (triggerInfo[i].Exited && triggerInfo[i].Area == area)
                return true;
        return false;
    }

    public bool IsInside(Terrain.TriggerArea area)
    {
        for (int i = 0; i < numTriggerAreasActive; i++)
            if (triggerInfo[i].IsInside && triggerInfo[i].Area == area)
                return true;
        return false;
    }

    void UpdateTriggerAreaFlags()
    {
        // (As the original: the test reads the compacted slot.)
        int j = 0;
        for (int i = 0; i < numTriggerAreasActive; i++)
        {
            if (!triggerInfo[j].IsInside)
                continue;
            triggerInfo[j].Entered = false;
            triggerInfo[j].Exited = false;
            triggerInfo[j].IsInside = triggerInfo[i].IsInside;
            triggerInfo[j].Area = triggerInfo[i].Area;
            j++;
        }
        numTriggerAreasActive = j;
    }

    internal void InitTriggerAreas() => SetTriggerAreaFlagsForChangeInPosition();

    /// <summary>The original's setTriggerAreaFlagsForChangeInPosition: notes areas entered and left as the object moves.</summary>
    void SetTriggerAreaFlagsForChangeInPosition()
    {
        if (IsKindOf(Kind.Projectile) || IsKindOf("INERT"))
            return;
        var newPos = ((int)Position.X, (int)Position.Y);
        if (newPos == iPos)
            return;
        int now = Game.Frame;
        if (enteredOrExitedFrame != 0 && enteredOrExitedFrame != now)
            UpdateTriggerAreaFlags();
        // (The original tests the previous position here, so exits register a move late.)
        for (int i = 0; i < numTriggerAreasActive; i++)
        {
            if (!triggerInfo[i].Area.Contains(iPos.X, iPos.Y))
            {
                triggerInfo[i].IsInside = false;
                triggerInfo[i].Exited = true;
                enteredOrExitedFrame = now;
                Team?.SetEnteredExited();
            }
        }
        iPos = newPos;
        foreach (var area in Game.Terrain.TriggerAreas)
        {
            bool skip = false;
            for (int i = 0; i < numTriggerAreasActive; i++)
                if (triggerInfo[i].Area == area)
                {
                    skip = true;
                    break;
                }
            if (skip || !area.Contains(iPos.X, iPos.Y) || numTriggerAreasActive >= MaxTriggerAreaInfos)
                continue;
            triggerInfo[numTriggerAreasActive++] = new TriggerInfo { Area = area, IsInside = true, Entered = true };
            enteredOrExitedFrame = now;
            Team?.SetEnteredExited();
        }
    }

    /// <summary>Until this frame, a defector still looks like its old side's unit to its old allies.</summary>
    public int UndetectedDefectorUntilFrame { get; set; }

    /// <summary>
    /// Switches to another team, as the original's Object::defect: not while inside something,
    /// under construction or sold; production is cancelled and refunded first.
    /// </summary>
    public void Defect(Team newTeam, int detectionFrames)
    {
        if (IsContained || newTeam == Owner.DefaultTeam || IsUnderConstruction || HasStatus(Flags.ObjectStatus.IndexOf("SOLD")))
            return;
        FindModule<Modules.Production.ProductionUpdate>()?.CancelAll();
        UndetectedDefectorUntilFrame = detectionFrames > 0 ? Game.Frame + detectionFrames : 0;
        SetTeam(newTeam);
        AI?.Stop(Commands.CommandSource.AI);
    }

    /// <summary>Replaces the template in place (used by upgrades that turn one object into another).</summary>
    public void ChangeTemplate(ObjectTemplate template) => Template = template;

    // ---- Upgrades ----

    /// <summary>True if this object or its player has completed the upgrade.</summary>
    public bool HasUpgrade(string upgrade) => upgrades.Contains(upgrade) || Owner.HasUpgrade(upgrade);
    public bool HasObjectUpgrade(string upgrade) => upgrades.Contains(upgrade);

    public void GiveUpgrade(string upgrade)
    {
        if (upgrades.Add(upgrade))
            CheckUpgradeModules();
    }

    public void RemoveUpgrade(string upgrade) => upgrades.Remove(upgrade);

    /// <summary>Lets every upgrade module check whether its triggers are now satisfied.</summary>
    public void CheckUpgradeModules()
    {
        foreach (var module in modules.OfType<UpgradeModule>().ToList())
            module.CheckUpgrades();
    }

    // ---- Experience ----

    public void AddExperience(int points)
    {
        if (ExperienceSinkId != 0 && Game.FindObject(ExperienceSinkId) is { } sink && sink != this)
        {
            sink.AddExperience(points);
            return;
        }
        if (!RuleValue.Bool(Template.Rules.GetString("IsTrainable"), false) || points <= 0)
            return;
        ExperiencePoints += (int)(points * ExperienceScalar);
        int[] required = Template.Rules.GetString("ExperienceRequired").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => RuleValue.Int(w)).ToArray();
        var level = Veterancy;
        while ((int)level < 3 && (int)level + 1 < required.Length && ExperiencePoints >= required[(int)level + 1])
            level++;
        if (level != Veterancy)
            SetVeterancy(level);
    }

    public void SetVeterancy(VeterancyLevel level)
    {
        if (level == Veterancy)
            return;
        var old = Veterancy;
        Veterancy = level;
        SetWeaponBonus("VETERAN", level == VeterancyLevel.Veteran);
        SetWeaponBonus("ELITE", level == VeterancyLevel.Elite);
        SetWeaponBonus("HERO", level == VeterancyLevel.Heroic);
        SetWeaponSetFlag("VETERAN", level == VeterancyLevel.Veteran);
        SetWeaponSetFlag("ELITE", level == VeterancyLevel.Elite);
        SetWeaponSetFlag("HERO", level == VeterancyLevel.Heroic);
        SetArmorSetFlag("VETERAN", level == VeterancyLevel.Veteran);
        SetArmorSetFlag("ELITE", level == VeterancyLevel.Elite);
        SetArmorSetFlag("HERO", level == VeterancyLevel.Heroic);
        Body?.OnVeterancyChanged(old, level);
        Game.Events.VeterancyChanged(this, old, level);
    }

    /// <summary>Experience given to whoever kills this object, by its veterancy level.</summary>
    public int ExperienceValue
    {
        get
        {
            var values = Template.Rules.GetString("ExperienceValue").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return values.Length > (int)Veterancy ? RuleValue.Int(values[(int)Veterancy]) : 0;
        }
    }

    // ---- Damage and death ----

    /// <summary>Applies damage through the body (armor, damage states, death).</summary>
    public void Damage(DamageInfo damage) => Body?.AttemptDamage(damage);

    /// <summary>Kills the object outright.</summary>
    public void Kill(string deathType = "NORMAL", uint killerId = 0)
    {
        if (IsEffectivelyDead)
            return;
        if (Body != null)
            Body.AttemptDamage(DamageInfo.Killing(deathType, killerId));
        else
            OnDie(DamageInfo.Killing(deathType, killerId));
    }

    bool hasDied;

    /// <summary>Built for free by a GLA rebuild hole (does not score as a new building).</summary>
    public bool IsRebuild { get; set; }

    /// <summary>
    /// This object killed the victim (the original's scoreTheKill): the victim's owner records a loss; a kill
    /// of an enemy's playable-side unit scores, earns skill points and bounty, and gives experience
    /// (none for things under construction).
    /// </summary>
    public void ScoreTheKill(GameObject victim)
    {
        var victimOwner = victim.Owner;
        if (!victimOwner.IsPlayableSide || victim.IsKindOf("IGNORED_IN_GUI"))
            return;
        victimOwner.Score.AddObjectLost(victim);
        if (!IsEnemyOf(victim) || Owner == victimOwner)
            return;
        Owner.Score.AddObjectDestroyed(victim);
        Owner.AddSkillPointsForKill(victim);
        Owner.DoBountyForKill(this, victim);
        if (!victim.IsUnderConstruction)
            AddExperience(victim.ExperienceValue);
    }

    /// <summary>A rebuilt bridge or tower comes back from rubble and can be destroyed again.</summary>
    internal void Revive() => hasDied = false;

    /// <summary>Called by the body when health reaches zero: runs die modules, then the object is removed (unless a module delays it).</summary>
    public void OnDie(DamageInfo killingBlow)
    {
        if (hasDied)
            return;
        hasDied = true;

        if (Game.FindObject(killingBlow.SourceId) is { } killer && killer != this)
            killer.ScoreTheKill(this);

        SetCondition(Condition.Dying);
        Game.Events.ObjectDied(this, killingBlow);
        Game.Scripts?.OnObjectDied(this);

        CheckAndDetonateBoobyTrap(null); // a booby trap goes off with what it is on
        // A spawned unit tells its spawner (which replaces it), and its team hears of the loss.
        Game.FindObject(ProducerId)?.FindModule<Modules.Behaviors.SpawnBehavior>()?.OnSpawnDeath(Id, killingBlow);
        Team?.NotifyTeamOfObjectDeath();

        bool delayed = false;
        foreach (var die in modules.OfType<IDieModule>().ToList())
        {
            die.OnDie(killingBlow);
            if (die is IDelaysDestruction { IsDelayingDestruction: true })
                delayed = true;
        }

        if (!delayed)
            Game.DestroyObject(this);
    }

    // ---- Per-frame plumbing used by GameLogic ----

    internal void RunUpdates(UpdatePhase phase)
    {
        int frame = Game.Frame;
        for (int i = 0; i < updates.Count; i++)
        {
            var slot = updates[i];
            if (slot.Phase != phase || slot.NextFrame > frame)
                continue;
            // A disabled object only runs the updates that work while disabled that way.
            if (!DisabledFlags.IsEmpty && !DisabledFlags.HasAny(slot.Module.DisabledTypesToProcess))
            {
                slot.NextFrame = frame + 1;
                continue;
            }
            long started = GameProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            int sleep = slot.Module.Update();
            if (GameProfiler.Enabled)
                GameProfiler.Add(slot.Module.GetType().Name, started);
            slot.NextFrame = frame + 1 + Math.Max(0, sleep);
            if (IsDestroyed)
                return;
        }
        if (phase != UpdatePhase.Final)
            return;
        UpdateTempWeaponBonus();

        if (statusTimers.Count > 0)
        {
            foreach (var (status, until) in statusTimers.ToList())
            {
                if (until > frame)
                    continue;
                SetStatus(status, false);
                statusTimers.Remove(status);
            }
        }
        if (disabledTimers.Count > 0)
        {
            foreach (var (type, until) in disabledTimers.ToList())
            {
                if (until > frame)
                    continue;
                SetDisabled(type, false);
                disabledTimers.Remove(type);
            }
        }
    }

    /// <summary>Wakes an update module before its sleep ends (e.g. a new order arrived).</summary>
    public void WakeUpdate(IUpdateModule module)
    {
        foreach (var slot in updates)
            if (slot.Module == module)
                slot.NextFrame = Math.Min(slot.NextFrame, Game.Frame);
    }

    /// <summary>Puts an update module to sleep for a number of frames (by default until woken).</summary>
    public void SleepUpdate(IUpdateModule module, int frames = LogicTime.SleepForever)
    {
        foreach (var slot in updates)
            if (slot.Module == module)
                slot.NextFrame = Game.Frame + frames;
    }

    internal void MarkDestroyed() => SetStatus(Core.Status.Destroyed);
}

/// <summary>A die module that keeps the object alive for a while (e.g. SlowDeathBehavior plays a death animation).</summary>
public interface IDelaysDestruction
{
    bool IsDelayingDestruction { get; }
}

/// <summary>The original's drawable tint states.</summary>
[Flags]
public enum TintStatus
{
    None = 0,
    Disabled = 1,
    Irradiated = 2,
    Poisoned = 4,
    GainingSubdualDamage = 8,
    Frenzy = 16,
}

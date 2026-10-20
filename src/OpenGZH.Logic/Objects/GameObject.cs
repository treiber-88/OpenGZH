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
public sealed class GameObject
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
    public uint ContainedById { get; set; }

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
        if (module is IUpdateModule update)
            updates.Add(new UpdateSlot(update) { NextFrame = Game.Frame });
    }

    // ---- Queries ----

    public bool IsKindOf(int kindBit) => KindOf.Has(kindBit);
    public bool IsKindOf(string kind) => KindOf.Has(Flags.KindOf.IndexOf(kind));
    public bool HasStatus(int statusBit) => StatusFlags.Has(statusBit);
    public bool HasCondition(int conditionBit) => ModelConditions.Has(conditionBit);
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
    public float Radius => Math.Max(Template.GeometryMajorRadius, Template.GeometryMinorRadius);

    public float Height => Template.GeometryHeight;

    /// <summary>Radius of the circle around the footprint (a box's corner distance; a sphere or cylinder's radius).</summary>
    public float BoundingCircleRadius => Template.Geometry.BoundingCircleRadius;
    public float BoundingSphereRadius => Template.Geometry.BoundingSphereRadius;
    public Core.Geometry Geometry => Template.Geometry;

    /// <summary>Height above the ground (or bridge) beneath it.</summary>
    public float HeightAboveTerrain => Position.Z - Game.Terrain.LayerHeight(Position.X, Position.Y);

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

    public void SetPosition(Vector3 position)
    {
        Position = position;
        Game.Partition.Moved(this);
    }

    public void SetOrientation(float radians)
    {
        Orientation = MathUtil.NormalizeAngle(radians);
    }

    public void SetStatus(int statusBit, bool on = true) => StatusFlags = StatusFlags.With(statusBit, on);
    public void SetStatus(string status, bool on = true) => SetStatus(Flags.ObjectStatus.IndexOf(status), on);
    public void SetCondition(int conditionBit, bool on = true) => ModelConditions = ModelConditions.With(conditionBit, on);
    public void SetCondition(string condition, bool on = true) => SetCondition(Flags.ModelCondition.IndexOf(condition), on);
    public void SetDisabled(string type, bool on = true)
    {
        bool wasDisabled = IsDisabled;
        DisabledFlags = DisabledFlags.With(Flags.Disabled.IndexOf(type), on);
        if (wasDisabled != IsDisabled)
            foreach (var module in modules.OfType<Modules.IDisabledEdgeModule>().ToList())
                module.OnDisabledEdge(IsDisabled);
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

    public void SetOwner(Player newOwner, Team? newTeam = null)
    {
        var old = Owner;
        Owner = newOwner;
        Team = newTeam ?? newOwner.DefaultTeam;
        if (old != newOwner)
            Game.OnOwnerChanged(this, old);
    }

    public void SetTeam(Team team)
    {
        Team = team;
        if (team.Owner != Owner)
            SetOwner(team.Owner, team);
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

    /// <summary>Called by the body when health reaches zero: runs die modules, then the object is removed (unless a module delays it).</summary>
    public void OnDie(DamageInfo killingBlow)
    {
        if (hasDied)
            return;
        hasDied = true;

        // Like the original, only kills of enemies count, and things under construction give no experience.
        if (Game.FindObject(killingBlow.SourceId) is { } killer && killer != this && killer.Owner.IsEnemyOf(Owner))
        {
            killer.Owner.ScoreKill(this);
            killer.Owner.DoBountyForKill(killer, this);
            if (!IsUnderConstruction)
                killer.AddExperience(ExperienceValue);
        }

        SetCondition(Condition.Dying);
        Game.Events.ObjectDied(this, killingBlow);
        Game.Scripts?.OnObjectDied(this);

        CheckAndDetonateBoobyTrap(null); // a booby trap goes off with what it is on

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
            int sleep = slot.Module.Update();
            slot.NextFrame = frame + 1 + Math.Max(0, sleep);
            if (IsDestroyed)
                return;
        }
        if (phase != UpdatePhase.Final)
            return;

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

using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Spatial;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// Lays mines around its object (China's Land Mines upgrade, mine-laying bombs). When triggered (by
/// its upgrade, or on death with GenerateOnlyOnDeath) it places MineName objects:
/// - SmartBorder: rings around the object's footprint, one mine wide, out to DistanceAroundObject
///   (with a mine in the middle unless SmartBorderSkipInterior);
/// - BorderOnly: a single ring DistanceAroundObject out;
/// - otherwise scattered over the area at MinesPerSquareFoot.
/// Rings follow a box footprint unless AlwaysCircular. RandomJitter shuffles each mine a little.
/// No mine goes in water, on a cliff, or under a structure (SkipIfThisMuchUnderStructure).
/// Upgradable fields are re-laid with UpgradedMineName once Upgrade_ChinaEMPMines is in.
/// </summary>
public sealed class GenerateMinefieldBehavior : UpgradeModule, IUpdateModule, IDieModule, IMinefieldTarget
{
    string mineName = "", upgradedMineName = "", genFx = "";
    float distanceAroundObject, minesPerSquareFoot, randomJitter, skipIfUnderStructure;
    bool onDeath, borderOnly, alwaysCircular, upgradable, smartBorder, smartBorderSkipInterior;

    Vector3? target;
    bool generated, upgraded;
    readonly List<uint> mines = new();

    protected override void InitializeUpgrade()
    {
        mineName = Setting("MineName");
        upgradedMineName = Setting("UpgradedMineName");
        genFx = Setting("GenerationFX");
        distanceAroundObject = Definition.Settings.Has("DistanceAroundObject") ? FloatSetting("DistanceAroundObject") : Game.Catalog.GameData.StandardMinefieldDistance;
        minesPerSquareFoot = Definition.Settings.Has("MinesPerSquareFoot") ? FloatSetting("MinesPerSquareFoot") : Game.Catalog.GameData.StandardMinefieldDensity;
        onDeath = BoolSetting("GenerateOnlyOnDeath");
        borderOnly = BoolSetting("BorderOnly", true);
        smartBorder = BoolSetting("SmartBorder");
        smartBorderSkipInterior = BoolSetting("SmartBorderSkipInterior", true);
        alwaysCircular = BoolSetting("AlwaysCircular");
        upgradable = BoolSetting("Upgradable");
        randomJitter = FloatSetting("RandomJitter");
        skipIfUnderStructure = Definition.Settings.Has("SkipIfThisMuchUnderStructure") ? FloatSetting("SkipIfThisMuchUnderStructure") : 0.33f;
    }

    protected override void OnUpgrade() => PlaceMines();

    public void OnDie(DamageInfo killingBlow)
    {
        if (onDeath)
            PlaceMines();
    }

    public void SetMinefieldTarget(Vector3 position) => target = position;

    Vector3 Target => target ?? Owner.Position;

    static Geometry Expand(Geometry g, float amount) =>
        new(g.Type, g.MajorRadius + amount, g.MinorRadius + amount, g.Height, g.IsSmall);

    static Geometry Circular(Geometry g) =>
        new(GeometryType.Cylinder, g.BoundingCircleRadius, g.BoundingCircleRadius, 1);

    GameObject? PlaceMineAt(Vector3 pt, Templates.ObjectTemplate mineTemplate)
    {
        var terrain = Game.Terrain;
        if (terrain.IsUnderwater(pt.X, pt.Y, out _))
            return null;
        var cell = terrain.CellAt(pt.X, pt.Y);
        if (terrain.IsCliffCell(cell.X, cell.Y))
            return null;
        float orient = Game.Random.Float(-MathF.PI, MathF.PI);
        float mineRadius = mineTemplate.Geometry.BoundingCircleRadius;
        var geom = Expand(mineTemplate.Geometry, mineRadius * -(1 - skipIfUnderStructure));
        var shape = new Collision.Shape(pt, geom, orient);
        foreach (var them in Game.Partition.Query(pt, geom.BoundingCircleRadius))
            if (them.IsKindOf(Kind.Structure) && Collision.Collides(shape, new Collision.Shape(them.Position, them.Geometry, them.Orientation), out _))
                return null;

        var mine = Game.CreateObject(mineTemplate.Name, Owner.Owner, pt, orient);
        if (mine == null)
            return null;
        mine.ProducerId = Owner.Id;
        mine.FindModule<MinefieldBehavior>()?.SetScootParms(Owner.Position, pt);
        if (upgradable)
            mines.Add(mine.Id);
        return mine;
    }

    void Jitter(ref Vector3 pt, float amount)
    {
        pt.X += Game.Random.Float(-amount, amount);
        pt.Y += Game.Random.Float(-amount, amount);
    }

    void PlaceMinesAlongLine(Vector3 start, Vector3 end, Templates.ObjectTemplate mineTemplate, bool skipOneAtStart)
    {
        float dx = end.X - start.X, dy = end.Y - start.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        float mineRadius = mineTemplate.Geometry.BoundingCircleRadius;
        int count = Math.Max(1, (int)MathF.Ceiling(len / (mineRadius * 2)));
        float inc = len / count;
        if (len <= 0)
            return;
        for (float place = skipOneAtStart ? inc : 0; place <= len; place += inc)
        {
            var pt = new Vector3(start.X + place * dx / len, start.Y + place * dy / len, 0);
            pt.Z = Game.Terrain.GroundHeight(pt.X, pt.Y);
            Jitter(ref pt, mineRadius * randomJitter);
            PlaceMineAt(pt, mineTemplate);
        }
    }

    void PlaceMinesAroundRect(Vector3 pos, float major, float minor, Templates.ObjectTemplate mineTemplate)
    {
        // As in the original, the corners are rotated by the object's facing but not offset by the target position.
        var m = Matrix4x4.CreateRotationZ(Owner.Orientation) * Matrix4x4.CreateTranslation(Owner.Position);
        Vector3 Corner(float a, float b) => Vector3.Transform(new Vector3(a, b, 0), m);
        var p0 = Corner(major, minor);
        var p1 = Corner(-major, minor);
        var p2 = Corner(-major, -minor);
        var p3 = Corner(major, -minor);
        PlaceMinesAlongLine(p0, p1, mineTemplate, true);
        PlaceMinesAlongLine(p1, p2, mineTemplate, true);
        PlaceMinesAlongLine(p2, p3, mineTemplate, true);
        PlaceMinesAlongLine(p3, p0, mineTemplate, true);
    }

    void PlaceMinesAroundCircle(Vector3 pos, float radius, Templates.ObjectTemplate mineTemplate)
    {
        float mineRadius = mineTemplate.Geometry.BoundingCircleRadius;
        int count = Math.Max(1, (int)MathF.Ceiling(2 * MathF.PI * radius / (mineRadius * 2)));
        float inc = 2 * MathF.PI / count;
        float limit = 2 * MathF.PI - inc * 0.5f;
        for (float angle = 0; angle < limit; angle += inc)
        {
            var pt = new Vector3(pos.X + radius * MathF.Cos(angle), pos.Y + radius * MathF.Sin(angle), 0);
            pt.Z = Game.Terrain.GroundHeight(pt.X, pt.Y);
            Jitter(ref pt, mineRadius * randomJitter);
            PlaceMineAt(pt, mineTemplate);
        }
    }

    Vector3 RandomOffsetWithin(Geometry g)
    {
        if (g.Type == GeometryType.Box)
            return new Vector3(Game.Random.Float(-g.MajorRadius, g.MajorRadius), Game.Random.Float(-g.MinorRadius, g.MinorRadius), 0);
        while (true)
        {
            var p = new Vector3(Game.Random.Float(-g.MajorRadius, g.MajorRadius), Game.Random.Float(-g.MajorRadius, g.MajorRadius), 0);
            if (p.X * p.X + p.Y * p.Y <= g.MajorRadius * g.MajorRadius)
                return p;
        }
    }

    static bool IsPointInFootprint(Geometry g, Vector3 center, Vector3 pt) => g.Type == GeometryType.Box
        ? pt.X >= center.X - g.MajorRadius && pt.X <= center.X + g.MajorRadius && pt.Y >= center.Y - g.MinorRadius && pt.Y <= center.Y + g.MinorRadius
        : MathUtil.Distance2D(pt, center) <= g.MajorRadius;

    void PlaceMinesInFootprint(Geometry geom, Templates.ObjectTemplate mineTemplate)
    {
        int count = Math.Max(1, (int)MathF.Ceiling(minesPerSquareFoot * geom.FootprintArea));
        var center = Target;
        var placed = new List<GameObject>();
        float minDist = mineTemplate.Geometry.BoundingCircleRadius * 2;
        for (int i = 0; i < count; i++)
        {
            Vector3 pt;
            int retries = 100;
            do
            {
                pt = RandomOffsetWithin(geom) + center;
                retries--;
            } while (placed.Any(m => MathUtil.Distance2D(m.Position, pt) < minDist) && retries > 0);
            if (IsPointInFootprint(Owner.Geometry, center, pt))
                continue;
            if (PlaceMineAt(pt, mineTemplate) is { } mine)
                placed.Add(mine);
        }
    }

    void PlaceMines()
    {
        if (generated)
            return;
        generated = true;
        var mineTemplate = Game.Templates.Find(upgraded ? upgradedMineName : mineName);
        if (mineTemplate == null)
            return;
        var center = Target;
        if (smartBorder)
        {
            var geom = Owner.Geometry;
            if (!smartBorderSkipInterior)
            {
                geom = mineTemplate.Geometry;
                PlaceMineAt(center, mineTemplate);
            }
            if (alwaysCircular)
                geom = Circular(geom);
            float mineRadius = mineTemplate.Geometry.BoundingCircleRadius;
            geom = Expand(geom, mineRadius);
            do
            {
                if (geom.Type == GeometryType.Box && !alwaysCircular)
                    PlaceMinesAroundRect(center, geom.MajorRadius, geom.MinorRadius, mineTemplate);
                else
                    PlaceMinesAroundCircle(center, geom.MajorRadius, mineTemplate);
                geom = Expand(geom, mineRadius * 2);
            } while (geom.BoundingCircleRadius < distanceAroundObject);
        }
        else if (borderOnly)
        {
            var geom = Expand(Owner.Geometry, distanceAroundObject);
            if (geom.Type == GeometryType.Box && !alwaysCircular)
                PlaceMinesAroundRect(center, geom.MajorRadius, geom.MinorRadius, mineTemplate);
            else
                PlaceMinesAroundCircle(center, geom.MajorRadius, mineTemplate);
        }
        else
        {
            var geom = Expand(Owner.Geometry, distanceAroundObject);
            if (alwaysCircular)
                geom = Circular(geom);
            PlaceMinesInFootprint(geom, mineTemplate);
        }
        if (genFx.Length > 0)
            Game.Events.PlayFx(genFx, Owner.Position, Owner);
    }

    public int Update()
    {
        if (upgraded || !upgradable)
            return LogicTime.SleepForever;
        if (generated && Owner.HasObjectUpgrade("Upgrade_ChinaEMPMines"))
        {
            upgraded = true;
            foreach (var id in mines)
                if (Game.FindObject(id) is { } old)
                    Game.DestroyObject(old);
            mines.Clear();
            generated = false;
            PlaceMines();
        }
        return 0;
    }
}

/// <summary>
/// A mine (or patch of NumVirtualMines mines sharing one health bar). It goes off (DetonationWeapon,
/// CreationList) when touched by someone of a DetonatedBy relationship - once per detonator until it
/// moves RepeatDetonateMoveThresh - and when damaged enough to lose a virtual mine. Mines with
/// Regenerates come back as they heal (they stay as rubble at zero); if StopsRegenAfterCreatorDies,
/// they stop regenerating and drain away (DegenPercentPerSecondAfterCreatorDies) once their building
/// is gone. Units clearing mines (anti-mine weapon) are immune while they work. Freshly laid mines
/// slide out from their layer (ScootFromStartingPointTime).
/// </summary>
public sealed class MinefieldBehavior : Module, IUpdateModule, ICollideModule, IDamageModule, IDieModule, ICreateModule
{
    const float MinHealth = 0.1f;
    const int MaxImmunity = 3;

    WeaponTemplate? detonationWeapon;
    bool detonatedByAllies, detonatedByEnemies = true, detonatedByNeutral = true;
    bool stopsRegenAfterCreatorDies, workersDetonate;
    int creatorDeathCheckRate, scootTime, numVirtualMines;
    float repeatDetonateMoveThresh, drainPercentPerSecond;
    string creationList = "";

    int nextDeathCheckFrame;
    int scootFramesLeft;
    Vector3 scootVel, scootAccel;
    readonly List<(uint Id, Vector3 Where)> detonators = new();
    readonly (uint Id, int CollideFrame)[] immunes = new (uint, int)[MaxImmunity];
    bool ignoreDamage, regenerates, draining;
    int virtualMinesRemaining;

    protected override void Initialize()
    {
        detonationWeapon = Game.Catalog.Weapon(Setting("DetonationWeapon"));
        if (Definition.Settings.Has("DetonatedBy"))
        {
            var words = RuleValue.Words(Setting("DetonatedBy"));
            detonatedByAllies = words.Contains("ALLIES", StringComparer.OrdinalIgnoreCase);
            detonatedByEnemies = words.Contains("ENEMIES", StringComparer.OrdinalIgnoreCase);
            detonatedByNeutral = words.Contains("NEUTRAL", StringComparer.OrdinalIgnoreCase);
        }
        stopsRegenAfterCreatorDies = BoolSetting("StopsRegenAfterCreatorDies", true);
        regenerates = BoolSetting("Regenerates");
        workersDetonate = BoolSetting("WorkersDetonate");
        creatorDeathCheckRate = Definition.Settings.Has("CreatorDeathCheckRate") ? FramesSetting("CreatorDeathCheckRate") : LogicTime.FramesPerSecond;
        scootTime = FramesSetting("ScootFromStartingPointTime");
        numVirtualMines = IntSetting("NumVirtualMines", 1);
        repeatDetonateMoveThresh = FloatSetting("RepeatDetonateMoveThresh", 1);
        drainPercentPerSecond = FloatSetting("DegenPercentPerSecondAfterCreatorDies");
        creationList = Setting("CreationList");
        virtualMinesRemaining = numVirtualMines;
    }

    public void OnCreate() => Owner.SetStatus("NO_ATTACK_FROM_AI");

    public int Update()
    {
        int now = Frame;
        if (scootFramesLeft > 0)
        {
            scootVel += scootAccel;
            var pt = Owner.Position + scootVel;
            float ground = Game.Terrain.LayerHeight(pt.X, pt.Y);
            if (pt.Z < ground || scootFramesLeft <= 1)
                pt.Z = ground;
            Owner.SetPosition(pt);
            scootFramesLeft--;
        }

        for (int i = 0; i < MaxImmunity; i++)
        {
            if (immunes[i].Id == 0)
                continue;
            if (Game.FindObject(immunes[i].Id) == null || now > immunes[i].CollideFrame + 2)
                immunes[i] = (0, 0);
        }

        if (now >= nextDeathCheckFrame && regenerates && stopsRegenAfterCreatorDies)
        {
            nextDeathCheckFrame = now + creatorDeathCheckRate;
            if (Owner.ProducerId != 0 && Game.FindObject(Owner.ProducerId) is not { IsEffectivelyDead: false })
            {
                regenerates = false;
                draining = true;
                Owner.FindModule<Updates.AutoHealBehavior>()?.StopHealing();
            }
        }

        if (draining)
            Owner.Damage(new DamageInfo { Amount = Owner.MaxHealth * drainPercentPerSecond / LogicTime.FramesPerSecond, SourceId = Owner.Id, DamageType = "UNRESISTABLE", DeathType = "NORMAL" });
        return 0;
    }

    void DetonateOnce(Vector3 position)
    {
        if (detonationWeapon != null)
            Weapon.FireTemporary(detonationWeapon, Owner, position);
        if (virtualMinesRemaining > 0)
            virtualMinesRemaining--;
        if (!regenerates && virtualMinesRemaining == 0)
            Game.DestroyObject(Owner);
        else if (Owner.Body is { } body)
        {
            float desired = Math.Max(MinHealth, (float)virtualMinesRemaining / numVirtualMines * body.MaxHealth);
            float amount = body.Health - desired;
            if (amount > 0)
            {
                ignoreDamage = true;
                Owner.Damage(new DamageInfo { DamageType = "UNRESISTABLE", DeathType = "NONE", SourceId = Owner.Id, Amount = amount });
                ignoreDamage = false;
            }
        }
        SetRubble(virtualMinesRemaining == 0);
        if (creationList.Length > 0)
            Game.ObjectCreation.Run(creationList, Owner, Owner.Position, Owner);
    }

    void SetRubble(bool rubble)
    {
        Owner.SetCondition("RUBBLE", rubble);
        Owner.SetStatus("MASKED", rubble);
    }

    public void OnCollide(GameObject? other, Vector3 location, Vector3 normal)
    {
        if (other == null || other.IsEffectivelyDead || virtualMinesRemaining == 0)
            return;
        int now = Frame;
        for (int i = 0; i < MaxImmunity; i++)
        {
            if (immunes[i].Id == other.Id)
            {
                immunes[i].CollideFrame = now;
                return;
            }
        }
        if (!workersDetonate && other.IsKindOf(Kind.Infantry) && other.IsKindOf("DOZER"))
            return;
        var relation = Owner.Owner.RelationshipTo(other.Owner);
        bool allowed = relation switch
        {
            Players.Relationship.Allies => detonatedByAllies,
            Players.Relationship.Enemies => detonatedByEnemies,
            _ => detonatedByNeutral,
        };
        if (!allowed || scootFramesLeft > 0)
            return;

        // Someone clearing mines doesn't set them off.
        if (other.AI is { IsClearingMines: true } && other.AI.TargetId != 0)
        {
            for (int i = 0; i < MaxImmunity; i++)
            {
                if (immunes[i].Id == 0 || immunes[i].Id == other.Id)
                {
                    immunes[i] = (other.Id, now);
                    break;
                }
            }
            return;
        }

        int index = detonators.FindIndex(d => d.Id == other.Id);
        if (index >= 0)
        {
            if (Vector3.DistanceSquared(other.Position, detonators[index].Where) <= repeatDetonateMoveThresh * repeatDetonateMoveThresh)
                return;
            detonators[index] = (other.Id, other.Position);
        }
        else
            detonators.Add((other.Id, other.Position));

        DetonateOnce(ClipToFootprint(other.Position));
    }

    Vector3 ClipToFootprint(Vector3 pt)
    {
        var g = Owner.Geometry;
        var c = Owner.Position;
        if (g.Type == GeometryType.Box)
            return pt with { X = Math.Clamp(pt.X, c.X - g.MajorRadius, c.X + g.MajorRadius), Y = Math.Clamp(pt.Y, c.Y - g.MinorRadius, c.Y + g.MinorRadius) };
        float dx = pt.X - c.X, dy = pt.Y - c.Y;
        float r = MathF.Sqrt(dx * dx + dy * dy);
        return r > g.MajorRadius ? pt with { X = c.X + dx * g.MajorRadius / r, Y = c.Y + dy * g.MajorRadius / r } : pt;
    }

    public void OnDamage(DamageInfo damage) => SyncVirtualMines(damage);

    public void OnHealing(DamageInfo healing) => SyncVirtualMines(healing);

    void SyncVirtualMines(DamageInfo damage)
    {
        if (ignoreDamage || Owner.Body is not { } body)
            return;
        for (int guard = 0; guard < 64; guard++)
        {
            float expectedF = numVirtualMines * body.Health / body.MaxHealth;
            int expected = damage.DamageType == "HEALING" ? (int)MathF.Floor(expectedF) : (int)MathF.Ceiling(expectedF);
            expected = Math.Min(expected, numVirtualMines);
            if (virtualMinesRemaining < expected)
                virtualMinesRemaining = expected;
            else if (virtualMinesRemaining > expected)
            {
                if (draining && damage.SourceId == Owner.Id && damage.DamageType == "UNRESISTABLE")
                    virtualMinesRemaining--;
                else
                    DetonateOnce(Owner.Position);
                if (Owner.IsDestroyed)
                    return;
            }
            else
                break;
        }
        if (virtualMinesRemaining == 0)
        {
            if (regenerates && body.Health < MinHealth)
                body.SetHealth(MinHealth);
            SetRubble(true);
        }
        else
            SetRubble(false);
    }

    public void OnDie(DamageInfo killingBlow) => Game.DestroyObject(Owner);

    /// <summary>A mine-clearing weapon removed it (regenerating mines are left as rubble instead).</summary>
    public void Disarm()
    {
        if (!regenerates)
        {
            Game.DestroyObject(Owner);
            return;
        }
        if (Owner.Body is { } body)
        {
            ignoreDamage = true;
            Owner.Damage(new DamageInfo { DamageType = "UNRESISTABLE", DeathType = "NONE", SourceId = Owner.Id, Amount = body.Health - MinHealth });
            ignoreDamage = false;
        }
        virtualMinesRemaining = 0;
        SetRubble(true);
    }

    /// <summary>A mine thrown out by its layer slides (and falls) from there to its spot.</summary>
    public void SetScootParms(Vector3 start, Vector3 end)
    {
        var endOnGround = end with { Z = Game.Terrain.GroundHeight(end.X, end.Y) };
        int time = scootTime;
        float gravity = MathF.Abs(Game.Catalog.GameData.Gravity);
        if (start.Z > endOnGround.Z)
            time = Math.Max(time, (int)MathF.Ceiling(MathF.Sqrt(2 * (start.Z - endOnGround.Z) / gravity)));
        float dx = endOnGround.X - start.X, dy = endOnGround.Y - start.Y, dz = endOnGround.Z - start.Z;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (time == 0 || (dist <= 0.1f && MathF.Abs(dz) <= 0.1f))
        {
            Owner.SetPosition(endOnGround);
            scootFramesLeft = 0;
            return;
        }
        float t = time;
        float speed = dist / t;
        float accel = MathF.Abs(2 * (dist - speed * t) / (t * t));
        float nx = dist <= 0.1f ? 0 : dx / dist, ny = dist <= 0.1f ? 0 : dy / dist;
        scootVel = new Vector3(nx * speed, ny * speed, 0);
        scootAccel = new Vector3(-nx * accel, -ny * accel, Game.Catalog.GameData.Gravity);
        Owner.SetPosition(start);
        scootFramesLeft = time;
    }
}

/// <summary>
/// A GLA demo trap: in proximity mode it goes off (DetonationWeapon) when an enemy on the ground
/// comes within TriggerDetonationRange (not if friends are near, unless
/// AutoDetonationWithFriendsInvolved), checked every ScanRate; in manual mode only on command
/// (switching to its detonation weapon slot). DetonateWhenKilled: it also goes off when destroyed.
/// Dozers clearing mines don't trigger it.
/// </summary>
public sealed class DemoTrapUpdate : Module, IUpdateModule, ICreateModule
{
    bool defaultProximity, friendlyDetonation, detonateWhenKilled;
    WeaponSlot detonationSlot, proximitySlot, manualSlot;
    float triggerRange;
    FlagSet ignoreKindOf;
    int scanFrames, nextScanFrames;
    WeaponTemplate? detonationWeapon;
    bool detonated;

    static WeaponSlot Slot(string text) => text.ToUpperInvariant() switch
    {
        "SECONDARY" => WeaponSlot.Secondary,
        "TERTIARY" => WeaponSlot.Tertiary,
        _ => WeaponSlot.Primary,
    };

    protected override void Initialize()
    {
        defaultProximity = BoolSetting("DefaultProximityMode");
        detonationSlot = Slot(Setting("DetonationWeaponSlot"));
        proximitySlot = Slot(Setting("ProximityModeWeaponSlot"));
        manualSlot = Slot(Setting("ManualModeWeaponSlot"));
        triggerRange = FloatSetting("TriggerDetonationRange");
        ignoreKindOf = Flags.KindOf.Parse(Setting("IgnoreTargetTypes"));
        scanFrames = FramesSetting("ScanRate");
        friendlyDetonation = BoolSetting("AutoDetonationWithFriendsInvolved");
        detonationWeapon = Game.Catalog.Weapon(Setting("DetonationWeapon"));
        detonateWhenKilled = BoolSetting("DetonateWhenKilled");
    }

    public void OnCreate()
    {
        Owner.SetWeaponSetFlag("VETERAN");
        Owner.Weapons.LockSlot(defaultProximity ? proximitySlot : manualSlot);
    }

    public int Update()
    {
        if (detonated)
            return 0;
        if (Owner.IsUnderConstruction || Owner.HasStatus(Status.Sold))
            return 0;
        if (Owner.IsEffectivelyDead)
        {
            if (detonateWhenKilled)
                Detonate();
            return 0;
        }
        var slot = Owner.Weapons.CurrentSlot;
        if (slot == detonationSlot)
        {
            Detonate();
            return 0;
        }
        if (nextScanFrames > 0)
        {
            nextScanFrames--;
            return 0;
        }
        if (slot == manualSlot)
            return 0;
        nextScanFrames = scanFrames;

        bool shallDetonate = false;
        foreach (var other in Game.Partition.Query(Owner.Position, triggerRange))
        {
            if (!ignoreKindOf.IsEmpty && other.KindOf.HasAny(ignoreKindOf))
                continue;
            if (other.IsEffectivelyDead || other == Owner)
                continue;
            if (other.IsKindOf("DOZER") && other.Weapons.Current is { } w && w.Template.DamageType == "DISARM" && other.HasStatus(Status.IsAttacking))
                continue;
            if (!Owner.Owner.IsEnemyOf(other.Owner))
            {
                if (!friendlyDetonation)
                    return 0;
                continue;
            }
            if (other.IsAboveTerrain)
                continue;
            if (MathUtil.DistanceSquared2D(Owner.Position, other.Position) <= triggerRange * triggerRange)
            {
                shallDetonate = true;
                if (friendlyDetonation)
                    break;
            }
        }
        if (shallDetonate)
            Detonate();
        return 0;
    }

    /// <summary>The player chose a mode (proximity or manual) or set it off (the detonation slot).</summary>
    public void SetMode(WeaponSlot slot) => Owner.Weapons.LockSlot(slot);

    void Detonate()
    {
        if (!Owner.IsUnderConstruction && !Owner.HasStatus(Status.Sold) && detonationWeapon != null)
            Weapon.FireTemporary(detonationWeapon, Owner, Owner.Position);
        Owner.Kill();
        detonated = true;
    }
}

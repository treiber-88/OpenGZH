using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// A bridge's logic object (the original's BridgeBehavior): damage and healing are shared with its
/// four towers; changing damage state plays the bridge style's transition effects over the deck and
/// updates the terrain's bridge (a broken one drops whoever is on it and closes the layer). When it
/// dies it kills its towers and plays BridgeDieFX / BridgeDieOCL on their delays; it stays as rubble
/// until dozers rebuild it, raising scaffolding meanwhile.
/// </summary>
public sealed class BridgeBehavior : Module, IUpdateModule, IDamageModule, IDieModule, IOwnershipModule, IDelaysDestruction
{
    readonly uint[] towerIds = new uint[4];
    readonly List<uint> scaffoldObjectIds = new();
    int deathFrame;

    readonly record struct TimedEffect(string Name, int Delay, string Bone);
    List<TimedEffect> dieFx = new(), dieOcl = new();

    public bool IsScaffoldPresent { get; private set; }

    /// <summary>A bridge is never removed; it lies broken until repaired.</summary>
    public bool IsDelayingDestruction => true;

    protected override void Initialize()
    {
        dieFx = ParseTimed("BridgeDieFX", "FX");
        dieOcl = ParseTimed("BridgeDieOCL", "OCL");
    }

    /// <summary>"FX:name Delay:ms Bone:name" lines.</summary>
    List<TimedEffect> ParseTimed(string key, string nameKey)
    {
        var result = new List<TimedEffect>();
        foreach (string line in ListSetting(key))
        {
            string name = "", bone = "";
            int delay = 0;
            foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = word.IndexOf(':');
                if (colon < 0)
                    continue;
                string k = word[..colon], v = word[(colon + 1)..];
                if (k.Equals(nameKey, StringComparison.OrdinalIgnoreCase))
                    name = v;
                else if (k.Equals("Delay", StringComparison.OrdinalIgnoreCase))
                    delay = RuleUnits.Duration(v);
                else if (k.Equals("Bone", StringComparison.OrdinalIgnoreCase))
                    bone = v;
            }
            if (name.Length > 0)
                result.Add(new TimedEffect(name, delay, bone));
        }
        return result;
    }

    public void SetTower(BridgeTowerType type, GameObject? tower) => towerIds[(int)type] = tower?.Id ?? 0;
    public uint TowerId(BridgeTowerType type) => towerIds[(int)type];

    Bridge? TerrainBridge => Game.Terrain.Bridges.All.FirstOrDefault(b => b.Info.BridgeObjectId == Owner.Id) ?? Game.Terrain.Bridges.FindBridgeAt(Owner.Position);
    BridgeTemplate? Template => TerrainBridge is { } bridge ? BridgeSetup.FindTemplate(Game, bridge.TemplateName) : null;

    bool FromBridgePart(uint sourceId) =>
        Game.FindObject(sourceId) is { } source && (source.IsKindOf("BRIDGE") || source.IsKindOf("BRIDGE_TOWER"));

    public void OnDamage(DamageInfo damage)
    {
        if (Owner.Body is not { MaxHealth: > 0 } body || FromBridgePart(damage.SourceId))
            return;
        float percentage = damage.Amount / body.MaxHealth;
        foreach (uint id in towerIds)
        {
            if (Game.FindObject(id) is { Body: { } towerBody } tower)
                tower.Damage(new DamageInfo { Amount = percentage * towerBody.MaxHealth, SourceId = Owner.Id, DamageType = damage.DamageType, DeathType = damage.DeathType });
        }
    }

    public void OnHealing(DamageInfo healing)
    {
        if (Owner.Body is not { MaxHealth: > 0 } body || FromBridgePart(healing.SourceId))
            return;
        float percentage = healing.Amount / body.MaxHealth;
        foreach (uint id in towerIds)
            if (Game.FindObject(id) is { Body: { } towerBody })
                towerBody.AttemptHealing(percentage * towerBody.MaxHealth, Owner.Id);
    }

    /// <summary>A random point on the deck (the original's getRandomSurfacePosition).</summary>
    Vector3 RandomSurfacePosition(BridgeTemplate template, BridgeInfo info)
    {
        var v1 = (info.ToLeft - info.FromLeft) * Game.Random.Float(0, 1);
        var v2 = (info.FromRight - info.FromLeft) * Game.Random.Float(0, 1);
        var pos = v1 + v2 + info.FromLeft;
        pos.Z += Game.Random.Float(0, template.TransitionEffectsHeight);
        return pos;
    }

    void DoAreaEffects(BridgeTemplate template, Bridge bridge, string ocl, string fx)
    {
        if (ocl.Length == 0 && fx.Length == 0)
            return;
        for (int i = 0; i < template.NumFXPerType; i++)
        {
            if (fx.Length > 0)
                Game.Events.PlayFx(fx, RandomSurfacePosition(template, bridge.Info));
            if (ocl.Length > 0)
                Game.ObjectCreation.Run(ocl, Owner, RandomSurfacePosition(template, bridge.Info));
        }
    }

    public void OnBodyDamageStateChange(BodyDamageState oldState, BodyDamageState newState)
    {
        if (newState != BodyDamageState.Rubble)
            deathFrame = 0;
        if (TerrainBridge is not { } bridge || Template is not { } template)
            return;
        bool gotRepaired = newState < oldState;
        Game.Events.PlaySound(gotRepaired ? template.RepairedToSound : template.DamageToSound, Owner.Position, source: Owner);
        for (int i = 0; i < BridgeTemplate.MaxBridgeBodyFx; i++)
        {
            if (gotRepaired)
                DoAreaEffects(template, bridge, template.RepairedToOcl(newState, i), template.RepairedToFx(newState, i));
            else
                DoAreaEffects(template, bridge, template.DamageToOcl(newState, i), template.DamageToFx(newState, i));
        }
        BridgeDamage.UpdateBridgeDamageStates(Game);
    }

    public int Update()
    {
        if (deathFrame == 0)
            return 0;
        var us = Owner;
        var bridge = TerrainBridge;
        var template = Template;
        int deathTime = Frame - deathFrame;
        Vector3 Where(string bone) =>
            bone.Length > 0 ? Game.Bones.BonePosition(us, bone)
            : bridge != null && template != null ? RandomSurfacePosition(template, bridge.Info) : us.Position;
        foreach (var fx in dieFx)
            if (deathTime == fx.Delay)
                Game.Events.PlayFx(fx.Name, Where(fx.Bone));
        foreach (var ocl in dieOcl)
        {
            if (deathTime != ocl.Delay)
                continue;
            if (ocl.Bone.Equals("ParentObject", StringComparison.Ordinal))
                Game.ObjectCreation.Run(ocl.Name, us, us.Position);
            else
                Game.ObjectCreation.Run(ocl.Name, us, Where(ocl.Bone));
        }
        return 0;
    }

    public void OnDie(DamageInfo killingBlow)
    {
        foreach (uint id in towerIds)
            Game.FindObject(id)?.Kill();
        HandleObjectsOnBridgeOnDie();
        deathFrame = Frame;
    }

    /// <summary>Everything standing on the deck falls (or dies, without physics).</summary>
    void HandleObjectsOnBridgeOnDie()
    {
        if (TerrainBridge is not { } bridge)
            return;
        var info = bridge.Info;
        float lowZ = new[] { info.FromLeft.Z, info.FromRight.Z, info.ToRight.Z, info.ToLeft.Z }.Min();
        float radius = new Vector2(info.ToLeft.X - Owner.Position.X, info.ToLeft.Y - Owner.Position.Y).Length();
        foreach (var other in Game.ObjectsInRadius(Owner.Position, radius).ToList())
        {
            if (other.IsKindOf("BRIDGE") || other.IsKindOf("BRIDGE_TOWER") || other.IsAboveTerrain)
                continue;
            if (other.Position.Z < lowZ || !bridge.IsPointOnBridge(other.Position) || other.Layer != bridge.Layer)
                continue;
            other.Layer = 0;
            if (other.FindModule<Updates.PhysicsBehavior>() is { } physics)
                physics.AllowToFall = true;
            else
                other.Kill();
        }
    }

    public void OnDelete()
    {
        foreach (uint id in towerIds)
            if (Game.FindObject(id) is { } tower)
                Game.DestroyObject(tower);
        scaffoldObjectIds.Clear();
    }

    // ---- Scaffolding (dozers rebuilding a broken bridge) ----

    void SetScaffoldData(GameObject obj, float angle, float sunkenHeight, Vector3 riseToPos, Vector3 buildPos, Vector3 bridgeCenter)
    {
        if (obj.FindModule<BridgeScaffoldBehavior>() is not { } scaffold)
            return;
        const float Fudge = 8;
        var sunkenPos = riseToPos with { Z = riseToPos.Z - sunkenHeight - Fudge };
        obj.SetPosition(sunkenPos);
        scaffold.SetPositions(sunkenPos, riseToPos, buildPos);
        scaffold.SetMotion(ScaffoldMotion.Rise);
        obj.SetOrientation(angle);
        float lateralSpeed = RuleUnits.Velocity(Setting("LateralScaffoldSpeed"), 30);
        float distBuild = (buildPos - riseToPos).Length();
        float distCenter = (bridgeCenter - riseToPos).Length();
        scaffold.LateralSpeed = distCenter > 0 ? lateralSpeed * (distBuild / distCenter) : lateralSpeed;
        scaffold.VerticalSpeed = RuleUnits.Velocity(Setting("VerticalScaffoldSpeed"), 30);
    }

    public void CreateScaffolding()
    {
        if (IsScaffoldPresent || TerrainBridge is not { } bridge || Template is not { } template)
            return;
        var us = Owner;
        var center = us.Position;
        var scaffoldTemplate = Game.Templates.Find(template.Get("ScaffoldObjectName"));
        var supportTemplate = Game.Templates.Find(template.Get("ScaffoldSupportObjectName"));
        if (scaffoldTemplate == null || supportTemplate == null)
            return;
        float spacing = scaffoldTemplate.GeometryMajorRadius * 2;
        float scaffoldHeight = scaffoldTemplate.Geometry.MaxHeightAbovePosition + scaffoldTemplate.Geometry.MaxHeightBelowPosition;
        float supportHeight = supportTemplate.Geometry.MaxHeightAbovePosition + supportTemplate.Geometry.MaxHeightBelowPosition;
        var info = bridge.Info;
        var leftStart = (info.FromLeft - info.FromRight) / 2 + info.FromRight;
        var rightStart = (info.ToLeft - info.ToRight) / 2 + info.ToRight;
        float leftAngle = MathF.Atan2(rightStart.Y - leftStart.Y, rightStart.X - leftStart.X);
        float rightAngle = leftAngle + MathF.Tau; // (as the original: the same direction)
        var leftVector = rightStart - leftStart;
        var rightVector = leftStart - rightStart;
        float tileDistance = leftVector.Length();
        int numObjects = (int)MathF.Ceiling(tileDistance / Math.Max(1, spacing)) + 1;
        int numIterations = (int)MathF.Ceiling(numObjects / 2f);
        if (tileDistance > 0)
        {
            leftVector = Vector3.Normalize(leftVector);
            rightVector = Vector3.Normalize(rightVector);
        }
        int created = 0;

        void Column(Vector3 riseTo, float angle, Vector3 vector, int i)
        {
            var obj = Game.CreateObject(scaffoldTemplate, us.Owner, riseTo, angle, us.Team);
            var destination = new Vector3(vector.X * spacing * i + riseTo.X + 0.1f, vector.Y * spacing * i + riseTo.Y, vector.Z * spacing * i + riseTo.Z);
            SetScaffoldData(obj, angle, scaffoldHeight, riseTo, destination, center);
            created++;
            scaffoldObjectIds.Add(obj.Id);
            float offset = riseTo.Z;
            var supportRise = riseTo;
            var supportDest = destination;
            var supportCenter = center;
            while (offset >= 0)
            {
                supportRise.Z -= supportHeight;
                supportDest.Z -= supportHeight;
                supportCenter.Z -= supportHeight;
                var support = Game.CreateObject(supportTemplate, us.Owner, supportRise, angle, us.Team);
                SetScaffoldData(support, angle, supportHeight, supportRise, supportDest, supportCenter);
                scaffoldObjectIds.Add(support.Id);
                offset -= Math.Max(1, supportHeight);
            }
        }

        for (int i = 0; i < numIterations; i++)
        {
            Column(leftStart, leftAngle, leftVector, i);
            if (created < numObjects)
                Column(rightStart, rightAngle, rightVector, i);
        }
        IsScaffoldPresent = true;
        bridge.PathfindingClosed = true;
    }

    public void RemoveScaffolding()
    {
        if (!IsScaffoldPresent)
            return;
        foreach (uint id in scaffoldObjectIds)
            Game.FindObject(id)?.FindModule<BridgeScaffoldBehavior>()?.ReverseMotion();
        scaffoldObjectIds.Clear();
        IsScaffoldPresent = false;
        if (Owner.Body?.DamageState != BodyDamageState.Rubble && TerrainBridge is { } bridge)
            bridge.PathfindingClosed = false;
    }

    public bool IsScaffoldInMotion => scaffoldObjectIds.Any(id => Game.FindObject(id)?.FindModule<BridgeScaffoldBehavior>() is { CurrentMotion: not ScaffoldMotion.Still });
}

/// <summary>
/// One of a landmark bridge's four end towers (the original's BridgeTowerBehavior): damage and repair
/// done to a tower are shared out to the other towers and the bridge, and its death brings the bridge down.
/// </summary>
public sealed class BridgeTowerBehavior : Module, IDamageModule, IDieModule
{
    public uint BridgeId { get; private set; }
    public BridgeTowerType TowerType { get; set; }

    public void SetBridge(GameObject? bridge) => BridgeId = bridge?.Id ?? 0;

    bool FromBridgePart(uint sourceId) =>
        Game.FindObject(sourceId) is { } source && (source.IsKindOf("BRIDGE") || source.IsKindOf("BRIDGE_TOWER"));

    public void OnDamage(DamageInfo damage)
    {
        if (Game.FindObject(BridgeId) is not { } bridge || bridge.FindModule<BridgeBehavior>() is not { } bridgeBehavior)
            return;
        if (Owner.Body is not { MaxHealth: > 0 } body || FromBridgePart(damage.SourceId))
            return;
        float percentage = damage.Amount / body.MaxHealth;
        for (int i = 0; i < 4; i++)
        {
            if (Game.FindObject(bridgeBehavior.TowerId((BridgeTowerType)i)) is { Body: { } towerBody } tower && tower != Owner)
                tower.Damage(new DamageInfo { Amount = percentage * towerBody.MaxHealth, SourceId = Owner.Id, DamageType = damage.DamageType, DeathType = damage.DeathType });
        }
        if (bridge.Body is { } bridgeBody)
            bridge.Damage(new DamageInfo { Amount = percentage * bridgeBody.MaxHealth, SourceId = Owner.Id, DamageType = damage.DamageType, DeathType = damage.DeathType });
    }

    public void OnHealing(DamageInfo healing)
    {
        if (Game.FindObject(BridgeId) is not { } bridge || bridge.FindModule<BridgeBehavior>() is not { } bridgeBehavior)
            return;
        if (Owner.Body is not { MaxHealth: > 0 } body || FromBridgePart(healing.SourceId))
            return;
        float percentage = healing.Amount / body.MaxHealth;
        for (int i = 0; i < 4; i++)
        {
            if (Game.FindObject(bridgeBehavior.TowerId((BridgeTowerType)i)) is { Body: { } towerBody } tower && tower != Owner)
                towerBody.AttemptHealing(percentage * towerBody.MaxHealth, Owner.Id);
        }
        bridge.Body?.AttemptHealing(percentage * (bridge.Body?.MaxHealth ?? 0), Owner.Id);
    }

    public void OnDie(DamageInfo killingBlow) => Game.FindObject(BridgeId)?.Kill();
}

public enum ScaffoldMotion { Still, Rise, BuildAcross, TearDownAcross, Sink }

/// <summary>
/// A piece of bridge-repair scaffolding (the original's BridgeScaffoldBehavior): it rises out of the
/// ground, slides across to its spot, and on removal slides back and sinks away.
/// </summary>
public sealed class BridgeScaffoldBehavior : Module, IUpdateModule
{
    Vector3 createPos, riseToPos, buildPos, targetPos;
    public ScaffoldMotion CurrentMotion { get; private set; } = ScaffoldMotion.Still;
    public float LateralSpeed { get; set; } = 1;
    public float VerticalSpeed { get; set; } = 1;

    public void SetPositions(Vector3 create, Vector3 riseTo, Vector3 build)
    {
        createPos = create;
        riseToPos = riseTo;
        buildPos = build;
    }

    public void SetMotion(ScaffoldMotion motion)
    {
        CurrentMotion = motion;
        targetPos = motion switch
        {
            ScaffoldMotion.Rise or ScaffoldMotion.TearDownAcross => riseToPos,
            ScaffoldMotion.BuildAcross => buildPos,
            ScaffoldMotion.Sink => createPos,
            _ => targetPos,
        };
    }

    public void ReverseMotion() => SetMotion(CurrentMotion switch
    {
        ScaffoldMotion.Still => ScaffoldMotion.TearDownAcross,
        ScaffoldMotion.Rise => ScaffoldMotion.Sink,
        ScaffoldMotion.BuildAcross => ScaffoldMotion.TearDownAcross,
        ScaffoldMotion.TearDownAcross => ScaffoldMotion.BuildAcross,
        _ => ScaffoldMotion.Rise,
    });

    public int Update()
    {
        if (CurrentMotion == ScaffoldMotion.Still)
            return 0;
        var us = Owner;
        var ourPos = us.Position;
        var dirV = targetPos - ourPos;
        var v = dirV.LengthSquared() > 0 ? Vector3.Normalize(dirV) : Vector3.Zero;
        var (topSpeed, start, end) = CurrentMotion switch
        {
            ScaffoldMotion.Rise => (VerticalSpeed, createPos, riseToPos),
            ScaffoldMotion.Sink => (VerticalSpeed, riseToPos, createPos),
            ScaffoldMotion.BuildAcross => (LateralSpeed, riseToPos, buildPos),
            _ => (LateralSpeed, buildPos, riseToPos),
        };
        float totalDistance = (end - start).Length() * 0.25f;
        float ourDistance = (end - ourPos).Length();
        float speed = totalDistance > 0 ? ourDistance / totalDistance * topSpeed : topSpeed;
        speed = Math.Max(Math.Min(Math.Max(speed, topSpeed * 0.08f), topSpeed), 0.001f);
        var newPos = ourPos + v * speed;
        if (Vector3.Dot(targetPos - newPos, dirV) <= 0)
        {
            newPos = targetPos;
            switch (CurrentMotion)
            {
                case ScaffoldMotion.Rise: SetMotion(ScaffoldMotion.BuildAcross); break;
                case ScaffoldMotion.BuildAcross: SetMotion(ScaffoldMotion.Still); break;
                case ScaffoldMotion.TearDownAcross: SetMotion(ScaffoldMotion.Sink); break;
                case ScaffoldMotion.Sink:
                    Game.DestroyObject(us);
                    return LogicTime.SleepForever;
            }
        }
        us.SetPosition(newPos);
        return 0;
    }
}

/// <summary>The terrain's bridges following their logic objects' damage (the original's updateBridgeDamageStates).</summary>
public static class BridgeDamage
{
    public static void UpdateBridgeDamageStates(GameLogic game)
    {
        foreach (var bridge in game.Terrain.Bridges.All)
            UpdateDamageState(game, bridge);
    }

    static void UpdateDamageState(GameLogic game, Bridge bridge)
    {
        var info = bridge.Info;
        info.DamageStateChanged = false;
        if (info.BridgeObjectId == 0 || game.FindObject(info.BridgeObjectId)?.Body is not { } body)
            return;
        var damageState = body.DamageState;
        var curState = info.CurDamageState;
        if (damageState == curState)
            return;
        info.CurDamageState = damageState;
        if (damageState == BodyDamageState.Rubble)
        {
            bridge.PathfindingClosed = true;
            info.DamageStateChanged = true;
            // Whoever is still on the deck goes down with it.
            foreach (var obj in game.Objects.ToList())
            {
                if (obj.Layer == bridge.Layer && game.Terrain.Bridges.ObjectInteractsWithBridgeLayer(obj, obj.Layer))
                    obj.Damage(new DamageInfo { Amount = DamageInfo.HugeDamage, DamageType = "FALLING", DeathType = "SPLATTED", SourceId = obj.Id });
            }
        }
        if (curState == BodyDamageState.Rubble)
        {
            var bridgeObj = game.FindObject(info.BridgeObjectId);
            if (bridgeObj?.FindModule<BridgeBehavior>() is not { IsScaffoldPresent: true })
                bridge.PathfindingClosed = false;
            info.DamageStateChanged = true;
        }
    }
}

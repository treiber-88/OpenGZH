using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>Shared bits of the two ways a big civilian building comes down.</summary>
public abstract class StructureFallModule : DieModule, IUpdateModule, IDelaysDestruction
{
    // Draw-only shaking (the original uses the client's random numbers, so the logic's stay in step).
    protected static readonly Random ClientRandom = new();

    protected bool Begun { get; set; }

    /// <summary>A fallen building stays as rubble: the original never destroys it.</summary>
    public bool IsDelayingDestruction => Begun;

    public FlagSet DisabledTypesToProcess => DisabledMask.All;

    public abstract int Update();

    /// <summary>"PHASE name name ..." lines grouped by phase name.</summary>
    protected Dictionary<string, List<string>> ReadPhased(string key)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in ListSetting(key))
        {
            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2)
                continue;
            if (!result.TryGetValue(words[0], out var list))
                result[words[0]] = list = new();
            list.AddRange(words.Skip(1));
        }
        return result;
    }

    /// <summary>The original's buildNonDupRandomIndexList: <paramref name="count"/> different random picks.</summary>
    protected List<string> PickDistinct(List<string> options, int count)
    {
        var picked = new List<int>();
        count = Math.Min(count, options.Count);
        while (picked.Count < count)
        {
            int idx = Game.Random.Int(0, options.Count - 1);
            if (!picked.Contains(idx))
                picked.Add(idx);
        }
        return picked.Select(i => options[i]).ToList();
    }

    /// <summary>The geometry's top (the original's getMaxHeightAbovePosition).</summary>
    protected float MaxHeightAbovePosition => Owner.Template.GeometryName.Equals("SPHERE", StringComparison.OrdinalIgnoreCase)
        ? Owner.Template.GeometryMajorRadius : Owner.Template.GeometryHeight;

    protected bool FxTypeAllowed(FlagSet types) =>
        Owner.Body?.LastDamage is not { DamageType.Length: > 0 } last || types.Has(Flags.DamageType.IndexOf(last.DamageType));

    protected void Fall()
    {
        Begun = true;
        Owner.AI?.Stop(Commands.CommandSource.AI);
        Owner.SetStatus(Status.Unselectable);
        Game.Events.Deselect(Owner);
        Game.Pathfinder.RemoveObstacle(Owner);
    }

    protected void FinishAsRubble()
    {
        Owner.FindModule<Damage.BoneFXUpdate>()?.StopAllBoneFX();
        Owner.SetCondition(Condition.Rubble, false);
        Owner.SetCondition("POST_COLLAPSE");
        Owner.DrawJitter = Vector3.Zero;
    }
}

/// <summary>
/// A high-rise that shudders, then sinks into a cloud of dust (the original's StructureCollapseUpdate):
/// after MinCollapseDelay..MaxCollapseDelay it falls with gravity slowed by CollapseDamping, with
/// bursts every MinBurstDelay..MaxBurstDelay (one in BigBurstFrequency a big one), and ends as rubble.
/// </summary>
public sealed class StructureCollapseUpdate : StructureFallModule
{
    enum State { Standing, WaitingForCollapseStart, Collapsing, Done }

    State state = State.Standing;
    int collapseFrame, burstFrame;
    float collapseVelocity, currentHeight;
    Dictionary<string, List<string>> fx = new(), ocl = new();

    protected override void InitializeDie()
    {
        fx = ReadPhased("FXList");
        ocl = ReadPhased("OCL");
    }

    protected override void Die(DamageInfo killingBlow)
    {
        Fall();
        collapseFrame = Frame + Game.Random.Int(FramesSetting("MinCollapseDelay"), FramesSetting("MaxCollapseDelay"));
        DoPhaseStuff("INITIAL", Owner.Position);
        state = State.WaitingForCollapseStart;
        currentHeight = 0;
        Owner.WakeUpdate(this);
    }

    int MinBurstDelay => Definition.Settings.Has("MinBurstDelay") ? FramesSetting("MinBurstDelay") : 9999;
    int MaxBurstDelay => FramesSetting("MaxBurstDelay");

    public override int Update()
    {
        if (state is State.Standing or State.Done)
            return LogicTime.SleepForever;
        var building = Owner;
        float maxShudder = FloatSetting("MaxShudder");
        float Shudder() => (float)(ClientRandom.NextDouble() * 2 - 1) * maxShudder;
        if (state == State.WaitingForCollapseStart)
        {
            building.DrawJitter = new Vector3(Shudder(), Shudder(), 0);
            if (Frame >= collapseFrame)
            {
                state = State.Collapsing;
                DoPhaseStuff("BURST", building.Position);
                burstFrame = Frame + Game.Random.Int(MinBurstDelay, MaxBurstDelay);
            }
        }
        if (state == State.Collapsing)
        {
            currentHeight -= collapseVelocity;
            collapseVelocity -= Game.Catalog.GameData.Gravity * (1 - FloatSetting("CollapseDamping"));
            building.DrawJitter = new Vector3(Shudder(), Shudder(), currentHeight);
            if (Frame >= burstFrame)
            {
                DoPhaseStuff(Game.Random.Int(1, Math.Max(1, IntSetting("BigBurstFrequency"))) == 1 ? "BURST" : "DELAY", building.Position);
                burstFrame += Game.Random.Int(MinBurstDelay, MaxBurstDelay);
            }
            if (currentHeight + MaxHeightAbovePosition <= 0)
            {
                state = State.Done;
                DoPhaseStuff("FINAL", building.Position);
                FinishAsRubble();
                return LogicTime.SleepForever;
            }
        }
        return 0;
    }

    void DoPhaseStuff(string phase, Vector3 target)
    {
        if (fx.TryGetValue(phase, out var fxList))
            foreach (var name in PickDistinct(fxList, 1))
                Game.Events.PlayFx(name, target);
        if (ocl.TryGetValue(phase, out var oclList))
            foreach (var name in PickDistinct(oclList, 1))
                Game.ObjectCreation.Run(name, Owner, target);
    }
}

/// <summary>
/// A tall tower that falls over away from whoever destroyed it (the original's StructureToppleUpdate):
/// after a delay with bursts at its foot it tips over, speeding up as StructuralIntegrity decays, plays
/// AngleFX as it passes their angles, fires CrushingWeaponName along the ground it lands on, and ends
/// as rubble lying in the fall direction. Scripts can set the direction (NAMED_SET_TOPPLE_DIRECTION).
/// </summary>
public sealed class StructureToppleUpdate : StructureFallModule
{
    enum State { Standing, WaitingForToppleStart, Toppling, WaitingForDone, Done }

    const float ToppleAccelerationFactor = 0.02f;

    State state = State.Standing;
    int toppleFrame, nextBurstFrame = -1;
    Vector2 toppleDirection;
    Vector3 delayBurstLocation;
    float structuralIntegrity, toppleVelocity, accumulatedAngle = 0.001f, lastCrushedLocation, buildingHeight;
    Dictionary<string, List<string>> ocl = new();
    readonly List<(float Angle, string Fx)> angleFx = new();
    FlagSet damageFxTypes;

    protected override void InitializeDie()
    {
        ocl = ReadPhased("OCL");
        foreach (string line in ListSetting("AngleFX"))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2)
                angleFx.Add((OpenGZH.Core.Rules.RuleValue.Float(words[0]) * MathF.PI / 180f, words[1]));
        }
        damageFxTypes = Flags.DamageType.Parse(Setting("DamageFXTypes", "ALL"));
        // Measured now: the rubble state is much lower.
        buildingHeight = MaxHeightAbovePosition;
    }

    int RandomBurstDelay() => ClientRandom.Next(FramesSetting("MinToppleBurstDelay"), Math.Max(FramesSetting("MinToppleBurstDelay"), FramesSetting("MaxToppleBurstDelay")) + 1);

    protected override void Die(DamageInfo killingBlow)
    {
        Fall();
        var building = Owner;
        toppleFrame = Frame + Game.Random.Int(FramesSetting("MinToppleDelay"), FramesSetting("MaxToppleDelay"));
        float toppleAngle;
        if (Game.FindObject(killingBlow.SourceId) is not { } attacker)
            toppleAngle = Game.Random.Float(0, MathF.Tau);
        else
        {
            toppleAngle = MathF.Atan2(building.Position.Y - attacker.Position.Y, building.Position.X - attacker.Position.X);
            toppleAngle += Game.Random.Float(-MathF.PI / 8, MathF.PI / 8);
        }
        toppleDirection = new Vector2(MathF.Cos(toppleAngle), MathF.Sin(toppleAngle));
        if (Game.Scripts?.ToppleDirectionFor(building) is { } scripted)
            toppleDirection = new Vector2(scripted.X, scripted.Y);

        float averageRadius = (building.Template.GeometryMajorRadius + building.Template.GeometryMinorRadius) / 2;
        float explosionRadius = averageRadius * 0.9f;
        delayBurstLocation = new Vector3(building.Position.X + explosionRadius * MathF.Cos(toppleAngle), building.Position.Y + explosionRadius * MathF.Sin(toppleAngle), 0);
        delayBurstLocation.Z = Game.Terrain.GroundHeight(delayBurstLocation);

        if (FxTypeAllowed(damageFxTypes))
            Game.Events.PlayFx(Setting("ToppleStartFX"), building.Position);
        DoPhaseStuff("INITIAL", building.Position);
        nextBurstFrame = Frame + RandomBurstDelay();
        state = State.WaitingForToppleStart;
        building.WakeUpdate(this);
    }

    public override int Update()
    {
        if (state is State.Standing or State.Done)
            return LogicTime.SleepForever;
        var building = Owner;
        if (state == State.WaitingForToppleStart)
        {
            if (Frame >= nextBurstFrame)
            {
                DoToppleDelayBurstFx();
                nextBurstFrame = Frame + RandomBurstDelay();
            }
            if (Frame >= toppleFrame)
            {
                state = State.Toppling;
                structuralIntegrity = FloatSetting("StructuralIntegrity");
            }
        }
        if (state == State.Toppling)
        {
            float acceleration = ToppleAccelerationFactor * (MathF.Sin(accumulatedAngle) * (1 - structuralIntegrity));
            toppleVelocity += acceleration;
            if (structuralIntegrity > 0)
                structuralIntegrity = Math.Max(0, structuralIntegrity * FloatSetting("StructuralDecay"));
            DoAngleFx(accumulatedAngle, accumulatedAngle + toppleVelocity);
            accumulatedAngle += toppleVelocity;
            ApplyCrushingDamage(MathF.PI / 2 - accumulatedAngle);
            if (accumulatedAngle >= MathF.PI / 2)
            {
                toppleVelocity -= accumulatedAngle - MathF.PI / 2;
                accumulatedAngle = MathF.PI / 2;
                state = State.WaitingForDone;
                ApplyCrushingDamage(0);
                DoPhaseStuff("FINAL", building.Position);
                if (FxTypeAllowed(damageFxTypes))
                    Game.Events.PlayFx(Setting("ToppleDoneFX"), building.Position, building);
                toppleFrame = Frame;
            }
            if (Frame >= nextBurstFrame)
            {
                DoToppleDelayBurstFx();
                nextBurstFrame = Frame + RandomBurstDelay();
            }
            // Tip over about the object's own axes (the original pre-rotates its transform).
            building.Tilt = building.Tilt
                * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -toppleVelocity * toppleDirection.Y)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitY, toppleVelocity * toppleDirection.X);
        }
        if (state == State.WaitingForDone && toppleFrame <= Frame)
        {
            FinishAsRubble();
            float toppleAngle = MathF.Atan2(toppleDirection.Y, toppleDirection.X);
            building.Tilt *= Quaternion.CreateFromAxisAngle(Vector3.UnitZ, toppleAngle - building.Orientation);
            state = State.Done;
            return LogicTime.SleepForever;
        }
        return 0;
    }

    void DoAngleFx(float curAngle, float newAngle)
    {
        foreach (var (angle, fxName) in angleFx)
            if (angle > curAngle && angle <= newAngle && FxTypeAllowed(damageFxTypes))
                Game.Events.PlayFx(fxName, Owner.Position, Owner);
    }

    void ApplyCrushingDamage(float theta)
    {
        const float ThetaCeiling = MathF.PI / 6;
        const float WeaponSpacingPerpendicular = 25;
        if (theta > ThetaCeiling)
            return;
        var building = Owner;
        float toppleAngle = MathF.Atan2(toppleDirection.Y, toppleDirection.X);
        float angle = building.Orientation - toppleAngle;
        float minorComponent = building.Template.GeometryMinorRadius * MathF.Cos(angle);
        float majorComponent = building.Template.GeometryMajorRadius * MathF.Sin(angle);
        float facingWidth = new Vector2(majorComponent, minorComponent).Length() / 2;
        string weapon = Setting("CrushingWeaponName");
        if (Game.Catalog.Weapon(weapon) == null)
            return;
        float maxDistance = buildingHeight * (1 - MathF.Sin(theta));
        float j;
        for (j = lastCrushedLocation; j < maxDistance; j += WeaponSpacingPerpendicular)
            DoDamageLine(weapon, j * MathF.Cos(toppleAngle), j * MathF.Sin(toppleAngle), facingWidth, toppleAngle);
        DoDamageLine(weapon, maxDistance * MathF.Cos(toppleAngle), maxDistance * MathF.Sin(toppleAngle), facingWidth, toppleAngle);
        lastCrushedLocation = j;
    }

    void DoDamageLine(string weapon, float jcos, float jsin, float facingWidth, float toppleAngle)
    {
        const float WeaponSpacingParallel = 25;
        var building = Owner;
        Vector3 At(float x, float y) => new(x, y, Game.Terrain.GroundHeight(new Vector3(x, y, 0)));
        void Hit(Vector3 target)
        {
            Game.Damage.FireTemporaryWeapon(weapon, building, target);
            if (FxTypeAllowed(damageFxTypes))
                Game.Events.PlayFx(Setting("CrushingFX"), target);
        }
        for (float i = -facingWidth; i < facingWidth; i += WeaponSpacingParallel)
            Hit(At(building.Position.X + jcos + i * MathF.Sin(toppleAngle), building.Position.Y + jsin + i * MathF.Cos(toppleAngle)));
        Hit(At(building.Position.X + jcos + facingWidth * MathF.Sin(toppleAngle), building.Position.Y + jsin + facingWidth * MathF.Cos(toppleAngle)));
        DoPhaseStuff("FINAL", At(building.Position.X + jcos, building.Position.Y + jsin));
    }

    void DoToppleDelayBurstFx()
    {
        if (FxTypeAllowed(damageFxTypes))
            Game.Events.PlayFx(Setting("ToppleDelayFX"), delayBurstLocation);
        DoPhaseStuff("DELAY", delayBurstLocation);
    }

    void DoPhaseStuff(string phase, Vector3 target)
    {
        if (ocl.TryGetValue(phase, out var oclList))
            foreach (var name in PickDistinct(oclList, 1))
                Game.ObjectCreation.Run(name, Owner, target);
    }
}

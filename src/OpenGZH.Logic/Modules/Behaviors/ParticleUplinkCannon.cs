using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Effects;
using OpenGZH.Logic.Modules.SpecialPowers;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Templates;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// The USA Particle Cannon (the original's ParticleUplinkCannonUpdate). As the power charges, the
/// building powers up (BeginChargeTime before RaiseAntennaTime), raises its antenna (UNPACKING) and
/// is DEPLOYED ReadyDelayTime before it is ready, with flares and connector lasers between its
/// outer nodes. When fired, a beam goes up from the building and, BeamTravelTime later, one comes
/// down on the target from orbit. It burns for TotalFiringTime: TotalDamagePulses pulses of
/// DamagePerSecond spread evenly, hitting everything within the beam's width times
/// DamageRadiusScalar, leaving scorch marks and remnants. Unless the player drives it (right-clicking
/// while it fires, ManualDrivingSpeed, double-click for ManualFastDrivingSpeed), it sweeps a sine
/// swath SwathOfDeathDistance long and SwathOfDeathAmplitude wide across the target. Losing power,
/// EMP, hacking or subdual cut the beam short.
/// </summary>
public sealed class ParticleUplinkCannonUpdate : Module, IUpdateModule, ICreateModule, ISpecialPowerUpdate
{
    enum Status { Idle, Charging, Preparing, AlmostReady, ReadyToFire, Prefire, Firing, Postfire, Packing }
    enum Laser { None, Born, Decaying, Dead }
    enum Intensity { Light, Medium, Intense, Finish }

    const int MaxOuterNodes = 16;
    const float OrbitHeight = 500f;

    /// <summary>The original's COMMAND_FIRED_BY_SCRIPT option bit.</summary>
    public const uint FiredByScript = Objects.ObjectCommandButtons.CommandFiredByScript;

    // Settings
    int beginChargeFrames, raiseAntennaFrames, readyDelayFrames, widthGrowFrames, beamTravelFrames, totalFiringFrames;
    int framesBetweenLaunchFx, outerNodeCount, totalScorchMarks, totalDamagePulses, doubleClickFastDriveDelay;
    float swathDistance, swathAmplitude, scorchMarkScalar, damagePerSecond, damageRadiusScalar, revealRange;
    float manualSpeed, manualFastSpeed;
    string damageType = "LASER", deathType = "LASERED";

    SpecialPowerModule? power;
    bool invalidSettings;

    Status status = Status.Idle;
    Laser laserStatus = Laser.None;

    readonly Vector3[] outerNodePositions = new Vector3[MaxOuterNodes];
    readonly Matrix4x4[] outerNodeOrientations = new Matrix4x4[MaxOuterNodes];
    readonly int[] outerSystems = new int[MaxOuterNodes];
    readonly int[] connectorBeams = new int[MaxOuterNodes];
    Vector3 connectorNodePosition, laserOriginPosition;
    Vector3 initialTargetPosition, currentTargetPosition, overrideTargetDestination;
    int groundToOrbitBeam, orbitToTargetBeam, connectorSystem, laserBaseSystem;
    int powerupSound, unpackToReadySound, firingToIdleSound, annihilationSound;

    int scorchMarksMade, nextScorchMarkFrame, nextLaunchFxFrame;
    int damagePulsesMade, nextDamagePulseFrame;
    int startAttackFrame, startDecayFrame;
    int lastDrivingClickFrame, secondLastDrivingClickFrame;
    Waypoint? nextDestinationWaypoint;
    bool upBonesCached, defaultInfoCached, manualTargetMode, scriptedWaypointMode;

    public FlagSet DisabledTypesToProcess => DisabledMask.SuperweaponStates;

    protected override void Initialize()
    {
        beginChargeFrames = FramesSetting("BeginChargeTime");
        raiseAntennaFrames = FramesSetting("RaiseAntennaTime");
        readyDelayFrames = FramesSetting("ReadyDelayTime");
        widthGrowFrames = FramesSetting("WidthGrowTime");
        beamTravelFrames = FramesSetting("BeamTravelTime");
        totalFiringFrames = FramesSetting("TotalFiringTime");
        framesBetweenLaunchFx = FramesSetting("DelayBetweenLaunchFX", 1000);
        outerNodeCount = Math.Min(MaxOuterNodes, IntSetting("OuterEffectNumBones"));
        swathDistance = FloatSetting("SwathOfDeathDistance");
        swathAmplitude = FloatSetting("SwathOfDeathAmplitude");
        totalScorchMarks = IntSetting("TotalScorchMarks");
        scorchMarkScalar = FloatSetting("ScorchMarkScalar", 1);
        totalDamagePulses = IntSetting("TotalDamagePulses");
        damagePerSecond = FloatSetting("DamagePerSecond");
        damageType = Setting("DamageType", "LASER");
        deathType = Setting("DeathType", "LASERED");
        damageRadiusScalar = FloatSetting("DamageRadiusScalar", 1);
        revealRange = FloatSetting("RevealRange");
        manualSpeed = FloatSetting("ManualDrivingSpeed");
        manualFastSpeed = FloatSetting("ManualFastDrivingSpeed");
        doubleClickFastDriveDelay = FramesSetting("DoubleClickToFastDriveDelay", 500);
        for (int i = 0; i < MaxOuterNodes; i++)
            outerNodeOrientations[i] = Matrix4x4.Identity;
    }

    public void OnCreate()
    {
        string name = Setting("SpecialPowerTemplate");
        power = Owner.FindModules<SpecialPowerModule>().FirstOrDefault(p => p.Power != null && p.Power.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (power == null)
        {
            Log.Warning($"{Owner.Template.Name}: ParticleUplinkCannonUpdate has no special power module for '{name}'.");
            invalidSettings = true;
            return;
        }
        connectorNodePosition = Owner.Position;
        laserOriginPosition = Owner.Position;
    }

    // ---- Firing orders ----

    public bool InitiateIntentToDoSpecialPower(SpecialPowerTemplate template, GameObject? target, Vector3? position, uint options) =>
        Initiate(template, target, position, null, options);

    /// <summary>A script fires it along a waypoint path: the beam drives from waypoint to waypoint.</summary>
    public bool InitiateAtWaypoint(SpecialPowerTemplate template, Waypoint waypoint) =>
        Initiate(template, null, null, waypoint, FiredByScript);

    bool Initiate(SpecialPowerTemplate template, GameObject? target, Vector3? position, Waypoint? way, uint options)
    {
        if (power?.Power != template)
            return false;
        int now = Frame;
        if ((options & FiredByScript) == 0)
        {
            var at = position ?? target?.Position ?? Owner.Position;
            startAttackFrame = now;
            laserStatus = Laser.None;
            manualTargetMode = true;
            initialTargetPosition = at;
            overrideTargetDestination = at;
            currentTargetPosition = at;
        }
        else if (way != null)
        {
            var pos = way.Position;
            startAttackFrame = Math.Max(now, 1);
            scriptedWaypointMode = true;
            laserStatus = Laser.None;
            SetLogicalStatus(Status.ReadyToFire);
            power.SetReadyFrame(now);
            initialTargetPosition = pos;
            currentTargetPosition = pos;
            nextDestinationWaypoint = way;
            if (way.Next.Count > 0 && way.Next[Game.Random.Int(0, way.Next.Count - 1)] is { } next)
            {
                nextDestinationWaypoint = next;
                overrideTargetDestination = next.Position;
            }
        }
        else
        {
            var pos = position ?? target?.Position ?? Vector3.Zero;
            initialTargetPosition = pos;
            startAttackFrame = Math.Max(now, 1);
            laserStatus = Laser.None;
            SetLogicalStatus(Status.ReadyToFire);
            power.SetReadyFrame(now);
        }
        startDecayFrame = startAttackFrame + totalFiringFrames;
        power.TriggerSpecialPower(initialTargetPosition);
        return true;
    }

    public bool IsPowerCurrentlyInUse => startAttackFrame != 0 && startAttackFrame <= Frame;

    public bool DoesSpecialPowerHaveOverridableDestination => true;

    public bool DoesSpecialPowerHaveOverridableDestinationActive => status is Status.Prefire or Status.Firing or Status.Postfire;

    /// <summary>The player steers the beam (right-click while it fires); two quick clicks drive it fast.</summary>
    public void SetSpecialPowerOverridableDestination(Vector3 location)
    {
        if (Owner.IsDisabled)
            return;
        overrideTargetDestination = location;
        manualTargetMode = true;
        secondLastDrivingClickFrame = lastDrivingClickFrame;
        lastDrivingClickFrame = Frame;
    }

    /// <summary>Is the cannon doing anything (charging, firing, packing)?</summary>
    public bool IsActive => status != Status.Idle;

    // ---- Update ----

    public int Update()
    {
        if (invalidSettings || power == null)
            return 0;
        if (Owner.HasStatus(Core.Status.Sold))
        {
            if (status != Status.Idle)
            {
                SetLogicalStatus(Status.Idle);
                KillEverything();
            }
            return 0;
        }
        if (Owner.IsUnderConstruction || Owner.IsEffectivelyDead)
            return 0;

        int now = Frame;
        int readyToFireFrame = power.IsReady ? now : power.ReadyFrame;
        int almostReadyFrame = readyToFireFrame - readyDelayFrames;
        int raiseAntennaFrame = almostReadyFrame - raiseAntennaFrames;
        int beginChargeFrame = raiseAntennaFrame - beginChargeFrames;

        if (startAttackFrame != 0 && startAttackFrame <= now)
        {
            if (startDecayFrame > now && (Owner.IsDisabledBy("DISABLED_UNDERPOWERED") || Owner.IsDisabledBy("DISABLED_EMP")
                || Owner.IsDisabledBy("DISABLED_SUBDUED") || Owner.IsDisabledBy("DISABLED_HACKED")))
                startDecayFrame = now;

            int endDecayFrame = startDecayFrame + widthGrowFrames;
            int orbitalBirthFrame = startAttackFrame + beamTravelFrames;
            int orbitalDecayStart = startDecayFrame + beamTravelFrames;
            int orbitalDeathFrame = orbitalDecayStart + widthGrowFrames;

            switch (laserStatus)
            {
                case Laser.None:
                    if (orbitalBirthFrame <= now)
                    {
                        CreateOrbitToTargetLaser(widthGrowFrames);
                        laserStatus = Laser.Born;
                        scorchMarksMade = 0;
                        nextScorchMarkFrame = now;
                        damagePulsesMade = 0;
                        nextDamagePulseFrame = now;
                    }
                    break;
                case Laser.Born:
                    if (Game.Effects.FindBeam(orbitToTargetBeam) is { } born && orbitalDecayStart <= now)
                    {
                        born.SetDecayFrames(widthGrowFrames, now);
                        laserStatus = Laser.Decaying;
                    }
                    break;
                case Laser.Decaying:
                    if (Game.Effects.FindBeam(orbitToTargetBeam) is { })
                    {
                        StopSound(ref annihilationSound);
                        if (orbitalDeathFrame <= now)
                        {
                            Game.Effects.DestroyBeam(orbitToTargetBeam);
                            orbitToTargetBeam = 0;
                            laserStatus = Laser.Dead;
                            startAttackFrame = 0;
                            SetLogicalStatus(Status.Idle);
                        }
                    }
                    break;
            }

            if (Game.Effects.FindBeam(orbitToTargetBeam) is { } beam && orbitalBirthFrame <= now && now <= orbitalDeathFrame)
            {
                MoveTarget(now, orbitalBirthFrame, orbitalDeathFrame);
                currentTargetPosition.Z = Game.Terrain.GroundHeight(currentTargetPosition.X, currentTargetPosition.Y);
                var orbitPosition = currentTargetPosition + new Vector3(0, 0, OrbitHeight);
                beam.Init(Game, null, null, orbitPosition, currentTargetPosition);
                float scorchRadius = beam.CurrentRadius * scorchMarkScalar;
                float damageRadius = beam.CurrentRadius * damageRadiusScalar;

                if (nextScorchMarkFrame <= now)
                {
                    scorchMarksMade++;
                    // Which of the four scorch marks is cosmetic (the original uses the client's random numbers).
                    Game.Effects.Scorch(currentTargetPosition, scorchRadius, (int)((Owner.Id + (uint)scorchMarksMade * 7) % 4));
                    float nextFactor = scorchMarksMade / (float)Math.Max(1, totalScorchMarks);
                    nextScorchMarkFrame = orbitalBirthFrame + (int)(nextFactor * (orbitalDeathFrame - orbitalBirthFrame));
                    if (Setting("GroundHitFX") is { Length: > 0 } hitFx)
                        Game.Events.PlayFx(hitFx, currentTargetPosition);
                    uint mask = Owner.Owner.Index is >= 0 and < 32 ? 1u << Owner.Owner.Index : 0;
                    var center = new Vector2(currentTargetPosition.X, currentTargetPosition.Y);
                    Game.Shroud.DoShroudReveal(center, revealRange, mask);
                    Game.Shroud.UndoShroudReveal(center, revealRange, mask);
                }

                if (nextDamagePulseFrame <= now)
                {
                    damagePulsesMade++;
                    float totalFiringSeconds = (float)totalFiringFrames / LogicTime.FramesPerSecond;
                    float damagePerPulse = totalFiringSeconds * damagePerSecond / Math.Max(1, totalDamagePulses);
                    foreach (var victim in Game.Partition.Query(currentTargetPosition, damageRadius).ToList())
                    {
                        if (victim.IsEffectivelyDead || MathUtil.Distance2D(victim.Position, currentTargetPosition) > damageRadius)
                            continue;
                        victim.Damage(new DamageInfo { Amount = damagePerPulse, SourceId = Owner.Id, DamageType = damageType, DeathType = deathType });
                    }
                    if (Setting("DamagePulseRemnantObjectName") is { Length: > 0 } remnant)
                        Game.CreateObject(remnant, Owner.Owner, currentTargetPosition, 0, Owner.Team);
                    float nextFactor = damagePulsesMade / (float)Math.Max(1, totalDamagePulses);
                    nextDamagePulseFrame = orbitalBirthFrame + (int)(nextFactor * (orbitalDeathFrame - orbitalBirthFrame));
                }
            }

            if (endDecayFrame <= now)
                SetLogicalStatus(Status.Packing);
            else if (startDecayFrame <= now)
                SetLogicalStatus(Status.Postfire);
            else
                SetLogicalStatus(Status.Firing);
        }
        else if (readyToFireFrame <= now)
            SetLogicalStatus(Status.ReadyToFire);
        else if (almostReadyFrame <= now)
            SetLogicalStatus(Status.AlmostReady);
        else if (raiseAntennaFrame <= now)
            SetLogicalStatus(Status.Preparing);
        else if (beginChargeFrame <= now)
            SetLogicalStatus(Status.Charging);
        else if (status == Status.ReadyToFire)
            SetLogicalStatus(Status.Packing);

        if (status == Status.Firing && nextLaunchFxFrame <= now)
        {
            if (Setting("BeamLaunchFX") is { Length: > 0 } launchFx)
                Game.Events.PlayFx(launchFx, laserOriginPosition);
            nextLaunchFxFrame = now + framesBetweenLaunchFx;
        }
        return 0;
    }

    /// <summary>Where the beam goes this frame: along the sine swath, or driven towards the override destination.</summary>
    void MoveTarget(int now, int orbitalBirthFrame, int orbitalDeathFrame)
    {
        if (!manualTargetMode && !scriptedWaypointMode)
        {
            float factor = (now - orbitalBirthFrame) / (float)Math.Max(1, orbitalDeathFrame - orbitalBirthFrame);
            float radians = factor * MathF.Tau - MathF.PI;
            float cxDistance = factor * swathDistance - swathDistance * 0.5f;
            float cxHeight = MathF.Sin(radians) * swathAmplitude;
            var toInitial = initialTargetPosition - Owner.Position;
            float targetDistance = toInitial.Length();
            var cartesian = new Vector2(cxDistance + targetDistance, cxHeight);
            targetDistance = cartesian.Length();
            var buildingToTarget = new Vector2(initialTargetPosition.X - Owner.Position.X, initialTargetPosition.Y - Owner.Position.Y);
            buildingToTarget = buildingToTarget.LengthSquared() > 0 ? Vector2.Normalize(buildingToTarget) : Vector2.UnitX;
            var cartesianDir = cartesian.LengthSquared() > 0 ? Vector2.Normalize(cartesian) : Vector2.UnitX;
            float dot = Math.Clamp(Vector2.Dot(buildingToTarget, cartesianDir), -0.99999f, 0.99999f);
            float angle = MathF.Acos(dot);
            if (buildingToTarget.Y >= 0)
                angle = -angle;
            // The original rotates (distance, 0) by -angle.
            currentTargetPosition.X = Owner.Position.X + targetDistance * MathF.Cos(-angle);
            currentTargetPosition.Y = Owner.Position.Y + targetDistance * MathF.Sin(-angle);
            return;
        }

        float speed = manualSpeed;
        if (scriptedWaypointMode || lastDrivingClickFrame - secondLastDrivingClickFrame < doubleClickFastDriveDelay)
            speed = manualFastSpeed;
        speed /= LogicTime.FramesPerSecond;
        var vector = overrideTargetDestination - currentTargetPosition;
        float distance = vector.Length();
        if (distance < speed)
        {
            speed = distance;
            if (scriptedWaypointMode && nextDestinationWaypoint is { Next.Count: > 0 } way)
            {
                var next = way.Next[Game.Random.Int(0, way.Next.Count - 1)];
                nextDestinationWaypoint = next;
                overrideTargetDestination = next.Position;
            }
        }
        if (distance > 0)
            vector = vector / distance * speed;
        else
            vector = Vector3.Zero;
        currentTargetPosition.X += vector.X;
        currentTargetPosition.Y += vector.Y;
    }

    // ---- Status ----

    void SetConditions(string[] clear, string? set)
    {
        foreach (var c in clear)
            Owner.SetCondition(c, false);
        if (set != null)
            Owner.SetCondition(set, true);
    }

    void StopSound(ref int id)
    {
        if (id != 0)
            Game.Effects.StopSound(id);
        id = 0;
    }

    void StartSound(ref int id, string key)
    {
        StopSound(ref id);
        id = Game.Effects.StartSound(Setting(key), Owner);
    }

    void SetLogicalStatus(Status next)
    {
        if (status == next)
            return;
        switch (next)
        {
            case Status.Idle:
                SetConditions(["PACKING", "UNPACKING", "DEPLOYED"], null);
                StopSound(ref powerupSound);
                StopSound(ref unpackToReadySound);
                StopSound(ref firingToIdleSound);
                StopSound(ref annihilationSound);
                break;
            case Status.Charging:
                laserStatus = Laser.None;
                StartSound(ref powerupSound, "PoweringUpSoundLoop");
                StopSound(ref unpackToReadySound);
                StopSound(ref firingToIdleSound);
                StopSound(ref annihilationSound);
                break;
            case Status.Preparing:
                SetConditions(["PACKING", "DEPLOYED"], "UNPACKING");
                StartSound(ref unpackToReadySound, "UnpackToIdleSoundLoop");
                StopSound(ref firingToIdleSound);
                StopSound(ref annihilationSound);
                laserStatus = Laser.None;
                break;
            case Status.AlmostReady:
                SetConditions(["PACKING", "UNPACKING"], "DEPLOYED");
                laserStatus = Laser.None;
                break;
            case Status.ReadyToFire:
                SetConditions(["PACKING", "UNPACKING"], "DEPLOYED");
                StopSound(ref powerupSound);
                StopSound(ref firingToIdleSound);
                StopSound(ref annihilationSound);
                laserStatus = Laser.None;
                break;
            case Status.Firing:
                SetConditions(["PACKING", "UNPACKING"], "DEPLOYED");
                StartSound(ref firingToIdleSound, "FiringToPackSoundLoop");
                StopSound(ref powerupSound);
                StopSound(ref unpackToReadySound);
                StopSound(ref annihilationSound);
                nextLaunchFxFrame = 0;
                break;
            case Status.Packing:
                SetConditions(["UNPACKING", "DEPLOYED"], "PACKING");
                break;
        }
        status = next;
        SetClientStatus(status);
    }

    /// <summary>The effects for each status: node flares, connector lasers, the beams.</summary>
    void SetClientStatus(Status next)
    {
        if (!defaultInfoCached)
        {
            if (!CalculateDefaultInformation())
            {
                invalidSettings = true;
                return;
            }
            defaultInfoCached = true;
        }
        RemoveAllEffects();
        switch (next)
        {
            case Status.Charging:
                CreateOuterNodeParticleSystems(Intensity.Light);
                break;
            case Status.Preparing:
                CreateOuterNodeParticleSystems(Intensity.Medium);
                break;
            case Status.AlmostReady:
                CreateOuterNodeParticleSystems(Intensity.Medium);
                CreateConnectorLasers(Intensity.Medium);
                CreateConnectorFlare(Intensity.Medium);
                break;
            case Status.ReadyToFire:
                CreateOuterNodeParticleSystems(Intensity.Medium);
                CreateConnectorLasers(Intensity.Medium);
                CreateConnectorFlare(Intensity.Medium);
                CreateLaserBaseFlare(Intensity.Light);
                break;
            case Status.Firing:
                CreateGroundToOrbitLaser(widthGrowFrames);
                CreateOuterNodeParticleSystems(Intensity.Intense);
                CreateConnectorLasers(Intensity.Intense);
                CreateConnectorFlare(Intensity.Intense);
                CreateLaserBaseFlare(Intensity.Intense);
                break;
            case Status.Postfire:
                CreateOuterNodeParticleSystems(Intensity.Medium);
                CreateConnectorLasers(Intensity.Medium);
                CreateConnectorFlare(Intensity.Medium);
                CreateLaserBaseFlare(Intensity.Medium);
                CreateGroundToOrbitLaser(0);
                Game.Effects.FindBeam(groundToOrbitBeam)?.SetDecayFrames(widthGrowFrames, Frame);
                break;
        }
    }

    // ---- Effects ----

    bool CalculateDefaultInformation()
    {
        string boneName = Setting("OuterEffectBoneName");
        if (outerNodeCount == 0)
            return true;
        var bones = Game.Bones.NumberedBones(Owner, boneName, outerNodeCount);
        if (bones.Count != outerNodeCount)
        {
            // Without art (headless tests) the nodes sit at the building; the original treats this as an asset error.
            if (Game.Bones.HasArt)
            {
                Log.Warning($"{Owner.Template.Name}: the Particle Cannon needs {outerNodeCount} '{boneName}' bones but has {bones.Count}.");
                return false;
            }
            for (int i = 0; i < outerNodeCount; i++)
                outerNodePositions[i] = Owner.Position;
            return true;
        }
        for (int i = 0; i < outerNodeCount; i++)
        {
            outerNodePositions[i] = bones[i].Translation;
            outerNodeOrientations[i] = bones[i] with { M41 = 0, M42 = 0, M43 = 0 };
            connectorBeams[i] = 0;
            outerSystems[i] = 0;
        }
        return true;
    }

    /// <summary>The connector and beam origin bones, as the building stands now (antenna raised).</summary>
    void CalculateUpBonePositions()
    {
        if (Setting("ConnectorBoneName") is { Length: > 0 } connector)
        {
            if (Game.Bones.TryGetBone(Owner, connector, out var c))
                connectorNodePosition = c.Translation;
            if (Game.Bones.TryGetBone(Owner, Setting("FireBoneName"), out var fire))
                laserOriginPosition = fire.Translation;
        }
    }

    string ByIntensity(Intensity intensity, string light, string medium, string intense) => intensity switch
    {
        Intensity.Light => light.Length > 0 ? Setting(light) : "",
        Intensity.Medium => medium.Length > 0 ? Setting(medium) : "",
        Intensity.Intense => intense.Length > 0 ? Setting(intense) : "",
        _ => "",
    };

    void CreateOuterNodeParticleSystems(Intensity intensity)
    {
        string system = ByIntensity(intensity, "OuterNodesLightFlareParticleSystem", "OuterNodesMediumFlareParticleSystem", "OuterNodesIntenseFlareParticleSystem");
        if (system.Length == 0)
            return;
        for (int i = 0; i < outerNodeCount; i++)
            outerSystems[i] = Game.Effects.CreateParticleSystem(system, outerNodePositions[i], outerNodeOrientations[i]);
    }

    void CreateConnectorLasers(Intensity intensity)
    {
        if (!upBonesCached)
        {
            CalculateUpBonePositions();
            upBonesCached = true;
        }
        string laser = ByIntensity(intensity, "", "ConnectorMediumLaserName", "ConnectorIntenseLaserName");
        if (laser.Length == 0)
            return;
        for (int i = 0; i < outerNodeCount; i++)
        {
            if (Game.Effects.CreateBeam(laser) is not { } beam)
                break;
            connectorBeams[i] = beam.Id;
            beam.Init(Game, null, null, outerNodePositions[i], connectorNodePosition);
        }
    }

    void CreateConnectorFlare(Intensity intensity)
    {
        string system = ByIntensity(intensity, "", "ConnectorMediumFlare", "ConnectorIntenseFlare");
        if (system.Length > 0)
            connectorSystem = Game.Effects.CreateParticleSystem(system, connectorNodePosition);
    }

    void CreateLaserBaseFlare(Intensity intensity)
    {
        string system = ByIntensity(intensity, "LaserBaseLightFlareParticleSystemName", "LaserBaseMediumFlareParticleSystemName", "LaserBaseIntenseFlareParticleSystemName");
        if (system.Length > 0)
            laserBaseSystem = Game.Effects.CreateParticleSystem(system, laserOriginPosition);
    }

    void CreateGroundToOrbitLaser(int growthFrames)
    {
        // (The original clears the wrong ID here; the old beam is destroyed either way.)
        if (groundToOrbitBeam != 0)
            Game.Effects.DestroyBeam(groundToOrbitBeam);
        groundToOrbitBeam = 0;
        if (Game.Effects.CreateBeam(Setting("ParticleBeamLaserName")) is { } beam)
        {
            groundToOrbitBeam = beam.Id;
            beam.Init(Game, null, null, laserOriginPosition, laserOriginPosition + new Vector3(0, 0, OrbitHeight), "", growthFrames);
        }
    }

    void CreateOrbitToTargetLaser(int growthFrames)
    {
        if (orbitToTargetBeam != 0)
        {
            StopSound(ref annihilationSound);
            Game.Effects.DestroyBeam(orbitToTargetBeam);
            orbitToTargetBeam = 0;
        }
        if (Game.Effects.CreateBeam(Setting("ParticleBeamLaserName")) is { } beam)
        {
            orbitToTargetBeam = beam.Id;
            beam.Init(Game, null, null, initialTargetPosition + new Vector3(0, 0, OrbitHeight), initialTargetPosition, "", growthFrames);
            StopSound(ref annihilationSound);
            annihilationSound = Game.Effects.StartSound(Setting("GroundAnnihilationSoundLoop"), null, initialTargetPosition);
        }
    }

    void RemoveAllEffects()
    {
        for (int i = 0; i < outerNodeCount; i++)
        {
            if (outerSystems[i] != 0)
                Game.Effects.DestroyParticleSystem(outerSystems[i]);
            outerSystems[i] = 0;
            if (connectorBeams[i] != 0)
                Game.Effects.DestroyBeam(connectorBeams[i]);
            connectorBeams[i] = 0;
        }
        if (connectorSystem != 0)
            Game.Effects.DestroyParticleSystem(connectorSystem);
        connectorSystem = 0;
        if (laserBaseSystem != 0)
            Game.Effects.DestroyParticleSystem(laserBaseSystem);
        laserBaseSystem = 0;
        if (groundToOrbitBeam != 0)
            Game.Effects.DestroyBeam(groundToOrbitBeam);
        groundToOrbitBeam = 0;
    }

    void KillEverything()
    {
        RemoveAllEffects();
        if (orbitToTargetBeam != 0)
            Game.Effects.DestroyBeam(orbitToTargetBeam);
        orbitToTargetBeam = 0;
        StopSound(ref powerupSound);
        StopSound(ref unpackToReadySound);
        StopSound(ref firingToIdleSound);
        StopSound(ref annihilationSound);
    }
}

using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// A train (the original's RailroadBehavior). The locomotive finds the map waypoint it stands on and
/// follows that waypoint chain as its track: it speeds up to SpeedMax, brakes at waypoints named
/// "...Station" (letting passengers off at "...Disembark"), waits WaitAtStationTime, reverses at
/// "...PingPong" and hides in "...Tunnel" sections. CarriageTemplateName cars are hitched behind and
/// pulled along the track. It flattens what it runs into and pushes things out of the way.
/// </summary>
public sealed class RailroadBehavior : Module, IUpdateModule, ICollideModule, IOwnershipModule
{
    sealed class TrackPoint
    {
        public Vector3 Position;
        public float DistanceFromPrev, DistanceFromFirst;
        public bool IsFirstPoint, IsLastPoint, IsTunnelOrBridge, IsStation, IsPingPong, IsDisembark;
        public int Handle;
    }

    sealed class TrainTrack
    {
        public readonly List<TrackPoint> Points = new();
        public float Length;
        public bool IsLooping;
    }

    sealed class PullInfo
    {
        public float Speed, TrackDistance, Direction = 1;
        public Vector3 TowHitchPosition;
        public int MostRecentSpecialPointHandle = unchecked((int)0xfacade);
        public PullInfo Clone() => (PullInfo)MemberwiseClone();
    }

    enum ConductorState { Coast, Accelerate, ApplyBrakes, WaitAtStation }

    const int FramesUnpulledLongEnoughToUnhitch = 2;

    TrainTrack? track;
    PullInfo conductorPullInfo = new(), pullInfo = new();
    ConductorState conductorState;
    uint trailerId;
    int currentPointHandle = unchecked((int)0xfacade);
    int waitAtStationTimer;
    int anchorWaypointId = -1;
    bool carriagesCreated, hasEverBeenHitched, trackDataLoaded, waitingInWings = true, endOfLine;
    bool isLocomotive, isLeadCarriage, disembarkPending, inTunnel;
    int wantsToBeLeadCarriage;
    uint runningSound;

    /// <summary>Held at the station by a script (the original's m_held).</summary>
    public bool Held { get; set; }
    public bool HasEverBeenHitched => hasEverBeenHitched;

    protected override void Initialize()
    {
        isLocomotive = BoolSetting("IsLocomotive");
        isLeadCarriage = isLocomotive;
        conductorState = isLocomotive ? ConductorState.Accelerate : ConductorState.Coast;
    }

    float SpeedMax => FloatSetting("SpeedMax", 4);
    int WaitAtStationTime => Definition.Settings.Has("WaitAtStationTime") ? FramesSetting("WaitAtStationTime") : 150;

    /// <summary>Running into another train counts only while this one leads (the original's isRailroad).</summary>
    public bool IsRailroad => track != null && !waitingInWings && !endOfLine && (isLeadCarriage || trailerId == 0);

    RailroadBehavior? Rail(uint id) => Game.FindObject(id)?.FindModule<RailroadBehavior>();

    public void OnCollide(GameObject? other, Vector3 location, Vector3 normal)
    {
        var obj = Owner;
        if (other == null || waitingInWings || endOfLine)
            return;
        if (other.FindModule<RailroadBehavior>() is { IsRailroad: true })
        {
            if (isLocomotive)
                other.Kill();
            else if (isLeadCarriage)
            {
                other.Kill();
                obj.Kill();
            }
            return;
        }
        if (other.IsKindOf(Kind.Structure))
        {
            if (other.IsKindOf("FS_POWER") || other.IsKindOf("FS_FACTORY") || other.IsKindOf("FS_BASE_DEFENSE") || other.IsKindOf("FS_TECHNOLOGY") || other.IsKindOf("REBUILD_HOLE"))
            {
                PlayImpactSound(other);
                other.Kill();
                return;
            }
            if (other.FindModule<DemoTrapUpdate>() != null)
            {
                if (!other.IsUnderConstruction)
                    obj.Kill();
                PlayImpactSound(other);
                other.Kill();
                return;
            }
        }
        if (other.FindModule<Updates.PhysicsBehavior>() is not { } theirPhys)
            return;
        float runningGarrisonSpeedMax = FloatSetting("RunningGarrisonSpeedMax", 1);
        // Passengers boarding at a station are let through.
        if (conductorState == ConductorState.WaitAtStation && pullInfo.Speed < runningGarrisonSpeedMax && other.AI?.EnterTargetId == obj.Id)
            return;
        if (other.ContainedById == obj.Id)
            return;

        bool victimIsInfantry = other.IsKindOf(Kind.Infantry);
        var myDir = new Vector3(MathF.Cos(obj.Orientation), MathF.Sin(obj.Orientation), 0);
        var dlt = other.Position - obj.Position;
        if (conductorState != ConductorState.WaitAtStation)
            Game.Events.PlaySound(Setting("WhistleSound"), obj.Position, source: obj);
        float dist = dlt.Length();
        float overlap = obj.Template.GeometryMajorRadius + other.Template.GeometryMajorRadius - dist + 1;
        if (dist > 0)
            dlt /= dist;
        if (!victimIsInfantry)
            other.SetPosition(other.Position + dlt * (overlap / 4));
        if (conductorState == ConductorState.WaitAtStation || (conductorState == ConductorState.Coast && pullInfo.Speed < runningGarrisonSpeedMax) || !isLocomotive)
            return;

        var delta = other.Position - obj.Position;
        if (delta.LengthSquared() > 0)
            delta = Vector3.Normalize(delta);
        float dot = Vector3.Dot(delta, myDir);
        if (other.IsEffectivelyDead)
            delta *= Math.Min(0.3f, pullInfo.Speed * 0.66f);
        else
        {
            delta *= Math.Min(1.4f, pullInfo.Speed * 0.66f);
            if (pullInfo.Speed >= FloatSetting("KillSpeedMin", 1))
            {
                other.Kill();
                theirPhys.PitchRate = Game.Random.Float(-0.03f, 0.03f);
                theirPhys.RollRate = Game.Random.Float(-0.03f, 0.03f);
            }
            else
            {
                PlayImpactSound(other);
                other.Damage(new DamageInfo { DamageType = "CRUSH", DeathType = "CRUSHED", SourceId = obj.Id, Amount = pullInfo.Speed * 10 });
            }
        }
        var heft = other.Position;
        heft.Z = Math.Max(heft.Z, Game.Terrain.GroundHeight(heft) + 2);
        other.SetPosition(heft);
        delta.Z = Game.Random.Float(0.05f, Math.Max(0.05f, pullInfo.Speed / 10));
        delta *= dot;
        if (!(victimIsInfantry && theirPhys.VelocityMagnitude > 5))
            theirPhys.AddVelocity(delta);
        theirPhys.AllowToFall = true;
        theirPhys.AllowBouncing = true;
        theirPhys.AllowAirborneFriction = true;
        var cross = Vector3.Cross(myDir, Vector3.UnitZ);
        if (delta.LengthSquared() > 0)
            delta = Vector3.Normalize(delta);
        if (dot > 0)
            theirPhys.YawRate = Vector3.Dot(cross, delta) * -0.06f * pullInfo.Speed;
    }

    void PlayImpactSound(GameObject victim)
    {
        string sound = victim.IsKindOf(Kind.Infantry) ? Setting("MeatyBounceSound")
            : victim.IsKindOf("HUGE_VEHICLE") || victim.IsKindOf(Kind.Structure) ? Setting("BigMetalBounceSound")
            : victim.IsKindOf("VEHICLE") ? Setting("SmallMetalBounceSound") : "";
        if (sound.Length > 0)
            Game.Events.PlaySound(sound, victim.Position, victim.Owner, victim);
    }

    /// <summary>The track is the waypoint chain starting at the waypoint nearest the locomotive.</summary>
    void LoadTrackData()
    {
        if (track != null || Game.Waypoints is not { } waypoints)
            return;
        var myPos = Owner.Position;
        Waypoint? anchor = null;
        if (anchorWaypointId < 0)
        {
            float closest = 99999.9f;
            foreach (var w in waypoints.All.OrderBy(w => w.Id))
            {
                float d = (myPos - w.Position).Length();
                if (closest > d)
                {
                    closest = d;
                    anchor = w;
                    anchorWaypointId = w.Id;
                }
            }
        }
        else
            anchor = waypoints.All.FirstOrDefault(w => w.Id == anchorWaypointId);
        if (anchor == null)
            return;
        track = new TrainTrack();
        var scanner = anchor;
        track.Points.Add(new TrackPoint
        {
            IsFirstPoint = true,
            IsTunnelOrBridge = scanner.Name.EndsWith("Tunnel", StringComparison.Ordinal),
            IsStation = scanner.Name.EndsWith("Station", StringComparison.Ordinal),
            IsDisembark = scanner.Name.EndsWith("Disembark", StringComparison.Ordinal),
            Position = scanner.Position,
            Handle = scanner.Id,
        });
        while (scanner.Next.Count > 0)
        {
            var another = scanner.Next[0];
            float distFromTo = (scanner.Position - another.Position).Length();
            track.Length += distFromTo;
            track.Points.Add(new TrackPoint
            {
                DistanceFromPrev = distFromTo,
                DistanceFromFirst = track.Length,
                IsLastPoint = another.Next.Count == 0,
                IsTunnelOrBridge = another.Name.EndsWith("Tunnel", StringComparison.Ordinal),
                IsStation = another.Name.EndsWith("Station", StringComparison.Ordinal),
                IsPingPong = scanner.Name.EndsWith("PingPong", StringComparison.Ordinal),
                IsDisembark = scanner.Name.EndsWith("Disembark", StringComparison.Ordinal),
                Position = another.Position,
                Handle = scanner.Id,
            });
            scanner = another;
            if (scanner == anchor)
            {
                track.IsLooping = true;
                break;
            }
            if (track.Points.Count > 10000)
                break;
        }
    }

    /// <summary>A stopped train blocks the way like a wall (the original's makeAWallOutOfThisTrain).</summary>
    void MakeAWallOutOfThisTrain(bool on)
    {
        if (on)
            Game.Pathfinder.AddObstacle(Owner);
        else
            Game.Pathfinder.RemoveObstacle(Owner);
        Rail(trailerId)?.MakeAWallOutOfThisTrain(on);
    }

    public int Update()
    {
        if (!trackDataLoaded && isLocomotive)
        {
            LoadTrackData();
            if (track != null)
                CreateCarriages();
            trackDataLoaded = true;
        }
        if (track == null)
            return 0;

        if (isLocomotive)
        {
            switch (conductorState)
            {
                case ConductorState.ApplyBrakes:
                    conductorPullInfo.Speed *= FloatSetting("Braking", 0.99f);
                    if (MathF.Abs(conductorPullInfo.Speed) < 0.1f)
                    {
                        conductorPullInfo.Speed = 0;
                        waitAtStationTimer = WaitAtStationTime;
                        conductorState = ConductorState.WaitAtStation;
                        MakeAWallOutOfThisTrain(true);
                        if (disembarkPending)
                        {
                            Disembark();
                            disembarkPending = false;
                        }
                    }
                    break;
                case ConductorState.WaitAtStation:
                    --waitAtStationTimer;
                    if (waitAtStationTimer <= 0 && !Held)
                    {
                        conductorState = ConductorState.Accelerate;
                        conductorPullInfo.Speed = 0.05f * conductorPullInfo.Direction;
                        StartRunningSound();
                        MakeAWallOutOfThisTrain(false);
                    }
                    else if (waitAtStationTimer == WaitAtStationTime / 4)
                        Game.Events.PlaySound(Setting("WhistleSound"), Owner.Position, source: Owner);
                    break;
                case ConductorState.Accelerate:
                    conductorPullInfo.Speed += 0.02f * conductorPullInfo.Direction;
                    conductorPullInfo.Speed *= FloatSetting("Acceleration", 1.01f);
                    conductorPullInfo.Speed = Math.Clamp(conductorPullInfo.Speed, -SpeedMax, SpeedMax);
                    StartRunningSound();
                    break;
            }
        }

        if (wantsToBeLeadCarriage > FramesUnpulledLongEnoughToUnhitch)
            isLeadCarriage = true;
        if (isLeadCarriage)
        {
            if (conductorState == ConductorState.Coast)
            {
                conductorPullInfo.Speed *= FloatSetting("Friction", 0.97f);
                StopRunningSound();
            }
            conductorPullInfo.TrackDistance += conductorPullInfo.Speed;
            while (conductorPullInfo.TrackDistance > track.Length && track.IsLooping && track.Length > 0)
                conductorPullInfo.TrackDistance -= track.Length;
            while (conductorPullInfo.TrackDistance < 0 && track.IsLooping && track.Length > 0)
                conductorPullInfo.TrackDistance += track.Length;
            conductorPullInfo.TowHitchPosition = FindPosByPathDistance(conductorPullInfo.TrackDistance, track.Length);
            UpdatePositionTrackDistance(conductorPullInfo, pullInfo);
            if (Rail(trailerId) is { } trailer)
                trailer.GetPulled(pullInfo);
            else
            {
                trailerId = 0;
                if (endOfLine)
                    Game.DestroyObject(Owner);
            }
        }
        else if (wantsToBeLeadCarriage <= FramesUnpulledLongEnoughToUnhitch)
            wantsToBeLeadCarriage++;

        if (!track.IsLooping)
            Owner.DrawableHidden = waitingInWings || endOfLine;
        Owner.SetCondition("OVER_WATER", Owner.Position.Z < Game.Terrain.GroundHeight(Owner.Position) - 3);
        return 0;
    }

    void StartRunningSound()
    {
        if (runningSound == 0)
            runningSound = Game.Events.PlayHandledSound(Setting("RunningSound"), Owner);
    }

    void StopRunningSound()
    {
        Game.Events.StopHandledSound(runningSound);
        runningSound = 0;
    }

    void Disembark()
    {
        Owner.Contain?.OrderAllPassengersToExit(CommandSource.AI, false);
        Rail(trailerId)?.Disembark();
    }

    /// <summary>The cars named in CarriageTemplateName, or ones already standing behind the engine on the map.</summary>
    void CreateCarriages()
    {
        if (!isLocomotive)
            return;
        var self = Owner;
        var list = ListSetting("CarriageTemplateName").SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var closeCarriage = Game.FindObject(trailerId) ?? ClosestUnhitchedCarriage(self);
        if (list.Count > 0 && Game.Templates.Find(list[0]) is { } first)
        {
            var firstCarriage = closeCarriage ?? Game.CreateObject(first, self.Owner, self.Position, self.Orientation, self.Team);
            firstCarriage.ProducerId = self.Id;
            trailerId = firstCarriage.Id;
            if (firstCarriage.FindModule<RailroadBehavior>() is { } rb)
            {
                if (closeCarriage != null)
                    rb.HitchNewCarriageByProximity(self.Id, track!);
                else
                    rb.HitchNewCarriageByTemplate(self.Id, list, 1, track!);
            }
        }
        carriagesCreated = true;
    }

    GameObject? ClosestUnhitchedCarriage(GameObject self)
    {
        float maxRadius = self.Template.GeometryMajorRadius * 2;
        var hitch = self.Position - new Vector3(MathF.Cos(self.Orientation), MathF.Sin(self.Orientation), 0) * maxRadius;
        return Game.ObjectsInRadius(hitch, maxRadius)
            .Where(o => o != self && o.FindModule<RailroadBehavior>() is { HasEverBeenHitched: false } && self.RelationshipTo(o) == Relationship.Allies)
            .Where(o => MathUtil.Distance2D(o.Position, hitch) <= maxRadius)
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, hitch))
            .FirstOrDefault();
    }

    void HitchNewCarriageByTemplate(uint locoId, List<string> list, int next, TrainTrack sharedTrack)
    {
        if (isLocomotive || Game.FindObject(locoId) is not { } locomotive)
            return;
        track = sharedTrack;
        hasEverBeenHitched = true;
        if (next < list.Count && Game.Templates.Find(list[next]) is { } template)
        {
            var newCarriage = Game.CreateObject(template, locomotive.Owner, Owner.Position, Owner.Orientation, locomotive.Team);
            newCarriage.ProducerId = locomotive.Id;
            trailerId = newCarriage.Id;
            newCarriage.FindModule<RailroadBehavior>()?.HitchNewCarriageByTemplate(locoId, list, next + 1, sharedTrack);
        }
    }

    void HitchNewCarriageByProximity(uint locoId, TrainTrack sharedTrack)
    {
        if (isLocomotive || Game.FindObject(locoId) is null)
            return;
        track = sharedTrack;
        hasEverBeenHitched = true;
        var self = Owner;
        var closeCarriage = Game.FindObject(trailerId) ?? ClosestUnhitchedCarriage(self);
        if (closeCarriage != null)
        {
            closeCarriage.ProducerId = self.Id;
            trailerId = closeCarriage.Id;
            closeCarriage.FindModule<RailroadBehavior>()?.HitchNewCarriageByProximity(self.Id, sharedTrack);
        }
    }

    void GetPulled(PullInfo info)
    {
        wantsToBeLeadCarriage = 0;
        if (track == null)
            return;
        conductorPullInfo = info.Clone();
        UpdatePositionTrackDistance(info, pullInfo);
        if (Rail(trailerId) is { } trailer)
            trailer.GetPulled(pullInfo);
        else
        {
            trailerId = 0;
            if (endOfLine)
                Game.DestroyObject(Owner);
        }
    }

    /// <summary>Puts this car on the track behind whatever pulls it, turned towards the puller's hitch.</summary>
    void UpdatePositionTrackDistance(PullInfo puller, PullInfo mine)
    {
        if (track == null)
            return;
        var obj = Owner;
        float hitchRadius = obj.Template.GeometryMajorRadius;
        mine.TrackDistance = puller.TrackDistance - hitchRadius * 2;
        mine.Speed = puller.Speed;
        mine.Direction = puller.Direction;
        mine.TowHitchPosition = FindPosByPathDistance(mine.TrackDistance, track.Length);
        var carPosition = FindPosByPathDistance(mine.TrackDistance, track.Length, setState: true);
        // The car pivots about a point behind its middle, towards the hitch ahead, then slides onto the track.
        float desiredAngle = MathF.Atan2(puller.TowHitchPosition.Y - (obj.Position.Y - MathF.Sin(obj.Orientation) * hitchRadius),
            puller.TowHitchPosition.X - (obj.Position.X - MathF.Cos(obj.Orientation) * hitchRadius));
        var forward = new Vector3(MathF.Cos(desiredAngle), MathF.Sin(desiredAngle), 0);
        var newPos = new Vector3(carPosition.X, carPosition.Y, obj.Position.Z) + forward * hitchRadius;
        var turnPos = new Vector3(carPosition.X, carPosition.Y, 0);
        if (!inTunnel)
            newPos.Z = Game.Terrain.GroundHeight(turnPos);
        obj.SetOrientation(desiredAngle);
        obj.SetPosition(newPos);
    }

    /// <summary>A point a distance along the track (the original's FindPosByPathDistance); for this car's own position it also notices stations.</summary>
    Vector3 FindPosByPathDistance(float dist, float length, bool setState = false)
    {
        if (track == null)
            return default;
        waitingInWings = false;
        float actualDistance = dist;
        if (track.IsLooping)
        {
            if (length > 0)
            {
                while (actualDistance < 0)
                    actualDistance += length;
                while (actualDistance > length)
                    actualDistance -= length;
            }
        }
        else
        {
            if (dist < 0)
                waitingInWings = true;
            else if (dist >= length)
                endOfLine = true;
            actualDistance = Math.Max(Math.Min(length, dist), 0);
        }
        var pos = Vector3.Zero;
        var points = track.Points;
        for (int i = 0; i < points.Count; i++)
        {
            var thisPoint = points[i];
            var nextPoint = i + 1 < points.Count ? points[i + 1] : null;
            if (thisPoint.DistanceFromFirst >= actualDistance)
                continue;
            var thisPointPos = thisPoint.Position;
            if (nextPoint != null && nextPoint.DistanceFromFirst > actualDistance)
            {
                int handleFound = thisPoint.Handle;
                bool edge = currentPointHandle != handleFound;
                if (setState)
                {
                    inTunnel = thisPoint.IsTunnelOrBridge;
                    if (isLocomotive && edge)
                    {
                        currentPointHandle = handleFound;
                        if (thisPoint.IsStation)
                        {
                            conductorState = ConductorState.ApplyBrakes;
                            disembarkPending = false;
                            StopRunningSound();
                        }
                        else if (thisPoint.IsDisembark)
                        {
                            conductorState = ConductorState.ApplyBrakes;
                            disembarkPending = true;
                            StopRunningSound();
                        }
                        else if (thisPoint.IsPingPong && conductorPullInfo.MostRecentSpecialPointHandle != handleFound)
                        {
                            conductorPullInfo.MostRecentSpecialPointHandle = handleFound;
                            conductorState = ConductorState.ApplyBrakes;
                            disembarkPending = false;
                            StopRunningSound();
                            conductorPullInfo.Direction = -conductorPullInfo.Direction;
                        }
                    }
                    if (edge && !inTunnel)
                        Game.Events.PlaySound(Setting("ClicketyClackSound"), Owner.Position, source: Owner);
                }
                float difference = actualDistance - thisPoint.DistanceFromFirst;
                var delta = nextPoint.Position - thisPointPos;
                if (delta.LengthSquared() > 0)
                    delta = Vector3.Normalize(delta) * difference;
                return thisPointPos + delta;
            }
            pos = thisPointPos;
        }
        return pos;
    }

    public void DestroyTheWholeTrainNow()
    {
        Game.DestroyObject(Owner);
        Rail(trailerId)?.DestroyTheWholeTrainNow();
    }

    public void OnDelete() => StopRunningSound();
}

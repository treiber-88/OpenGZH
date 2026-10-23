using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Logic;

namespace OpenGZH.Client.World;

/// <summary>
/// The original's parabolic ease (ParabolicEase::setEaseTimes): accelerate for the first
/// <c>easeIn</c> of the time, glide, then slow down for the last <c>easeOut</c>.
/// </summary>
public readonly struct ParabolicEase
{
    readonly float easeIn, easeOut;

    public ParabolicEase(float easeIn, float easeOut)
    {
        this.easeIn = Math.Clamp(easeIn, 0, 1);
        this.easeOut = Math.Clamp(easeOut, 0, 1 - this.easeIn);
    }

    public float Apply(float t)
    {
        t = Math.Clamp(t, 0, 1);
        float a = easeIn, b = easeOut;
        float v = 2 / (2 - a - b);
        if (t < a)
            return v * t * t / (2 * a);
        if (t <= 1 - b)
            return v * (t - a / 2);
        float rest = 1 - t;
        return 1 - v * rest * rest / (2 * b);
    }
}

/// <summary>
/// Script-driven camera moves (the original W3DView's camera code): moving along a smoothed waypoint
/// path with the camera turning to the path, rotating, zooming and pitching over time, and the
/// "mod" actions that adjust a move already under way. Angles are the original's (0 = looking
/// north); zoom 1 is the normal camera height, pitch 1 the normal pitch.
/// </summary>
public sealed class ScriptedCamera
{
    const float FrameMs = 1000f / 30f;
    const int MaxWaypoints = 25;

    readonly Camera camera;
    readonly GameLogic game;
    readonly float defaultDistance, defaultPitch, maxHeightAboveGround, cameraHeight;

    public float Angle;
    public float Zoom = 1;
    public float FxPitch = 1;
    public Vector3 Position;

    // Move along a waypoint path (m_mcwpInfo).
    readonly Vector3[] waypoints = new Vector3[MaxWaypoints + 2];
    readonly float[] cameraAngle = new float[MaxWaypoints + 2];
    readonly float[] waySegLength = new float[MaxWaypoints + 2];
    int numWaypoints, curSegment;
    float totalTimeMs, elapsedMs, curSegDistance, totalDistance;
    int shutter, curShutter, rollingAverageFrames = 1;
    ParabolicEase moveEase;
    bool doingMove, arrivedAtWaypoint;

    // Rotate (m_rcInfo).
    bool doingRotate, rotateTrackObject;
    int rotateFrames, rotateHoldFrames, rotateCurFrame;
    float rotateStart, rotateEnd;
    uint rotateTargetId;
    Vector3 rotateTargetPos;
    ParabolicEase rotateEase;

    // Zoom and pitch (m_zcInfo, m_pcInfo).
    bool doingZoom, doingPitch;
    int zoomFrames, zoomCurFrame, pitchFrames, pitchCurFrame;
    float zoomStart, zoomEnd, pitchStart, pitchEnd;
    ParabolicEase zoomEase, pitchEase;

    float carriedMs;

    /// <summary>The camera follows this object (CAMERA_FOLLOW_NAMED / TETHER); 0 = none.</summary>
    public uint LockedObjectId;
    public bool SnapToLock;
    public float TetherDistance;

    public ScriptedCamera(Camera camera, GameLogic game)
    {
        this.camera = camera;
        this.game = game;
        defaultDistance = camera.Distance;
        defaultPitch = camera.Pitch;
        var gameData = game.Rules.Section("GameData");
        cameraHeight = OpenGZH.Core.Rules.RuleValue.Float(gameData.GetString("CameraHeight"), 232);
        maxHeightAboveGround = OpenGZH.Core.Rules.RuleValue.Float(gameData.GetString("MaxCameraHeight"), 310);
        Sync();
    }

    /// <summary>Takes the camera's current state (the player moved it).</summary>
    public void Sync()
    {
        Position = camera.Target;
        Angle = NormAngle(camera.Yaw + MathF.PI / 2);
        Zoom = camera.Distance / defaultDistance;
        FxPitch = camera.Pitch / defaultPitch;
    }

    public bool IsBusy => doingMove || doingRotate || doingZoom || doingPitch || LockedObjectId != 0;

    /// <summary>The original's isCameraMovementFinished.</summary>
    public bool IsMovementFinished => !doingMove && !doingRotate && !doingPitch && !doingZoom;

    /// <summary>The original's isCameraMovementAtWaypointAlongPath: true once per waypoint passed.</summary>
    public bool ConsumeArrivedAtWaypoint()
    {
        bool value = arrivedAtWaypoint;
        arrivedAtWaypoint = false;
        return value;
    }

    static float NormAngle(float a)
    {
        while (a > MathF.PI) a -= 2 * MathF.PI;
        while (a < -MathF.PI) a += 2 * MathF.PI;
        return a;
    }

    static float AngleToward(Vector3 from, Vector3 to, out bool valid)
    {
        var dir = new Vector2(to.X - from.X, to.Y - from.Y);
        float len = dir.Length();
        valid = len >= 0.1f;
        if (!valid)
            return 0;
        float angle = MathF.Acos(dir.X / len);
        if (dir.Y < 0)
            angle = -angle;
        return NormAngle(angle - MathF.PI / 2);
    }

    float MaxZoomAt(Vector3 pos)
    {
        float terrainHeightMax = game.Terrain.GroundHeight(pos);
        return (terrainHeightMax + maxHeightAboveGround) / cameraHeight;
    }

    public void LookAt(Vector3 pos)
    {
        Position = pos with { Z = 0 };
        doingRotate = false;
        doingMove = false;
        arrivedAtWaypoint = false;
    }

    public void MoveCameraTo(Vector3 o, float ms, float shutterMs, bool orient, float easeInMs, float easeOutMs)
    {
        waypoints[0] = Position;
        cameraAngle[0] = Angle;
        waySegLength[0] = 0;
        waypoints[1] = Position;
        waySegLength[1] = 0;
        waypoints[2] = o;
        waySegLength[2] = 0;
        numWaypoints = 2;
        if (ms < 1) ms = 1;
        totalTimeMs = ms;
        shutter = 1;
        moveEase = new ParabolicEase(easeInMs / ms, easeOutMs / ms);
        SetupWaypointPath(orient);
        if (totalTimeMs <= 1)
        {
            MoveAlongWaypointPath(1);
            doingMove = true;
            arrivedAtWaypoint = false;
        }
    }

    public void MoveCameraAlongWaypointPath(IReadOnlyList<Vector3> path, float ms, float shutterMs, bool orient, float easeInMs, float easeOutMs)
    {
        const float MinDelta = 10f;
        waypoints[0] = Position;
        cameraAngle[0] = Angle;
        waySegLength[0] = 0;
        waypoints[1] = Position;
        numWaypoints = 1;
        if (ms < 1) ms = 1;
        totalTimeMs = ms;
        shutter = Math.Max(1, (int)(shutterMs / FrameMs));
        moveEase = new ParabolicEase(easeInMs / ms, easeOutMs / ms);
        for (int i = 0; i < path.Count && numWaypoints < MaxWaypoints; i++)
        {
            numWaypoints++;
            waypoints[numWaypoints] = path[i];
            bool more = i + 1 < path.Count;
            var d = new Vector2(waypoints[numWaypoints].X - waypoints[numWaypoints - 1].X, waypoints[numWaypoints].Y - waypoints[numWaypoints - 1].Y);
            if (d.Length() < MinDelta)
            {
                if (!more)
                    waypoints[numWaypoints - 1] = waypoints[numWaypoints];
                numWaypoints--;
            }
        }
        SetupWaypointPath(orient);
    }

    void SetupWaypointPath(bool orient)
    {
        curSegment = 1;
        curSegDistance = 0;
        totalDistance = 0;
        rollingAverageFrames = 1;
        float angle = Angle;
        for (int i = 1; i < numWaypoints; i++)
        {
            var dir = new Vector2(waypoints[i + 1].X - waypoints[i].X, waypoints[i + 1].Y - waypoints[i].Y);
            waySegLength[i] = dir.Length();
            totalDistance += waySegLength[i];
            if (orient && waySegLength[i] > 0)
            {
                angle = MathF.Acos(dir.X / waySegLength[i]);
                if (dir.Y < 0)
                    angle = -angle;
                angle = NormAngle(angle - MathF.PI / 2);
            }
            cameraAngle[i] = angle;
        }
        cameraAngle[1] = Angle;
        cameraAngle[numWaypoints] = cameraAngle[numWaypoints - 1];
        for (int i = numWaypoints - 1; i > 1; i--)
            cameraAngle[i] = (cameraAngle[i] + cameraAngle[i - 1]) / 2;
        waySegLength[numWaypoints + 1] = waySegLength[numWaypoints];
        if (totalDistance < 1)
        {
            waySegLength[numWaypoints - 1] += 1 - totalDistance;
            totalDistance = 1;
        }
        waypoints[numWaypoints + 1] = waypoints[numWaypoints] + (waypoints[numWaypoints] - waypoints[numWaypoints - 1]) with { Z = 0 };
        cameraAngle[numWaypoints + 1] = cameraAngle[numWaypoints];
        waypoints[0] -= (waypoints[2] - waypoints[1]) with { Z = 0 };
        doingMove = numWaypoints > 1;
        arrivedAtWaypoint = false;
        doingRotate = false;
        elapsedMs = 0;
        curShutter = shutter;
    }

    void MoveAlongWaypointPath(float ms)
    {
        elapsedMs += ms;
        if (elapsedMs > totalTimeMs)
        {
            doingMove = false;
            arrivedAtWaypoint = false;
            Angle = cameraAngle[numWaypoints];
            Position = waypoints[numWaypoints] with { Z = 0 };
            return;
        }
        float deltaTime = moveEase.Apply(elapsedMs / totalTimeMs) - moveEase.Apply((elapsedMs - ms) / totalTimeMs);
        curSegDistance += deltaTime * totalDistance;
        while (curSegDistance >= waySegLength[curSegment])
        {
            if (doingMove)
                arrivedAtWaypoint = true;
            curSegDistance -= waySegLength[curSegment];
            curSegment++;
            if (curSegment >= numWaypoints)
            {
                totalTimeMs = 0;
                return;
            }
        }
        float avgFactor = 1f / rollingAverageFrames;
        if (--curShutter > 0)
            return;
        curShutter = shutter;
        float factor = curSegDistance / waySegLength[curSegment];
        if (curSegment == numWaypoints - 1)
            avgFactor += (1 - avgFactor) * factor;
        float factor1 = 1 - factor, factor2 = factor;
        float angle1 = cameraAngle[curSegment], angle2 = cameraAngle[curSegment + 1];
        if (angle2 - angle1 > MathF.PI) angle1 += 2 * MathF.PI;
        if (angle2 - angle1 < -MathF.PI) angle1 -= 2 * MathF.PI;
        float angle = NormAngle(angle1 * factor1 + angle2 * factor2);
        float deltaAngle = NormAngle(angle - Angle);
        Angle = NormAngle(Angle + avgFactor * deltaAngle);
        Vector3 start, mid, end;
        if (factor < 0.5f)
        {
            start = (waypoints[curSegment - 1] + waypoints[curSegment]) / 2;
            mid = waypoints[curSegment];
            end = (waypoints[curSegment] + waypoints[curSegment + 1]) / 2;
            factor += 0.5f;
        }
        else
        {
            start = (waypoints[curSegment] + waypoints[curSegment + 1]) / 2;
            mid = waypoints[curSegment + 1];
            end = (waypoints[curSegment + 1] + waypoints[curSegment + 2]) / 2;
            factor -= 0.5f;
        }
        var result = start + factor * (end - start) + (1 - factor) * factor * (mid - end + mid - start);
        Position = result with { Z = 0 };
    }

    public void RotateCamera(float rotations, float ms, float easeInMs, float easeOutMs)
    {
        rotateHoldFrames = 0;
        rotateTrackObject = false;
        if (ms < 1) ms = 1;
        rotateFrames = Math.Max(1, (int)(ms / FrameMs));
        rotateCurFrame = 0;
        doingRotate = true;
        rotateStart = Angle;
        rotateEnd = Angle + 2 * MathF.PI * rotations;
        rotateEase = new ParabolicEase(easeInMs / ms, easeOutMs / ms);
        doingMove = false;
        arrivedAtWaypoint = false;
    }

    public void RotateCameraTowardObject(uint id, float ms, float holdMs, float easeInMs, float easeOutMs)
    {
        rotateTrackObject = true;
        rotateHoldFrames = Math.Max(0, (int)(Math.Max(0, holdMs) / FrameMs));
        if (ms < 1) ms = 1;
        rotateFrames = Math.Max(1, (int)(ms / FrameMs));
        rotateCurFrame = 0;
        doingRotate = true;
        rotateTargetId = id;
        rotateEase = new ParabolicEase(easeInMs / ms, easeOutMs / ms);
        doingMove = false;
        arrivedAtWaypoint = false;
    }

    public void RotateCameraTowardPosition(Vector3 loc, float ms, float easeInMs, float easeOutMs, bool reverse)
    {
        rotateHoldFrames = 0;
        rotateTrackObject = false;
        if (ms < 1) ms = 1;
        rotateFrames = Math.Max(1, (int)(ms / FrameMs));
        float angle = AngleToward(Position, loc, out bool valid);
        if (!valid)
            return;
        if (reverse)
            angle += Angle < angle ? -2 * MathF.PI : 2 * MathF.PI;
        rotateCurFrame = 0;
        doingRotate = true;
        rotateStart = Angle;
        rotateEnd = angle;
        rotateEase = new ParabolicEase(easeInMs / ms, easeOutMs / ms);
        doingMove = false;
        arrivedAtWaypoint = false;
    }

    public void ZoomCamera(float finalZoom, float ms, float easeInMs, float easeOutMs)
    {
        if (ms < 1) ms = 1;
        zoomFrames = Math.Max(1, (int)(ms / FrameMs));
        zoomCurFrame = 0;
        doingZoom = true;
        zoomStart = Zoom;
        zoomEnd = finalZoom;
        zoomEase = new ParabolicEase(easeInMs / ms, easeOutMs / ms);
    }

    public void PitchCamera(float finalPitch, float ms, float easeInMs, float easeOutMs)
    {
        if (ms < 1) ms = 1;
        pitchFrames = Math.Max(1, (int)(ms / FrameMs));
        pitchCurFrame = 0;
        doingPitch = true;
        pitchStart = FxPitch;
        pitchEnd = finalPitch;
        pitchEase = new ParabolicEase(easeInMs / ms, easeOutMs / ms);
    }

    float RemainingMs => doingRotate ? (rotateFrames + rotateHoldFrames - rotateCurFrame) * FrameMs : totalTimeMs - elapsedMs;

    public void CameraModFinalZoom(float finalZoom, float easeIn, float easeOut)
    {
        if (doingRotate)
        {
            float time = RemainingMs;
            ZoomCamera(finalZoom * MaxZoomAt(Position), time, time * easeIn, time * easeOut);
        }
        if (doingMove)
        {
            float time = totalTimeMs - elapsedMs;
            ZoomCamera(finalZoom * MaxZoomAt(waypoints[numWaypoints]), time, time * easeIn, time * easeOut);
        }
    }

    public void CameraModFinalPitch(float finalPitch, float easeIn, float easeOut)
    {
        if (doingRotate)
        {
            float time = (rotateFrames + rotateHoldFrames - rotateCurFrame) * FrameMs;
            PitchCamera(finalPitch, time, time * easeIn, time * easeOut);
        }
        if (doingMove)
        {
            float time = totalTimeMs - elapsedMs;
            PitchCamera(finalPitch, time, time * easeIn, time * easeOut);
        }
    }

    public void CameraModFreezeAngle()
    {
        if (doingRotate)
        {
            if (rotateTrackObject)
                rotateTargetId = 0;
            else
                rotateStart = rotateEnd = Angle;
        }
        if (doingMove)
            for (int i = 0; i < numWaypoints; i++)
                cameraAngle[i + 1] = cameraAngle[0];
    }

    Vector3 SmoothedPoint(int i)
    {
        var start = (waypoints[i - 1] + waypoints[i]) / 2;
        var mid = waypoints[i];
        var end = (waypoints[i] + waypoints[i + 1]) / 2;
        const float factor = 0.5f;
        return (start + factor * (end - start) + (1 - factor) * factor * (mid - end + mid - start)) with { Z = 0 };
    }

    public void CameraModLookToward(Vector3 loc)
    {
        if (doingRotate || !doingMove)
            return;
        for (int i = 2; i <= numWaypoints; i++)
        {
            float angle = AngleToward(SmoothedPoint(i), loc, out bool valid);
            if (valid)
                cameraAngle[i] = angle;
        }
        if (totalTimeMs <= 1)
        {
            MoveAlongWaypointPath(1);
            doingMove = true;
            arrivedAtWaypoint = false;
        }
    }

    public void CameraModFinalLookToward(Vector3 loc)
    {
        if (doingRotate || !doingMove)
            return;
        for (int i = Math.Max(2, numWaypoints - 1); i <= numWaypoints; i++)
        {
            float angle = AngleToward(SmoothedPoint(i), loc, out bool valid);
            if (!valid)
                continue;
            if (i == numWaypoints)
                cameraAngle[i] = angle;
            else
                cameraAngle[i] = NormAngle(cameraAngle[i] + NormAngle(angle - cameraAngle[i]) / 2);
        }
    }

    public void CameraModFinalMoveTo(Vector3 loc)
    {
        if (doingRotate || !doingMove)
            return;
        var delta = (loc - waypoints[numWaypoints]) with { Z = 0 };
        for (int i = 2; i <= numWaypoints; i++)
            waypoints[i] += delta;
    }

    public void CameraModRollingAverage(int frames) => rollingAverageFrames = Math.Max(1, frames);

    /// <summary>RESET_CAMERA: back to the normal angle, height and pitch at a spot.</summary>
    public void ResetCamera(Vector3 location, float ms, float easeInMs, float easeOutMs)
    {
        MoveCameraTo(location, ms, 0, false, easeInMs, easeOutMs);
        cameraAngle[2] = 0;
        Angle = cameraAngle[0];
        ZoomCamera(MaxZoomAt(location), ms, easeInMs, easeOutMs);
        PitchCamera(1, ms, easeInMs, easeOutMs);
    }

    /// <summary>Advances the moves by real time and writes the result to the camera.</summary>
    public void Update(float seconds)
    {
        if (!IsBusy)
        {
            Sync();
            return;
        }
        carriedMs += seconds * 1000;
        while (carriedMs >= FrameMs)
        {
            carriedMs -= FrameMs;
            if (doingMove)
                MoveAlongWaypointPath(FrameMs);
            if (doingRotate)
                RotateOneFrame();
            if (doingZoom)
                ZoomOneFrame();
            if (doingPitch)
                PitchOneFrame();
        }
        if (LockedObjectId != 0)
        {
            if (game.FindObject(LockedObjectId) is { } obj)
            {
                var target = obj.Position with { Z = 0 };
                if (SnapToLock || TetherDistance <= 0)
                    Position = target;
                else
                {
                    var offset = Position - target;
                    if (offset.Length() > TetherDistance)
                        Position = target + Vector3.Normalize(offset) * TetherDistance;
                }
                SnapToLock = false;
            }
            else
                LockedObjectId = 0;
        }
        Apply();
    }

    void Apply()
    {
        camera.Target = Position with { Z = game.Terrain.GroundHeight(Position) };
        camera.Yaw = Angle - MathF.PI / 2;
        camera.Distance = defaultDistance * Zoom;
        camera.Pitch = Math.Clamp(defaultPitch * FxPitch, 0.05f, 1.55f);
    }

    void RotateOneFrame()
    {
        rotateCurFrame++;
        if (rotateTrackObject)
        {
            if (rotateCurFrame <= rotateFrames + rotateHoldFrames)
            {
                if (game.FindObject(rotateTargetId) is { } obj)
                    rotateTargetPos = obj.Position;
                float angle = AngleToward(Position, rotateTargetPos, out bool valid);
                if (valid)
                {
                    if (rotateCurFrame <= rotateFrames)
                    {
                        float factor = rotateEase.Apply((float)rotateCurFrame / rotateFrames);
                        Angle = NormAngle(Angle + NormAngle(angle - Angle) * factor);
                    }
                    else
                        Angle = angle;
                }
            }
        }
        else if (rotateCurFrame <= rotateFrames)
        {
            float factor = rotateEase.Apply((float)rotateCurFrame / rotateFrames);
            Angle = NormAngle(rotateStart + (rotateEnd - rotateStart) * factor);
        }
        if (rotateCurFrame >= rotateFrames + rotateHoldFrames)
        {
            doingRotate = false;
            if (!rotateTrackObject)
                Angle = NormAngle(rotateEnd);
        }
    }

    void ZoomOneFrame()
    {
        zoomCurFrame++;
        if (zoomCurFrame <= zoomFrames)
            Zoom = zoomStart + (zoomEnd - zoomStart) * zoomEase.Apply((float)zoomCurFrame / zoomFrames);
        if (zoomCurFrame >= zoomFrames)
        {
            doingZoom = false;
            Zoom = zoomEnd;
        }
    }

    void PitchOneFrame()
    {
        pitchCurFrame++;
        if (pitchCurFrame <= pitchFrames)
            FxPitch = pitchStart + (pitchEnd - pitchStart) * pitchEase.Apply((float)pitchCurFrame / pitchFrames);
        if (pitchCurFrame >= pitchFrames)
        {
            doingPitch = false;
            FxPitch = pitchEnd;
        }
    }
}

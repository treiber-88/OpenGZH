using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// The Ambulance's cleanup: every ScanRate it looks for the nearest CLEANUP_HAZARD object (toxin
/// puddles, radiation) within ScanRange and shoots it with the weapon in WeaponSlot. A
/// CleanupAreaPower order sends it to a point and widens the search around that point.
/// </summary>
public sealed class CleanupHazardUpdate : Module, IUpdateModule, ICreateModule
{
    WeaponSlot slot;
    int scanFrames;
    float scanRange;

    uint bestTargetId;
    int nextScanFrames;
    bool inRange;
    float moveRange;
    Vector3 cleanupPosition;
    float fireRange;

    protected override void Initialize()
    {
        slot = Setting("WeaponSlot").ToUpperInvariant() switch
        {
            "SECONDARY" => WeaponSlot.Secondary,
            "TERTIARY" => WeaponSlot.Tertiary,
            _ => WeaponSlot.Primary,
        };
        scanFrames = FramesSetting("ScanRate");
        scanRange = FloatSetting("ScanRange");
    }

    public void OnCreate()
    {
        // As in the original, the cleanup weapon lives in the VETERAN weapon set.
        Owner.SetWeaponSetFlag("VETERAN");
        fireRange = Owner.Weapons.Slots[(int)slot]?.Template.AttackRange ?? 0;
        if (scanRange <= fireRange)
            Log.Warning($"CleanupHazardUpdate on {Owner.Template.Name}: ScanRange ({scanRange}) should be larger than the weapon range ({fireRange}).");
    }

    public void SetCleanupArea(Vector3 position, float range)
    {
        moveRange = range;
        cleanupPosition = position;
        Owner.AI?.MoveTo(position, CommandSource.AI);
    }

    public int Update()
    {
        var ai = Owner.AI;
        if (moveRange > 0 && ai != null)
        {
            if (ai.IsIdle)
                ai.SetBusy(CommandSource.AI);
            else if (ai.LastCommandSource != CommandSource.AI)
            {
                // The player gave a new order: forget the cleanup area.
                moveRange = 0;
                return 0;
            }
        }

        if (nextScanFrames > 0)
        {
            nextScanFrames--;
            FireWhenReady();
            return 0;
        }
        nextScanFrames = scanFrames;
        if (ScanClosestTarget() != null)
            FireWhenReady();
        else if (moveRange > 0 && ai != null && (ai.IsIdle || ai.IsBusy))
        {
            if (MathUtil.Distance2D(Owner.Position, cleanupPosition) < 25)
                moveRange = 0;
            else
                ai.MoveTo(cleanupPosition, CommandSource.AI);
        }
        return 0;
    }

    void FireWhenReady()
    {
        var target = Game.FindObject(bestTargetId);
        if (target != null && moveRange == 0)
        {
            if (MathUtil.Distance2D(Owner.Position, target.Position) < fireRange)
                inRange = true;
            else if (inRange)
            {
                // The target drifted out of range: look again soon.
                nextScanFrames = Game.Random.Int(0, 3);
                bestTargetId = 0;
                if (nextScanFrames == 0)
                {
                    ScanClosestTarget();
                    nextScanFrames = scanFrames;
                    target = null;
                }
            }
            else
                inRange = false;
        }

        if (target != null && Owner.AI is { } ai && (ai.IsIdle || ai.IsBusy))
        {
            Owner.Weapons.LockSlot(slot);
            ai.AttackObject(target, CommandSource.AI);
        }
    }

    GameObject? ScanClosestTarget()
    {
        bestTargetId = 0;
        var center = moveRange > 0 ? cleanupPosition : Owner.Position;
        float radius = moveRange > 0 ? scanRange + moveRange : scanRange;
        var best = Game.Partition.Query(center, radius)
            .Where(o => o.IsKindOf("CLEANUP_HAZARD") && !o.IsEffectivelyDead)
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, center))
            .FirstOrDefault();
        if (best != null)
            bestTargetId = best.Id;
        return best;
    }
}

/// <summary>
/// Spy vision: while active, the owner sees what enemy units of SpyOnKindof kinds see. Activated by
/// SpyVisionSpecialPower, by an upgrade (NeedsUpgrade), or on its own (SelfPowered: on for
/// SelfPoweredDuration every SelfPoweredInterval). Switches off while the object is disabled.
/// </summary>
public sealed class SpyVisionUpdate : UpgradeModule, IUpdateModule, IOwnershipModule, IDisabledEdgeModule
{
    const int Forever = int.MaxValue;

    int deactivateFrame;
    int disabledUntilFrame;
    bool currentlyActive;
    bool resetTimersNextUpdate = true;
    bool selfPowered;
    int selfPoweredDuration, selfPoweredInterval;
    string spyOnKindOf = "";

    protected override void InitializeUpgrade()
    {
        selfPowered = BoolSetting("SelfPowered");
        selfPoweredDuration = FramesSetting("SelfPoweredDuration");
        selfPoweredInterval = FramesSetting("SelfPoweredInterval");
        spyOnKindOf = Setting("SpyOnKindof");
    }

    /// <summary>A saboteur knocks it out until this frame.</summary>
    public void SetDisabledUntilFrame(int frame)
    {
        disabledUntilFrame = frame;
        SetActive(Owner.Owner, false);
        Owner.WakeUpdate(this);
    }

    public void Activate(int durationFrames)
    {
        deactivateFrame = durationFrames == 0 ? Forever : Frame + durationFrames;
        SetActive(Owner.Owner, true);
        Owner.WakeUpdate(this);
    }

    protected override void OnUpgrade()
    {
        if (BoolSetting("NeedsUpgrade"))
            Activate(selfPoweredDuration); // zero = on for good
    }

    public int Update()
    {
        if (Frame < disabledUntilFrame)
            return disabledUntilFrame == Forever ? LogicTime.SleepForever : disabledUntilFrame - Frame - 1;
        if (resetTimersNextUpdate)
        {
            resetTimersNextUpdate = false;
            if (selfPowered)
            {
                if (selfPoweredInterval == 0)
                {
                    SetActive(Owner.Owner, true);
                    return LogicTime.SleepForever;
                }
                return selfPoweredInterval;
            }
        }

        if (currentlyActive && deactivateFrame <= Frame)
        {
            SetActive(Owner.Owner, false);
            deactivateFrame = 0;
        }
        else if (!currentlyActive && selfPowered)
        {
            SetActive(Owner.Owner, true);
            deactivateFrame = selfPoweredDuration == 0 ? Forever : Frame + selfPoweredDuration;
        }

        if (selfPowered)
            return currentlyActive ? selfPoweredDuration : selfPoweredInterval;
        if (currentlyActive && deactivateFrame != Forever)
            return Math.Max(0, deactivateFrame - Frame - 1);
        return LogicTime.SleepForever;
    }

    void SetActive(Player spy, bool on)
    {
        foreach (var player in Game.Players.All)
            if (spy.IsEnemyOf(player))
                player.SetUnitsVisionSpied(on, spyOnKindOf, spy.Index);
        currentlyActive = on;
    }

    public void OnDisabledEdge(bool nowDisabled)
    {
        if (nowDisabled)
        {
            if (currentlyActive)
                SetActive(Owner.Owner, false);
            disabledUntilFrame = Forever;
        }
        else
        {
            disabledUntilFrame = Frame;
            resetTimersNextUpdate = true;
            Owner.WakeUpdate(this);
        }
    }

    public void OnCapture(Player oldOwner, Player newOwner)
    {
        if (!currentlyActive)
            return;
        SetActive(oldOwner, false);
        SetActive(newOwner, true);
    }

    public void OnDelete()
    {
        if (currentlyActive)
            SetActive(Owner.Owner, false);
    }
}

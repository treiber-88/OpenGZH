using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// China's nuclear reactor overcharge (the original's OverchargeBehavior): toggled from its button,
/// it adds the plant's EnergyBonus and extends the rods, but drains HealthPercentToDrainPerSecond;
/// it switches itself off below NotAllowedWhenHealthBelowPercent.
/// </summary>
public sealed class OverchargeBehavior : Module, IUpdateModule, IOwnershipModule, ICreateModule
{
    public bool IsOverchargeActive { get; private set; }

    public void OnCreate() => Owner.SleepUpdate(this);

    public int Update()
    {
        if (!IsOverchargeActive)
            return LogicTime.SleepForever;
        var us = Owner;
        if (us.Body is not { } body)
            return 0;
        us.Damage(new DamageInfo
        {
            Amount = body.MaxHealth * FloatSetting("HealthPercentToDrainPerSecond") / LogicTime.FramesPerSecond,
            SourceId = us.Id,
            DamageType = "PENALTY",
            DeathType = "NORMAL",
        });
        if (body.Health < body.MaxHealth * FloatSetting("NotAllowedWhenHealthBelowPercent"))
        {
            Enable(false);
            if (us.Owner.IsLocalPlayer)
            {
                Game.Events.Message("GUI:OverchargeExhausted", us.Owner);
                Game.Events.RadarEvent("INFORMATION", us.Position, us.Owner);
            }
        }
        return 0;
    }

    public void Toggle() => Enable(!IsOverchargeActive);

    public void Enable(bool enable)
    {
        var us = Owner;
        if (!enable)
        {
            if (!IsOverchargeActive)
                return;
            us.FindModule<Upgrades.PowerPlantUpdate>()?.ExtendRods(false);
            us.Owner.Energy.AdjustBonus(us.Template.EnergyBonus, adding: false);
            IsOverchargeActive = false;
            us.SleepUpdate(this);
        }
        else
        {
            if (IsOverchargeActive)
                return;
            us.FindModule<Upgrades.PowerPlantUpdate>()?.ExtendRods(true);
            us.Owner.Energy.AdjustBonus(us.Template.EnergyBonus, adding: true);
            IsOverchargeActive = true;
            us.WakeUpdate(this);
        }
    }

    public void OnDelete()
    {
        if (!IsOverchargeActive)
            return;
        Owner.Owner.Energy.AdjustBonus(Owner.Template.EnergyBonus, adding: false);
        IsOverchargeActive = false;
    }

    public void OnCapture(Player oldOwner, Player newOwner)
    {
        if (!IsOverchargeActive || Owner.IsDisabled)
            return;
        oldOwner.Energy.AdjustBonus(Owner.Template.EnergyBonus, adding: false);
        newOwner.Energy.AdjustBonus(Owner.Template.EnergyBonus, adding: true);
    }
}

/// <summary>
/// A computer player's pilot (from a destroyed veteran vehicle) climbs into the nearest own vehicle
/// above MinHealth that it can upgrade, scanning every ScanRate; with none around it walks back to
/// base once (the original's PilotFindVehicleUpdate). Human players' pilots wait for orders.
/// </summary>
public sealed class PilotFindVehicleUpdate : Module, IUpdateModule
{
    bool didMoveToBase;

    public int Update()
    {
        var obj = Owner;
        if (obj.Owner.IsHuman)
            return LogicTime.SleepForever;
        if (obj.AI is not { } ai)
            return LogicTime.SleepForever;
        int scanFrames = FramesSetting("ScanRate");
        if (!ai.IsIdle)
            return scanFrames;
        if (ScanClosestTarget() is { } upgradeUnit)
        {
            ai.Enter(upgradeUnit, CommandSource.AI);
            didMoveToBase = false;
        }
        else if (!didMoveToBase && obj.Owner.AI is { } aiPlayer && aiPlayer.GetBaseCenter(out var basePos))
        {
            ai.MoveTo(basePos, CommandSource.AI);
            didMoveToBase = true;
        }
        return scanFrames;
    }

    GameObject? ScanClosestTarget()
    {
        var me = Owner;
        float minHealth = FloatSetting("MinHealth", 0.5f);
        var collides = me.FindModules<Collide.CrateCollide>().ToList();
        foreach (var other in Game.ObjectsInRadius(me.Position, FloatSetting("ScanRange"))
                     .Where(o => o.IsKindOf("VEHICLE") && !o.IsEffectivelyDead && o.Owner == me.Owner && o.IsOffMap == me.IsOffMap)
                     .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, me.Position)))
        {
            if (other.Body is not { } body || body.Health < body.MaxHealth * minHealth)
                continue;
            if (collides.Any(c => c.IsValidToExecute(other)))
                return other;
        }
        return null;
    }
}

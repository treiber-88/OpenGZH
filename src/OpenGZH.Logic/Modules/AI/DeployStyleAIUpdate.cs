using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// Units that must set up before they can fire and pack up before they can move (Nuke Cannon,
/// Inferno Cannon, Tomahawk...). Once a target is in range (or while guarding) it unpacks for
/// UnpackTime, and fires only when DEPLOYED; ordered to move, it packs up for PackTime first
/// (re-centering its turret first if TurretsMustCenterBeforePacking). An order that changes its
/// mind halfway through reverses the animation from where it is.
/// </summary>
public sealed class DeployStyleAIUpdate : AIUpdateInterface
{
    enum DeployState { ReadyToMove, Deploy, ReadyToAttack, Undeploy, AligningTurrets }

    int unpackFrames, packFrames;
    bool turretsOnlyWhenDeployed, turretsMustCenter, manualAnimations;

    DeployState state = DeployState.ReadyToMove;
    int frameToWaitForDeploy;

    /// <summary>For ManualDeployAnimations: how far through its pack/unpack animation the model is.</summary>
    public int ManualAnimationFrame { get; private set; }

    protected override void Initialize()
    {
        base.Initialize();
        unpackFrames = FramesSetting("UnpackTime");
        packFrames = FramesSetting("PackTime");
        turretsOnlyWhenDeployed = BoolSetting("TurretsFunctionOnlyWhenDeployed");
        turretsMustCenter = BoolSetting("TurretsMustCenterBeforePacking");
        manualAnimations = BoolSetting("ManualDeployAnimations");
    }

    public override int Update()
    {
        int now = Frame;
        var weapon = Owner.Weapons.Current;
        bool tryingToMove = IsTryingToMove;
        bool inRange = false;
        if (IsInAttackState && weapon != null)
        {
            if (CurrentVictim is { } victim)
                inRange = weapon.IsInRange(victim);
            else if (CurrentVictimPosition is { } position)
                inRange = weapon.IsInRange(position);
        }

        if (frameToWaitForDeploy != 0 && now >= frameToWaitForDeploy)
        {
            if (state == DeployState.Deploy)
                SetState(DeployState.ReadyToAttack);
            else if (state == DeployState.Undeploy)
                SetState(DeployState.ReadyToMove);
        }

        if (inRange || IsInGuardIdleState)
        {
            switch (state)
            {
                case DeployState.ReadyToMove:
                    SetState(DeployState.Deploy);
                    break;
                case DeployState.Undeploy when frameToWaitForDeploy != 0:
                    SetState(DeployState.Deploy, reverse: true);
                    break;
                case DeployState.AligningTurrets:
                    SetState(DeployState.ReadyToAttack);
                    break;
            }
        }
        else if (tryingToMove)
        {
            switch (state)
            {
                case DeployState.ReadyToAttack:
                    if (TurretForCurrentWeapon != null && turretsMustCenter)
                        SetState(DeployState.AligningTurrets);
                    else
                        SetState(DeployState.Undeploy);
                    break;
                case DeployState.Deploy when frameToWaitForDeploy != 0:
                    SetState(DeployState.Undeploy, reverse: true);
                    break;
                case DeployState.AligningTurrets:
                    if (TurretForCurrentWeapon is { IsInNaturalPosition: true })
                        SetState(DeployState.Undeploy);
                    break;
            }
        }

        switch (state)
        {
            case DeployState.ReadyToMove:
                if (tryingToMove)
                    Owner.SetCondition("MOVING");
                break;
            case DeployState.Deploy:
                if (manualAnimations)
                    ManualAnimationFrame = unpackFrames - (frameToWaitForDeploy - now);
                HoldBusyThisFrame();
                break;
            case DeployState.Undeploy:
                if (manualAnimations)
                    ManualAnimationFrame = frameToWaitForDeploy - now;
                HoldBusyThisFrame();
                break;
            case DeployState.AligningTurrets:
                HoldBusyThisFrame();
                break;
        }

        base.Update();
        return 0;
    }

    void SetState(DeployState next, bool reverse = false)
    {
        state = next;
        int now = Frame;
        switch (next)
        {
            case DeployState.Deploy:
                Owner.SetCondition("PACKING", false);
                Owner.SetCondition("UNPACKING");
                frameToWaitForDeploy = reverse ? now + unpackFrames - (frameToWaitForDeploy - now) : now + unpackFrames;
                PlayUnitSound("Deploy");
                break;

            case DeployState.Undeploy:
                Owner.SetStatus(Status.Deployed, false);
                Owner.SetCondition("UNPACKING", false);
                Owner.SetCondition("DEPLOYED", false);
                Owner.SetCondition("PACKING");
                // As in the original, reversing uses the unpack time for both directions.
                frameToWaitForDeploy = reverse ? now + unpackFrames - (frameToWaitForDeploy - now) : now + packFrames;
                if (turretsOnlyWhenDeployed && TurretForCurrentWeapon is { } packing)
                    packing.Enabled = false;
                PlayUnitSound("Undeploy");
                break;

            case DeployState.ReadyToMove:
                frameToWaitForDeploy = 0;
                Owner.SetCondition("PACKING", false);
                break;

            case DeployState.ReadyToAttack:
                Owner.SetStatus(Status.Deployed);
                frameToWaitForDeploy = 0;
                Owner.SetCondition("UNPACKING", false);
                Owner.SetCondition("DEPLOYED");
                if (turretsOnlyWhenDeployed && TurretForCurrentWeapon is { } deployed)
                    deployed.Enabled = true;
                break;

            case DeployState.AligningTurrets:
                frameToWaitForDeploy = 0;
                TurretForCurrentWeapon?.Recenter();
                break;
        }
    }

    void PlayUnitSound(string name)
    {
        if (Owner.Template.UnitSound(name) is { Length: > 0 } sound)
            Game.Events.PlaySound(sound, Owner.Position, source: Owner);
    }
}

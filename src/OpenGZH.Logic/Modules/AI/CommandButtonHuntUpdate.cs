using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.SpecialPowers;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// Hunts with an ability (the original's CommandButtonHuntUpdate, used by TEAM_HUNT_WITH_COMMAND_BUTTON):
/// a computer unit keeps looking for the best target for one of its buttons (Black Lotus hacks,
/// Hijacker, Jarmen Kell's sniping) and uses it whenever it is idle. Any other order ends the hunt.
/// </summary>
public sealed class CommandButtonHuntUpdate : Module, IUpdateModule
{
    CommandButton? commandButton;
    string commandButtonName = "";

    int ScanFrames => FramesSetting("ScanRate", 1000f);
    float ScanRange => FloatSetting("ScanRange", 9999f);

    public void SetCommandButton(string buttonName)
    {
        commandButtonName = buttonName;
        commandButton = Owner.CommandSetButtons().FirstOrDefault(b => b.Name.Length > 0 && b.Name == buttonName);
        if (commandButton == null || Owner.AI is not { } ai)
            return;
        ai.Stop(CommandSource.AI);
        Update();
        Owner.WakeUpdate(this);
    }

    public int Update()
    {
        var ai = Owner.AI;
        if (ai == null || commandButton == null)
            return LogicTime.SleepForever;
        if (ai.LastCommandSource != CommandSource.AI)
        {
            commandButton = null;
            commandButtonName = "";
            return LogicTime.SleepForever;
        }
        return commandButton.CommandType switch
        {
            "SPECIAL_POWER" => HuntSpecialPower(ai),
            "SWITCH_WEAPON" or "FIRE_WEAPON" => HuntWeapon(ai),
            "HIJACK_VEHICLE" or "CONVERT_TO_CARBOMB" or "SABOTAGE_BUILDING" => HuntEnter(ai),
            _ => LogicTime.SleepForever,
        };
    }

    int HuntWeapon(AIUpdateInterface ai)
    {
        if (ai.IsIdle)
            ai.Hunt(CommandSource.AI);
        Owner.Weapons.LockSlot(commandButton!.Slot);
        return 0;
    }

    int HuntSpecialPower(AIUpdateInterface ai)
    {
        if (!ai.IsIdle)
            return ScanFrames;
        if (Game.Catalog.SpecialPower(commandButton!.SpecialPower) is { } power)
        {
            if (SpecialPowerRules.AbilityOfType(Owner, power.Type) is not { } ability)
                return LogicTime.SleepForever;
            if (ability.IsActive)
                return ScanFrames;
        }
        if (ScanClosestTarget() is { } victim)
            Owner.DoCommandButtonAtObject(commandButton, victim, CommandSource.AI);
        return ScanFrames;
    }

    int HuntEnter(AIUpdateInterface ai)
    {
        if (!ai.IsIdle)
            return ScanFrames;
        if (ScanClosestTarget() is { } victim)
            Owner.DoCommandButtonAtObject(commandButton!, victim, CommandSource.AI);
        return ScanFrames;
    }

    /// <summary>The best target in range: by attack priority (or nearness) for powers, the nearest valid one for entering.</summary>
    GameObject? ScanClosestTarget()
    {
        var me = Owner;
        bool isEnter = false;
        bool neutralTargets = false;
        switch (commandButton!.CommandType)
        {
            case "CONVERT_TO_CARBOMB":
                neutralTargets = true;
                isEnter = true;
                break;
            case "HIJACK_VEHICLE":
            case "SABOTAGE_BUILDING":
                isEnter = true;
                break;
        }
        var power = Game.Catalog.SpecialPower(commandButton.SpecialPower);
        bool relationshipFilter = true;
        bool isBlackLotusVehicleHack = false, isCaptureBuilding = false;
        if (!isEnter)
        {
            if (power == null)
                return null;
            isBlackLotusVehicleHack = power.IsType("SPECIAL_BLACKLOTUS_DISABLE_VEHICLE_HACK");
            isCaptureBuilding = power.IsType("SPECIAL_INFANTRY_CAPTURE_BUILDING");
            if (isCaptureBuilding)
                relationshipFilter = false;
        }
        var wanted = neutralTargets ? Relationship.Neutral : Relationship.Enemies;
        float range = ScanRange;
        var candidates = Game.ObjectsInRadius(me.Position, range)
            .Where(o => o != me && !o.IsEffectivelyDead && o.IsOffMap == me.IsOffMap && !Logic.AI.Targeting.IsStealthedAndUndetectedFor(me, o)
                && (!relationshipFilter || me.RelationshipTo(o) == wanted))
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, me.Position)).ToList();

        if (power != null && SpecialPowerRules.ModuleFor(me, power) != null)
        {
            var info = me.AI?.AttackInfo;
            float modifierDistance = Math.Max(1f, Game.AIData.AttackPriorityDistanceModifier);
            GameObject? best = null;
            int effectivePriority = 0, actualPriority = 0;
            foreach (var other in candidates)
            {
                if (other.IsDisabled && isBlackLotusVehicleHack)
                    continue;
                if (isCaptureBuilding && (me.Owner == other.Owner || me.RelationshipTo(other) == Relationship.Allies))
                    continue;
                if (!SpecialPowerRules.CanDoSpecialPowerAtObject(me, other, CommandSource.AI, power, checkSource: false))
                    continue;
                float dist = MathF.Max(0, MathUtil.Distance2D(me.Position, other.Position) - me.BoundingSphereRadius - other.BoundingSphereRadius);
                int curPriority = (int)(range - dist);
                if (info != null)
                    curPriority = info.GetPriority(other.Template);
                if (curPriority == 0)
                    continue;
                int modPriority = Math.Max(1, curPriority - (int)(dist / modifierDistance));
                if (modPriority > effectivePriority || (modPriority == effectivePriority && curPriority > actualPriority))
                {
                    effectivePriority = modPriority;
                    actualPriority = curPriority;
                    best = other;
                }
            }
            return best;
        }
        if (isEnter)
        {
            var collides = me.FindModules<Collide.CrateCollide>().ToList();
            return candidates.FirstOrDefault(other => collides.Any(c => c.IsValidToExecute(other)));
        }
        return null;
    }
}

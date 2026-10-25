using OpenGZH.TiberianDawn.Logic.Objects;

namespace OpenGZH.TiberianDawn.Logic;

/// <summary>
/// SuperClass (SUPER.CPP): a special weapon's charge — present or not, recharging, suspended
/// (no power), ready — and its EVA lines.
/// </summary>
public sealed class SuperClass
{
    public const int ANIMATION_STAGES = 102;

    readonly TdGame game;
    bool isPresent, isOneTime, isReady, isSuspended;
    readonly CountDownTimer control;
    int oldStage = -1;
    int suspendTime;
    readonly VoxType voxRecharge, voxCharging, voxImpatient, voxSuspend;
    readonly int rechargeTime;

    public SuperClass(TdGame game, int recharge, VoxType ready, VoxType charging, VoxType impatient, VoxType suspend)
    {
        this.game = game;
        control = new CountDownTimer(game);
        control.Set(0);
        rechargeTime = recharge;
        voxRecharge = ready;
        voxCharging = charging;
        voxImpatient = impatient;
        voxSuspend = suspend;
    }

    public bool Is_Ready() => isReady;
    public bool Is_Present() => isPresent;
    public bool Is_One_Time() => isOneTime && isPresent;
    public int Get_Recharge_Time() => rechargeTime;

    public bool Suspend(bool on)
    {
        if (isPresent && !isReady && !isOneTime && on != isSuspended)
        {
            if (on)
                suspendTime = (int)control.Time;
            else
                control.Set(suspendTime);
            isSuspended = on;
            return true;
        }
        return false;
    }

    public bool Enable(bool onetime = false, bool player = false, bool quiet = false)
    {
        if (!isPresent)
        {
            isPresent = true;
            isOneTime = onetime;
            bool retval = Recharge(player && !quiet);
            if (quiet)
                Suspend(true);
            return retval;
        }
        return false;
    }

    public bool Remove(bool forced = false)
    {
        if (isPresent && (!isOneTime || forced))
        {
            isReady = false;
            isPresent = false;
            return true;
        }
        return false;
    }

    bool Recharge(bool player)
    {
        if (isPresent && !isReady)
        {
            isSuspended = false;
            oldStage = -1;
            control.Set(rechargeTime);
            if (player && voxCharging != VoxType.VOX_NONE)
                game.Speak(voxCharging);
            return true;
        }
        return false;
    }

    public bool Discharged(bool player)
    {
        if (!isSuspended && isPresent && isReady)
        {
            isReady = false;
            if (isOneTime)
            {
                isOneTime = false;
                return Remove();
            }
            Recharge(player);
        }
        return false;
    }

    /// <summary>AI: true when the sidebar icon needs redrawing (charged up, or the gauge moved).</summary>
    public bool AI(bool player = false)
    {
        if (isPresent && !isReady)
        {
            if (isSuspended)
            {
                if (oldStage != -1)
                {
                    oldStage = -1;
                    return true;
                }
            }
            else if (control.Expired)
            {
                isReady = true;
                if (player && voxRecharge != VoxType.VOX_NONE)
                    game.Speak(voxRecharge);
                return true;
            }
            else if (Anim_Stage() != oldStage)
            {
                oldStage = Anim_Stage();
                return true;
            }
        }
        return false;
    }

    public int Anim_Stage()
    {
        if (!isPresent)
            return 0;
        if (isReady)
            return ANIMATION_STAGES;
        int time = isSuspended ? suspendTime : (int)control.Time;
        return (int)TdMath.FixedToCardinal(ANIMATION_STAGES, TdMath.CardinalToFixed((uint)rechargeTime, (uint)(rechargeTime - time)));
    }

    public void Impatient_Click()
    {
        if (isSuspended)
        {
            if (voxSuspend != VoxType.VOX_NONE)
                game.Speak(voxSuspend);
        }
        else if (voxImpatient != VoxType.VOX_NONE)
        {
            game.Speak(voxImpatient);
        }
    }

    public void Forced_Charge(bool player = false)
    {
        if (isPresent)
        {
            isReady = true;
            isSuspended = false;
            if (player && voxRecharge != VoxType.VOX_NONE)
                game.Speak(voxRecharge);
        }
    }
}

/// <summary>UnitTrackerClass: per-type totals kept for the internet game statistics.</summary>
public sealed class UnitTrackerClass(int count)
{
    public readonly int[] Totals = new int[Math.Max(count, 1)];

    public void Increment_Unit_Total(int type)
    {
        if ((uint)type < (uint)Totals.Length)
            Totals[type]++;
    }

    public void Decrement_Unit_Total(int type)
    {
        if ((uint)type < (uint)Totals.Length)
            Totals[type]--;
    }
}

/// <summary>RegionClass (REGION.H): one 4x4-cell region's threat value as a house sees it.</summary>
public struct RegionClass
{
    long threat;
    public void Reset_Threat() => threat = 0;
    public void Adjust_Threat(int value, bool neg) => threat += neg ? -value : value;
    public readonly int Threat_Value() => (int)threat;
}

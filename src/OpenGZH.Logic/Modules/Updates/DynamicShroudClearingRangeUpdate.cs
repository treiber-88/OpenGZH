namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// Grows the object's shroud-clearing range to its normal value, holds it, then shrinks it to
/// FinalVision (the original's DynamicShroudClearingRangeUpdate: the spy satellite and radar scan
/// reveals). The original also animates a ring of grid decals while growing; that is drawn by the client.
/// </summary>
public sealed class DynamicShroudClearingRangeUpdate : Module, IUpdateModule, ICreateModule
{
    enum State { NotStartedYet, Growing, Sustaining, Shrinking, DoneForever, Sleeping }

    State state = State.NotStartedYet;
    int stateCountDown, totalFrames, changeIntervalCountdown;
    // Unsigned like the original, so the countdown (signed) compares as unsigned against them.
    uint growStartDeadline, sustainDeadline, shrinkStartDeadline;
    int doneForeverFrame;
    float nativeClearingRange = 200, currentClearingRange;

    public void OnCreate()
    {
        int shrinkDelay = FramesSetting("ShrinkDelay"), shrinkTime = FramesSetting("ShrinkTime");
        int growDelay = FramesSetting("GrowDelay"), growTime = FramesSetting("GrowTime");
        stateCountDown = shrinkDelay + shrinkTime;
        totalFrames = Math.Max(1, stateCountDown);
        shrinkStartDeadline = unchecked((uint)(stateCountDown - shrinkDelay));
        growStartDeadline = unchecked((uint)(stateCountDown - growDelay));
        sustainDeadline = unchecked(growStartDeadline - (uint)growTime);
        doneForeverFrame = Frame + stateCountDown;
        nativeClearingRange = Owner.ShroudClearingRange;
    }

    /// <summary>The current range (the client's grid ring scales from it).</summary>
    public float CurrentClearingRange => currentClearingRange;
    public float RingRadius => currentClearingRange + (totalFrames - stateCountDown) * 2;
    public bool ShowsGrid => state is State.NotStartedYet or State.Growing;

    public int Update()
    {
        if (state == State.Sleeping)
            return 0;
        uint countDown = unchecked((uint)stateCountDown);
        if (stateCountDown <= 0 || Frame > doneForeverFrame)
            state = State.DoneForever;
        else if (countDown <= shrinkStartDeadline)
            state = State.Shrinking;
        else if (countDown <= sustainDeadline)
            state = State.Sustaining;
        else if (countDown <= growStartDeadline)
            state = State.Growing;

        switch (state)
        {
            case State.Growing:
                currentClearingRange += nativeClearingRange / Math.Max(1f, FramesSetting("GrowTime"));
                if (currentClearingRange >= nativeClearingRange)
                    state = State.Sustaining;
                break;
            case State.Sustaining:
                currentClearingRange = nativeClearingRange;
                break;
            case State.Shrinking:
                currentClearingRange -= (nativeClearingRange - FloatSetting("FinalVision")) / Math.Max(1f, FramesSetting("ShrinkTime"));
                break;
            case State.DoneForever:
                currentClearingRange = FloatSetting("FinalVision");
                break;
        }

        if (stateCountDown > 0)
            stateCountDown--;
        if (changeIntervalCountdown > 0)
            changeIntervalCountdown--;
        else
        {
            changeIntervalCountdown = state == State.Growing ? FramesSetting("GrowInterval") : FramesSetting("ChangeInterval");
            Owner.ShroudClearingRange = currentClearingRange;
            if (state == State.DoneForever)
                state = State.Sleeping;
        }
        return 0;
    }
}

using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// Hackers hacking the internet for money: unpack the laptop (UnpackTime), then earn cash every
/// CashUpdateDelay (CashUpdateDelayFast inside an Internet Center) - RegularCashAmount, or the
/// Veteran/Elite/Heroic amount by rank, falling back to the next rank down when that one is 0 -
/// plus XpPerCashUpdate experience. A hacker that is itself hacked earns nothing meanwhile.
/// Any new order makes it pack up (PackTime; none inside a building) and then carries out the order.
/// Pack and unpack times vary by up to PackUnpackVariationFactor.
/// </summary>
public sealed class HackInternetAIUpdate : AIUpdateInterface
{
    enum HackState { None, Unpacking, Hacking, Packing }

    int unpackFrames, packFrames, cashDelay, cashDelayFast;
    float variationFactor;
    int regularCash, veteranCash, eliteCash, heroicCash, xpPerCash;

    HackState hackState = HackState.None;
    int framesRemaining;
    Action? pendingOrder;

    protected override void Initialize()
    {
        base.Initialize();
        unpackFrames = FramesSetting("UnpackTime");
        packFrames = FramesSetting("PackTime");
        variationFactor = FloatSetting("PackUnpackVariationFactor");
        cashDelay = FramesSetting("CashUpdateDelay");
        cashDelayFast = FramesSetting("CashUpdateDelayFast");
        regularCash = IntSetting("RegularCashAmount");
        veteranCash = IntSetting("VeteranCashAmount");
        eliteCash = IntSetting("EliteCashAmount");
        heroicCash = IntSetting("HeroicCashAmount");
        xpPerCash = IntSetting("XpPerCashUpdate");
    }

    public bool IsHacking => hackState == HackState.Hacking;
    public bool IsHackingPackingOrUnpacking => hackState != HackState.None;

    public override bool IsIdle => pendingOrder == null && hackState == HackState.None && base.IsIdle;

    int PackTime => Owner.IsContained ? 0 : packFrames;
    int CashUpdateDelay => Owner.IsContained ? cashDelayFast : cashDelay;

    public override void HackInternet(CommandSource source)
    {
        // The original sets the state directly, without the order queue.
        pendingOrder = null;
        SetBusy(source);
        EnterState(HackState.Unpacking);
    }

    protected override bool OnOrder(AIOrder order, Action replay)
    {
        if (hackState is not (HackState.Hacking or HackState.Packing))
        {
            pendingOrder = null;
            return false;
        }
        pendingOrder = replay;
        if (hackState == HackState.Hacking)
            EnterState(HackState.Packing);
        return true;
    }

    public override int Update()
    {
        if (hackState == HackState.None && base.IsIdle && pendingOrder is { } order)
        {
            pendingOrder = null;
            order();
        }

        if (hackState != HackState.None)
        {
            UpdateHackState();
            if (hackState != HackState.None)
                HoldBusyThisFrame();
        }
        base.Update();
        return 0;
    }

    void EnterState(HackState next)
    {
        // Leaving the old state.
        switch (hackState)
        {
            case HackState.Unpacking: Owner.SetCondition("UNPACKING", false); break;
            case HackState.Hacking: Owner.SetCondition("FIRING_A", false); break;
            case HackState.Packing: Owner.SetCondition("PACKING", false); break;
        }

        hackState = next;
        switch (next)
        {
            case HackState.Unpacking:
                Owner.SetCondition("PACKING", false);
                Owner.SetCondition("FIRING_A", false);
                Owner.SetCondition("UNPACKING");
                PlayUnitSound("UnitUnpack");
                framesRemaining = (int)(unpackFrames * Variation());
                break;
            case HackState.Hacking:
                Owner.SetCondition("UNPACKING", false);
                Owner.SetCondition("FIRING_A");
                framesRemaining = CashUpdateDelay;
                break;
            case HackState.Packing:
                Owner.SetCondition("FIRING_A", false);
                Owner.SetCondition("PACKING");
                PlayUnitSound("UnitPack");
                framesRemaining = (int)(PackTime * Variation());
                break;
            case HackState.None:
                // Done packing: back to idle, ready for the waiting order.
                State = AIState.Idle;
                break;
        }
    }

    float Variation() => Game.Random.Float(1 - variationFactor, 1 + variationFactor);

    void UpdateHackState()
    {
        switch (hackState)
        {
            case HackState.Unpacking:
                Owner.SetCondition("UNPACKING");
                if (framesRemaining > 0)
                    framesRemaining--;
                else
                    EnterState(HackState.Hacking);
                break;

            case HackState.Packing:
                if (framesRemaining > 0)
                    framesRemaining--;
                else
                    EnterState(HackState.None);
                break;

            case HackState.Hacking:
                if (Owner.IsDisabledBy("DISABLED_HACKED"))
                    break;
                if (framesRemaining > 0)
                {
                    framesRemaining--;
                    break;
                }
                EarnCash();
                framesRemaining = CashUpdateDelay;
                break;
        }
    }

    void EarnCash()
    {
        int amount = Owner.Veterancy switch
        {
            VeterancyLevel.Heroic => First(heroicCash, eliteCash, veteranCash, regularCash),
            VeterancyLevel.Elite => First(eliteCash, veteranCash, regularCash),
            VeterancyLevel.Veteran => First(veteranCash, regularCash),
            _ => First(regularCash),
        };
        Owner.Owner.Money.Deposit(amount);
        Game.Events.MoneyEarned(Owner, amount);
        Owner.AddExperience(xpPerCash);
        PlayUnitSound("UnitCashPing");
    }

    /// <summary>The first non-zero amount, or 1 (the original's switch fall-through).</summary>
    static int First(params int[] amounts) => amounts.FirstOrDefault(a => a != 0) is var a && a != 0 ? a : 1;

    void PlayUnitSound(string name)
    {
        if (Owner.Template.UnitSound(name) is { Length: > 0 } sound)
            Game.Events.PlaySound(sound, Owner.Position, source: Owner);
    }
}

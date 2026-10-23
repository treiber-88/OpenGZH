using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// A capturable tech building (the original's TechBuildingBehavior): shows CAPTURED while a playable
/// side owns it, pulses PulseFX every PulseFXRate, and goes back to the neutral side when destroyed.
/// </summary>
public sealed class TechBuildingBehavior : Module, IUpdateModule, IDieModule, IOwnershipModule
{
    public int Update()
    {
        var us = Owner;
        bool captured = us.Owner.IsPlayableSide;
        us.SetCondition("CAPTURED", captured);
        string fx = Setting("PulseFX");
        int rate = FramesSetting("PulseFXRate");
        if (fx.Length > 0 && rate > 0 && captured)
        {
            Game.Events.PlayFx(fx, us.Position, us);
            return rate;
        }
        return LogicTime.SleepForever;
    }

    public void OnDie(DamageInfo killingBlow)
    {
        Owner.SetCondition("CAPTURED", false);
        Owner.SetTeam(Game.Players.Neutral.DefaultTeam);
    }

    public void OnCapture(Player oldOwner, Player newOwner) => Owner.WakeUpdate(this);
}

/// <summary>
/// Runs an object creation list every MinDelay..MaxDelay (the original's OCLUpdate): the reinforcement
/// pad's free vehicle, the USA drop zone's supply drop. FactionTriggered ones only run while a playable
/// side owns the object, restart their timer when the owner changes, and pick FactionOCL by side.
/// </summary>
public sealed class OCLUpdate : Module, IUpdateModule
{
    int nextCreationFrame, timerStartedFrame;
    bool isFactionNeutral = true;
    uint currentPlayerColor;

    public FlagSet DisabledTypesToProcess => DisabledMask.All;

    public int Update()
    {
        var obj = Owner;
        if (obj.IsDisabled)
        {
            nextCreationFrame++;
            return 0;
        }
        bool factionTriggered = BoolSetting("FactionTriggered");
        if (factionTriggered)
        {
            var player = obj.Owner;
            if (isFactionNeutral)
            {
                if (player.IsPlayableSide)
                {
                    currentPlayerColor = player.Color;
                    isFactionNeutral = false;
                    SetNextCreationFrame();
                }
            }
            else if (!player.IsPlayableSide)
                isFactionNeutral = true;
            else if (player.Color != currentPlayerColor)
            {
                currentPlayerColor = player.Color;
                SetNextCreationFrame();
            }
            if (isFactionNeutral)
                return 0;
        }
        if (!ShouldCreate())
            return 0;
        if (nextCreationFrame == 0)
        {
            SetNextCreationFrame();
            return 0;
        }
        SetNextCreationFrame();
        var at = BoolSetting("CreateAtEdge") ? Game.Terrain.ClosestEdgePoint(obj.Position) : obj.Position;
        if (factionTriggered)
        {
            string side = obj.Owner.Side;
            foreach (var entry in ListSetting("FactionOCL"))
            {
                var (faction, ocl) = ParseFactionOcl(entry);
                if (faction == side)
                {
                    Game.ObjectCreation.Run(ocl, obj, at, secondary: obj.Position);
                    break;
                }
            }
        }
        else
            Game.ObjectCreation.Run(Setting("OCL"), obj, at, secondary: obj.Position);
        return 0;
    }

    /// <summary>"Faction:America OCL:OCL_Name".</summary>
    static (string Faction, string Ocl) ParseFactionOcl(string entry)
    {
        string faction = "", ocl = "";
        foreach (var token in entry.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = token.IndexOf(':');
            if (colon < 0)
                continue;
            string key = token[..colon], value = token[(colon + 1)..];
            if (key.Equals("Faction", StringComparison.OrdinalIgnoreCase))
                faction = value;
            else if (key.Equals("OCL", StringComparison.OrdinalIgnoreCase))
                ocl = value;
        }
        return (faction, ocl);
    }

    bool ShouldCreate() => Frame >= nextCreationFrame && !Owner.IsUnderConstruction;

    public void ResetTimer() => SetNextCreationFrame();

    void SetNextCreationFrame()
    {
        int delay = Game.Random.Int(FramesSetting("MinDelay"), FramesSetting("MaxDelay"));
        timerStartedFrame = Frame;
        nextCreationFrame = timerStartedFrame + delay;
    }

    /// <summary>For the countdown bar under the building.</summary>
    public float CountdownPercent => nextCreationFrame == timerStartedFrame ? 1
        : 1f - (nextCreationFrame - Frame) / (float)(nextCreationFrame - timerStartedFrame);

    public int RemainingFrames => nextCreationFrame - Frame;
}

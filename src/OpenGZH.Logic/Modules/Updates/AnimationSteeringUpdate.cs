using OpenGZH.Logic.Locomotion;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// Plays steering animations as the vehicle turns (the original's AnimationSteeringUpdate):
/// CENTER_TO_LEFT/RIGHT when a turn starts, LEFT/RIGHT_TO_CENTER when it ends, at most one change
/// every MinTransitionTime.
/// </summary>
public sealed class AnimationSteeringUpdate : Module, IUpdateModule
{
    string? currentTurnAnim;
    int nextTransitionFrame;

    public int Update()
    {
        var obj = Owner;
        if (obj.FindModule<PhysicsBehavior>() is not { } physics || Frame < nextTransitionFrame)
            return 0;
        int transition = FramesSetting("MinTransitionTime");
        var turn = physics.Turning;
        void Go(string? clear, string set)
        {
            if (clear != null)
                obj.SetCondition(clear, false);
            obj.SetCondition(set);
            nextTransitionFrame = Frame + transition;
            currentTurnAnim = set;
        }
        switch (currentTurnAnim)
        {
            case null:
                if (turn == Turning.Negative)
                    Go(null, "CENTER_TO_RIGHT");
                else if (turn == Turning.Positive)
                    Go(null, "CENTER_TO_LEFT");
                break;
            case "CENTER_TO_RIGHT":
                if (turn != Turning.Negative)
                    Go("CENTER_TO_RIGHT", "RIGHT_TO_CENTER");
                break;
            case "CENTER_TO_LEFT":
                if (turn != Turning.Positive)
                    Go("CENTER_TO_LEFT", "LEFT_TO_CENTER");
                break;
            default:
                if (turn == Turning.None)
                {
                    obj.SetCondition("LEFT_TO_CENTER", false);
                    obj.SetCondition("RIGHT_TO_CENTER", false);
                    nextTransitionFrame = Frame;
                    currentTurnAnim = null;
                }
                break;
        }
        return 0;
    }
}

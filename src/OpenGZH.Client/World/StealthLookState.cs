using OpenGZH.Logic.Modules.Stealth;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Client.World;

/// <summary>
/// How a stealthy object looks to the local player (the original's Drawable::setStealthLook and the
/// opacity pulse StealthUpdate drives): hidden from enemies, see-through and pulsing for its own side,
/// and a fading orange "heat vision" glow when it is detected (drawn alone for enemies).
/// </summary>
public sealed class StealthLookState
{
    const float VeryTransparentMaterialPassOpacity = 0.001f, MaterialPassOpacityFadeScalar = 0.8f;

    StealthLook look = StealthLook.None;
    float stealthOpacity = 1, pulsePhase = Random.Shared.NextSingle() * MathF.PI;
    float stepTime;

    public bool HiddenByStealth { get; private set; }
    /// <summary>The model's alpha (1 = solid).</summary>
    public float EffectiveOpacity { get; private set; } = 1;
    /// <summary>Strength of the heat-vision pass (0 = none).</summary>
    public float SecondMaterialPassOpacity { get; private set; }
    /// <summary>Only the heat-vision pass is drawn (a detected enemy).</summary>
    public bool HeatVisionOnly => look == StealthLook.VisibleDetected && SecondMaterialPassOpacity != 0;

    public void Update(GameObject obj, Player? viewer, float friendlyOpacity, float seconds)
    {
        var stealth = obj.FindModule<StealthUpdate>();
        var newLook = stealth == null ? StealthLook.None
            : viewer == null ? (obj.HasStatus("STEALTHED") ? StealthLook.VisibleFriendly : StealthLook.None)
            : stealth.LookFor(viewer);
        if (newLook != look)
            SetLook(obj, stealth, newLook, friendlyOpacity);

        stepTime += seconds;
        for (int i = 0; stepTime >= 1f / 30 && i < 4; i++)
        {
            stepTime -= 1f / 30;
            // The heat-vision glow fades unless frenzied.
            if (!obj.Tint.HasFlag(TintStatus.Frenzy))
            {
                if (obj.IsEffectivelyDead)
                    SecondMaterialPassOpacity = 0;
                else if (SecondMaterialPassOpacity > VeryTransparentMaterialPassOpacity)
                    SecondMaterialPassOpacity *= MaterialPassOpacityFadeScalar;
                else
                    SecondMaterialPassOpacity = 0;
            }
            // StealthUpdate's opacity: mines are clear, disguises fade through, the rest pulse.
            if (stealth is { Enabled: true })
            {
                if (obj.IsKindOf("MINE"))
                    SetEffectiveOpacity(0, 0);
                else if (stealth.IsDisguiseTransitioning)
                    SetEffectiveOpacity(stealth.DisguiseOpacity, stealth.DisguiseOpacity < 1 ? 0 : 1);
                else
                {
                    SetEffectiveOpacity(0.5f + MathF.Sin(pulsePhase) * 0.5f);
                    pulsePhase += 0.2f;
                }
            }
            else
                SetEffectiveOpacity(1);
        }
        if (stepTime > 1f / 30)
            stepTime = 0;
    }

    void SetEffectiveOpacity(float pulseFactor, float explicitOpacity = -1)
    {
        if (explicitOpacity != -1)
            stealthOpacity = Math.Clamp(explicitOpacity, 0, 1);
        float pf = Math.Clamp(pulseFactor, 0, 1);
        EffectiveOpacity = stealthOpacity + (1 - stealthOpacity) * pf;
    }

    void SetLook(GameObject obj, StealthUpdate? stealth, StealthLook newLook, float friendlyOpacity)
    {
        stealthOpacity = 1;
        switch (newLook)
        {
            case StealthLook.None:
            case StealthLook.DisguisedEnemy:
                HiddenByStealth = false;
                SecondMaterialPassOpacity = 0;
                break;
            case StealthLook.VisibleFriendly:
            case StealthLook.VisibleFriendlyDetected:
                HiddenByStealth = false;
                if (stealth is { IsDisguised: true })
                    break;   // disguises drive the opacity themselves
                stealthOpacity = stealth?.FriendlyOpacityMin ?? friendlyOpacity;
                SecondMaterialPassOpacity = newLook == StealthLook.VisibleFriendlyDetected && !obj.IsKindOf("MINE") ? 1 : 0;
                break;
            case StealthLook.VisibleDetected:
                HiddenByStealth = false;
                SecondMaterialPassOpacity = obj.IsKindOf("MINE") ? 0 : 1;
                break;
            case StealthLook.Invisible:
                HiddenByStealth = true;
                SecondMaterialPassOpacity = 0;
                break;
        }
        look = newLook;
    }
}

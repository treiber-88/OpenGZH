using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic;

/// <summary>
/// Things the simulation tells the outside world about: visual and sound effects, EVA announcements,
/// objects appearing and disappearing. The logic never draws or plays anything itself; the client
/// subscribes to these events. (Effects never feed back into the simulation, so it stays deterministic.)
/// </summary>
public sealed class GameEvents
{
    public event Action<GameObject>? OnObjectCreated;
    public event Action<GameObject>? OnObjectDestroyed;
    public event Action<GameObject, DamageInfo>? OnObjectDied;
    public event Action<GameObject, Player>? OnOwnerChanged;
    public event Action<GameObject, VeterancyLevel, VeterancyLevel>? OnVeterancyChanged;

    /// <summary>An FXList (particles, sounds, camera shake) at a position, optionally attached to an object.</summary>
    public event Action<FxRequest>? OnFx;

    /// <summary>A weapon's FireFX or detonation FX, played at the shooter's barrel by the client.</summary>
    public event Action<WeaponFireFx>? OnWeaponFireFx;

    /// <summary>A particle system (by template name) at a position, optionally attached to an object.</summary>
    public event Action<string, Vector3, GameObject?>? OnParticleSystem;

    /// <summary>A radar ping for a player (e.g. "StealthDiscovered", "Infiltration", "UnderAttack").</summary>
    public event Action<string, Vector3, Player>? OnRadarEvent;

    /// <summary>A one-off sound event at a position (null = not positional).</summary>
    public event Action<SoundRequest>? OnSound;

    /// <summary>Scripts controlling the audio: music changes, speech, volumes, muting sound types.</summary>
    public event Action<AudioCommand>? OnAudioCommand;

    public void Audio(AudioCommand command) => OnAudioCommand?.Invoke(command);

    /// <summary>A script action for the camera, the screen or the interface (letterbox, camera moves, timers ...).</summary>
    public event Action<Scripting.ScriptAction>? OnScriptClientAction;
    public void ScriptClientAction(Scripting.ScriptAction action) => OnScriptClientAction?.Invoke(action);

    /// <summary>EVA announcements switched on or off by a script.</summary>
    public bool EvaEnabled { get; private set; } = true;
    public event Action<bool>? OnEvaEnabledChanged;
    public void SetEvaEnabled(bool enabled)
    {
        EvaEnabled = enabled;
        OnEvaEnabledChanged?.Invoke(enabled);
    }

    /// <summary>An object must leave every selection (it became unmanned, say).</summary>
    public event Action<GameObject>? OnDeselect;
    public void Deselect(GameObject obj) => OnDeselect?.Invoke(obj);

    /// <summary>OBJECT_FORCE_SELECT: select this object (and maybe centre the camera on it, with a sound).</summary>
    public event Action<GameObject, bool, string>? OnForceSelect;
    public void ForceSelect(GameObject obj, bool centerInView, string sound) => OnForceSelect?.Invoke(obj, centerInView, sound);

    /// <summary>An EVA announcement ("Building complete", "Unit lost") for a player.</summary>
    public event Action<string, Player>? OnEva;

    /// <summary>A weapon fired: the client shows muzzle flashes and recoil.</summary>
    public event Action<GameObject, WeaponTemplate, int>? OnWeaponFired;

    /// <summary>Cash arrived at a building (supply delivery, oil derrick): the client shows a floating "+$".</summary>
    public event Action<GameObject, int>? OnMoneyEarned;

    public event Action<Player, string>? OnUpgradeCompleted;
    public event Action<Player, string>? OnScienceGranted;

    /// <summary>A message for the player (script text, cheats, errors such as "not enough money").</summary>
    public event Action<string, Player?>? OnMessage;

    public void ObjectCreated(GameObject obj) => OnObjectCreated?.Invoke(obj);
    public void ObjectDestroyed(GameObject obj) => OnObjectDestroyed?.Invoke(obj);
    public void ObjectDied(GameObject obj, DamageInfo damage) => OnObjectDied?.Invoke(obj, damage);
    public void OwnerChanged(GameObject obj, Player oldOwner) => OnOwnerChanged?.Invoke(obj, oldOwner);
    public void VeterancyChanged(GameObject obj, VeterancyLevel from, VeterancyLevel to) => OnVeterancyChanged?.Invoke(obj, from, to);

    /// <summary>Plays an FX list at a point, oriented and attached to <paramref name="attachedTo"/> if given.</summary>
    public void PlayFx(string fxList, Vector3 position, GameObject? attachedTo = null)
    {
        if (!string.IsNullOrEmpty(fxList) && !fxList.Equals("None", StringComparison.OrdinalIgnoreCase))
            OnFx?.Invoke(new FxRequest(fxList, position, attachedTo));
    }

    /// <summary>Plays an FX list with everything the original passes: a second point (tracers, rays), a speed and a radius.</summary>
    public void PlayFx(FxRequest request)
    {
        if (!string.IsNullOrEmpty(request.Name) && !request.Name.Equals("None", StringComparison.OrdinalIgnoreCase))
            OnFx?.Invoke(request);
    }

    public void WeaponFired(WeaponFireFx fx)
    {
        if (!string.IsNullOrEmpty(fx.Name) && !fx.Name.Equals("None", StringComparison.OrdinalIgnoreCase))
            OnWeaponFireFx?.Invoke(fx);
    }

    /// <summary>
    /// Plays an audio event: at a position, or following <paramref name="source"/> (whose owner decides
    /// who hears "player" / "allies" / "enemies" sounds unless <paramref name="onlyFor"/> says).
    /// </summary>
    public void PlaySound(string audioEvent, Vector3? position = null, Player? onlyFor = null, GameObject? source = null)
    {
        if (!string.IsNullOrEmpty(audioEvent) && !audioEvent.Equals("NoSound", StringComparison.OrdinalIgnoreCase))
            OnSound?.Invoke(new SoundRequest(audioEvent, position, onlyFor, source));
    }

    /// <summary>A sound following an object that the logic stops later (a crashing jet's death loop).</summary>
    public event Action<uint, string, GameObject>? OnHandledSound;
    public event Action<uint>? OnStopHandledSound;
    uint nextSoundHandle;

    public uint PlayHandledSound(string audioEvent, GameObject source)
    {
        if (string.IsNullOrEmpty(audioEvent) || audioEvent.Equals("NoSound", StringComparison.OrdinalIgnoreCase))
            return 0;
        uint handle = ++nextSoundHandle;
        OnHandledSound?.Invoke(handle, audioEvent, source);
        return handle;
    }

    public void StopHandledSound(uint handle)
    {
        if (handle != 0)
            OnStopHandledSound?.Invoke(handle);
    }

    public void Eva(string evaEvent, Player player) => OnEva?.Invoke(evaEvent, player);

    public void ParticleSystem(string template, Vector3 position, GameObject? attachedTo = null)
    {
        if (!string.IsNullOrEmpty(template) && !template.Equals("None", StringComparison.OrdinalIgnoreCase))
            OnParticleSystem?.Invoke(template, position, attachedTo);
    }

    /// <summary>A particle system fixed to an object at an object-relative point, that the logic can stop later (damage smoke).</summary>
    public event Action<uint, string, Vector3, GameObject, int, int>? OnAttachedParticleSystem;
    public event Action<uint>? OnStopParticleSystem;
    uint nextParticleHandle;

    /// <summary>Starts an attached particle system; returns a handle for <see cref="StopParticleSystem"/> (0 if none).</summary>
    public uint AttachParticleSystem(string template, Vector3 localPosition, GameObject attachedTo, int lifetimeFrames = 0, int initialDelayFrames = 0)
    {
        if (string.IsNullOrEmpty(template) || template.Equals("None", StringComparison.OrdinalIgnoreCase))
            return 0;
        uint handle = ++nextParticleHandle;
        OnAttachedParticleSystem?.Invoke(handle, template, localPosition, attachedTo, lifetimeFrames, initialDelayFrames);
        return handle;
    }

    public void StopParticleSystem(uint handle)
    {
        if (handle != 0)
            OnStopParticleSystem?.Invoke(handle);
    }

    public void RadarEvent(string type, Vector3 position, Player player) => OnRadarEvent?.Invoke(type, position, player);
    public void WeaponFired(GameObject shooter, WeaponTemplate weapon, int slot) => OnWeaponFired?.Invoke(shooter, weapon, slot);
    public void MoneyEarned(GameObject at, int amount) => OnMoneyEarned?.Invoke(at, amount);
    public void UpgradeCompleted(Player player, string upgrade) => OnUpgradeCompleted?.Invoke(player, upgrade);
    public void ScienceGranted(Player player, string science) => OnScienceGranted?.Invoke(player, science);
    public void Message(string text, Player? player = null) => OnMessage?.Invoke(text, player);

    /// <summary>A 2D animation in the world (a crate's ExecuteAnimation).</summary>
    public event Action<string, Vector3>? OnWorldAnimation;
    public void WorldAnimation(string animation, Vector3 position) => OnWorldAnimation?.Invoke(animation, position);

    /// <summary>An object took damage (after armor); the radar raises "under attack" from this.</summary>
    public event Action<GameObject, DamageInfo>? OnDamaged;
    public void Damaged(GameObject obj, DamageInfo damage) => OnDamaged?.Invoke(obj, damage);

    /// <summary>A target marker on the ground for a superweapon in flight (DeliveryDecal).</summary>
    public event Action<Vector3, float, Player, GameObject>? OnRadiusDecal;
    public void RadiusDecal(Vector3 position, float radius, Player owner, GameObject attachedTo) => OnRadiusDecal?.Invoke(position, radius, owner, attachedTo);

    /// <summary>The object flashes as if selected (a sabotaged building).</summary>
    public event Action<GameObject>? OnFlashAsSelected;
    public void FlashAsSelected(GameObject obj) => OnFlashAsSelected?.Invoke(obj);

    /// <summary>Money taken away (red floating text).</summary>
    public event Action<GameObject, int>? OnMoneyLost;
    public void MoneyLost(GameObject at, int amount) => OnMoneyLost?.Invoke(at, amount);

    /// <summary>The "got healed" icon over a unit (GameData's GetHealedAnimationName).</summary>
    public event Action<GameObject>? OnHealedIcon;
    public void HealedIcon(GameObject obj) => OnHealedIcon?.Invoke(obj);
}

/// <summary>
/// One FX list to play (the original's doFXPos / doFXObj): at <paramref name="Position"/>, turned like
/// <paramref name="Object"/> (which attached nuggets follow), towards <paramref name="Secondary"/> for tracers
/// and rays, with the weapon's <paramref name="Speed"/> and an <paramref name="OverrideRadius"/> for UseCallersRadius.
/// </summary>
public sealed record FxRequest(string Name, Vector3 Position, GameObject? Object = null, Vector3? Secondary = null,
    float Speed = 0, float OverrideRadius = 0, Matrix4x4? Transform = null, GameObject? SecondaryObject = null);

/// <summary>
/// A weapon's fire FX: the client plays it at the barrel's fire-FX bone (the original's handleWeaponFireFX),
/// or at the shooter (the target, for contact weapons) when the model has none.
/// </summary>
public sealed record WeaponFireFx(string Name, GameObject Shooter, int Slot, int Barrel, Vector3 Target, float Speed, float Radius,
    bool IsContactWeapon, bool PlayWhenStealthed);

/// <summary>A sound to play: where, for whom, and the object it follows.</summary>
public sealed record SoundRequest(string Name, Vector3? Position, Player? Player, GameObject? Source);

public enum AudioCommandKind
{
    /// <summary>A sound for the local player (Name), at Position or following Object if given.</summary>
    PlaySound,
    /// <summary>Speech (Name); Flag = may overlap other speech.</summary>
    Speech,
    /// <summary>Change the music to Name; Flag = fade the old track out, Flag2 = fade the new one in.</summary>
    Music,
    /// <summary>Volume 0..1 (Value) of music, sound or speech (Name: "MUSIC", "SOUND", "SPEECH").</summary>
    SetVolume,
    /// <summary>Mute (Flag = false) or unmute an audio event by name; empty Name = all.</summary>
    EnableType,
    /// <summary>Stop everything that is muted.</summary>
    RemoveDisabled,
    /// <summary>Stop every playing copy of an audio event.</summary>
    RemoveType,
    /// <summary>Set an audio event's volume (Value 0..1), or restore it (Value &lt; 0); empty Name = all.</summary>
    OverrideVolume,
    /// <summary>Pause (Flag = true) or resume ambient object sounds.</summary>
    PauseAmbient,
    /// <summary>Pause (Flag = true) or resume all sound effects.</summary>
    PauseSounds,
    /// <summary>Allow (Flag = true) or silence an object's ambient sound.</summary>
    EnableObjectSound,
}

/// <summary>One audio instruction from a script (the original's ScriptActions audio actions).</summary>
public sealed record AudioCommand(AudioCommandKind Kind, string Name = "", Vector3? Position = null, GameObject? Object = null,
    float Value = 0, bool Flag = false, bool Flag2 = false);


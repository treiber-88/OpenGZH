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
    public event Action<string, Vector3, GameObject?>? OnFx;

    /// <summary>A particle system (by template name) at a position, optionally attached to an object.</summary>
    public event Action<string, Vector3, GameObject?>? OnParticleSystem;

    /// <summary>A radar ping for a player (e.g. "StealthDiscovered", "Infiltration", "UnderAttack").</summary>
    public event Action<string, Vector3, Player>? OnRadarEvent;

    /// <summary>A one-off sound event at a position (null = not positional).</summary>
    public event Action<string, Vector3?, Player?>? OnSound;

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

    public void PlayFx(string fxList, Vector3 position, GameObject? attachedTo = null)
    {
        if (!string.IsNullOrEmpty(fxList) && !fxList.Equals("None", StringComparison.OrdinalIgnoreCase))
            OnFx?.Invoke(fxList, position, attachedTo);
    }

    public void PlaySound(string audioEvent, Vector3? position = null, Player? onlyFor = null)
    {
        if (!string.IsNullOrEmpty(audioEvent) && !audioEvent.Equals("NoSound", StringComparison.OrdinalIgnoreCase))
            OnSound?.Invoke(audioEvent, position, onlyFor);
    }

    public void Eva(string evaEvent, Player player) => OnEva?.Invoke(evaEvent, player);

    public void ParticleSystem(string template, Vector3 position, GameObject? attachedTo = null)
    {
        if (!string.IsNullOrEmpty(template) && !template.Equals("None", StringComparison.OrdinalIgnoreCase))
            OnParticleSystem?.Invoke(template, position, attachedTo);
    }

    public void RadarEvent(string type, Vector3 position, Player player) => OnRadarEvent?.Invoke(type, position, player);
    public void WeaponFired(GameObject shooter, WeaponTemplate weapon, int slot) => OnWeaponFired?.Invoke(shooter, weapon, slot);
    public void MoneyEarned(GameObject at, int amount) => OnMoneyEarned?.Invoke(at, amount);
    public void UpgradeCompleted(Player player, string upgrade) => OnUpgradeCompleted?.Invoke(player, upgrade);
    public void ScienceGranted(Player player, string science) => OnScienceGranted?.Invoke(player, science);
    public void Message(string text, Player? player = null) => OnMessage?.Invoke(text, player);
}

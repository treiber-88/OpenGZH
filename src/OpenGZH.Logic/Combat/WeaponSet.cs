using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Combat;

/// <summary>
/// An object's weapons. The template lists several WeaponSets, each with Conditions; the set whose
/// conditions best match the object's current weapon-set flags (veterancy, upgrades, riders...) is used.
/// </summary>
public sealed class WeaponSet
{
    readonly GameObject owner;
    sealed record SetOption(FlagSet Conditions, RuleNode Node);

    readonly List<SetOption> sets = new();
    RuleNode? currentSet;

    public Weapon?[] Slots { get; } = new Weapon?[3];

    /// <summary>The slot chosen for the current attack (or the default).</summary>
    public WeaponSlot CurrentSlot { get; private set; } = WeaponSlot.Primary;

    /// <summary>Weapon slot locked by a player command (e.g. "switch to flashbangs"), or null.</summary>
    public WeaponSlot? LockedSlot { get; private set; }

    public WeaponSet(GameObject owner) => this.owner = owner;

    public bool HasAnyWeapon => Slots.Any(w => w != null);
    public IEnumerable<Weapon> All => Slots.Where(w => w != null)!;
    public Weapon? Current => Slots[(int)CurrentSlot] ?? All.FirstOrDefault();

    internal void LoadFromTemplate()
    {
        sets.Clear();
        if (owner.Template.Rules.TryGet("WeaponSet", out var node))
            foreach (var set in node.AsList())
                sets.Add(new SetOption(Flags.WeaponSet.Parse(set.GetString("Conditions")), set));
        UpdateFromFlags();
    }

    /// <summary>Re-picks the active set after the object's weapon-set flags change.</summary>
    public void UpdateFromFlags()
    {
        var best = SparseMatch.Best(sets, set => [set.Conditions], owner.WeaponSetFlags)?.Node;

        if (best == currentSet)
            return;
        currentSet = best;

        var old = Slots.ToArray();
        Array.Clear(Slots);
        if (best == null)
            return;

        if (best.TryGet("Weapon", out var weapons))
        {
            foreach (var entry in weapons.AsList())
            {
                string[] words = RuleValue.Words(entry.Value);
                if (words.Length < 2 || !Enum.TryParse<WeaponSlot>(words[0], ignoreCase: true, out var slot))
                    continue;
                var template = owner.Game.Catalog.Weapon(words[1]);
                if (template == null)
                    continue;
                // Keep ammo and reload state when the same weapon stays in the slot.
                Slots[(int)slot] = old[(int)slot]?.Template == template ? old[(int)slot] : new Weapon(owner, template, slot);
            }
        }

        owner.SetStatus("CAN_ATTACK", HasAnyWeapon);
    }

    public void LockSlot(WeaponSlot slot)
    {
        LockedSlot = slot;
        CurrentSlot = slot;
    }

    public void ReleaseLock() => LockedSlot = null;

    /// <summary>Instantly refills every weapon's clip (e.g. FireWeaponPower before its volley).</summary>
    public void ReloadAll()
    {
        foreach (var weapon in All)
            weapon.Reload();
    }

    /// <summary>KindOf preferences per slot ("PreferredAgainst: PRIMARY INFANTRY").</summary>
    IEnumerable<(WeaponSlot Slot, string[] Kinds)> Preferences()
    {
        if (currentSet == null || !currentSet.TryGet("PreferredAgainst", out var node))
            yield break;
        foreach (var entry in node.AsList())
        {
            string[] words = RuleValue.Words(entry.Value);
            if (words.Length >= 2 && Enum.TryParse<WeaponSlot>(words[0], true, out var slot))
                yield return (slot, words[1..]);
        }
    }

    /// <summary>Picks the best weapon to use against a target, and makes it current. Returns null if none can hit it.</summary>
    public Weapon? ChooseFor(GameObject target)
    {
        if (LockedSlot is { } locked)
            return Slots[(int)locked] is { } lockedWeapon && lockedWeapon.CanTarget(target) ? lockedWeapon : null;

        foreach (var (slot, kinds) in Preferences())
        {
            if (Slots[(int)slot] is { } preferred && preferred.CanTarget(target) && kinds.Any(target.IsKindOf))
            {
                CurrentSlot = slot;
                return preferred;
            }
        }

        // Otherwise the first weapon that can hit the target, preferring one already in range.
        Weapon? choice = All.Where(w => w.CanTarget(target)).OrderByDescending(w => w.IsInRange(target)).ThenBy(w => w.Slot).FirstOrDefault();
        if (choice != null)
            CurrentSlot = choice.Slot;
        return choice;
    }

    /// <summary>The longest range of any weapon that can attack this target (for approaching it).</summary>
    public float MaxRangeAgainst(GameObject target) =>
        All.Where(w => w.CanTarget(target)).Select(w => w.AttackRange).DefaultIfEmpty(0).Max();

    public float MaxRange => All.Select(w => w.AttackRange).DefaultIfEmpty(0).Max();
}

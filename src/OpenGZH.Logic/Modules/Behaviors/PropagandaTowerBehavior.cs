using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// China's speaker tower and propaganda Overlord (the original's PropagandaTowerBehavior): every
/// DelayBetweenUpdates it pulses and collects allied non-structures within Radius; while inside they
/// heal HealPercentEachSecond and fire ENTHUSIASTIC (and SUBLIMINAL once UpgradeRequired is done).
/// </summary>
public sealed class PropagandaTowerBehavior : Module, IUpdateModule, IDieModule, IOwnershipModule
{
    List<uint> insideList = new();
    int lastScanFrame;

    static readonly int Sold = Flags.ObjectStatus.IndexOf("SOLD");
    static readonly FlagSet AllButHeld = DisabledMask.All.Without(Flags.Disabled.IndexOf("DISABLED_HELD"));

    public FlagSet DisabledTypesToProcess => DisabledMask.All;

    public void OnDelete() => RemoveAllInfluence();

    public void OnDie(DamageInfo killingBlow) => RemoveAllInfluence();

    public void OnCapture(Player oldOwner, Player newOwner)
    {
        if (newOwner == Game.Players.Neutral)
        {
            RemoveAllInfluence();
            Owner.SleepUpdate(this);
        }
        else
            Owner.WakeUpdate(this);
    }

    public int Update()
    {
        var self = Owner;
        if (self.IsUnderConstruction)
            return 0;
        if (self.HasStatus(Sold))
        {
            RemoveAllInfluence();
            return LogicTime.SleepForever;
        }
        if (self.IsEffectivelyDead)
            return LogicTime.SleepForever;
        if (self.IsDisabled && self.DisabledFlags.HasAny(AllButHeld))
        {
            RemoveAllInfluence();
            return 0;
        }
        if (Game.FindObject(self.ContainedById) is { } container && container.ContainedById != 0)
        {
            RemoveAllInfluence();
            return 0;
        }
        if (Frame - lastScanFrame >= FramesSetting("DelayBetweenUpdates", 100 * LogicTime.MillisecondsPerFrame))
        {
            DoScan();
            lastScanFrame = Frame;
        }
        // Only things that count as units keep the effect (the original's SCORE / MP_COUNT_FOR_VICTORY test).
        insideList.RemoveAll(id =>
        {
            var obj = Game.FindObject(id);
            if (obj != null && (obj.IsKindOf("SCORE") || obj.IsKindOf("SCORE_CREATE") || obj.IsKindOf("SCORE_DESTROY") || obj.IsKindOf("MP_COUNT_FOR_VICTORY")))
            {
                EffectLogic(obj, giving: true);
                return false;
            }
            return true;
        });
        return 0;
    }

    bool UpgradeComplete
    {
        get
        {
            string name = Setting("UpgradeRequired");
            if (name.Length == 0)
                return false;
            var upgrade = Game.Catalog.Upgrade(name);
            bool isObject = upgrade != null && upgrade.GetString("Type", "PLAYER").Equals("OBJECT", StringComparison.OrdinalIgnoreCase);
            return isObject ? Owner.HasUpgrade(name) : Owner.Owner.HasUpgrade(name);
        }
    }

    void EffectLogic(GameObject obj, bool giving)
    {
        if (giving)
        {
            // (The original's effectLogic checks the player's upgrade even for object upgrades.)
            string required = Setting("UpgradeRequired");
            bool upgraded = required.Length > 0 && Owner.Owner.HasUpgrade(required);
            if (obj.Weapons.All.Any(w => w.Template.PrimaryDamage > 0 || w.Template.SecondaryDamage > 0))
            {
                obj.SetWeaponBonus("ENTHUSIASTIC");
                if (upgraded)
                    obj.SetWeaponBonus("SUBLIMINAL");
            }
            if (obj.Body is { } body)
            {
                float percent = upgraded ? Percent("UpgradedHealPercentEachSecond", 0.02f) : Percent("HealPercentEachSecond", 0.01f);
                float amount = percent / LogicTime.FramesPerSecond * body.MaxHealth;
                obj.AttemptHealingFromSoleBenefactor(amount, Owner, FramesSetting("DelayBetweenUpdates", 100 * LogicTime.MillisecondsPerFrame));
            }
        }
        else
        {
            obj.SetWeaponBonus("ENTHUSIASTIC", false);
            obj.SetWeaponBonus("SUBLIMINAL", false);
        }
    }

    float Percent(string key, float fallback) => FloatSetting(key, fallback);

    void RemoveAllInfluence()
    {
        foreach (var id in insideList)
            if (Game.FindObject(id) is { } obj)
                EffectLogic(obj, giving: false);
        insideList.Clear();
    }

    void DoScan()
    {
        var us = Owner;
        bool upgradePresent = UpgradeComplete;
        bool doFx = true;
        if (Game.FindObject(us.ContainedById) is { } overlord)
        {
            if (!us.Owner.IsLocalPlayer && overlord.HasStatus(Status.Stealthed) && !overlord.HasStatus(Status.Detected))
                doFx = false;
            if (us.IsKindOf("VEHICLE") && !us.IsKindOf("PORTABLE_STRUCTURE"))
                return;
            if (overlord.ContainedById != 0)
                return; // inside a Helix inside...
        }
        if (!us.Owner.IsLocalPlayer && us.HasStatus(Status.Stealthed) && !us.HasStatus(Status.Detected))
            doFx = false;
        if (doFx)
        {
            string fx = upgradePresent ? Setting("UpgradedPulseFX") : Setting("PulseFX");
            if (fx.Length > 0)
                Game.Events.PlayFx(fx, us.Position, us);
        }

        bool affectsSelf = BoolSetting("AffectsSelf");
        var newInside = new List<uint>();
        float radius = FloatSetting("Radius", 1);
        foreach (var obj in Game.ObjectsInRadius(us.Position, radius))
        {
            if (obj == us && !affectsSelf)
                continue;
            if (obj.IsEffectivelyDead || obj.IsKindOf(Kind.Structure) || us.RelationshipTo(obj) != Relationship.Allies || obj.IsOffMap != us.IsOffMap)
                continue;
            if (MathUtil.DistanceSquared2D(obj.Position, us.Position) > radius * radius)
                continue;
            newInside.Add(obj.Id);
        }
        foreach (var id in insideList)
            if (!newInside.Contains(id) && Game.FindObject(id) is { } left)
                EffectLogic(left, giving: false);
        // The original builds the new list by prepending.
        newInside.Reverse();
        insideList = newInside;
    }
}

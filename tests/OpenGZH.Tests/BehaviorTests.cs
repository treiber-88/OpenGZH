using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Healing, poison, fire and crushing behaviours.</summary>
public class BehaviorTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static Vector3 Ground(GameLogic game, Vector3 p) => p with { Z = game.Terrain.GroundHeight(p) };

    static void Run(GameLogic game, int frames)
    {
        for (int i = 0; i < frames; i++)
            game.Update();
    }

    static (GameLogic, Logic.Players.Player, Logic.Players.Player, Vector3) Setup(BaseGameFixture content)
    {
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        var spot = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter").Position + new Vector3(200, 0, 0);
        spot = game.Pathfinder.NearestPassable(spot, Logic.Locomotion.Surfaces.Ground) ?? spot;
        return (game, alice, bob, Ground(game, spot));
    }

    [Fact]
    public void AmbulanceHealsNearbyInfantry()
    {
        if (!content.Available) return;
        var (game, alice, _, spot) = Setup(content);
        var ranger = game.CreateObject("AmericaInfantryRanger", alice, spot)!;
        ranger.Body!.SetHealth(ranger.MaxHealth / 2);
        float hurt = ranger.Health;
        var medic = game.CreateObject("AmericaVehicleMedic", alice, Ground(game, spot + new Vector3(30, 0, 0)))!;
        Run(game, 30 * 5);
        output.WriteLine($"ranger health {hurt} -> {ranger.Health} / {ranger.MaxHealth}");
        // 4 HP a second for 5 seconds (the first pulse comes at a random moment within the first second).
        Assert.InRange(ranger.Health, hurt + 4 * 4, hurt + 4 * 6);
    }

    [Fact]
    public void PoisonKeepsHurtingUntilHealed()
    {
        if (!content.Available) return;
        var (game, alice, bob, spot) = Setup(content);
        var ranger = game.CreateObject("AmericaInfantryRanger", alice, spot)!;
        ranger.Damage(new DamageInfo { Amount = 5, DamageType = "POISON", DeathType = "POISONED" });
        Assert.True(ranger.Tint.HasFlag(TintStatus.Poisoned));
        float afterHit = ranger.Health;
        Run(game, 30);
        float afterSecond = ranger.Health;
        output.WriteLine($"health {ranger.MaxHealth} -> {afterHit} -> {afterSecond}");
        Assert.True(afterSecond < afterHit, "poison keeps damaging after the hit");

        ranger.Body!.AttemptHealing(1);
        Assert.False(ranger.Tint.HasFlag(TintStatus.Poisoned), "healing cures poison");
        float cured = ranger.Health;
        Run(game, 30);
        Assert.Equal(cured, ranger.Health);
    }

    [Fact]
    public void BuildingsRepairThemselvesAfterADelay()
    {
        if (!content.Available) return;
        var (game, alice, _, _) = Setup(content);
        var commandCenter = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter");
        commandCenter.Damage(new DamageInfo { Amount = 1000, DamageType = "EXPLOSION" });
        float damaged = commandCenter.Health;
        int delay = game.Catalog.GameData.BaseRegenDelayFrames;
        Run(game, delay - 10);
        Assert.Equal(damaged, commandCenter.Health);
        Run(game, 30 * 3);
        output.WriteLine($"after {delay} frames: {damaged} -> {commandCenter.Health}");
        Assert.True(commandCenter.Health > damaged, "repair starts after BaseRegenDelay");
    }

    [Fact]
    public void TankRunsOverEnemyInfantry()
    {
        if (!content.Available) return;
        var (game, alice, bob, spot) = Setup(content);
        var tank = game.CreateObject("AmericaTankCrusader", alice, spot)!;
        var victim = game.CreateObject("GLAInfantryRebel", bob, Ground(game, spot + new Vector3(80, 0, 0)))!;
        victim.AI!.Stop(CommandSource.AI);
        game.Commands.Enqueue(new MoveCommand(alice.Index, [tank.Id], Ground(game, spot + new Vector3(200, 0, 0))));
        for (int i = 0; i < 30 * 8 && !victim.IsEffectivelyDead; i++)
            game.Update();
        output.WriteLine($"tank at {tank.Position}, rebel dead={victim.IsEffectivelyDead}");
        Assert.True(victim.IsEffectivelyDead, "the Crusader squishes the Rebel it drives over");
    }

    [Fact]
    public void NukeCannonUnpacksBeforeFiring()
    {
        if (!content.Available) return;
        var (game, _, bob, spot) = Setup(content);
        var china = game.Players.Find("Alice")!;
        var cannon = game.CreateObject("ChinaVehicleNukeLauncher", china, spot)!;
        var target = game.CreateObject("GLAInfantryRebel", bob, Ground(game, spot + new Vector3(250, 0, 0)))!;
        target.Body!.Indestructible = true;
        int firstShotFrame = -1;
        game.Events.OnWeaponFired += (shooter, _, _) => { if (shooter == cannon && firstShotFrame < 0) firstShotFrame = game.Frame; };

        int start = game.Frame;
        game.Commands.Enqueue(new AttackObjectCommand(china.Index, [cannon.Id], target.Id));
        Run(game, 30);
        Assert.True(cannon.HasCondition("UNPACKING"), "it unpacks first");
        Assert.Equal(-1, firstShotFrame);
        Run(game, 30 * 6);
        output.WriteLine($"deployed={cannon.HasStatus(Logic.Core.Status.Deployed)} first shot after {firstShotFrame - start} frames");
        Assert.True(cannon.HasStatus(Logic.Core.Status.Deployed));
        Assert.True(firstShotFrame - start >= 100, "no shot before UnpackTime (3333 ms = 100 frames)");

        // Ordered away, it packs up before driving off.
        var startPosition = cannon.Position;
        game.Commands.Enqueue(new MoveCommand(china.Index, [cannon.Id], Ground(game, spot - new Vector3(200, 0, 0))));
        Run(game, 30 * 2);
        Assert.True(cannon.HasCondition("PACKING") || !cannon.HasStatus(Logic.Core.Status.Deployed));
        Assert.True(MathF.Abs(cannon.Position.X - startPosition.X) < 1, "doesn't move while packing");
        Run(game, 30 * 6);
        Assert.True(cannon.Position.X < startPosition.X - 20, "drives off once packed");
    }

    [Fact]
    public void HackerEarnsCashAndPacksUpBeforeMoving()
    {
        if (!content.Available) return;
        var (game, alice, _, spot) = Setup(content);
        var hacker = game.CreateObject("ChinaInfantryHacker", alice, spot)!;
        var ai = (Logic.Modules.AI.HackInternetAIUpdate)hacker.AI!;
        int before = alice.Money.Amount;
        game.Commands.Enqueue(new HackInternetCommand(alice.Index, [hacker.Id]));
        Run(game, 2);
        Assert.True(ai.IsHackingPackingOrUnpacking);
        Assert.False(ai.IsIdle);

        // Unpacking takes 7.3 s +-50%, then 5 credits every 2 s.
        Run(game, 30 * 20);
        int earned = alice.Money.Amount - before;
        output.WriteLine($"earned {earned} in 20 s, xp {hacker.ExperiencePoints}");
        Assert.True(ai.IsHacking);
        Assert.InRange(earned, 5 * 4, 5 * 7);

        var start = hacker.Position;
        game.Commands.Enqueue(new MoveCommand(alice.Index, [hacker.Id], Ground(game, spot + new Vector3(0, 100, 0))));
        Run(game, 30);
        Assert.False(ai.IsHacking);
        Assert.True(hacker.HasCondition("PACKING"));
        Assert.True(Vector3.Distance(hacker.Position, start) < 1, "stays put while packing up");
        Run(game, 30 * 12);
        Assert.False(ai.IsHackingPackingOrUnpacking);
        Assert.True(Vector3.Distance(hacker.Position, start) > 50, "then carries out the move order");
    }

    [Fact]
    public void TroopCrawlerUnloadsToAttackAndCallsTroopsBack()
    {
        if (!content.Available) return;
        var (game, alice, bob, spot) = Setup(content);
        var crawler = game.CreateObject("ChinaVehicleTroopCrawler", alice, spot)!;
        Run(game, 2);
        Assert.Equal(8, crawler.Contain!.Count);

        var target = game.CreateObject("GLAInfantryRebel", bob, Ground(game, spot + new Vector3(120, 0, 0)))!;
        game.Commands.Enqueue(new AttackObjectCommand(alice.Index, [crawler.Id], target.Id));
        int mostOutside = 0;
        for (int i = 0; i < 30 * 15 && !target.IsEffectivelyDead; i++)
        {
            game.Update();
            mostOutside = Math.Max(mostOutside, 8 - crawler.Contain.Count);
        }
        output.WriteLine($"rebel dead={target.IsEffectivelyDead}, at most {mostOutside} Red Guards out");
        Assert.True(mostOutside >= 4, "the Red Guards jump out to attack");
        Assert.True(target.IsEffectivelyDead);

        Run(game, 30 * 10);
        output.WriteLine($"back inside: {crawler.Contain.Count}");
        Assert.True(crawler.Contain.Count >= mostOutside - 1, "and climb back in once the target is dead");
    }

    [Theory]
    [InlineData("AmericaInfantryMissileDefender", "GLATankScorpion", 150)]
    [InlineData("AmericaVehicleTomahawk", "GLABarracks", 400)]
    public void GuidedMissilesFlyToTheirTargetAndHit(string shooterName, string targetName, float distance)
    {
        if (!content.Available) return;
        var (game, alice, bob, spot) = Setup(content);
        var shooter = game.CreateObject(shooterName, alice, spot)!;
        var target = game.CreateObject(targetName, bob, Ground(game, spot + new Vector3(distance, 40, 0)))!;
        float health = target.Health;
        int missiles = 0;
        game.Events.OnWeaponFired += (s, _, _) => { if (s == shooter) missiles++; };
        game.Commands.Enqueue(new AttackObjectCommand(alice.Index, [shooter.Id], target.Id));
        for (int i = 0; i < 30 * 25 && target.Health == health; i++)
            game.Update();
        output.WriteLine($"{shooterName}: {missiles} fired, {targetName} health {health} -> {target.Health}");
        Assert.True(missiles > 0);
        Assert.True(target.Health < health, "the missile reached and damaged its target");
        Assert.DoesNotContain(game.Objects, o => o.AI is Logic.Modules.AI.MissileAIUpdate m && !o.IsDestroyed && game.Frame - o.CreatedFrame > 30 * 20);
    }

    [Fact]
    public void FlameDamageSetsThingsAlight()
    {
        if (!content.Available) return;
        var (game, alice, _, spot) = Setup(content);
        var target = game.ObjectsOwnedBy(alice).Concat(game.Objects).FirstOrDefault(o => o.FindModule<Logic.Modules.Updates.FlammableUpdate>() != null);
        if (target == null)
        {
            // Trees and most infantry are flammable; make one if the map has none nearby.
            target = game.CreateObject("AmericaInfantryRanger", alice, spot)!;
            if (target.FindModule<Logic.Modules.Updates.FlammableUpdate>() == null)
                return;
        }
        // Damage has to be dealt to count (an indestructible thing never catches fire).
        target.Damage(new DamageInfo { Amount = 200, DamageType = "FLAME" });
        output.WriteLine($"{target.Template.Name} aflame={target.HasStatus(Logic.Core.Status.Aflame)}");
        Assert.True(target.HasStatus(Logic.Core.Status.Aflame));
    }
}

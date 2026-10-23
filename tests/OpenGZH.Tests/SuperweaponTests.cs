using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.SpecialPowers;
using OpenGZH.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>The superweapons: firing, flight and damage.</summary>
public class SuperweaponTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static Vector3 Ground(GameLogic game, Vector3 p) => p with { Z = game.Terrain.GroundHeight(p) };

    (GameLogic, Logic.Players.Player Alice, Logic.Players.Player Bob, GameObject Silo, Vector3 Target, List<GameObject> Victims) Setup(string silo, string power, int victims = 4)
    {
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        alice.Energy.Adjust(1000, adding: true);
        var cc = game.ObjectsOwnedBy(alice).First(o => o.IsKindOf("COMMANDCENTER"));
        var building = game.CreateObject(silo, alice, Ground(game, cc.Position + new Vector3(0, 220, 0)))!;
        game.TreatAsBuilt(building);
        foreach (var module in building.FindModules<SpecialPowerModule>())
            module.SetReadyFrame(game.Frame);
        var target = Ground(game, cc.Position + new Vector3(700, 0, 0));
        var list = new List<GameObject>();
        for (int i = 0; i < victims; i++)
            list.Add(game.CreateObject("GLATankScorpion", bob, Ground(game, target + new Vector3(30 * i - 45, 20, 0)))!);
        game.Update();
        game.Commands.Enqueue(new DoSpecialPowerCommand(alice.Index, power, building.Id, [], Position: target));
        return (game, alice, bob, building, target, list);
    }

    [Fact]
    public void NuclearMissileLaunchesFliesAndDevastatesTheTarget()
    {
        if (!content.Available) return;
        var (game, _, _, silo, target, victims) = Setup("ChinaNuclearMissileLauncher", "SuperweaponNeutronMissile");
        float maxHeight = 0;
        GameObject? missile = null;
        bool sawDecal = false;
        for (int i = 0; i < 30 * 40 && victims.Any(v => !v.IsEffectivelyDead); i++)
        {
            game.Update();
            sawDecal |= game.Effects.RadiusDecals.Any(d => d.Template.Texture.Length > 0 && d.Radius > 0);
            missile ??= game.Objects.FirstOrDefault(o => o.FindModule<Logic.Modules.Behaviors.NeutronMissileUpdate>() != null);
            if (missile is { IsDestroyed: false })
                maxHeight = Math.Max(maxHeight, missile.HeightAboveTerrain);
        }
        output.WriteLine($"missile seen={missile != null}, max height {maxHeight:F0}, dead {victims.Count(v => v.IsEffectivelyDead)}/{victims.Count}, frame {game.Frame}");
        Assert.NotNull(missile);
        Assert.True(maxHeight > 100, "it climbs out of the silo");
        Assert.True(sawDecal, "a target circle marks where it will land");
        for (int i = 0; i < 30 * 3; i++)
            game.Update();
        Assert.Empty(game.Effects.RadiusDecals);
        Assert.All(victims, v => Assert.True(v.IsEffectivelyDead));
        Assert.True(silo.HasCondition("DOOR_1_WAITING_TO_CLOSE") || silo.HasCondition("DOOR_1_CLOSING") || !silo.HasCondition("DOOR_1_WAITING_OPEN"), "the silo doors close after the launch");
    }

    [Fact]
    public void ScudStormRainsMissilesOnTheTarget()
    {
        if (!content.Available) return;
        var (game, _, _, _, _, victims) = Setup("GLAScudStorm", "SuperweaponScudStorm");
        float before = victims.Sum(v => v.Health);
        int shots = 0;
        game.Events.OnWeaponFired += (sh, w, _) => { if (w.Name == "ScudStormWeapon") shots++; };
        for (int i = 0; i < 30 * 40 && victims.Any(v => !v.IsEffectivelyDead); i++)
            game.Update();
        float after = victims.Sum(v => v.IsEffectivelyDead ? 0 : v.Health);
        output.WriteLine($"shots {shots}; health {before} -> {after}, dead {victims.Count(v => v.IsEffectivelyDead)}/{victims.Count}");
        Assert.True(after < before * 0.5f, "the Scud Storm hits the target area hard");
        Assert.Equal(9, shots);
    }

    [Fact]
    public void ParticleCannonBurnsATrailThroughTheTarget()
    {
        if (!content.Available) return;
        var (game, alice, _, cannon, target, victims) = Setup("AmericaParticleCannonUplink", "SuperweaponParticleUplinkCannon");
        var update = cannon.FindModule<Logic.Modules.Behaviors.ParticleUplinkCannonUpdate>()!;
        bool sawBeam = false, sawDeployed = false;
        int steeredAt = -1;
        for (int i = 0; i < 30 * 20; i++)
        {
            game.Update();
            sawDeployed |= cannon.HasCondition("DEPLOYED");
            if (game.Effects.Beams.Any(b => b.Template.Contains("OrbitalLaser") && b.End.Z < 200 && MathUtil.Distance2D(b.End, target) < 150))
                sawBeam = true;
            // Steer the beam once it is down: it should accept the order while firing.
            if (steeredAt < 0 && sawBeam)
            {
                steeredAt = game.Frame;
                game.Commands.Enqueue(new SpecialPowerOverrideDestinationCommand(alice.Index, "SuperweaponParticleUplinkCannon", cannon.Id, target + new Vector3(0, 40, 0)));
            }
        }
        output.WriteLine($"beam seen {sawBeam}, deployed {sawDeployed}, dead {victims.Count(v => v.IsEffectivelyDead)}/{victims.Count}, frame {game.Frame}, effects left {game.Effects.Beams.Count()}/{game.Effects.ParticleSystems.Count()}");
        Assert.True(sawDeployed, "the antenna goes up");
        Assert.True(sawBeam, "the orbital beam comes down on the target");
        // The beam is about 88 wide (13 x DamageRadiusScalar 3.4 each side): it burns the tanks it passes over.
        Assert.True(victims.Count(v => v.IsEffectivelyDead) >= 2, "the beam burns the tanks");
        Assert.False(update.IsPowerCurrentlyInUse, "the beam is over");
        Assert.True(cannon.HasCondition("PACKING"), "the cannon packs up afterwards");
        Assert.Empty(game.Effects.Beams);
    }
}

using System.Numerics;
using OpenGZH.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

public class UpgradeTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void PlayerUpgradesChangeExistingAndNewObjects()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var start = game.ObjectsOwnedBy(alice).First().Position + new Vector3(80, 0, 0);

        var humvee = game.CreateObject("AmericaVehicleHumvee", alice, start)!;
        int towBit = Flags.WeaponSet.IndexOf("PLAYER_UPGRADE");
        Assert.False(humvee.WeaponSetFlags.Has(towBit));

        alice.CompleteUpgrade("Upgrade_AmericaTOWMissile");
        alice.CompleteUpgrade("Upgrade_AmericaAdvancedTraining");
        Assert.True(humvee.WeaponSetFlags.Has(towBit), "TOW missile upgrade switches the Humvee's weapon set");
        Assert.Equal(2f, humvee.ExperienceScalar);

        // Objects made after the upgrade get it straight away.
        var second = game.CreateObject("AmericaVehicleHumvee", alice, start + new Vector3(30, 0, 0))!;
        Assert.True(second.WeaponSetFlags.Has(towBit));
    }

    [Fact]
    public void DroneUpgradesConflict()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var start = game.ObjectsOwnedBy(alice).First().Position + new Vector3(80, 0, 0);
        var humvee = game.CreateObject("AmericaVehicleHumvee", alice, start)!;
        int before = game.Objects.Count();

        humvee.GiveUpgrade("Upgrade_AmericaScoutDrone");
        game.Update();
        int afterScout = game.Objects.Count();
        humvee.GiveUpgrade("Upgrade_AmericaBattleDrone");
        game.Update();
        output.WriteLine($"objects {before} -> {afterScout} -> {game.Objects.Count()}");
        Assert.True(afterScout > before, "the scout drone upgrade creates a drone");
        Assert.Equal(afterScout, game.Objects.Count());
    }

    [Fact]
    public void ControlRodsAddPower()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var start = game.ObjectsOwnedBy(alice).First().Position + new Vector3(150, 0, 0);
        var plant = game.CreateObject("AmericaPowerPlant", alice, start)!;
        int bonusBefore = alice.Energy.Bonus;

        alice.CompleteUpgrade("Upgrade_AmericaAdvancedControlRods");
        Assert.Equal(bonusBefore + plant.Template.EnergyBonus, alice.Energy.Bonus);

        game.DestroyObject(plant);
        game.Update();
        Assert.Equal(bonusBefore, alice.Energy.Bonus);
    }
}

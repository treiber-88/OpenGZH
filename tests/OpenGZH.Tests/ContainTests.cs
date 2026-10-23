using System.Numerics;
using OpenGZH.Logic;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

public class ContainTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static Vector3 Ground(GameLogic game, Vector3 p) => p with { Z = game.Terrain.GroundHeight(p) };

    static void Run(GameLogic game, int seconds)
    {
        for (int i = 0; i < 30 * seconds; i++)
            game.Update();
    }

    [Fact]
    public void RangerBoardsHumveeAndGetsOut()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var start = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter").Position + new Vector3(160, 0, 0);
        var humvee = game.CreateObject("AmericaVehicleHumvee", alice, Ground(game, start))!;
        var ranger = game.CreateObject("AmericaInfantryRanger", alice, Ground(game, start + new Vector3(60, 30, 0)))!;

        game.Commands.Enqueue(new EnterCommand(alice.Index, [ranger.Id], humvee.Id));
        Run(game, 10);
        output.WriteLine($"ranger contained={ranger.IsContained} hidden={ranger.IsHiddenInContainer} humvee count={humvee.Contain!.Count}");
        Assert.True(ranger.IsContained, "the Ranger walks to the Humvee and gets in");
        Assert.Equal(1, humvee.Contain.Count);

        game.Commands.Enqueue(new EvacuateCommand(alice.Index, [humvee.Id]));
        Run(game, 3);
        Assert.False(ranger.IsContained, "evacuate lets the Ranger out");
        Assert.False(ranger.IsHiddenInContainer);
    }

    [Fact]
    public void ParadropLandsRangers()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        alice.GrantScience("SCIENCE_Paradrop1");
        var commandCenter = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter");
        var target = Ground(game, commandCenter.Position + new Vector3(300, 200, 0));
        int rangersBefore = game.ObjectsOwnedBy(alice).Count(o => o.Template.Name == "AmericaInfantryRanger");

        game.Commands.Enqueue(new DoSpecialPowerCommand(alice.Index, "SuperweaponParadropAmerica", commandCenter.Id, [], Position: target));
        GameObject? plane = null;
        int landed = 0;
        for (int second = 0; second < 90; second++)
        {
            Run(game, 1);
            plane ??= game.ObjectsOwnedBy(alice).FirstOrDefault(o => o.Template.Name == "AmericaJetCargoPlane");
            var rangers = game.ObjectsOwnedBy(alice).Where(o => o.Template.Name == "AmericaInfantryRanger").ToList();
            landed = rangers.Count(r => !r.IsContained && !r.IsEffectivelyDead && !r.IsAboveTerrain);
            if (second % 10 == 0)
                output.WriteLine($"t={second}s plane={(plane is { IsDestroyed: false } ? plane.Position.ToString() : "gone")} rangers={rangers.Count} landed={landed}");
            if (landed - rangersBefore >= 5)
                break;
        }
        Assert.NotNull(plane);
        Assert.True(landed - rangersBefore >= 5, "five Rangers parachute down and land");
        var down = game.ObjectsOwnedBy(alice).Where(o => o.Template.Name == "AmericaInfantryRanger").ToList();
        foreach (var r in down)
            output.WriteLine($"ranger at {r.Position}, {Logic.Core.MathUtil.Distance2D(r.Position, target):F0} from target");
        Assert.All(down, r => Assert.True(Logic.Core.MathUtil.Distance2D(r.Position, target) < 250, "lands near the target"));
    }

    [Fact]
    public void RangersGarrisonCivilianBuilding()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var building = game.Objects.Where(o => o.Contain is { IsGarrisonable: true } && !o.Owner.IsHuman && o.Owner.Name != "Bob")
            .OrderBy(o => Logic.Core.MathUtil.DistanceSquared2D(o.Position, alice.StartPosition ?? Vector3.Zero)).First();
        var near = building.Position + new Vector3(building.BoundingCircleRadius + 60, 0, 0);
        near = game.Pathfinder.NearestPassable(near, Logic.Locomotion.Surfaces.Ground) ?? near;
        var ranger = game.CreateObject("AmericaInfantryRanger", alice, Ground(game, near))!;
        output.WriteLine($"valid={building.Contain!.IsValidContainerFor(ranger, true)} max={building.Contain.ContainMax} radius={building.BoundingCircleRadius}");
        game.Commands.Enqueue(new EnterCommand(alice.Index, [ranger.Id], building.Id));
        for (int s = 0; s < 15; s++)
        {
            Run(game, 1);
            output.WriteLine($"  t={s} path={ranger.AI!.Path.Count} loco={ranger.AI.CurrentLocomotor} vel={ranger.FindModule<Logic.Modules.Updates.PhysicsBehavior>()?.Velocity} pos={ranger.Position} cell={game.Pathfinder.TypeAt(game.Terrain.CellAt(ranger.Position.X, ranger.Position.Y).X, game.Terrain.CellAt(ranger.Position.X, ranger.Position.Y).Y)} state={ranger.AI!.State} dist={Logic.Core.MathUtil.Distance2D(ranger.Position, building.Position):F0} contained={ranger.IsContained}");
            if (ranger.IsContained) break;
        }
        output.WriteLine($"{building.Template.Name}: ranger contained={ranger.IsContained} building owner={building.Owner}");
        Assert.True(ranger.IsContained, "the Ranger garrisons the building");
        Assert.Equal(alice, building.Owner);
    }
}

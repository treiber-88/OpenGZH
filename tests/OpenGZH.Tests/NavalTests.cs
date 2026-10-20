using System.Numerics;
using OpenGZH.Formats.Maps;
using OpenGZH.Logic;
using OpenGZH.Logic.Construction;
using OpenGZH.Logic.Modules.AI;
using OpenGZH.Logic.Modules.Production;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>The OpenGZH naval addition: shipyards on the shore, ships on the water.</summary>
public class NavalTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    const string CoastalMap = "Maps/Bombardment Beach/Bombardment Beach.map";

    [Fact]
    public void ShipRulesLoadForEveryFaction()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish(CoastalMap);
        foreach (string name in new[] {
                     "AmericaShipyard", "AirF_AmericaShipyard", "Lazr_AmericaShipyard", "SupW_AmericaShipyard",
                     "ChinaShipyard", "Tank_ChinaShipyard", "Infa_ChinaShipyard", "Nuke_ChinaShipyard",
                     "GLAShipyard", "Demo_GLAShipyard", "Slth_GLAShipyard", "Chem_GLAShipyard",
                     "AmericaShipPTBoat", "AmericaShipBattleship", "AmericaShipAircraftCarrier",
                     "ChinaShipPatrolJunk", "ChinaShipGunboat", "GLAShipArmedTrawler", "GLAShipBombBoat" })
        {
            var template = game.Templates.Find(name);
            Assert.True(template != null, $"{name} is missing");
            if (name.Contains("Shipyard"))
                Assert.True(template!.Is("SHIPYARD"), $"{name} is not a SHIPYARD");
            else
                Assert.True(template!.Is("BOAT"), $"{name} is not a BOAT");
        }
        // Every dozer/worker has the shipyard button.
        Assert.Equal("Command_ConstructAmericaShipyard", game.Rules.Get("CommandSets", "AmericaDozerCommandSet")!.GetString("12"));
        Assert.Equal("Command_ConstructChinaShipyard", game.Rules.Get("CommandSets", "ChinaDozerCommandSet")!.GetString("13"));
        Assert.Equal("Command_ConstructGLAShipyard", game.Rules.Get("CommandSets", "GLAWorkerCommandSet")!.GetString("12"));
    }

    [Fact]
    public void DozerBuildsShipyardOnShoreAndPTBoatSails()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish(CoastalMap);
        var alice = game.Players.Find("Alice")!;
        alice.Money.Deposit(20000);
        var dozer = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaVehicleDozer");
        var shipyard = game.Templates.Find("AmericaShipyard")!;

        // Search outward from the dozer for the nearest legal shoreline spot, facing each direction.
        (Vector3 Position, float Angle)? site = null;
        for (float radius = 50; radius < 3000 && site == null; radius += 25)
            for (float a = 0; a < MathF.Tau && site == null; a += 0.2f)
                for (float facing = 0; facing < MathF.Tau && site == null; facing += MathF.PI / 4)
                {
                    var p = dozer.Position + new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * radius;
                    if (game.Building.CheckLocation(shipyard, p, facing, alice, dozer) == BuildLocation.Ok)
                        site = (p, facing);
                }
        if (site == null)
        {
            int waterCells = 0;
            for (int y = 0; y < game.Terrain.CellsY; y++)
                for (int x = 0; x < game.Terrain.CellsX; x++)
                    if (game.Terrain.IsWaterCell(x, y)) waterCells++;
            output.WriteLine($"{game.Terrain.WaterAreas.Count} water areas ({string.Join(", ", game.Terrain.WaterAreas.Select(w => $"{w.Polygon.Name} h={w.Height} pts={w.Polygon.Points.Count}"))}), {waterCells} water cells");
            var reasons = new Dictionary<BuildLocation, int>();
            for (float radius = 50; radius < 3000; radius += 50)
                for (float a = 0; a < MathF.Tau; a += 0.3f)
                {
                    var p = dozer.Position + new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * radius;
                    var r = game.Building.CheckLocation(shipyard, p, 0, alice, dozer);
                    reasons[r] = reasons.GetValueOrDefault(r) + 1;
                }
            output.WriteLine(string.Join(", ", reasons.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        Assert.True(site != null, "no legal shoreline location found");
        output.WriteLine($"shipyard site {site!.Value.Position} facing {site.Value.Angle}");

        // A shipyard may not be placed fully on land (the dozer starts inland, next to its base).
        Assert.NotEqual(BuildLocation.Ok, game.Building.CheckLocation(shipyard, dozer.Position, 0, alice, dozer));

        // The shipyard needs a supply center first.
        var supply = game.CreateObject("AmericaSupplyCenter", alice, dozer.Position + new Vector3(-200, 0, 0));
        var structure = game.Building.StartConstruction(shipyard, site.Value.Position, site.Value.Angle, alice, dozer);
        Assert.NotNull(structure);
        Assert.True(structure!.IsUnderConstruction);

        int frames = 0;
        while (structure.IsUnderConstruction && frames++ < 30 * 180)
            game.Update();
        output.WriteLine($"shipyard built in {frames / 30f:F1}s");
        Assert.False(structure.IsUnderConstruction);

        var production = structure.FindModule<ProductionUpdate>()!;
        var boat = game.Templates.Find("AmericaShipPTBoat")!;
        Assert.Null(production.CanQueueUnit(boat));
        production.QueueUnit(boat);
        for (int i = 0; i < 30 * 15; i++)
            game.Update();

        var ptBoat = game.ObjectsOwnedBy(alice).FirstOrDefault(o => o.Template.Name == "AmericaShipPTBoat");
        Assert.NotNull(ptBoat);
        Assert.True(game.Terrain.IsUnderwater(ptBoat!.Position.X, ptBoat.Position.Y, out _), $"the PT boat should be on water, is at {ptBoat.Position}");

        // Sail somewhere else on the water and check it stays on water the whole way.
        var start = ptBoat.Position;
        var destination = start + MathUtil(ptBoat.Orientation) * 300;
        destination = game.Pathfinder.NearestPassable(destination, Logic.Locomotion.Surfaces.Water) ?? destination;
        ptBoat.AI!.MoveTo(destination);
        for (int i = 0; i < 30 * 30; i++)
        {
            game.Update();
            Assert.True(game.Terrain.IsUnderwater(ptBoat.Position.X, ptBoat.Position.Y, out _), "the boat left the water");
        }
        output.WriteLine($"PT boat moved from {start} to {ptBoat.Position} (asked for {destination})");
        Assert.True(Vector3.Distance(start, ptBoat.Position) > 100);
    }

    static Vector3 MathUtil(float angle) => new(MathF.Cos(angle), MathF.Sin(angle), 0);
}

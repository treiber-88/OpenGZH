using System.Numerics;
using OpenGZH.Core.FileSystem;
using OpenGZH.Core.Mods;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Maps;
using OpenGZH.Logic;
using OpenGZH.Logic.Modules.Production;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Loads the base game once for all simulation tests (they need the imported content).</summary>
public sealed class BaseGameFixture : IDisposable
{
    public VirtualFileSystem? FileSystem { get; }
    public RuleSet? Rules { get; }

    public BaseGameFixture()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "mods", "zh", "mod.yaml")))
            dir = Path.GetDirectoryName(dir);
        if (dir == null || !Directory.Exists(Path.Combine(dir, "content")))
            return;

        var manifest = ModManifest.Load(Path.Combine(dir, "mods", "zh", "mod.yaml"));
        FileSystem = manifest.MountPackages(dir);
        Rules = RuleSet.Load(manifest);
        Models = new Formats.W3d.W3dLibrary(FileSystem);
    }

    public Formats.W3d.W3dLibrary? Models { get; }

    public bool Available => FileSystem != null && Rules != null;

    public GameLogic NewSkirmish(string map = "Maps/Tournament Desert/Tournament Desert.map")
    {
        var mapFile = MapFile.Read(FileSystem!.ReadAllBytes(map));
        return GameSetup.FromMap(mapFile, Rules!, seed: 1234,
        [
            new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionChina", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: Models);
    }

    public void Dispose() => FileSystem?.Dispose();
}

public class SimulationTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static void Run(GameLogic game, int frames)
    {
        for (int i = 0; i < frames; i++)
            game.Update();
    }

    [Fact]
    public void SkirmishStartsWithCommandCentersAndDozers()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;

        Assert.Contains(game.ObjectsOwnedBy(alice), o => o.Template.Name == "AmericaCommandCenter");
        Assert.Contains(game.ObjectsOwnedBy(alice), o => o.Template.Name == "AmericaVehicleDozer");
        Assert.Contains(game.ObjectsOwnedBy(bob), o => o.Template.Name == "ChinaCommandCenter");
        Assert.True(alice.IsEnemyOf(bob));
        Assert.Equal(10000, alice.Money.Amount);

        Run(game, 30);
        output.WriteLine($"{game.Objects.Count} objects after 1 second");
    }

    [Fact]
    public void TankShellsBarelyHurtInfantry()
    {
        // Crusader shells do 60 armor-piercing damage; infantry armor takes 10% of that.
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var (alice, bob, center) = Opponents(game);
        var tank = game.CreateObject("AmericaTankCrusader", alice, center)!;
        var infantry = game.CreateObject("ChinaInfantryRedguard", bob, center + new Vector3(100, 0, 0))!;
        infantry.SetStatus("UNSELECTABLE"); // keep the test about armor, not AI
        tank.AI!.AttackObject(infantry);

        // The shell flies for about 8 frames (100 units at 400 units/s); the next shot is 2 s away.
        Run(game, 30);
        Assert.Equal(infantry.MaxHealth - 6, infantry.Health, 0.01f);
        Assert.DoesNotContain(game.Objects, o => o.Template.Name == "GenericTankShell");
    }

    [Fact]
    public void TankDuelEndsWithOneDestroyed()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var (alice, bob, center) = Opponents(game);
        var crusader = game.CreateObject("AmericaTankCrusader", alice, center)!;
        var battlemaster = game.CreateObject("ChinaTankBattleMaster", bob, center + new Vector3(120, 0, 0))!;
        crusader.AI!.AttackObject(battlemaster);
        battlemaster.AI!.AttackObject(crusader);

        int frames = 0;
        while (!crusader.IsEffectivelyDead && !battlemaster.IsEffectivelyDead && frames++ < 30 * 90)
            game.Update();

        output.WriteLine($"after {frames / 30f:F1}s: Crusader {crusader.Health}/{crusader.MaxHealth}, Battlemaster {battlemaster.Health}/{battlemaster.MaxHealth}");
        Assert.True(crusader.IsEffectivelyDead || battlemaster.IsEffectivelyDead);
        // The Crusader is the stronger tank (more health, same gun damage).
        Assert.True(battlemaster.IsEffectivelyDead);
    }

    static (Logic.Players.Player, Logic.Players.Player, Vector3) Opponents(GameLogic game)
    {
        var center = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        center.Z = game.Terrain.GroundHeight(center);
        return (game.Players.Find("Alice")!, game.Players.Find("Bob")!, center);
    }

    [Fact]
    public void WarFactoryBuildsCrusader()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var commandCenter = game.ObjectsOwnedBy(alice).First(o => o.Template.Name == "AmericaCommandCenter");

        var factoryPosition = commandCenter.Position + new Vector3(150, 0, 0);
        var factory = game.CreateObject("AmericaWarFactory", alice, factoryPosition with { Z = game.Terrain.GroundHeight(factoryPosition) })!;
        var production = factory.FindModule<ProductionUpdate>()!;

        int before = alice.Money.Amount;
        var crusader = game.Templates.Find("AmericaTankCrusader")!;
        Assert.Null(production.CanQueueUnit(crusader));
        Assert.NotNull(production.QueueUnit(crusader));
        Assert.Equal(before - 900, alice.Money.Amount);

        Run(game, (int)(crusader.BuildTimeSeconds * 30) + 30);
        var built = game.ObjectsOwnedBy(alice).Where(o => o.Template.Name == "AmericaTankCrusader").ToList();
        Assert.Single(built);
        output.WriteLine($"Crusader built at {built[0].Position}, factory at {factory.Position}");
    }
}

public class StringTableTests(BaseGameFixture content) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void OriginalStringTableLoads()
    {
        if (!content.Available) return;
        var table = new OpenGZH.Formats.Strings.StringTable();
        table.LoadCsf(content.FileSystem!.ReadAllBytes("Data/English/generals.csf"));
        Assert.True(table.Count > 5000, $"only {table.Count} strings");
        Assert.Equal("Crusader", table.Get("OBJECT:Crusader"));
    }
}

public class WndTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void EveryOriginalLayoutParses()
    {
        if (!content.Available) return;
        int files = 0, windows = 0;
        foreach (string path in content.FileSystem!.List("Window", ".wnd", recursive: true))
        {
            var roots = OpenGZH.Formats.Wnd.WndFile.Parse(content.FileSystem.ReadAllText(path));
            Assert.NotEmpty(roots);
            files++;
            windows += Count(roots);
        }
        output.WriteLine($"{files} layouts, {windows} windows");
        var bar = OpenGZH.Formats.Wnd.WndFile.Parse(content.FileSystem.ReadAllText("Window/ControlBar.wnd"))[0];
        output.WriteLine($"{bar.Name} {bar.X},{bar.Y} {bar.Width}x{bar.Height} children {bar.Children.Count}");
        Assert.Equal("ControlBar.wnd:ControlBarParent", bar.Name);
        Assert.Equal(799, bar.Width); // the original layout spans 0..799
    }

    static int Count(List<OpenGZH.Formats.Wnd.WndWindow> list) => list.Sum(w => 1 + Count(w.Children));
}

public class EconomyTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void ChinaSupplyTruckEarnsMoney()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var bob = game.Players.Find("Bob")!;
        var commandCenter = game.ObjectsOwnedBy(bob).First(o => o.Template.Name == "ChinaCommandCenter");

        // Build a supply center next to the nearest supply dock, then a truck.
        var dock = game.Objects.Where(o => o.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>() != null)
            .OrderBy(o => Logic.Core.MathUtil.DistanceSquared2D(o.Position, commandCenter.Position)).First();
        var centerPosition = dock.Position + Vector3.Normalize(commandCenter.Position - dock.Position) * 120;
        var center = game.CreateObject("ChinaSupplyCenter", bob, centerPosition with { Z = game.Terrain.GroundHeight(centerPosition) })!;
        var truck = game.CreateObject("ChinaVehicleSupplyTruck", bob, centerPosition + new Vector3(60, 0, 0))!;
        int before = bob.Money.Amount;

        for (int i = 0; i < 30 * 90; i++)
            game.Update();
        output.WriteLine($"money {before} -> {bob.Money.Amount}, dock {dock.Template.Name} at distance {Logic.Core.MathUtil.Distance2D(dock.Position, center.Position):F0}");
        Assert.True(bob.Money.Amount > before, "the supply truck should have delivered at least once in 90 seconds");
    }
}

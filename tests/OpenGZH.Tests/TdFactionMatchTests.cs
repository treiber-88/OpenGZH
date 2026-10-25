using OpenGZH.Formats.Maps;
using OpenGZH.Logic;
using OpenGZH.Logic.AI;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Behaviors;
using OpenGZH.Logic.Modules.Production;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Tiberian Dawn's GDI and Nod as factions in Zero Hour engine matches (mods/zh/rules/td).</summary>
public class TdFactionMatchTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    GameLogic NewMatch(string alice, string bob, bool aliceHuman = true, string map = "Maps/Tournament Desert/Tournament Desert.map")
    {
        var mapFile = MapFile.Read(content.FileSystem!.ReadAllBytes(map));
        return GameSetup.FromMap(mapFile, content.Rules!, seed: 1234,
        [
            new PlayerSlot("Alice", alice, IsHuman: aliceHuman, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", bob, IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    }

    [Fact]
    public void LobbyOffersTdFactionsAndStartsTheMatch()
    {
        if (!content.Available) return;
        var templates = Logic.Multiplayer.GameInfo.PlayerTemplates(content.Rules!);
        int nod = templates.First(t => t.Name == "FactionTDNod").Index;
        int gdi = templates.First(t => t.Name == "FactionTDGDI").Index;
        Assert.True(Logic.Multiplayer.GameInfo.IsPlayableTemplate(templates[nod].Rules));

        var cache = Logic.Multiplayer.MapCache.Load(content.FileSystem!);
        var info = new Logic.Multiplayer.GameInfo { Map = "Maps/Tournament Desert/Tournament Desert.map", Seed = 7, StartingCash = 10000 };
        info.Slots[0].SetState(Logic.Multiplayer.SlotState.Player, "Alice");
        info.Slots[0].PlayerTemplate = nod;
        info.Slots[1].SetState(Logic.Multiplayer.SlotState.MedAI, "Bob");
        info.Slots[1].PlayerTemplate = gdi;
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes(info.Map));
        var game = GameSetup.FromGameInfo(map, content.Rules!, info, cache, content.Models,
            p => content.FileSystem.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        Assert.Contains(game.ObjectsOwnedBy(alice), o => o.Template.Name == "TDNOD_MCV");
        Assert.Contains(game.ObjectsOwnedBy(bob), o => o.Template.Name == "TDGDI_MCV");
        Assert.IsType<TdFactionAI>(bob.AI);
        Assert.Null(alice.AI);
    }

    [Fact]
    public void SidebarListsWhatTheYardCanBuild()
    {
        if (!content.Available) return;
        var game = NewMatch("FactionTDGDI", "FactionChina");
        var alice = game.Players.Find("Alice")!;
        var mcv = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == "TDGDI_MCV");
        Assert.True(mcv.FindModule<TdDeployToStructure>()!.Deploy());
        var yard = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == "TDGDI_FACT");
        foreach (string name in Logic.Templates.CommandSets.BuildableObjects(game.Catalog, yard.CommandSetName, "UNIT_BUILD"))
        {
            var t = game.Templates.Find(name);
            output.WriteLine($"{name}: found {t != null} canBuild {t != null && alice.CanBuild(t)} prereq {t != null && Prerequisites.AreMet(game, alice, t.Rules)}");
        }
        var panel = new Client.TiberianDawn.TdSidebarPanel(game, alice, null!);
        var structures = panel.Column(0);
        output.WriteLine("structures: " + string.Join(", ", structures.Select(i => i.Template.Name)));
        Assert.Contains(structures, i => i.Template.Name == "TDGDI_NUKE");
    }

    [Fact]
    public void McvDeploysAtEveryStartOfTheSkirmishMaps()
    {
        if (!content.Available) return;
        var cache = Logic.Multiplayer.MapCache.Load(content.FileSystem!);
        var failures = new List<string>();
        foreach (var meta in cache.Maps.Where(m => m.IsMultiplayer && content.FileSystem!.Exists(m.FileName)).OrderBy(m => m.FileName))
        {
            var mapFile = MapFile.Read(content.FileSystem!.ReadAllBytes(meta.FileName));
            for (int start = 1; start <= meta.NumPlayers; start++)
            {
                var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 1,
                [
                    new PlayerSlot("Alice", "FactionTDGDI", IsHuman: true, StartPosition: start, Color: 0x0000FF),
                ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
                var alice = game.Players.Find("Alice")!;
                var mcv = game.ObjectsOwnedBy(alice).FirstOrDefault(o => o.Template.Name == "TDGDI_MCV");
                if (mcv == null)
                {
                    failures.Add($"{meta.FileName} start {start}: no MCV");
                    continue;
                }
                var yard = game.Templates.Find("TDGDI_FACT")!;
                var result = game.Building.CheckLocation(yard, mcv.Position, 0, alice, mcv);
                if (result != Logic.Construction.BuildLocation.Ok)
                {
                    var near = game.ObjectsInRadius(mcv.Position, 160).Where(o => o != mcv && (o.IsKindOf(Kind.Structure) || o.IsKindOf(Kind.Immobile)))
                        .Select(o => $"{o.Template.Name}@{Logic.Core.MathUtil.Distance2D(o.Position, mcv.Position):F0}");
                    failures.Add($"{meta.FileName} start {start}: {result} near {string.Join(" ", near)}");
                }
            }
        }
        foreach (var f in failures)
            output.WriteLine(f);
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("FactionTDGDI", "TDGDI_")]
    [InlineData("FactionTDNod", "TDNOD_")]
    public void HumanHarvesterCollectsZeroHourSupplies(string faction, string prefix)
    {
        if (!content.Available) return;
        // A TD computer opponent: it doesn't attack in the first five minutes.
        var game = NewMatch(faction, "FactionTDGDI");
        var alice = game.Players.Find("Alice")!;
        Assert.True(game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == prefix + "MCV").FindModule<TdDeployToStructure>()!.Deploy());
        var yard = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == prefix + "FACT");
        var production = yard.FindModule<ProductionUpdate>()!;
        // A power plant, then a refinery, each placed next to the base as a player would.
        foreach (string name in new[] { "NUKE", "PROC" })
        {
            var template = game.Templates.Find(prefix + name)!;
            var entry = production.QueueUnit(template)!;
            for (int i = 0; i < 120 * LogicTime.FramesPerSecond && production.ReadyStructure == null; i++)
                game.Update();
            Assert.NotNull(production.ReadyStructure);
            Logic.Objects.GameObject? placed = null;
            for (float r = 40; r < 400 && placed == null; r += 18)
                for (int k = 0; k < 16 && placed == null; k++)
                {
                    var p = yard.Position + new System.Numerics.Vector3(MathF.Cos(k * MathF.Tau / 16), MathF.Sin(k * MathF.Tau / 16), 0) * r;
                    placed = production.PlaceReadyStructure(entry.Id, p with { Z = game.Terrain.GroundHeight(p) }, 0);
                }
            Assert.NotNull(placed);
        }
        int before = alice.Money.Amount;
        for (int i = 0; i < 150 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            if (i % (30 * LogicTime.FramesPerSecond) == 0)
                output.WriteLine($"{i / LogicTime.FramesPerSecond}s: " + string.Join(", ", game.ObjectsOwnedBy(alice).GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
        }
        var harvester = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == prefix + "HARV");
        output.WriteLine($"money {before} -> {alice.Money.Amount}, earned {alice.Score.TotalMoneyEarned}; harvester {harvester.Position} {harvester.AI!.State}");
        foreach (var dock in game.Objects.Where(o => o.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>() != null))
            output.WriteLine($"supply {dock.Template.Name} at {dock.Position} distance {Logic.Core.MathUtil.Distance2D(dock.Position, harvester.Position):F0} boxes {dock.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>()!.Boxes}");
        var g = Logic.Modules.Supply.SupplyGatherer.Of(harvester)!;
        output.WriteLine($"gatherer {g.State} scan {g.WarehouseScanDistance} best {g.FindBestSupplyWarehouse()?.Position}");
        Assert.True(alice.Score.TotalMoneyEarned > 0, "the harvester brings in supplies");
    }

    [Fact]
    public void TdFactionsLoadAsZeroHourRules()
    {
        if (!content.Available) return;
        var game = NewMatch("FactionTDGDI", "FactionTDNod");
        foreach (var name in new[] { "TDGDI_MCV", "TDGDI_FACT", "TDGDI_MTNK", "TDNOD_OBLI", "TDNOD_HAND", "TD_Tiberium12" })
            Assert.NotNull(game.Templates.Find(name));
        Assert.Equal("Mammoth Tank", game.Templates.Find("TDGDI_HTNK")!.DisplayName);
    }

    [Fact]
    public void TdFactionStartsWithAnMcvThatDeploys()
    {
        if (!content.Available) return;
        var game = NewMatch("FactionTDGDI", "FactionAmerica");
        var alice = game.Players.Find("Alice")!;
        var mcv = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == "TDGDI_MCV");
        Assert.DoesNotContain(game.ObjectsOwnedBy(alice), o => o.IsKindOf(Kind.Structure));
        Assert.True(mcv.FindModule<TdDeployToStructure>()!.Deploy());
        game.Update();
        var yard = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == "TDGDI_FACT");
        Assert.False(yard.IsUnderConstruction);

        // Build a power plant at the yard, then place it next to the yard.
        var production = yard.FindModule<ProductionUpdate>()!;
        alice.Money.Deposit(5000);
        var entry = production.QueueUnit(game.Templates.Find("TDGDI_NUKE")!);
        Assert.NotNull(entry);
        for (int i = 0; i < 60 * LogicTime.FramesPerSecond && production.ReadyStructure == null; i++)
            game.Update();
        Assert.NotNull(production.ReadyStructure);
        var far = yard.Position + new System.Numerics.Vector3(300, 0, 0);
        Assert.Null(production.PlaceReadyStructure(entry!.Id, far, 0)); // TD: must touch the base
        var plantTemplate = game.Templates.Find("TDGDI_NUKE")!;
        var near = yard.Position + new System.Numerics.Vector3(yard.Geometry.MajorRadius + plantTemplate.GeometryMajorRadius + 4, 0, 0);
        var plant = production.PlaceReadyStructure(entry.Id, near, 0);
        Assert.NotNull(plant);
        Assert.Empty(production.Queue);
    }

    [Theory]
    [InlineData("FactionTDGDI", "FactionChina")]
    [InlineData("FactionTDNod", "FactionTDGDI")]
    public void TdComputerPlayerBuildsABaseAndAnArmy(string bobFaction, string aliceFaction)
    {
        if (!content.Available) return;
        var game = NewMatch(aliceFaction, bobFaction);
        var bob = game.Players.Find("Bob")!;
        Assert.IsType<TdFactionAI>(bob.AI);
        bool trace = Environment.GetEnvironmentVariable("OPENGZH_TD_TRACE") == "1";
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("OPENGZH_AI_MINUTES"), out int m) ? m : 6;
        for (int i = 0; i < minutes * 60 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            if (i % (60 * LogicTime.FramesPerSecond) == 0)
                output.WriteLine($"{i / LogicTime.FramesPerSecond}s: Bob ${bob.Money.Amount}, "
                    + string.Join(", ", game.ObjectsOwnedBy(bob).GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
            if (trace && i % (5 * LogicTime.FramesPerSecond) == 0)
                foreach (var h in game.ObjectsOwnedBy(bob).Where(o => o.AI is Logic.Modules.Supply.SupplyTruckAIUpdate))
                    output.WriteLine($"  {i / LogicTime.FramesPerSecond}s harvester {h.Id} {h.Position} {((Logic.Modules.Supply.SupplyTruckAIUpdate)h.AI!).Gatherer.State} {h.AI!.DockStepName} ${bob.Money.Amount}");
        }
        var mine = game.ObjectsOwnedBy(bob).ToList();
        output.WriteLine("end: " + string.Join(", ", mine.GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
        if (trace)
            foreach (var st in mine.Where(o => o.IsKindOf(Kind.Structure)))
                output.WriteLine($"  structure {st.Template.Name} at {st.Position} half {st.Geometry.MajorRadius}x{st.Geometry.MinorRadius} angle {st.Orientation}");
        foreach (var truck in mine.Where(o => o.AI is Logic.Modules.Supply.SupplyTruckAIUpdate))
        {
            var g = ((Logic.Modules.Supply.SupplyTruckAIUpdate)truck.AI!).Gatherer;
            var dockWith = game.FindObject(truck.AI!.DockTargetId);
            output.WriteLine($"  docking with {dockWith?.Template.Name} at {dockWith?.Position} r {dockWith?.Geometry.MajorRadius}x{dockWith?.Geometry.MinorRadius} dockpos {dockWith?.FindModule<Logic.Modules.Dock.DockUpdate>()?.GetDockPosition(truck)}");
            output.WriteLine($"harvester {truck.Id} at {truck.Position} state {g.State} step {truck.AI!.DockStepName} boxes {g.Boxes} pref {g.PreferredCenterId} ai {truck.AI!.State}");
        }
        Assert.Contains(mine, o => o.IsKindOf("COMMANDCENTER"));
        Assert.True(mine.Count(o => o.IsKindOf(Kind.Structure)) >= 4, "the TD computer player builds a base");
        Assert.Contains(mine, o => TdFactionAI.TdTypeOf(o.Template) == "STRUCT_REFINERY");
        Assert.Contains(mine, o => TdFactionAI.TdTypeOf(o.Template) == "UNIT_HARVESTER");
        Assert.True(bob.Score.TotalMoneyEarned > 0, "harvesters bring in money");
        Assert.True(mine.Count(o => o.IsMobile && TdFactionAI.TdTypeOf(o.Template) != "UNIT_HARVESTER") >= 3, "and trains units");
    }
}

/// <summary>Walking infantry: the walk cycle's rate follows the soldier's speed (W3DModelDraw::adjustAnimSpeedToMovementSpeed).</summary>
public class InfantryWalkTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void RangerWalkCycleCoversItsDistanceAtWalkingSpeed()
    {
        if (!content.Available) return;
        var anim = content.Models!.GetAnimation("AIRngr_SKL.AIRngr_RNA");
        Assert.NotNull(anim);
        float speedPerFrame = 20f / LogicTime.FramesPerSecond; // BasicHumanLocomotor Speed 20
        float rate = speedPerFrame * anim!.FrameCount / 30f * LogicTime.FramesPerSecond / anim.FrameRate;
        output.WriteLine($"frames {anim.FrameCount} at {anim.FrameRate} fps; plays at x{rate:F2} when walking");
        Assert.InRange(rate, 0.2f, 5f);
    }
}

/// <summary>TD weapons show TD's own muzzle flashes, bullets and explosions.</summary>
public class TdWeaponFxTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void TdShotsRaiseTdAnimationsThatExist()
    {
        if (!content.Available || OpenGZH.Client.TiberianDawnContent.Art is not { } art) return;
        var td = OpenGZH.Client.TiberianDawnContent.Get()!;
        var types = new OpenGZH.TiberianDawn.Logic.Types.TdTypes(td.Rules);
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionTDNod", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionTDGDI", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var names = new List<string>();
        game.Events.OnFx += fx => { if (fx.Name.StartsWith("TDFX:")) names.Add(fx.Name); };
        game.Events.OnWeaponFireFx += fx => { if (fx.Name.StartsWith("TDFX:")) names.Add(fx.Name); };
        var middle = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        foreach (var (unit, owner, dx) in new[] { ("TDNOD_LTNK", "Alice", -60f), ("TDNOD_E1", "Alice", -50f), ("TDGDI_MTNK", "Bob", 60f), ("TDGDI_E3", "Bob", 50f) })
        {
            var p = middle + new System.Numerics.Vector3(dx, dx > 0 ? 20 : -20, 0);
            game.CreateObject(unit, game.Players.Find(owner)!, p with { Z = game.Terrain.GroundHeight(p) });
        }
        for (int i = 0; i < 20 * LogicTime.FramesPerSecond; i++)
            game.Update();
        output.WriteLine(string.Join(", ", names.Distinct()));
        Assert.NotEmpty(names);
        foreach (string anim in names.SelectMany(n => n["TDFX:".Length..].Split('|')).Distinct().Where(a => a != "ANIM_NONE"))
        {
            var type = types.Anim(Enum.Parse<OpenGZH.TiberianDawn.Logic.AnimType>(anim));
            Assert.True(art.Shape(type.IniName) != null, $"{anim} ({type.IniName}) has art");
        }
        foreach (var bullet in types.AllBullets.Where(b => !b.IsInvisible))
            output.WriteLine($"bullet {bullet.Type} {bullet.IniName}: {(art.Shape(bullet.IniName) is { } s ? s.FrameCount + " frames" : "NO ART")}");
    }
}

public class TdArtProbe(ITestOutputHelper output)
{
    [Fact]
    public void TransportColours()
    {
        if (OpenGZH.Client.TiberianDawnContent.Art is not { } art) return;
        foreach (string name in new[] { "TRAN", "RROTOR", "LROTOR", "HELI", "MTNK" })
        {
            var shape = art.Shape(name);
            if (shape == null) { output.WriteLine($"{name}: none"); continue; }
            var px = shape.Frame(0);
            int ramp = px.Count(c => c >= 176 && c <= 191), solid = px.Count(c => c != 0 && c != 4);
            output.WriteLine($"{name}: {shape.FrameCount} frames {shape.Width}x{shape.Height}, frame 0 ramp {ramp} of {solid}");
        }
    }
}

using OpenGZH.Formats.Maps;
using OpenGZH.Logic;
using OpenGZH.Logic.Multiplayer;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>The skirmish setup behind the menus: the map cache and random slot choices.</summary>
public class SkirmishSetupTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void MapCacheListsTheOfficialSkirmishMaps()
    {
        if (!content.Available) return;
        var cache = MapCache.Load(content.FileSystem!);
        var alpine = cache.Find("Maps/Alpine Assault/Alpine Assault.map");
        output.WriteLine($"{cache.Maps.Count} maps, {cache.Maps.Count(m => m.IsMultiplayer)} multiplayer");
        Assert.NotNull(alpine);
        Assert.True(alpine!.IsMultiplayer);
        Assert.Equal(2, alpine.NumPlayers);
        Assert.Equal("MAP:AlpineAssault", alpine.NameLookupTag);
        Assert.NotNull(alpine.StartPosition(1));
        Assert.NotEmpty(alpine.SupplyPositions);
    }

    [Fact]
    public void RandomSlotsAreSettledAtGameStart()
    {
        if (!content.Available) return;
        var cache = MapCache.Load(content.FileSystem!);
        var mapMeta = cache.Maps.Where(m => m.IsMultiplayer && m.NumPlayers >= 4).OrderBy(m => m.FileName)
            .First(m => content.FileSystem!.Exists(m.FileName));
        var info = new GameInfo { Map = mapMeta.FileName, Seed = 42, StartingCash = 5000 };
        info.Slots[0].SetState(SlotState.Player, "Alice");
        info.Slots[1].SetState(SlotState.MedAI, "Bob");
        info.Slots[2].SetState(SlotState.EasyAI, "Carol");
        info.Slots[2].TeamNumber = 1;
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes(info.Map));
        var game = GameSetup.FromGameInfo(map, content.Rules!, info, cache, content.Models,
            p => content.FileSystem.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        foreach (var slot in info.Slots.Where(s => s.IsOccupied))
            output.WriteLine($"{slot.Name}: template {slot.PlayerTemplate} color {slot.Color} start {slot.StartPos}");
        var occupied = info.Slots.Where(s => s.IsOccupied).ToList();
        Assert.All(occupied, s => Assert.True(s.PlayerTemplate >= 0 && s.Color >= 0 && s.StartPos >= 0));
        Assert.Equal(occupied.Count, occupied.Select(s => s.StartPos).Distinct().Count());
        Assert.Equal(occupied.Count, occupied.Select(s => s.Color).Distinct().Count());
        Assert.Equal(5000, game.Players.Find("Alice")!.Money.Amount);
        Assert.All(new[] { "Alice", "Bob", "Carol" }, name => Assert.NotNull(game.Players.Find(name)));
    }
}

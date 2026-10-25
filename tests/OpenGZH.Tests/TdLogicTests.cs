using OpenGZH.TiberianDawn.Data;
using OpenGZH.TiberianDawn.Logic;
using OpenGZH.TiberianDawn.Logic.Types;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Tiberian Dawn's ported game logic: scenarios load into it and run.</summary>
public class TdLogicTests(TdFixture fixture, ITestOutputHelper output) : IClassFixture<TdFixture>
{
    TdGame? Load(string name)
    {
        if (fixture.Content is not { } td)
            return null;
        var game = new TdGame(new TdTypes(td.Rules));
        string ini = System.Text.Encoding.Latin1.GetString(td.Files.ReadAllBytes(name + ".INI"));
        byte[]? bin = td.Files.Exists(name + ".BIN") ? td.Files.ReadAllBytes(name + ".BIN") : null;
        string? mission = td.Files.Exists("MISSION.INI") ? System.Text.Encoding.Latin1.GetString(td.Files.ReadAllBytes("MISSION.INI")) : null;
        Assert.True(game.Set_Scenario_From_Name(name));
        Assert.True(game.Read_Scenario_Ini(name, ini, bin, true, mission));
        game.GameActive = true;
        return game;
    }

    [Fact]
    public void GdiMissionOneLoadsWithItsForces()
    {
        var game = Load("SCG01EA");
        if (game == null) return;
        output.WriteLine($"player {game.PlayerPtr.Class.IniName}, credits {game.PlayerPtr.Credits}, theater {game.Map.Theater}");
        output.WriteLine($"units {game.Units.Count}, infantry {game.Infantry.Count}, buildings {game.Buildings.Count}, aircraft {game.Aircraft.Count}, terrain {game.Terrains.Count}");
        output.WriteLine($"triggers {game.Triggers.Count}, team types {game.TeamTypes.Count}, briefing: {game.BriefingText}");
        Assert.Equal(HousesType.HOUSE_GOOD, game.PlayerPtr.Class.House);
        Assert.True(game.Units.Count + game.Infantry.Count > 0);
        Assert.True(game.Buildings.Count > 0);
        Assert.True(game.Triggers.Count > 0);
    }

    TdGame? Skirmish(string map, HousesType playerSide, HousesType enemySide, uint seed = 1234)
    {
        if (fixture.Content is not { } td)
            return null;
        var game = new TdGame(new TdTypes(td.Rules));
        Assert.True(game.Set_Multiplayer_Data(new TdSkirmishOptions(),
        [
            new TdSkirmishPlayer("Player", playerSide, PlayerColorType.REMAP_GOLD, IsAI: false, Team: 0),
            new TdSkirmishPlayer("Computer", enemySide, PlayerColorType.REMAP_RED, IsAI: true, Team: 1),
        ]));
        string ini = System.Text.Encoding.Latin1.GetString(td.Files.ReadAllBytes(map + ".INI"));
        byte[]? bin = td.Files.Exists(map + ".BIN") ? td.Files.ReadAllBytes(map + ".BIN") : null;
        Assert.True(game.Start_Skirmish(map, ini, bin, seed));
        return game;
    }

    [Theory]
    [InlineData(HousesType.HOUSE_GOOD, HousesType.HOUSE_BAD)]
    [InlineData(HousesType.HOUSE_BAD, HousesType.HOUSE_GOOD)]
    public void ASkirmishGivesEachSideAnMcvAndTheComputerBuildsABase(HousesType playerSide, HousesType enemySide)
    {
        if (fixture.Content is not { } td) return;
        string map = TdScenario.List(td.Files).First(n => n[2] is 'M' or 'm');
        var game = Skirmish(map, playerSide, enemySide)!;
        var player = game.PlayerPtr;
        var computer = game.Houses.Active.First(h => h != player && !h.IsHuman && h.Class.House >= HousesType.HOUSE_MULTI1);
        output.WriteLine($"{map}: player {player.Class.IniName} plays {player.ActLike}; computer {computer.Class.IniName} plays {computer.ActLike}");
        Assert.True(player.IsHuman);
        Assert.Equal(playerSide, player.ActLike);
        Assert.Equal(enemySide, computer.ActLike);
        Assert.Contains(game.Units.Active, u => u.House == player && u.Class.Type == UnitType.UNIT_MCV);
        Assert.Contains(game.Units.Active, u => u.House == computer && u.Class.Type == UnitType.UNIT_MCV);

        for (int frame = 0; frame < TdDefines.TICKS_PER_MINUTE * 15 && !game.PlayerLoses && !game.PlayerWins; frame++)
            game.Main_Loop();

        var built = game.Buildings.Active.Where(b => b.House == computer).GroupBy(b => b.Class.IniName).Select(g => $"{g.Key}x{g.Count()}");
        output.WriteLine($"after {game.Frame / TdDefines.TICKS_PER_SECOND}s the computer has: {string.Join(", ", built)}; units {game.Units.Active.Count(u => u.House == computer)}, infantry {game.Infantry.Active.Count(u => u.House == computer)}, credits {computer.Available_Money()}");
        Assert.True(game.Buildings.Active.Count(b => b.House == computer) >= 4, "the computer should have built a base");
    }

    [Fact]
    public void TheSkirmishPlayerBuildsAndPlacesAPowerPlant()
    {
        if (fixture.Content is not { } td) return;
        string map = TdScenario.List(td.Files).First(n => n[2] is 'M' or 'm');
        var game = Skirmish(map, HousesType.HOUSE_BAD, HousesType.HOUSE_GOOD)!;
        var player = game.PlayerPtr;
        var mcv = game.Units.Active.First(u => u.House == player && u.Class.Type == UnitType.UNIT_MCV);
        game.Queue_Mission(mcv.As_Target(), MissionType.MISSION_UNLOAD, Target.TARGET_NONE, Target.TARGET_NONE);
        for (int frame = 0; frame < TdDefines.TICKS_PER_SECOND * 10; frame++)
            game.Main_Loop();
        Assert.Contains(game.Buildings.Active, b => b.House == player && b.Class.Type == StructType.STRUCT_CONST);

        var sidebar = game.SidebarFor(player);
        output.WriteLine("buildable: " + string.Join(", ", sidebar.Column[0].Buildables.Select(b => game.Types.Building((StructType)b.BuildableID).IniName)));
        Assert.True(game.Start_Construction(RTTIType.RTTI_BUILDINGTYPE, (int)StructType.STRUCT_POWER));
        for (int frame = 0; frame < TdDefines.TICKS_PER_MINUTE && game.Pending_Building() == null; frame++)
            game.Main_Loop();
        Assert.NotNull(game.Pending_Building());

        game.Start_Placement(RTTIType.RTTI_BUILDINGTYPE, (int)StructType.STRUCT_POWER);
        int yard = TdMath.CoordCell(game.Buildings.Active.First(b => b.House == player && b.Class.Type == StructType.STRUCT_CONST).Coord);
        int spot = Enumerable.Range(0, TdDefines.MAP_CELL_TOTAL).OrderBy(c => TdMath.CellDistance(c, yard)).First(game.Can_Place_At);
        Assert.True(game.Place(spot));
        for (int frame = 0; frame < TdDefines.TICKS_PER_SECOND * 3; frame++)
            game.Main_Loop();
        Assert.Contains(game.Buildings.Active, b => b.House == player && b.Class.Type == StructType.STRUCT_POWER);
        output.WriteLine($"power plant placed at {spot}; credits left {player.Available_Money()}");
    }

    [Fact]
    public void ClickingSelectsAUnitAndARightClickMovesIt()
    {
        if (fixture.Content is not { } td) return;
        string map = TdScenario.List(td.Files).First(n => n[2] is 'M' or 'm');
        var game = Skirmish(map, HousesType.HOUSE_GOOD, HousesType.HOUSE_BAD)!;
        var mcv = game.Units.Active.First(u => u.House == game.PlayerPtr && u.Class.Type == UnitType.UNIT_MCV);

        game.Selection_At_Mouse(mcv.Center_Coord(), shift: false);
        Assert.True(mcv.Is_Selected_By_Player());

        // Two cells over, somewhere the MCV can drive to.
        int start = TdMath.CoordCell(mcv.Coord);
        int goal = Enumerable.Range(1, 6).Select(d => start + d * 2).First(c => game.Map[c].Is_Clear_To_Move(false, false) && game.Map[c].Is_Mapped(game.PlayerPtr));
        Assert.Equal(ActionType.ACTION_MOVE, game.Action_At(TdMath.CellCoord(goal), out _));
        game.Command_Object(TdMath.CellCoord(goal));
        for (int frame = 0; frame < TdDefines.TICKS_PER_SECOND * 20; frame++)
            game.Main_Loop();
        output.WriteLine($"MCV {start} -> {TdMath.CoordCell(mcv.Coord)} (ordered to {goal})");
        Assert.True(TdMath.CellDistance(TdMath.CoordCell(mcv.Coord), goal) <= 1);

        // A click on empty ground clears the selection.
        game.Selection_At_Mouse(TdMath.CellCoord(start), shift: false);
        Assert.False(mcv.Is_Selected_By_Player());
    }

    [Fact]
    public void AMoveOrderTakesAUnitToItsDestination()
    {
        var game = Load("SCG01EA");
        if (game == null) return;
        var tank = game.Infantry.Active.First(u => u.House == game.PlayerPtr);
        int start = TdMath.CoordCell(tank.Coord);
        int goal = start - 4 * TdMath.MapCellW;   // four cells north
        output.WriteLine($"{tank.Class.IniName} at {start}, ordered to {goal}");

        game.Queue_Mission(tank.As_Target(), MissionType.MISSION_MOVE, Target.TARGET_NONE, Target.As_Target(goal));
        for (int frame = 0; frame < TdDefines.TICKS_PER_SECOND * 20; frame++)
            game.Main_Loop();

        int end = TdMath.CoordCell(tank.Coord);
        output.WriteLine($"after 20 seconds: cell {end}, mission {tank.Mission}");
        Assert.True(TdMath.CellDistance(end, goal) <= 1, $"ended at {end}, wanted {goal}");
    }

    [Fact]
    public void TheMcvDeploysIntoAConstructionYard()
    {
        var game = Load("SCG01EA");
        if (game == null) return;
        var mcv = game.Units.Active.First(u => u.House == game.PlayerPtr && u.Class.Type == UnitType.UNIT_MCV);
        int before = game.Buildings.Active.Count(b => b.House == game.PlayerPtr);

        // The original's deploy order: a MEGAMISSION of MISSION_UNLOAD.
        game.Queue_Mission(mcv.As_Target(), MissionType.MISSION_UNLOAD, Target.TARGET_NONE, Target.TARGET_NONE);
        for (int frame = 0; frame < TdDefines.TICKS_PER_SECOND * 10; frame++)
            game.Main_Loop();

        var yards = game.Buildings.Active.Where(b => b.House == game.PlayerPtr && b.Class.Type == StructType.STRUCT_CONST).ToList();
        output.WriteLine($"player buildings {before} -> {game.Buildings.Active.Count(b => b.House == game.PlayerPtr)}; MCV active {mcv.IsActive}");
        Assert.Single(yards);
        Assert.False(mcv.IsActive);
    }

    [Fact]
    public void EveryCampaignScenarioRunsAMinute()
    {
        if (fixture.Content is not { } td) return;
        var failures = new List<string>();
        foreach (string name in TdScenario.List(td.Files).Where(n => n[2] is 'G' or 'B' or 'J' or 'g' or 'b' or 'j'))
        {
            try
            {
                var game = Load(name)!;
                int start = game.Units.Count + game.Infantry.Count + game.Buildings.Count + game.Aircraft.Count;
                for (int frame = 0; frame < TdDefines.TICKS_PER_MINUTE; frame++)
                    game.Main_Loop();
                int end = game.Units.Count + game.Infantry.Count + game.Buildings.Count + game.Aircraft.Count;
                output.WriteLine($"{name}: {start} objects -> {end} after a minute; wins {game.PlayerWins}, loses {game.PlayerLoses}");
            }
            catch (Exception e)
            {
                failures.Add($"{name}: {e.GetType().Name}: {e.Message}\n{e.StackTrace?.Split('\n').Take(6).Aggregate((a, b) => a + "\n" + b)}");
            }
        }
        foreach (string f in failures)
            output.WriteLine(f);
        Assert.Empty(failures);
    }
}

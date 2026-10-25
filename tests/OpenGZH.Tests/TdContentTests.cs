using OpenGZH.Formats.Images;
using OpenGZH.TiberianDawn;
using OpenGZH.TiberianDawn.Art;
using OpenGZH.TiberianDawn.Data;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Loads the Tiberian Dawn content once (the freeware .mix files in content/tiberiandawn).</summary>
public sealed class TdFixture : IDisposable
{
    public TdContent? Content { get; }

    public TdFixture()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "mods", "tiberiandawn", "mod.yaml")))
            dir = Path.GetDirectoryName(dir);
        if (dir != null && File.Exists(Path.Combine(dir, "content", "tiberiandawn", "conquer.mix")))
            Content = TdContent.Load(dir);
    }

    public void Dispose() => Content?.Dispose();
}

/// <summary>Tiberian Dawn: the original scenarios load, and their art draws.</summary>
public class TdContentTests(TdFixture fixture, ITestOutputHelper output) : IClassFixture<TdFixture>
{
    [Fact]
    public void EveryOriginalScenarioLoads()
    {
        if (fixture.Content is not { } td) return;
        var names = TdScenario.List(td.Files);
        output.WriteLine($"{names.Count} scenarios: {string.Join(" ", names)}");
        Assert.Contains("SCG01EA", names);
        foreach (string name in names)
        {
            var scenario = TdScenario.Load(td.Files, name)!;
            Assert.True(scenario.Width > 0 && scenario.Height > 0, name);
            Assert.All(scenario.Units.Concat(scenario.Infantry).Concat(scenario.Structures),
                o => Assert.True((td.Find("Vehicles", o.Type) ?? td.Find("Infantry", o.Type) ?? td.Find("Buildings", o.Type)) != null, $"{name}: unknown {o.Type}"));
        }
    }

    [Fact]
    public void TypeTablesLoadWithTheOriginalValues()
    {
        if (fixture.Content is not { } td) return;
        var types = new OpenGZH.TiberianDawn.Logic.Types.TdTypes(td.Rules);
        var mtnk = types.Unit(OpenGZH.TiberianDawn.Logic.UnitType.UNIT_MTANK);
        Assert.Equal("MTNK", mtnk.IniName);
        Assert.Equal(400, mtnk.MaxStrength);
        Assert.Equal(OpenGZH.TiberianDawn.Logic.MPHType.MPH_MEDIUM, mtnk.MaxSpeed);
        Assert.Equal(OpenGZH.TiberianDawn.Logic.WeaponType.WEAPON_105MM, mtnk.Primary);
        Assert.True(mtnk.IsTurretEquipped && mtnk.IsCrusher);
        var fact = types.Building(OpenGZH.TiberianDawn.Logic.StructType.STRUCT_CONST);
        Assert.Equal(800, fact.MaxStrength);
        Assert.Equal(3, fact.Width());
        Assert.Equal(2, fact.Height());
        Assert.Equal((4, 20, 3), fact.Anim(OpenGZH.TiberianDawn.Logic.BStateType.BSTATE_ACTIVE));
        var e1 = types.Infantry(OpenGZH.TiberianDawn.Logic.InfantryType.INFANTRY_E1);
        Assert.Equal((16, 6, 6), e1.DoControl(OpenGZH.TiberianDawn.Logic.DoType.DO_WALK));
        Assert.Equal(1408, types.Weapon(OpenGZH.TiberianDawn.Logic.WeaponType.WEAPON_RIFLE).Range);
        Assert.Equal(256, types.House(OpenGZH.TiberianDawn.Logic.HousesType.HOUSE_GOOD).Remap.Length);
        Assert.Same(mtnk, types.FromName("mtnk"));
        Assert.Equal(112, types.Ground(OpenGZH.TiberianDawn.Logic.LandType.LAND_CLEAR).Cost[0]);
    }

    [Fact]
    public void GdiMissionOneRenders()
    {
        if (fixture.Content is not { } td) return;
        var scenario = TdScenario.Load(td.Files, "SCG01EA")!;
        var image = new TdMapRenderer(td).Render(scenario);
        Assert.Equal(scenario.Width * 24, image.Width);
        string? shot = Environment.GetEnvironmentVariable("OPENGZH_TD_SHOT");
        if (shot != null)
            foreach (string name in shot.Split(','))
                PngWriter.Save(new TdMapRenderer(td).Render(TdScenario.Load(td.Files, name)!), Path.Combine("C:/Users/mk-ki/AppData/Local/Temp/claude/c--Users-mk-ki-Desktop-OpenGZH/f64029c1-50dc-440f-820c-099299666828/scratchpad", name + ".png"));
    }
}

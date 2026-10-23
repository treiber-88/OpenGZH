using OpenGZH.Core.Mods;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Ini;

namespace OpenGZH.Tests;

public class IniParserTests
{
    [Fact]
    public void ParsesNestedModulesAndAliasProperties()
    {
        const string ini = """
            Object TestTank
              Side = America ; comment
              Draw = W3DTankDraw ModuleTag_01
                DefaultConditionState
                  Model = AVTest
                End
                ConditionState = REALLYDAMAGED
                  Model = AVTest_D
                End
                AliasConditionState = RUBBLE
              End
              WeaponSet
                Weapon = PRIMARY TestGun
              End
            End
            """;

        var doc = IniParser.Parse(ini, "test.ini");
        var obj = Assert.Single(doc.Blocks);
        Assert.Equal("TestTank", obj.Name);
        Assert.Equal("America", obj.Get("Side"));

        var draw = obj.Blocks.First(b => b.Type == "Draw");
        Assert.Equal("W3DTankDraw ModuleTag_01", draw.Name);
        Assert.Equal(2, draw.Blocks.Count());
        Assert.Contains(draw.Properties, p => p.Key == "AliasConditionState" && p.Value == "RUBBLE");
    }

    [Fact]
    public void UnclosedBlockIsAnError()
    {
        Assert.Throws<IniException>(() => IniParser.Parse("Weapon Gun\n  Damage = 5\n", "bad.ini"));
    }
}

public class RuleNodeTests
{
    [Fact]
    public void MergeOverridesFieldsAndTildeRemoves()
    {
        var baseRules = RuleYaml.Parse("""
            Objects:
              Tank:
                BuildCost: 900
                Modules:
                  ModuleTag_01: { Module: Body, MaxHealth: 480 }
                  ModuleTag_02: { Module: Behavior, Class: Foo }
            """, "base");
        var modRules = RuleYaml.Parse("""
            Objects:
              Tank:
                BuildCost: 800
                Modules:
                  ModuleTag_02: ~
            """, "mod");

        var set = new RuleSet();
        set.Merge(baseRules);
        set.Merge(modRules);

        var tank = set.Get("Objects", "Tank")!;
        Assert.Equal("800", tank.GetString("BuildCost"));
        Assert.Equal("480", tank["Modules"]!["ModuleTag_01"]!.GetString("MaxHealth"));
        Assert.False(tank["Modules"]!.Has("ModuleTag_02"));
    }

    [Fact]
    public void YamlRoundTripKeepsValues()
    {
        var node = RuleNode.Mapping();
        node.Set("DisplayName", "OBJECT:Crusader");
        node.Set("Text", "a: b # not a comment");
        var list = node.Set("FX", RuleNode.Sequence());
        list.Add(RuleNode.Scalar("INITIAL FX_A"));
        list.Add(RuleNode.Scalar("FINAL FX_B"));

        var parsed = RuleYaml.Parse(RuleYaml.Write(node), "roundtrip");
        Assert.Equal("OBJECT:Crusader", parsed.GetString("DisplayName"));
        Assert.Equal("a: b # not a comment", parsed.GetString("Text"));
        Assert.Equal(["INITIAL FX_A", "FINAL FX_B"], parsed["FX"]!.Items.Select(i => i.Value));
    }
}

/// <summary>Tests that need the imported base game rules (mods/zh/rules). Skipped work if they are missing.</summary>
public class BaseGameRulesTests
{
    static string? FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "mods", "zh", "mod.yaml")))
            dir = Path.GetDirectoryName(dir);
        return dir;
    }

    [Fact]
    public void BaseGameRulesLoadWithAllFactions()
    {
        string? root = FindRoot();
        if (root == null || !Directory.Exists(Path.Combine(root, "mods", "zh", "rules")))
            return;

        var manifest = ModManifest.Load(Path.Combine(root, "mods", "zh", "mod.yaml"));
        var rules = RuleSet.Load(manifest);

        var objects = rules.Section("Objects");
        Assert.True(objects.Entries.Count > 2000, $"only {objects.Entries.Count} objects");
        Assert.Equal("900", rules.Get("Objects", "AmericaTankCrusader")!.GetString("BuildCost"));
        Assert.NotNull(rules.Get("Weapons", "CrusaderTankGun"));
        Assert.NotNull(rules.Get("Locomotors", "CrusaderLocomotor"));

        // Every playable faction has its PlayerTemplate.
        foreach (string faction in new[] { "FactionAmerica", "FactionChina", "FactionGLA",
                     "FactionAmericaSuperWeaponGeneral", "FactionAmericaLaserGeneral", "FactionAmericaAirForceGeneral",
                     "FactionChinaTankGeneral", "FactionChinaInfantryGeneral", "FactionChinaNukeGeneral",
                     "FactionGLAToxinGeneral", "FactionGLADemolitionGeneral", "FactionGLAStealthGeneral" })
            Assert.NotNull(rules.Get("PlayerTemplates", faction));
    }
}

public class RuleValueTests
{
    [Theory]
    [InlineData("100.0f", 100f)]
    [InlineData("12.5", 12.5f)]
    [InlineData("50%", 0.5f)]
    [InlineData("-3", -3f)]
    [InlineData(".5f", 0.5f)]
    public void NumbersReadLikeTheOriginal(string text, float expected) =>
        Assert.Equal(expected, RuleValue.Float(text), 4);

    [Fact]
    public void JunkFallsBack() => Assert.Equal(7f, RuleValue.Float("abc", 7));
}

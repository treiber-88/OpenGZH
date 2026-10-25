using System.Numerics;
using OpenGZH.Logic.Construction;
using OpenGZH.Logic.Core;
using Xunit;

namespace OpenGZH.Tests;

/// <summary>The OpenGZH build radius: human players build only near their Command Centers.</summary>
public class BuildRadiusTests(BaseGameFixture content) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void HumansBuildOnlyNearACommandCenter()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var cc = game.Objects.First(o => o.Owner == alice && o.IsKindOf("COMMANDCENTER"));
        var barracks = game.Templates.Find("AmericaBarracks")!;
        float limit = game.Catalog.GameData.MaxBuildDistanceFromCommandCenter;
        Assert.True(limit > 0);

        // Somewhere well outside the radius, towards the map centre.
        var centre = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var far = cc.Position + Vector3.Normalize((centre - cc.Position) with { Z = 0 }) * (limit + 200);
        Assert.Equal(BuildLocation.TooFarFromCommandCenter, game.Building.CheckLocation(barracks, far, 0, alice));

        // Next to the base the limit does not get in the way.
        var near = cc.Position + Vector3.Normalize((centre - cc.Position) with { Z = 0 }) * 250;
        Assert.NotEqual(BuildLocation.TooFarFromCommandCenter, game.Building.CheckLocation(barracks, near, 0, alice));

        // The computer player is not limited.
        var bob = game.Players.Find("Bob")!;
        Assert.NotEqual(BuildLocation.TooFarFromCommandCenter, game.Building.CheckLocation(game.Templates.Find("ChinaBarracks")!, far, 0, bob));

        // With no Command Center left, a new one may go anywhere, but nothing else may.
        cc.Kill();
        for (int i = 0; i < 30 * 20 && !cc.IsDestroyed; i++)
            game.Update();
        Assert.NotEqual(BuildLocation.TooFarFromCommandCenter, game.Building.CheckLocation(cc.Template, far, 0, alice));
        Assert.Equal(BuildLocation.TooFarFromCommandCenter, game.Building.CheckLocation(barracks, near, 0, alice));
    }
}

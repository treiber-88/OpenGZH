using OpenGZH.Formats.Images;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>The original's animated mouse cursors (Data/Cursors/*.ani) decode.</summary>
public class CursorTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void EveryCursorInTheRulesDecodes()
    {
        if (!content.Available) return;
        int checkedFiles = 0;
        foreach (var (name, node) in content.Rules!.Section("MouseCursors").Entries)
        {
            string texture = node.GetString("Texture", node.GetString("Image"));
            int directions = Math.Max(1, OpenGZH.Core.Rules.RuleValue.Int(node.GetString("Directions"), 1));
            for (int d = 0; d < directions; d++)
            {
                string file = directions > 1 ? $"Data/Cursors/{texture}{d}.ani" : $"Data/Cursors/{texture}.ani";
                if (!content.FileSystem!.Exists(file))
                {
                    output.WriteLine($"{name}: no {file}");
                    continue;
                }
                var cursor = AniCursor.Decode(content.FileSystem.ReadAllBytes(file));
                Assert.NotEmpty(cursor.Frames);
                Assert.All(cursor.Frames, f => Assert.Contains(f.Pixels.Where((_, i) => i % 4 == 3), a => a == 255));
                Assert.InRange(cursor.HotspotX, 0, cursor.Frames[0].Width);
                checkedFiles++;
            }
        }
        output.WriteLine($"{checkedFiles} cursor files decoded");
        Assert.True(checkedFiles > 30);
    }
}

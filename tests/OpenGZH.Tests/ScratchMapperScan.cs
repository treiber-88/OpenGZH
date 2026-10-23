using OpenGZH.Formats.W3d;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

public class ScratchMapperScan(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Scan()
    {
        if (!content.Available) return;
        var counts = new Dictionary<string, int>();
        var examples = new Dictionary<string, string>();
        foreach (var path in content.FileSystem!.List("Art/W3D", ".w3d", recursive: true))
        {
            W3dFile file;
            try { file = W3dReader.Read(content.FileSystem.ReadAllBytes(path)); } catch { continue; }
            foreach (var mesh in file.Meshes)
                foreach (var vm in mesh.VertexMaterials)
                {
                    int stage1 = (int)((vm.Attributes >> 8) & 0xFF);
                    string key = $"s0={vm.StageMapping} s1={stage1}";
                    if (vm.StageMapping == 0 && stage1 == 0) continue;
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                    examples.TryAdd(key, $"{path} {mesh.Name}: {vm.MapperArgs.Replace("\r", " ").Replace("\n", " ")}");
                }
        }
        foreach (var (k, n) in counts.OrderByDescending(kv => kv.Value))
            output.WriteLine($"{k}: {n}  e.g. {examples[k]}");
    }
}

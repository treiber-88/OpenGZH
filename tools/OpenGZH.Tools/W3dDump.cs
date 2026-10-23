using OpenGZH.Core.FileSystem;
using OpenGZH.Formats.W3d;

namespace OpenGZH.Tools;

/// <summary>Prints a W3D model's meshes, material passes, shaders, vertex materials and textures.</summary>
public static class W3dDump
{
    public static int Run(VirtualFileSystem fileSystem, string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("w3d-dump <model name>");
            return 1;
        }
        var library = new W3dLibrary(fileSystem);
        var file = library.GetFile(args[0]);
        if (file == null)
        {
            Console.WriteLine($"No model {args[0]}");
            return 1;
        }
        foreach (var mesh in file.Meshes)
        {
            Console.WriteLine($"MESH {mesh.FullName} flags={mesh.Flags} verts={mesh.Positions.Length} tris={mesh.TriangleCount}");
            Console.WriteLine($"  textures: {string.Join(", ", mesh.Textures.Select(t => t.Name))}");
            for (int i = 0; i < mesh.VertexMaterials.Count; i++)
            {
                var m = mesh.VertexMaterials[i];
                Console.WriteLine($"  vmat[{i}] {m.Name} attr=0x{m.Attributes:X8} map0={m.StageMapping} args='{m.MapperArgs.Replace("\r", "").Replace("\n", "|")}' amb={m.Ambient} dif={m.Diffuse} emi={m.Emissive} spec={m.Specular} opac={m.Opacity}");
            }
            for (int i = 0; i < mesh.Shaders.Count; i++)
            {
                var s = mesh.Shaders[i];
                Console.WriteLine($"  shader[{i}] depthCmp={s.DepthCompare} depthMask={s.DepthMask} src={s.SrcBlend} dst={s.DestBlend} prim={s.PrimaryGradient} sec={s.SecondaryGradient} tex={s.Texturing} detailC={s.DetailColorFunc} detailA={s.DetailAlphaFunc} alphaTest={s.AlphaTest}");
            }
            for (int p = 0; p < mesh.Passes.Count; p++)
            {
                var pass = mesh.Passes[p];
                Console.WriteLine($"  pass {p}: vmatIds[{pass.VertexMaterialIds.Length}]={string.Join(",", pass.VertexMaterialIds.Distinct())} shaderIds={string.Join(",", pass.ShaderIds.Distinct())} dcg={pass.DiffuseColors.Length} stages={pass.Stages.Count}");
                for (int st = 0; st < pass.Stages.Count; st++)
                {
                    var uvs = pass.Stages[st].TexCoords;
                    string range = uvs.Length == 0 ? "" : $" u=[{uvs.Min(t => t.X):F2},{uvs.Max(t => t.X):F2}] v=[{uvs.Min(t => t.Y):F2},{uvs.Max(t => t.Y):F2}]";
                    Console.WriteLine($"    stage {st}: texIds={string.Join(",", pass.Stages[st].TextureIds.Distinct())} uvs={uvs.Length}{range}");
                }
            }
        }
        return 0;
    }
}

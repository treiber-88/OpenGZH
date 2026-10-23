using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Formats.W3d;
using OpenGZH.Logic;
using OpenGZH.Logic.Art;
using OpenGZH.Logic.Modules;
using OpenGZH.Logic.Terrain;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.World;

/// <summary>
/// Draws the map's bridges (the original's W3DBridgeBuffer): each bridge model's BRIDGE_LEFT end,
/// as many BRIDGE_SPAN sections as fit, and the BRIDGE_RIGHT end, laid between the bridge's two points
/// with the bridge style's texture; a model without sections is stretched. The look follows the
/// bridge's damage state.
/// </summary>
public sealed class BridgeView : IDisposable
{
    readonly GL gl;
    readonly GameLogic game;
    readonly Dictionary<Bridge, (BodyDamageState State, ModelInstance? Instance)> built = new();

    public BridgeView(GL gl, GameLogic game)
    {
        this.gl = gl;
        this.game = game;
    }

    /// <summary>The bridge models to draw this frame (rebuilt when a bridge's damage state changes).</summary>
    public IEnumerable<ModelInstance> Instances()
    {
        foreach (var bridge in game.Terrain.Bridges.All)
        {
            // Landmark bridges are ordinary objects with their own models.
            if (game.FindObject(bridge.Info.BridgeObjectId) is { } obj && obj.IsKindOf("LANDMARK_BRIDGE"))
                continue;
            var state = bridge.Info.CurDamageState;
            if (!built.TryGetValue(bridge, out var entry) || entry.State != state)
            {
                entry.Instance?.Model.Dispose();
                entry = (state, Build(bridge, state));
                built[bridge] = entry;
            }
            if (entry.Instance != null)
                yield return entry.Instance;
        }
    }

    ModelInstance? Build(Bridge bridge, BodyDamageState state)
    {
        if (BridgeSetup.FindTemplate(game, bridge.TemplateName) is not { } template || game.Bones.Library is not { } library)
            return null;
        var (modelName, texture) = template.Look(state);
        var model = BridgeModel.Load(library, modelName);
        if (model == null)
            return null;
        var meshes = new List<W3dMesh>();
        float scale = template.BridgeScale;
        var start = bridge.Info.From;
        var end = bridge.Info.To;

        var vec = end - start;
        var vecNormal = new Vector3(-vec.Y, vec.X, 0);
        if (vecNormal.LengthSquared() > 0)
            vecNormal = Vector3.Normalize(vecNormal);
        float desiredLength = Math.Max(1e-3f, vec.Length());
        float deltaZ = (end.Z - start.Z) / desiredLength;
        float deltaX = MathF.Sqrt(Math.Max(0, 1 - deltaZ * deltaZ));
        // (As the original: the "up" vector leans along world X, not along the bridge.)
        var vecZ = new Vector3(-deltaZ, 0, deltaX);
        float xOffset = -model.LeftMinX;

        if (model.SectionMesh == null)
        {
            // A single stretched piece (the original's getModelVerticesFixed).
            meshes.Add(Place(model.LeftMesh, model.LeftMtx, xOffset, vec / model.Length, vecNormal * scale, vecZ * scale, start, texture));
        }
        else
        {
            float spanLength = model.RightMinX - model.LeftMaxX;
            int numSpans = 1;
            if (model.Type != BridgeModel.BridgeType.Fixed && spanLength > 0)
            {
                float spannable = desiredLength - (model.Length - spanLength);
                numSpans = Math.Max(0, (int)MathF.Floor((spannable + spanLength / 2) / spanLength));
            }
            float bridgeLength = model.Length + (numSpans - 1) * spanLength;
            var along = vec / Math.Max(1e-3f, bridgeLength);
            meshes.Add(Place(model.LeftMesh, model.LeftMtx, xOffset, along, vecNormal * scale, vecZ * scale, start, texture));
            for (int i = 0; i < numSpans; i++)
                meshes.Add(Place(model.SectionMesh, model.SectionMtx, xOffset + i * spanLength, along, vecNormal * scale, vecZ * scale, start, texture));
            if (model.RightMesh != null)
                meshes.Add(Place(model.RightMesh, model.RightMtx, xOffset + (numSpans - 1) * spanLength, along, vecNormal * scale, vecZ * scale, start, texture));
        }
        var render = RenderModel.FromMeshes(gl, $"bridge{bridge.Layer}", meshes);
        return new ModelInstance(render);
    }

    /// <summary>A copy of a mesh laid out along the bridge (the original's getModelVertices).</summary>
    static W3dMesh Place(W3dMesh source, Matrix4x4 mtx, float xOffset, Vector3 vec, Vector3 vecNormal, Vector3 vecZ, Vector3 start, string texture)
    {
        var mesh = new W3dMesh
        {
            Name = source.Name,
            ContainerName = source.ContainerName,
            Flags = source.Flags & ~W3dMeshFlags.Hidden,
            Indices = source.Indices,
        };
        var dirX = vec.LengthSquared() > 0 ? Vector3.Normalize(vec) : Vector3.UnitX;
        var dirY = vecNormal.LengthSquared() > 0 ? Vector3.Normalize(vecNormal) : Vector3.UnitY;
        var dirZ = vecZ.LengthSquared() > 0 ? Vector3.Normalize(vecZ) : Vector3.UnitZ;
        var positions = new Vector3[source.Positions.Length];
        var normals = new Vector3[source.Positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            var v = Vector3.Transform(source.Positions[i], mtx);
            positions[i] = (v.X + xOffset) * vec + v.Y * vecNormal + v.Z * vecZ + start;
            var n = i < source.Normals.Length ? Vector3.TransformNormal(source.Normals[i], mtx) : Vector3.UnitZ;
            var placed = n.X * dirX + n.Y * dirY + n.Z * dirZ;
            normals[i] = placed.LengthSquared() > 0 ? Vector3.Normalize(placed) : Vector3.UnitZ;
        }
        mesh.Positions = positions;
        mesh.Normals = normals;
        // The bridge style's texture replaces the model's own.
        foreach (var t in source.Textures)
            mesh.Textures.Add(new W3dTexture { Name = texture.Length > 0 ? texture : t.Name, FrameCount = t.FrameCount, FrameRate = t.FrameRate, Attributes = t.Attributes });
        mesh.Shaders.AddRange(source.Shaders);
        mesh.VertexMaterials.AddRange(source.VertexMaterials);
        mesh.Passes.AddRange(source.Passes);
        return mesh;
    }

    public void Dispose()
    {
        foreach (var (_, instance) in built.Values)
            instance?.Model.Dispose();
        built.Clear();
    }
}

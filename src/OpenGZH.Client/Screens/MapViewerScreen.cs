using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.Platform;
using OpenGZH.Client.Terrain;
using OpenGZH.Client.World;
using OpenGZH.Formats.Maps;
using OpenGZH.Logic.Templates;
using Silk.NET.Input;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.Screens;

/// <summary>
/// Shows a map with its terrain, water and placed objects. Arrow keys / WASD scroll,
/// mouse wheel zooms, Q/E rotate.
/// </summary>
public sealed class MapViewerScreen : IScreen
{
    readonly GL gl;
    readonly IInputContext input;
    readonly TextureCache textures;
    readonly ModelRenderer modelRenderer;
    readonly TerrainRenderer terrain;
    readonly WaterRenderer water;
    readonly Camera camera = new();
    readonly SceneLighting terrainLighting = new();
    readonly SceneLighting objectLighting = new();
    readonly Dictionary<string, RenderModel?> models = new(StringComparer.OrdinalIgnoreCase);
    readonly List<ModelInstance> instances = new();

    public MapFile Map { get; }

    public MapViewerScreen(GL gl, IInputContext input, GameContent content, string mapPath, bool topDown = false)
    {
        this.gl = gl;
        this.input = input;
        textures = new TextureCache(gl, content.FileSystem);
        modelRenderer = new ModelRenderer(gl, textures);

        var start = DateTime.UtcNow;
        Map = MapFile.Read(content.FileSystem.ReadAllBytes(mapPath));
        var atlas = new TerrainAtlas(Map.BlendTiles!, content.Rules, content.FileSystem);
        terrain = new TerrainRenderer(gl, Map, atlas);
        water = new WaterRenderer(gl, Map, content.Rules, textures);
        ApplyLighting(Map.Lighting);

        var templates = new TemplateStore(content.Rules);
        int placed = 0, missing = 0;
        foreach (var mapObject in Map.Objects)
        {
            var template = templates.Find(mapObject.TemplateName);
            if (template == null)
            {
                if (!mapObject.TemplateName.StartsWith('*'))
                    missing++;
                continue;
            }
            string? modelName = ObjectArt.DefaultModel(template);
            if (modelName == null)
                continue;
            if (!models.TryGetValue(modelName, out var model))
                models[modelName] = model = RenderModel.Build(gl, content.Models, modelName);
            if (model == null)
                continue;

            var position = mapObject.Position;
            position.Z += Map.HeightMap.HeightAt(position.X, position.Y);
            var instance = new ModelInstance(model)
            {
                World = Matrix4x4.CreateRotationZ(mapObject.Angle) * Matrix4x4.CreateTranslation(position),
            };
            instance.UpdatePose();
            instances.Add(instance);
            placed++;
        }

        var heights = Map.HeightMap;
        var playable = heights.Boundaries.FirstOrDefault();
        camera.Target = new Vector3(playable.X * MapFile.CellSize / 2, playable.Y * MapFile.CellSize / 2, 0);
        camera.Target.Z = heights.HeightAt(camera.Target.X, camera.Target.Y);
        camera.Distance = 450;
        camera.Pitch = 0.9f;
        camera.Yaw = -MathF.PI / 2 - 0.6f;
        camera.FarPlane = 8000;
        if (topDown)
        {
            // Whole map from straight above, north (+Y) up: handy for comparing with the minimap.
            camera.Pitch = 1.5707f;
            camera.Yaw = -MathF.PI / 2;
            camera.Distance = Math.Max(heights.Width, heights.Height) * MapFile.CellSize * 1.25f;
            camera.FarPlane = camera.Distance * 3;
        }

        Console.WriteLine($"{mapPath}: {heights.Width}x{heights.Height}, {placed} objects drawn ({missing} unknown templates), " +
                          $"{models.Count(m => m.Value != null)} models, loaded in {(DateTime.UtcNow - start).TotalSeconds:F1}s");

        foreach (var mouse in input.Mice)
            mouse.Scroll += (_, wheel) => camera.Distance = Math.Clamp(camera.Distance * (wheel.Y > 0 ? 0.9f : 1.1f), 100, 3000);
    }

    /// <summary>Uses the map's own lighting for the current time of day.</summary>
    void ApplyLighting(MapLighting lighting)
    {
        int time = lighting.TimeIndex;
        terrainLighting.Ambient = lighting.TerrainLights[time, 0].Ambient;
        objectLighting.Ambient = lighting.ObjectLights[time, 0].Ambient;
        for (int i = 0; i < 3; i++)
        {
            terrainLighting.Colors[i] = lighting.TerrainLights[time, i].Diffuse;
            terrainLighting.Directions[i] = Safe(lighting.TerrainLights[time, i].Direction);
            objectLighting.Colors[i] = lighting.ObjectLights[time, i].Diffuse;
            objectLighting.Directions[i] = Safe(lighting.ObjectLights[time, i].Direction);
        }

        static Vector3 Safe(Vector3 direction) => direction.LengthSquared() > 0 ? direction : -Vector3.UnitZ;
    }

    public void Resize(int width, int height) => camera.AspectRatio = width / (float)Math.Max(1, height);

    public void Update(float seconds)
    {
        water.Update(seconds);
        var keyboard = input.Keyboards.FirstOrDefault();
        if (keyboard == null)
            return;

        float speed = camera.Distance * 1.5f * seconds;
        if (keyboard.IsKeyPressed(Key.Left) || keyboard.IsKeyPressed(Key.A)) camera.Target -= camera.ScreenRight * speed;
        if (keyboard.IsKeyPressed(Key.Right) || keyboard.IsKeyPressed(Key.D)) camera.Target += camera.ScreenRight * speed;
        if (keyboard.IsKeyPressed(Key.Up) || keyboard.IsKeyPressed(Key.W)) camera.Target += camera.ScreenUp * speed;
        if (keyboard.IsKeyPressed(Key.Down) || keyboard.IsKeyPressed(Key.S)) camera.Target -= camera.ScreenUp * speed;
        if (keyboard.IsKeyPressed(Key.Q)) camera.Yaw += seconds;
        if (keyboard.IsKeyPressed(Key.E)) camera.Yaw -= seconds;
        camera.Target.Z = Map.HeightMap.HeightAt(camera.Target.X, camera.Target.Y);
    }

    public void Render(float seconds)
    {
        gl.ClearColor(0.45f, 0.55f, 0.65f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        var viewProjection = camera.ViewProjection;
        terrain.Draw(viewProjection, terrainLighting);

        var frustum = new Frustum(viewProjection);
        var visible = instances.Where(i => frustum.Intersects(i.World.Translation, 150));
        modelRenderer.Draw(visible, viewProjection, camera.Position, objectLighting);
        water.Draw(viewProjection);
    }

    public void Dispose()
    {
        foreach (var model in models.Values)
            model?.Dispose();
        terrain.Dispose();
        water.Dispose();
        modelRenderer.Dispose();
        textures.Dispose();
    }
}

using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.Terrain;
using OpenGZH.Formats.Maps;
using OpenGZH.Logic;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.World;

/// <summary>
/// Shows a running game: terrain, water and every object, kept in step with the simulation.
/// Also drives the simulation at its fixed 30 frames per second from real time.
/// </summary>
public sealed class WorldView : IDisposable
{
    readonly GL gl;
    readonly TextureCache textures;
    readonly ModelRenderer modelRenderer;
    readonly ModelCache models;
    readonly TerrainRenderer terrain;
    readonly WaterRenderer water;
    readonly Dictionary<GameObject, Drawable> drawables = new();
    readonly List<ModelInstance> visibleInstances = new();
    float logicTimeAccumulator;

    public GameLogic Game { get; }
    public MapFile Map { get; }
    public Camera Camera { get; } = new();
    public SceneLighting TerrainLighting { get; } = new();
    public SceneLighting ObjectLighting { get; } = new();

    /// <summary>When false the simulation does not advance (paused game, menus).</summary>
    public bool RunLogic { get; set; } = true;

    /// <summary>Game speed multiplier (1 = normal).</summary>
    public float Speed { get; set; } = 1;

    public IReadOnlyDictionary<GameObject, Drawable> Drawables => drawables;

    public WorldView(GL gl, GameContent content, GameLogic game, MapFile map, TextureCache textures)
    {
        this.gl = gl;
        this.textures = textures;
        Game = game;
        Map = map;
        modelRenderer = new ModelRenderer(gl, textures);
        models = new ModelCache(gl, content.Models);
        terrain = new TerrainRenderer(gl, map, new TerrainAtlas(map.BlendTiles!, content.Rules, content.FileSystem));
        water = new WaterRenderer(gl, map, content.Rules, textures);
        ApplyLighting(map.Lighting);

        foreach (var obj in game.Objects)
            Add(obj);
        game.Events.OnObjectCreated += Add;
        game.Events.OnObjectDestroyed += obj => drawables.Remove(obj);
        game.Events.OnWeaponFired += (shooter, _, slot) =>
        {
            if (drawables.TryGetValue(shooter, out var drawable))
                drawable.OnWeaponFired(slot);
        };

        Camera.Pitch = 0.95f;
        Camera.Yaw = -MathF.PI / 2;
        Camera.Distance = 420;
        Camera.FarPlane = 6000;
    }

    void Add(GameObject obj)
    {
        if (obj.Template.ModulesOfType("Draw").Any())
            drawables[obj] = new Drawable(obj);
    }

    void ApplyLighting(MapLighting lighting)
    {
        int time = lighting.TimeIndex;
        TerrainLighting.Ambient = lighting.TerrainLights[time, 0].Ambient;
        ObjectLighting.Ambient = lighting.ObjectLights[time, 0].Ambient;
        for (int i = 0; i < 3; i++)
        {
            TerrainLighting.Colors[i] = lighting.TerrainLights[time, i].Diffuse;
            TerrainLighting.Directions[i] = Safe(lighting.TerrainLights[time, i].Direction);
            ObjectLighting.Colors[i] = lighting.ObjectLights[time, i].Diffuse;
            ObjectLighting.Directions[i] = Safe(lighting.ObjectLights[time, i].Direction);
        }
        static Vector3 Safe(Vector3 direction) => direction.LengthSquared() > 0 ? direction : -Vector3.UnitZ;
    }

    public void LookAt(Vector3 point)
    {
        Camera.Target = point with { Z = Game.Terrain.GroundHeight(point) };
    }

    /// <summary>Advances the simulation (in whole logic frames) and the visuals.</summary>
    public void Update(float seconds)
    {
        if (RunLogic)
        {
            logicTimeAccumulator += seconds * Speed;
            int frames = 0;
            while (logicTimeAccumulator >= LogicTime.SecondsPerFrame && frames++ < 5)
            {
                Game.Update();
                logicTimeAccumulator -= LogicTime.SecondsPerFrame;
            }
            if (frames >= 5)
                logicTimeAccumulator = 0; // too slow to catch up: drop time rather than spiral
        }
        water.Update(seconds);
    }

    public void Render(float seconds, float aspectRatio)
    {
        Camera.AspectRatio = aspectRatio;
        var viewProjection = Camera.ViewProjection;
        terrain.Draw(viewProjection, TerrainLighting);

        var frustum = new Frustum(viewProjection);
        visibleInstances.Clear();
        foreach (var (obj, drawable) in drawables)
        {
            if (obj.IsContained || !frustum.Intersects(obj.Position, Math.Max(40, obj.Radius * 2)))
                continue;
            drawable.Update(seconds, models);
            foreach (var part in drawable.Parts)
                if (part.Instance != null)
                    visibleInstances.Add(part.Instance);
        }
        modelRenderer.Draw(visibleInstances, viewProjection, Camera.Position, ObjectLighting);
        water.Draw(viewProjection);
    }

    /// <summary>The ground point under a screen pixel (for orders and building placement).</summary>
    public Vector3? GroundPointAt(Vector2 pixel, Vector2 viewport)
    {
        var (origin, direction) = Camera.ScreenRay(pixel, viewport);
        // March along the ray until it goes below the terrain, then refine.
        float step = 10;
        var previous = origin;
        for (float distance = 0; distance < Camera.FarPlane; distance += step)
        {
            var point = origin + direction * distance;
            if (point.Z <= Game.Terrain.LayerHeight(point.X, point.Y))
            {
                for (int i = 0; i < 8; i++)
                {
                    var middle = (previous + point) / 2;
                    if (middle.Z <= Game.Terrain.LayerHeight(middle.X, middle.Y)) point = middle; else previous = middle;
                }
                return point;
            }
            previous = point;
        }
        return null;
    }

    /// <summary>The object under a screen pixel, if any (closest to the camera).</summary>
    public GameObject? ObjectAt(Vector2 pixel, Vector2 viewport, Func<GameObject, bool>? filter = null)
    {
        var (origin, direction) = Camera.ScreenRay(pixel, viewport);
        GameObject? best = null;
        float bestDistance = float.MaxValue;
        foreach (var obj in drawables.Keys)
        {
            if (obj.IsContained || obj.IsEffectivelyDead || filter != null && !filter(obj))
                continue;
            // Test the ray against a sphere around the object's middle.
            var center = obj.Position + new Vector3(0, 0, obj.Height / 2);
            float radius = Math.Max(obj.Radius, obj.Height / 2) * 0.9f + 2;
            var toCenter = center - origin;
            float along = Vector3.Dot(toCenter, direction);
            if (along < 0)
                continue;
            float missSquared = toCenter.LengthSquared() - along * along;
            if (missSquared <= radius * radius && along < bestDistance)
            {
                bestDistance = along;
                best = obj;
            }
        }
        return best;
    }

    /// <summary>Where a world point appears on screen (pixels), or null if behind the camera.</summary>
    public Vector2? ToScreen(Vector3 world, Vector2 viewport)
    {
        var clip = Vector4.Transform(new Vector4(world, 1), Camera.ViewProjection);
        if (clip.W <= 0.01f)
            return null;
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        return new Vector2((ndc.X + 1) / 2 * viewport.X, (1 - ndc.Y) / 2 * viewport.Y);
    }

    public void Dispose()
    {
        terrain.Dispose();
        water.Dispose();
        models.Dispose();
        modelRenderer.Dispose();
    }
}

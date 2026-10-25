using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.Terrain;
using OpenGZH.Formats.Maps;
using OpenGZH.Logic;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Vision;
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
    readonly BridgeView bridges;
    readonly Terrain.RoadRenderer roads;
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

    public TerrainAtlas TerrainAtlas => terrain.Atlas;

    /// <summary>Particles, FX, beams, tracers, scorch marks and camera shake.</summary>
    public Effects.EffectsView Effects { get; }

    ShroudTexture? shroud;

    /// <summary>The FX name prefix of a Tiberian Dawn animation (TD weapons' FireFX and ProjectileDetonationFX).</summary>
    public const string TdFxPrefix = "TDFX:";

    /// <summary>Tiberian Dawn objects' sprites (made when the first one appears).</summary>
    TiberianDawn.TdSpriteRenderer? tdSprites;
    bool tdSpritesTried;

    /// <summary>
    /// Whose fog of war to show (null = everything visible, e.g. the map viewer or an observer).
    /// Enemy units are only drawn where that player can see; enemy buildings once they have been seen.
    /// </summary>
    public Player? Viewer
    {
        get => viewer;
        set
        {
            viewer = value;
            if (value == null)
            {
                terrain.Shroud = null;
                modelRenderer.Shroud = null;
                if (tdSprites != null)
                    tdSprites.Shroud = null;
                roads.Shroud = null;
                return;
            }
            shroud ??= new ShroudTexture(gl, Game, value.Index);
            shroud.PlayerIndex = value.Index;
            terrain.Shroud = shroud;
            roads.Shroud = shroud;
            modelRenderer.Shroud = shroud;
            if (tdSprites != null)
                tdSprites.Shroud = shroud;
        }
    }
    Player? viewer;
    readonly HashSet<uint> seenStructures = new();

    /// <summary>Whether the viewer may see this object now.</summary>
    public bool IsVisibleToViewer(GameObject obj)
    {
        if (viewer == null || obj.Owner == viewer || obj.Owner.IsAllyOf(viewer))
            return true;
        var status = Game.Shroud.StatusOf(obj, viewer);
        bool visible = status is ObjectShroud.Clear or ObjectShroud.PartialClear;
        if (obj.IsKindOf(Kind.Structure) || obj.IsKindOf("SHRUBBERY") || obj.IsKindOf("PROP"))
        {
            // Buildings and scenery stay drawn in the fog once seen (the original's ghost objects).
            if (visible)
                seenStructures.Add(obj.Id);
            return visible || (status == ObjectShroud.Fogged && seenStructures.Contains(obj.Id));
        }
        return visible;
    }

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
        bridges = new BridgeView(gl, game);
        roads = new Terrain.RoadRenderer(gl, map, content.Rules, textures, game.Terrain);
        ApplyLighting(map.Lighting);

        Effects = new Effects.EffectsView(gl, this, textures);

        foreach (var obj in game.Objects)
            Add(obj, atStart: true);
        game.Events.OnObjectCreated += obj => Add(obj, atStart: false);
        game.Events.OnObjectDestroyed += obj =>
        {
            drawables.Remove(obj);
            tdSprites?.OnDestroyed(obj);
        };
        string friendly = game.Catalog.GameData.Get("StealthFriendlyOpacity") ?? "";
        stealthFriendlyOpacity = Core.Rules.RuleValue.Float(friendly, 50) / (friendly.Contains('%') ? 100 : 1);
        SelectionFlashSaturation = Core.Rules.RuleValue.Float(game.Catalog.GameData.Get("SelectionFlashSaturationFactor"), 0.5f);
        game.Events.OnFlashAsSelected += FlashAsSelected;
        // Tiberian Dawn weapons name their muzzle flash and explosion "TDFX:<TD animation>".
        game.Events.OnFx += fx =>
        {
            if (fx.Name.StartsWith(TdFxPrefix, StringComparison.OrdinalIgnoreCase))
                foreach (string anim in fx.Name[TdFxPrefix.Length..].Split('|'))
                    tdSprites?.PlayAnim(anim, fx.Position);
        };
        game.Events.OnWeaponFireFx += fx =>
        {
            if (!fx.Name.StartsWith(TdFxPrefix, StringComparison.OrdinalIgnoreCase) || tdSprites == null || !IsVisibleToViewer(fx.Shooter))
                return;
            var from = fx.Shooter.Position;
            float towards = MathF.Atan2(fx.Target.Y - from.Y, fx.Target.X - from.X);
            // At the muzzle: out from the shooter's middle towards the target.
            var muzzle = from + new Vector3(MathF.Cos(towards), MathF.Sin(towards), 0) * fx.Shooter.Radius + new Vector3(0, 0, fx.Shooter.Height / 2);
            var parts = fx.Name[TdFxPrefix.Length..].Split('|');
            tdSprites.PlayAnim(parts[0], muzzle, towards, Camera);
            if (parts.Length > 1)
                tdSprites.PlayAnim(parts[1], fx.Target with { Z = Game.Terrain.GroundHeight(fx.Target) + 4 });
        };
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

    float stealthFriendlyOpacity = 0.5f;

    /// <summary>SelectionFlashSaturationFactor from the game data.</summary>
    public float SelectionFlashSaturation { get; private set; } = 0.5f;

    /// <summary>The quick flash a unit gives when it is selected.</summary>
    public void FlashAsSelected(GameObject obj)
    {
        if (drawables.TryGetValue(obj, out var drawable))
            drawable.FlashAsSelected(SelectionFlashSaturation);
    }

    void Add(GameObject obj, bool atStart)
    {
        if (TiberianDawn.TdSpriteRenderer.Handles(obj))
        {
            if (!tdSpritesTried)
            {
                tdSpritesTried = true;
                tdSprites = TiberianDawn.TdSpriteRenderer.TryCreate(gl);
                if (tdSprites != null)
                    tdSprites.Shroud = viewer != null ? shroud : null;
            }
            tdSprites?.OnCreated(obj, atStart);
        }
        if (obj.Template.ModulesOfType("Draw").Any())
            drawables[obj] = new Drawable(obj, Effects.Particles)
            {
                FindDrawable = other => drawables.GetValueOrDefault(other),
                LocalPlayer = () => Viewer,
                StealthFriendlyOpacity = stealthFriendlyOpacity,
            };
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
                long tl = GameProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                Game.Update();
                if (GameProfiler.Enabled) GameProfiler.Add("#logic frame (total)", tl);
                logicTimeAccumulator -= LogicTime.SecondsPerFrame;
            }
            if (frames >= 5)
                logicTimeAccumulator = 0; // too slow to catch up: drop time rather than spiral
        }
        long t0 = GameProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        water.Update(seconds);
        if (GameProfiler.Enabled) GameProfiler.Add("#client water", t0);
        t0 = GameProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Effects.Update(seconds);
        if (GameProfiler.Enabled) GameProfiler.Add("#client effects", t0);
    }

    public void Render(float seconds, float aspectRatio)
    {
        Camera.AspectRatio = aspectRatio;
        // Camera shake moves the view for this frame only.
        var unshaken = Camera.Target;
        Camera.Target += new Vector3(Effects.ShakeOffset, 0);
        var viewProjection = Camera.ViewProjection;
        shroud?.Refresh();
        terrain.Draw(viewProjection, TerrainLighting);
        roads.Draw(viewProjection, TerrainLighting);
        Effects.RenderGround(viewProjection);

        var frustum = new Frustum(viewProjection);
        visibleInstances.Clear();
        // Objects riding on others (Overlord add-ons) go after their carriers, whose bones they sit on.
        foreach (bool riders in new[] { false, true })
        {
            foreach (var (obj, drawable) in drawables)
            {
                if ((obj.ContainedById != 0) != riders)
                    continue;
                if (obj.IsHiddenInContainer || obj.DrawableHidden || !frustum.Intersects(obj.Position, Math.Max(40, obj.Radius * 2)))
                    continue;
                if (!IsVisibleToViewer(obj))
                    continue;
                drawable.Update(seconds, models);
                if (tdSprites != null && !drawable.HiddenByStealth && TiberianDawn.TdSpriteRenderer.Handles(obj))
                    tdSprites.Add(obj, drawable, Camera);
                if (drawable.HiddenByStealth)
                    continue;
                foreach (var part in drawable.Parts)
                    if (part.Instance != null && !part.Hidden)
                        visibleInstances.Add(part.Instance);
            }
        }
        visibleInstances.AddRange(bridges.Instances());
        tdSprites?.AddAnims(seconds, Camera);
        tdSprites?.Draw(viewProjection, Camera);
        modelRenderer.Draw(visibleInstances, viewProjection, Camera.Position, ObjectLighting, Camera.View);
        water.Draw(viewProjection);
        Effects.Render(viewProjection);
        Camera.Target = unshaken;
    }

    /// <summary>The size of the 3D view in pixels (set by the screen each frame).</summary>
    public Vector2 ViewportSize { get; set; } = new(800, 600);

    /// <summary>Where a screen pixel's ray meets a flat plane at height <paramref name="z"/> (sky pixels hit far away).</summary>
    public Vector3 PlanePointAt(Vector2 pixel, Vector2 viewport, float z)
    {
        var (origin, direction) = Camera.ScreenRay(pixel, viewport);
        if (direction.Z >= -0.0001f)
            return origin + direction * Camera.FarPlane;
        float t = (z - origin.Z) / direction.Z;
        return origin + direction * Math.Min(t, Camera.FarPlane);
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
            if (obj.IsHiddenInContainer || obj.IsEffectivelyDead || filter != null && !filter(obj))
                continue;
            if (!IsVisibleToViewer(obj))
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
        Effects.Dispose();
        tdSprites?.Dispose();
        terrain.Dispose();
        water.Dispose();
        models.Dispose();
        modelRenderer.Dispose();
    }
}

using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.UI;
using OpenGZH.Client.World;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Images;
using OpenGZH.Logic;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Vision;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.InGame;

/// <summary>
/// The radar in the control bar's LeftHUD window, as the original's W3DRadar draws it: a 128x128
/// picture of the map (tile colours, lighter on high ground and darker low down, water in
/// RadarWaterColor), the player's and visible enemy units as 2x2 dots in their player colours
/// (stealthed ones pulse; undetected enemy stealth units don't show), the fog of war, spinning
/// triangles for radar events (under attack, construction, infiltration...) and the camera's view box.
/// It only works while the player has radar (a radar building with power, or a radar upgrade).
/// Clicking moves the camera there - or, with units selected, orders them there.
/// </summary>
public sealed class Radar : IDisposable
{
    const int CellWidth = 128, CellHeight = 128;
    const int OverlayRefreshFrames = 6;
    const int MaxEvents = 64;

    public enum EventType { Invalid, Construction, Upgrade, UnderAttack, Information, BeaconPulse, Infiltration, BattlePlan, StealthDiscovered, StealthNeutralized, Fake }

    struct RadarEvent
    {
        public EventType Type;
        public bool Active;
        public int CreateFrame, DieFrame, FadeFrame;
        public Vector4 Color1, Color2;
        public Vector3 WorldLoc;
        public Vector2 RadarLoc;
        public bool SoundPlayed;
    }

    static readonly Dictionary<EventType, (Vector4, Vector4)> EventColors = new()
    {
        [EventType.Construction] = (Rgba(128, 128, 255), Rgba(128, 255, 255)),
        [EventType.Upgrade] = (Rgba(128, 0, 64), Rgba(255, 185, 220)),
        [EventType.UnderAttack] = (Rgba(255, 0, 0), Rgba(255, 128, 128)),
        [EventType.Information] = (Rgba(255, 255, 0), Rgba(255, 255, 128)),
        [EventType.BeaconPulse] = (Rgba(255, 255, 0), Rgba(255, 255, 128)),
        [EventType.Infiltration] = (Rgba(0, 255, 255), Rgba(128, 255, 255)),
        [EventType.BattlePlan] = (Rgba(255, 255, 255), Rgba(255, 255, 255)),
        [EventType.StealthDiscovered] = (Rgba(0, 255, 0), Rgba(0, 128, 0)),
        [EventType.StealthNeutralized] = (Rgba(0, 255, 0), Rgba(0, 128, 0)),
        [EventType.Fake] = (Vector4.Zero, Vector4.Zero),
    };

    static Vector4 Rgba(int r, int g, int b, int a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    readonly GameLogic game;
    readonly Player player;
    readonly WorldView world;
    readonly Texture terrainTexture;
    readonly Image overlay = new(CellWidth, CellHeight);
    readonly Image shroud = new(CellWidth, CellHeight);
    Texture? overlayTexture, shroudTexture;
    readonly GL gl;
    readonly RadarEvent[] events = new RadarEvent[MaxEvents];
    int nextEvent;
    int lastEvent = -1;
    readonly List<Vector3> heroPositions = new();
    readonly float mapWidth, mapHeight;

    /// <summary>A radar event that should play a sound or show a message (the screen decides).</summary>
    public event Action<string>? Message;
    public event Action<string>? Sound;

    public Radar(GL gl, GameLogic game, Player player, WorldView world)
    {
        this.gl = gl;
        this.game = game;
        this.player = player;
        this.world = world;
        mapWidth = game.Terrain.WorldWidth;
        mapHeight = game.Terrain.WorldHeight;
        terrainTexture = new Texture(gl, BuildTerrainImage(), repeat: false, mipmaps: false);

        game.Events.OnRadarEvent += (type, position, who) =>
        {
            if (who == player && Enum.TryParse<EventType>(type.Replace("_", ""), true, out var t))
            {
                if (t == EventType.Infiltration)
                {
                    Message?.Invoke("RADAR:Infiltration");
                    Sound?.Invoke(game.Catalog.MiscAudio("RadarInfiltrationSound"));
                }
                CreateEvent(position, t);
            }
        };
        game.Events.OnDamaged += OnDamaged;
    }

    // ---- Coordinates ----

    Vector2 WorldToRadar(Vector3 world) => new(world.X / (mapWidth / CellWidth), world.Y / (mapHeight / CellHeight));

    Vector3 RadarToWorld(Vector2 radar)
    {
        float x = (radar.X + 0.5f) * (mapWidth / CellWidth), y = (radar.Y + 0.5f) * (mapHeight / CellHeight);
        return new Vector3(x, y, game.Terrain.GroundHeight(x, y));
    }

    /// <summary>Where the 128x128 radar picture goes inside the window, keeping the map's aspect ratio.</summary>
    (Vector2 Ul, Vector2 Lr) DrawPositions(Rect window)
    {
        float ratioW = mapWidth / window.Width, ratioH = mapHeight / window.Height;
        Vector2 ul, lr;
        if (ratioW >= ratioH)
        {
            var size = new Vector2(mapWidth / ratioW, mapHeight / ratioW);
            ul = new Vector2(0, (window.Height - size.Y) / 2);
            lr = new Vector2(size.X, window.Height - ul.Y);
        }
        else
        {
            var size = new Vector2(mapWidth / ratioH, mapHeight / ratioH);
            ul = new Vector2((window.Width - size.X) / 2, 0);
            lr = new Vector2(window.Width - ul.X, size.Y);
        }
        var origin = new Vector2(window.X, window.Y);
        return (ul + origin, lr + origin);
    }

    /// <summary>A radar cell to a screen pixel (radar Y grows northwards, screen Y downwards).</summary>
    static Vector2 RadarToPixel(Vector2 radar, Vector2 ul, Vector2 size) =>
        new(radar.X * size.X / CellWidth + ul.X, (CellHeight - 1 - radar.Y) * size.Y / CellHeight + ul.Y);

    /// <summary>The world point under a screen pixel in the radar window, if it is on the map picture.</summary>
    public Vector3? PixelToWorld(Rect window, Vector2 pixel)
    {
        var (ul, lr) = DrawPositions(window);
        if (pixel.X < ul.X || pixel.Y < ul.Y || pixel.X > lr.X || pixel.Y > lr.Y)
            return null;
        var size = lr - ul;
        var radar = new Vector2((pixel.X - ul.X) * CellWidth / size.X, CellHeight - 1 - (pixel.Y - ul.Y) * CellHeight / size.Y);
        return RadarToWorld(radar);
    }

    public bool IsActive => player.HasRadar;

    // ---- Terrain picture ----

    Image BuildTerrainImage()
    {
        var image = new Image(CellWidth, CellHeight);
        var terrain = game.Terrain;
        var atlas = world.TerrainAtlas;
        var map = world.Map;
        var water = RuleColor.Parse(game.Rules.Section("WaterTransparency").GetString("RadarWaterColor"), new Vector4(140 / 255f, 140 / 255f, 1, 1));

        // Height range and average for shading.
        float lo = float.MaxValue, hi = float.MinValue, sum = 0;
        int count = 0;
        for (int y = 0; y < CellHeight; y++)
            for (int x = 0; x < CellWidth; x++)
            {
                var p = RadarToWorld(new Vector2(x, y));
                lo = Math.Min(lo, p.Z);
                hi = Math.Max(hi, p.Z);
                if (!terrain.IsUnderwater(p.X, p.Y, out _))
                {
                    sum += p.Z;
                    count++;
                }
            }
        float average = count > 0 ? sum / count : 0;

        Vector3 TerrainColorAt(Vector3 p)
        {
            var heights = map.HeightMap;
            int gx = Math.Clamp((int)MathF.Floor(p.X / Formats.Maps.MapFile.CellSize) + heights.BorderSize, 0, heights.Width - 1);
            int gy = Math.Clamp((int)MathF.Floor(p.Y / Formats.Maps.MapFile.CellSize) + heights.BorderSize, 0, heights.Height - 1);
            int index = gy * heights.Width + gx;
            if (map.BlendTiles == null || index >= map.BlendTiles.TileIndices.Length)
                return Vector3.Zero;
            return atlas.TileAverageColor(map.BlendTiles.TileIndices[index]);
        }

        for (int y = 0; y < CellHeight; y++)
        {
            for (int x = 0; x < CellWidth; x++)
            {
                var center = RadarToWorld(new Vector2(x, y));
                var sample = Vector3.Zero;
                int samples = 0;
                bool underwater = terrain.IsUnderwater(center.X, center.Y, out float waterZ);
                for (int j = y - 1; j <= y + 1; j++)
                    for (int i = x - 1; i <= x + 1; i++)
                    {
                        if (i < 0 || j < 0 || i >= CellWidth || j >= CellHeight)
                            continue;
                        var p = RadarToWorld(new Vector2(i, j));
                        if (underwater)
                        {
                            if (!terrain.IsUnderwater(p.X, p.Y, out _))
                                continue;
                            sample += Shade(new Vector3(water.X, water.Y, water.Z), p.Z, waterZ, waterZ, lo);
                        }
                        else
                            sample += Shade(TerrainColorAt(p), p.Z, average, hi, lo);
                        samples++;
                    }
                var c = sample / Math.Max(1, samples);
                // Radar Y runs up the map; the image's first row is the top of the picture.
                image.SetPixel(x, CellHeight - 1 - y, (byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255), 255);
            }
        }
        return image;
    }

    /// <summary>The original's interpolateColorForHeight: towards white above the middle height, towards black below.</summary>
    static Vector3 Shade(Vector3 color, float height, float hiZ, float midZ, float loZ)
    {
        const float HowBright = 0.95f, HowDark = 0.60f;
        // Note the original passes (average, max, min) as (hi, mid, lo) for land; kept as is.
        if (hiZ == midZ) hiZ = midZ + 0.1f;
        if (midZ == loZ) loZ = midZ - 0.1f;
        if (hiZ == loZ) hiZ = loZ + 0.2f;
        float t;
        Vector3 target;
        if (height >= midZ)
        {
            t = (height - midZ) / (hiZ - midZ);
            target = color + (Vector3.One - color) * HowBright;
        }
        else
        {
            t = (midZ - height) / (midZ - loZ);
            target = color + (Vector3.Zero - color) * HowDark;
        }
        return Vector3.Clamp(color + (target - color) * t, Vector3.Zero, Vector3.One);
    }

    // ---- Objects and shroud ----

    void RefreshOverlay()
    {
        Array.Clear(overlay.Pixels);
        heroPositions.Clear();
        foreach (var obj in game.Objects)
        {
            if (obj.IsDestroyed || obj.IsEffectivelyDead || obj.IsHiddenInContainer || obj.IsRemovedFromPartition)
                continue;
            string priority = obj.Template.Rules.GetString("RadarPriority", "INVALID").ToUpperInvariant();
            if (priority is "INVALID" or "NOT_ON_RADAR")
                continue;
            bool local = obj.Owner == player;
            if (local && obj.IsKindOf("HERO"))
                heroPositions.Add(obj.Position);
            if (!local && game.Shroud.StatusOf(obj, player) > ObjectShroud.PartialClear)
                continue;
            if (priority == "LOCAL_UNIT_ONLY" && !local)
                continue;
            var color = RuleColor.FromRgb(obj.Owner.Color);
            if (obj.HasStatus(Status.Stealthed))
            {
                if (player.IsEnemyOf(obj.Owner) && !obj.HasStatus(Status.Detected) && !obj.HasStatus("DISGUISED"))
                    continue;
                // Pulse between faint and full.
                const int Transition = LogicTime.FramesPerSecond;
                const float MinAlpha = 32 / 255f;
                float scale = game.Frame % Transition / (Transition / 2f);
                color.W = scale > 0 ? (scale - 1) * (1 - MinAlpha) + MinAlpha : scale * (1 - MinAlpha) + MinAlpha;
                color.W = Math.Clamp(Math.Abs(color.W), 0, 1);
            }
            var r = WorldToRadar(obj.Position);
            int rx = (int)r.X, ry = (int)r.Y;
            for (int dy = 0; dy <= 1; dy++)
                for (int dx = 0; dx <= 1; dx++)
                {
                    int x = rx + dx, y = ry + dy;
                    if (x >= 0 && y >= 0 && x < CellWidth && y < CellHeight)
                        overlay.SetPixel(x, CellHeight - 1 - y, (byte)(color.X * 255), (byte)(color.Y * 255), (byte)(color.Z * 255), (byte)(color.W * 255));
                }
        }
        Upload(ref overlayTexture, overlay);

        // Shroud: black, half-transparent over fog, clear where seen.
        var sm = game.Shroud;
        for (int y = 0; y < CellHeight; y++)
            for (int x = 0; x < CellWidth; x++)
            {
                var p = RadarToWorld(new Vector2(x, y));
                byte alpha = sm.StatusAt(player, p) switch { CellShroud.Shrouded => 255, CellShroud.Fogged => 127, _ => 0 };
                shroud.SetPixel(x, CellHeight - 1 - y, 0, 0, 0, alpha);
            }
        Upload(ref shroudTexture, shroud);
    }

    void Upload(ref Texture? texture, Image image)
    {
        texture?.Dispose();
        texture = new Texture(gl, image, repeat: false, mipmaps: false, nearest: true);
    }

    // ---- Events ----

    void CreateEvent(Vector3 worldPos, EventType type, float secondsToLive = 4)
    {
        var (c1, c2) = EventColors.TryGetValue(type, out var colors) ? colors : (Vector4.One, Vector4.One);
        int now = game.Frame;
        ref var e = ref events[nextEvent];
        e.Type = type;
        e.Active = true;
        e.CreateFrame = now;
        e.DieFrame = now + (int)(LogicTime.FramesPerSecond * secondsToLive);
        e.FadeFrame = e.DieFrame - (int)(LogicTime.FramesPerSecond * 0.5f);
        e.Color1 = c1;
        e.Color2 = c2;
        e.WorldLoc = worldPos;
        e.RadarLoc = WorldToRadar(worldPos);
        e.SoundPlayed = false;
        if (type != EventType.BeaconPulse)
            lastEvent = nextEvent;
        nextEvent = (nextEvent + 1) % MaxEvents;
    }

    /// <summary>Where the last radar event happened (the original's "jump to last event" key).</summary>
    public Vector3? LastEventLocation => lastEvent >= 0 ? events[lastEvent].WorldLoc : null;

    bool TryEvent(EventType type, Vector3 pos)
    {
        int now = game.Frame;
        const float CloseEnoughSq = 250f * 250f;
        const int FramesBetween = LogicTime.FramesPerSecond * 10;
        foreach (var e in events)
        {
            if (e.Type != type)
                continue;
            // The original's distance has an operator-precedence slip (a - b*a - b), which makes it
            // nearly always "close"; so in practice one event of a type per 10 seconds. Kept.
            float distSq = e.WorldLoc.X - pos.X * e.WorldLoc.X - pos.X + e.WorldLoc.Y - pos.Y * e.WorldLoc.Y - pos.Y;
            if (distSq <= CloseEnoughSq && now - e.CreateFrame < FramesBetween)
                return false;
        }
        CreateEvent(pos, type);
        return true;
    }

    void OnDamaged(GameObject obj, DamageInfo damage)
    {
        if (obj.Owner != player || damage.ActualDamageDealt <= 0 || damage.DamageType is "PENALTY" or "HEALING")
            return;
        if (game.FindObject(damage.SourceId)?.Owner == player)
            return;
        if (!TryEvent(EventType.UnderAttack, obj.Position))
            return;
        if (obj.IsKindOf(Kind.Infantry) || obj.IsKindOf("VEHICLE"))
        {
            bool harvester = obj.IsKindOf("HARVESTER");
            Message?.Invoke(harvester ? "RADAR:HarvesterUnderAttack" : "RADAR:UnitUnderAttack");
            Sound?.Invoke(game.Catalog.MiscAudio(harvester ? "RadarHarvesterUnderAttackSound" : "RadarStructureUnderAttackSound"));
        }
        else if (obj.IsKindOf(Kind.Structure) && obj.IsKindOf("MP_COUNT_FOR_VICTORY"))
        {
            game.Events.Eva("BaseUnderAttack", player);
            Message?.Invoke("RADAR:StructureUnderAttack");
            Sound?.Invoke(game.Catalog.MiscAudio("RadarStructureUnderAttackSound"));
        }
        else
        {
            Message?.Invoke("RADAR:UnderAttack");
            Sound?.Invoke(game.Catalog.MiscAudio("RadarStructureUnderAttackSound"));
        }
    }

    // ---- Drawing ----

    public void Draw(SpriteBatch batch, Rect window, ImageCatalog images)
    {
        if (!IsActive)
            return;
        var (ul, lr) = DrawPositions(window);
        var size = lr - ul;
        var fill = new Vector4(0, 0, 0, 1);
        var line = Rgba(50, 50, 50);
        if (mapWidth / window.Width >= mapHeight / window.Height)
        {
            batch.FillRect(new Rect(window.X, window.Y, window.Width, ul.Y - window.Y - 1), fill);
            batch.FillRect(new Rect(window.X, lr.Y + 1, window.Width, window.Bottom - lr.Y - 1), fill);
            batch.DrawLine(new Vector2(window.X, ul.Y), new Vector2(window.Right, ul.Y), line);
            batch.DrawLine(new Vector2(window.X, lr.Y + 1), new Vector2(window.Right, lr.Y + 1), line);
        }
        else
        {
            batch.FillRect(new Rect(window.X, window.Y, ul.X - window.X - 1, window.Height), fill);
            batch.FillRect(new Rect(lr.X + 1, window.Y, window.Right - lr.X - 1, window.Height), fill);
            batch.DrawLine(new Vector2(ul.X, window.Y), new Vector2(ul.X, window.Bottom), line);
            batch.DrawLine(new Vector2(lr.X + 1, window.Y), new Vector2(lr.X + 1, window.Bottom), line);
        }
        var picture = new Rect(ul.X, ul.Y, size.X, size.Y);
        batch.DrawTexture(terrainTexture, picture);
        if (overlayTexture == null || game.Frame % OverlayRefreshFrames == 0)
            RefreshOverlay();
        batch.DrawTexture(overlayTexture!, picture);
        batch.DrawTexture(shroudTexture!, picture);

        // Heroes get a reticle.
        foreach (var hero in heroPositions)
        {
            var p = RadarToPixel(WorldToRadar(hero), ul, size);
            images.Draw(batch, "HeroReticle", new Rect(p.X - 7, p.Y - 7, 15, 15));
        }

        DrawEvents(batch, ul, size);
        DrawViewBox(batch, ul, size, window);
    }

    void DrawEvents(SpriteBatch batch, Vector2 ul, Vector2 size)
    {
        int now = game.Frame;
        for (int i = 0; i < MaxEvents; i++)
        {
            ref var e = ref events[i];
            if (!e.Active || e.Type == EventType.Fake)
                continue;
            if (now >= e.DieFrame)
            {
                e.Active = false;
                continue;
            }
            if (!e.SoundPlayed && e.Type != EventType.BeaconPulse)
                Sound?.Invoke("RadarEvent");
            e.SoundPlayed = true;

            bool beacon = e.Type == EventType.BeaconPulse;
            float maxSize = size.X / (beacon ? 10f : 2f);
            const float FullToSmall = LogicTime.FramesPerSecond * 1.5f;
            int frameDiff = now - e.CreateFrame;
            float eventSize = Math.Max(6, (int)(maxSize * (1 - frameDiff / FullToSmall)));
            float spin = 2 * MathF.PI * (frameDiff / FullToSmall) * (beacon ? -1 : 1);
            var tri = new Vector2[3];
            float[] angles = [0 - spin, 2 * MathF.PI / 3 - spin, -2 * MathF.PI / 3 - spin];
            for (int k = 0; k < 3; k++)
            {
                // The triangle is sized in radar cells, then mapped to pixels.
                var radar = new Vector2(MathF.Cos(angles[k]) * eventSize, MathF.Sin(angles[k]) * eventSize) * (CellWidth / size.X) + e.RadarLoc;
                tri[k] = RadarToPixel(radar, ul, size);
            }
            var c1 = e.Color1;
            var c2 = e.Color2;
            if (now > e.FadeFrame)
            {
                float fade = 1 - (float)(now - e.FadeFrame) / (e.DieFrame - e.FadeFrame);
                c1.W *= fade;
                c2.W *= fade;
            }
            var clip = new Rect(ul.X, ul.Y, size.X, size.Y);
            for (int k = 0; k < 3; k++)
                if (Clip(tri[k], tri[(k + 1) % 3], clip, out var a, out var b))
                    batch.DrawLine(a, b, Vector4.Lerp(c1, c2, 0.5f));
        }
    }

    void DrawViewBox(SpriteBatch batch, Vector2 ul, Vector2 size, Rect window)
    {
        var viewport = world.ViewportSize;
        Vector2[] screen = [new(0, 0), new(viewport.X, 0), new(viewport.X, viewport.Y), new(0, viewport.Y)];
        var corners = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            var ground = world.GroundPointAt(screen[i], viewport) ?? world.PlanePointAt(screen[i], viewport, game.Terrain.GroundHeight(world.Camera.Target));
            corners[i] = RadarToPixel(WorldToRadar(ground), ul, size);
        }
        var top = Rgba(225, 225, 0);
        var bottom = Rgba(158, 158, 0);
        Vector4[] colors = [top, Vector4.Lerp(top, bottom, 0.5f), bottom, Vector4.Lerp(bottom, top, 0.5f)];
        for (int i = 0; i < 4; i++)
            if (Clip(corners[i], corners[(i + 1) % 4], window, out var a, out var b))
                batch.DrawLine(a, b, colors[i]);
    }

    /// <summary>Liang-Barsky line clipping to the radar window.</summary>
    static bool Clip(Vector2 p0, Vector2 p1, Rect r, out Vector2 a, out Vector2 b)
    {
        float t0 = 0, t1 = 1;
        var d = p1 - p0;
        bool Edge(float p, float q)
        {
            if (p == 0)
                return q >= 0;
            float t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        a = b = default;
        if (!Edge(-d.X, p0.X - r.X) || !Edge(d.X, r.Right - p0.X) || !Edge(-d.Y, p0.Y - r.Y) || !Edge(d.Y, r.Bottom - p0.Y))
            return false;
        a = p0 + d * t0;
        b = p0 + d * t1;
        return true;
    }

    public void Dispose()
    {
        terrainTexture.Dispose();
        overlayTexture?.Dispose();
        shroudTexture?.Dispose();
    }
}

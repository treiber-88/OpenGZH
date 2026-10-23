using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.World;
using OpenGZH.Core.Rules;
using OpenGZH.Logic;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Effects;
using OpenGZH.Logic.Modules.SpecialPowers;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Templates;
using OpenGZH.Logic.Vision;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.Effects;

/// <summary>
/// Everything the game shows that is not a model or the terrain: particle systems, FX lists, laser
/// beams, tracers, scorch marks and camera shake. It listens to the simulation's effect events, keeps
/// the lasting effects the logic registers (<see cref="EffectRegistry"/>) in step, and draws it all.
/// </summary>
public sealed class EffectsView : IFxHost, IParticleWorld, IDisposable
{
    readonly WorldView world;
    readonly TextureCache textures;
    readonly EffectsRenderer renderer;
    readonly GameLogic game;
    readonly Dictionary<int, int> registrySystems = new();
    readonly List<TracerEffect> tracers = new();
    readonly List<ScorchMark> scorches = new();
    readonly Dictionary<string, LaserLook?> laserLooks = new(StringComparer.OrdinalIgnoreCase);
    int lastLogicFrame = -1;
    float clock;

    /// <summary>Per object: the draw state whose ParticleSysBone systems are running, and those systems.</summary>
    readonly Dictionary<uint, (Logic.Art.ModelConditionState State, List<int> Systems)> boneSystems = new();

    // Camera shake (the original's W3DView::shake).
    float shakeIntensity, shakeCos, shakeSin;
    readonly float[] shakeIntensities;
    readonly float maxShakeIntensity, maxShakeRange;

    public ParticleSystemManager Particles { get; }
    public FxPlayer Fx { get; }

    /// <summary>Plays a sound (set by whoever owns audio; none until then).</summary>
    public Action<string, Vector3?, GameObject?>? SoundSink { get; set; }

    /// <summary>How far the camera is pushed by shaking this frame.</summary>
    public Vector2 ShakeOffset { get; private set; }

    sealed class TracerEffect
    {
        public Vector3 Position, Direction;
        public float Speed, Length, Width, Opacity;
        public Vector3 Color;
        public int ExpiresFrame;
    }

    record struct ScorchMark(Vector3 Position, float Radius, int Type);

    sealed record LaserLook(Texture? Texture, int NumBeams, float InnerWidth, float OuterWidth, Vector4 InnerColor, Vector4 OuterColor,
        float ScrollRate, bool Tile, int Segments, float ArcHeight, float SegmentOverlap, float TilingScalar, float Aspect);

    /// <summary>Particle systems the logic asked for by handle (damage smoke it stops again).</summary>
    readonly Dictionary<uint, int> logicParticleHandles = new();

    public EffectsView(GL gl, WorldView world, TextureCache textures)
    {
        this.world = world;
        this.textures = textures;
        game = world.Game;
        renderer = new EffectsRenderer(gl, textures);
        Particles = new ParticleSystemManager(this, game.Catalog.ParticleSystem);
        var data = game.Catalog.GameData;
        Particles.ParticleScale = RuleValue.Float(data.Get("ParticleScale"), 1);
        Particles.MaxParticleCount = RuleValue.Int(data.Get("MaxParticleCount"), 2500);
        Particles.MaxFieldParticleCount = RuleValue.Int(data.Get("MaxFieldParticleCount"), 30);
        Fx = new FxPlayer(this, game.Catalog.FxList);
        shakeIntensities =
        [
            RuleValue.Float(data.Get("ShakeSubtleIntensity"), 0.5f), RuleValue.Float(data.Get("ShakeNormalIntensity"), 1),
            RuleValue.Float(data.Get("ShakeStrongIntensity"), 2.5f), RuleValue.Float(data.Get("ShakeSevereIntensity"), 5),
            RuleValue.Float(data.Get("ShakeCineExtremeIntensity"), 7.5f), RuleValue.Float(data.Get("ShakeCineInsaneIntensity"), 10),
        ];
        maxShakeIntensity = RuleValue.Float(data.Get("MaxShakeIntensity"), 10);
        maxShakeRange = RuleValue.Float(data.Get("MaxShakeRange"), 150);

        game.Events.OnFx += Fx.Play;
        game.Events.OnWeaponFireFx += OnWeaponFireFx;
        game.Events.OnParticleSystem += (name, position, attachedTo) =>
        {
            if (Particles.CreateSystem(name) is not { } system)
                return;
            if (attachedTo != null)
                system.AttachToObject(attachedTo);
            else
                system.SetPosition(position);
        };
        game.Events.OnAttachedParticleSystem += (handle, name, local, attachedTo, lifetime, delay) =>
        {
            if (Particles.CreateSystem(name) is not { } system)
                return;
            system.SetPosition(local);
            system.AttachToObject(attachedTo);
            if (lifetime > 0)
                system.SetSystemLifetime(lifetime);
            if (delay > 0)
                system.SetInitialDelay(delay);
            logicParticleHandles[handle] = system.Id;
        };
        game.Events.OnStopParticleSystem += handle =>
        {
            if (logicParticleHandles.Remove(handle, out int id))
                Particles.Destroy(id);
        };
        game.Events.OnObjectDestroyed += Particles.DestroyAttachedSystems;
        game.Effects.OnScorch += Scorch;
    }

    // ---- IParticleWorld / IFxHost ----

    public int Frame => game.Frame;
    public GameObject? FindObject(uint id) => game.FindObject(id);
    public Matrix4x4 ObjectTransform(GameObject obj) => Logic.Art.ModelBones.ObjectTransform(obj);
    public bool IsHiddenFromLocalPlayer(GameObject obj) => world.Viewer is { } viewer && game.Shroud.StatusOf(obj, viewer) >= ObjectShroud.Fogged;
    public float GroundHeight(float x, float y) => game.Terrain.GroundHeight(x, y);
    public float LayerHeight(float x, float y) => game.Terrain.LayerHeight(x, y);
    public bool IsPointVisible(Vector3 position) => world.Viewer is not { } viewer || game.Shroud.StatusAt(viewer, position) == CellShroud.Clear;
    public bool IsObjectVisible(GameObject obj) => world.Viewer is not { } viewer || game.Shroud.StatusOf(obj, viewer) <= ObjectShroud.PartialClear;
    public bool TryGetBone(GameObject obj, string bone, out Matrix4x4 boneWorld) => game.Bones.TryGetBone(obj, bone, out boneWorld);

    public void PlaySound(string name, Vector3? position, GameObject? source) => SoundSink?.Invoke(name, position, source);

    public void Shake(Vector3 epicenter, string type)
    {
        int index = type.ToUpperInvariant() switch
        {
            "SUBTLE" => 0,
            "NORMAL" => 1,
            "STRONG" => 2,
            "SEVERE" => 3,
            "CINE_EXTREME" => 4,
            "CINE_INSANE" => 5,
            _ => 1,
        };
        float angle = ClientRandom.Real(0, MathF.Tau);
        shakeCos = MathF.Cos(angle);
        shakeSin = MathF.Sin(angle);
        var camera = world.Camera.Target;
        float distance = Vector2.Distance(new Vector2(epicenter.X, epicenter.Y), new Vector2(camera.X, camera.Y));
        if (distance > maxShakeRange)
            return;
        shakeIntensity += shakeIntensities[index] * (1f - distance / maxShakeRange);
        // (The original caps at 3 once past MaxShakeIntensity.)
        if (shakeIntensity > maxShakeIntensity)
            shakeIntensity = 3;
    }

    public void Scorch(Vector3 position, float radius, int type)
    {
        if (scorches.Count >= 500)
            scorches.RemoveAt(0);
        scorches.Add(new ScorchMark(position, radius, type));
    }

    /// <summary>Light pulses need dynamic lights, which the renderer does not have yet.</summary>
    public void LightPulse(Vector3 position, Vector3 color, float radius, int increaseFrames, int decreaseFrames) { }

    public void Tracer(Vector3 from, Vector3 to, float speed, float length, float width, Vector3 color, float decayAt)
    {
        var dir = to - from;
        if (dir.LengthSquared() == 0)
            return;
        dir = Vector3.Normalize(dir);
        float distance = Vector3.Distance(from, to) - length;
        float frames = distance >= 0 && speed >= 0 && speed > 0 ? distance / speed : 1;
        tracers.Add(new TracerEffect
        {
            Position = from, Direction = dir, Speed = speed, Length = length, Width = width, Color = color, Opacity = 1,
            ExpiresFrame = game.Frame + (int)MathF.Ceiling(frames * decayAt),
        });
    }

    // ---- Weapon fire FX ----

    void OnWeaponFireFx(WeaponFireFx fx)
    {
        var shooter = fx.Shooter;
        bool locallyControlled = world.Viewer == null || shooter.Owner == world.Viewer;
        if (!locallyControlled && shooter.HasStatus(Status.Stealthed) && !shooter.HasStatus(Status.Detected)
            && !shooter.HasStatus("DISGUISED") && !shooter.IsKindOf("MINE") && !fx.PlayWhenStealthed)
            return;
        // At the barrel's fire-FX bone if the model has one (the original's handleWeaponFireFX).
        if (game.Bones.CurrentState(shooter) is { } state)
        {
            string slotName = ((Logic.Combat.WeaponSlot)fx.Slot).ToString().ToUpperInvariant();
            if (state.FireFxBone.TryGetValue(slotName, out var bone) && bone.Length > 0
                && (game.Bones.TryGetBone(shooter, $"{bone}{fx.Barrel + 1:00}", out var at) || game.Bones.TryGetBone(shooter, bone, out at)))
            {
                Fx.DoFxPos(fx.Name, at.Translation, at, fx.Speed, fx.Target, fx.Radius);
                return;
            }
        }
        var where = fx.IsContactWeapon ? fx.Target : shooter.Position;
        Fx.DoFxPos(fx.Name, where, ObjectTransform(shooter), fx.Speed, fx.Target, fx.Radius);
    }

    // ---- Update ----

    /// <summary>Steps effects once per logic frame and follows the logic's lasting effects.</summary>
    public void Update(float seconds)
    {
        clock += seconds;
        SyncRegistry();
        if (game.Frame == lastLogicFrame)
            return;
        lastLogicFrame = game.Frame;
        UpdateBoneParticleSystems();
        Particles.Update();

        for (int i = tracers.Count - 1; i >= 0; i--)
        {
            var t = tracers[i];
            int left = t.ExpiresFrame - game.Frame;
            if (left <= 0)
            {
                tracers.RemoveAt(i);
                continue;
            }
            t.Opacity -= t.Opacity / left;
            t.Position += t.Direction * t.Speed;
        }

        if (shakeIntensity > 0.01f)
        {
            ShakeOffset = new Vector2(shakeIntensity * shakeCos, shakeIntensity * shakeSin);
            shakeIntensity *= 0.75f;
            shakeCos = -shakeCos;
            shakeSin = -shakeSin;
        }
        else
        {
            shakeIntensity = 0;
            ShakeOffset = Vector2.Zero;
        }
    }

    /// <summary>
    /// The model's own particle systems ("ParticleSysBone: Bone System" in a draw state: smoke stacks,
    /// steam, glows), started at their bones when the object enters the state and stopped when it leaves.
    /// </summary>
    readonly Dictionary<string, bool> followsBones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// AnimatedParticleSysBoneClientUpdate: the model's particle systems follow their bones as the
    /// model animates (a rotating radar dish's light, a moving crane's smoke) instead of staying put.
    /// </summary>
    void FollowAnimatedBones(GameObject obj, Logic.Art.ModelConditionState state, List<int> systems)
    {
        if (!followsBones.TryGetValue(obj.Template.Name, out bool follows))
            followsBones[obj.Template.Name] = follows = obj.Template.ModulesOfType("ClientUpdate")
                .Any(m => m.ClassName.Equals("AnimatedParticleSysBoneClientUpdate", StringComparison.OrdinalIgnoreCase));
        if (!follows || !world.Drawables.TryGetValue(obj, out var drawable))
            return;
        // Bone positions from the drawn model, relative to the object the systems are attached to.
        Matrix4x4.Invert(ObjectTransform(obj), out var toObject);
        int i = 0;
        foreach (var (bone, _) in state.ParticleBones)
        {
            if (i >= systems.Count)
                break;
            if (drawable.TryGetBoneWorld(bone, out var boneWorld) && Particles.Find(systems[i]) is { } system)
                system.SetLocalTransform(boneWorld * toObject);
            i++;
        }
    }

    void UpdateBoneParticleSystems()
    {
        foreach (var obj in world.Drawables.Keys)
        {
            var state = game.Bones.CurrentState(obj);
            bool has = boneSystems.TryGetValue(obj.Id, out var current);
            if (has && current.State == state)
            {
                FollowAnimatedBones(obj, current.State, current.Systems);
                continue;
            }
            if (has)
            {
                foreach (int id in current.Systems)
                    Particles.Destroy(id);
                boneSystems.Remove(obj.Id);
            }
            if (state == null || state.ParticleBones.Count == 0 || obj.IsEffectivelyDead)
                continue;
            var systems = new List<int>();
            Matrix4x4.Invert(ObjectTransform(obj), out var toObject);
            foreach (var (bone, systemName) in state.ParticleBones)
            {
                if (Particles.CreateSystem(systemName) is not { } system)
                {
                    systems.Add(0);   // (keeps the list in step with the state's bones)
                    continue;
                }
                var local = game.Bones.TryGetBone(obj, bone, out var boneWorld) ? boneWorld * toObject : Matrix4x4.Identity;
                system.SetLocalTransform(local);
                system.AttachToObject(obj);
                systems.Add(system.Id);
            }
            boneSystems[obj.Id] = (state, systems);
        }
        if (boneSystems.Count > world.Drawables.Count)
            foreach (var id in boneSystems.Keys.Where(id => game.FindObject(id) == null).ToList())
                boneSystems.Remove(id);
    }

    /// <summary>Creates client systems for particle systems the logic registered, and ends the ones it removed.</summary>
    void SyncRegistry()
    {
        var live = new HashSet<int>();
        foreach (var record in game.Effects.ParticleSystems)
        {
            live.Add(record.Id);
            if (registrySystems.ContainsKey(record.Id))
                continue;
            var system = Particles.CreateSystem(record.Template);
            registrySystems[record.Id] = system?.Id ?? 0;
            if (system == null)
                continue;
            var orientation = record.Orientation;
            orientation.Translation = record.Position;
            system.SetLocalTransform(orientation);
            if (record.AttachedTo != 0 && game.FindObject(record.AttachedTo) is { } attached)
                system.AttachToObject(attached);
        }
        if (registrySystems.Count == live.Count)
            return;
        foreach (var id in registrySystems.Keys.Where(id => !live.Contains(id)).ToList())
        {
            Particles.Destroy(registrySystems[id]);
            registrySystems.Remove(id);
        }
    }

    // ---- Drawing ----

    /// <summary>Scorch marks lie on the terrain: draw them right after it, before the models.</summary>
    public void RenderGround(Matrix4x4 viewProjection)
    {
        var decals = game.Effects.RadiusDecals.ToList();
        if (scorches.Count == 0 && decals.Count == 0)
            return;
        renderer.Begin(world.Camera);
        DrawRadiusDecals(decals);
        var texture = textures.Get("EXScorch01.tga", repeat: false);
        var light = world.TerrainLighting.Ambient + world.TerrainLighting.Colors[0] / 2;
        var color = new Vector4(Math.Min(1, light.X), Math.Min(1, light.Y), Math.Min(1, light.Z), 1);
        const int perRow = 3;
        const float cell = Formats.Maps.MapFile.CellSize;
        foreach (var s in scorches)
        {
            int type = s.Type is >= 0 and < 9 ? s.Type : 0;
            float uOffset = type % perRow * 1.5f, vOffset = type / perRow * 1.5f;
            float r = Math.Max(1, s.Radius);
            int minX = (int)MathF.Floor((s.Position.X - r) / cell), minY = (int)MathF.Floor((s.Position.Y - r) / cell);
            int maxX = (int)MathF.Ceiling((s.Position.X + r) / cell) + 1, maxY = (int)MathF.Ceiling((s.Position.Y + r) / cell) + 1;
            Vector3 Corner(int i, int j)
            {
                float x = i * cell, y = j * cell;
                return new Vector3(x, y, game.Terrain.GroundHeight(x, y) + 0.25f);
            }
            Vector2 UV(Vector3 p) => new((uOffset + 0.5f + (p.X - s.Position.X) / (2 * r)) / (perRow + 1),
                (vOffset + 0.5f + (p.Y - s.Position.Y) / (2 * r)) / (perRow + 1));
            for (int j = minY; j < maxY - 1; j++)
                for (int i = minX; i < maxX - 1; i++)
                {
                    var a = Corner(i, j); var b = Corner(i + 1, j); var c = Corner(i + 1, j + 1); var d = Corner(i, j + 1);
                    renderer.Triangle(texture, EffectBlend.Alpha, a, UV(a), b, UV(b), c, UV(c), color);
                    renderer.Triangle(texture, EffectBlend.Alpha, a, UV(a), c, UV(c), d, UV(d), color);
                }
        }
        renderer.Draw(viewProjection);
    }

    /// <summary>
    /// Radius decals (the original's RadiusDecal): the template's texture laid over the ground in a
    /// square twice the radius across, tinted its Color (or the owner's), its opacity throbbing between
    /// OpacityMin and OpacityMax every OpacityThrobTime. Some are only shown to their owner.
    /// </summary>
    void DrawRadiusDecals(List<Logic.Effects.RadiusDecal> decals)
    {
        const float cell = Formats.Maps.MapFile.CellSize;
        foreach (var d in decals)
        {
            var t = d.Template;
            if (t.OnlyVisibleToOwningPlayer && world.Viewer is { } viewer && viewer.Index != d.OwnerIndex)
                continue;
            var texture = textures.Get(t.Texture, repeat: false);
            var rgb = t.Color.Length > 0 ? RuleColor.Parse(t.Color, Vector4.One) : RuleColor.FromRgb(d.OwnerColor);
            float theta = 2 * MathF.PI * (game.Frame % t.ThrobFrames) / t.ThrobFrames;
            float percent = 0.5f * (MathF.Sin(theta) + 1);
            float opacity = t.OpacityMin + percent * (t.OpacityMax - t.OpacityMin);
            var blend = t.Style.Contains("ADDITIVE", StringComparison.OrdinalIgnoreCase) ? EffectBlend.Additive : EffectBlend.Alpha;
            var color = blend == EffectBlend.Additive ? new Vector4(rgb.X * opacity, rgb.Y * opacity, rgb.Z * opacity, 1) : new Vector4(rgb.X, rgb.Y, rgb.Z, opacity);
            float r = d.Radius;
            var center = d.Position;
            int minX = (int)MathF.Floor((center.X - r) / cell), minY = (int)MathF.Floor((center.Y - r) / cell);
            int maxX = (int)MathF.Ceiling((center.X + r) / cell), maxY = (int)MathF.Ceiling((center.Y + r) / cell);
            Vector3 Corner(float x, float y) => new(x, y, game.Terrain.LayerHeight(x, y) + 0.3f);
            Vector2 UV(Vector3 p) => new(0.5f + (p.X - center.X) / (2 * r), 0.5f - (p.Y - center.Y) / (2 * r));
            for (int j = minY; j < maxY; j++)
                for (int i = minX; i < maxX; i++)
                {
                    // Clip each terrain cell to the decal square so nothing is drawn outside it.
                    float x0 = Math.Max(i * cell, center.X - r), x1 = Math.Min((i + 1) * cell, center.X + r);
                    float y0 = Math.Max(j * cell, center.Y - r), y1 = Math.Min((j + 1) * cell, center.Y + r);
                    if (x1 <= x0 || y1 <= y0)
                        continue;
                    var a = Corner(x0, y0); var b = Corner(x1, y0); var c = Corner(x1, y1); var e = Corner(x0, y1);
                    renderer.Triangle(texture, blend, a, UV(a), b, UV(b), c, UV(c), color);
                    renderer.Triangle(texture, blend, a, UV(a), c, UV(c), e, UV(e), color);
                }
        }
    }

    /// <summary>Particles, beams and tracers: draw after everything solid.</summary>
    public void Render(Matrix4x4 viewProjection)
    {
        renderer.Begin(world.Camera);
        DrawParticles();
        DrawBeams();
        DrawTracers();
        DrawProjectileStreams();
        DrawRopes();
        renderer.Draw(viewProjection);
    }

    static EffectBlend BlendFor(ParticleShader shader) => shader switch
    {
        ParticleShader.Additive => EffectBlend.Additive,
        ParticleShader.AlphaTest => EffectBlend.AlphaTest,
        ParticleShader.Multiply => EffectBlend.Multiply,
        _ => EffectBlend.Alpha,
    };

    readonly List<Vector3> streakPoints = new();
    readonly List<float> streakWidths = new();
    readonly List<Vector4> streakColors = new();

    void DrawParticles()
    {
        int field = 0;
        foreach (var system in Particles.Systems)
        {
            if (system.ParticleCount == 0 || system.Type is ParticleType.Drawable or ParticleType.Smudge)
                continue;
            var texture = textures.Get(system.ParticleName);
            var blend = BlendFor(system.Template.Shader);
            if (system.Type == ParticleType.Streak && system.ParticleCount >= 2)
            {
                streakPoints.Clear();
                streakWidths.Clear();
                streakColors.Clear();
                foreach (var p in system.Particles)
                {
                    streakPoints.Add(p.Position);
                    streakWidths.Add(p.Size);
                    streakColors.Add(new Vector4(p.Color, p.Alpha));
                }
                renderer.Ribbon(texture, blend, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(streakPoints),
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(streakWidths), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(streakColors));
                continue;
            }
            bool ground = system.IsGroundAligned;
            if (ground && system.Priority == ParticlePriority.AreaEffect)
                field += system.ParticleCount;
            foreach (var p in system.Particles)
                renderer.Sprite(texture, blend, p.Position, p.Size, p.Angle, new Vector4(p.Color, p.Alpha), ground);
        }
        Particles.FieldParticleCount = field;
    }

    LaserLook? LookFor(string templateName)
    {
        if (laserLooks.TryGetValue(templateName, out var look))
            return look;
        look = null;
        if (game.Templates.Find(templateName) is { } t
            && t.ModulesOfType("Draw").FirstOrDefault(m => m.ClassName.Equals("W3DLaserDraw", StringComparison.OrdinalIgnoreCase)) is { } d)
        {
            var texture = textures.Get(d.Get("Texture"));
            look = new LaserLook(texture, Math.Clamp(d.GetInt("NumBeams", 1), 1, 16), d.GetFloat("InnerBeamWidth"), d.GetFloat("OuterBeamWidth", 1),
                RuleColor.Parse(d.Get("InnerColor"), Vector4.One), RuleColor.Parse(d.Get("OuterColor"), Vector4.One),
                d.GetFloat("ScrollRate"), d.GetBool("Tile"), Math.Clamp(d.GetInt("Segments", 1), 1, 64), d.GetFloat("ArcHeight"),
                d.GetFloat("SegmentOverlapRatio"), Math.Max(0.01f, d.GetFloat("TilingScalar", 1)),
                texture != null && texture.Height > 0 ? texture.Width / (float)texture.Height : 1);
        }
        laserLooks[templateName] = look;
        return look;
    }

    /// <summary>A beam as the original's W3DLaserDraw draws it: NumBeams layers from inner to outer, optionally arcing.</summary>
    void DrawLaser(LaserLook look, Vector3 start, Vector3 end, float widthScale)
    {
        if (widthScale <= 0)
            return;
        var line = end - start;
        float lineLength = line.Length();
        var middle = (start + end) * 0.5f;
        float half = lineLength * 0.5f;
        for (int segment = 0; segment < look.Segments; segment++)
        {
            Vector3 a, b;
            if (look.ArcHeight > 0 && look.Segments > 1 && half > 0)
            {
                float startRatio = segment / (float)look.Segments, endRatio = (segment + 1f) / look.Segments;
                if (segment > 0) startRatio -= look.SegmentOverlap;
                if (segment < look.Segments - 1) endRatio += look.SegmentOverlap;
                a = start + line * startRatio;
                b = start + line * endRatio;
                a.Z += MathF.Cos(Vector3.Distance(middle, a) / half * MathF.PI * 0.5f) * look.ArcHeight;
                b.Z += MathF.Cos(Vector3.Distance(middle, b) / half * MathF.PI * 0.5f) * look.ArcHeight;
                a.Z = Math.Max(a.Z, 2 + game.Terrain.GroundHeight(a.X, a.Y));
                b.Z = Math.Max(b.Z, 2 + game.Terrain.GroundHeight(b.X, b.Y));
            }
            else
            {
                a = start;
                b = end;
            }
            for (int i = look.NumBeams - 1; i >= 0; i--)
            {
                float width;
                Vector3 rgb;
                var inner = look.InnerColor;
                var outer = look.OuterColor;
                if (look.NumBeams == 1)
                {
                    width = look.InnerWidth * widthScale;
                    rgb = new Vector3(inner.X, inner.Y, inner.Z) * inner.W;
                }
                else
                {
                    float scale = i / (look.NumBeams - 1f);
                    width = (look.InnerWidth + scale * (look.OuterWidth - look.InnerWidth)) * widthScale;
                    // (As the original: only the difference is weighted by the inner alpha.)
                    rgb = new Vector3(inner.X + scale * (outer.X - inner.X) * inner.W, inner.Y + scale * (outer.Y - inner.Y) * inner.W,
                        inner.Z + scale * (outer.Z - inner.Z) * inner.W);
                }
                if (width <= 0)
                    continue;
                float tiles = look.Texture != null && look.Tile ? Vector3.Distance(a, b) / width * look.Aspect * look.TilingScalar : 1;
                var scroll = new Vector2(look.ScrollRate * clock, 0);
                renderer.Beam(look.Texture, EffectBlend.Additive, a, b, width, new Vector4(rgb, 1), tiles, scroll);
            }
        }
    }

    void DrawBeams()
    {
        foreach (var beam in game.Effects.Beams)
            if (LookFor(beam.Template) is { } look)
                DrawLaser(look, beam.Start, beam.End, beam.WidthScale);

        // Beams that are objects in the logic (a Pathfinder's laser, a hacker's data stream).
        foreach (var obj in world.Drawables.Keys)
        {
            if (obj.FindModule<LaserUpdate>() is not { } laser || LookFor(obj.Template.Name) is not { } look)
                continue;
            if (game.FindObject(laser.SourceId) is not { } source)
                continue;
            var start = laser.SourceBone.Length > 0 ? game.Bones.BonePosition(source, laser.SourceBone) : source.Position;
            var end = game.FindObject(laser.TargetId)?.Position ?? obj.Position;
            if (world.IsVisibleToViewer(source))
                DrawLaser(look, start, end, 1);
        }
    }

    sealed record StreamLook(Texture? Texture, float Width, float TileFactor, float ScrollRate, int MaxSegments);
    readonly Dictionary<string, StreamLook?> streamLooks = new(StringComparer.OrdinalIgnoreCase);

    StreamLook? StreamLookFor(GameObject obj)
    {
        if (streamLooks.TryGetValue(obj.Template.Name, out var look))
            return look;
        look = obj.Template.ModulesOfType("Draw").FirstOrDefault(m => m.ClassName.Equals("W3DProjectileStreamDraw", StringComparison.OrdinalIgnoreCase)) is { } d
            ? new StreamLook(textures.Get(d.Get("Texture")), d.GetFloat("Width"), d.GetFloat("TileFactor"), d.GetFloat("ScrollRate"), d.GetInt("MaxSegments"))
            : null;
        streamLooks[obj.Template.Name] = look;
        return look;
    }

    /// <summary>
    /// Toxin and flame streams (the original's W3DProjectileStreamDraw): a textured line through the
    /// stream's projectiles, broken where one has gone, scrolling along, additive.
    /// </summary>
    void DrawProjectileStreams()
    {
        foreach (var obj in world.Drawables.Keys)
        {
            if (obj.FindModule<ProjectileStreamUpdate>() is not { } stream || StreamLookFor(obj) is not { } look)
                continue;
            var all = stream.AllPoints();
            int first = look.MaxSegments > 0 ? Math.Max(0, all.Count - look.MaxSegments) : 0;
            var run = new List<Vector3>();
            void Flush()
            {
                if (run.Count > 1)
                {
                    Span<float> widths = stackalloc float[run.Count];
                    widths.Fill(look.Width);
                    var colors = new Vector4[run.Count];
                    Array.Fill(colors, Vector4.One);
                    renderer.Ribbon(look.Texture, EffectBlend.Additive, run.ToArray(), widths, colors, look.TileFactor,
                        new Vector2(look.ScrollRate * clock, 0), tilePerPoint: true);
                }
                run.Clear();
            }
            for (int i = first; i < all.Count; i++)
            {
                if (all[i] is { } p && p != Vector3.Zero)
                    run.Add(p);
                else
                    Flush();
            }
            Flush();
        }
    }

    /// <summary>
    /// Chinook rappel ropes (the original's W3DRopeDraw on GenericRope): a dark line hanging from each
    /// rope bone, in RopeWobbleLen segments whose ends sway on their own axis, drawn as a solid core
    /// with a half-transparent edge.
    /// </summary>
    void DrawRopes()
    {
        foreach (var obj in world.Drawables.Keys)
        {
            if (obj.AI is not Logic.Modules.AI.ChinookAIUpdate chinook || !world.IsVisibleToViewer(obj))
                continue;
            var module = obj.Template.ModulesOfType("Behavior").FirstOrDefault(m => m.ClassName.Equals("ChinookAIUpdate", StringComparison.OrdinalIgnoreCase));
            if (module == null)
                continue;
            float width = module.GetFloat("RopeWidth", 0.5f);
            float wobbleLen = Math.Max(1, module.GetFloat("RopeWobbleLen", 10));
            float wobbleAmp = module.GetFloat("RopeWobbleAmplitude");
            // Degrees per second in the rules; the original advanced the phase once per frame.
            float wobbleRate = module.GetFloat("RopeWobbleRate") * MathF.PI / 180;
            var color = RuleColor.Parse(module.Get("RopeColor"), new Vector4(0, 0, 0, 1));
            float deflection = MathF.Sin(clock * wobbleRate) * wobbleAmp;
            int ropeIndex = 0;
            foreach (var (top, length) in chinook.Ropes)
            {
                ropeIndex++;
                if (length <= 0)
                    continue;
                int segments = (int)MathF.Ceiling(length / wobbleLen);
                float eachLen = length / segments;
                var start = top;
                for (int s = 0; s < segments; s++)
                {
                    // Each segment sways along its own fixed axis (random in the original).
                    float axis = (obj.Id * 7919 + ropeIndex * 104729 + s * 15485863) % 6283 / 1000f;
                    var end = new Vector3(top.X + deflection * MathF.Cos(axis), top.Y + deflection * MathF.Sin(axis), start.Z - eachLen);
                    renderer.Beam(null, EffectBlend.Alpha, start, end, width, new Vector4(color.X, color.Y, color.Z, 0.5f));
                    renderer.Beam(null, EffectBlend.Alpha, start, end, width * 0.5f, new Vector4(color.X, color.Y, color.Z, 1));
                    start = end;
                }
            }
        }
    }

    void DrawTracers()
    {
        foreach (var t in tracers)
        {
            var end = t.Position + t.Direction * t.Length;
            renderer.Beam(null, EffectBlend.Alpha, t.Position, end, t.Width, new Vector4(t.Color, t.Opacity));
        }
    }

    public void Dispose() => renderer.Dispose();
}

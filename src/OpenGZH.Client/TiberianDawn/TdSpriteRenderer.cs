using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.World;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Westwood;
using OpenGZH.Logic.Objects;
using OpenGZH.TiberianDawn;
using OpenGZH.TiberianDawn.Art;
using OpenGZH.TiberianDawn.Logic;
using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Types;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.TiberianDawn;

/// <summary>
/// Draws Tiberian Dawn objects in a Zero Hour engine match (Draw module TDSpriteDraw) with their original
/// sprites: upright quads facing the camera, 1.5 world units a pixel (a 24-pixel TD cell is 36 units),
/// the frame for the facing as seen from the camera, in the owner's colour. Each sprite is depth-tested
/// as a whole at its ground point: drawn before the 3D models, it is never cut by the terrain, and models in front still hide it.
/// </summary>
public sealed class TdSpriteRenderer : IDisposable
{
    public const float UnitsPerPixel = 1.5f;

    const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec2 aUV;
        layout(location = 2) in vec3 aAnchor;
        layout(location = 3) in vec4 aColor;
        uniform mat4 uViewProjection;
        out vec2 vUV;
        out vec4 vColor;
        out vec2 vWorld;
        flat out float vDepth;
        void main()
        {
            vUV = aUV;
            vColor = aColor;
            vWorld = aAnchor.xy;
            vec4 anchor = uViewProjection * vec4(aAnchor, 1.0);
            vDepth = clamp(anchor.z / anchor.w * 0.5 + 0.5, 0.0, 1.0);
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec2 vUV;
        in vec4 vColor;
        in vec2 vWorld;
        flat in float vDepth;
        uniform sampler2D uTexture;
        uniform sampler2D uShroud;
        uniform vec2 uShroudScale;
        out vec4 fragColor;
        void main()
        {
            vec4 c = texture(uTexture, vUV) * vColor;
            if (c.a < 0.02)
                discard;
            if (uShroudScale.x > 0.0)
                c.rgb *= texture(uShroud, vWorld * uShroudScale).r;
            fragColor = c;
            gl_FragDepth = vDepth;
        }
        """;

    const int FloatsPerVertex = 12;

    readonly GL gl;
    readonly TdArt art;
    readonly TdTypes types;
    readonly ShaderProgram shader;
    readonly uint vao, vbo;
    readonly Dictionary<(KeyframeShape Shape, int Frame, uint Color, int Mode), Texture?> textures = new();
    readonly Dictionary<GameObject, Info?> infos = new();
    readonly Dictionary<uint, int> createdFrame = new();
    readonly List<TdShapeLayer> layers = new();
    readonly List<(float Distance, int Order, Texture Texture, Vector3 Center, Vector3 Anchor, float HalfW, float HalfH, Vector4 Color)> quads = new();
    float[] vertices = new float[FloatsPerVertex * 6 * 256];

    /// <summary>What the exported Draw module says the object is.</summary>
    sealed record Info(string Kind, string Shape, object? Type, Vector3 ArtOffset);

    public World.ShroudTexture? Shroud { get; set; }

    TdSpriteRenderer(GL gl, TdContent content)
    {
        this.gl = gl;
        art = TiberianDawnContent.Art ?? new TdArt(content, TheaterType.THEATER_TEMPERATE);
        types = new TdTypes(content.Rules);
        shader = new ShaderProgram(gl, VertexSource, FragmentSource);
        vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        unsafe
        {
            uint stride = FloatsPerVertex * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
            gl.EnableVertexAttribArray(3);
            gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        }
        gl.BindVertexArray(0);
    }

    /// <summary>A renderer, if the original Tiberian Dawn files are installed.</summary>
    public static TdSpriteRenderer? TryCreate(GL gl) =>
        TiberianDawnContent.Get() is { } content ? new TdSpriteRenderer(gl, content) : null;

    /// <summary>Whether this object is drawn as a TD sprite.</summary>
    public static bool Handles(GameObject obj) =>
        obj.Template.ModulesOfType("Draw").Any(m => m.ClassName.Equals("TDSpriteDraw", StringComparison.OrdinalIgnoreCase));

    /// <summary>The object appeared (built, produced, or there from the start): its build-up plays from now.</summary>
    public void OnCreated(GameObject obj, bool atStart) => createdFrame[obj.Id] = atStart ? int.MinValue / 2 : obj.Game.Frame;

    public void OnDestroyed(GameObject obj)
    {
        infos.Remove(obj);
        createdFrame.Remove(obj.Id);
    }

    Info? InfoFor(GameObject obj)
    {
        if (infos.TryGetValue(obj, out var info))
            return info;
        var draw = obj.Template.ModulesOfType("Draw").FirstOrDefault(m => m.ClassName.Equals("TDSpriteDraw", StringComparison.OrdinalIgnoreCase));
        if (draw != null)
        {
            string kind = draw.Settings.GetString("TdKind");
            string typeName = draw.Settings.GetString("TdType");
            object? type = kind switch
            {
                "Building" when Enum.TryParse<StructType>(typeName, out var t) => types.Building(t),
                "Unit" when Enum.TryParse<UnitType>(typeName, out var t) => types.Unit(t),
                "Infantry" when Enum.TryParse<InfantryType>(typeName, out var t) => types.Infantry(t),
                "Aircraft" when Enum.TryParse<AircraftType>(typeName, out var t) => types.Aircraft(t),
                "Tiberium" => RuleValue.Int(typeName),
                "Bullet" when Enum.TryParse<BulletType>(typeName, out var t) => types.Bullet(t),
                _ => null,
            };
            var offset = obj.Template.Rules.GetString("TdArtOffset") is { Length: > 0 } o ? RuleValue.Coord(o) : Vector3.Zero;
            info = new Info(kind, draw.Settings.GetString("Shape"), type, offset with { Z = 0 });
        }
        return infos[obj] = info;
    }

    /// <summary>Collects the object's sprite layers for this frame (call after the drawable has updated).</summary>
    public void Add(GameObject obj, Drawable drawable, Camera camera)
    {
        if (InfoFor(obj) is not { } info || art.Shape(info.Shape) is not { } own)
            return;
        var view = camera.View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        float screenUp = MathF.Atan2(camera.ScreenUp.Y, camera.ScreenUp.X);
        int created = createdFrame.TryGetValue(obj.Id, out int f) ? f : int.MinValue / 2;
        float turret = obj.AI is { Turrets.Count: > 0 } ai ? obj.Orientation + ai.Turrets[0].Angle : obj.Orientation;
        var state = new TdSpriteState
        {
            Dir = TdSpriteLayers.Dir(obj.Orientation, screenUp),
            TurretDir = TdSpriteLayers.Dir(turret, screenUp),
            HealthRatio = obj.MaxHealth > 0 ? (int)(obj.Health / obj.MaxHealth * 256) : 256,
            Age = Math.Max(0, obj.Game.Frame - created) / (float)Logic.Core.LogicTime.FramesPerSecond,
            Moving = obj.AI?.IsMoving ?? false,
            Firing = drawable.SecondsSinceFired < 0.3f,
            Harvesting = obj.AI is Logic.Modules.Supply.SupplyTruckAIUpdate { IsDocking: true } truck
                && obj.Game.FindObject(truck.DockTargetId)?.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>() != null,
            Dying = obj.IsEffectivelyDead,
            Altitude = (int)(Math.Max(0, obj.HeightAboveTerrain) / UnitsPerPixel),
            Ammo = 2,
        };
        layers.Clear();
        switch (info.Type)
        {
            case BuildingTypeClass b:
                TdSpriteLayers.Building(b, state, art.Shape(b.IniName + "MAKE")?.FrameCount ?? 0, own.FrameCount, layers);
                break;
            case UnitTypeClass u:
                TdSpriteLayers.Unit(u, state, layers);
                break;
            case InfantryTypeClass i:
                TdSpriteLayers.Infantry(i, state, layers);
                break;
            case AircraftTypeClass a:
                TdSpriteLayers.Aircraft(a, state, layers);
                break;
            case int stage:
                layers.Add(new TdShapeLayer(null, Math.Clamp(stage, 0, own.FrameCount - 1), 0, 0, TdShapeFlags.None));
                break;
            case BulletTypeClass bullet:
            {
                // BulletClass::Shape_Number: turned to its flight direction (unless faceless); napalm and grenades tumble.
                float heading = obj.FindModule<Logic.Modules.Updates.PhysicsBehavior>() is { } physics && physics.Velocity with { Z = 0 } is { } v && v.LengthSquared() > 1e-4f
                    ? MathF.Atan2(v.Y, v.X) : obj.Orientation;
                int shapenum = bullet.IsFaceless ? 0 : TdMath.BodyShape[TdMath.Facing32[TdSpriteLayers.Dir(heading, screenUp)]];
                int ticks = (int)(state.Age * TdSpriteLayers.TicksPerSecond);
                if (bullet.Type == BulletType.BULLET_NAPALM)
                    shapenum += ticks % 6;
                if (bullet.Type == BulletType.BULLET_GRENADE)
                    shapenum += ticks % 8;
                layers.Add(new TdShapeLayer(null, Math.Clamp(shapenum, 0, own.FrameCount - 1), 0, 0, bullet.IsTranslucent ? TdShapeFlags.Ghost : TdShapeFlags.None));
                break;
            }
            default:
                layers.Add(new TdShapeLayer(null, 0, 0, 0, TdShapeFlags.None));
                break;
        }

        uint team = obj.Owner.IsNeutral ? 0xFFFFFFFF : obj.Owner.Color & 0xFFFFFF;
        var anchor = obj.Position + info.ArtOffset;
        float distance = Vector3.DistanceSquared(camera.Position, anchor);
        float opacity = drawable.HiddenByStealth ? 0 : 1;
        foreach (var layer in layers)
        {
            var shape = layer.File != null ? art.Shape(layer.File) : own;
            if (shape == null)
                continue;
            int mode = (layer.Flags & TdShapeFlags.Shadow) != 0 ? 1 : (layer.Flags & TdShapeFlags.Ghost) != 0 ? 2 : 0;
            uint color = (layer.Flags & TdShapeFlags.Remap) != 0 ? team : 0xFFFFFFFF;
            if (TextureFor(shape, layer.Frame, color, mode) is not { } texture)
                continue;
            var center = anchor + right * (layer.Dx * UnitsPerPixel) - up * (layer.Dy * UnitsPerPixel);
            // Shadows go just behind their owner so the owner draws over them.
            quads.Add((distance + (mode == 1 ? 1 : 0), quads.Count, texture, center, anchor, shape.Width * UnitsPerPixel / 2, shape.Height * UnitsPerPixel / 2,
                new Vector4(1, 1, 1, opacity)));
        }
    }

    // ---- TD animations (muzzle flashes, explosions): AnimClass, played on their own ----

    sealed class PlayingAnim
    {
        public required AnimTypeClass Type;
        public required Vector3 Position;
        public float Ticks;
        public int Loops;
    }

    readonly List<PlayingAnim> anims = new();

    /// <summary>
    /// Plays a TD animation ("ANIM_PIFF") at a point. Directional ones (TechnoClass::Fire_At's gun, chem and
    /// flame flashes) take the eighth of the compass the shot went towards.
    /// </summary>
    public void PlayAnim(string animName, Vector3 position, float? towards = null, Camera? camera = null)
    {
        if (!Enum.TryParse<AnimType>(animName, out var type) || type == AnimType.ANIM_NONE)
            return;
        if (type is AnimType.ANIM_GUN_N or AnimType.ANIM_CHEM_N or AnimType.ANIM_FLAME_N && towards is { } angle && camera != null)
        {
            byte dir = TdSpriteLayers.Dir(angle, MathF.Atan2(camera.ScreenUp.Y, camera.ScreenUp.X));
            type = (AnimType)((int)type + ((dir + 16) / 32 % 8));
        }
        var cls = types.Anim(type);
        anims.Add(new PlayingAnim { Type = cls, Position = position, Loops = Math.Max(cls.Loops, 1) });
    }

    /// <summary>Steps the animations (AnimClass::AI: a frame each Delay ticks, loops, then ChainTo) and collects their frames.</summary>
    public void AddAnims(float seconds, Camera camera)
    {
        var view = camera.View;
        for (int i = anims.Count - 1; i >= 0; i--)
        {
            var a = anims[i];
            a.Ticks += seconds * TdSpriteLayers.TicksPerSecond;
            var cls = a.Type;
            if (art.Shape(cls.IniName) is not { } shape)
            {
                anims.RemoveAt(i);
                continue;
            }
            int stages = cls.Stages > 0 ? cls.Stages : shape.FrameCount - cls.Start;
            int stage = (int)(a.Ticks / Math.Max(cls.Delay, 1));
            bool looping = a.Loops > 1 && cls.LoopEnd > cls.LoopStart;
            if (looping ? stage >= cls.LoopEnd - cls.Start : stage >= stages)
            {
                if (--a.Loops > 0 && looping)
                {
                    a.Ticks = (cls.LoopStart - cls.Start) * Math.Max(cls.Delay, 1);
                    stage = cls.LoopStart - cls.Start;
                }
                else if (cls.ChainTo != AnimType.ANIM_NONE)
                {
                    a.Type = types.Anim(cls.ChainTo);
                    a.Ticks = 0;
                    a.Loops = Math.Max(a.Type.Loops, 1);
                    continue;
                }
                else
                {
                    anims.RemoveAt(i);
                    continue;
                }
            }
            int frame = cls.Start + stage;
            if (TextureFor(shape, frame, 0xFFFFFFFF, cls.IsTranslucent ? 2 : 0) is not { } texture)
                continue;
            float distance = Vector3.DistanceSquared(camera.Position, a.Position) - 2; // over what it hit
            quads.Add((distance, quads.Count, texture, a.Position, a.Position, shape.Width * UnitsPerPixel / 2, shape.Height * UnitsPerPixel / 2, Vector4.One));
        }
    }

    Texture? TextureFor(KeyframeShape shape, int frame, uint color, int mode)
    {
        var key = (shape, frame, color, mode);
        if (textures.TryGetValue(key, out var texture))
            return texture;
        var image = color == 0xFFFFFFFF
            ? art.Frame(shape, frame, null, shadowOnly: mode == 1, ghost: mode == 2)
            : art.Frame(shape, frame, ((byte)(color >> 16), (byte)(color >> 8), (byte)color), shadowOnly: mode == 1, ghost: mode == 2);
        texture = image != null ? new Texture(gl, image, repeat: false, mipmaps: false, nearest: true) : null;
        return textures[key] = texture;
    }

    /// <summary>Draws everything collected since the last call, far to near, and starts over.</summary>
    public unsafe void Draw(Matrix4x4 viewProjection, Camera camera)
    {
        if (quads.Count == 0)
            return;
        // Far to near; an object's layers in the order Draw_It draws them.
        quads.Sort((a, b) => b.Distance != a.Distance ? b.Distance.CompareTo(a.Distance) : a.Order.CompareTo(b.Order));
        var view = camera.View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);

        int needed = quads.Count * 6 * FloatsPerVertex;
        if (vertices.Length < needed)
            Array.Resize(ref vertices, needed);
        int v = 0;
        foreach (var q in quads)
        {
            var r = right * q.HalfW;
            var u = up * q.HalfH;
            Put(q.Center - r + u, 0, 0); Put(q.Center + r + u, 1, 0); Put(q.Center + r - u, 1, 1);
            Put(q.Center - r + u, 0, 0); Put(q.Center + r - u, 1, 1); Put(q.Center - r - u, 0, 1);

            void Put(Vector3 p, float s, float t)
            {
                vertices[v++] = p.X; vertices[v++] = p.Y; vertices[v++] = p.Z;
                vertices[v++] = s; vertices[v++] = t;
                vertices[v++] = q.Anchor.X; vertices[v++] = q.Anchor.Y; vertices[v++] = q.Anchor.Z;
                vertices[v++] = q.Color.X; vertices[v++] = q.Color.Y; vertices[v++] = q.Color.Z; vertices[v++] = q.Color.W;
            }
        }

        shader.Use();
        shader.Set("uViewProjection", viewProjection);
        shader.Set("uTexture", 0);
        shader.Set("uShroud", 2);
        shader.Set("uShroudScale", Shroud != null ? new Vector2(1 / Shroud.WorldWidth, 1 / Shroud.WorldHeight) : Vector2.Zero);
        Shroud?.Bind(2);
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(v * sizeof(float)), p, BufferUsageARB.StreamDraw);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(true);
        gl.Disable(EnableCap.CullFace);
        // The terrain never hides a sprite (TD art stands on flat ground, and its lower half reaches over
        // the ground in front); sprites are painted far to near and write their anchor's depth, so 3D
        // models drawn afterwards still go behind or in front of them correctly.
        gl.DepthFunc(DepthFunction.Always);
        for (int i = 0; i < quads.Count; i++)
        {
            quads[i].Texture.Bind(0);
            gl.DrawArrays(PrimitiveType.Triangles, i * 6, 6);
        }
        gl.BindVertexArray(0);
        gl.DepthFunc(DepthFunction.Less);
        gl.Disable(EnableCap.Blend);
        quads.Clear();
    }

    public void Dispose()
    {
        foreach (var texture in textures.Values)
            texture?.Dispose();
        shader.Dispose();
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
    }
}

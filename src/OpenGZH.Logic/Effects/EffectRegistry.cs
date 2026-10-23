using System.Numerics;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Effects;

/// <summary>
/// A beam drawn between two points or objects (the original's laser drawables with a LaserUpdate
/// client module): it can widen in over a number of frames and decay out again. The template is an
/// object template with a W3DLaserDraw module, which says how the client draws it. Its current width
/// matters to the logic too (the Particle Cannon's damage radius follows the beam's width).
/// </summary>
public sealed class LaserBeam
{
    public int Id { get; init; }
    public required string Template { get; init; }
    public Vector3 Start { get; private set; }
    public Vector3 End { get; private set; }

    /// <summary>The object the beam starts at (and its bone), or 0 for a fixed start.</summary>
    public uint ParentId { get; private set; }
    public string ParentBone { get; private set; } = "";

    /// <summary>The object the beam ends at, or 0 for a fixed end.</summary>
    public uint TargetId { get; private set; }

    /// <summary>Half the template's outer beam width (W3DLaserDraw's getLaserTemplateWidth).</summary>
    public float TemplateRadius { get; init; }

    /// <summary>How far the end overshoots once the target is gone (LaserUpdate's PunchThroughScalar).</summary>
    public float PunchThroughScalar { get; init; }

    /// <summary>0..1: how wide the beam is now, growing in and decaying out.</summary>
    public float WidthScale { get; private set; } = 1;

    /// <summary>Muzzle and target particle systems (LaserUpdate's MuzzleParticleSystem / TargetParticleSystem).</summary>
    public string MuzzleParticleSystem { get; init; } = "";
    public string TargetParticleSystem { get; init; } = "";

    bool widening, decaying;
    int widenStart, widenFinish, decayStart, decayFinish;

    /// <summary>The original's initLaser: sets the ends (objects win over fixed points) and the size change.</summary>
    public void Init(GameLogic game, GameObject? parent, GameObject? target, Vector3? start, Vector3? end, string parentBone = "", int sizeDeltaFrames = 0)
    {
        int now = game.Frame;
        if (sizeDeltaFrames > 0)
        {
            widening = true;
            widenStart = now;
            widenFinish = now + sizeDeltaFrames;
            WidthScale = 0;
        }
        else if (sizeDeltaFrames < 0)
        {
            decaying = true;
            decayStart = now;
            decayFinish = now - sizeDeltaFrames;
            WidthScale = 1;
        }
        ParentBone = parentBone;
        if (parent != null)
        {
            ParentId = parent.Id;
            UpdateStart(game);
        }
        else if (start is { } s)
            Start = s;
        if (target != null && end == null)
        {
            TargetId = target.Id;
            End = target.Position;
        }
        else if (end is { } e)
            End = e;
    }

    public void SetDecayFrames(int frames, int now)
    {
        if (frames <= 0)
            return;
        decaying = true;
        decayStart = now;
        decayFinish = now + frames;
        WidthScale = 1;
    }

    /// <summary>The beam's radius now (template width times the growth scale).</summary>
    public float CurrentRadius => TemplateRadius * WidthScale;

    void UpdateStart(GameLogic game)
    {
        if (ParentId == 0 || game.FindObject(ParentId) is not { } parent)
            return;
        Start = ParentBone.Length > 0 ? game.Bones.BonePosition(parent, ParentBone) : parent.Position;
    }

    void UpdateEnd(GameLogic game)
    {
        if (TargetId == 0)
            return;
        var target = game.FindObject(TargetId);
        if (target == null || target.IsEffectivelyDead)
        {
            if (PunchThroughScalar > 0)
                End = Start + (End - Start) * PunchThroughScalar;
            TargetId = 0;
        }
        else
            End = target.Position;
    }

    /// <summary>The original's LaserUpdate::clientUpdate: follow the ends and grow or decay.</summary>
    internal void Update(GameLogic game)
    {
        UpdateStart(game);
        UpdateEnd(game);
        int now = game.Frame;
        if (decaying)
        {
            WidthScale = 1f - (now - decayStart) / (float)Math.Max(1, decayFinish - decayStart);
            if (WidthScale <= 0)
                WidthScale = 0;
        }
        else if (widening)
        {
            WidthScale = (now - widenStart) / (float)Math.Max(1, widenFinish - widenStart);
            if (WidthScale >= 1)
            {
                WidthScale = 1;
                widening = false;
            }
        }
    }
}

/// <summary>A particle system that stays until it is removed (the original's systems kept by ID).</summary>
public sealed record PersistentParticleSystem(int Id, string Template, Vector3 Position, Matrix4x4 Orientation, uint AttachedTo);

/// <summary>A looping sound that plays until stopped (the original's audio handles).</summary>
public sealed record LoopingSound(int Id, string AudioEvent, uint ObjectId, Vector3 Position);

/// <summary>
/// How a radius decal looks (the original's RadiusDecalTemplate): a texture projected on the ground,
/// its opacity throbbing between OpacityMin and OpacityMax every OpacityThrobTime, tinted Color (the
/// owner's color when none), optionally shown only to the owner.
/// </summary>
public sealed record RadiusDecalTemplate(string Texture, string Style, float OpacityMin, float OpacityMax, int ThrobFrames, string Color, bool OnlyVisibleToOwningPlayer)
{
    public static RadiusDecalTemplate? Parse(OpenGZH.Core.Rules.RuleNode? node)
    {
        if (node == null || !node.IsMapping || node.GetString("Texture") is not { Length: > 0 } texture)
            return null;
        static float Percent(string text, float fallback) =>
            text.Length == 0 ? fallback : OpenGZH.Core.Rules.RuleValue.Float(text, fallback * 100) / (text.Contains('%') ? 100 : 1);
        return new RadiusDecalTemplate(texture, node.GetString("Style", "SHADOW_ALPHA_DECAL"),
            Percent(node.GetString("OpacityMin"), 1), Percent(node.GetString("OpacityMax"), 1),
            Math.Max(1, OpenGZH.Logic.Core.LogicTime.Frames(OpenGZH.Core.Rules.RuleValue.Float(node.GetString("OpacityThrobTime"), 1000))),
            node.GetString("Color"), OpenGZH.Core.Rules.RuleValue.Bool(node.GetString("OnlyVisibleToOwningPlayer")));
    }
}

/// <summary>A radius decal on the ground; gone when <see cref="AttachedTo"/> (if set) is.</summary>
public sealed record RadiusDecal(int Id, RadiusDecalTemplate Template, Vector3 Position, float Radius, int OwnerIndex, uint OwnerColor, uint AttachedTo);

/// <summary>
/// Effects that last (beams, particle systems and sound loops that modules keep and later remove),
/// which the client draws and plays. One-off effects go through <see cref="GameEvents"/> instead.
/// Nothing the logic decides depends on these except beam widths, which are computed here
/// deterministically from logic frames.
/// </summary>
public sealed class EffectRegistry
{
    readonly GameLogic game;
    readonly Dictionary<int, LaserBeam> beams = new();
    readonly Dictionary<int, PersistentParticleSystem> particleSystems = new();
    readonly Dictionary<int, LoopingSound> sounds = new();
    readonly Dictionary<int, RadiusDecal> decals = new();
    int nextId = 1;

    public EffectRegistry(GameLogic game) => this.game = game;

    public IEnumerable<LaserBeam> Beams => beams.Values;
    public IEnumerable<PersistentParticleSystem> ParticleSystems => particleSystems.Values;
    public IEnumerable<LoopingSound> Sounds => sounds.Values;

    /// <summary>Radius decals on the ground (ones whose object is gone are dropped).</summary>
    public IEnumerable<RadiusDecal> RadiusDecals
    {
        get
        {
            foreach (var dead in decals.Values.Where(d => d.AttachedTo != 0 && game.FindObject(d.AttachedTo) is not { IsDestroyed: false }).ToList())
                decals.Remove(dead.Id);
            return decals.Values;
        }
    }

    /// <summary>Puts down a radius decal (the original's createRadiusDecal); 0 if there is nothing to show.</summary>
    public int CreateRadiusDecal(RadiusDecalTemplate? template, Vector3 position, float radius, Players.Player owner, GameObject? attachedTo = null)
    {
        if (template == null || radius <= 0)
            return 0;
        int id = nextId++;
        decals[id] = new RadiusDecal(id, template, position, radius, owner.Index, owner.Color, attachedTo?.Id ?? 0);
        return id;
    }

    public void DestroyRadiusDecal(int id) => decals.Remove(id);

    /// <summary>A scorch mark on the ground (Particle Cannon's trail): position, radius, which of the four marks.</summary>
    public event Action<Vector3, float, int>? OnScorch;

    public void Scorch(Vector3 position, float radius, int type) => OnScorch?.Invoke(position, radius, type);

    /// <summary>Makes a beam from a laser object template (it needs a W3DLaserDraw); null if the template is unknown.</summary>
    public LaserBeam? CreateBeam(string template)
    {
        if (game.Templates.Find(template) is not { } t)
            return null;
        var draw = t.ModulesOfType("Draw").FirstOrDefault(m => m.ClassName.Equals("W3DLaserDraw", StringComparison.OrdinalIgnoreCase));
        var update = t.Modules.FirstOrDefault(m => m.ClassName.Equals("LaserUpdate", StringComparison.OrdinalIgnoreCase));
        var beam = new LaserBeam
        {
            Id = nextId++,
            Template = t.Name,
            TemplateRadius = (draw?.GetFloat("OuterBeamWidth", 1) ?? 1) * 0.5f,
            PunchThroughScalar = update?.GetFloat("PunchThroughScalar") ?? 0,
            MuzzleParticleSystem = update?.Get("MuzzleParticleSystem") ?? "",
            TargetParticleSystem = update?.Get("TargetParticleSystem") ?? "",
        };
        beams[beam.Id] = beam;
        return beam;
    }

    public LaserBeam? FindBeam(int id) => id != 0 ? beams.GetValueOrDefault(id) : null;

    public void DestroyBeam(int id) => beams.Remove(id);

    public int CreateParticleSystem(string template, Vector3 position, Matrix4x4? orientation = null, GameObject? attachedTo = null)
    {
        if (template.Length == 0)
            return 0;
        int id = nextId++;
        particleSystems[id] = new PersistentParticleSystem(id, template, position, orientation ?? Matrix4x4.Identity, attachedTo?.Id ?? 0);
        return id;
    }

    public void DestroyParticleSystem(int id) => particleSystems.Remove(id);

    public int StartSound(string audioEvent, GameObject? obj, Vector3? position = null)
    {
        if (audioEvent.Length == 0)
            return 0;
        int id = nextId++;
        sounds[id] = new LoopingSound(id, audioEvent, obj?.Id ?? 0, position ?? obj?.Position ?? Vector3.Zero);
        return id;
    }

    public void StopSound(int id) => sounds.Remove(id);

    internal void Update()
    {
        foreach (var beam in beams.Values)
            beam.Update(game);
    }
}

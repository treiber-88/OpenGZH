using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Modules;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Terrain;

/// <summary>A bridge style from the Bridges section of the roads-and-bridges rules (the original's TerrainRoadType for bridges).</summary>
public sealed class BridgeTemplate
{
    public string Name { get; }
    public float BridgeScale { get; }
    readonly RuleNode rules;

    public BridgeTemplate(string name, RuleNode rules)
    {
        Name = name;
        this.rules = rules;
        BridgeScale = RuleValue.Float(rules.GetString("BridgeScale"), 1);
    }

    public string Get(string key) => rules.GetString(key);

    /// <summary>Model and texture for a damage state.</summary>
    public (string Model, string Texture) Look(BodyDamageState state) => state switch
    {
        BodyDamageState.Damaged => (Get("BridgeModelNameDamaged"), Get("TextureDamaged")),
        BodyDamageState.ReallyDamaged => (Get("BridgeModelNameReallyDamaged"), Get("TextureReallyDamaged")),
        BodyDamageState.Rubble => (Get("BridgeModelNameBroken"), Get("TextureBroken")),
        _ => (Get("BridgeModelName"), Get("Texture")),
    };

    public string TowerObjectName(BridgeTowerType type) => type switch
    {
        BridgeTowerType.FromLeft => Get("TowerObjectNameFromLeft"),
        BridgeTowerType.FromRight => Get("TowerObjectNameFromRight"),
        BridgeTowerType.ToLeft => Get("TowerObjectNameToLeft"),
        _ => Get("TowerObjectNameToRight"),
    };

    // (repair?, state, effect number 0-based) -> name
    Dictionary<(bool, BodyDamageState, int), string>? oclTransitions, fxTransitions;

    Dictionary<(bool, BodyDamageState, int), string> Transitions(string key, string nameKey)
    {
        var result = new Dictionary<(bool, BodyDamageState, int), string>();
        if (!rules.TryGet(key, out var node))
            return result;
        foreach (var entry in node.AsList())
        {
            bool repair = false;
            var state = BodyDamageState.Pristine;
            int effect = 0;
            string name = "";
            foreach (var word in (entry.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = word.IndexOf(':');
                if (colon < 0)
                    continue;
                string k = word[..colon], v = word[(colon + 1)..];
                if (k.Equals("Transition", StringComparison.OrdinalIgnoreCase))
                    repair = v.Equals("Repair", StringComparison.OrdinalIgnoreCase);
                else if (k.Equals("ToState", StringComparison.OrdinalIgnoreCase))
                    state = v.ToUpperInvariant() switch
                    {
                        "DAMAGED" => BodyDamageState.Damaged,
                        "REALLYDAMAGED" => BodyDamageState.ReallyDamaged,
                        "RUBBLE" => BodyDamageState.Rubble,
                        _ => BodyDamageState.Pristine,
                    };
                else if (k.Equals("EffectNum", StringComparison.OrdinalIgnoreCase))
                    effect = RuleValue.Int(v, 1) - 1;
                else if (k.Equals(nameKey, StringComparison.OrdinalIgnoreCase))
                    name = v;
            }
            result[(repair, state, effect)] = name;
        }
        return result;
    }

    public const int MaxBridgeBodyFx = 3;

    string Lookup(Dictionary<(bool, BodyDamageState, int), string> table, bool repair, BodyDamageState state, int i) =>
        table.TryGetValue((repair, state, i), out var name) ? name : "";

    /// <summary>Effects when the bridge gets worse or is repaired, by the state it reaches.</summary>
    public string DamageToOcl(BodyDamageState state, int i) => Lookup(oclTransitions ??= Transitions("TransitionToOCL", "OCL"), false, state, i);
    public string DamageToFx(BodyDamageState state, int i) => Lookup(fxTransitions ??= Transitions("TransitionToFX", "FX"), false, state, i);
    public string RepairedToOcl(BodyDamageState state, int i) => Lookup(oclTransitions ??= Transitions("TransitionToOCL", "OCL"), true, state, i);
    public string RepairedToFx(BodyDamageState state, int i) => Lookup(fxTransitions ??= Transitions("TransitionToFX", "FX"), true, state, i);
    public string DamageToSound => Get("DamagedToSound");
    public string RepairedToSound => Get("RepairedToSound");
    public float TransitionEffectsHeight => RuleValue.Float(Get("TransitionEffectsHeight"));
    public int NumFXPerType => RuleValue.Int(Get("NumFXPerType"), 0);
}

public enum BridgeTowerType { FromLeft, FromRight, ToLeft, ToRight }

/// <summary>Where a bridge lies (the original's BridgeInfo): its two end centres and four corners.</summary>
public sealed class BridgeInfo
{
    public Vector3 From, To, FromLeft, FromRight, ToLeft, ToRight;
    public float BridgeWidth;
    public int BridgeIndex = -1;
    public BodyDamageState CurDamageState = BodyDamageState.Pristine;
    public bool DamageStateChanged;
    public uint BridgeObjectId;
    public readonly uint[] TowerObjectIds = new uint[4];
}

/// <summary>
/// A bridge in the terrain (the original's Bridge): a deck over the ground that units drive onto at its
/// ends. Each bridge is its own pathfinding layer (<see cref="Layer"/>, 1 and up; 0 is the ground).
/// </summary>
public sealed class Bridge
{
    public BridgeInfo Info { get; }
    public string TemplateName { get; }
    public int Layer { get; internal set; }

    /// <summary>Closed to pathfinding: broken, or scaffolding up for repairs (the original's layer setDestroyed).</summary>
    public bool PathfindingClosed { get; set; }
    public Vector2 BoundsLo { get; }
    public Vector2 BoundsHi { get; }

    /// <summary>The cell size the end and side tests use (the original's PATHFIND_CELL_SIZE).</summary>
    const float CellSize = Formats.Maps.MapFile.CellSize;

    public Bridge(BridgeInfo info, string templateName)
    {
        Info = info;
        TemplateName = templateName;
        var corners = new[] { info.FromLeft, info.FromRight, info.ToLeft, info.ToRight };
        BoundsLo = new Vector2(corners.Min(c => c.X), corners.Min(c => c.Y));
        BoundsHi = new Vector2(corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    /// <summary>A landmark bridge object: its box geometry gives the corners (the original's Bridge(Object*)).</summary>
    public static BridgeInfo InfoFromObject(GameObject bridgeObj)
    {
        var pos = bridgeObj.Position;
        float angle = bridgeObj.Orientation;
        float hx = bridgeObj.Template.GeometryMajorRadius, hy = bridgeObj.Template.GeometryMinorRadius;
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        var info = new BridgeInfo
        {
            BridgeWidth = 2 * hy,
            FromLeft = new Vector3(pos.X - hx * c - hy * s, pos.Y + hy * c - hx * s, pos.Z),
            ToLeft = new Vector3(pos.X + hx * c - hy * s, pos.Y + hy * c + hx * s, pos.Z),
            FromRight = new Vector3(pos.X - hx * c + hy * s, pos.Y - hy * c - hx * s, pos.Z),
            ToRight = new Vector3(pos.X + hx * c + hy * s, pos.Y - hy * c + hx * s, pos.Z),
            BridgeObjectId = bridgeObj.Id,
        };
        info.From = (info.FromLeft + info.FromRight) / 2;
        info.To = (info.ToLeft + info.ToRight) / 2;
        return info;
    }

    /// <summary>Washed away (the original's deleteBridge); the layer stays so the others keep their numbers.</summary>
    public bool Deleted { get; internal set; }

    public bool IsPointOnBridge(Vector3 p)
    {
        if (Deleted)
            return false;
        if (p.X < BoundsLo.X || p.X > BoundsHi.X || p.Y < BoundsLo.Y || p.Y > BoundsHi.Y)
            return false;
        var test = new Vector2(p.X, p.Y);
        var l1 = Flat(Info.FromLeft);
        var r1 = Flat(Info.FromRight);
        var l2 = Flat(Info.ToLeft);
        var r2 = Flat(Info.ToRight);
        return PointInTriangle(l1, r1, l2, test) || PointInTriangle(r1, l2, r2, test);
    }

    static Vector2 Flat(Vector3 v) => new(v.X, v.Y);

    static bool PointInTriangle(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
    {
        static float Cross(Vector2 o, Vector2 u, Vector2 v) => (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X);
        float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    /// <summary>The deck height under a point: the plane through three corners (the original's getBridgeHeight).</summary>
    public float GetBridgeHeight(Vector3 p) => GetBridgeHeight(p, out _);

    public float GetBridgeHeight(Vector3 p, out Vector3 normal)
    {
        var plane = Plane.CreateFromVertices(Info.FromLeft, Info.FromRight, Info.ToLeft);
        normal = plane.Normal.Z < 0 ? -plane.Normal : plane.Normal;
        if (MathF.Abs(plane.Normal.Z) < 1e-6f)
            return Info.From.Z;
        // n·(x,y,z) + d = 0  →  z = -(n.x x + n.y y + d) / n.z
        return -(plane.Normal.X * p.X + plane.Normal.Y * p.Y + plane.D) / plane.Normal.Z;
    }

    /// <summary>Does a line cross the box (the original's LineInRegion clipping test)?</summary>
    public static bool LineInRegion(Vector2 p1, Vector2 p2, Vector2 lo, Vector2 hi)
    {
        const int Left = 1, Right = 2, Bottom = 4, Top = 8;
        float x1 = p1.X, y1 = p1.Y, x2 = p2.X, y2 = p2.Y;
        int Code(float x, float y) => (x < lo.X ? Left : x > hi.X ? Right : 0) | (y < lo.Y ? Top : y > hi.Y ? Bottom : 0);
        int c1 = Code(x1, y1), c2 = Code(x2, y2);
        if ((c1 | c2) == 0)
            return true;
        if ((c1 & c2) != 0)
            return false;
        float diff;
        if (c1 != 0)
        {
            if ((c1 & Top) != 0) { if ((diff = y2 - y1) == 0) return false; x1 += (x2 - x1) * (lo.Y - y1) / diff; y1 = lo.Y; }
            else if ((c1 & Bottom) != 0) { if ((diff = y2 - y1) == 0) return false; x1 += (x2 - x1) * (hi.Y - y1) / diff; y1 = hi.Y; }
            if (x1 > hi.X) { if ((diff = x2 - x1) == 0) return false; y1 += (y2 - y1) * (hi.X - x1) / diff; x1 = hi.X; }
            else if (x1 < lo.X) { if ((diff = x2 - x1) == 0) return false; y1 += (y2 - y1) * (lo.X - x1) / diff; x1 = lo.X; }
        }
        if (c2 != 0)
        {
            // (As the original, which steps x2 with the full line's slope from the end.)
            if ((c2 & Top) != 0) { if ((diff = y2 - y1) == 0) return false; x2 += (x2 - x1) * (lo.Y - y2) / diff; y2 = lo.Y; }
            else if ((c2 & Bottom) != 0) { if ((diff = y2 - y1) == 0) return false; x2 += (x2 - x1) * (hi.Y - y2) / diff; y2 = hi.Y; }
            if (x2 > hi.X) { if ((diff = x2 - x1) == 0) return false; y2 += (y2 - y1) * (hi.X - x2) / diff; x2 = hi.X; }
            else if (x2 < lo.X) { if ((diff = x2 - x1) == 0) return false; y2 += (y2 - y1) * (lo.X - x2) / diff; x2 = lo.X; }
        }
        return x1 >= lo.X && x1 <= hi.X && y1 >= lo.Y && y1 <= hi.Y && x2 >= lo.X && x2 <= hi.X && y2 >= lo.Y && y2 <= hi.Y;
    }

    Vector2 EndVector(float scale)
    {
        var v = Info.FromRight - Info.FromLeft;
        var flat = new Vector2(v.X, v.Y);
        float len = new Vector3(v.X, v.Y, v.Z).Length();
        return len > 0 ? flat / len * scale : Vector2.Zero;
    }

    /// <summary>Does the cell touch either end edge of the deck (pulled in by a cell)?</summary>
    public bool IsCellOnEnd(Vector2 lo, Vector2 hi)
    {
        var e = EndVector(CellSize);
        return LineInRegion(Flat(Info.FromLeft) + e, Flat(Info.FromRight) - e, lo, hi)
            || LineInRegion(Flat(Info.ToLeft) + e, Flat(Info.ToRight) - e, lo, hi);
    }

    /// <summary>Does the cell touch the deck's sides (where units must not step off)?</summary>
    public bool IsCellOnSide(Vector2 lo, Vector2 hi)
    {
        var e = EndVector(CellSize * 0.51f);
        Vector2 fl = Flat(Info.FromLeft) - e, fr = Flat(Info.FromRight) + e, tl = Flat(Info.ToLeft) - e, tr = Flat(Info.ToRight) + e;
        if (LineInRegion(fl, tl, lo, hi) || LineInRegion(fr, tr, lo, hi))
            return true;
        fl -= e; fr += e; tl -= e; tr += e;
        return LineInRegion(fl, tl, lo, hi) || LineInRegion(fr, tr, lo, hi);
    }

    /// <summary>A cell just beyond either end, where units get on and off.</summary>
    public bool IsCellEntryPoint(Vector2 lo, Vector2 hi)
    {
        var e = EndVector(CellSize);
        var b3 = Info.To - Info.From;
        float blen = b3.Length();
        var b = blen > 0 ? new Vector2(b3.X, b3.Y) / blen * (CellSize / 2) : Vector2.Zero;
        Vector2 fl = Flat(Info.FromLeft) - b + e, fr = Flat(Info.FromRight) - b - e;
        Vector2 tl = Flat(Info.ToLeft) + b + e, tr = Flat(Info.ToRight) + b - e;
        return LineInRegion(fl, fr, lo, hi) || LineInRegion(tl, tr, lo, hi);
    }
}

/// <summary>The terrain's bridges: map-drawn spans and landmark bridge objects.</summary>
public sealed class BridgeList
{
    readonly List<Bridge> bridges = new();
    public IReadOnlyList<Bridge> All => bridges;

    /// <summary>The original's BRIDGE_FLOAT_AMT: map bridges sit this far above the ground at their ends.</summary>
    public const float BridgeFloatAmount = 0.25f;

    /// <summary>The original's LAYER_Z_CLOSE_ENOUGH_F.</summary>
    public const float LayerZCloseEnough = 10;

    /// <summary>Raised when a bridge is added (the pathfinder builds its layer).</summary>
    public event Action<Bridge>? BridgeAdded;

    public Bridge Add(BridgeInfo info, string templateName)
    {
        var bridge = new Bridge(info, templateName) { Layer = bridges.Count + 1 };
        bridges.Add(bridge);
        BridgeAdded?.Invoke(bridge);
        return bridge;
    }

    /// <summary>
    /// Removes a bridge (the original's deleteBridge): its object and towers go, and its layer is closed.
    /// </summary>
    public void Delete(Bridge? bridge, GameLogic game)
    {
        if (bridge == null || bridge.Deleted)
            return;
        bridge.Deleted = true;
        bridge.PathfindingClosed = true;
        foreach (uint id in bridge.Info.TowerObjectIds.Append(bridge.Info.BridgeObjectId))
            if (game.FindObject(id) is { } obj)
                game.DestroyObject(obj);
    }

    public Bridge? ForLayer(int layer) => layer >= 1 && layer <= bridges.Count ? bridges[layer - 1] : null;

    public Bridge? FindBridgeAt(Vector3 p) => bridges.FirstOrDefault(b => b.IsPointOnBridge(p));

    public Bridge? FindBridgeLayerAt(Vector3 p, int layer, bool clip = true) =>
        ForLayer(layer) is { } b && (!clip || b.IsPointOnBridge(p)) ? b : null;

    /// <summary>The layer a point at this height is on (the original's getLayerForDestination).</summary>
    public int LayerForDestination(Vector3 p, float groundHeight)
    {
        int best = 0;
        float bestDistance = MathF.Abs(p.Z - groundHeight);
        foreach (var b in bridges)
        {
            if (!b.IsPointOnBridge(p))
                continue;
            float delta = MathF.Abs(p.Z - b.GetBridgeHeight(p));
            if (delta < bestDistance)
            {
                best = b.Layer;
                bestDistance = delta;
            }
        }
        return best;
    }

    /// <summary>The highest layer at or below a point (the original's getHighestLayerForDestination).</summary>
    public int HighestLayerForDestination(Vector3 p, float groundHeight, bool onlyHealthyBridges = false)
    {
        int best = 0;
        float bestDistance = p.Z - groundHeight;
        foreach (var b in bridges)
        {
            if (onlyHealthyBridges && b.Info.CurDamageState == BodyDamageState.Rubble)
                continue;
            if (!b.IsPointOnBridge(p))
                continue;
            float delta = p.Z - b.GetBridgeHeight(p);
            if (delta >= 0 && MathF.Abs(delta) < MathF.Abs(bestDistance))
            {
                best = b.Layer;
                bestDistance = delta;
            }
        }
        return best;
    }

    /// <summary>Is the object on (or at an end of) this bridge layer, at deck height (the original's objectInteractsWithBridgeLayer)?</summary>
    public bool ObjectInteractsWithBridgeLayer(GameObject obj, int layer, bool considerBridgeHealth = false)
    {
        if (ForLayer(layer) is not { } bridge)
            return false;
        bool match = bridge.IsPointOnBridge(obj.Position);
        float radius = obj.Template.GeometryMinorRadius + Formats.Maps.MapFile.CellSize / 2;
        var lo = new Vector2(obj.Position.X - radius, obj.Position.Y - radius);
        var hi = new Vector2(obj.Position.X + radius, obj.Position.Y + radius);
        if (bridge.IsCellOnEnd(lo, hi))
            match = true;
        if (!match)
            return false;
        if (MathF.Abs(obj.Position.Z - bridge.GetBridgeHeight(obj.Position)) > LayerZCloseEnough)
            return false;
        return !(considerBridgeHealth && bridge.Info.CurDamageState == BodyDamageState.Rubble);
    }

    /// <summary>Is the object at an end of this bridge layer, at deck height (the original's objectInteractsWithBridgeEnd)?</summary>
    public bool ObjectInteractsWithBridgeEnd(GameObject obj, int layer)
    {
        if (ForLayer(layer) is not { } bridge)
            return false;
        float radius = obj.Template.GeometryMinorRadius + Formats.Maps.MapFile.CellSize / 2;
        var lo = new Vector2(obj.Position.X - radius, obj.Position.Y - radius);
        var hi = new Vector2(obj.Position.X + radius, obj.Position.Y + radius);
        if (!bridge.IsCellOnEnd(lo, hi))
            return false;
        return MathF.Abs(obj.Position.Z - bridge.GetBridgeHeight(obj.Position)) <= LayerZCloseEnough;
    }

    public void Clear() => bridges.Clear();
}

using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Maps;

namespace OpenGZH.Client.World;

/// <summary>
/// Builds the map's road meshes (a port of the original's W3DRoadBuffer, without the drawing): road
/// points on the map become segments, chained end to end; three-way and four-way crossings get tee, Y
/// and H junction pieces; bends get curve pieces or mitred corners; road ends marked as joins blend into
/// another road type. Every piece is a grid of quads draped over the terrain, with texture coordinates
/// into the road texture's tile sheet.
/// </summary>
public sealed class RoadBuilder
{
    // ---- Data (the original's TRoadPt, RoadSegment, RoadType) ----

    public sealed class RoadPt
    {
        public Vector2 Loc, Top, Bottom;
        public int Count;
        public bool Last, Multi, IsAngled, IsJoin;
        public RoadPt Clone() => (RoadPt)MemberwiseClone();
    }

    public enum Corner { Segment, Curve, Tee, FourWay, ThreeWayY, ThreeWayH, ThreeWayHFlip, AlphaJoin }

    public sealed class RoadSegment
    {
        public RoadPt Pt1 = new(), Pt2 = new();
        public float CurveRadius;
        public Corner Type = Corner.Segment;
        public float Scale = DefaultRoadScale;
        public float WidthInTexture = 1;
        public int UniqueId;
        public Vertex[] Vertices = [];
        public ushort[] Indices = [];

        public RoadSegment Clone()
        {
            var copy = (RoadSegment)MemberwiseClone();
            copy.Pt1 = Pt1.Clone();
            copy.Pt2 = Pt2.Clone();
            return copy;
        }
    }

    public readonly record struct Vertex(Vector3 Position, Vector2 Uv);

    public sealed class RoadType
    {
        public required string Name { get; init; }
        public required string Texture { get; init; }
        public required int UniqueId { get; init; }
        public float RoadWidth { get; init; }
        public float RoadWidthInTexture { get; init; }
        public int Stacking { get; set; }
    }

    const float TeeWidthAdjustment = 1.03f;
    const float CornerRadius = 1.5f;
    const float TightCornerRadius = 0.5f;
    const float DefaultRoadScale = 8;
    const float MinRoadSegment = 0.25f;
    const int MaxSegVertex = 500, MaxSegIndex = 2000;
    const int FlagRoadPoint1 = 0x2, FlagRoadPoint2 = 0x4, FlagRoadCornerAngled = 0x8, FlagRoadCornerTight = 0x40, FlagRoadJoin = 0x80;
    const float MapXYFactor = MapFile.CellSize;
    const float MapHeightScale = MapFile.HeightScale;

    readonly HeightMap heights;
    readonly int maxRoadSegments;
    public List<RoadSegment> Roads { get; } = new();
    public List<RoadType> RoadTypes { get; } = new();

    public RoadBuilder(MapFile map, RuleSet rules)
    {
        heights = map.HeightMap;
        maxRoadSegments = RuleValue.Int(rules.Section("GameData").GetString("MaxRoadSegments"), 500);
        int maxRoadTypes = RuleValue.Int(rules.Section("GameData").GetString("MaxRoadTypes"), 8);

        // Road types are numbered from 1 in definition order; each road takes DefaultRoad's settings
        // unless it has its own (the original's TerrainRoadCollection).
        var section = rules.Section("Roads");
        var defaults = section["DefaultRoad"];
        int id = 1;
        var types = new List<RoadType>();
        foreach (string name in section.Keys)
        {
            var node = section[name]!;
            string Get(string key) => node.Has(key) ? node.GetString(key) : defaults?.GetString(key) ?? "";
            types.Add(new RoadType
            {
                Name = name,
                Texture = Get("Texture"),
                UniqueId = id++,
                RoadWidth = RuleValue.Float(Get("RoadWidth"), DefaultRoadScale),
                RoadWidthInTexture = RuleValue.Float(Get("RoadWidthInTexture"), 1),
            });
        }
        // (The original keeps them in a list built by prepending, and loads at most MaxRoadTypes.)
        types.Reverse();
        RoadTypes.AddRange(types.Take(maxRoadTypes));

        LoadRoads(map);
    }

    // ---- Setup (the original's loadRoads) ----

    void LoadRoads(MapFile map)
    {
        AddMapObjects(map);
        UpdateCountsAndFlags();
        InsertTeeIntersections();
        InsertCurveSegments();
        InsertCrossTypeJoins();
        PreloadRoads();
    }

    RoadType? FindRoad(string name) => RoadTypes.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    static Vector2 Rotate(Vector2 v, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    static Vector2 Normalize(Vector2 v) => v.LengthSquared() > 0 ? Vector2.Normalize(v) : v;

    static int XpSign(Vector2 v1, Vector2 v2)
    {
        float x = v1.X * v2.Y - v1.Y * v2.X;
        return x < 0 ? -1 : x > 0 ? 1 : 0;
    }

    static float CrossZ(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Where two infinite lines cross (the original's LineSegClass::Find_Intersection); false if parallel.</summary>
    static bool LineIntersection(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2, out Vector2 point)
    {
        var d1 = a2 - a1;
        var d2 = b2 - b1;
        float denom = CrossZ(d1, d2);
        if (MathF.Abs(denom) < 1e-6f)
        {
            point = default;
            return false;
        }
        float t = CrossZ(b1 - a1, d2) / denom;
        point = a1 + d1 * t;
        return true;
    }

    static Vector2 ClosestPointOnSegment(Vector2 a, Vector2 b, Vector2 p)
    {
        var d = b - a;
        float len2 = d.LengthSquared();
        if (len2 <= 0)
            return a;
        float t = Math.Clamp(Vector2.Dot(p - a, d) / len2, 0, 1);
        return a + d * t;
    }

    static void FlipTheRoad(RoadSegment road) => (road.Pt1, road.Pt2) = (road.Pt2, road.Pt1);

    void MoveRoadSegTo(int fromNdx, int toNdx)
    {
        if (fromNdx < 0 || fromNdx >= Roads.Count || toNdx < 0 || toNdx >= Roads.Count || fromNdx == toNdx)
            return;
        var cur = Roads[fromNdx];
        Roads.RemoveAt(fromNdx);
        Roads.Insert(toNdx, cur);
    }

    void CheckLinkBefore(int ndx)
    {
        if (Roads[ndx].Pt2.Count != 1)
            return;
        var loc2 = Roads[ndx].Pt2.Loc;
        int endOfCurSeg = ndx + 1;
        while (endOfCurSeg < Roads.Count - 1)
        {
            if (Roads[endOfCurSeg].Pt1.Loc != Roads[endOfCurSeg + 1].Pt2.Loc)
                break;
            endOfCurSeg++;
        }
        int checkNdx = endOfCurSeg + 1;
        while (checkNdx < Roads.Count)
        {
            if (Roads[checkNdx].Pt1.Loc == loc2)
            {
                MoveRoadSegTo(checkNdx, ndx);
                loc2 = Roads[ndx].Pt2.Loc;
                if (Roads[ndx].Pt2.Count != 1)
                    return;
                endOfCurSeg++;
            }
            else if (Roads[checkNdx].Pt2.Loc == loc2)
            {
                FlipTheRoad(Roads[checkNdx]);
                MoveRoadSegTo(checkNdx, ndx);
                loc2 = Roads[ndx].Pt2.Loc;
                if (Roads[ndx].Pt2.Count != 1)
                    return;
                endOfCurSeg++;
            }
            else
                checkNdx++;
            if (checkNdx <= endOfCurSeg)
                checkNdx = endOfCurSeg + 1;
        }
    }

    void CheckLinkAfter(int ndx)
    {
        if (Roads[ndx].Pt1.Count != 1 || ndx >= Roads.Count - 1)
            return;
        var loc1 = Roads[ndx].Pt1.Loc;
        int checkNdx = ndx + 1;
        while (checkNdx < Roads.Count && ndx < Roads.Count - 1)
        {
            if (Roads[checkNdx].Pt2.Loc == loc1)
            {
                ndx++;
                MoveRoadSegTo(checkNdx, ndx);
                loc1 = Roads[ndx].Pt1.Loc;
                if (Roads[ndx].Pt1.Count != 1)
                    return;
            }
            else if (Roads[checkNdx].Pt1.Loc == loc1)
            {
                FlipTheRoad(Roads[checkNdx]);
                ndx++;
                MoveRoadSegTo(checkNdx, ndx);
                loc1 = Roads[ndx].Pt1.Loc;
                if (Roads[ndx].Pt1.Count != 1)
                    return;
            }
            else
                checkNdx++;
        }
    }

    bool Full => Roads.Count >= maxRoadSegments;

    void AddMapObject(RoadSegment road, bool updateTheCounts)
    {
        var cur = road.Clone();
        var loc1 = cur.Pt1.Loc;
        var loc2 = cur.Pt2.Loc;
        var roadNormal = Normalize(new Vector2(-(loc2.Y - loc1.Y), loc2.X - loc1.X)) * (cur.Scale * cur.WidthInTexture / 2);
        cur.Pt1.Top = loc1 + roadNormal;
        cur.Pt1.Bottom = loc1 - roadNormal;
        cur.Pt2.Top = loc2 + roadNormal;
        cur.Pt2.Bottom = loc2 - roadNormal;
        if (updateTheCounts)
            UpdateCounts(cur);
        if (Full)
            return;
        bool flip = false, addBefore = false, addAfter = false, bothMatch = false;
        int i;
        for (i = 0; i < Roads.Count; i++)
        {
            bothMatch = false;
            if ((Roads[i].Pt1.Loc == loc1 && Roads[i].Pt2.Loc == loc2) || (Roads[i].Pt1.Loc == loc2 && Roads[i].Pt2.Loc == loc1))
            {
                bothMatch = true;
                break;
            }
            if (cur.Pt1.Count == 1)
            {
                if (Roads[i].Pt1.Loc == loc1) { flip = true; addAfter = true; }
                if (Roads[i].Pt2.Loc == loc1) { flip = false; addBefore = true; }
            }
            if (cur.Pt2.Count == 1)
            {
                if (Roads[i].Pt1.Loc == loc2) { flip = false; addAfter = true; }
                if (Roads[i].Pt2.Loc == loc2) { flip = true; addBefore = true; }
            }
            if (addBefore || addAfter)
                break;
        }
        if (bothMatch)
            return;
        int addIndex = i;
        if (addAfter)
            addIndex++;
        Roads.Insert(Math.Min(addIndex, Roads.Count), cur);
        addIndex = Math.Min(addIndex, Roads.Count - 1);
        if (flip)
            FlipTheRoad(Roads[addIndex]);
        if (addBefore)
            CheckLinkBefore(addIndex);
        else if (addAfter)
            CheckLinkAfter(addIndex);
    }

    void AddMapObjects(MapFile map)
    {
        var objects = map.Objects;
        for (int k = 0; k < objects.Count; k++)
        {
            if (Full)
                break;
            var first = objects[k];
            if ((first.Flags & FlagRoadPoint1) == 0)
                continue;
            if (k + 1 >= objects.Count)
                break;
            var second = objects[k + 1];
            if ((second.Flags & FlagRoadPoint2) == 0)
                continue;
            var loc1 = new Vector2(first.Position.X, first.Position.Y);
            var loc2 = new Vector2(second.Position.X, second.Position.Y);
            if (loc1 == loc2)
                loc2.X += 0.25f;
            var cur = new RoadSegment { Scale = DefaultRoadScale, WidthInTexture = 1, UniqueId = 1 };
            if (FindRoad(first.TemplateName) is { } type)
            {
                cur.WidthInTexture = type.RoadWidthInTexture;
                cur.Scale = type.RoadWidth;
                cur.UniqueId = type.UniqueId;
            }
            cur.Pt1.Loc = loc1;
            cur.Pt1.IsAngled = (first.Flags & FlagRoadCornerAngled) != 0;
            cur.Pt1.IsJoin = (first.Flags & FlagRoadJoin) != 0;
            cur.Pt2.Loc = loc2;
            cur.Pt2.IsAngled = (second.Flags & FlagRoadCornerAngled) != 0;
            cur.Pt2.IsJoin = (second.Flags & FlagRoadJoin) != 0;
            cur.Type = Corner.Segment;
            cur.CurveRadius = (first.Flags & FlagRoadCornerTight) != 0 ? TightCornerRadius : CornerRadius;
            AddMapObject(cur, true);
            k++;
        }
        // Add them all again, now that the counts are known, so they chain up in order.
        var all = Roads.ToList();
        Roads.Clear();
        foreach (var road in all)
            AddMapObject(road, false);
    }

    void UpdateCounts(RoadSegment road)
    {
        road.Pt1.Last = road.Pt2.Last = true;
        road.Pt1.Multi = road.Pt2.Multi = false;
        road.Pt1.Count = road.Pt2.Count = 0;
        var loc1 = road.Pt1.Loc;
        var loc2 = road.Pt2.Loc;
        foreach (var other in Roads)
        {
            if (other.UniqueId != road.UniqueId)
                continue;
            if (other.Pt1.Loc == loc1) { other.Pt1.Count++; road.Pt1.Count++; }
            if (other.Pt1.Loc == loc2) { other.Pt1.Count++; road.Pt2.Count++; }
            if (other.Pt2.Loc == loc1) { other.Pt2.Count++; road.Pt1.Count++; }
            if (other.Pt2.Loc == loc2) { other.Pt2.Count++; road.Pt2.Count++; }
            other.Pt1.Multi = other.Pt1.Count > 1;
            other.Pt2.Multi = other.Pt2.Count > 1;
        }
        road.Pt1.Multi = road.Pt1.Count > 1;
        road.Pt2.Multi = road.Pt2.Count > 1;
    }

    void UpdateCountsAndFlags()
    {
        foreach (var road in Roads)
        {
            road.Pt1.Last = road.Pt2.Last = true;
            road.Pt1.Count = road.Pt2.Count = 0;
        }
        for (int j = Roads.Count - 1; j > 0; j--)
        {
            var loc1 = Roads[j].Pt1.Loc;
            var loc2 = Roads[j].Pt2.Loc;
            for (int i = 0; i < j; i++)
            {
                if (Roads[i].UniqueId != Roads[j].UniqueId)
                    continue;
                if (Roads[i].Pt1.Loc == loc1) { Roads[i].Pt1.Last = false; Roads[i].Pt1.Count++; Roads[j].Pt1.Count++; }
                if (Roads[i].Pt1.Loc == loc2) { Roads[i].Pt1.Last = false; Roads[i].Pt1.Count++; Roads[j].Pt2.Count++; }
                if (Roads[i].Pt2.Loc == loc1) { Roads[i].Pt2.Last = false; Roads[i].Pt2.Count++; Roads[j].Pt1.Count++; }
                if (Roads[i].Pt2.Loc == loc2) { Roads[i].Pt2.Last = false; Roads[i].Pt2.Count++; Roads[j].Pt2.Count++; }
            }
        }
    }

    RoadSegment NewJunction(Vector2 loc1, Vector2 loc2, RoadSegment like, Corner type, int pt1Count)
    {
        var road = new RoadSegment
        {
            Scale = like.Scale,
            WidthInTexture = like.WidthInTexture,
            Type = type,
            UniqueId = like.UniqueId,
        };
        road.Pt1.Loc = loc1;
        road.Pt2.Loc = loc2;
        road.Pt1.Last = road.Pt2.Last = true;
        road.Pt1.Count = pt1Count;
        Roads.Add(road);
        return road;
    }

    /// <summary>The other two roads meeting at a crossing (the original's search after index1).</summary>
    (RoadPt pr1, RoadPt pc1, RoadPt? pr2, RoadPt? pc2, RoadPt? pr3, RoadPt? pc3, RoadPt? pr4, RoadPt? pc4) FindCrossing(Vector2 loc, int index1)
    {
        RoadPt pr1, pc1;
        if (Roads[index1].Pt1.Loc == loc) { pr1 = Roads[index1].Pt2; pc1 = Roads[index1].Pt1; }
        else { pr1 = Roads[index1].Pt1; pc1 = Roads[index1].Pt2; }
        RoadPt? pr2 = null, pc2 = null, pr3 = null, pc3 = null, pr4 = null, pc4 = null;
        void Take(RoadPt far, RoadPt near)
        {
            if (pr2 == null) { pr2 = far; pc2 = near; }
            else if (pr3 == null) { pr3 = far; pc3 = near; }
            else { pr4 = far; pc4 = near; }
        }
        for (int i = index1 + 1; i < Roads.Count; i++)
        {
            if (Roads[i].Pt1.Loc == loc)
            {
                Roads[i].Pt1.Count = -2;
                Take(Roads[i].Pt2, Roads[i].Pt1);
            }
            if (Roads[i].Pt2.Loc == loc)
            {
                Roads[i].Pt2.Count = -2;
                Take(Roads[i].Pt1, Roads[i].Pt2);
            }
        }
        return (pr1, pc1, pr2, pc2, pr3, pc3, pr4, pc4);
    }

    static void ResetPts(params RoadPt?[] pts)
    {
        foreach (var p in pts)
        {
            if (p == null)
                continue;
            p.Last = true;
            p.Count = 0;
        }
    }

    void InsertTee(Vector2 loc, int index1, float scale)
    {
        if (InsertY(loc, index1, scale))
            return;
        // (The original's three-way search keeps the last road found as the third, overwriting earlier ones.)
        RoadPt pr1, pc1;
        if (Roads[index1].Pt1.Loc == loc) { pr1 = Roads[index1].Pt2; pc1 = Roads[index1].Pt1; }
        else { pr1 = Roads[index1].Pt1; pc1 = Roads[index1].Pt2; }
        RoadPt? pr2 = null, pc2 = null, pr3 = null, pc3 = null;
        for (int i = index1 + 1; i < Roads.Count; i++)
        {
            if (Roads[i].Pt1.Loc == loc)
            {
                Roads[i].Pt1.Count = -2;
                if (pr2 == null) { pr2 = Roads[i].Pt2; pc2 = Roads[i].Pt1; }
                else { pr3 = Roads[i].Pt2; pc3 = Roads[i].Pt1; }
            }
            if (Roads[i].Pt2.Loc == loc)
            {
                Roads[i].Pt2.Count = -2;
                if (pr2 == null) { pr2 = Roads[i].Pt1; pc2 = Roads[i].Pt2; }
                else { pr3 = Roads[i].Pt1; pc3 = Roads[i].Pt2; }
            }
        }
        if (pr2 == null || pr3 == null)
            return;
        var v1 = Normalize(pr1.Loc - loc);
        var v2 = Normalize(pr2.Loc - loc);
        var v3 = Normalize(pr3.Loc - loc);
        float dot12 = Vector2.Dot(v1, v2), dot13 = Vector2.Dot(v1, v3), dot32 = Vector2.Dot(v3, v2);
        bool do12 = false, do13 = false, do32 = false;
        if (dot12 < dot13)
        {
            if (dot12 < dot32) do12 = true; else do32 = true;
        }
        else
        {
            if (dot13 < dot32) do13 = true; else do32 = true;
        }
        Vector2 upVector = default, decider = default;
        if (do12) { upVector = v2 - v1; decider = v3; }
        if (do13) { upVector = v3 - v1; decider = v2; }
        if (do32) { upVector = v2 - v3; decider = v1; }
        upVector = Normalize(upVector);
        const float Cos60 = 0.5f;
        float dot = MathF.Abs(Vector2.Dot(upVector, decider));
        var road1 = Roads[index1];
        if (dot > Cos60)
        {
            float angle = MathF.PI / 2;
            bool mirror = false;
            if (CrossZ(upVector, decider) < 0)
            {
                angle = -angle;
                mirror = true;
            }
            upVector = Normalize(upVector) * (0.5f * scale);
            var teeVector = Rotate(upVector, angle);
            bool flip = false;
            if (do12) { flip = XpSign(teeVector, v3) == 1; OffsetH(pc1, pc2!, pc3!, loc, upVector, teeVector, flip, mirror, road1.WidthInTexture); }
            if (do13) { flip = XpSign(teeVector, v2) == 1; OffsetH(pc1, pc3!, pc2!, loc, upVector, teeVector, flip, mirror, road1.WidthInTexture); }
            if (do32) { flip = XpSign(teeVector, v1) == 1; OffsetH(pc3!, pc2!, pc1, loc, upVector, teeVector, flip, mirror, road1.WidthInTexture); }
            ResetPts(pc1, pc2, pc3);
            if (Full)
                return;
            NewJunction(loc, loc + teeVector, road1, flip ? Corner.ThreeWayHFlip : Corner.ThreeWayH, -3);
        }
        else
        {
            float angle = MathF.PI / 2;
            if (CrossZ(upVector, decider) < 0)
                angle = -angle;
            upVector = Normalize(upVector) * (0.5f * scale);
            var teeVector = Rotate(upVector, angle);
            if (do12) Offset3Way(pc1, pc2!, pc3!, loc, upVector, teeVector, road1.WidthInTexture);
            if (do13) Offset3Way(pc1, pc3!, pc2!, loc, upVector, teeVector, road1.WidthInTexture);
            if (do32) Offset3Way(pc3!, pc2!, pc1, loc, upVector, teeVector, road1.WidthInTexture);
            ResetPts(pc1, pc2, pc3);
            if (Full)
                return;
            NewJunction(loc, loc + teeVector, road1, Corner.Tee, -3);
        }
    }

    bool InsertY(Vector2 loc, int index1, float scale)
    {
        RoadPt pr1, pc1;
        if (Roads[index1].Pt1.Loc == loc) { pr1 = Roads[index1].Pt2; pc1 = Roads[index1].Pt1; }
        else { pr1 = Roads[index1].Pt1; pc1 = Roads[index1].Pt2; }
        RoadPt? pr2 = null, pc2 = null, pr3 = null, pc3 = null;
        for (int i = index1 + 1; i < Roads.Count; i++)
        {
            if (Roads[i].Pt1.Loc == loc)
            {
                Roads[i].Pt1.Count = -2;
                if (pr2 == null) { pr2 = Roads[i].Pt2; pc2 = Roads[i].Pt1; }
                else { pr3 = Roads[i].Pt2; pc3 = Roads[i].Pt1; }
            }
            if (Roads[i].Pt2.Loc == loc)
            {
                Roads[i].Pt2.Count = -2;
                if (pr2 == null) { pr2 = Roads[i].Pt1; pc2 = Roads[i].Pt2; }
                else { pr3 = Roads[i].Pt1; pc3 = Roads[i].Pt2; }
            }
        }
        if (pr2 == null || pr3 == null)
            return false;
        var v1 = Normalize(pr1.Loc - loc);
        var v2 = Normalize(pr2.Loc - loc);
        var v3 = Normalize(pr3.Loc - loc);
        bool do12 = false, do13 = false, do32 = false;
        float dot12 = Vector2.Dot(v1, v2), dot13 = Vector2.Dot(v1, v3), dot32 = Vector2.Dot(v3, v2);
        float score12 = 2, score13 = 2, score32 = 2;
        const float Cos30 = 0.866f, Cos45 = 0.707f;
        if (dot12 < -Cos30 || dot13 < -Cos30 || dot32 < -Cos30)
            return false;
        int s2 = XpSign(v1, v2), s3 = XpSign(v1, v3);
        if (s2 != s3 && s2 + s3 == 0)
        {
            var v1_90 = new Vector2(-v1.Y, v1.X);
            if (XpSign(v1_90, v2) == 1 && XpSign(v1_90, v3) == 1)
            {
                do32 = true;
                score32 = MathF.Abs(dot12 + Cos45) + MathF.Abs(dot13 + Cos45);
            }
        }
        int s1 = XpSign(v3, v1);
        s2 = XpSign(v3, v2);
        if (s2 != s1 && s2 + s1 == 0)
        {
            var v3_90 = new Vector2(-v3.Y, v3.X);
            if (XpSign(v3_90, v2) == 1 && XpSign(v3_90, v1) == 1)
            {
                do12 = true;
                score12 = MathF.Abs(dot13 + Cos45) + MathF.Abs(dot32 + Cos45);
            }
        }
        s1 = XpSign(v2, v1);
        s3 = XpSign(v2, v3);
        if (s3 != s1 && s3 + s1 == 0)
        {
            var v2_90 = new Vector2(-v2.Y, v2.X);
            if (XpSign(v2_90, v3) == 1 && XpSign(v2_90, v1) == 1)
            {
                do13 = true;
                score13 = MathF.Abs(dot12 + Cos45) + MathF.Abs(dot32 + Cos45);
            }
        }
        if (score12 < score13)
        {
            do13 = false;
            if (score12 < score32) do32 = false; else do12 = false;
        }
        else
        {
            do12 = false;
            if (score13 < score32) do32 = false; else do13 = false;
        }
        Vector2 upVector;
        if (do12) upVector = v3;
        else if (do13) upVector = v2;
        else if (do32) upVector = v1;
        else return false;
        upVector = Normalize(upVector) * (0.5f * scale);
        var teeVector = Rotate(upVector, -MathF.PI / 2);
        float width = Roads[index1].WidthInTexture;
        if (do12)
        {
            if (XpSign(v3, v1) == -1) OffsetY(pc1, pc2!, pc3!, loc, upVector, width);
            else OffsetY(pc2!, pc1, pc3!, loc, upVector, width);
        }
        if (do13)
        {
            if (XpSign(v2, v1) == -1) OffsetY(pc1, pc3!, pc2!, loc, upVector, width);
            else OffsetY(pc3!, pc1, pc2!, loc, upVector, width);
        }
        if (do32)
        {
            if (XpSign(v1, v3) == -1) OffsetY(pc3!, pc2!, pc1, loc, upVector, width);
            else OffsetY(pc2!, pc3!, pc1, loc, upVector, width);
        }
        ResetPts(pc1, pc2, pc3);
        if (Full)
            return false;
        NewJunction(loc, loc + teeVector, Roads[index1], Corner.ThreeWayY, -3);
        return true;
    }

    static void Offset3Way(RoadPt pc1, RoadPt pc2, RoadPt pc3, Vector2 loc, Vector2 upVector, Vector2 teeVector, float widthInTexture)
    {
        pc1.Loc = loc - upVector;
        pc2.Loc = loc + upVector;
        pc3.Loc = loc + teeVector;
        var rightTee = CrossZ(upVector, teeVector) < 0 ? -teeVector : teeVector;
        rightTee *= widthInTexture;
        Orient(pc1, upVector, rightTee, positive: true);
        Orient(pc2, upVector, rightTee, positive: true);
        upVector *= widthInTexture;
        if (CrossZ(rightTee, pc3.Top - pc3.Loc) < 0) { pc3.Bottom = pc3.Loc - upVector; pc3.Top = pc3.Loc + upVector; }
        else { pc3.Bottom = pc3.Loc + upVector; pc3.Top = pc3.Loc - upVector; }
    }

    /// <summary>Sets top and bottom across <paramref name="across"/>, keeping the side the top was on.</summary>
    static void Orient(RoadPt pc, Vector2 along, Vector2 across, bool positive)
    {
        if (CrossZ(along, pc.Top - pc.Loc) > 0 == positive)
        {
            pc.Bottom = pc.Loc - across;
            pc.Top = pc.Loc + across;
        }
        else
        {
            pc.Bottom = pc.Loc + across;
            pc.Top = pc.Loc - across;
        }
    }

    static void OffsetH(RoadPt pc1, RoadPt pc2, RoadPt pc3, Vector2 loc, Vector2 upVector, Vector2 teeVector, bool flip, bool mirror, float widthInTexture)
    {
        if (flip != mirror)
        {
            pc1.Loc = loc - upVector * 2.05f;
            pc2.Loc = loc + upVector * 0.46f;
        }
        else
        {
            pc1.Loc = loc - upVector * 0.46f;
            pc2.Loc = loc + upVector * 2.05f;
        }
        var rightTee = CrossZ(upVector, teeVector) < 0 ? -teeVector : teeVector;
        rightTee *= widthInTexture;
        Orient(pc1, upVector, rightTee, positive: true);
        Orient(pc2, upVector, rightTee, positive: true);
        var arm = Rotate(teeVector, flip ? MathF.PI / 4 : -MathF.PI / 4);
        var armNormal = new Vector2(-arm.Y, arm.X) * widthInTexture;
        pc3.Loc += arm * 2.10f;
        if (XpSign(arm, pc3.Top - loc) == 1) { pc3.Top = pc3.Loc + armNormal; pc3.Bottom = pc3.Loc - armNormal; }
        else { pc3.Top = pc3.Loc - armNormal; pc3.Bottom = pc3.Loc + armNormal; }
    }

    static void OffsetY(RoadPt pc1, RoadPt pc2, RoadPt pc3, Vector2 loc, Vector2 upVector, float widthInTexture)
    {
        pc3.Loc += upVector * 0.55f;
        pc3.Top += upVector * 0.55f;
        pc3.Bottom += upVector * 0.55f;
        var arm = Rotate(upVector, 3 * MathF.PI / 4);
        var armNormal = new Vector2(-arm.Y, arm.X) * widthInTexture;
        pc2.Loc += arm * 1.1f;
        if (XpSign(arm, pc2.Top - loc) == 1) { pc2.Top = pc2.Loc + armNormal; pc2.Bottom = pc2.Loc - armNormal; }
        else { pc2.Top = pc2.Loc - armNormal; pc2.Bottom = pc2.Loc + armNormal; }
        arm = Rotate(upVector, -3 * MathF.PI / 4);
        armNormal = new Vector2(-arm.Y, arm.X) * widthInTexture;
        pc1.Loc += arm * 1.1f;
        if (XpSign(arm, pc1.Top - loc) == 1) { pc1.Top = pc1.Loc + armNormal; pc1.Bottom = pc1.Loc - armNormal; }
        else { pc1.Top = pc1.Loc - armNormal; pc1.Bottom = pc1.Loc + armNormal; }
    }

    static void Offset4Way(RoadPt pc1, RoadPt pc2, RoadPt pc3, RoadPt pr3, RoadPt pc4, Vector2 loc, Vector2 alignVector, float widthInTexture)
    {
        pc1.Loc = loc - alignVector;
        pc2.Loc = loc + alignVector;
        var v3 = pr3.Loc - loc;
        float angle = CrossZ(alignVector, v3) < 0 ? -MathF.PI / 2 : MathF.PI / 2;
        var teeVector = Rotate(alignVector, angle);
        pc3.Loc = loc + teeVector;
        pc4.Loc = loc - teeVector;
        var realTee = Rotate(alignVector, MathF.PI / 2) * widthInTexture;
        Orient(pc1, alignVector, realTee, positive: true);
        Orient(pc2, alignVector, realTee, positive: true);
        alignVector *= widthInTexture;
        if (CrossZ(realTee, pc3.Top - pc3.Loc) < 0) { pc3.Bottom = pc3.Loc - alignVector; pc3.Top = pc3.Loc + alignVector; }
        else { pc3.Bottom = pc3.Loc + alignVector; pc3.Top = pc3.Loc - alignVector; }
        if (CrossZ(realTee, pc4.Top - pc4.Loc) < 0) { pc4.Bottom = pc4.Loc - alignVector; pc4.Top = pc4.Loc + alignVector; }
        else { pc4.Bottom = pc4.Loc + alignVector; pc4.Top = pc4.Loc - alignVector; }
        ResetPts(pc1, pc2, pc3, pc4);
    }

    void Insert4Way(Vector2 loc, int index1, float scale)
    {
        var (pr1, pc1, pr2, pc2, pr3, pc3, pr4, pc4) = FindCrossing(loc, index1);
        if (pr2 == null || pr3 == null || pr4 == null)
            return;
        var v1 = Normalize(pr1.Loc - loc);
        var v2 = Normalize(pr2.Loc - loc);
        var v3 = Normalize(pr3.Loc - loc);
        var v4 = Normalize(pr4.Loc - loc);
        var pairs = new (int Pair, float Dot)[]
        {
            (12, Vector2.Dot(v1, v2)), (13, Vector2.Dot(v1, v3)), (14, Vector2.Dot(v1, v4)),
            (23, Vector2.Dot(v2, v3)), (24, Vector2.Dot(v2, v4)), (34, Vector2.Dot(v3, v4)),
        };
        int curPair = 12;
        float curDot = pairs[0].Dot;
        foreach (var (pair, d) in pairs.Skip(1))
        {
            if (d < curDot)
            {
                curPair = pair;
                curDot = d;
            }
        }
        var alignVector = curPair switch
        {
            12 => v2 - v1,
            13 => v3 - v1,
            14 => v4 - v1,
            23 => v3 - v2,
            24 => v4 - v2,
            _ => v4 - v3,
        };
        alignVector = Normalize(alignVector) * (0.5f * scale);
        float width = Roads[index1].WidthInTexture;
        switch (curPair)
        {
            case 12: Offset4Way(pc1, pc2!, pc3!, pr3, pc4!, loc, alignVector, width); break;
            case 13: Offset4Way(pc1, pc3!, pc2!, pr2, pc4!, loc, alignVector, width); break;
            case 14: Offset4Way(pc1, pc4!, pc3!, pr3, pc2!, loc, alignVector, width); break;
            case 23: Offset4Way(pc2!, pc3!, pc1, pr1, pc4!, loc, alignVector, width); break;
            case 24: Offset4Way(pc2!, pc4!, pc1, pr1, pc3!, loc, alignVector, width); break;
            default: Offset4Way(pc3!, pc4!, pc1, pr1, pc2!, loc, alignVector, width); break;
        }
        if (alignVector.X < 0)
            alignVector = -alignVector;
        if (Full)
            return;
        var road = NewJunction(loc, loc + alignVector, Roads[index1], Corner.FourWay, -4);
        road.WidthInTexture = TeeWidthAdjustment;
    }

    void InsertTeeIntersections()
    {
        int numRoadSegments = Roads.Count;
        for (int i = 0; i < numRoadSegments; i++)
        {
            if (Roads[i].Type != Corner.Segment)
                continue;
            if (Roads[i].Pt1.Count == 2) InsertTee(Roads[i].Pt1.Loc, i, Roads[i].Scale);
            if (Roads[i].Pt2.Count == 2) InsertTee(Roads[i].Pt2.Loc, i, Roads[i].Scale);
            if (Roads[i].Pt1.Count == 3) Insert4Way(Roads[i].Pt1.Loc, i, Roads[i].Scale);
            if (Roads[i].Pt2.Count == 3) Insert4Way(Roads[i].Pt2.Loc, i, Roads[i].Scale);
        }
    }

    void InsertCurveSegments()
    {
        int numRoadSegments = Roads.Count;
        int segmentStartIndex = -1;
        for (int i = 0; i < numRoadSegments; i++)
        {
            if (i < numRoadSegments - 1 && Roads[i].Pt1.Loc == Roads[i + 1].Pt2.Loc)
            {
                if (Roads[i + 1].Pt2.Count == 1 && Roads[i].Pt1.Count == 1)
                {
                    InsertCurveSegmentAt(i, i + 1);
                    if (segmentStartIndex < 0)
                        segmentStartIndex = i;
                }
            }
            else if (segmentStartIndex >= 0)
            {
                // A closed loop: bend the last piece into the first.
                if (Roads[i].Pt1.Loc == Roads[segmentStartIndex].Pt2.Loc && Roads[segmentStartIndex].Pt2.Count == 1 && Roads[i].Pt1.Count == 1)
                    InsertCurveSegmentAt(i, segmentStartIndex);
                segmentStartIndex = -1;
            }
        }
    }

    int FindCrossTypeJoinVector(Vector2 loc, ref Vector2 joinVector, int uniqueId)
    {
        foreach (var road in Roads)
        {
            if (road.UniqueId == uniqueId || road.Type != Corner.Segment)
                continue;
            var loc1 = road.Pt1.Loc;
            var loc2 = road.Pt2.Loc;
            var lo = Vector2.Min(loc1, loc2) - new Vector2(road.Scale / 2);
            var hi = Vector2.Max(loc1, loc2) + new Vector2(road.Scale / 2);
            if (loc.X < lo.X || loc.Y < lo.Y || loc.X > hi.X || loc.Y > hi.Y)
                continue;
            var ptOnLine = ClosestPointOnSegment(loc1, loc2, loc);
            if (Vector2.Distance(ptOnLine, loc) < road.Scale * 0.55f)
            {
                var roadVec = loc2 - loc1;
                roadVec = Rotate(roadVec, XpSign(roadVec, joinVector) == 1 ? MathF.PI / 2 : -MathF.PI / 2);
                joinVector = roadVec;
                return road.UniqueId;
            }
        }
        return 0;
    }

    void AdjustStacking(int topUniqueId, int bottomUniqueId)
    {
        var top = RoadTypes.FirstOrDefault(t => t.UniqueId == topUniqueId);
        var bottom = RoadTypes.FirstOrDefault(t => t.UniqueId == bottomUniqueId);
        if (top == null || bottom == null || top.Stacking > bottom.Stacking)
            return;
        int newStacking = bottom.Stacking;
        foreach (var t in RoadTypes)
            if (t.Stacking > newStacking)
                t.Stacking++;
        top.Stacking = newStacking + 1;
    }

    void InsertCrossTypeJoins()
    {
        int numRoadSegments = Roads.Count;
        for (int i = 0; i < numRoadSegments; i++)
        {
            var road = Roads[i];
            Vector2 loc1, loc2;
            bool isPt1 = false;
            if (road.Pt2.Count == 0 && road.Pt2.IsJoin)
            {
                loc1 = road.Pt2.Loc;
                loc2 = road.Pt1.Loc;
            }
            else if (road.Pt1.Count == 0 && road.Pt1.IsJoin)
            {
                loc1 = road.Pt1.Loc;
                loc2 = road.Pt2.Loc;
                isPt1 = true;
            }
            else
                continue;
            var roadVector = Normalize(loc2 - loc1);
            var joinVector = roadVector;
            int otherId = FindCrossTypeJoinVector(loc1, ref joinVector, road.UniqueId);
            if (otherId == 0)
                joinVector *= 100;
            var roadNormal = new Vector2(-roadVector.Y, roadVector.X);
            var joinNormal = new Vector2(-joinVector.Y, joinVector.X);
            float half = road.Scale * road.WidthInTexture / 2;
            var top = road.Pt1.Top;
            if (LineIntersection(loc1, loc1 + joinNormal, loc1 + roadNormal * half, loc2 + roadNormal * half, out var p))
            {
                if (isPt1) { road.Pt1.Top = p; top = road.Pt1.Top; }
                else { road.Pt2.Bottom = p; top = road.Pt2.Bottom; }
            }
            var bottom = road.Pt1.Bottom;
            if (LineIntersection(loc1, loc1 + joinNormal, loc1 - roadNormal * half, loc2 - roadNormal * half, out p))
            {
                if (isPt1) { road.Pt1.Bottom = p; bottom = road.Pt1.Bottom; }
                else { road.Pt2.Top = p; bottom = road.Pt2.Top; }
            }
            float scaleAdjustment = (bottom - top).Length() / (road.Scale * road.WidthInTexture);
            if (otherId != 0)
                AdjustStacking(road.UniqueId, otherId);
            if (Full)
                return;
            var join = NewJunction(loc1, loc1 + joinVector, road, Corner.AlphaJoin, 0);
            join.WidthInTexture = road.Scale * scaleAdjustment;
        }
    }

    void Miter(int ndx1, int ndx2)
    {
        var r1 = Roads[ndx1];
        var r2 = Roads[ndx2];
        if (LineIntersection(r1.Pt1.Top, r1.Pt2.Top, r2.Pt1.Top, r2.Pt2.Top, out var p))
        {
            r2.Pt2.Top = p;
            r1.Pt1.Top = p;
        }
        if (LineIntersection(r1.Pt1.Bottom, r1.Pt2.Bottom, r2.Pt1.Bottom, r2.Pt2.Bottom, out p))
        {
            r2.Pt2.Bottom = p;
            r1.Pt1.Bottom = p;
        }
    }

    static Vector2 RotateAbout(Vector2 pt, Vector2 center, float angle) => center + Rotate(pt - center, angle);

    void InsertCurveSegmentAt(int ndx1, int ndx2)
    {
        const float DotLimit = 0.5f;
        var r1 = Roads[ndx1];
        var r2 = Roads[ndx2];
        if (r1.UniqueId != r2.UniqueId)
            return;
        float radius = r1.CurveRadius * r1.Scale;
        var originalPt = r1.Pt1.Loc;
        var dir1 = Normalize(r1.Pt2.Loc - r1.Pt1.Loc);
        var dir2 = Normalize(r2.Pt2.Loc - r2.Pt1.Loc);
        float curSin = Vector2.Dot(dir1, dir2);
        float xpdct = CrossZ(dir1, dir2);

        // The four corner points, ordered so the turn is to the right (refs into the two roads).
        RoadPt p1Pt, p2Pt, p3Pt, p4Pt;
        if (xpdct > 0)
        {
            p1Pt = r1.Pt1; p2Pt = r1.Pt2; p3Pt = r2.Pt1; p4Pt = r2.Pt2;
        }
        else
        {
            p4Pt = r1.Pt1; p3Pt = r1.Pt2; p2Pt = r2.Pt1; p1Pt = r2.Pt2;
        }
        var line1Dir = Normalize(p2Pt.Loc - p1Pt.Loc);
        var line2Dir = Normalize(p4Pt.Loc - p3Pt.Loc);
        var line1a = p1Pt.Loc; var line1b = p2Pt.Loc;
        var line2a = p3Pt.Loc; var line2b = p4Pt.Loc;

        float angle = MathF.Acos(Math.Clamp(curSin, -1, 1));
        float count = angle / (MathF.PI / 6);
        if (count < 0.9f || r1.Pt1.IsAngled)
        {
            Miter(ndx1, ndx2);
            return;
        }
        var offset1 = Rotate(radius * line1Dir, -MathF.PI / 2);
        var offset2 = Rotate(radius * line2Dir, -MathF.PI / 2);
        if (!LineIntersection(line1a + offset1, line1b + offset1, line2a + offset2, line2b + offset2, out var pInt1))
            return;
        r2.Pt2.Last = true;
        // Where the curve meets each road.
        LineIntersection(pInt1, pInt1 - offset2, line2a, line2b, out var meet2);
        LineIntersection(pInt1, pInt1 - offset1, line1a, line1b, out var meet1);
        if (Vector2.Dot(line2Dir, meet2 - p3Pt.Loc) < DotLimit || Vector2.Dot(line1Dir, p2Pt.Loc - meet1) < DotLimit)
        {
            p1Pt.Loc = originalPt;
            p4Pt.Loc = originalPt;
            Miter(ndx1, ndx2);
            return;
        }
        p4Pt.Loc = meet2;
        float step = -MathF.PI / 6;
        var pt2 = p4Pt.Loc;
        var pt1 = p3Pt.Loc;
        var direction = pt1 - pt2;
        var centerOfCurve = Normalize(new Vector2(-direction.Y, direction.X)) * (r1.CurveRadius * r1.Scale) + pt2;
        pt2 = RotateAbout(pt2, centerOfCurve, step);
        direction = Rotate(direction, step);
        pt1 = pt2 + direction;
        if (Full)
            return;
        AddCurve(pt2, pt1, r1);
        if (count > 2)
        {
            for (int i = 2; i < count; i++)
            {
                direction = Rotate(direction, step);
                pt2 = RotateAbout(pt2, centerOfCurve, step);
                pt1 = pt2 + direction;
                if (Full)
                    return;
                AddCurve(pt2, pt1, r1);
            }
        }
        p1Pt.Loc = meet1;
        r1.Pt1.Last = true;
        if (count > 1)
        {
            pt2 = p1Pt.Loc;
            pt1 = p1Pt.Loc + p1Pt.Loc - p2Pt.Loc;
            if (Full)
                return;
            AddCurve(pt2, pt1, r1);
        }
        var roadVector = r1.Pt2.Loc - r1.Pt1.Loc;
        var roadNormal = Normalize(new Vector2(-roadVector.Y, roadVector.X)) * (r1.Scale * r1.WidthInTexture / 2);
        r1.Pt1.Top = r1.Pt1.Loc + roadNormal;
        r1.Pt1.Bottom = r1.Pt1.Loc - roadNormal;
        roadVector = r2.Pt2.Loc - r2.Pt1.Loc;
        roadNormal = Normalize(new Vector2(-roadVector.Y, roadVector.X)) * (r2.Scale * r2.WidthInTexture / 2);
        r2.Pt2.Top = r2.Pt2.Loc + roadNormal;
        r2.Pt2.Bottom = r2.Pt2.Loc - roadNormal;
    }

    void AddCurve(Vector2 from, Vector2 to, RoadSegment like)
    {
        var curve = NewJunction(from, to, like, Corner.Curve, 0);
        curve.CurveRadius = like.CurveRadius;
    }

    // ---- Geometry (the original's load* functions) ----

    void PreloadRoads()
    {
        foreach (var road in Roads.Where(r => r.Type == Corner.Segment).ToList())
            PreloadRoadSegment(road);
        foreach (var road in Roads.Where(r => r.Type == Corner.Curve).ToList())
            LoadCurve(road, road.Pt1.Loc, road.Pt2.Loc, road.Scale);
        foreach (var road in Roads.Where(r => r.Type == Corner.ThreeWayY).ToList())
            LoadY(road, road.Pt1.Loc, road.Pt2.Loc, road.Scale);
        foreach (var road in Roads.Where(r => r.Type is Corner.ThreeWayH or Corner.ThreeWayHFlip).ToList())
            LoadH(road, road.Pt1.Loc, road.Pt2.Loc, road.Type == Corner.ThreeWayHFlip, road.Scale);
        foreach (var road in Roads.Where(r => r.Type is Corner.Tee or Corner.FourWay).ToList())
            LoadTee(road, road.Pt1.Loc, road.Pt2.Loc, road.Type == Corner.FourWay, road.Scale);
        foreach (var road in Roads.Where(r => r.Type == Corner.AlphaJoin).ToList())
            LoadAlphaJoin(road, road.Pt1.Loc, road.Pt2.Loc, road.Scale);
    }

    static (Vector2 Vector, Vector2 Normal) Directions(Vector2 loc1, Vector2 loc2)
    {
        var roadVector = loc2 - loc1;
        var roadNormal = new Vector2(-roadVector.Y, roadVector.X);
        if (MathF.Abs(roadVector.X) < MinRoadSegment && MathF.Abs(roadVector.Y) < MinRoadSegment)
            return (new Vector2(1, 0), new Vector2(0, 1));
        return (Normalize(roadVector), Normalize(roadNormal));
    }

    void LoadTee(RoadSegment road, Vector2 loc1, Vector2 loc2, bool is4Way, float scale)
    {
        float uOffset = 425f / 512f;
        float vOffset = is4Way ? 425f / 512f : 255f / 512f;
        float teeFactor = scale * TeeWidthAdjustment / 2;
        float left = road.WidthInTexture * scale / 2;
        LoadFloatSection(road, loc1, loc2 - loc1, teeFactor, left, teeFactor, uOffset, vOffset, scale);
    }

    void LoadAlphaJoin(RoadSegment road, Vector2 loc1, Vector2 loc2, float scale)
    {
        var (roadVector, roadNormal) = Directions(loc1, loc2);
        float uOffset = 106f / 512f, vOffset = 425f / 512f;
        float uScale = road.WidthInTexture;
        roadVector *= scale * 48 / 128;
        roadNormal *= uScale * (1 + 8f / 128);
        var corners = new Vector2[4];
        corners[TopLeft] = loc1 + roadNormal * 0.5f - roadVector * 0.65f;
        corners[BottomLeft] = corners[TopLeft] - roadNormal;
        corners[BottomRight] = corners[BottomLeft] + roadVector;
        corners[TopRight] = corners[TopLeft] + roadVector;
        LoadFloat4PtSection(road, loc1, roadNormal, roadVector, corners, uOffset, vOffset, scale, uScale);
    }

    void LoadY(RoadSegment road, Vector2 loc1, Vector2 loc2, float scale)
    {
        var (roadVector, roadNormal) = Directions(loc1, loc2);
        float uOffset = 255f / 512f, vOffset = 226f / 512f;
        roadVector *= scale * 1.59f;
        roadNormal *= scale;
        var corners = new Vector2[4];
        corners[TopLeft] = loc1 + roadNormal * 0.29f - roadVector * 0.5f;
        corners[BottomLeft] = corners[TopLeft] - roadNormal * 1.08f;
        corners[BottomRight] = corners[BottomLeft] + roadVector;
        corners[TopRight] = corners[TopLeft] + roadVector;
        LoadFloat4PtSection(road, loc1, roadNormal, roadVector, corners, uOffset, vOffset, scale, scale);
    }

    void LoadH(RoadSegment road, Vector2 loc1, Vector2 loc2, bool flip, float scale)
    {
        var (roadVector, roadNormal) = Directions(loc1, loc2);
        float uOffset = 202f / 512f, vOffset = 364f / 512f;
        roadVector *= scale;
        roadNormal *= scale * 1.35f;
        var corners = new Vector2[4];
        corners[BottomLeft] = flip
            ? loc1 - roadNormal * 0.20f - roadVector * road.WidthInTexture / 2
            : loc1 - roadNormal * 0.8f - roadVector * road.WidthInTexture / 2;
        var width = roadVector * road.WidthInTexture / 2 + roadVector * 1.2f;
        corners[BottomRight] = corners[BottomLeft] + width;
        corners[TopRight] = corners[BottomRight] + roadNormal;
        corners[TopLeft] = corners[BottomLeft] + roadNormal;
        if (flip)
            roadNormal = -roadNormal;
        LoadFloat4PtSection(road, loc1, roadNormal, roadVector, corners, uOffset, vOffset, scale, scale);
    }

    void LoadFloatSection(RoadSegment road, Vector2 loc, Vector2 roadVector, float halfHeight, float left, float right, float uOffset, float vOffset, float scale)
    {
        var roadNormal = Normalize(new Vector2(-roadVector.Y, roadVector.X));
        roadVector = Normalize(roadVector) * right;
        halfHeight = MathF.Abs(halfHeight);
        roadNormal *= halfHeight;
        var roadLeft = Normalize(roadVector) * left;
        roadVector += roadLeft;
        var leftCenter = loc - roadLeft;
        var corners = new Vector2[4];
        corners[BottomLeft] = leftCenter - roadNormal;
        corners[BottomRight] = corners[BottomLeft] + roadVector;
        corners[TopRight] = corners[BottomRight] + 2 * roadNormal;
        corners[TopLeft] = corners[BottomLeft] + 2 * roadNormal;
        LoadFloat4PtSection(road, loc, roadNormal, roadVector, corners, uOffset, vOffset, scale, scale);
    }

    void LoadCurve(RoadSegment road, Vector2 loc1, Vector2 loc2, float scale)
    {
        bool tight = road.CurveRadius == TightCornerRadius;
        float uOffset = 4f / 512f;
        float vOffset = tight ? 425f / 512f : 255f / 512f;
        var roadVector = loc2 - loc1;
        var roadNormal = new Vector2(-roadVector.Y, roadVector.X);
        float curveHeight = road.WidthInTexture * scale / 2;
        roadVector = Normalize(roadVector) * scale;
        roadNormal = Normalize(roadNormal) * MathF.Abs(curveHeight);
        var corners = new Vector2[4];
        corners[BottomLeft] = loc1 - roadNormal;
        if (tight)
        {
            corners[BottomRight] = corners[BottomLeft] + roadVector * 0.5f;
            corners[TopRight] = corners[BottomRight] + 2 * roadNormal;
            corners[TopLeft] = corners[BottomLeft] + 2 * roadNormal;
            corners[BottomRight] += roadVector * 0.1f + roadNormal * 0.2f;
            corners[BottomLeft] -= roadNormal * 0.1f + roadVector * 0.02f;
            corners[TopLeft] -= roadVector * 0.02f;
            corners[TopRight] += -roadVector * 0.4f + roadNormal * 0.2f;
        }
        else
        {
            corners[BottomRight] = corners[BottomLeft] + roadVector;
            corners[TopRight] = corners[BottomRight] + 2 * roadNormal;
            corners[TopLeft] = corners[BottomLeft] + 2 * roadNormal;
            corners[BottomRight] += roadVector * 0.1f + roadNormal * 0.4f;
            corners[BottomLeft] -= roadNormal * 0.2f + roadVector * 0.02f;
            corners[TopLeft] -= roadVector * 0.02f;
            corners[TopRight] += -roadVector * 0.4f + roadNormal * 0.4f;
        }
        LoadFloat4PtSection(road, loc1, roadNormal, roadVector, corners, uOffset, vOffset, scale, scale);
    }

    void PreloadRoadSegment(RoadSegment road)
    {
        var roadVector = road.Pt2.Loc - road.Pt1.Loc;
        var roadNormal = Normalize(new Vector2(-roadVector.Y, roadVector.X)) * (road.WidthInTexture * road.Scale / 2);
        var corners = new Vector2[4];
        corners[BottomLeft] = road.Pt1.Bottom;
        corners[TopLeft] = road.Pt1.Top;
        corners[BottomRight] = road.Pt2.Bottom;
        corners[TopRight] = road.Pt2.Top;
        LoadFloat4PtSection(road, road.Pt1.Loc, roadNormal, roadVector, corners, 0, 85f / 512f, road.Scale, road.Scale);
    }

    const int BottomLeft = 0, BottomRight = 1, TopLeft = 2, TopRight = 3;

    /// <summary>The highest of the four height-map corners around a point (the original's getMaxCellHeight).</summary>
    float MaxCellHeight(float x, float y)
    {
        int iX = (int)(x / MapXYFactor) + heights.BorderSize;
        int iY = (int)(y / MapXYFactor) + heights.BorderSize;
        iX = Math.Clamp(iX, 0, heights.Width - 2);
        iY = Math.Clamp(iY, 0, heights.Height - 2);
        float p0 = heights.WorldHeight(iX, iY), p1 = heights.WorldHeight(iX + 1, iY);
        float p2 = heights.WorldHeight(iX + 1, iY + 1), p3 = heights.WorldHeight(iX, iY + 1);
        return Math.Max(Math.Max(p0, p1), Math.Max(p2, p3));
    }

    sealed class Column
    {
        public bool Collapsed, Deleted;
        public Vector3[] Vtx;
        public int[] VertexIndex;
        public float UIndex;
        public Column(int rows) { Vtx = new Vector3[rows]; VertexIndex = new int[rows]; Deleted = true; }
        public Column Clone() => new(Vtx.Length) { Collapsed = Collapsed, Deleted = Deleted, Vtx = (Vector3[])Vtx.Clone(), VertexIndex = (int[])VertexIndex.Clone(), UIndex = UIndex };
    }

    /// <summary>
    /// Drapes a four-cornered piece over the terrain (the original's loadFloat4PtSection): columns of
    /// points every map cell along the road, each lifted to the highest terrain under it, with columns
    /// that lie on a straight slope between their neighbours dropped.
    /// </summary>
    void LoadFloat4PtSection(RoadSegment road, Vector2 loc, Vector2 roadNormal, Vector2 roadVector, Vector2[] corners, float uOffset, float vOffset, float uScale, float vScale)
    {
        const float FloatAmount = MapHeightScale / 8;
        const float MaxError = MapHeightScale * 1.1f;
        var vb = new List<Vertex>();
        var ib = new List<ushort>();
        float roadLen = roadVector.Length();
        float halfHeight = roadNormal.Length();
        roadNormal = Normalize(roadNormal);
        roadVector = Normalize(roadVector);
        int uCount = Math.Max(2, (int)(roadLen / MapXYFactor) + 1);
        int vCount = Math.Max(2, (int)(2 * halfHeight / MapXYFactor) + 1);
        const int MaxRows = 100;
        vCount = Math.Min(vCount, MaxRows);

        var prevColumn = new Column(vCount);
        var curColumn = new Column(vCount);
        var nextColumn = new Column(vCount);

        var origin = new Vector3(corners[BottomLeft], 0);
        var uVector1 = new Vector3(corners[BottomRight] - corners[BottomLeft], 0);
        var uVector2 = new Vector3(corners[TopRight] - corners[TopLeft], 0);
        var vVector1 = new Vector3(corners[TopLeft] - corners[BottomLeft], 0);
        var vVector2 = new Vector3(corners[TopRight] - corners[BottomRight], 0);
        uVector2 += vVector1 - vVector2;

        for (int i = 0; i <= uCount; i++)
        {
            float iFactor = (float)i / (uCount - 1);
            float iBarFactor = 1 - iFactor;
            if (i < uCount)
            {
                nextColumn = new Column(vCount) { Collapsed = false, Deleted = false, UIndex = i };
                float minHeight = 255 * MapHeightScale, maxHeight = 0;
                for (int j = 0; j < vCount; j++)
                {
                    float jFactor = (float)j / (vCount - 1);
                    float jBarFactor = 1 - jFactor;
                    var v = origin + uVector1 * jBarFactor * iFactor + uVector2 * jFactor * iFactor + vVector1 * iBarFactor * jFactor + vVector2 * iFactor * jFactor;
                    float z = MaxCellHeight(v.X, v.Y);
                    minHeight = Math.Min(minHeight, z);
                    maxHeight = Math.Max(maxHeight, z);
                    nextColumn.VertexIndex[j] = -1;
                    nextColumn.Vtx[j] = v with { Z = z };
                }
                // (The original always collapses each column to its two edge points at the highest height.)
                nextColumn.Collapsed = true;
                nextColumn.Vtx[0].Z = maxHeight;
                nextColumn.Vtx[1] = nextColumn.Vtx[vCount - 1] with { Z = maxHeight };
                if (i < 2)
                    curColumn = nextColumn;
                else if (prevColumn.Collapsed && curColumn.Collapsed && nextColumn.Collapsed)
                {
                    float span = nextColumn.UIndex - prevColumn.UIndex;
                    float theZ = (prevColumn.Vtx[0].Z * (curColumn.UIndex - prevColumn.UIndex) + nextColumn.Vtx[0].Z * (nextColumn.UIndex - curColumn.UIndex)) / span;
                    if (theZ >= curColumn.Vtx[0].Z && theZ < curColumn.Vtx[0].Z + MaxError)
                    {
                        theZ = (prevColumn.Vtx[1].Z * (curColumn.UIndex - prevColumn.UIndex) + nextColumn.Vtx[1].Z * (nextColumn.UIndex - curColumn.UIndex)) / span;
                        if (theZ >= curColumn.Vtx[1].Z && theZ < curColumn.Vtx[1].Z + MaxError)
                            curColumn.Deleted = true;
                    }
                }
            }
            if (!curColumn.Deleted && i != 1)
            {
                for (int j = 0; j < vCount; j++)
                {
                    if (vb.Count >= MaxSegIndex)
                        break;
                    var curVector = new Vector2(curColumn.Vtx[j].X - loc.X, curColumn.Vtx[j].Y - loc.Y);
                    float V = Vector2.Dot(roadNormal, curVector);
                    float U = Vector2.Dot(roadVector, curVector);
                    var position = curColumn.Vtx[j] with { Z = curColumn.Vtx[j].Z + FloatAmount };
                    vb.Add(new Vertex(position, new Vector2(uOffset + U / (uScale * 4), vOffset - V / (vScale * 4))));
                    curColumn.VertexIndex[j] = vb.Count - 1;
                    if (j == 1 && curColumn.Collapsed)
                        break;
                }
                if (vb.Count >= MaxSegIndex)
                    break;
                if (i > 1)
                {
                    int j = 0, k = 0;
                    while (j < vCount - 1 && k < vCount - 1)
                    {
                        if (ib.Count >= MaxSegIndex)
                            break;
                        if (k == 0 || !prevColumn.Collapsed)
                        {
                            ib.Add((ushort)prevColumn.VertexIndex[j + 1]);
                            ib.Add((ushort)prevColumn.VertexIndex[j]);
                            ib.Add((ushort)curColumn.VertexIndex[k]);
                        }
                        if (j == 0 || !curColumn.Collapsed)
                        {
                            int offset = curColumn.Collapsed && !prevColumn.Collapsed ? vCount - 1 : 1;
                            ib.Add((ushort)prevColumn.VertexIndex[j + offset]);
                            ib.Add((ushort)curColumn.VertexIndex[k]);
                            ib.Add((ushort)curColumn.VertexIndex[k + 1]);
                        }
                        if (prevColumn.Collapsed && curColumn.Collapsed)
                            break;
                        if (!prevColumn.Collapsed)
                            j++;
                        if (!curColumn.Collapsed)
                            k++;
                    }
                    prevColumn = curColumn;
                }
                else if (i == 0)
                    prevColumn = curColumn;
                if (ib.Count >= MaxSegIndex)
                    break;
            }
            curColumn = nextColumn;
        }
        road.Vertices = vb.Count <= MaxSegVertex ? vb.ToArray() : [];
        road.Indices = road.Vertices.Length > 0 ? ib.ToArray() : [];
    }

    /// <summary>
    /// The pieces to draw, grouped by road type in stacking order, each type's pieces by kind (straight
    /// pieces first, joins last), as the original's drawRoads.
    /// </summary>
    public IEnumerable<(RoadType Type, List<RoadSegment> Pieces)> DrawOrder()
    {
        int maxStacking = RoadTypes.Count > 0 ? RoadTypes.Max(t => t.Stacking) : 0;
        for (int stacking = 0; stacking <= maxStacking; stacking++)
        {
            foreach (var type in RoadTypes.Where(t => t.Stacking == stacking))
            {
                var pieces = new List<RoadSegment>();
                foreach (Corner corner in Enum.GetValues<Corner>())
                    pieces.AddRange(Roads.Where(r => r.UniqueId == type.UniqueId && r.Type == corner && r.Indices.Length > 0));
                if (pieces.Count > 0)
                    yield return (type, pieces);
            }
        }
    }
}

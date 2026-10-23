using System.Numerics;
using OpenGZH.Formats.W3d;

namespace OpenGZH.Logic.Art;

/// <summary>
/// A map bridge's model, split as the original's W3DBridge::load: the meshes named
/// "{model}.BRIDGE_LEFT", ".BRIDGE_SPAN" and ".BRIDGE_RIGHT" with their pose transforms. A bridge
/// with all three (and parts that line up) is SECTIONAL: the span repeats to fill the distance;
/// otherwise it is FIXED and the left mesh is stretched. The extents set the bridge's width.
/// </summary>
public sealed class BridgeModel
{
    public enum BridgeType { Sectional, Fixed }

    public W3dMesh LeftMesh { get; }
    public W3dMesh? SectionMesh { get; }
    public W3dMesh? RightMesh { get; }
    public Matrix4x4 LeftMtx { get; }
    public Matrix4x4 SectionMtx { get; }
    public Matrix4x4 RightMtx { get; }
    public BridgeType Type { get; }
    public float LeftMinX { get; }
    public float LeftMaxX { get; }
    public float SectionMinX { get; }
    public float SectionMaxX { get; }
    public float RightMinX { get; }
    public float RightMaxX { get; }
    public float MinY { get; }
    public float MaxY { get; }
    /// <summary>Unscaled length of left end to right end.</summary>
    public float Length { get; }

    BridgeModel(W3dMesh left, Matrix4x4 leftMtx, W3dMesh? section, Matrix4x4 sectionMtx, W3dMesh? right, Matrix4x4 rightMtx)
    {
        LeftMesh = left;
        LeftMtx = leftMtx;
        SectionMesh = section;
        SectionMtx = sectionMtx;
        RightMesh = right;
        RightMtx = rightMtx;
        var type = section == null || right == null ? BridgeType.Fixed : BridgeType.Sectional;

        float leftMinX = float.MaxValue, leftMaxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var v in left.Positions)
        {
            var t = Vector3.Transform(v, leftMtx);
            leftMinX = Math.Min(leftMinX, t.X);
            leftMaxX = Math.Max(leftMaxX, t.X);
            minY = Math.Min(minY, t.Y);
            maxY = Math.Max(maxY, t.Y);
        }
        float sectionMinX, sectionMaxX, rightMinX, rightMaxX;
        if (type == BridgeType.Sectional)
        {
            (sectionMinX, sectionMaxX) = XRange(section!, sectionMtx);
            (rightMinX, rightMaxX) = XRange(right!, rightMtx);
        }
        else
            sectionMinX = sectionMaxX = rightMinX = rightMaxX = leftMaxX;

        float length = Math.Max(1, rightMaxX - leftMinX);
        if (type == BridgeType.Sectional)
        {
            float allowableError = 0.05f * length;
            if (leftMaxX > sectionMinX + allowableError || rightMinX < sectionMaxX - allowableError)
                type = BridgeType.Fixed;
        }
        Type = type;
        LeftMinX = leftMinX;
        LeftMaxX = leftMaxX;
        SectionMinX = sectionMinX;
        SectionMaxX = sectionMaxX;
        RightMinX = rightMinX;
        RightMaxX = rightMaxX;
        MinY = minY;
        MaxY = maxY;
        Length = length;
    }

    static (float, float) XRange(W3dMesh mesh, Matrix4x4 mtx)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in mesh.Positions)
        {
            float x = Vector3.Transform(v, mtx).X;
            min = Math.Min(min, x);
            max = Math.Max(max, x);
        }
        return (min, max);
    }

    static readonly Dictionary<string, BridgeModel?> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads (and caches) a bridge model; null if it has no BRIDGE_LEFT mesh.</summary>
    public static BridgeModel? Load(W3dLibrary library, string modelName)
    {
        lock (cache)
        {
            if (cache.TryGetValue(modelName, out var cached))
                return cached;
            var model = LoadUncached(library, modelName);
            cache[modelName] = model;
            return model;
        }
    }

    static BridgeModel? LoadUncached(W3dLibrary library, string modelName)
    {
        var file = library.GetFile(modelName);
        if (file == null)
            return null;
        var hierarchy = file.Hierarchy ?? (file.HLod != null ? library.GetHierarchy(file.HLod.HierarchyName) : null);
        var pose = hierarchy != null ? new W3dPose(hierarchy.Pivots.Count) : null;
        pose?.Update(hierarchy, Matrix4x4.Identity);

        (W3dMesh? Mesh, Matrix4x4 Mtx) Find(string suffix)
        {
            string prefix = modelName + suffix;
            var subObjects = (file.HLod?.HighestLod ?? []).Concat(file.HLod?.Aggregates ?? []);
            foreach (var sub in subObjects)
            {
                if (!sub.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var mesh = library.GetMesh(sub.Name, file);
                if (mesh == null)
                    continue;
                var mtx = pose != null && sub.BoneIndex >= 0 && sub.BoneIndex < pose.Transforms.Length ? pose.Transforms[sub.BoneIndex] : Matrix4x4.Identity;
                return (mesh, mtx);
            }
            // A plain mesh file without an HLOD.
            var plain = file.Meshes.FirstOrDefault(m => m.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return (plain, Matrix4x4.Identity);
        }

        var left = Find(".BRIDGE_LEFT");
        if (left.Mesh == null)
            return null;
        var section = Find(".BRIDGE_SPAN");
        var right = Find(".BRIDGE_RIGHT");
        return new BridgeModel(left.Mesh, left.Mtx, section.Mesh, section.Mtx, right.Mesh, right.Mtx);
    }
}

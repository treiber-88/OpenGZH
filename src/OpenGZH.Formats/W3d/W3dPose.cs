using System.Numerics;

namespace OpenGZH.Formats.W3d;

/// <summary>
/// World transforms for every pivot of a hierarchy, either at rest or at a frame of an animation.
///
/// Matches the original engine: each pivot's transform is
///   parent * base * translate(animTranslation) * rotate(animRotation)
/// (written here in System.Numerics row-vector order, i.e. reversed).
/// </summary>
public sealed class W3dPose
{
    public Matrix4x4[] Transforms { get; }
    public bool[] Visible { get; }

    /// <summary>
    /// Extra local transforms applied to individual pivots after the animation (turret yaw/pitch,
    /// gun recoil). Applied as: parent * base * animation * override.
    /// </summary>
    public Dictionary<int, Matrix4x4> Overrides { get; } = new();

    public W3dPose(int pivotCount)
    {
        Transforms = new Matrix4x4[Math.Max(1, pivotCount)];
        Visible = new bool[Transforms.Length];
        Array.Fill(Transforms, Matrix4x4.Identity);
        Array.Fill(Visible, true);
    }

    /// <summary>Computes the pose. <paramref name="animation"/> may be null for the rest pose.</summary>
    public void Update(W3dHierarchy? hierarchy, Matrix4x4 root, W3dAnimation? animation = null, float frame = 0)
    {
        if (hierarchy == null || hierarchy.Pivots.Count == 0)
        {
            Transforms[0] = root;
            return;
        }

        // Pivot 0 is the root: the original engine sets it to the object transform directly.
        Transforms[0] = root;
        Visible[0] = true;

        var pivots = hierarchy.Pivots;
        for (int i = 1; i < pivots.Count && i < Transforms.Length; i++)
        {
            var pivot = pivots[i];
            Matrix4x4 parent = pivot.ParentIndex >= 0 && pivot.ParentIndex < i ? Transforms[pivot.ParentIndex] : root;
            Matrix4x4 local = pivot.BaseTransform;
            bool visible = true;

            if (animation != null && animation.AnimatesPivot(i))
            {
                var translation = animation.GetTranslation(i, frame);
                var rotation = animation.GetRotation(i, frame);
                local = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation) * local;
            }
            if (animation != null)
                visible = animation.IsVisible(i, frame);
            if (Overrides.TryGetValue(i, out var extra))
                local = extra * local;

            Transforms[i] = local * parent;
            Visible[i] = visible;
        }
    }
}

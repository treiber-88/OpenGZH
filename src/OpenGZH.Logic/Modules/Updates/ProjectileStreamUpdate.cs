using System.Numerics;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// Keeps the shots of a stream weapon in order so the client can draw them as one continuous jet
/// (the original's ProjectileStreamUpdate: the toxin truck's and Dragon tank's streams). A new target
/// starts a new segment; the stream goes when its shooter is gone and its last shot has landed.
/// </summary>
public sealed class ProjectileStreamUpdate : Module, IUpdateModule
{
    public const int MaxProjectileStream = 20;

    readonly uint[] projectileIds = new uint[MaxProjectileStream];
    uint owningObject, targetObject;
    Vector3 targetPosition;
    int nextFreeIndex, firstValidIndex;

    public int Update()
    {
        CullFrontOfList();
        if (ConsiderDying())
            Game.DestroyObject(Owner);
        return 0;
    }

    public void AddProjectile(uint sourceId, uint newId, uint victimId, Vector3? victimPos)
    {
        if (owningObject == 0)
            owningObject = sourceId;
        if (victimId != 0)
        {
            if (victimId != targetObject)
            {
                // A gap, so the stream doesn't join up across targets.
                projectileIds[nextFreeIndex] = 0;
                nextFreeIndex = (nextFreeIndex + 1) % MaxProjectileStream;
                targetObject = victimId;
            }
            targetPosition = Vector3.Zero;
        }
        else if (victimPos is { } pos)
        {
            if (targetPosition != pos)
            {
                projectileIds[nextFreeIndex] = 0;
                nextFreeIndex = (nextFreeIndex + 1) % MaxProjectileStream;
                targetPosition = pos;
            }
            targetObject = 0;
        }
        projectileIds[nextFreeIndex] = newId;
        nextFreeIndex = (nextFreeIndex + 1) % MaxProjectileStream;
    }

    void CullFrontOfList()
    {
        while (firstValidIndex != nextFreeIndex && Game.FindObject(projectileIds[firstValidIndex]) == null)
            firstValidIndex = (firstValidIndex + 1) % MaxProjectileStream;
    }

    bool ConsiderDying() => firstValidIndex == nextFreeIndex && owningObject != 0 && Game.FindObject(owningObject) == null;

    /// <summary>
    /// The stream's points in order, gaps as null (the original's getAllPoints). Points over the
    /// shooting vehicle are lifted to its roof so the jet doesn't dip into it.
    /// </summary>
    public List<Vector3?> AllPoints()
    {
        var points = new List<Vector3?>();
        var obj = Game.FindObject(owningObject);
        for (int i = firstValidIndex; i != nextFreeIndex; i = (i + 1) % MaxProjectileStream)
        {
            if (Game.FindObject(projectileIds[i]) is not { } projectile)
            {
                points.Add(null);
                continue;
            }
            var point = projectile.Position;
            if (obj != null && obj.IsKindOf("VEHICLE"))
            {
                float myTop = obj.Template.Geometry.MaxHeightAbovePosition + obj.Position.Z + 0.5f;
                if (new Vector2(obj.Position.X - point.X, obj.Position.Y - point.Y).Length() <= obj.Template.GeometryMajorRadius * 1.5f)
                    point.Z = Math.Max(point.Z, myTop);
            }
            points.Add(point);
        }
        return points;
    }

    public void SetPosition(Vector3 position) => Owner.SetPosition(position);
}

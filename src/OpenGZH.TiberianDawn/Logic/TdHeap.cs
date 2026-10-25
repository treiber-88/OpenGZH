namespace OpenGZH.TiberianDawn.Logic;

/// <summary>
/// TFixedIHeapClass (HEAP.CPP): a fixed number of slots for one kind of object. A new object takes
/// the first free slot (its ID), and the active objects are kept in creation order, which is the
/// order the game updates them in. A freed slot keeps its old object until reused (Raw_Ptr), as
/// the original's memory does, so stale references read it as inactive.
/// </summary>
public sealed class TdHeap<T> where T : class
{
    readonly T?[] slots;
    readonly bool[] used;
    readonly List<T> active = new();
    readonly Dictionary<T, int> ids = new(ReferenceEqualityComparer.Instance);

    public TdHeap(int size)
    {
        slots = new T?[size];
        used = new bool[size];
    }

    public int Count => active.Count;
    public int Length => slots.Length;
    public int Avail => slots.Length - active.Count;

    /// <summary>The index-th active object, in creation order (Ptr).</summary>
    public T Ptr(int index) => active[index];

    public IReadOnlyList<T> Active => active;

    /// <summary>Whatever object last used a slot (Raw_Ptr), or null.</summary>
    public T? RawPtr(int index) => index >= 0 && index < slots.Length ? slots[index] : null;

    /// <summary>ID: the object's slot (its heap index), also after it is freed (-1 if never placed).</summary>
    public int ID(T item) => ids.TryGetValue(item, out int i) ? i : -1;

    /// <summary>Allocate: reserves the first free slot; the caller then builds the object with <see cref="Place"/>. -1 if full.</summary>
    public int Allocate()
    {
        if (active.Count >= slots.Length)
            return -1;
        for (int i = 0; i < used.Length; i++)
            if (!used[i])
            {
                used[i] = true;
                return i;
            }
        return -1;
    }

    /// <summary>Puts a newly made object into its reserved slot and the active list.</summary>
    public void Place(int index, T item)
    {
        if (slots[index] is { } old)
            ids.Remove(old);
        slots[index] = item;
        ids[item] = index;
        active.Add(item);
    }

    /// <summary>Free: the slot becomes reusable; the object leaves the active list (order kept).</summary>
    public bool Free(T item)
    {
        int index = ID(item);
        if (index < 0 || !used[index])
            return false;
        used[index] = false;
        active.Remove(item);
        return true;
    }

    public void FreeAll()
    {
        Array.Clear(used);
        active.Clear();
    }
}

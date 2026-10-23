using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Vision;

/// <summary>What a player knows about a map cell.</summary>
public enum CellShroud { Clear, Fogged, Shrouded }

/// <summary>What a player knows about an object, from the cells under it.</summary>
public enum ObjectShroud { Clear, PartialClear, Fogged, Shrouded }

/// <summary>
/// Fog of war, as the original's partition cells keep it (cells of GameData PartitionCellSize).
///
/// Every cell has, per player, a "current shroud" counter: 1 = never seen (shrouded), 0 = seen
/// before (fogged), negative = being looked at by that many lookers (clear); and an "active shroud"
/// count of GPS scramblers covering it. Each object looks at a discrete circle of cells of its
/// ShroudClearingRange for its own and allied players (and for players spying on it); when it moves
/// to another cell, dies, or changes hands, the old look is undone UnlookPersistDuration later, so
/// sight lingers briefly. Objects with ShroudRevealToAllRange are seen by everyone around them, and
/// objects with ShroudRange cover their surroundings in shroud for their enemies.
/// </summary>
public sealed class ShroudManager
{
    const int MaxPlayers = 32;

    readonly GameLogic game;
    readonly float cellSize;
    readonly int cellsX, cellsY;
    readonly short[] currentShroud;   // [player * cells + cell]
    readonly short[] activeShroud;
    readonly int persistFrames;

    readonly struct Sighting(Vector2 where, float howFar, uint mask)
    {
        public Vector2 Where { get; } = where;
        public float HowFar { get; } = howFar;
        public uint Mask { get; } = mask;
    }

    sealed class ObjectVision
    {
        public Sighting? Look, RevealAll, Shroud;
        public (int X, int Y, float Range, uint Mask, float RevealAll, uint RevealMask, float Shroud, uint ShroudMask) Key;
    }

    readonly Dictionary<uint, ObjectVision> vision = new();
    readonly Queue<(int Frame, Sighting Sighting)> pendingUnlooks = new();

    /// <summary>Raised when a cell's state changes for a player (the client redraws its fog).</summary>
    public event Action<int, int, int, CellShroud>? CellChanged;

    public ShroudManager(GameLogic game)
    {
        this.game = game;
        cellSize = game.Catalog.GameData.PartitionCellSize;
        cellsX = Math.Max(1, (int)MathF.Ceiling(game.Terrain.WorldWidth / cellSize));
        cellsY = Math.Max(1, (int)MathF.Ceiling(game.Terrain.WorldHeight / cellSize));
        currentShroud = new short[MaxPlayers * cellsX * cellsY];
        activeShroud = new short[MaxPlayers * cellsX * cellsY];
        Array.Fill(currentShroud, (short)1);
        persistFrames = game.Catalog.GameData.UnlookPersistFrames;
    }

    public int CellsX => cellsX;
    public int CellsY => cellsY;
    public float CellSize => cellSize;

    int Index(int player, int x, int y) => (player * cellsY + y) * cellsX + x;

    public (int X, int Y) WorldToCell(float x, float y) => ((int)MathF.Floor(x / cellSize), (int)MathF.Floor(y / cellSize));

    int WorldToCellDist(float w) => (int)MathF.Ceiling(w / cellSize);

    // ---- Cells ----

    public CellShroud CellStatus(int player, int x, int y)
    {
        if (x < 0 || y < 0 || x >= cellsX || y >= cellsY || player < 0 || player >= MaxPlayers)
            return CellShroud.Shrouded;
        short v = currentShroud[Index(player, x, y)];
        return v == 1 ? CellShroud.Shrouded : v == 0 ? CellShroud.Fogged : CellShroud.Clear;
    }

    public CellShroud StatusAt(Player player, Vector3 pos)
    {
        var (x, y) = WorldToCell(pos.X, pos.Y);
        return CellStatus(player.Index, x, y);
    }

    void Changed(int player, int x, int y, CellShroud old)
    {
        var now = CellStatus(player, x, y);
        if (now != old)
            CellChanged?.Invoke(player, x, y, now);
    }

    void AddLooker(int player, int x, int y)
    {
        var old = CellStatus(player, x, y);
        int i = Index(player, x, y);
        currentShroud[i] = (short)Math.Min(currentShroud[i] - 1, -1);
        Changed(player, x, y, old);
    }

    void RemoveLooker(int player, int x, int y)
    {
        var old = CellStatus(player, x, y);
        int i = Index(player, x, y);
        if (currentShroud[i] == -1)
            currentShroud[i] = Math.Min(activeShroud[i], (short)1);
        else if (currentShroud[i] < 0)
            currentShroud[i]++;
        Changed(player, x, y, old);
    }

    void AddShrouder(int player, int x, int y)
    {
        var old = CellStatus(player, x, y);
        int i = Index(player, x, y);
        activeShroud[i]++;
        if (currentShroud[i] == 0)
            currentShroud[i] = 1;
        Changed(player, x, y, old);
    }

    void RemoveShrouder(int player, int x, int y)
    {
        int i = Index(player, x, y);
        if (activeShroud[i] > 0)
            activeShroud[i]--;
    }

    /// <summary>The original's DiscreteCircle: horizontal runs of cells covering a filled circle.</summary>
    static List<(int XStart, int XEnd, int Y)> DiscreteCircle(int cx, int cy, int radius)
    {
        var edges = new List<(int, int, int)>();
        int x = 0, y = radius, d = (1 - radius) << 1;
        while (y >= 0)
        {
            edges.Add((cx - x, cx + x, cy + y));
            if (d + y > 0)
            {
                --y;
                d -= (y << 1) - 1;
            }
            if (x > d)
            {
                ++x;
                d += (x << 1) + 1;
            }
        }
        for (int i = 0; i + 1 < edges.Count;)
        {
            if (edges[i].Item3 == edges[i + 1].Item3)
                edges.RemoveAt(i);
            else
                i++;
        }
        var lines = new List<(int, int, int)>();
        foreach (var (xs, xe, yp) in edges)
        {
            lines.Add((xs, xe, yp));
            if (yp != cy)
                lines.Add((xs, xe, (cy << 1) - yp));
        }
        return lines;
    }

    void ForCircle(Vector2 center, float radius, uint mask, Action<int, int, int> cell)
    {
        var (cx, cy) = WorldToCell(center.X, center.Y);
        int r = Math.Max(1, WorldToCellDist(radius));
        var lines = DiscreteCircle(cx, cy, r);
        for (int p = game.Players.All.Count - 1; p >= 0; p--)
        {
            int index = game.Players.All[p].Index;
            if (index < 0 || index >= MaxPlayers || (mask & (1u << index)) == 0)
                continue;
            foreach (var (xs, xe, y) in lines)
            {
                if (y < 0 || y >= cellsY || xs >= cellsX || xe < 0)
                    continue;
                for (int x = Math.Max(0, xs); x <= Math.Min(cellsX - 1, xe); x++)
                    cell(index, x, y);
            }
        }
    }

    public void DoShroudReveal(Vector2 center, float radius, uint mask) => ForCircle(center, radius, mask, AddLooker);
    public void UndoShroudReveal(Vector2 center, float radius, uint mask) => ForCircle(center, radius, mask, RemoveLooker);
    public void DoShroudCover(Vector2 center, float radius, uint mask) => ForCircle(center, radius, mask, AddShrouder);
    public void UndoShroudCover(Vector2 center, float radius, uint mask) => ForCircle(center, radius, mask, RemoveShrouder);

    void QueueUndoShroudReveal(Sighting s) => pendingUnlooks.Enqueue((game.Frame + persistFrames, s));

    void ProcessPendingUnlooks(bool all = false)
    {
        while (pendingUnlooks.Count > 0 && (all || pendingUnlooks.Peek().Frame < game.Frame))
        {
            var (_, s) = pendingUnlooks.Dequeue();
            UndoShroudReveal(s.Where, s.HowFar, s.Mask);
        }
    }

    /// <summary>Everything becomes explored (fogged) for the player (shroud crate, scripts).</summary>
    public void RevealMapForPlayer(Player player)
    {
        for (int y = 0; y < cellsY; y++)
            for (int x = 0; x < cellsX; x++)
            {
                AddLooker(player.Index, x, y);
                RemoveLooker(player.Index, x, y);
            }
    }

    /// <summary>Everything stays visible for the player until undone (script "reveal map permanently").</summary>
    public void RevealMapForPlayerPermanently(Player player)
    {
        for (int y = 0; y < cellsY; y++)
            for (int x = 0; x < cellsX; x++)
                AddLooker(player.Index, x, y);
    }

    public void UndoRevealMapForPlayerPermanently(Player player)
    {
        ProcessPendingUnlooks(all: true);
        for (int y = 0; y < cellsY; y++)
            for (int x = 0; x < cellsX; x++)
                RemoveLooker(player.Index, x, y);
    }

    /// <summary>Everything not currently seen goes back under the shroud (script "shroud map").</summary>
    public void ShroudMapForPlayer(Player player)
    {
        ProcessPendingUnlooks(all: true);
        for (int y = 0; y < cellsY; y++)
            for (int x = 0; x < cellsX; x++)
            {
                AddShrouder(player.Index, x, y);
                RemoveShrouder(player.Index, x, y);
            }
    }

    // ---- Objects ----

    /// <summary>How well a player can see an object (the cells under its footprint).</summary>
    public ObjectShroud StatusOf(GameObject obj, Player player)
    {
        float r = Math.Max(1, obj.BoundingCircleRadius);
        var (x0, y0) = WorldToCell(obj.Position.X - r, obj.Position.Y - r);
        var (x1, y1) = WorldToCell(obj.Position.X + r, obj.Position.Y + r);
        int shrouded = 0, fogged = 0, total = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                total++;
                switch (CellStatus(player.Index, x, y))
                {
                    case CellShroud.Shrouded: shrouded++; break;
                    case CellShroud.Fogged: fogged++; break;
                }
            }
        if (shrouded == total)
            return ObjectShroud.Shrouded;
        if (shrouded + fogged == total)
            return ObjectShroud.Fogged;
        return shrouded == 0 && fogged == 0 ? ObjectShroud.Clear : ObjectShroud.PartialClear;
    }

    /// <summary>Whether a player can see an object right now (some of it is in a clear cell).</summary>
    public bool IsVisibleTo(GameObject obj, Player player) => StatusOf(obj, player) is ObjectShroud.Clear or ObjectShroud.PartialClear;

    uint AlliesMask(Player owner)
    {
        uint mask = 0;
        foreach (var p in game.Players.All)
            if (p == owner || owner.IsAllyOf(p))
                mask |= Bit(p);
        return mask;
    }

    static uint Bit(Player p) => p.Index is >= 0 and < MaxPlayers ? 1u << p.Index : 0;

    uint SpiedMask(GameObject obj)
    {
        uint mask = 0;
        foreach (var (spy, kinds) in obj.Owner.VisionSpiedBy)
            if (kinds.Count > 0 && kinds.Any(k => k.Length == 0 || obj.IsAnyKindOf(k)) && spy is >= 0 and < MaxPlayers)
                mask |= 1u << spy;
        return mask;
    }

    /// <summary>
    /// Brings every object's look, reveal-to-all and shroud up to date (the original's
    /// handlePartitionCellMaintenance, run whenever any of its inputs changes), then lets old looks
    /// expire. Called once a frame.
    /// </summary>
    public void Update()
    {
        foreach (var obj in game.Objects)
            Maintain(obj);
        // Objects that are gone stop looking.
        if (vision.Count > game.Objects.Count)
        {
            var alive = game.Objects.Select(o => o.Id).ToHashSet();
            foreach (var id in vision.Keys.Where(id => !alive.Contains(id)).ToList())
            {
                Unlook(vision[id]);
                vision.Remove(id);
            }
        }
        ProcessPendingUnlooks();
    }

    /// <summary>A removed object stops looking at once (queued like any unlook).</summary>
    public void OnObjectRemoved(GameObject obj)
    {
        if (vision.Remove(obj.Id, out var v))
            Unlook(v);
    }

    void Unlook(ObjectVision v)
    {
        if (v.Look is { } look)
            QueueUndoShroudReveal(look);
        if (v.RevealAll is { } all)
            QueueUndoShroudReveal(all);
        if (v.Shroud is { } shroud)
            UndoShroudCover(shroud.Where, shroud.HowFar, shroud.Mask);
        v.Look = v.RevealAll = v.Shroud = null;
    }

    void Maintain(GameObject obj)
    {
        var (cx, cy) = WorldToCell(obj.Position.X, obj.Position.Y);
        bool alive = !obj.IsDestroyed && !obj.IsEffectivelyDead;

        float range = 0;
        uint mask = 0;
        // Inside a (non-garrison) container, a unit doesn't look.
        bool enclosed = obj.IsContained && game.FindObject(obj.ContainedById)?.Contain is { IsGarrisonable: false };
        if (alive && !enclosed && obj.ShroudClearingRange > 0)
        {
            range = obj.ShroudClearingRange;
            mask = obj.IsKindOf("REVEAL_TO_ALL") ? uint.MaxValue : AlliesMask(obj.Owner) | SpiedMask(obj);
        }

        float revealAll = 0;
        uint revealMask = 0;
        float revealRange = obj.Template.ShroudRevealToAllRange;
        if (alive && revealRange > 0 && !obj.IsUnderConstruction
            && !(obj.HasStatus(Status.Stealthed) && !obj.HasStatus(Status.Detected) && !obj.HasStatus("DISGUISED")))
        {
            revealAll = revealRange;
            foreach (var p in game.Players.All)
                if (p != obj.Owner && !obj.Owner.IsAllyOf(p))
                    revealMask |= Bit(p);
        }

        float shroud = 0;
        uint shroudMask = 0;
        if (alive && !obj.IsUnderConstruction && obj.Template.ShroudRange > 0)
        {
            shroud = obj.Template.ShroudRange;
            foreach (var p in game.Players.All)
                if (p != obj.Owner && !obj.Owner.IsAllyOf(p))
                    shroudMask |= Bit(p);
        }

        var key = (cx, cy, range, mask, revealAll, revealMask, shroud, shroudMask);
        if (!vision.TryGetValue(obj.Id, out var v))
            vision[obj.Id] = v = new ObjectVision { Key = (int.MinValue, 0, 0, 0, 0, 0, 0, 0) };
        if (v.Key == key)
            return;
        v.Key = key;

        Unlook(v);
        var where = new Vector2(obj.Position.X, obj.Position.Y);
        if (shroud > 0)
        {
            v.Shroud = new Sighting(where, shroud, shroudMask);
            DoShroudCover(where, shroud, shroudMask);
        }
        if (range > 0)
        {
            v.Look = new Sighting(where, range, mask);
            DoShroudReveal(where, range, mask);
        }
        if (revealAll > 0)
        {
            v.RevealAll = new Sighting(where, revealAll, revealMask);
            DoShroudReveal(where, revealAll, revealMask);
        }
    }
}

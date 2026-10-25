namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>PathType: a planned route — moves from Start, its cost, and the cells it covers.</summary>
public sealed class PathType
{
    public int Start;
    public int Cost;
    public int Length;
    public FacingType[] Command = [];
    public uint[] Overlap = [];
    public int LastOverlap;
    public int LastFixup;

    /// <summary>The original copies the struct (so the copy shares the Command and Overlap buffers).</summary>
    public void CopyFrom(PathType other)
    {
        Start = other.Start;
        Cost = other.Cost;
        Length = other.Length;
        Command = other.Command;
        Overlap = other.Overlap;
        LastOverlap = other.LastOverlap;
        LastFixup = other.LastFixup;
    }
}

/// <summary>The path finder's shared buffers (FINDPATH.CPP's file statics), one set per game.</summary>
public sealed class PathFinderState
{
    public readonly uint[] MainOverlap = new uint[TdDefines.MAP_CELL_TOTAL / 32];
    public readonly uint[] LeftOverlap = new uint[TdDefines.MAP_CELL_TOTAL / 32];
    public readonly uint[] RightOverlap = new uint[TdDefines.MAP_CELL_TOTAL / 32];
    public readonly PathType Path = new();
    public int DestLocation;
    public int StartLocation;
}

// FINDPATH.CPP: the original's "crash and turn" path finder. It walks straight at the goal; on
// hitting an obstacle it follows the obstacle's edge both ways and keeps the shorter route. The
// build defines DIAGONAL, so moves may be diagonal and Optimize_Moves smooths the result.
public abstract partial class FootClass
{
    const int MAX_MLIST_SIZE = 300;
    const int THREAT_THRESHOLD = 5;
    const int MAX_PATH_EDGE_FOLLOW = 400;
    const FacingType END = FacingType.FACING_NONE;
    const FacingType EMPTY = (FacingType)(-2);
    const int CLOCK = 1;
    const int COUNTERCLOCK = -1;

    static FacingType Opposite(FacingType face) => (FacingType)((int)face ^ 4);

    /// <summary>Next_Direction: with DIAGONAL there is no rounding to the four main directions.</summary>
    static FacingType Next_Direction(FacingType facing, int dir) => TdMath.FacingAdd(facing, dir);

    static FacingType Cell_Facing(int a, int b) => TdMath.DirFacingType(TdMath.CellDirection(a, b));

    static int Point_Relative_To_Line(int x, int z, int x1, int z1, int x2, int z2) =>
        (int)((((long)x - x2) * ((long)z1 - z2)) - (((long)z - z2) * ((long)x1 - x2)));

    // The overlap bit for a cell is (cell & 31) - 1: cell 0 of each word uses bit 31 (x86 masks the shift).
    static uint OverlapBit(int cell) => 1u << (((cell & 31) - 1) & 31);
    static void Overlap_Set(uint[] overlap, int cell) { if ((uint)(cell >> 5) < overlap.Length) overlap[cell >> 5] |= OverlapBit(cell); }
    static void Overlap_Clear(uint[] overlap, int cell) { if ((uint)(cell >> 5) < overlap.Length) overlap[cell >> 5] &= ~OverlapBit(cell); }
    static bool Overlap_Test(uint[] overlap, int cell) => (uint)(cell >> 5) < overlap.Length && (overlap[cell >> 5] & OverlapBit(cell)) != 0;

    /// <summary>Unravel_Loop: backs the path up to where it last crossed the start→goal line.</summary>
    bool Unravel_Loop(PathType path, ref int cell, ref FacingType dir, int sx, int sy, int dx, int dy, MoveType threshhold)
    {
        FacingType currDir = dir;
        int currPos = TdMath.AdjacentCellOf(cell, Opposite(currDir));
        int idx = path.Length;
        int list = idx - 1;
        bool lastWasLine = false;

        while (idx != 0)
        {
            int checkx = TdMath.CellX(currPos);
            int checky = TdMath.CellY(currPos);
            if (Point_Relative_To_Line(checkx, checky, sx, sy, dx, dy) == 0 || lastWasLine)
            {
                if (((int)currDir & 1) != 0 && currPos != path.LastFixup)
                {
                    cell = currPos;
                    dir = list - 1 >= 0 ? path.Command[list - 1] : END;
                    path.Length = idx;
                    path.LastFixup = currPos;
                    return true;
                }
                lastWasLine = !lastWasLine;
            }

            path.Cost -= Passable_Cell(currPos, path.Command[list], -1, threshhold);
            Overlap_Clear(path.Overlap, currPos);
            currDir = path.Command[list--];
            currPos = TdMath.AdjacentCellOf(currPos, Opposite(currDir));
            idx--;
        }
        return false;
    }

    /// <summary>Register_Cell: adds a step; a step back onto the path cuts the loop out.</summary>
    bool Register_Cell(PathType path, int cell, FacingType dir, int cost, MoveType threshhold)
    {
        if (Overlap_Test(path.Overlap, cell))
        {
            FacingType last = path.Length - 1 >= 0 ? path.Command[path.Length - 1] : EMPTY;
            if (last == Opposite(dir))
            {
                // Stepping straight back: drop the last step.
                int back = TdMath.AdjacentCellOf(cell, Opposite(dir));
                Overlap_Clear(path.Overlap, back);
                path.Length--;
            }
            else
            {
                if (path.LastOverlap == cell)
                    return false;
                path.LastOverlap = cell;

                // Find where the path first reached this cell and cut everything after it.
                int pos = path.Start;
                int newlen = 0;
                int idx = 0;
                int list = 0;
                if (pos != cell)
                {
                    while (idx < path.Length)
                    {
                        pos = TdMath.AdjacentCellOf(pos, path.Command[list]);
                        if (pos == cell)
                        {
                            idx++;
                            list++;
                            break;
                        }
                        idx++;
                        list++;
                    }
                    newlen = idx;
                }

                while (idx < path.Length)
                {
                    pos = TdMath.AdjacentCellOf(pos, path.Command[list]);
                    path.Cost -= Passable_Cell(pos, path.Command[list], -1, threshhold);
                    Overlap_Clear(path.Overlap, pos);
                    idx++;
                    list++;
                }
                path.Length = newlen;
            }
        }
        else
        {
            int cpos = path.Length++;
            path.Command[cpos] = dir;
            path.Cost += cost;
            Overlap_Set(path.Overlap, cell);
        }
        return true;
    }

    /// <summary>Find_Path: a path towards dest into finalMoves (at most maxlen entries). The result
    /// is the game's one shared PathType, as the original's static.</summary>
    protected PathType? Find_Path(int dest, FacingType[]? finalMoves, int maxlen, MoveType threshhold)
    {
        int source = TdMath.CoordCell(Coord);
        var st = Game.PathFinder;
        var path = st.Path;
        bool left = false, right = false;
        int unitThreat;
        var movesLeft = new FacingType[MAX_MLIST_SIZE + 2];
        var movesRight = new FacingType[MAX_MLIST_SIZE + 2];
        var pleft = new PathType();
        var pright = new PathType();
        int threat;
        int threatStage = 0;

        if (finalMoves == null)
            return null;

        // Round-about teams avoid dangerous regions, relaxing the limit step by step.
        if (Team != null && Team.Class.IsRoundAbout)
        {
            unitThreat = Team.Risk;
            threatStage = 0;
            threat = 0;
        }
        else
        {
            unitThreat = threat = -1;
        }

        st.StartLocation = source;
        st.DestLocation = dest;

        path.Start = source;
        path.Cost = 0;
        path.Length = 0;
        path.Command = finalMoves;
        path.Command[0] = END;
        path.Overlap = st.MainOverlap;
        path.LastOverlap = -1;
        path.LastFixup = -1;
        Array.Clear(path.Overlap);
        Overlap_Set(path.Overlap, source);

        int startcell = source;
        int next;
        maxlen--;

        bool restart = false;
        for (;;)
        {
            // goto top_of_list re-enters here without the length check.
            if (!restart && path.Length >= maxlen)
                break;
            restart = false;
            {
                if (startcell == dest)
                    break;

                FacingType direction = Cell_Facing(startcell, dest);
                next = TdMath.AdjacentCellOf(startcell, direction);

                int cost = Passable_Cell(next, direction, threat, threshhold);
                if (cost != 0)
                {
                    Register_Cell(path, next, direction, cost, threshhold);
                }
                else
                {
                    if (next == dest)
                        break;

                    bool endOfList = false;
                    for (int limiter = 0; limiter < 5; limiter++)
                    {
                        // Walk through the obstacle to the first free cell beyond it.
                        for (;;)
                        {
                            FacingType newdir = Cell_Facing(next, dest);
                            next = TdMath.AdjacentCellOf(next, newdir);
                            if (Passable_Cell(next, FacingType.FACING_NONE, threat, threshhold) != 0 || next == dest)
                                break;
                            if (next == dest)
                            {
                                if (Raise_Threat(ref threat, ref threatStage, unitThreat))
                                    restart = true;
                                else
                                    endOfList = true;
                                break;
                            }
                        }
                        if (restart || endOfList)
                            break;

                        // Follow the obstacle's edge both ways.
                        pleft.CopyFrom(path);
                        pleft.Command = movesLeft;
                        pleft.Overlap = st.LeftOverlap;
                        Array.Copy(path.Command, pleft.Command, path.Length);
                        Array.Copy(path.Overlap, pleft.Overlap, st.LeftOverlap.Length);
                        left = Follow_Edge(startcell, next, pleft, COUNTERCLOCK, direction, threat, threatStage, MAX_MLIST_SIZE, threshhold);

                        pright.CopyFrom(path);
                        pright.Command = movesRight;
                        pright.Overlap = st.RightOverlap;
                        Array.Copy(path.Command, pright.Command, path.Length);
                        Array.Copy(path.Overlap, pright.Overlap, st.RightOverlap.Length);
                        right = Follow_Edge(startcell, next, pright, CLOCK, direction, threat, threatStage, MAX_MLIST_SIZE, threshhold);

                        if (left || right)
                            break;

                        // Neither edge reached it: the free cell is inside the obstacle; go further.
                        FacingType nd;
                        do
                        {
                            if (next == dest)
                            {
                                if (Raise_Threat(ref threat, ref threatStage, unitThreat))
                                    restart = true;
                                else
                                    endOfList = true;
                                break;
                            }
                            nd = Cell_Facing(next, dest);
                            next = TdMath.AdjacentCellOf(next, nd);
                        } while (Passable_Cell(next, nd, threat, threshhold) != 0);
                        if (restart || endOfList)
                            break;
                    }
                    if (restart)
                        continue;
                    if (endOfList)
                        goto end_of_list;

                    if (!left && !right)
                        break;

                    PathType which = pleft;
                    if (right)
                    {
                        which = pright;
                        if (left)
                            which = pleft.Length < pright.Length ? pleft : pright;
                    }

                    int len = Math.Min(which.Length, maxlen);
                    if (len > 0)
                    {
                        Array.Copy(which.Overlap, path.Overlap, st.LeftOverlap.Length);
                        Array.Copy(which.Command, path.Command, len);
                        path.Length = len;
                        path.Cost = which.Cost;
                        path.LastOverlap = -1;
                        path.LastFixup = -1;
                    }
                    else
                    {
                        break;
                    }
                }
                startcell = next;
            }
        }

    end_of_list:
        if (path.Length < maxlen)
            path.Command[path.Length++] = END;

        Optimize_Moves(path, threshhold);
        return path;
    }

    /// <summary>The goto top_of_list case: when avoiding threats, relax the threat limit and start
    /// over (false once the limit is off).</summary>
    static bool Raise_Threat(ref int threat, ref int threatStage, int unitThreat)
    {
        if (threat == -1)
            return false;
        switch (threatStage++)
        {
            case 0:
                threat = unitThreat >> 1;
                break;
            case 1:
                threat += unitThreat;
                break;
            case 2:
                threat = -1;
                break;
        }
        return true;
    }

    /// <summary>Follow_Edge: hugs an obstacle (turning one way) until it reaches the target cell.</summary>
    bool Follow_Edge(int start, int target, PathType? path, int search, FacingType olddir, int threat, int threatStage, int maxCells, MoveType threshhold)
    {
        int cost = 0;
        bool online = true;
        int oldval = 0;
        int cellcount = 0;
        bool forceout = false;
        FacingType firstdir = (FacingType)(-1);
        int firstcell = -1;

        int startx = TdMath.CellX(start);
        int starty = TdMath.CellY(start);
        int targetx = TdMath.CellX(target);
        int targety = TdMath.CellY(target);

        if (path == null)
            return false;
        path.LastOverlap = -1;
        path.LastFixup = -1;

        FacingType newdir;
        int oldcell = start;
        int newcell;

        while (path.Length < maxCells)
        {
            newdir = olddir;
            for (;;)
            {
                bool forcefail = false;

                // Rotate 45 degrees the search way.
                newdir = Next_Direction(newdir, search);
                if (((int)newdir & (int)FacingType.FACING_NE) != 0)
                {
                    // A diagonal: if the next square round is the target, step straight onto it.
                    int checkcell = TdMath.AdjacentCellOf(oldcell, Next_Direction(newdir, search));
                    if (checkcell == target)
                    {
                        cost = Passable_Cell(checkcell, Next_Direction(newdir, search), threat, threshhold);
                        if (cost != 0)
                        {
                            newdir = Next_Direction(newdir, search);
                            newcell = TdMath.AdjacentCellOf(oldcell, newdir);
                            break;
                        }
                    }

                    // Don't cut diagonally across the start-to-target line (unless backing up).
                    checkcell = TdMath.AdjacentCellOf(oldcell, newdir);
                    int checkval = Point_Relative_To_Line(TdMath.CellX(checkcell), TdMath.CellY(checkcell), startx, starty, targetx, targety);
                    forcefail = checkval != 0 && !online && (checkval ^ oldval) < 0;
                    if (forcefail && path.Length > 0 && (FacingType)((int)newdir ^ 4) == path.Command[path.Length - 1])
                        forcefail = false;
                }

                if (newdir == olddir)
                    return false;

                newcell = TdMath.AdjacentCellOf(oldcell, newdir);
                if (!forcefail && (cost = Passable_Cell(newcell, newdir, threat, threshhold)) != 0)
                    break;
                if (newcell == target)
                {
                    forceout = true;
                    break;
                }
            }

            if (!forceout)
            {
                if (!Register_Cell(path, newcell, newdir, cost, threshhold))
                {
                    if (!Unravel_Loop(path, ref newcell, ref newdir, startx, starty, targetx, targety, threshhold))
                        return false;
                    newdir = Next_Direction(newdir, search * 2);
                }

                int val = Point_Relative_To_Line(TdMath.CellX(newcell), TdMath.CellY(newcell), startx, starty, targetx, targety);
                if (val != 0)
                {
                    oldval = val;
                    online = false;
                }
                else
                {
                    online = true;
                }

                cellcount++;
                if (cellcount == MAX_PATH_EDGE_FOLLOW)
                    return false;
            }

            if (newcell == target)
            {
                path.Command[path.Length] = END;
                return true;
            }

            if (newcell == firstcell && newdir == firstdir)
                return false;

            if (firstcell == -1)
            {
                firstcell = newcell;
                firstdir = newdir;
            }

            olddir = Next_Direction(newdir, -search * 3);
            oldcell = newcell;
        }
        return false;
    }

    // With DIAGONAL: how each turn between two consecutive moves is smoothed.
    static readonly int[] Trans = [0, 0, 1, 2, 3, -2, -1, 0];

    /// <summary>Optimize_Moves: removes back-and-forth steps and cuts corners where possible.</summary>
    public int Optimize_Moves(PathType? path, MoveType threshhold)
    {
        if (path == null || path.Command.Length == 0)
            return 0;
        var cmd = path.Command;
        cmd[path.Length] = END;
        int cell = path.Start;

        if (path.Length > 1)
        {
            int cmd2 = 1;
            while (cmd[cmd2] != END)
            {
                int cmd1 = cmd2 - 1;
                while (cmd[cmd1] == EMPTY && cmd1 != 0)
                    cmd1--;
                if (cmd[cmd1] == EMPTY)
                {
                    cmd2++;
                    continue;
                }

                int newcmd = Trans[((int)cmd[cmd2] - (int)cmd[cmd1]) & 0x07];

                // A reversal cancels both moves.
                if (newcmd == (int)FacingType.FACING_SE)
                {
                    cmd[cmd1] = EMPTY;
                    cmd[cmd2++] = EMPTY;
                    continue;
                }

                if (newcmd != 0)
                {
                    FacingType newdir;
                    if (((int)cmd[cmd1] & (int)FacingType.FACING_NE) != 0)
                    {
                        newdir = Next_Direction(cmd[cmd1], newcmd < 0 ? -1 : 1);
                        if (Math.Abs(newcmd) == 1)
                        {
                            if (Passable_Cell(TdMath.AdjacentCellOf(cell, newdir), newdir, -1, threshhold) != 0)
                            {
                                cmd[cmd2] = newdir;
                                cmd[cmd1] = newdir;
                            }
                            cell = TdMath.AdjacentCellOf(cell, cmd[cmd1]);
                            cmd2++;
                            continue;
                        }
                    }
                    else
                    {
                        newdir = Next_Direction(cmd[cmd1], newcmd);
                    }

                    // Replace the two moves with the one combined move.
                    cmd[cmd2] = newdir;
                    cmd[cmd1] = EMPTY;

                    while (cmd[cmd1] == EMPTY && cmd1 != 0)
                        cmd1--;
                    if (cmd[cmd1] != EMPTY)
                        cell = TdMath.AdjacentCellOf(cell, Next_Direction(cmd[cmd1], (int)FacingType.FACING_S));
                    else
                        cell = path.Start;
                    continue;
                }

                cell = TdMath.AdjacentCellOf(cell, cmd[cmd1]);
                cmd2++;
            }
        }

        // Pack the list (dropping the removed moves) and recount the cost.
        int c1 = 0, c2 = 0;
        cell = path.Start;
        path.Cost = 0;
        path.Length = 0;
        while (cmd[c2] != END)
        {
            if (cmd[c2] != EMPTY)
            {
                cell = TdMath.AdjacentCellOf(cell, cmd[c2]);
                path.Cost += Passable_Cell(cell, cmd[c2], -1, threshhold);
                path.Length++;
                cmd[c1++] = cmd[c2];
            }
            c2++;
        }
        path.Length++;
        cmd[c1] = END;
        return path.Length;
    }

    /// <summary>Safety_Point: the first cell this object can enter on growing rings past dst (away from src).</summary>
    public int Safety_Point(int src, int dst, int start, int max)
    {
        FacingType dir = TdMath.FacingSub((FacingType)((int)Cell_Facing(src, dst) ^ 4), 1);

        for (int dist = start; dist < max; dist++)
        {
            int next = dst;
            for (int lp = 0; lp < dist; lp++)
                next = TdMath.AdjacentCellOf(next, dir);

            if (((int)dir & 1) != 0)
            {
                for (int lp = 0; lp < dist << 1; lp++)
                {
                    next = TdMath.AdjacentCellOf(next, TdMath.FacingAdd(dir, 3));
                    if (Can_Enter_Cell(next) == MoveType.MOVE_OK)
                        return next;
                }
            }
            else
            {
                for (int lp = 0; lp < dist; lp++)
                {
                    next = TdMath.AdjacentCellOf(next, TdMath.FacingAdd(dir, 2));
                    if (Can_Enter_Cell(next) == MoveType.MOVE_OK)
                        return next;
                }
                for (int lp = 0; lp < dist; lp++)
                {
                    next = TdMath.AdjacentCellOf(next, TdMath.FacingAdd(dir, 4));
                    if (Can_Enter_Cell(next) == MoveType.MOVE_OK)
                        return next;
                }
            }
        }
        return -1;
    }

    static readonly int[] MoveValue = [1, 1, 3, 8, 10, 0];

    /// <summary>Passable_Cell: the cost of stepping into a cell (0 = can't), including threat avoidance.</summary>
    int Passable_Cell(int cell, FacingType face, int threat, MoveType threshhold)
    {
        MoveType move = Can_Enter_Cell(cell, face);

        // Units that will have moved by the time we get there don't block distant cells.
        if (move < MoveType.MOVE_MOVING_BLOCK && DistanceToCell(cell) > 1)
            threshhold = MoveType.MOVE_MOVING_BLOCK;

        if (move > threshhold)
            return 0;

        if (Game.GameToPlay == GameType.GAME_NORMAL && threat != -1)
        {
            if (Game.Map.Cell_Distance(cell, Game.PathFinder.DestLocation) > THREAT_THRESHOLD
                && Game.Map.Cell_Threat(cell, Owner()) > threat)
                return 0;
        }
        return MoveValue[(int)move];
    }
}

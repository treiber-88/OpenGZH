using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// AnimClass (ANIM.CPP): an animation on the map — explosions, fire, smoke, muzzle flashes, the
/// nuke and ion cannon blasts. Some do damage (burning), leave smudges, start fires, or chain into
/// another animation; some ride on an object.
/// </summary>
public sealed class AnimClass : ObjectClass
{
    AnimTypeClass @class;
    readonly StageClass stage = new();
    public ObjectClass? Object;
    public uint SortTarget = Target.TARGET_NONE;
    public HousesType OwnerHouse = HousesType.HOUSE_NONE;
    public int Loops;
    bool isToDelete, isBrandNew, isAlternate, isInvisible;
    uint visibleFlags;
    byte delay;
    byte accum;
    AnimClass? virtualAnim;
    ulong killTime;

    public AnimTypeClass Class => @class;

    // operator new runs before the constructor: the slot is taken and the object active first,
    // since the constructor puts the animation on the map.
    AnimClass(TdGame game, int slot, AnimType animnum, uint coord, byte timedelay, int loop, bool alt) : base(game)
    {
        game.Anims.Place(slot, this);
        Set_Active();

        @class = game.Types.Anim(animnum);
        Object = null;
        SortTarget = Target.TARGET_NONE;
        OwnerHouse = HousesType.HOUSE_NONE;
        killTime = 0;

        // Frame counts not given are taken from the art.
        if (@class.Stages == -1)
            @class.Stages = @class.ImageFrames;
        if (@class.LoopEnd == -1)
            @class.LoopEnd = @class.Stages;

        stage.Set_Rate(@class.IsNormalized ? game.Options.Normalize_Delay(@class.Delay) : @class.Delay);
        stage.Set_Stage(0);
        accum = 0;

        coord = Adjust_Coord(coord);
        Unlimbo(coord);

        visibleFlags = uint.MaxValue;

        // The landing-zone smoke reveals the area round it.
        if (@class.Type == AnimType.ANIM_LZ_SMOKE)
            game.Map.Sight_From(game.PlayerPtr, TdMath.CoordCell(coord), 4, false);

        delay = timedelay;
        if (@class.Loops >= 0)
        {
            Loops = unchecked((sbyte)(Math.Max((sbyte)loop, (sbyte)1) * @class.Loops));
            Loops = Math.Max((sbyte)Loops, (sbyte)1);
        }
        else
        {
            Loops = @class.Loops;
        }

        isToDelete = false;
        isBrandNew = true;
        isAlternate = alt;
        isInvisible = false;

        if (delay == 0)
            Start();

        // GlyphX's separate high-resolution version of the animation (kept invisible here).
        if (@class.VirtualAnim != AnimType.ANIM_NONE)
        {
            virtualAnim = Create(game, @class.VirtualAnim, Coord, timedelay, loop, alt);
            virtualAnim?.Make_Invisible();
        }
        else
        {
            virtualAnim = null;
        }
    }

    /// <summary>new AnimClass(type, coord, delay, loop, alt): null when the animation heap is full.</summary>
    public static AnimClass? Create(TdGame game, AnimType animnum, uint coord, int timedelay = 0, int loop = 1, bool alt = false)
    {
        int slot = game.Anims.Allocate();
        if (slot < 0)
            return null;
        return new AnimClass(game, slot, animnum, coord, (byte)timedelay, loop, alt);
    }

    /// <summary>~AnimClass: leaves the object it rode on (which stops burning if no other fire is on it).</summary>
    protected override void Destruct()
    {
        if (Game.GameActive)
        {
            if (Object != null)
            {
                Game.Map.Remove(this, In_Which_Layer());
                int index;
                for (index = 0; index < Game.Anims.Count; index++)
                {
                    var other = Game.Anims.Ptr(index);
                    if (other != this && other.Object == Object)
                        break;
                }
                if (index == Game.Anims.Count)
                {
                    Object.Fire_Out();
                    if (Object.In_Which_Layer() == LayerType.LAYER_GROUND)
                        Object.Mark(MarkType.MARK_OVERLAP_UP);
                    Object.IsAnimAttached = false;
                    if (Object.In_Which_Layer() == LayerType.LAYER_GROUND)
                        Object.Mark(MarkType.MARK_OVERLAP_DOWN);
                }
                Coord = TdMath.CoordAdd(Object.Center_Coord(), Coord);
                Object = null;
            }
            Limbo();
        }
        base.Destruct();
    }

    protected override void Release()
    {
        IsActive = false;
        Game.Anims.Free(this);
    }

    public override RTTIType What_Am_I() => RTTIType.RTTI_ANIM;
    public override HousesType Owner() => OwnerHouse;
    public override ObjectTypeClass Class_Of() => @class;
    public override uint As_Target() => Target.Build_Target(KindType.KIND_ANIMATION, Game.Anims.ID(this));

    public void Make_Invisible() => isInvisible = true;
    public void Make_Visible() => isInvisible = false;
    public bool IsInvisible => isInvisible;
    public void Kill_At(ulong killTimeFileTime) => killTime = killTimeFileTime;
    public int Fetch_Stage() => stage.Fetch_Stage();
    public byte Delay => delay;
    public bool IsAlternate => isAlternate;
    public AnimClass? VirtualAnim => virtualAnim;

    public void Set_Visible_Flags(uint flags)
    {
        visibleFlags = flags;
        virtualAnim?.Set_Visible_Flags(flags);
    }

    public uint Get_Visible_Flags() => delay == 0 ? visibleFlags : 0;

    public void Set_Owner(HousesType owner)
    {
        OwnerHouse = owner;
        virtualAnim?.Set_Owner(owner);
    }

    /// <summary>Shorten_Attached_Anims: fires on an object end after their current loop.</summary>
    public static void Shorten_Attached_Anims(TdGame game, ObjectClass? obj)
    {
        if (obj == null)
            return;
        for (int index = 0; index < game.Anims.Count; index++)
        {
            var anim = game.Anims.Ptr(index);
            if (anim.Object == obj)
                anim.Loops = 0;
        }
    }

    public override uint Sort_Y()
    {
        if (Object != null)
            return TdMath.CoordAdd(Object.Sort_Y(), 0x00010000);

        if (Game.Target_Legal(SortTarget))
        {
            var obj = Game.As_Object(SortTarget);
            if (obj != null && obj.IsActive)
                return TdMath.CoordAdd(obj.Sort_Y(), 0x00010000);
        }

        if (@class.Type == AnimType.ANIM_MOVE_FLASH)
            return TdMath.CoordAdd(Center_Coord(), TdMath.XYP_COORD(0, -24));
        if (@class.Type == AnimType.ANIM_LZ_SMOKE)
            return TdMath.CoordAdd(Center_Coord(), TdMath.XYP_COORD(0, 14));
        return Coord;
    }

    /// <summary>Center_Coord: an attached animation's Coord is an offset from its object.</summary>
    public override uint Center_Coord() => Object != null ? TdMath.CoordAdd(Coord, Object.Center_Coord()) : Coord;

    /// <summary>The frame to draw (Draw_It), or -1 if not drawn (past its last stage on the normal map).</summary>
    public int Shape_Number(bool virtualWindow) =>
        virtualWindow || stage.Fetch_Stage() < @class.Stages ? @class.Start + stage.Fetch_Stage() : -1;

    public override bool Mark(MarkType mark = MarkType.MARK_CHANGE)
    {
        if (base.Mark(mark))
        {
            Game.Map.Refresh_Cells(TdMath.CoordCell(Center_Coord()), Overlap_List());
            return true;
        }
        return false;
    }

    const int W = TdMath.MapCellW;
    static readonly short[] OverlapN = [0, -W, -(W + 1), -(W - 1), -(2 * W), -(2 * W - 1), -(2 * W + 1), TdMath.RefreshEol];
    static readonly short[] OverlapNW = [0, -1, -W, -(W + 1), -(W + 2), -(W * 2 + 2), -(W * 2 + 1), TdMath.RefreshEol];
    static readonly short[] OverlapW = [0, -1, -2, -(W + 1), -(W + 2), TdMath.RefreshEol];
    static readonly short[] OverlapSW = [0, -1, W, W - 1, W - 2, W * 2 - 2, W * 2 - 1, TdMath.RefreshEol];
    static readonly short[] OverlapS = [0, W - 1, W, W + 1, 2 * W + 1, 2 * W, 2 * W - 1, TdMath.RefreshEol];
    static readonly short[] OverlapSE = [0, 1, W, W + 1, W + 2, W * 2 + 2, W * 2 + 1, TdMath.RefreshEol];
    static readonly short[] OverlapE = [0, 1, 2, -(W - 1), -(W - 2), TdMath.RefreshEol];
    static readonly short[] OverlapNE = [0, 1, -W, -(W - 1), -(W - 2), -(W * 2 - 2), -(W * 2 - 1), TdMath.RefreshEol];
    static readonly short[] OverlapIon = BuildColumn(-7, 0);
    static readonly short[] OverlapAtom = BuildColumn(-2, 2);
    static readonly short[] OverlapFlag = [0, 1, -W, -(W - 1), W, W + 1, TdMath.RefreshEol];

    static short[] BuildColumn(int fromRow, int toRow)
    {
        var list = new List<short>();
        for (int row = fromRow; row <= toRow; row++)
        {
            list.Add((short)(W * row - 1));
            list.Add((short)(W * row));
            list.Add((short)(W * row + 1));
        }
        // The ion cannon's list runs from row -7 down to 0, as written.
        list.Add(TdMath.RefreshEol);
        return [.. list];
    }

    public override short[] Overlap_List() => @class.Type switch
    {
        AnimType.ANIM_CHEM_N or AnimType.ANIM_FLAME_N => OverlapN,
        AnimType.ANIM_CHEM_NW or AnimType.ANIM_FLAME_NW => OverlapNW,
        AnimType.ANIM_CHEM_W or AnimType.ANIM_FLAME_W => OverlapW,
        AnimType.ANIM_CHEM_SW or AnimType.ANIM_FLAME_SW => OverlapSW,
        AnimType.ANIM_CHEM_S or AnimType.ANIM_FLAME_S => OverlapS,
        AnimType.ANIM_CHEM_SE or AnimType.ANIM_FLAME_SE => OverlapSE,
        AnimType.ANIM_CHEM_E or AnimType.ANIM_FLAME_E => OverlapE,
        AnimType.ANIM_CHEM_NE or AnimType.ANIM_FLAME_NE => OverlapNE,
        AnimType.ANIM_ION_CANNON => OverlapIon,
        AnimType.ANIM_ATOM_BLAST => OverlapAtom,
        AnimType.ANIM_FLAG => OverlapFlag,
        _ => TdMath.Coord_Spillage_List(Center_Coord(), @class.Size),
    };

    static readonly short[] Simple = [TdMath.RefreshEol];

    public override short[] Occupy_List(bool placement = false) => Simple;

    public override void AI()
    {
        if (delay == 0 && @class.IsGroundLayer)
            Game.Map.Refresh_Cells(TdMath.CoordCell(Center_Coord()), Overlap_List());

        // The landing-zone smoke goes when a building is put there.
        if (@class.Type == AnimType.ANIM_LZ_SMOKE && Game.Map[TdMath.CoordCell(Center_Coord())].Cell_Building() != null)
            isToDelete = true;

        // A timed (real-time) removal, for GlyphX beacons.
        if (killTime > 0 && (ulong)DateTime.UtcNow.ToFileTimeUtc() >= killTime)
            isToDelete = true;

        if (isToDelete)
        {
            Delete_This();
            return;
        }

        if (isBrandNew)
        {
            isBrandNew = false;
            return;
        }

        if (@class.Stages == -1)
            @class.Stages = @class.ImageFrames;
        if (@class.LoopEnd == -1)
            @class.LoopEnd = @class.Stages;

        if (delay != 0)
        {
            delay--;
            if (delay == 0)
                Start();
            return;
        }

        Mark(MarkType.MARK_CHANGE);
        if (stage.Graphic_Logic())
        {
            int st = stage.Fetch_Stage();

            // Fires attached to an object burn it.
            if (Object != null && @class.Damage != 0 && st < @class.Stages)
            {
                uint acc = accum;
                acc += (uint)@class.Damage;
                if (acc > 255)
                {
                    int damage = (int)(acc >> 8);
                    if (Object.Take_Damage(ref damage, 0, WarheadType.WARHEAD_FIRE) == ResultType.RESULT_DESTROYED)
                    {
                        virtualAnim?.Delete_This();
                        Delete_This();
                        return;
                    }
                }
                accum = (byte)(acc & 0x00FF);
            }

            if (@class.Start + st == @class.Biggest)
                Middle();

            if ((Loops <= 1 && st >= @class.Stages) || (Loops > 1 && st >= @class.LoopEnd - @class.Start))
            {
                if (Loops > 0)
                    Loops--;
                if (Loops != 0)
                {
                    stage.Set_Stage(@class.LoopStart);
                }
                else if (@class.VirtualAnim != AnimType.ANIM_NONE)
                {
                    Make_Invisible();
                    if (virtualAnim == null)
                    {
                        if (@class.ChainTo != AnimType.ANIM_NONE)
                            Chain();
                        else
                            Delete_This();
                    }
                }
                else if (@class.VirtualStages < 0 || st >= @class.VirtualStages)
                {
                    if (@class.ChainTo != AnimType.ANIM_NONE)
                        Chain();
                    else
                        Delete_This();
                }
            }
        }
    }

    /// <summary>Attach_To: the animation rides on an object (its Coord becomes an offset).</summary>
    public void Attach_To(ObjectClass? obj)
    {
        if (obj == null)
            return;
        if (obj.In_Which_Layer() == LayerType.LAYER_GROUND)
            obj.Mark(MarkType.MARK_OVERLAP_UP);
        obj.IsAnimAttached = true;
        if (obj.In_Which_Layer() == LayerType.LAYER_GROUND)
            obj.Mark(MarkType.MARK_OVERLAP_DOWN);
        Game.Map.Remove(this, In_Which_Layer());
        Object = obj;
        Game.Map.Submit(this, In_Which_Layer());
        Coord = TdMath.CoordSub(Coord, obj.Center_Coord());
    }

    public void Sort_Above(uint target) => SortTarget = target;

    public override LayerType In_Which_Layer() => Object != null || @class.IsGroundLayer ? LayerType.LAYER_GROUND : LayerType.LAYER_AIR;

    /// <summary>Start: the sound, the middle effect for one-frame anims, and sticking to a gunboat.</summary>
    void Start()
    {
        int cell = TdMath.CoordCell(Coord);
        Mark();
        Game.Sound_Effect(@class.Sound, Coord);

        if (@class.Biggest == 0)
            Middle();

        if (Object == null && @class.IsSticky && Game.Map.In_Radar(cell))
        {
            var unit = Game.Map[cell].Cell_Unit();
            if (unit != null && unit.Class.Type == UnitType.UNIT_GUNBOAT)
                Attach_To(unit);
        }
    }

    /// <summary>Middle: the effect at the animation's biggest frame — the nuke's blast, smudges,
    /// craters, flamethrower fires, the ion cannon's damage, napalm fires.</summary>
    void Middle()
    {
        int cell = TdMath.CoordCell(Center_Coord());
        var cellptr = Game.Map[cell];

        if (@class.Type == AnimType.ANIM_ATOM_BLAST)
        {
            // The damage counts as the Temple's (or any of the owner's objects).
            BuildingClass? building = null;
            TechnoClass? backup = null;
            if (OwnerHouse != HousesType.HOUSE_NONE)
            {
                for (int index = 0; index < Game.Logic.Count; index++)
                {
                    var obj = Game.Logic[index];
                    if (obj != null && obj.Is_Techno() && obj.Owner() == OwnerHouse)
                    {
                        backup = (TechnoClass)obj;
                        if (obj is BuildingClass { Class.Type: StructType.STRUCT_TEMPLE } temple)
                        {
                            building = temple;
                            break;
                        }
                    }
                }
                // As the original: the fallback is cast to a building whatever it is.
                building ??= backup as BuildingClass;
                TechnoClass? source = building ?? backup;
                Atom_Blast(cell, source);
            }
            else
            {
                Atom_Blast(cell, null);
            }
        }

        if (@class.IsScorcher)
            SmudgeClass.Create(Game, (SmudgeType)Game.Random_Pick((int)SmudgeType.SMUDGE_SCORCH1, (int)SmudgeType.SMUDGE_SCORCH6), Center_Coord());

        if (@class.IsCraterForming)
        {
            cellptr.Reduce_Tiberium(6);
            SmudgeClass.Create(Game, SmudgeType.SMUDGE_CRATER1, Center_Coord());
        }

        // Flamethrower bursts set fires ahead.
        if (@class.IsFlameThrower)
        {
            byte dir = (byte)(((int)@class.Type - (int)AnimType.ANIM_FLAME_N) << 5);
            uint c2 = TdMath.CoordMove(Center_Coord(), dir, 0x00E0);
            uint c3 = Game.Map.Closest_Free_Spot(TdMath.CoordMove(Center_Coord(), dir, 0x0140), true);
            c2 = Game.Map.Closest_Free_Spot(c2, true);

            if (c3 != 0 && Game.Random_Pick(0, 1) == 1 && Game.Map[TdMath.CoordCell(c3)].Cell_Terrain() == null)
                Create(Game, AnimType.ANIM_FIRE_SMALL, c3, 0, 2);
            if (c2 != 0 && Game.Random_Pick(0, 1) == 1 && Game.Map[TdMath.CoordCell(c2)].Cell_Terrain() == null)
                Create(Game, AnimType.ANIM_FIRE_SMALL, c2, 0, 2);
            SmudgeClass.Create(Game, SmudgeType.SMUDGE_SCORCH1, c2);
            if (c3 != 0 && Game.Random_Pick(0, 1) == 1 && Game.Map[TdMath.CoordCell(c3)].Cell_Terrain() == null)
                Create(Game, AnimType.ANIM_SMOKE_M, c3);
        }

        switch (@class.Type)
        {
            case AnimType.ANIM_ION_CANNON:
            {
                // The damage counts as the Advanced Comm. Center's (or any of the owner's objects).
                BuildingClass? building = null;
                TechnoClass? backup = null;
                if (OwnerHouse != HousesType.HOUSE_NONE)
                {
                    for (int index = 0; index < Game.Logic.Count; index++)
                    {
                        var obj = Game.Logic[index];
                        if (obj != null && obj.Is_Techno() && obj.Owner() == OwnerHouse && !obj.IsInLimbo)
                        {
                            backup = (TechnoClass)obj;
                            if (obj is BuildingClass { Class.Type: StructType.STRUCT_EYE } eye)
                            {
                                building = eye;
                                break;
                            }
                        }
                    }
                }
                Game.Explosion_Damage(Center_Coord(), 600, (TechnoClass?)building ?? backup, WarheadType.WARHEAD_PB);
                break;
            }

            case AnimType.ANIM_NAPALM1:
            case AnimType.ANIM_NAPALM2:
            case AnimType.ANIM_NAPALM3:
                Napalm_Fire(AnimType.ANIM_FIRE_SMALL, 0x0040);
                if (Game.Random_Pick(0, 1) == 1)
                    Napalm_Fire(AnimType.ANIM_FIRE_SMALL, 0x00A0);
                if (Game.Random_Pick(0, 1) == 1)
                    Napalm_Fire(AnimType.ANIM_FIRE_MED, 0x0070);
                break;

            case AnimType.ANIM_FIRE_MED:
            case AnimType.ANIM_FIRE_MED2:
            {
                int loops = Game.Random_Pick(0, 1) == 1 ? 1 : 2;
                var newanim = Create(Game, AnimType.ANIM_FIRE_SMALL, Center_Coord(), 0, loops);
                if (newanim != null && Object != null)
                    newanim.Attach_To(Object);
                break;
            }
        }
    }

    /// <summary>new AnimClass(type, Map.Closest_Free_Spot(Coord_Scatter(...)), 0, random 1 or 2),
    /// with the arguments evaluated right to left as the original's compiler does.</summary>
    void Napalm_Fire(AnimType type, int scatter)
    {
        int loops = Game.Random_Pick(0, 1) == 1 ? 1 : 2;
        uint coord = Game.Map.Closest_Free_Spot(Game.Coord_Scatter(Center_Coord(), scatter), true);
        Create(Game, type, coord, 0, loops);
    }

    /// <summary>The nuke's blast: heavy fire damage and scorch marks over a square round the cell.</summary>
    void Atom_Blast(int cell, TechnoClass? source)
    {
        int radius = 3;
        int rawdamage = 200;
        if (Game.GameToPlay == GameType.GAME_NORMAL)
        {
            radius = 4;
            rawdamage = 1000;
            Game.Fade_Palette_To_White(30);
        }

        for (int x = -radius; x <= radius; x++)
        {
            for (int y = -radius; y <= radius; y++)
            {
                int xpos = TdMath.CellX(cell) + x;
                int ypos = TdMath.CellY(cell) + y;
                if ((uint)xpos > TdMath.MapCellW)
                    continue;
                if ((uint)ypos > TdMath.MapCellH)
                    continue;
                int tcell = TdMath.XyCell(xpos, ypos);
                if (!Game.Map.In_Radar(tcell))
                    continue;
                // As the original: the falloff uses the radius, so every cell takes the same damage.
                int damage = rawdamage / ((Math.Abs(radius) / 2) + 1);
                Game.Explosion_Damage(TdMath.CellCoord(tcell), damage, source, WarheadType.WARHEAD_FIRE);
                SmudgeClass.Create(Game, (SmudgeType)Game.Random_Pick((int)SmudgeType.SMUDGE_SCORCH1, (int)SmudgeType.SMUDGE_SCORCH6), TdMath.CellCoord(tcell));
            }
        }

        Game.Shake_The_Screen(3);
        if (Game.GameToPlay == GameType.GAME_NORMAL)
            Game.Fade_Palette_To_Game(15);
    }

    /// <summary>Chain: become the next animation in the sequence.</summary>
    void Chain()
    {
        if (@class.ChainTo != AnimType.ANIM_NONE)
        {
            @class = Game.Types.Anim(@class.ChainTo);
            stage.Set_Rate(@class.IsNormalized ? Game.Options.Normalize_Delay(@class.Delay) : @class.Delay);
            stage.Set_Stage(@class.Start);
        }
    }

    /// <summary>Adjust_Coord: the Temple's door animation sits above the Temple's corner.</summary>
    uint Adjust_Coord(uint coord)
    {
        if (@class.Type == AnimType.ANIM_ATOM_DOOR)
            return TdMath.CoordAdd(coord, TdMath.XYPixel_Coord(-1, -36));
        return coord;
    }

    public override void Detach(uint target, bool all)
    {
        if (all)
        {
            if (virtualAnim != null && virtualAnim.As_Target() == target)
            {
                virtualAnim = null;
                if (isInvisible)
                    isToDelete = true;
            }
            if (Object != null && Object.As_Target() == target)
            {
                Game.Map.Remove(this, In_Which_Layer());
                Object = null;
                isToDelete = true;
            }
        }
    }
}

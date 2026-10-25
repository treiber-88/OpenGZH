using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// TerrainClass (TERRAIN.CPP): trees and rocks, and the Tiberium "blossom" trees that grow
/// barnacles and spray damaging spores. Trees burn and crumble.
/// </summary>
public sealed class TerrainClass : ObjectClass
{
    const int BARNACLE_STAGE = 22;
    const int FIRST_SPORE_STAGE = 30;

    public readonly TerrainTypeClass Class;
    readonly StageClass stage = new();
    bool isOnFire, isCrumbling, isBlossoming, isBarnacled, isSporing;

    // operator new runs first (the constructor may place the object and even delete it).
    TerrainClass(TdGame game, int slot, TerrainType type, int cell) : base(game)
    {
        game.Terrains.Place(slot, this);
        Set_Active();

        Class = game.Types.Terrain(type);
        isBlossoming = false;
        isBarnacled = false;
        isSporing = false;
        isCrumbling = false;
        isOnFire = false;
        Strength = Class.MaxStrength;
        if (cell != -1 && !Unlimbo(TdMath.CellCoord(cell)))
            Delete();
        stage.Set_Rate(0);
    }

    /// <summary>new TerrainClass(type, cell): placed at once when a cell is given (and deleted if it
    /// can't be). Returns the object even then, as the original's pointer still points at it.</summary>
    public static TerrainClass? Create(TdGame game, TerrainType type, int cell = -1)
    {
        int slot = game.Terrains.Allocate();
        if (slot < 0)
            return null;
        return new TerrainClass(game, slot, type, cell);
    }

    protected override void Destruct()
    {
        if (Game.GameActive)
            Limbo();
        base.Destruct();
    }

    protected override void Release()
    {
        IsActive = false;
        Game.Terrains.Free(this);
    }

    public override RTTIType What_Am_I() => RTTIType.RTTI_TERRAIN;
    public override ObjectTypeClass Class_Of() => Class;
    public override uint As_Target() => Target.Build_Target(KindType.KIND_TERRAIN, Game.Terrains.ID(this));
    public override uint Center_Coord() => TdMath.CoordAdd(Coord, Class.CenterBaseCoord());
    public override uint Render_Coord() => Coord;
    public override uint Sort_Y() => TdMath.CoordAdd(Coord, Class.CenterBaseCoord());
    public override uint Target_Coord() => Sort_Y();
    public override void Clicked_As_Target(HousesType house, int count = 7) { }

    public int Fetch_Stage() => stage.Fetch_Stage();
    public bool IsCrumbling => isCrumbling;

    /// <summary>Take_Damage: fire spreads to trees; destroyed trees crumble (or burn out).</summary>
    public override ResultType Take_Damage(ref int damage, int distance, WarheadType warhead, TechnoClass? source = null)
    {
        ResultType res = ResultType.RESULT_NONE;
        if ((!isOnFire || warhead == WarheadType.WARHEAD_FIRE) && warhead != WarheadType.WARHEAD_SA && !Class.IsImmune)
        {
            res = base.Take_Damage(ref damage, distance, warhead, source);

            if (damage != 0 && warhead == WarheadType.WARHEAD_FIRE)
                Catch_Fire();

            if (res == ResultType.RESULT_DESTROYED)
            {
                Detach_All();
                if (isOnFire)
                    AnimClass.Shorten_Attached_Anims(Game, this);
                else
                    Start_To_Crumble();
            }
        }
        return res;
    }

    public override bool Mark(MarkType mark = MarkType.MARK_CHANGE)
    {
        if (base.Mark(mark))
        {
            short[] overlap = Class.Overlap_List();
            short[] occupy = Class.Occupy_List(Game.Special);
            int cell = TdMath.CoordCell(Coord);
            switch (mark)
            {
                case MarkType.MARK_UP:
                    Game.Map.Pick_Up(cell, this);
                    break;
                case MarkType.MARK_DOWN:
                    Game.Map.Place_Down(cell, this);
                    break;
                default:
                    Game.Map.Refresh_Cells(cell, overlap);
                    Game.Map.Refresh_Cells(cell, occupy);
                    break;
            }
            return true;
        }
        return false;
    }

    /// <summary>The frame to draw (Draw_It): the crumbling or growing animation, or the damaged frame.</summary>
    public int Shape_Number()
    {
        if (isCrumbling || Class.IsTransformable)
            return stage.Fetch_Stage() + (isCrumbling ? 1 : 0);
        return Strength < 2 ? 1 : 0;
    }

    public override MoveType Can_Enter_Cell(int cell, FacingType facing = FacingType.FACING_NONE)
    {
        if ((uint)cell >= TdDefines.MAP_CELL_TOTAL)
            return MoveType.MOVE_NO;
        foreach (short offset in Occupy_List())
        {
            if (offset == TdMath.RefreshEol)
                break;
            if (!Game.Map[(short)(cell + offset)].Is_Generally_Clear())
                return MoveType.MOVE_NO;
        }
        return MoveType.MOVE_OK;
    }

    /// <summary>Catch_Fire: flammable trees burn (big and medium flames).</summary>
    public override bool Catch_Fire()
    {
        if (!isCrumbling && !isOnFire && Class.IsFlammable)
        {
            AnimClass.Create(Game, AnimType.ANIM_BURN_BIG, TdMath.CoordAdd(Sort_Y(), 0xFFB00000))?.Attach_To(this);
            AnimClass.Create(Game, AnimType.ANIM_BURN_MED, TdMath.CoordAdd(Sort_Y(), 0xFF200000), 15)?.Attach_To(this);
            isOnFire = true;
            return true;
        }
        return false;
    }

    /// <summary>Fire_Out: a burnt-out dead tree crumbles in smoke.</summary>
    public override void Fire_Out()
    {
        if (isOnFire)
        {
            isOnFire = false;
            if (!isCrumbling && Strength == 0)
            {
                Detach_All();
                Mark();
                Start_To_Crumble();
                AnimClass.Create(Game, AnimType.ANIM_SMOKE_M, TdMath.CoordAdd(Coord, Class.CenterBaseCoord()));
            }
        }
    }

    public override void AI()
    {
        base.AI();

        if (stage.Graphic_Logic())
        {
            Mark();
            if (isCrumbling && stage.Fetch_Stage() == Class.ImageFrames - 1)
                Delete();
        }

        // Blossom trees: a rare chance to grow, then barnacles, then spores now and then.
        if (Class.IsTransformable)
        {
            if (isBlossoming)
            {
                if (!isBarnacled)
                {
                    if (stage.Fetch_Stage() == BARNACLE_STAGE)
                    {
                        isBarnacled = true;
                        stage.Set_Rate(0);
                    }
                }
                else if (isSporing)
                {
                    if (stage.Fetch_Stage() >= Class.ImageFrames - 1)
                    {
                        Game.Explosion_Damage(Sort_Y(), 5, null, WarheadType.WARHEAD_SPORE);
                        stage.Set_Stage(FIRST_SPORE_STAGE);
                        if ((Game.Random.Random() & 1) != 0)
                        {
                            isSporing = false;
                            stage.Set_Rate(0);
                        }
                    }
                }
                else if (Game.Random.Random() == 255)
                {
                    isSporing = true;
                    stage.Set_Rate(Game.Options.Normalize_Delay(1));
                }
            }
            else if (Game.Random_Pick(1, 5000) == 1)
            {
                isBlossoming = true;
                stage.Set_Stage(1);
                stage.Set_Rate(Game.Options.Normalize_Delay(1));
            }
        }
    }

    /// <summary>Unlimbo: only in the theaters the type belongs to.</summary>
    public override bool Unlimbo(uint coord, byte dir = 0)
    {
        if ((Class.Theater & (1L << (int)Game.Map.Theater)) != 0)
            return base.Unlimbo(coord, dir);
        return false;
    }

    public void Start_To_Crumble()
    {
        if (!isCrumbling)
        {
            isCrumbling = true;
            stage.Set_Rate(2);
            stage.Set_Stage(0);
        }
    }

    public override bool Limbo()
    {
        if (!IsInLimbo)
            Game.Map[TdMath.CoordCell(Coord)].Flag.Occupy.Monolith = false;
        return base.Limbo();
    }

    /// <summary>Read_INI: the scenario's [TERRAIN] — cell=type,trigger.</summary>
    public static void Read_INI(TdGame game, Data.WwProfile ini)
    {
        string buf = "";
        foreach (string key in ini.Entries("TERRAIN"))
        {
            int cell = (short)Data.WwProfile.Atoi(key);
            buf = ini.Get_String("TERRAIN", key, buf, 128 - 1);
            var tok = new Data.Strtok(buf);
            TerrainType terrain = game.Types.Terrain_From_Name(tok.Next(","));
            if (terrain != TerrainType.TERRAIN_NONE)
            {
                var tptr = Create(game, terrain, cell);
                // As the original, the trigger is set even if the tree couldn't be placed (and was deleted).
                if (tptr != null)
                {
                    tptr.Trigger = Scenario.TriggerClass.As_Pointer(game, tok.Next(","));
                    if (tptr.Trigger != null)
                        tptr.Trigger.AttachCount++;
                }
            }
        }
    }
}

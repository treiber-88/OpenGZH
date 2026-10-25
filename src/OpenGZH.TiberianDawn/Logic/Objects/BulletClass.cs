using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// BulletClass (BULLET.CPP): a projectile in flight — straight, arcing (artillery, grenades),
/// homing (missiles) or dropping (bombs) — with a fuse that sets it off near the target.
/// </summary>
public sealed class BulletClass : ObjectClass
{
    const int GRAVITY = 3;

    public readonly BulletTypeClass Class;
    public readonly FlyClass Fly = new();
    public readonly FuseClass Fuse = new();
    public TechnoClass? Payback;
    public readonly FacingClass PrimaryFacing = new();
    public bool IsInaccurate;
    bool isToAnimate;
    int altitude;
    sbyte riser;
    uint tarCom;
    bool isLocked;

    BulletClass(TdGame game, BulletType id) : base(game)
    {
        Class = game.Types.Bullet(id);
        altitude = 0;
        IsInaccurate = false;
        isLocked = true;
        isToAnimate = false;
        Payback = null;
        riser = 0;
        Strength = Class.MaxStrength;
        tarCom = Target.TARGET_NONE;
    }

    /// <summary>new BulletClass: the first free slot of the bullet heap (null when full).</summary>
    public static BulletClass? Create(TdGame game, BulletType id)
    {
        int slot = game.Bullets.Allocate();
        if (slot < 0)
            return null;
        var bullet = new BulletClass(game, id);
        game.Bullets.Place(slot, bullet);
        bullet.Set_Active();
        return bullet;
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
        Game.Bullets.Free(this);
    }

    public override RTTIType What_Am_I() => RTTIType.RTTI_BULLET;
    public override ObjectTypeClass Class_Of() => Class;
    public override LayerType In_Which_Layer() => LayerType.LAYER_TOP;
    public override uint As_Target() => Target.Build_Target(KindType.KIND_BULLET, Game.Bullets.ID(this));
    public int Altitude => altitude;

    public void Assign_Target(uint target) => tarCom = target;

    /// <summary>Occupy_List: the cells it covers (a flying shell also covers its shadow's cells).</summary>
    public override short[] Occupy_List(bool placement = false)
    {
        switch (Class.Type)
        {
            case BulletType.BULLET_FLAME:
                return TdMath.Coord_Spillage_List(Coord, 25);
            case BulletType.BULLET_NUKE_UP:
            case BulletType.BULLET_NUKE_DOWN:
                return TdMath.Coord_Spillage_List(Coord, 48);
        }

        if (altitude != 0)
        {
            var list = new List<short>();
            short[] ptr = TdMath.Coord_Spillage_List(Coord, 5);
            int cell1 = TdMath.CoordCell(Coord);
            foreach (short o in ptr)
            {
                if (o == TdMath.RefreshEol)
                    break;
                list.Add(o);
            }
            uint coord = TdMath.CoordSub(Coord, TdMath.XyCoord(0, altitude));
            int cell2 = TdMath.CoordCell(coord);
            foreach (short o in TdMath.Coord_Spillage_List(coord, 5))
            {
                if (o == TdMath.RefreshEol)
                    break;
                list.Add((short)(o + (cell2 - cell1)));
            }
            list.Add(TdMath.RefreshEol);
            return [.. list];
        }
        return TdMath.Coord_Spillage_List(Coord, 10);
    }

    public override short[] Overlap_List() => Occupy_List();

    public override bool Mark(MarkType mark = MarkType.MARK_CHANGE)
    {
        if (base.Mark(mark))
        {
            if (!Class.IsInvisible)
                Game.Map.Refresh_Cells(TdMath.CoordCell(Coord), Occupy_List());
            return true;
        }
        return false;
    }

    public override void AI()
    {
        base.AI();

        bool forced = false;

        // Arcs fall under gravity; bombs fall more slowly.
        if (Class.IsArcing)
        {
            altitude += riser;
            if (altitude <= 0)
                forced = true;
            if (riser > -100)
                riser -= GRAVITY;
        }
        if (Class.IsDropping)
        {
            altitude += riser;
            if (altitude <= 0)
                forced = true;
            if (riser > -100)
                riser -= 1;
        }

        // Missiles re-aim every other frame.
        if ((Game.Frame & 0x01) != 0 && Class.IsHoming && Game.Target_Legal(tarCom))
            PrimaryFacing.Set_Desired(TdMath.Direction(Coord, Game.As_Coord(tarCom)));

        uint coord = Coord;

        if (Class.IsFlameEquipped)
        {
            if (isToAnimate)
                AnimClass.Create(Game, AnimType.ANIM_SMOKE_PUFF, coord, 1);
            isToAnimate = !isToAnimate;
        }

        if (PrimaryFacing.Is_Rotating())
            PrimaryFacing.Rotation_Adjust(Class.ROT);

        switch (Fly.Physics(ref coord, PrimaryFacing.Current))
        {
            case ImpactType.IMPACT_EDGE:
                Mark();
                Delete_This();
                break;

            default:
                Mark();

                // Low shots hit walls and other high overlays.
                if (!Class.IsHigh)
                {
                    var cellptr = Game.Map[TdMath.CoordCell(coord)];
                    if (cellptr.Overlay != OverlayType.OVERLAY_NONE && Game.Types.Overlay(cellptr.Overlay).IsHigh)
                    {
                        forced = true;
                        Coord = coord = TdMath.CellCoord(TdMath.CoordCell(coord));
                    }
                }

                // Anti-aircraft shots close to their aircraft hit harder.
                if (Class.IsAntiAircraft && Game.As_Aircraft(tarCom) != null && DistanceToTarget(tarCom) < 0x0080)
                {
                    forced = true;
                    if (Class.Type == BulletType.BULLET_TOW)
                        Strength += Strength / 3;
                    else
                        Strength += Strength / 2;
                }

                if (!forced && (Class.IsDropping || !Fuse.Fuse_Checkup(coord)))
                {
                    Coord = coord;
                    Mark();
                    // Bullets weaken with distance.
                    if (Class.Type == BulletType.BULLET_BULLET && Strength > 5)
                        Strength--;
                }
                else
                {
                    // Detonate (at the fuse's target unless forced or curving).
                    Mark();
                    if (!forced && !Class.IsArcing && !Class.IsHoming && Fuse.Fuse_Target() != 0)
                        Coord = Fuse.Fuse_Target();

                    if (!Target.Is_Target_Aircraft(tarCom) || Game.As_Aircraft(tarCom)!.In_Which_Layer() == LayerType.LAYER_GROUND)
                    {
                        Game.Explosion_Damage(Coord, Strength, Payback, Class.Warhead);
                    }
                    else if (DistanceToTarget(tarCom) < 0x0080)
                    {
                        var aircraft = Game.As_Aircraft(tarCom);
                        int str = Strength;
                        aircraft?.Take_Damage(ref str, 0, Class.Warhead, Payback);
                    }

                    if (Class.IsInvisible)
                        Coord = Game.Coord_Scatter(Coord, 0x0020);

                    if (Class.Explosion != AnimType.ANIM_NONE)
                    {
                        var newanim = AnimClass.Create(Game, Class.Explosion, Coord);
                        newanim?.Sort_Above(tarCom);
                        if (newanim != null && Class.Explosion == AnimType.ANIM_ATOM_BLAST && newanim.OwnerHouse == HousesType.HOUSE_NONE)
                        {
                            if (Payback?.House?.Class != null)
                                newanim.Set_Owner(Payback.House.Class.House);
                        }
                    }
                    Delete_This();
                    return;
                }
                break;
        }
    }

    /// <summary>The frame to draw (Draw_It): facing, plus the napalm and grenade spin.</summary>
    public int Shape_Number()
    {
        int shapenum = 0;
        if (!Class.IsFaceless)
            shapenum = TdMath.BodyShape[TdMath.Facing32[PrimaryFacing.Current]];
        if (Class.Type == BulletType.BULLET_NAPALM)
            shapenum += (int)(Game.Frame % 6);
        if (Class.Type == BulletType.BULLET_GRENADE)
            shapenum += (int)(Game.Frame % 8);
        return shapenum;
    }

    /// <summary>Detach: forget the shooter, and (when the target is gone for good) the target.</summary>
    public override void Detach(uint target, bool all)
    {
        var obj = Game.As_Object(target);
        if (obj == Payback)
            Payback = null;
        if (all && target == tarCom)
            tarCom = Target.TARGET_NONE;
    }

    /// <summary>Unlimbo: launch — aim (with inaccuracy), set the speed and fuse, start the arc or drop.</summary>
    public override bool Unlimbo(uint coord, byte dir = 0)
    {
        if (base.Unlimbo(coord))
        {
            uint tcoord = Game.As_Coord(tarCom);

            if (!Class.IsHoming && !Class.IsDropping)
                dir = Direction(tcoord);

            // Inaccurate shots (and armour-piercing or fueled ones at the ground or infantry) scatter.
            if (IsInaccurate || Class.IsInaccurate ||
                ((Target.Is_Target_Cell(tarCom) || Target.Is_Target_Infantry(tarCom)) && (Class.Warhead == WarheadType.WARHEAD_AP || Class.IsFueled)))
            {
                if (Class.IsHoming || Class.IsArcing)
                {
                    int scatterdist = TdMath.Distance(coord, tcoord) / 3;
                    scatterdist = Math.Min(scatterdist, 0x0200);
                    if (Class.Type == BulletType.BULLET_GRENADE)
                    {
                        scatterdist = TdMath.Distance(coord, tcoord) / 4;
                        scatterdist = Math.Min(scatterdist, 0x0080);
                    }
                    dir = (byte)((dir + (Game.Random_Pick(0, 10) - 5)) & 0x00FF);
                    tcoord = Game.Coord_Scatter(tcoord, Game.Random_Pick(0, scatterdist));
                }
                else
                {
                    tcoord = TdMath.CoordMove(tcoord, dir, Game.Random_Pick(0, 0x0100));
                }

                // Never further than the shooter's range.
                if (Payback != null && !Payback.In_Range_Coord(tcoord, 0) && !Payback.In_Range_Coord(tcoord, 1))
                    tcoord = TdMath.CoordMove(tcoord, TdMath.Direction(tcoord, Coord), Distance(tcoord) - Math.Max(Payback.Weapon_Range(0), Payback.Weapon_Range(1)));
            }

            // Instant hits (lasers) start at the target.
            if (Class.MaxSpeed == MPHType.MPH_LIGHT_SPEED && Class.IsInvisible)
                Coord = tcoord;

            int range = 0xFF;
            if (Class.Range == 0)
            {
                if (!Class.IsDropping)
                    range = (TdMath.Distance(tcoord, Coord) / (int)Class.MaxSpeed) + 4;
            }
            else
            {
                range = Class.Range;
            }

            int speed = (int)Class.MaxSpeed;
            if (speed == (int)MPHType.MPH_LIGHT_SPEED)
                speed = (int)MPHType.MPH_IMMOBILE;
            if (Class.IsArcing)
            {
                speed = (int)Class.MaxSpeed + (Distance(tcoord) >> 5);
                speed = Math.Max(speed, 25);
            }

            if (!Class.IsDropping)
                Fly.Fly_Speed(255, (MPHType)speed);

            Fuse.Arm_Fuse(Coord, tcoord, range, Game.As_Aircraft(tarCom) != null ? 0 : Class.Arming);

            altitude = 0;
            riser = 0;
            if (Class.IsArcing)
            {
                altitude = 1;
                riser = unchecked((sbyte)(((Distance(tcoord) / 2) / (speed + 1)) * GRAVITY));
                riser = Math.Max(riser, (sbyte)10);
            }
            if (Class.IsDropping)
            {
                altitude = TdMath.PixelToLepton(24);
                riser = 0;
            }

            PrimaryFacing.Set(dir);
            return true;
        }
        return false;
    }
}

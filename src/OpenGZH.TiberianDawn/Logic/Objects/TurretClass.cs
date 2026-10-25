namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// TurretClass (TURRET.CPP): vehicles with a turret (or a radar dish) that turns apart from the
/// body: the turret's facing, where shots leave the barrel, and the facing check before firing.
/// </summary>
public abstract class TurretClass : DriveClass
{
    /// <summary>Pixel2Lepton (CONST.CPP): pixel offsets 0..23 as leptons.</summary>
    static readonly byte[] Pixel2Lepton =
    [
        0x00, 0x0B, 0x15, 0x20, 0x2B, 0x35, 0x40, 0x4B,
        0x55, 0x60, 0x6B, 0x75, 0x80, 0x8B, 0x95, 0xA0,
        0xAB, 0xB5, 0xC0, 0xCB, 0xD5, 0xE0, 0xEB, 0xF5,
    ];

    public readonly CountDownTimer Reload;
    public readonly FacingClass SecondaryFacing = new();

    protected TurretClass(TdGame game, UnitType classid, HousesType house) : base(game, classid, house)
    {
        Reload = new CountDownTimer(game);
        Reload.Set(0);
    }

    /// <summary>Ok_To_Move (a lock-turret unit turns its turret first). In the original this
    /// lacks DriveClass's const, so it hides rather than overrides it; nothing calls either.</summary>
    public new bool Ok_To_Move(byte dir)
    {
        if (Class.IsLockTurret)
        {
            if (IsRotating)
                return false;
            if (SecondaryFacing.Difference(dir) != 0)
            {
                SecondaryFacing.Set_Desired(dir);
                return false;
            }
        }
        return true;
    }

    public override void AI()
    {
        base.AI();

        if (Class.IsRadarEquipped)
        {
            // The radar dish spins.
            SecondaryFacing.Set((byte)(SecondaryFacing.Current + 8));
            Mark(MarkType.MARK_CHANGE);
        }
        else
        {
            IsRotating = false;
            if (Class.IsTurretEquipped)
            {
                if (IsTurretLockedDown)
                    SecondaryFacing.Set_Desired(PrimaryFacing.Current);

                if (SecondaryFacing.Is_Rotating())
                {
                    if (SecondaryFacing.Rotation_Adjust(Class.ROT + 1))
                        Mark(MarkType.MARK_CHANGE);
                    IsRotating = SecondaryFacing.Is_Rotating();
                }
                else if (!IsTurretLockedDown && !Game.Target_Legal(TarCom))
                {
                    // With nothing to shoot, the turret faces the way the unit is going.
                    if (!Game.Target_Legal(NavCom))
                        SecondaryFacing.Set_Desired(PrimaryFacing.Current);
                    else
                        SecondaryFacing.Set_Desired(DirectionToTarget(NavCom));
                }
            }
        }
    }

    /// <summary>Fire_At: the mobile SAM reloads for 30 seconds after a volley.</summary>
    public override BulletClass? Fire_At(uint target, int which = 0)
    {
        BulletClass? bullet = null;
        if (Can_Fire(target, which) == FireErrorType.FIRE_OK)
        {
            bullet = base.Fire_At(target, which);
            if (bullet != null && Class.Type == UnitType.UNIT_MSAM && Reload.Time == 0)
                Reload.Set(TdDefines.TICKS_PER_SECOND * 30);
        }
        return bullet;
    }

    /// <summary>Can_Fire: also needs the turret (or body) to face the target, and not to be moving
    /// (for units without a free turret).</summary>
    public override FireErrorType Can_Fire(uint target, int which = 0)
    {
        FireErrorType fire = base.Can_Fire(target, which);
        if (fire == FireErrorType.FIRE_OK)
        {
            var weapon = Game.Types.Weapon(which == 0 ? Class.Primary : Class.Secondary);
            var bullet = Game.Types.Bullet(weapon.Fires);

            if ((!Class.IsTurretEquipped || Class.IsLockTurret) && Game.Target_Legal(NavCom))
                return FireErrorType.FIRE_MOVING;

            if (!IsFiring && IsRotating && !bullet.IsHoming)
                return FireErrorType.FIRE_ROTATING;

            byte dir = DirectionToTarget(target);
            int diff = Class.IsTurretEquipped ? SecondaryFacing.Difference(dir) : PrimaryFacing.Difference(dir);
            diff = Math.Abs(diff);

            // Flame throwers only need the rough direction.
            if (weapon.Fires == BulletType.BULLET_FLAME && TdMath.DirFacingType(dir) == TdMath.DirFacingType(PrimaryFacing.Current))
                diff = 0;

            // Homing missiles can be fired more off target.
            if (bullet.IsHoming)
                diff >>= 2;

            if (diff < 8)
                return base.Can_Fire(target, which);
            return FireErrorType.FIRE_FACING;
        }
        return fire;
    }

    /// <summary>Fire_Data: where (before the barrel length) the shot leaves, and the barrel length.</summary>
    public override FireData Fire_Data(int which)
    {
        uint coord = Center_Coord();
        int dist = 0;

        switch (Class.Type)
        {
            case UnitType.UNIT_GUNBOAT:
                coord = TdMath.CoordMove(coord, PrimaryFacing.Current, Pixel2Lepton[Class.TurretOffset]);
                dist = 0x0060;
                break;
            case UnitType.UNIT_ARTY:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0040);
                dist = 0x0060;
                break;
            case UnitType.UNIT_FTANK:
                dist = 0x30;
                break;
            case UnitType.UNIT_HTANK:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0040);
                dist = which == 0 ? 0x00C0 : 0x0008;
                break;
            case UnitType.UNIT_LTANK:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0020);
                dist = 0x00C0;
                break;
            case UnitType.UNIT_MTANK:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0030);
                dist = 0x00C0;
                break;
            case UnitType.UNIT_APC:
            case UnitType.UNIT_JEEP:
            case UnitType.UNIT_BUGGY:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0030);
                dist = 0x0030;
                break;
        }
        return new FireData(coord, dist);
    }

    /// <summary>Fire_Coord: the barrel's tip (twin barrels alternate sides).</summary>
    public override uint Fire_Coord(int which)
    {
        uint coord = Center_Coord();
        int dist = 0;
        int lateral;
        byte dir = Class.IsTurretEquipped ? SecondaryFacing.Current : PrimaryFacing.Current;

        switch (Class.Type)
        {
            case UnitType.UNIT_GUNBOAT:
                coord = TdMath.CoordMove(coord, PrimaryFacing.Current, Pixel2Lepton[Class.TurretOffset]);
                dist = 0x0060;
                break;
            case UnitType.UNIT_ARTY:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0040);
                dist = 0x0060;
                break;
            case UnitType.UNIT_FTANK:
                dist = 0x30;
                coord = TdMath.CoordMove(coord, (byte)(dir + (IsSecondShot ? (int)DirType.DIR_E : (int)DirType.DIR_W)), 0x20);
                break;
            case UnitType.UNIT_HTANK:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0040);
                if (which == 0)
                {
                    dist = 0x00C0;
                    lateral = 0x0028;
                }
                else
                {
                    dist = 0x0008;
                    lateral = 0x0040;
                }
                coord = TdMath.CoordMove(coord, (byte)(dir + (IsSecondShot ? (int)DirType.DIR_E : (int)DirType.DIR_W)), lateral);
                break;
            case UnitType.UNIT_LTANK:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0020);
                dist = 0x00C0;
                break;
            case UnitType.UNIT_MTANK:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0030);
                dist = 0x00C0;
                break;
            case UnitType.UNIT_APC:
            case UnitType.UNIT_JEEP:
            case UnitType.UNIT_BUGGY:
                coord = TdMath.CoordMove(coord, (byte)DirType.DIR_N, 0x0030);
                dist = 0x0030;
                break;
        }

        if (dist != 0)
            coord = TdMath.CoordMove(coord, dir, dist);
        return coord;
    }

    public override bool Unlimbo(uint coord, byte dir = 0)
    {
        if (base.Unlimbo(coord, dir))
        {
            SecondaryFacing.Set(dir);
            return true;
        }
        return false;
    }

    /// <summary>Fire_Direction: the turret's facing (the mobile SAM's launcher tilts towards north).</summary>
    public override byte Fire_Direction()
    {
        if (Class.IsTurretEquipped)
        {
            if (Class.Type == UnitType.UNIT_MSAM)
            {
                int diff1 = Math.Abs(SecondaryFacing.Difference((byte)DirType.DIR_E));
                int diff2 = Math.Abs(SecondaryFacing.Difference((byte)DirType.DIR_W));
                int diff = Math.Min(diff1, diff2);
                int adj = (int)TdMath.FixedToCardinal((uint)Math.Abs(SecondaryFacing.Difference((byte)DirType.DIR_N)), (uint)(64 - diff));
                if (SecondaryFacing.Difference((byte)DirType.DIR_N) < 0)
                    return (byte)(SecondaryFacing.Current - adj);
                return (byte)(SecondaryFacing.Current + adj);
            }
            return SecondaryFacing.Current;
        }
        return PrimaryFacing.Current;
    }
}

/// <summary>TarComClass (TARCOM.CPP): turreted units fire at their target (TarCom) and keep
/// the turret (or the body) turned towards it.</summary>
public abstract class TarComClass : TurretClass
{
    protected TarComClass(TdGame game, UnitType classid, HousesType house) : base(game, classid, house) { }

    public override void AI()
    {
        base.AI();

        if (Class.Primary != WeaponType.WEAPON_NONE)
        {
            var weapon = Game.Types.Weapon(Class.Primary);
            int primary = 0;

            FireErrorType ok = Can_Fire(TarCom, 0);
            if (ok != FireErrorType.FIRE_OK && Can_Fire(TarCom, 1) == FireErrorType.FIRE_OK)
            {
                ok = FireErrorType.FIRE_OK;
                primary = 1;
                weapon = Game.Types.Weapon(Class.Secondary);
            }

            switch (ok)
            {
                case FireErrorType.FIRE_OK:
                    if (What_Am_I() != RTTIType.RTTI_UNIT || !Class.IsFireAnim)
                        IsFiring = false;
                    // TurretClass::Fire_At, called directly as in the original (not UnitClass's).
                    if (base.Fire_At(TarCom, primary) != null)
                        Game.Sound_Effect(weapon.Sound, Coord);
                    break;

                case FireErrorType.FIRE_FACING:
                    if (Class.IsLockTurret)
                    {
                        if (!Game.Target_Legal(NavCom) && !IsDriving)
                        {
                            PrimaryFacing.Set_Desired(DirectionToTarget(TarCom));
                            SecondaryFacing.Set_Desired(PrimaryFacing.Desired);
                        }
                    }
                    else if (Class.Type == UnitType.UNIT_FTANK)
                    {
                        SecondaryFacing.Set_Desired((byte)((int)TdMath.DirFacingType(DirectionToTarget(TarCom)) << 5));
                    }
                    else
                    {
                        SecondaryFacing.Set_Desired(DirectionToTarget(TarCom));
                    }
                    break;

                case FireErrorType.FIRE_CLOAKED:
                    IsFiring = false;
                    Do_Uncloak();
                    break;
            }
        }

        // Keep facing the target (tracked units and bikes turn the whole body when stopped).
        if (Game.Target_Legal(TarCom) && !IsRotating)
        {
            byte dir = DirectionToTarget(TarCom);
            if (Class.IsTurretEquipped)
            {
                SecondaryFacing.Set_Desired(dir);
            }
            else if ((Class.Speed == SpeedType.SPEED_TRACK || Class.Type == UnitType.UNIT_BIKE) && !Game.Target_Legal(NavCom) && !IsDriving && PrimaryFacing.Difference(dir) != 0)
            {
                if (Class.Type == UnitType.UNIT_FTANK)
                    PrimaryFacing.Set_Desired((byte)((int)TdMath.DirFacingType(dir) << 5));
                else
                    PrimaryFacing.Set_Desired(dir);
            }
        }
    }
}

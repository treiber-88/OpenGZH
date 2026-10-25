using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>FireDataType: where a shot leaves from and how far from the centre that is.</summary>
public readonly record struct FireData(uint Center, int Distance);

/// <summary>
/// TechnoClass (TECHNO.CPP): what buildings, vehicles, infantry and aircraft share: an owner
/// (house), a target (TarCom), weapons and reloading, cloaking, threat evaluation, radio replies
/// (tether, repair, reload), kills and discovery by other players.
/// </summary>
public abstract class TechnoClass : RadioClass
{
    public bool IsTickedOff, IsCloakable, IsLeader, IsALoaner, IsLocked, IsInRecoilState, IsTethered;
    public bool IsOwnedByPlayer, IsDiscoveredByPlayer, IsDiscoveredByComputer, IsALemon, IsSecondShot;
    public uint ArchiveTarget = Target.TARGET_NONE;
    public HouseClass House;
    public CloakType Cloak = CloakType.UNCLOAKED;
    public readonly StageClass CloakingDevice = new();
    public uint TarCom = Target.TARGET_NONE;
    public uint SuspendedTarCom = Target.TARGET_NONE;
    public readonly FacingClass PrimaryFacing = new();
    public int Arm;
    public int Ammo = -1;
    /// <summary>Laser lines to draw: [x0, y0, x1, y1, colour] in map pixels.</summary>
    public readonly int[,] Lines = new int[3, 5];
    public int LineCount, LineFrame, LineMaxFrames;
    public int PurchasePrice;
    public int IsDiscoveredByPlayerMask;

    // The original's mixin base classes.
    public readonly FlasherClass Flasher = new();
    public readonly StageClass Stage = new();
    public readonly CargoClass Cargo = new();
    public readonly DoorClass Door = new();
    public readonly CrewClass Crew = new();

    /// <summary>TechnoClass(HousesType): belongs to a house; may be a lemon (the house's Lemon chance).</summary>
    protected TechnoClass(TdGame game, HousesType house) : base(game)
    {
        House = game.HouseFor(house);
        CloakingDevice.Set_Stage(1);
        CloakingDevice.Set_Rate(0);
        IsSecondShot = false;
        PrimaryFacing.Set(0);
        IsOwnedByPlayer = game.GameToPlay == GameType.GAME_NORMAL ? house == game.PlayerPtr.Class.House : House.IsHuman;
        if (game.Random_Pick(0, 255) < House.Class.Lemon)
            IsALemon = true;
    }

    public TechnoTypeClass Techno_Type_Class() => (TechnoTypeClass)Class_Of();
    public override bool Is_Techno() => true;
    public override HousesType Owner() => House.Class.House;
    public override int Get_Ownable() => (int)Techno_Type_Class().Ownable;
    public virtual int Risk() => Techno_Type_Class().Risk;
    public virtual int Tiberium_Load() => 0;
    public virtual byte Desired_Load_Dir(ObjectClass passenger, out int moveTo)
    {
        moveTo = 0;
        return 0;
    }
    public virtual int Pip_Count() => 0;
    public virtual byte Fire_Direction() => PrimaryFacing.Current;
    public virtual void Response_Select() { }
    public virtual void Response_Move() { }
    public virtual void Response_Attack() { }
    public override int Exit_Object(TechnoClass obj) => 0;
    public virtual void Random_Animate() { }
    public virtual void Assign_Destination(uint target) { }
    public override void Scatter(uint threat, bool forced = false, bool nokidding = false) { }
    public virtual void Enter_Idle_Mode(bool initial = false) { }
    public abstract void Death_Announcement(TechnoClass? source = null);
    public virtual int Made_A_Kill() => Crew.Made_A_Kill();
    public int Kills => Crew.Kills;

    /// <summary>ObjectClass::Fire_Data: the fire coordinate and no extra distance.</summary>
    public virtual FireData Fire_Data(int which) => new(Fire_Coord(which), 0);

    // ---- Being seen ----

    public override bool Revealed(HouseClass? house)
    {
        if (house == null)
            return false;
        if (Is_Discovered_By_Player(house))
            return false;
        if (!house.IsHuman && IsDiscoveredByComputer)
            return false;
        if (base.Revealed(house))
        {
            if (!house.IsHuman && Mission == MissionType.MISSION_AMBUSH)
                Assign_Mission(MissionType.MISSION_HUNT);
            if (Game.GameToPlay == GameType.GAME_NORMAL)
            {
                if (house == Game.PlayerPtr)
                {
                    if (!IsOwnedByPlayer)
                    {
                        Trigger?.Spring(EventType.EVENT_DISCOVERED, this);
                        House.IsDiscovered = true;
                        if (house.IsHuman)
                            Set_Discovered_By_Player(house);
                        else
                            IsDiscoveredByComputer = true;
                    }
                    else
                    {
                        if (house.IsHuman)
                            Set_Discovered_By_Player(house);
                        else
                            IsDiscoveredByComputer = true;
                        Look();
                    }
                }
            }
            else
            {
                if (house.IsHuman)
                    Set_Discovered_By_Player(house);
                else
                    IsDiscoveredByComputer = true;
                Look();
            }
            return true;
        }
        return false;
    }

    public override void Hidden()
    {
        if (!Is_Discovered_By_Player())
            return;
        if (!House.IsHuman)
            Clear_Discovered_By_Players();
    }

    public override bool Mark(MarkType mark)
    {
        if (base.Mark(mark))
        {
            if (IsTethered)
                Transmit_Message(RadioMessageType.RADIO_REDRAW);
            return true;
        }
        return false;
    }

    public override RadioMessageType Receive_Message(RadioClass? from, RadioMessageType message, ref int param)
    {
        switch (message)
        {
            case RadioMessageType.RADIO_ATTACK_THIS:
                if (Techno_Type_Class().Primary != WeaponType.WEAPON_NONE)
                {
                    Assign_Target((uint)param);
                    Assign_Mission(MissionType.MISSION_ATTACK);
                    return RadioMessageType.RADIO_ROGER;
                }
                break;
            case RadioMessageType.RADIO_TETHER:
                if (!IsTethered)
                {
                    IsTethered = true;
                    Transmit_Message(RadioMessageType.RADIO_TETHER, from);
                    return RadioMessageType.RADIO_ROGER;
                }
                break;
            case RadioMessageType.RADIO_UNTETHER:
                if (IsTethered)
                {
                    IsTethered = false;
                    Transmit_Message(RadioMessageType.RADIO_UNTETHER, from);
                    return RadioMessageType.RADIO_ROGER;
                }
                break;
            case RadioMessageType.RADIO_UNLOADED:
                Transmit_Message(RadioMessageType.RADIO_UNTETHER, from);
                return Transmit_Message(RadioMessageType.RADIO_OVER_OUT, from);
            case RadioMessageType.RADIO_OVER_OUT:
                Transmit_Message(RadioMessageType.RADIO_UNTETHER, from);
                base.Receive_Message(from, message, ref param);
                return RadioMessageType.RADIO_ROGER;
            case RadioMessageType.RADIO_UNLOAD:
            case RadioMessageType.RADIO_BACKUP_NOW:
            case RadioMessageType.RADIO_HOLD_STILL:
                Transmit_Message(RadioMessageType.RADIO_TETHER, from);
                base.Receive_Message(from, message, ref param);
                return RadioMessageType.RADIO_ROGER;
            case RadioMessageType.RADIO_RELOAD:
                if (Ammo == Techno_Type_Class().MaxAmmo)
                    return RadioMessageType.RADIO_NEGATIVE;
                Ammo++;
                return RadioMessageType.RADIO_ROGER;
            case RadioMessageType.RADIO_REPAIR:
                if (Health_Ratio() < 0x0100)
                {
                    int cost = Math.Max(Techno_Type_Class().Repair_Cost(Game.Types), 1);
                    int step = Math.Max(Techno_Type_Class().Repair_Step(), 1);
                    if (House.Available_Money() >= cost)
                    {
                        House.Spend_Money(cost);
                        Strength += step;
                        return RadioMessageType.RADIO_ROGER;
                    }
                }
                break;
        }
        return base.Receive_Message(from, message, ref param);
    }

    /// <summary>Per_Cell_Process: locks on (fully on the map) and gets revealed by the cell's visibility.</summary>
    public virtual void Per_Cell_Process(bool center)
    {
        int cell = TdMath.CoordCell(Center_Coord());
        if (Game.Map.In_Radar(cell))
        {
            if (this is UnitClass u)
            {
                if (u.Class.Type != UnitType.UNIT_HOVER && u.Class.Type != UnitType.UNIT_GUNBOAT)
                    IsLocked = true;
            }
            else
                IsLocked = true;
        }
        if (!Is_Discovered_By_Player() && Game.Map[cell].Is_Visible(House))
            Revealed(House);
    }

    public override bool Unlimbo(uint coord, byte dir = 0)
    {
        if (base.Unlimbo(coord, dir))
        {
            PrimaryFacing.Set(dir);
            Enter_Idle_Mode(true);
            Commence();
            IsLocked = Game.Map.In_Radar(TdMath.CoordCell(coord));
            return true;
        }
        return false;
    }

    // ---- Range and threats ----

    public bool In_Range(uint target, int which, bool reciprocalCheck)
    {
        if (IsLocked && Game.Target_Legal(target))
        {
            int range = Weapon_Range(which);
            var building = Game.As_Building(target);
            if (building != null)
                range += (building.Class.Width() + building.Class.Height()) * ((int)TdDefines.ICON_LEPTON_W / 4);
            var data = Fire_Data(which);
            if (Math.Max(0, TdMath.Distance(data.Center, Game.As_Coord(target)) - data.Distance) <= range)
                return true;
            if (reciprocalCheck && building == null && Game.As_Object(target) is { } targetObject
                && targetObject.What_Am_I() == What_Am_I() && range == targetObject.Weapon_Range(which)
                && Game.As_Techno(target) is { } tech && tech.In_Range(As_Target(), which, false))
                return true;
        }
        return false;
    }

    public bool In_Range(uint target, int which = 0) => In_Range(target, which, true);

    public bool In_Range(ObjectClass? target, int which, bool reciprocalCheck = true)
    {
        if (IsLocked && target != null)
        {
            int range = Weapon_Range(which);
            if (target is BuildingClass building)
                range += (building.Class.Width() + building.Class.Height()) * ((int)TdDefines.ICON_LEPTON_W / 4);
            var data = Fire_Data(which);
            if (Math.Max(0, TdMath.Distance(data.Center, target.Center_Coord()) - data.Distance) <= range)
                return true;
            if (reciprocalCheck)
            {
                var mine = What_Am_I();
                if (mine != RTTIType.RTTI_BUILDING && target.What_Am_I() == mine && range == target.Weapon_Range(which)
                    && ((TechnoClass)target).In_Range(this, which, false))
                    return true;
            }
        }
        return false;
    }

    /// <summary>In_Range(COORDINATE): within weapon range of a map position.</summary>
    public override bool In_Range_Coord(uint coord, int which = 0) => IsLocked && TdMath.Distance(Fire_Coord(which), coord) <= Weapon_Range(which);

    /// <summary>Evaluate_Object: whether an object is a candidate target, and how valuable it is.</summary>
    public bool Evaluate_Object(ThreatType method, int mask, int range, TechnoClass obj, ref int value)
    {
        if (obj.IsInLimbo)
            return false;
        if (House.Is_Ally(obj))
            return false;
        int dist = Distance(obj);
        if (range > 0 && dist > range)
            return false;
        if (range == 0 && !In_Range(obj, 0) && !In_Range(obj, 1))
            return false;
        if (!obj.IsOwnedByPlayer && !obj.IsDiscoveredByPlayer && Game.GameToPlay == GameType.GAME_NORMAL && obj.What_Am_I() != RTTIType.RTTI_AIRCRAFT)
            return false;
        var otype = obj.What_Am_I();
        if (((1 << (int)otype) & mask) == 0)
            return false;
        if (obj.Is_Cloaked(this))
            return false;
        var tclass = obj.Techno_Type_Class();
        if (!tclass.IsLegalTarget)
            return false;
        if (otype == RTTIType.RTTI_INFANTRY && ((InfantryTypeClass)tclass).Type == InfantryType.INFANTRY_DELPHI)
            return false;
        if (otype == RTTIType.RTTI_AIRCRAFT && this is BuildingClass { Class.Type: StructType.STRUCT_SAM } && ((AircraftClass)obj).Altitude == 0)
            return false;
        if ((method & ThreatType.THREAT_CIVILIANS) != 0 && obj.Owner() != HousesType.HOUSE_NEUTRAL)
            return false;
        if ((method & ThreatType.THREAT_CAPTURE) != 0 && (otype != RTTIType.RTTI_BUILDING || !obj.Can_Capture()))
            return false;
        if ((method & ThreatType.THREAT_BOATS) == 0 && otype == RTTIType.RTTI_UNIT
            && ((UnitTypeClass)tclass).Speed is SpeedType.SPEED_HOVER or SpeedType.SPEED_FLOAT)
            return false;
        if (House.IsHuman && otype == RTTIType.RTTI_BUILDING && tclass.Primary == WeaponType.WEAPON_NONE)
            return false;
        if ((method & ThreatType.THREAT_TIBERIUM) != 0)
        {
            switch (otype)
            {
                case RTTIType.RTTI_UNIT:
                    if (!((UnitTypeClass)tclass).IsToHarvest)
                        return false;
                    break;
                case RTTIType.RTTI_BUILDING:
                    if (((BuildingTypeClass)tclass).Capacity == 0)
                        return false;
                    break;
                default:
                    return false;
            }
        }
        int rawval = obj.Value();
        value = rawval + obj.Kills;

        // The skirmish computer gravitates toward its designated enemy.
        if (House.Enemy != HousesType.HOUSE_NONE && House.Enemy == obj.House.Class.House)
        {
            value += 500;
            value *= 3;
        }
        if (range == -1 && value != 0)
            value = value * 32000 / (dist / (int)TdDefines.ICON_LEPTON_W + 1);
        else
        {
            int modifier = dist;
            int crange = range / (int)TdDefines.ICON_LEPTON_W;
            if (crange != 0)
                modifier /= crange;
            if (modifier != 0)
                value /= modifier;
        }
        if (rawval != 0)
            value = Math.Max(value, 2);
        return true;
    }

    public bool Evaluate_Cell(ThreatType method, int mask, int cell, int range, out TechnoClass? obj, ref int value)
    {
        obj = null;
        value = 0;
        if ((cell & 0xF000) != 0)
            return false;
        if (!Game.Map.In_Radar(cell))
            return false;
        var tentative = Game.Map[cell].Cell_Occupier();
        while (tentative != null)
        {
            if (tentative is TechnoClass t && !House.Is_Ally(t))
                break;
            tentative = tentative.Next;
        }
        if (tentative is not TechnoClass found)
            return false;
        obj = found;
        return Evaluate_Object(method, mask, range, found, ref value);
    }

    /// <summary>Greatest_Threat: the best target for the given threat method (nearby rings, or the whole map).</summary>
    public virtual uint Greatest_Threat(ThreatType method) => Techno_Greatest_Threat(method);

    /// <summary>TechnoClass::Greatest_Threat itself, for callers that name it directly (skipping overrides).</summary>
    protected uint Techno_Greatest_Threat(ThreatType method)
    {
        ObjectClass? bestobject = null;
        int bestval = -1;
        int mask = 0;
        if ((method & ThreatType.THREAT_CIVILIANS) != 0) mask |= (1 << (int)RTTIType.RTTI_BUILDING) | (1 << (int)RTTIType.RTTI_INFANTRY) | (1 << (int)RTTIType.RTTI_UNIT);
        if ((method & ThreatType.THREAT_AIR) != 0) mask |= 1 << (int)RTTIType.RTTI_AIRCRAFT;
        if ((method & ThreatType.THREAT_CAPTURE) != 0) mask |= 1 << (int)RTTIType.RTTI_BUILDING;
        if ((method & ThreatType.THREAT_BUILDINGS) != 0) mask |= 1 << (int)RTTIType.RTTI_BUILDING;
        if ((method & ThreatType.THREAT_INFANTRY) != 0) mask |= 1 << (int)RTTIType.RTTI_INFANTRY;
        if ((method & ThreatType.THREAT_VEHICLES) != 0) mask |= 1 << (int)RTTIType.RTTI_UNIT;

        const int maxBestObjects = 128;
        var bestObjects = new List<ObjectClass>();

        if ((method & (ThreatType.THREAT_AREA | ThreatType.THREAT_RANGE)) != 0)
        {
            int range = Threat_Range((method & ThreatType.THREAT_RANGE) != 0 ? 0 : 1);
            int crange = range / (int)TdDefines.ICON_LEPTON_W;
            if (range == 0)
            {
                crange = Math.Max(Weapon_Range(0), Weapon_Range(1)) / (int)TdDefines.ICON_LEPTON_W;
                crange++;
            }
            int cell = TdMath.CoordCell(Fire_Coord(0));
            if ((method & ThreatType.THREAT_AIR) != 0)
                for (int index = 0; index < Game.Aircraft.Count; index++)
                {
                    TechnoClass obj = Game.Aircraft.Ptr(index);
                    int value = 0;
                    if (Evaluate_Object(method, mask, range, obj, ref value) && value > bestval)
                    {
                        bestobject = obj;
                        bestval = value;
                    }
                }
            if ((method & ThreatType.THREAT_VEHICLES) != 0)
                mask |= 1 << (int)RTTIType.RTTI_AIRCRAFT;

            var map = Game.Map;
            for (int radius = 1; radius < crange; radius++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    if (TdMath.CellX(cell) + x < map.MapCellX) continue;
                    if (TdMath.CellX(cell) + x >= map.MapCellX + map.MapCellWidth) continue;
                    if (TdMath.CellY(cell) - radius >= map.MapCellY)
                    {
                        int newcell = TdMath.XyCell(TdMath.CellX(cell) + x, TdMath.CellY(cell) - radius);
                        int value = 0;
                        if (Evaluate_Cell(method, mask, newcell, range, out var obj, ref value) && bestval < value)
                            bestobject = obj;
                    }
                    if (TdMath.CellY(cell) + radius < map.MapCellY + map.MapCellHeight)
                    {
                        int newcell = TdMath.XyCell(TdMath.CellX(cell) + x, TdMath.CellY(cell) + radius);
                        int value = 0;
                        if (Evaluate_Cell(method, mask, newcell, range, out var obj, ref value) && bestval < value)
                            bestobject = obj;
                    }
                }
                for (int y = -(radius - 1); y < radius; y++)
                {
                    if (TdMath.CellY(cell) + y < map.MapCellY) continue;
                    if (TdMath.CellY(cell) + y >= map.MapCellY + map.MapCellHeight) continue;
                    if (TdMath.CellX(cell) - radius >= map.MapCellX)
                    {
                        int newcell = TdMath.XyCell(TdMath.CellX(cell) - radius, TdMath.CellY(cell) + y);
                        int value = 0;
                        if (Evaluate_Cell(method, mask, newcell, range, out var obj, ref value) && bestval < value)
                            bestobject = obj;
                    }
                    if (TdMath.CellX(cell) + radius < map.MapCellX + map.MapCellWidth)
                    {
                        int newcell = TdMath.XyCell(TdMath.CellX(cell) + radius, TdMath.CellY(cell) + y);
                        int value = 0;
                        if (Evaluate_Cell(method, mask, newcell, range, out var obj, ref value) && bestval < value)
                            bestobject = obj;
                    }
                }
                // (As in the original, the ring search does not update bestval, so any later find replaces it.)
                if (bestobject != null)
                {
                    if (radius == crange / 4)
                        return bestobject.As_Target();
                    if (radius == crange / 2)
                        return bestobject.As_Target();
                }
            }
        }
        else
        {
            for (int index = 0; index < Game.Aircraft.Count; index++)
            {
                TechnoClass obj = Game.Aircraft.Ptr(index);
                int value = 0;
                if (Evaluate_Object(method, mask, -1, obj, ref value))
                {
                    if (value > bestval)
                    {
                        bestobject = obj;
                        bestval = value;
                        bestObjects.Clear();
                        bestObjects.Add(obj);
                    }
                    else if (value == bestval && bestObjects.Count < maxBestObjects)
                        bestObjects.Add(obj);
                }
            }
            var ground = Game.Map.Layer[(int)LayerType.LAYER_GROUND];
            for (int index = 0; index < ground.Count; index++)
            {
                var obj = ground[index];
                int value = 0;
                if (obj is TechnoClass techno && Evaluate_Object(method, mask, -1, techno, ref value))
                {
                    if (value > bestval)
                    {
                        bestobject = obj;
                        bestval = value;
                        bestObjects.Clear();
                        bestObjects.Add(obj);
                    }
                    else if (value == bestval && bestObjects.Count < maxBestObjects)
                        bestObjects.Add(obj);
                }
            }
        }
        if (bestobject != null)
        {
            if (bestObjects.Count > 1)
            {
                int index = Game.Random_Pick(0, bestObjects.Count - 1);
                bestobject = Game.As_Object(bestObjects[index].As_Target());
            }
            return bestobject!.As_Target();
        }
        return Target.TARGET_NONE;
    }

    public override void Clicked_As_Target(HousesType house, int count = 7)
    {
        Flasher.FlashCount = count;
        if (house < HousesType.HOUSE_COUNT && house >= 0)
            Flasher.FlashCountPerPlayer[(int)house] = count;
        else
            for (int i = 0; i < Flasher.FlashCountPerPlayer.Length; i++)
                Flasher.FlashCountPerPlayer[i] = count;
    }

    // ---- The frame ----

    public override void AI()
    {
        base.AI();
        Door.AI();
        if (IsCloakable)
        {
            if (Cloak == CloakType.UNCLOAKED)
            {
                if (Is_Owned_By_Player())
                    Mark(MarkType.MARK_CHANGE);
                CloakingDevice.Graphic_Logic();
                if (Arm == 0 && CloakingDevice.Fetch_Stage() != 0)
                {
                    if (Health_Ratio() > 0x0040)
                        Do_Cloak();
                    else if (Game.Random_Pick(0, 25) == 1)
                        Do_Cloak();
                }
            }
            else
            {
                CloakingDevice.Graphic_Logic();
                switch (Cloak)
                {
                    case CloakType.UNCLOAKING:
                        Mark(MarkType.MARK_CHANGE);
                        if (Visual_Character(true) == VisualType.VISUAL_NORMAL)
                        {
                            CloakingDevice.Set_Rate((int)TdDefines.UNCLOAK_VIS_TIME);
                            CloakingDevice.Set_Stage(0);
                            Cloak = CloakType.UNCLOAKED;
                        }
                        break;
                    case CloakType.CLOAKING:
                        Mark(MarkType.MARK_CHANGE);
                        switch (Visual_Character(true))
                        {
                            case VisualType.VISUAL_DARKEN:
                                if (Health_Ratio() < 0x0040 && Game.Random_Pick(1, 3) == 1)
                                    Cloak = CloakType.UNCLOAKING;
                                break;
                            case VisualType.VISUAL_HIDDEN:
                                Cloak = CloakType.CLOAKED;
                                CloakingDevice.Set_Rate(0);
                                if (this is UnitClass { Flagged: not HousesType.HOUSE_NONE })
                                    Do_Shimmer();
                                else
                                    Detach_All(false);
                                if (What_Am_I() == RTTIType.RTTI_UNIT && !House.IsHuman)
                                    Scatter(0, true);
                                break;
                        }
                        break;
                    case CloakType.CLOAKED:
                        if (Is_Owned_By_Player())
                            Mark(MarkType.MARK_CHANGE);
                        break;
                }
            }
        }
        if (Arm != 0)
            Arm--;
        if (LineMaxFrames > 0 && LineFrame >= LineMaxFrames)
        {
            LineCount = 0;
            LineFrame = 0;
            LineMaxFrames = 0;
        }
        if (What_Am_I() != RTTIType.RTTI_BUILDING)
            if (Stage.Graphic_Logic() || Door.Time_To_Redraw())
                Mark(MarkType.MARK_CHANGE);
        if (Flasher.Process())
            Mark(MarkType.MARK_CHANGE);
    }

    public override bool Select(bool allowMixed = false)
    {
        if (!Is_Discovered_By_Player() && !Is_Owned_By_Player() && !Game.DebugUnshroud)
            return false;
        if (base.Select(allowMixed))
        {
            if (Is_Owned_By_Player() && Game.AllowVoice)
                Response_Select();
            return true;
        }
        return false;
    }

    // ---- Weapons ----

    public virtual FireErrorType Can_Fire(uint target, int which = 0)
    {
        if (!Game.Target_Legal(target))
            return FireErrorType.FIRE_ILLEGAL;
        var obj = Game.As_Object(target);
        if (obj is TechnoClass techno && techno.Is_Cloaked(this))
            return FireErrorType.FIRE_CANT;
        var weap = which == 0 ? Techno_Type_Class().Primary : Techno_Type_Class().Secondary;
        if (weap == WeaponType.WEAPON_NONE)
            return FireErrorType.FIRE_CANT;
        if (obj is AircraftClass aircraft && !Game.Types.Bullet(Game.Types.Weapon(weap).Fires).IsAntiAircraft && aircraft.Altitude > 0)
            return FireErrorType.FIRE_CANT;
        if (Arm != 0)
            return FireErrorType.FIRE_REARM;
        if (!In_Range(target, which))
            return FireErrorType.FIRE_RANGE;
        if (Ammo == 0)
            return FireErrorType.FIRE_AMMO;
        if (Cloak != CloakType.UNCLOAKED)
            return FireErrorType.FIRE_CLOAKED;
        return FireErrorType.FIRE_OK;
    }

    public virtual void Stun()
    {
        Assign_Target(Target.TARGET_NONE);
        Assign_Destination(Target.TARGET_NONE);
        Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
        Detach_All();
        Unselect_All_Players();
    }

    public virtual void Assign_Target(uint target)
    {
        if (target == TarCom)
            return;
        if (!Game.Target_Legal(target))
            target = Target.TARGET_NONE;
        else if (target == As_Target())
            target = Target.As_Target(TdMath.CoordCell(Coord));
        else if (Game.As_Object(target) is { } obj && (!obj.IsActive || obj.Strength == 0))
            target = Target.TARGET_NONE;
        TarCom = target;
    }

    public virtual int Rearm_Delay(bool second = true) =>
        second ? (int)(Game.Types.Weapon(Techno_Type_Class().Primary).ROF * House.ROFBias) + 3 : 9;

    /// <summary>Fire_At: fires a weapon at a target: the projectile, recoil, rearm delay, muzzle flash, laser lines.</summary>
    public virtual BulletClass? Fire_At(uint target, int which = 0)
    {
        var tclass = Techno_Type_Class();
        if ((which == 0 && tclass.Primary == WeaponType.WEAPON_NONE) || (which != 0 && tclass.Secondary == WeaponType.WEAPON_NONE))
            return null;
        var weapon = Game.Types.Weapon(which == 0 ? tclass.Primary : tclass.Secondary);
        if (Game.DebugMap || weapon.Fires == BulletType.BULLET_NONE || !Game.Target_Legal(target))
            return null;
        var btype = Game.Types.Bullet(weapon.Fires);
        var obj = Game.As_Object(target);
        uint targetCoord = obj != null ? obj.Target_Coord() : Game.As_Coord(target);
        uint fireCoord = Fire_Coord(which);
        byte dir;
        if (btype.IsHoming || btype.IsDropping)
        {
            dir = Fire_Direction();
            if (btype.IsDropping)
                fireCoord = Center_Coord();
        }
        else
            dir = TdMath.Direction(fireCoord, targetCoord);

        int firepower = weapon.Attack > 0 ? (int)(weapon.Attack * House.FirepowerBias) : 0;
        var bullet = BulletClass.Create(Game, weapon.Fires);
        if (bullet != null)
        {
            bullet.Assign_Target(target);
            bullet.Payback = this;
            bullet.Strength = (short)firepower;
            if (Game.Special.IsDefenderAdvantage && What_Am_I() != RTTIType.RTTI_BUILDING && ((FootClass)this).IsDriving)
                bullet.IsInaccurate = true;
            if (bullet.Unlimbo(fireCoord, dir))
            {
                bullet.Payback = this;
                bullet.Strength = (short)firepower;
            }
            else
                bullet.Delete();
            if (!bullet.Class.IsFueled)
                IsInRecoilState = true;
            Arm = Rearm_Delay(IsSecondShot);
            if (tclass.IsTwoShooter)
                IsSecondShot = !IsSecondShot;
            var a = weapon.Anim;
            if (a is AnimType.ANIM_GUN_N or AnimType.ANIM_CHEM_N or AnimType.ANIM_FLAME_N)
                a = (AnimType)((int)a + (int)TdMath.DirFacing(Fire_Direction()));
            if (a != AnimType.ANIM_NONE && AnimClass.Create(Game, a, Fire_Coord(which)) is { } anim)
                anim.Attach_To(this);
            if (Ammo > 0)
                Ammo--;
            Mark(MarkType.MARK_CHANGE);

            // A shot from an unseen object shows where it came from (the original's Sight_From one cell).
            if (Game.As_Object(target) is { } victim && Game.HouseFor(victim.Owner()) is { } player && (player.IsHuman || Game.GameToPlay != GameType.GAME_NORMAL))
                if ((!Is_Owned_By_Player(player) && !Is_Discovered_By_Player(player)) || !Game.Map[TdMath.CoordCell(Center_Coord())].Is_Mapped(House))
                    Game.Map.Sight_From(player, TdMath.CoordCell(Center_Coord()), 1, false);
        }
        if (weapon.Fires == BulletType.BULLET_LASER)
        {
            uint source = Fire_Coord(which), dest = Game.As_Coord(target);
            int x = TdMath.LeptonToPixel(TdMath.CoordX(source)), y = TdMath.LeptonToPixel(TdMath.CoordY(source));
            int x1 = TdMath.LeptonToPixel(TdMath.CoordX(dest)), y1 = TdMath.LeptonToPixel(TdMath.CoordY(dest));
            int[][] lines = [[x + 1, y, x1, y1, 0x7D], [x - 1, y, x1, y1, 0x7D], [x, y, x1, y1, 0x7F]];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 5; j++)
                    Lines[i, j] = lines[i][j];
            LineCount = 3;
            LineFrame = 0;
            LineMaxFrames = 5;
            SmudgeClass.Create(Game, (SmudgeType)Game.Random_Pick((int)SmudgeType.SMUDGE_SCORCH1, (int)SmudgeType.SMUDGE_SCORCH6), Game.As_Coord(target));
        }
        return bullet;
    }

    /// <summary>Player_Assign_Mission: the player's order, with a voice reply, sent through the event queue.</summary>
    public virtual void Player_Assign_Mission(MissionType mission, uint target = Target.TARGET_NONE, uint destination = Target.TARGET_NONE)
    {
        if (Game.AllowVoice)
        {
            if (mission == MissionType.MISSION_ATTACK)
                Response_Attack();
            else
                Response_Move();
        }
        Game.Queue_Mission(As_Target(), mission, target, destination);
    }

    /// <summary>What_Action(object): what a click on an object would do (modifier keys come from the game's input state).</summary>
    public override ActionType What_Action(ObjectClass obj)
    {
        if (obj == this && Game.CurrentObject.Count(Game.PlayerPtr.Class.House) == 1 && House == Game.PlayerPtr)
            return ActionType.ACTION_SELF;
        bool altdown = Game.Input.Alt, ctrldown = Game.Input.Ctrl, shiftdown = Game.Input.Shift;
        if (Is_Owned_By_Player() && ctrldown && altdown && Can_Player_Move() && Can_Player_Fire())
            return ActionType.ACTION_GUARD_AREA;
        if (altdown && Is_Owned_By_Player() && Can_Player_Move())
            return ActionType.ACTION_MOVE;
        bool isALoaner = obj is TechnoClass { IsALoaner: true };
        if (shiftdown && !isALoaner)
            return ActionType.ACTION_TOGGLE_SELECT;
        if (Is_Owned_By_Player() && (ctrldown || !House.Is_Ally(obj))
            && (ctrldown || obj.Class_Of().IsLegalTarget || (Game.Special.IsTreeTarget && obj.What_Am_I() == RTTIType.RTTI_TERRAIN)))
        {
            if (Can_Player_Move() || In_Range(obj, 0))
            {
                if (obj is not AircraftClass aircraft)
                    return ActionType.ACTION_ATTACK;
                if (aircraft.Class.Type != AircraftType.AIRCRAFT_CARGO)
                {
                    var t = Techno_Type_Class();
                    if ((t.Primary != WeaponType.WEAPON_NONE && Game.Types.Bullet(Game.Types.Weapon(t.Primary).Fires).IsAntiAircraft)
                        || (t.Secondary != WeaponType.WEAPON_NONE && Game.Types.Bullet(Game.Types.Weapon(t.Secondary).Fires).IsAntiAircraft)
                        || aircraft.Altitude == 0)
                        return ActionType.ACTION_ATTACK;
                }
            }
        }
        if (!Is_Weapon_Equipped() || !Is_Owned_By_Player() || obj.Owner() == Owner())
        {
            if ((!isALoaner || !Is_Owned_By_Player()) && obj.Class_Of().IsSelectable
                && (!obj.Is_Selected_By_Player() || Game.CurrentObject.Count(Game.PlayerPtr.Class.House) != 0))
                return ActionType.ACTION_SELECT;
            return ActionType.ACTION_NONE;
        }
        return ActionType.ACTION_NONE;
    }

    /// <summary>What_Action(cell): what a click on a map cell would do.</summary>
    public override ActionType What_Action(int cell)
    {
        var cellptr = Game.Map[cell];
        OverlayTypeClass? optr = cellptr.Overlay != OverlayType.OVERLAY_NONE ? Game.Types.Overlay(cellptr.Overlay) : null;
        bool altdown = Game.Input.Alt, ctrldown = Game.Input.Ctrl, shiftdown = Game.Input.Shift;
        if (What_Am_I() == RTTIType.RTTI_BUILDING)
            ctrldown = false;
        if (Is_Owned_By_Player() && ctrldown && altdown && Can_Player_Move() && Can_Player_Fire())
            return ActionType.ACTION_GUARD_AREA;
        if (Is_Owned_By_Player() && Techno_Type_Class().Primary != WeaponType.WEAPON_NONE && (ctrldown || (optr != null && optr.IsLegalTarget)))
        {
            var whead = Game.Types.Warhead(Game.Types.Bullet(Game.Types.Weapon(Techno_Type_Class().Primary).Fires).Warhead);
            if (optr == null || (optr.IsWall && (whead.IsWallDestroyer || (whead.IsWoodDestroyer && optr.IsWooden))))
                if (Can_Player_Move() || In_Range(Target.As_Target(cell), 0))
                    return ActionType.ACTION_ATTACK;
        }
        if (Is_Owned_By_Player() && Can_Player_Move())
        {
            if (shiftdown)
                return ActionType.ACTION_MOVE;
            if (Can_Enter_Cell(cell) <= MoveType.MOVE_CLOAK)
                return ActionType.ACTION_MOVE;
            return ActionType.ACTION_NOMOVE;
        }
        return ActionType.ACTION_NONE;
    }

    public override bool Can_Player_Move() => Game.PlayerPtr == House;
    public override bool Can_Player_Fire() => House.IsHuman && Techno_Type_Class().Primary != WeaponType.WEAPON_NONE;
    public virtual bool Is_Weapon_Equipped() => Techno_Type_Class().Primary != WeaponType.WEAPON_NONE;

    // ---- The remaster's AI strength ratings (from Red Alert) ----

    /// <summary>Anti_Air: anti-aircraft strength, for the computer's base defence planning.</summary>
    public int Anti_Air()
    {
        if (Is_Weapon_Equipped())
        {
            var weaponType = Techno_Type_Class().Primary;
            var weapon = Game.Types.Weapon(weaponType);
            var bullet = Game.Types.Bullet(weapon.Fires);
            var warhead = Game.Types.Warhead(bullet.Warhead);
            if (weaponType != WeaponType.WEAPON_NONE && bullet.IsAntiAircraft)
            {
                int value = ((weapon.Attack * warhead.Modifier[(int)ArmorType.ARMOR_ALUMINUM]) * weapon.Range) / weapon.ROF;
                return value / 50;
            }
        }
        return 0;
    }

    /// <summary>Anti_Armor: strength against vehicles (anti-aircraft weapons count none).</summary>
    public int Anti_Armor() => Anti_Ground(ArmorType.ARMOR_STEEL);

    /// <summary>Anti_Infantry: strength against soldiers (anti-aircraft weapons count none).</summary>
    public int Anti_Infantry() => Anti_Ground(ArmorType.ARMOR_NONE);

    int Anti_Ground(ArmorType armor)
    {
        if (Is_Weapon_Equipped())
        {
            var weaponType = Techno_Type_Class().Primary;
            if (weaponType != WeaponType.WEAPON_NONE && Game.Types.Bullet(Game.Types.Weapon(weaponType).Fires).IsAntiAircraft)
                return 0;
            var weapon = Game.Types.Weapon(weaponType);
            var bullet = Game.Types.Bullet(weapon.Fires);
            var warhead = Game.Types.Warhead(bullet.Warhead);
            int mrange = Math.Min(weapon.Range, 0x0400);
            int value = ((weapon.Attack * warhead.Modifier[(int)armor]) * mrange * warhead.SpreadFactor) / weapon.ROF;
            if (bullet.IsInaccurate)
                value /= 2;
            return value / 50;
        }
        return 0;
    }

    public override bool Can_Repair()
    {
        if (What_Am_I() is RTTIType.RTTI_UNIT or RTTIType.RTTI_INFANTRY or RTTIType.RTTI_AIRCRAFT)
            return false;
        return Techno_Type_Class().IsRepairable && Strength != Class_Of().MaxStrength;
    }

    public override int Weapon_Range(int which = 0)
    {
        var ttype = Techno_Type_Class();
        var weapon = which switch { 0 => ttype.Primary, 1 => ttype.Secondary, _ => WeaponType.WEAPON_NONE };
        if (weapon != WeaponType.WEAPON_NONE)
        {
            if (weapon == WeaponType.WEAPON_NIKE && Game.GameToPlay == GameType.GAME_NORMAL)
                return Game.Types.Weapon(weapon).Range * 2;
            return Game.Types.Weapon(weapon).Range;
        }
        return 0;
    }

    public override void Override_Mission(MissionType mission, uint tarcom, uint navcom)
    {
        SuspendedTarCom = TarCom;
        base.Override_Mission(mission, tarcom, navcom);
        Assign_Target(tarcom);
    }

    public override bool Restore_Mission()
    {
        if (base.Restore_Mission())
        {
            Assign_Target(SuspendedTarCom);
            return true;
        }
        return false;
    }

    /// <summary>Captured: changes owner (an engineer took it): counts as a kill for the new owner.</summary>
    public virtual bool Captured(HouseClass newOwner)
    {
        if (newOwner != House)
        {
            Trigger?.Spring(EventType.EVENT_PLAYER_ENTERED, this);
            Record_The_Kill(null);
            House.Tracking_Remove(this);
            newOwner.Tracking_Add(this);
            switch (What_Am_I())
            {
                case RTTIType.RTTI_BUILDING:
                    newOwner.BuildingsKilled[(int)Owner()]++;
                    break;
                case RTTIType.RTTI_AIRCRAFT:
                case RTTIType.RTTI_INFANTRY:
                case RTTIType.RTTI_UNIT:
                    newOwner.UnitsKilled[(int)Owner()]++;
                    break;
            }
            House.WhoLastHurtMe = newOwner.Class.House;
            Detach_All(false);
            House = newOwner;
            IsOwnedByPlayer = House == Game.PlayerPtr;
            return true;
        }
        return false;
    }

    public override ResultType Take_Damage(ref int damage, int distance, WarheadType warhead, TechnoClass? source = null)
    {
        if (damage > 0)
            damage = (int)(damage * House.ArmorBias);
        var result = base.Take_Damage(ref damage, distance, warhead, source);
        switch (result)
        {
            case ResultType.RESULT_DESTROYED:
                Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
                Stun();
                break;
            case ResultType.RESULT_NONE:
                break;
            default:
                if (source != null && !House.Is_Ally(source))
                    IsTickedOff = true;
                Do_Shimmer();
                break;
        }
        return result;
    }

    public override void Record_The_Kill(TechnoClass? source)
    {
        if (Trigger != null && source != null)
            Trigger.Spring(EventType.EVENT_ATTACKED, this);
        if (Trigger != null && source != null)
            Trigger.Spring(EventType.EVENT_DISCOVERED, this);
        Trigger?.Spring(EventType.EVENT_DESTROYED, this);
        if (source != null)
        {
            source.Made_A_Kill();
            House.WhoLastHurtMe = source.Owner();
        }
        switch (What_Am_I())
        {
            case RTTIType.RTTI_BUILDING:
                if (((BuildingClass)this).WhoLastHurtMe != HousesType.HOUSE_NONE)
                    House.BuildingsLost++;
                if (source != null)
                    source.House.BuildingsKilled[(int)Owner()]++;
                break;
            case RTTIType.RTTI_AIRCRAFT:
            case RTTIType.RTTI_INFANTRY:
            case RTTIType.RTTI_UNIT:
                House.UnitsLost++;
                if (source != null)
                    source.House.UnitsKilled[(int)Owner()]++;
                break;
        }
    }

    /// <summary>Nearby_Location: the nearest generally clear cell, searching rings outward.</summary>
    public int Nearby_Location(TechnoClass? from = null)
    {
        int best = 0;
        int cell = TdMath.CoordCell(Center_Coord());
        for (int radius = 0; radius < TdMath.MapCellW / 2; radius++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                int newcell = cell + TdMath.XyCellOffset(x, -radius);
                if (Game.Map.In_Radar(newcell) && Game.Map[newcell].Is_Generally_Clear())
                    best = newcell;
                newcell = cell + TdMath.XyCellOffset(x, radius);
                if (Game.Map.In_Radar(newcell) && Game.Map[newcell].Is_Generally_Clear())
                    best = newcell;
            }
            for (int y = -(radius - 1); y < radius; y++)
            {
                int newcell = cell + TdMath.XyCellOffset(-radius, y);
                if (Game.Map.In_Radar(newcell) && Game.Map[newcell].Is_Generally_Clear())
                    best = newcell;
                newcell = cell + TdMath.XyCellOffset(radius, y);
                if (Game.Map.In_Radar(newcell) && Game.Map[newcell].Is_Generally_Clear())
                    best = newcell;
            }
            if (best != 0)
                break;
        }
        return best;
    }

    // ---- Cloaking ----

    public virtual void Do_Uncloak()
    {
        if (IsCloakable && Cloak is CloakType.CLOAKED or CloakType.CLOAKING)
        {
            Game.Sound_Effect(VocType.VOC_CLOAK, Coord);
            Cloak = CloakType.UNCLOAKING;
            CloakingDevice.Set_Stage(0);
            CloakingDevice.Set_Rate(1);
        }
    }

    public virtual void Do_Cloak()
    {
        if (IsCloakable && Cloak is CloakType.UNCLOAKED or CloakType.UNCLOAKING)
        {
            Game.Sound_Effect(VocType.VOC_CLOAK, Coord);
            Detach_All(false);
            Cloak = CloakType.CLOAKING;
            CloakingDevice.Set_Stage(0);
            CloakingDevice.Set_Rate(1);
        }
    }

    public override void Do_Shimmer()
    {
        if (IsCloakable && Cloak == CloakType.CLOAKED)
        {
            Cloak = CloakType.CLOAKING;
            CloakingDevice.Set_Stage((int)TdDefines.MAX_UNCLOAK_STAGE / 2);
            CloakingDevice.Set_Rate(1);
        }
    }

    /// <summary>Visual_Character: how visible the object is while cloaking (the client draws by this).</summary>
    public VisualType Visual_Character(bool raw = false)
    {
        if (Cloak == CloakType.UNCLOAKED || Game.DebugMap)
            return VisualType.VISUAL_NORMAL;
        if (Cloak == CloakType.CLOAKED)
        {
            if (!raw && Is_Owned_By_Player())
                return VisualType.VISUAL_SHADOWY;
            return VisualType.VISUAL_HIDDEN;
        }
        int stage = CloakingDevice.Fetch_Stage();
        if (Cloak == CloakType.UNCLOAKING)
            stage = (int)TdDefines.MAX_UNCLOAK_STAGE - stage;
        if (stage <= 0)
            return VisualType.VISUAL_NORMAL;
        stage = (int)TdMath.CardinalToFixed((uint)TdDefines.MAX_UNCLOAK_STAGE, (uint)stage);
        if (stage < 0x0040) return VisualType.VISUAL_INDISTINCT;
        if (stage < 0x0080) return VisualType.VISUAL_DARKEN;
        if (stage < 0x00C0) return VisualType.VISUAL_SHADOWY;
        if (!raw && Is_Owned_By_Player()) return VisualType.VISUAL_SHADOWY;
        if (stage < 0x00FF) return VisualType.VISUAL_RIPPLE;
        return VisualType.VISUAL_HIDDEN;
    }

    // ---- Other ----

    public override void Detach(uint target, bool all)
    {
        base.Detach(target, all);
        if (SuspendedMission != MissionType.MISSION_NONE && SuspendedTarCom == target)
        {
            SuspendedMission = MissionType.MISSION_NONE;
            SuspendedTarCom = Target.TARGET_NONE;
        }
        if (TarCom == target)
        {
            Assign_Target(Target.TARGET_NONE);
            Restore_Mission();
        }
        if (all && In_Radio_Contact() && Contact_With_Whom()!.As_Target() == target)
            Transmit_Message(RadioMessageType.RADIO_OVER_OUT);
    }

    public void Kill_Cargo(TechnoClass? source)
    {
        while (Cargo.Is_Something_Attached())
        {
            var foot = Cargo.Detach_Object();
            if (foot != null)
            {
                foot.Record_The_Kill(source);
                foot.Delete();
            }
        }
    }

    public virtual InfantryType Crew_Type()
    {
        var infantry = InfantryType.INFANTRY_E1;
        if (House.ActLike == HousesType.HOUSE_NEUTRAL)
            infantry = (InfantryType)Game.Random_Pick((int)InfantryType.INFANTRY_C1, (int)InfantryType.INFANTRY_C9);
        else if (Techno_Type_Class().Primary == WeaponType.WEAPON_NONE && Game.Random_Pick(0, 6) == 1)
            infantry = Game.Random_Pick(0, 1) == 0 ? InfantryType.INFANTRY_C1 : InfantryType.INFANTRY_C7;
        return infantry;
    }

    public override int Value()
    {
        int value = 0;
        if (Game.BuildLevel > 8 || Game.GameToPlay != GameType.GAME_NORMAL)
            for (var obj = Cargo.Attached_Object(); obj != null; obj = obj.Next as FootClass)
                value += obj.Value();
        return Risk() + Techno_Type_Class().Reward + value;
    }

    public virtual int Threat_Range(int control)
    {
        if (control == -1)
            return -1;
        if (control == 0)
            return 0;
        int range = Math.Max(Weapon_Range(0), Weapon_Range(1)) * 2;
        return Math.Clamp(range, 0x0000, 0x0A00);
    }

    /// <summary>Base_Is_Attacked: a computer player's unarmed building calls for the strongest nearby defenders.</summary>
    public void Base_Is_Attacked(TechnoClass? enemy)
    {
        var defender = new FootClass?[6];
        var value = new int[6];
        int count = 0, weakest = 0;
        if (enemy == null || House.Is_Ally(enemy) || House.IsHuman)
            return;
        int desired = enemy.Risk() * 2, risktotal = 0;
        if (Techno_Type_Class().Primary != WeaponType.WEAPON_NONE)
            return;
        if (enemy.What_Am_I() != RTTIType.RTTI_INFANTRY && enemy.What_Am_I() != RTTIType.RTTI_UNIT)
            return;
        if (enemy is UnitClass { Class.Type: UnitType.UNIT_GUNBOAT or UnitType.UNIT_HOVER })
            return;
        if (!((FootClass)enemy).BaseAttackTimer.Expired)
            return;
        Scenario.TeamClass.Suspend_Teams(Game, 20);

        void Consider(FootClass foot)
        {
            if (foot.Owner() != Owner())
                return;
            if (foot.Mission is MissionType.MISSION_STICKY or MissionType.MISSION_SLEEP)
                return;
            int threat = foot.Rescue_Mission(enemy!.As_Target());
            if (threat == 0)
                return;
            if (threat < 0)
            {
                desired += threat;
                return;
            }
            if (count < 6)
            {
                defender[count] = foot;
                value[count] = threat;
                count++;
                return;
            }
            if (threat > weakest)
            {
                int newweakest = threat;
                for (int lp = 0; lp < count; lp++)
                {
                    if (value[lp] == weakest)
                    {
                        value[lp] = threat;
                        defender[lp] = foot;
                        continue;
                    }
                    // (The original compares value[count] here, one past the filled entries: count is 6, so index 6.)
                    if (count < value.Length && value[count] < newweakest)
                        newweakest = value[lp];
                }
                weakest = newweakest;
            }
        }

        for (int index = 0; index < Game.Infantry.Count && desired > 0; index++)
            Consider(Game.Infantry.Ptr(index));
        for (int index = 0; index < Game.Units.Count && desired > 0; index++)
            Consider(Game.Units.Ptr(index));

        if (desired > 0)
        {
            for (int lp = 0; lp < count - 1; lp++)
                for (int lp2 = lp + 1; lp2 < count; lp2++)
                    if (value[lp] < value[lp2])
                    {
                        (value[lp], value[lp2]) = (value[lp2], value[lp]);
                        (defender[lp], defender[lp2]) = (defender[lp2], defender[lp]);
                    }
            for (int lp = 0; lp < count; lp++)
            {
                defender[lp]!.Assign_Mission(MissionType.MISSION_RESCUE);
                defender[lp]!.Assign_Target(enemy.As_Target());
                risktotal += defender[lp]!.Risk();
                if (risktotal > desired)
                    break;
            }
        }
        if (risktotal > desired)
            ((FootClass)enemy).BaseAttackTimer.Set(15 * 15);
    }

    public virtual bool Target_Something_Nearby(ThreatType threat = ThreatType.THREAT_NORMAL)
    {
        threat &= ThreatType.THREAT_RANGE | ThreatType.THREAT_AREA;
        if (Game.Target_Legal(TarCom) && (threat & ThreatType.THREAT_RANGE) != 0 && !In_Range(TarCom))
            Assign_Target(Target.TARGET_NONE);
        if (!Game.Target_Legal(TarCom))
            Assign_Target(Greatest_Threat(threat));
        return Game.Target_Legal(TarCom);
    }

    /// <summary>Find_Docking_Bay: the nearest (or primary) building of a kind that will take this object.</summary>
    public virtual BuildingClass? Find_Docking_Bay(StructType b, bool friendly)
    {
        BuildingClass? best = null;
        if ((House.BScan & (1L << (int)b)) != 0)
        {
            int bestval = -1;
            for (int index = 0; index < Game.Buildings.Count; index++)
            {
                var building = Game.Buildings.Ptr(index);
                if ((friendly ? building.House.Is_Ally(this) : building.House == House) && !building.IsInLimbo && building.Class.Type == b
                    && Transmit_Message(RadioMessageType.RADIO_CAN_LOAD, building) == RadioMessageType.RADIO_ROGER)
                {
                    if (bestval == -1 || Distance(building) < bestval || building.IsLeader)
                    {
                        best = building;
                        bestval = Distance(building);
                        if (building.IsLeader)
                            break;
                    }
                }
            }
        }
        return best;
    }

    public virtual int Find_Exit_Cell(TechnoClass? techno) => TdMath.CoordCell(Docking_Coord());

    public virtual int Refund_Amount()
    {
        int cost = (int)(Techno_Type_Class().Raw_Cost(Game.Types, Game.Special) * House.CostBias);
        cost += (int)TdMath.FixedToCardinal(UnitTypeClass.FULL_LOAD_CREDITS, (uint)Tiberium_Load()) / 2;
        if (House.IsHuman)
            cost /= 2;
        return cost;
    }

    public bool Is_Discovered_By_Player(HouseClass? player = null)
    {
        player ??= Game.PlayerPtr;
        return (IsDiscoveredByPlayerMask & (1 << (int)player.Class.House)) != 0;
    }

    public void Set_Discovered_By_Player(HouseClass? player = null)
    {
        int shift = (int)(player ?? Game.PlayerPtr).Class.House;
        IsDiscoveredByPlayerMask |= 1 << shift;
        if (Game.GameToPlay == GameType.GAME_NORMAL && player == Game.PlayerPtr)
            IsDiscoveredByPlayer = true;
    }

    public void Clear_Discovered_By_Players()
    {
        IsDiscoveredByPlayerMask = 0;
        IsDiscoveredByPlayer = false;
    }

    public bool Is_Owned_By_Player(HouseClass? player = null) => player == null ? House == Game.PlayerPtr : House == player;

    public bool Is_Cloaked(HousesType house) => !House.Is_Ally(house) && Cloak == CloakType.CLOAKED;
    public bool Is_Cloaked(HouseClass house) => !House.Is_Ally(house) && Cloak == CloakType.CLOAKED;
    public bool Is_Cloaked(ObjectClass obj) => !House.Is_Ally(obj) && Cloak == CloakType.CLOAKED;
}

using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic.Objects;

/// <summary>
/// AbstractClass (ABSTRACT.H/CPP): anything with a position in the world. The game it belongs to
/// stands in for the original's globals (Map, Logic, the object heaps, PlayerPtr...).
/// </summary>
public abstract class AbstractClass
{
    public TdGame Game { get; }

    public uint Coord;
    public bool IsActive;
    public bool IsRecentlyCreated;

    protected AbstractClass(TdGame game) => Game = game;

    public virtual HousesType Owner() => HousesType.HOUSE_NONE;
    public virtual uint Center_Coord() => Coord;
    public virtual uint Target_Coord() => Coord;

    public byte Direction(AbstractClass obj) => TdMath.Direction(Center_Coord(), obj.Target_Coord());
    public byte Direction(uint coord) => TdMath.Direction(Center_Coord(), coord);
    public byte DirectionToTarget(uint target) => TdMath.Direction(Center_Coord(), Game.As_Coord(target));
    public byte DirectionToCell(int cell) => TdMath.CellDirection(TdMath.CoordCell(Center_Coord()), cell);

    /// <summary>AbstractClass::Distance(TARGET): to the target's coordinate, less a building's average radius.</summary>
    public int DistanceToTarget(uint target)
    {
        var building = Game.As_Building(target);
        int distance = Distance(Game.As_Coord(target));
        if (building != null && building.IsActive)
        {
            distance -= (building.Class.Width() + building.Class.Height()) * (0x100 / 4);
            if (distance < 0)
                distance = 0;
        }
        return distance;
    }

    public int Distance(uint coord) => TdMath.Distance(Center_Coord(), coord);
    public int DistanceToCell(int cell) => TdMath.CellDistance(TdMath.CoordCell(Center_Coord()), cell);
    public int Distance(AbstractClass obj) => TdMath.Distance(Center_Coord(), obj.Target_Coord());

    public virtual MoveType Can_Enter_Cell(int cell, FacingType facing = FacingType.FACING_NONE) => MoveType.MOVE_OK;
    public virtual void AI() { }
    public void Set_Active()
    {
        IsActive = true;
        IsRecentlyCreated = true;
    }

    /// <summary>delete: the destructors run (most derived first), then operator delete frees the
    /// heap slot. The object itself stays readable, as freed heap memory did in the original.</summary>
    public void Delete()
    {
        Destruct();
        Release();
    }

    /// <summary>Delete_This: "delete this" (the original keeps the vtable for stale pointers).</summary>
    public void Delete_This() => Delete();

    /// <summary>A destructor body; overrides call base.Destruct() last, as C++ destroys bases after.</summary>
    protected virtual void Destruct() { }

    /// <summary>operator delete: inactive, and the heap slot freed (each kind frees from its heap).</summary>
    protected virtual void Release() => IsActive = false;
}

/// <summary>
/// ObjectClass (OBJECT.H/CPP): anything that can be placed on the map: buildings, units, trees,
/// projectiles, animations. Handles limbo (off the map) and placement, marking cells as occupied,
/// selection, damage and the radio messages every object answers.
/// </summary>
public abstract class ObjectClass : AbstractClass
{
    public bool IsDown;
    public bool IsToDamage;
    public bool IsToDisplay;
    public bool IsInLimbo = true;
    public bool IsSelected;
    public int IsSelectedMask;
    public bool IsAnimAttached;
    /// <summary>The next object in the same cell (the cell's occupier list).</summary>
    public ObjectClass? Next;
    public Scenario.TriggerClass? Trigger;
    public int Strength = 255;

    protected ObjectClass(TdGame game) : base(game) => Coord = 0xFFFFFFFF;

    public virtual RTTIType What_Am_I() => RTTIType.RTTI_OBJECT;
    public abstract ObjectTypeClass Class_Of();

    public virtual ActionType What_Action(ObjectClass obj) => ActionType.ACTION_NONE;
    public virtual ActionType What_Action(int cell) => ActionType.ACTION_NONE;
    public virtual LayerType In_Which_Layer() => LayerType.LAYER_GROUND;
    public virtual bool Is_Infantry() => false;
    public virtual bool Is_Techno() => false;
    public virtual int Get_Ownable() => 0xFF;
    public virtual bool Can_Repair() => false;
    public virtual bool Can_Demolish() => false;
    public virtual bool Can_Demolish_Unit() => false;
    public virtual bool Can_Capture() => false;
    public virtual bool Can_Player_Fire() => false;
    public virtual bool Can_Player_Move() => false;
    public override uint Target_Coord() => Center_Coord();
    public virtual uint Render_Coord() => Center_Coord();
    public virtual uint Docking_Coord() => Center_Coord();
    public virtual uint Sort_Y() => Coord;
    public virtual uint Fire_Coord(int which) => Coord;
    public virtual void Record_The_Kill(TechnoClass? source) { }
    public virtual void Do_Shimmer() { }
    public virtual int Exit_Object(TechnoClass obj) => 0;
    public virtual void Hidden() { }
    public virtual void Look(bool incremental = false) { }
    public virtual void Active_Click_With(ActionType action, ObjectClass obj) { }
    public virtual void Active_Click_With(ActionType action, int cell) { }
    public virtual void Clicked_As_Target(HousesType house, int count = 7) { }
    /// <summary>In_Range(COORDINATE): named apart from In_Range(TARGET), since both are uint here.</summary>
    public virtual bool In_Range_Coord(uint coord, int which = 0) => false;
    public virtual int Weapon_Range(int which = 0) => 0;
    public virtual uint As_Target() => Target.TARGET_NONE;
    public virtual void Scatter(uint threat, bool forced = false, bool nokidding = false) { }
    public virtual bool Catch_Fire() => false;
    public virtual void Fire_Out() { }
    public virtual int Value() => 0;
    public virtual MissionType Get_Mission() => MissionType.MISSION_NONE;
    public virtual void Repair(int control) { }
    public virtual void Sell_Back(int control) { }
    public virtual void Detach(uint target, bool all) { }
    public virtual BuildingClass? Who_Can_Build_Me(bool intheory, bool legal) => null;
    public virtual bool Revealed(HouseClass? house) => house != null;
    public virtual short[] Occupy_List(bool placement = false) => Class_Of().Occupy_List(Game.Special, placement);
    public virtual short[] Overlap_List() => Class_Of().Overlap_List();

    /// <summary>Health_Ratio: strength as a 0..256 fraction of the maximum.</summary>
    public virtual uint Health_Ratio() => TdMath.CardinalToFixed((uint)Class_Of().MaxStrength, (uint)Strength);

    public virtual int Full_Name() => Class_Of().Full_Name(Game.Special);

    /// <summary>Moves one cell in a direction if it can enter it (ObjectClass::Move).</summary>
    public virtual void Move(FacingType facing)
    {
        Mark(MarkType.MARK_UP);
        uint coord = TdMath.AdjacentCellCoord(Coord, (TdMath.Facing)facing);
        if (Can_Enter_Cell(TdMath.CoordCell(coord)) == MoveType.MOVE_OK)
            Coord = coord;
        Mark(MarkType.MARK_DOWN);
    }

    // ---- Selection ----

    public void Unselect()
    {
        if (Is_Selected_By_Player())
        {
            if (In_Which_Layer() == LayerType.LAYER_GROUND)
                Mark(MarkType.MARK_OVERLAP_UP);
            Set_Unselected_By_Player();
            if (In_Which_Layer() == LayerType.LAYER_GROUND)
                Mark(MarkType.MARK_OVERLAP_DOWN);
        }
    }

    public void Unselect_All_Players()
    {
        Game.CurrentObject.Delete_All(this);
        if (In_Which_Layer() == LayerType.LAYER_GROUND)
            Mark(MarkType.MARK_OVERLAP_UP);
        IsSelected = false;
        IsSelectedMask = 0;
        if (In_Which_Layer() == LayerType.LAYER_GROUND)
            Mark(MarkType.MARK_OVERLAP_DOWN);
    }

    public void Unselect_All_Players_Except_Owner()
    {
        Game.CurrentObject.Delete_All_Except(this, Owner());
        if (In_Which_Layer() == LayerType.LAYER_GROUND)
            Mark(MarkType.MARK_OVERLAP_UP);
        int ownerMask = 1 << (int)Owner();
        if ((IsSelectedMask & ownerMask) != 0)
        {
            IsSelected = true;
            IsSelectedMask = ownerMask;
        }
        else
        {
            IsSelected = false;
            IsSelectedMask = 0;
        }
        if (In_Which_Layer() == LayerType.LAYER_GROUND)
            Mark(MarkType.MARK_OVERLAP_DOWN);
    }

    public virtual bool Select(bool allowMixed = false)
    {
        if (!Game.DebugMap && (Is_Selected_By_Player() || !Class_Of().IsSelectable))
            return false;
        if (Can_Player_Move() && this is TechnoClass { IsALoaner: true })
            return false;
        if (Game.PendingObject != null)
            return false;
        if (!allowMixed)
            foreach (var other in Game.CurrentObject.Of(Game.PlayerPtr.Class.House))
                if (Owner() != other.Owner())
                {
                    Game.Unselect_All();
                    break;
                }
        if (In_Which_Layer() == LayerType.LAYER_GROUND)
            Mark(MarkType.MARK_OVERLAP_UP);
        Set_Selected_By_Player();
        if (In_Which_Layer() == LayerType.LAYER_GROUND)
            Mark(MarkType.MARK_OVERLAP_DOWN);
        return true;
    }

    public void Set_Selected_By_Player(HouseClass? player = null)
    {
        player ??= Game.PlayerPtr;
        var house = player.Class.House;
        if (Class_Of() is TechnoTypeClass { IsLeader: true })
            Game.CurrentObject.Add_Head(house, this);
        else
            Game.CurrentObject.Add(house, this);
        IsSelectedMask |= 1 << (int)house;
        if (Game.GameToPlay == GameType.GAME_NORMAL && player == Game.PlayerPtr)
            IsSelected = true;
    }

    public void Set_Unselected_By_Player(HouseClass? player = null)
    {
        player ??= Game.PlayerPtr;
        var house = player.Class.House;
        Game.CurrentObject.Delete(house, this);
        IsSelectedMask &= ~(1 << (int)house);
        if (Game.GameToPlay == GameType.GAME_NORMAL && player == Game.PlayerPtr)
            IsSelected = false;
    }

    public bool Is_Selected_By_Player(HouseClass? player = null)
    {
        player ??= Game.PlayerPtr;
        return (IsSelectedMask & (1 << (int)player.Class.House)) != 0;
    }

    // ---- Being on the map ----

    /// <summary>Mark_For_Redraw: the client redraws from the logic every frame, so this only keeps the flag.</summary>
    void Mark_For_Redraw()
    {
        if (!IsToDisplay)
            IsToDisplay = true;
    }

    /// <summary>Limbo: takes the object off the map (into a transport, a factory, or before deletion).</summary>
    public virtual bool Limbo()
    {
        if (Game.GameActive && !IsInLimbo)
        {
            Unselect_All_Players();
            Detach_All();
            Mark(MarkType.MARK_UP);
            Game.Map.Remove(this, In_Which_Layer());
            if (Class_Of().IsSentient)
                Game.Logic.Delete(this);
            Hidden();
            IsInLimbo = true;
            IsToDisplay = false;
            return true;
        }
        return false;
    }

    /// <summary>Unlimbo: puts the object on the map at a coordinate (if it may enter the cell).</summary>
    public virtual bool Unlimbo(uint coord, byte facing = 0)
    {
        if (Game.GameActive && IsInLimbo && !IsDown)
        {
            if (Game.ScenarioInit != 0 || Can_Enter_Cell(TdMath.CoordCell(coord), FacingType.FACING_NONE) == MoveType.MOVE_OK)
            {
                IsInLimbo = false;
                IsToDisplay = false;
                Coord = Class_Of().Coord_Fixup(coord);
                if (Mark(MarkType.MARK_DOWN))
                {
                    if (IsActive)
                    {
                        if (In_Which_Layer() != LayerType.LAYER_NONE)
                            Game.Map.Submit(this, In_Which_Layer());
                        if (Class_Of().IsSentient)
                            Game.Logic.Submit(this);
                    }
                    return true;
                }
            }
        }
        return false;
    }

    public virtual void Detach_All(bool all = true)
    {
        if (all)
            Unselect_All_Players();
        else
            Unselect_All_Players_Except_Owner();
        Game.Map.Detach(this);
        Game.Detach_This_From_All(As_Target(), all);
    }

    public virtual RadioMessageType Receive_Message(RadioClass? from, RadioMessageType message, ref int param)
    {
        if (message == RadioMessageType.RADIO_REDRAW)
        {
            Mark(MarkType.MARK_CHANGE);
            return RadioMessageType.RADIO_ROGER;
        }
        return RadioMessageType.RADIO_STATIC;
    }

    /// <summary>Take_Damage: armour-modified damage; reports half strength, near death and destruction.</summary>
    public virtual ResultType Take_Damage(ref int damage, int distance, WarheadType warhead, TechnoClass? source = null)
    {
        var result = ResultType.RESULT_NONE;
        int oldStrength = Strength;
        if (oldStrength != 0 && damage != 0 && !Class_Of().IsImmune)
        {
            int maxStrength = Class_Of().MaxStrength;
            damage = Game.Modify_Damage(damage, warhead, Class_Of().Armor, distance);
            if (damage == 0)
                return ResultType.RESULT_NONE;
            result = ResultType.RESULT_LIGHT;
            if (oldStrength > damage)
            {
                if (oldStrength >= (maxStrength >> 1) && (oldStrength - damage) < (maxStrength >> 1))
                    result = ResultType.RESULT_HALF;
            }
            else
                damage = oldStrength;
            Strength = oldStrength - damage;
            switch (Strength)
            {
                case 0:
                    Record_The_Kill(source);
                    result = ResultType.RESULT_DESTROYED;
                    Detach_All();
                    break;
                case 1:
                    result = ResultType.RESULT_MAJOR;
                    break;
            }
            if (source != null && Trigger != null && result != ResultType.RESULT_DESTROYED)
                Trigger.Spring(EventType.EVENT_ATTACKED, this);
            if (result != ResultType.RESULT_NONE && Is_Selected_By_Player())
                Mark(MarkType.MARK_CHANGE);
        }
        return result;
    }

    /// <summary>
    /// Mark: puts the object down on its cells (MARK_DOWN), lifts it (MARK_UP), or flags a redraw;
    /// techno objects also add or remove their threat from the cell in single-player games.
    /// </summary>
    public virtual bool Mark(MarkType mark = MarkType.MARK_CHANGE)
    {
        if (IsInLimbo || !IsActive)
            return false;
        if (mark == MarkType.MARK_CHANGE)
        {
            if (IsToDisplay)
                return false;
            if (IsDown)
            {
                Mark_For_Redraw();
                return true;
            }
            return false;
        }
        if (mark == MarkType.MARK_OVERLAP_UP && IsDown)
        {
            Game.Map.Overlap_Up(TdMath.CoordCell(Coord), this);
            Mark_For_Redraw();
            return true;
        }
        if (mark == MarkType.MARK_OVERLAP_DOWN && IsDown)
        {
            Game.Map.Overlap_Down(TdMath.CoordCell(Coord), this);
            Mark_For_Redraw();
            return true;
        }
        var tech = this as TechnoClass;
        int threat = tech?.Risk() ?? 0;
        var house = tech?.Owner() ?? HousesType.HOUSE_NONE;
        int cell = TdMath.CoordCell(Coord);
        if (mark == MarkType.MARK_DOWN && !IsDown)
        {
            if (tech != null && Game.GameToPlay == GameType.GAME_NORMAL)
                Game.Map[cell].Adjust_Threat(house, threat);
            IsDown = true;
            Mark_For_Redraw();
            return true;
        }
        if (mark == MarkType.MARK_UP && IsDown)
        {
            if (tech != null && Game.GameToPlay == GameType.GAME_NORMAL)
                Game.Map[cell].Adjust_Threat(house, -threat);
            Game.Map.Overlap_Up(TdMath.CoordCell(Coord), this);
            IsDown = false;
            return true;
        }
        return false;
    }
}

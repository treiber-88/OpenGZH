using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic;

/// <summary>
/// FactoryClass (FACTORY.CPP): one thing being built — the object (or special weapon), progress
/// in 108 steps, and the money still to pay (paid a step at a time; production waits for money).
/// </summary>
public sealed class FactoryClass
{
    public const int STEP_COUNT = 108;

    readonly TdGame game;
    readonly StageClass stage = new();
    public bool IsActive;
    bool isSuspended, isDifferent, isBlocked;
    int balance;
    TechnoClass? obj;
    int specialItem;
    HouseClass? house;

    FactoryClass(TdGame game)
    {
        this.game = game;
        isSuspended = false;
        isDifferent = false;
        isBlocked = false;
        balance = 0;
        specialItem = (int)SpecialWeaponType.SPC_NONE;
        stage.Set_Rate(0);
        stage.Set_Stage(0);
    }

    /// <summary>new FactoryClass: a slot in the factory heap (null when full).</summary>
    public static FactoryClass? Create(TdGame game)
    {
        int slot = game.Factories.Allocate();
        if (slot < 0)
            return null;
        var f = new FactoryClass(game);
        game.Factories.Place(slot, f);
        f.IsActive = true;
        return f;
    }

    /// <summary>delete: (the destructor abandons production while a game is running).</summary>
    public void Delete()
    {
        if (game.GameActive)
            Abandon();
        IsActive = false;
        game.Factories.Free(this);
    }

    public bool Is_Building() => stage.Fetch_Rate() != 0;
    public HouseClass? Get_House() => house;
    public bool Is_Blocked() => isBlocked;
    public void Set_Is_Blocked(bool set) => isBlocked = set;
    public TechnoClass? Get_Object() => obj;
    public int Get_Special_Item() => specialItem;
    public int Completion() => stage.Fetch_Stage();

    /// <summary>AI: production advances (faster with more factories of the kind, for humans) while money lasts.</summary>
    public void AI()
    {
        if (!isSuspended && (obj != null || specialItem != 0))
        {
            int stages = 1;
            if (obj != null && house!.IsHuman)
            {
                stages = obj.What_Am_I() switch
                {
                    RTTIType.RTTI_AIRCRAFT => house.AircraftFactories,
                    RTTIType.RTTI_INFANTRY => house.InfantryFactories,
                    RTTIType.RTTI_UNIT => house.UnitFactories,
                    RTTIType.RTTI_BUILDING => house.BuildingFactories,
                    _ => stages,
                };
                stages = Math.Max(stages, 1);
            }

            for (int index = 0; index < stages; index++)
            {
                if (!Has_Completed() && stage.Graphic_Logic())
                {
                    isDifferent = true;

                    int cost = Math.Min(Cost_Per_Tick(), balance);
                    if (cost > house!.Available_Money())
                    {
                        stage.Set_Stage(stage.Fetch_Stage() - 1);
                    }
                    else
                    {
                        house.Spend_Money(cost);
                        balance -= cost;
                    }

                    if (game.DebugInstantBuild)
                        stage.Set_Stage(STEP_COUNT);

                    if (stage.Fetch_Stage() == STEP_COUNT)
                    {
                        isSuspended = true;
                        stage.Set_Rate(0);
                        house.Spend_Money(balance);
                        balance = 0;
                    }
                }
            }
        }
    }

    public void Force_Complete()
    {
        if (!isSuspended && (obj != null || specialItem != 0))
        {
            stage.Set_Stage(STEP_COUNT);
            isSuspended = true;
            stage.Set_Rate(0);
            balance = 0;
            isDifferent = true;
        }
    }

    public bool Has_Changed()
    {
        bool changed = isDifferent;
        isDifferent = false;
        return changed;
    }

    /// <summary>Set: build an object of this type (made now, in limbo; the price scaled by the house's cost bias).</summary>
    public bool Set(TechnoTypeClass type, HouseClass h)
    {
        Abandon();
        isDifferent = true;
        isSuspended = true;
        stage.Set_Rate(0);
        stage.Set_Stage(0);
        obj = game.Create_One_Of(type, h) as TechnoClass;
        if (obj != null)
        {
            house = obj.House;
            balance = (int)(type.Cost_Of(game.Special) * h.CostBias);
            obj.PurchasePrice = balance;
        }
        return obj != null;
    }

    /// <summary>Set: charge a special weapon.</summary>
    public bool Set(int type, HouseClass h)
    {
        Abandon();
        isDifferent = true;
        isSuspended = true;
        stage.Set_Rate(0);
        stage.Set_Stage(0);
        specialItem = type;
        house = h;
        balance = 0;
        return specialItem != (int)SpecialWeaponType.SPC_NONE;
    }

    /// <summary>Set: an existing object, already complete.</summary>
    public void Set(TechnoClass o)
    {
        Abandon();
        obj = o;
        house = o.House;
        balance = 0;
        stage.Set_Rate(0);
        stage.Set_Stage(STEP_COUNT);
        isDifferent = true;
        isSuspended = true;
    }

    public bool Suspend()
    {
        if (!isSuspended)
        {
            isSuspended = true;
            stage.Set_Rate(0);
            return true;
        }
        return false;
    }

    /// <summary>Start: production runs at the rate from the object's build time (computers need the next step's money).</summary>
    public bool Start()
    {
        if ((obj != null || specialItem != 0) && isSuspended && !Has_Completed())
        {
            if (house!.IsHuman || house.Available_Money() >= Cost_Per_Tick())
            {
                int time = obj != null
                    ? game.Time_To_Build(obj.Techno_Type_Class(), house.Class.House)
                    : TdDefines.TICKS_PER_MINUTE * 5;
                time /= STEP_COUNT;
                time = Math.Clamp(time, 1, 255);
                stage.Set_Rate(time);
                isSuspended = false;
                return true;
            }
        }
        return false;
    }

    /// <summary>Abandon: the unspent money comes back and the half-made object is deleted.</summary>
    public bool Abandon()
    {
        if (obj != null)
        {
            int money = (int)(obj.Class_Of().Cost_Of(game.Special) * obj.House.CostBias);
            house!.Refund_Money(money - balance);
            balance = 0;
            game.ScenarioInit++;
            obj.Delete();
            obj = null;
            game.ScenarioInit--;

            if (specialItem != 0)
                specialItem = (int)SpecialWeaponType.SPC_NONE;
            stage.Set_Rate(0);
            stage.Set_Stage(0);
            isSuspended = true;
            isDifferent = true;
            return true;
        }
        return false;
    }

    public bool Has_Completed() => (obj != null || specialItem != 0) && stage.Fetch_Stage() == STEP_COUNT;

    int Cost_Per_Tick()
    {
        if (obj != null)
        {
            int steps = STEP_COUNT - stage.Fetch_Stage();
            return steps != 0 ? balance / steps : balance;
        }
        return 0;
    }

    /// <summary>Completed: the finished object has been placed (the factory is free again).</summary>
    public bool Completed()
    {
        if ((obj != null || specialItem != 0) && stage.Fetch_Stage() == STEP_COUNT)
        {
            if (obj != null)
                obj = null;
            else
                specialItem = (int)SpecialWeaponType.SPC_NONE;
            isSuspended = true;
            isDifferent = true;
            stage.Set_Stage(0);
            stage.Set_Rate(0);
            return true;
        }
        return false;
    }
}

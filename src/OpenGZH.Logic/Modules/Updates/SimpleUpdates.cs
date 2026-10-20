using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>Destroys (or kills) the object after a random time between MinLifetime and MaxLifetime.</summary>
public sealed class LifetimeUpdate : Module, IUpdateModule, ICreateModule
{
    int dieFrame;

    /// <summary>The frame the object will die on.</summary>
    public int DieFrame => dieFrame;

    public void OnCreate()
    {
        int min = FramesSetting("MinLifetime");
        int max = Math.Max(min, FramesSetting("MaxLifetime", FloatSetting("MinLifetime")));
        dieFrame = Frame + Game.Random.Int(min, max);
    }

    public void SetLifetime(int frames)
    {
        dieFrame = Frame + frames;
        Owner.WakeUpdate(this);
    }

    public int Update()
    {
        if (Frame < dieFrame)
            return Math.Max(0, dieFrame - Frame - 1);
        Owner.Kill();
        return 1000;
    }
}

/// <summary>Removes the object (no death effects) after a random time.</summary>
public sealed class DeletionUpdate : Module, IUpdateModule, ICreateModule
{
    int deleteFrame;

    public void OnCreate()
    {
        int min = FramesSetting("MinLifetime");
        int max = Math.Max(min, FramesSetting("MaxLifetime", FloatSetting("MinLifetime")));
        deleteFrame = Frame + Game.Random.Int(min, max);
    }

    public void SetLifetime(int frames)
    {
        deleteFrame = Frame + frames;
        Owner.WakeUpdate(this);
    }

    public int Update()
    {
        if (Frame < deleteFrame)
            return Math.Max(0, deleteFrame - Frame - 1);
        Game.DestroyObject(Owner);
        return 1000;
    }
}

/// <summary>Kills the object when it falls below (or rises above) a height, e.g. aircraft that crash.</summary>
public sealed class HeightDieUpdate : Module, IUpdateModule
{
    float targetHeight;
    bool onlyWhenMovingDown;
    int initialDelay;
    float lastZ = float.MaxValue;

    protected override void Initialize()
    {
        targetHeight = FloatSetting("TargetHeight");
        onlyWhenMovingDown = BoolSetting("OnlyWhenMovingDown");
        initialDelay = FramesSetting("InitialDelay");
    }

    public int Update()
    {
        if (Frame - Owner.CreatedFrame < initialDelay)
            return 0;
        // Height is measured above the ground (or water surface) below the object.
        float ground = Game.Terrain.LayerHeight(Owner.Position.X, Owner.Position.Y);
        bool movingDown = Owner.Position.Z < lastZ;
        lastZ = Owner.Position.Z;
        if (Owner.Position.Z - ground <= targetHeight && (!onlyWhenMovingDown || movingDown))
            Owner.Kill();
        return 0;
    }
}

/// <summary>A unit spawned and controlled by another (drones, hive slaves); it dies when its master dies.</summary>
public sealed class SlavedUpdate : Module, IUpdateModule
{
    public int Update()
    {
        var master = Game.FindObject(Owner.ProducerId);
        if (master == null || master.IsEffectivelyDead)
        {
            if (BoolSetting("DieOnMastersDeath", true))
                Owner.Kill();
            return 1000;
        }

        // Stay near the master when idle.
        float leash = FloatSetting("GuardMaxRange", 30);
        if (Owner.AI != null && Owner.AI.IsIdle && MathUtil.Distance2D(Owner.Position, master.Position) > leash)
            Owner.AI.MoveTo(master.Position, Commands.CommandSource.AI);
        return 15;
    }
}

/// <summary>Keeps a floating object on the water surface (boats, floating debris).</summary>
public sealed class FloatUpdate : Module, IUpdateModule
{
    bool enabled;

    protected override void Initialize() => enabled = BoolSetting("Enabled", true);

    public int Update()
    {
        if (!enabled)
            return 1000;
        var position = Owner.Position;
        if (Game.Terrain.IsUnderwater(position.X, position.Y, out float waterHeight) && MathF.Abs(position.Z - waterHeight) > 0.01f)
            Owner.SetPosition(position with { Z = waterHeight });
        return 3;
    }
}

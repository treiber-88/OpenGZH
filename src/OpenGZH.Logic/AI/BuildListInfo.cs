using System.Numerics;

namespace OpenGZH.Logic.AI;

/// <summary>
/// One structure the computer player wants in its base (the original's BuildListInfo): from the
/// map's build list (campaign) or AIData's SkirmishBuildList (skirmish), plus what scripts add.
/// </summary>
public sealed class BuildListInfo
{
    public const int UnlimitedRebuilds = -1;

    public string BuildingName { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public Vector3 Location { get; set; }
    public Vector2 RallyOffset { get; set; }
    public float Angle { get; set; }
    public bool IsInitiallyBuilt { get; set; }
    public int NumRebuilds { get; set; }
    public string Script { get; set; } = "";
    public int Health { get; set; } = 100;
    public bool Whiner { get; set; }
    public bool Unsellable { get; set; }
    public bool Repairable { get; set; }
    public bool IsAutomaticBuild { get; set; } = true;
    public bool IsPriorityBuild { get; private set; }

    /// <summary>The building standing there now (0 = none).</summary>
    public uint ObjectId { get; set; }
    /// <summary>When the building went away (rebuilding waits RebuildDelayTimeSeconds after that); 0 = not waiting.</summary>
    public int ObjectTimestamp { get; set; }
    public bool IsUnderConstruction { get; set; }

    public bool IsSupplyBuilding { get; set; }
    public int DesiredGatherers { get; set; }
    public int CurrentGatherers { get; set; }

    public void MarkPriorityBuild() => IsPriorityBuild = true;

    /// <summary>Has rebuilds left (the original's isBuildable).</summary>
    public bool IsBuildable => NumRebuilds > 0 || NumRebuilds == UnlimitedRebuilds;

    public void DecrementNumRebuilds()
    {
        if (NumRebuilds > 0 && NumRebuilds != UnlimitedRebuilds)
            NumRebuilds--;
    }

    public void IncrementNumRebuilds()
    {
        if (NumRebuilds != UnlimitedRebuilds)
            NumRebuilds++;
    }

    public BuildListInfo Duplicate() => (BuildListInfo)MemberwiseClone();

    public static BuildListInfo From(Formats.Maps.BuildListEntry e) => new()
    {
        BuildingName = e.BuildingName,
        TemplateName = e.TemplateName,
        Location = e.Position,
        Angle = e.Angle,
        IsInitiallyBuilt = e.InitiallyBuilt,
        NumRebuilds = e.Rebuilds,
        Script = e.Script,
        Health = e.Health,
        Whiner = e.Whiner,
        Unsellable = e.Unsellable,
        Repairable = e.Repairable,
    };

    public static BuildListInfo From(AIBuildListEntry e) => new()
    {
        TemplateName = e.TemplateName,
        Location = new Vector3(e.Location, 0),
        Angle = e.Angle,
        IsInitiallyBuilt = e.InitiallyBuilt,
        NumRebuilds = e.Rebuilds,
        IsAutomaticBuild = e.AutomaticallyBuild,
    };
}

/// <summary>A unit type the computer player is having built for a team (the original's WorkOrder).</summary>
public sealed class WorkOrder
{
    public required Templates.ObjectTemplate Thing;
    public uint FactoryId;
    public int NumCompleted;
    public int NumRequired = 1;
    public bool Required;
    public bool IsResourceGatherer;

    public bool IsWaitingToBuild => FactoryId == 0 && NumCompleted < NumRequired;

    public void ValidateFactory(Players.Player player)
    {
        if (FactoryId == 0)
            return;
        var factory = player.Game.FindObject(FactoryId);
        if (factory == null || factory.Owner != player)
            FactoryId = 0;
    }
}

/// <summary>A team being built or waiting to go (the original's TeamInQueue).</summary>
public sealed class TeamInQueue
{
    public List<WorkOrder> WorkOrders { get; } = new();
    public bool PriorityBuild;
    public Players.Team? Team;
    public int FrameStarted;
    public bool SentToStartLocation;
    public bool Reinforcement;
    public uint ReinforcementId;

    public bool IsAllBuilt => WorkOrders.All(o => o.NumRequired <= o.NumCompleted);

    public bool IsBuildTimeExpired(int frame)
    {
        int idle = Team!.Prototype.TemplateInfo.InitialIdleFrames;
        return idle >= 1 && frame > FrameStarted + idle;
    }

    public bool IsMinimumBuilt => WorkOrders.All(o => !(o.NumRequired > o.NumCompleted + (o.FactoryId != 0 ? 1 : 0) && o.Required));

    public bool IncludesADozer => WorkOrders.Any(o => o.Thing.Is("DOZER") && !o.IsResourceGatherer);

    public bool AreBuildsComplete => WorkOrders.All(o => o.FactoryId == 0);

    /// <summary>Build time ran out: the units join the player's default team.</summary>
    public void Disband(Scripting.ScriptEngine? engine)
    {
        var newTeam = Team!.Prototype.ControllingPlayer.DefaultTeam;
        engine?.AppendDebugMessage(Team.Prototype.Name + " - team disbanded, build time expired.", false);
        if (Team != newTeam)
        {
            Team.TransferUnitsTo(newTeam);
            if (!Team.Prototype.IsSingleton)
                Team.ControllingPlayer.Game.Teams.DeleteTeam(Team);
            Team = null;
        }
    }

    /// <summary>The original's destructor: the team becomes active when its queue entry goes away.</summary>
    public void Release() => Team?.SetActive();
}

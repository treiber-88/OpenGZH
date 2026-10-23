using OpenGZH.Core.Rules;
using OpenGZH.Logic;
using OpenGZH.Logic.Scripting;

namespace OpenGZH.Client.Shell;

/// <summary>One mission of a campaign (the original's Mission): its map, briefing and what follows it.</summary>
public sealed record CampaignMission(string Name, RuleNode Data)
{
    public string Map => Data.GetString("Map").Replace('\\', '/');
    public string NextMission => Data.GetString("NextMission");
    public string IntroMovie => Data.GetString("IntroMovie");
    public string BriefingVoice => Data.GetString("BriefingVoice");
    public string LocationNameLabel => Data.GetString("LocationNameLabel");
    public string GeneralName => Data.GetString("GeneralName");
    public int VoiceLength => RuleValue.Int(Data.GetString("VoiceLength"));

    public IEnumerable<string> ObjectiveLines =>
        Enumerable.Range(0, 5).Select(i => Data.GetString($"ObjectiveLine{i}")).Where(s => s.Length > 0);

    public IEnumerable<string> UnitNames =>
        Enumerable.Range(0, 3).Select(i => Data.GetString($"UnitNames{i}")).Where(s => s.Length > 0);
}

/// <summary>A campaign (the original's Campaign): missions in order from FirstMission via NextMission.</summary>
public sealed class Campaign
{
    public string Name { get; }
    public RuleNode Data { get; }
    readonly Dictionary<string, CampaignMission> missions = new(StringComparer.OrdinalIgnoreCase);

    public Campaign(string name, RuleNode data)
    {
        Name = name;
        Data = data;
        if (data.TryGet("Mission", out var list) && list.IsMapping)
            foreach (var (missionName, node) in list.Entries)
                missions[missionName] = new CampaignMission(missionName, node);
    }

    public string FirstMission => Data.GetString("FirstMission");
    public string FinalVictoryMovie => Data.GetString("FinalVictoryMovie");
    public bool IsChallengeCampaign => RuleValue.Bool(Data.GetString("IsChallengeCampaign"));
    public string PlayerFaction => Data.GetString("PlayerFaction");
    public string NameLabel => Data.GetString("CampaignNameLabel");

    public CampaignMission? Mission(string name) => missions.GetValueOrDefault(name);

    /// <summary>The mission after <paramref name="current"/> (the first one when null), or null at the end.</summary>
    public CampaignMission? NextMission(CampaignMission? current) =>
        Mission(current == null ? FirstMission : current.NextMission);
}

/// <summary>
/// Which campaign and mission is being played (the original's CampaignManager): set from the main
/// menu, advanced by the score screen after a victory.
/// </summary>
public sealed class CampaignManager
{
    readonly Dictionary<string, Campaign> campaigns = new(StringComparer.OrdinalIgnoreCase);

    public Campaign? CurrentCampaign { get; private set; }
    public CampaignMission? CurrentMission { get; private set; }
    public Difficulty GameDifficulty { get; set; } = Difficulty.Normal;
    public int RankPoints { get; set; }

    /// <summary>The last mission was won (the original's SetVictorious / isVictorious).</summary>
    public bool IsVictorious { get; set; }

    public CampaignManager(RuleNode? data)
    {
        if (data == null)
            return;
        foreach (var (name, node) in data.Entries)
            campaigns[name] = new Campaign(name, node);
    }

    public void SetCampaign(string name)
    {
        if (name.Length > 0 && campaigns.TryGetValue(name, out var campaign))
        {
            CurrentCampaign = campaign;
            CurrentMission = campaign.NextMission(null);
            return;
        }
        CurrentCampaign = null;
        CurrentMission = null;
    }

    public void SetCampaignAndMission(string campaign, string mission)
    {
        if (campaigns.TryGetValue(campaign, out var c))
        {
            CurrentCampaign = c;
            CurrentMission = c.Mission(mission);
        }
    }

    public CampaignMission? GotoNextMission()
    {
        if (CurrentCampaign == null || CurrentMission == null)
            return null;
        CurrentMission = CurrentCampaign.NextMission(CurrentMission);
        return CurrentMission;
    }

    public string CurrentMap => CurrentMission?.Map ?? "";

    /// <summary>0-based position of the current mission in its campaign.</summary>
    public int CurrentMissionNumber
    {
        get
        {
            int n = 0;
            for (var m = CurrentCampaign?.NextMission(null); m != null; m = CurrentCampaign!.NextMission(m), n++)
                if (m == CurrentMission)
                    return n;
            return -1;
        }
    }
}

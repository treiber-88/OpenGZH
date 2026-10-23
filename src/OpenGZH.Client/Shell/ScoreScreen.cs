using System.Numerics;
using OpenGZH.Client.UI.Gui;
using OpenGZH.Logic;
using OpenGZH.Logic.Players;

namespace OpenGZH.Client.Shell;

/// <summary>A game that has just ended, handed back to the shell for the score screen.</summary>
public sealed record FinishedGame(GameLogic Game, Player? LocalPlayer, Logic.Multiplayer.GameInfo? Skirmish, IReadOnlyList<string> ShellStack)
{
    public bool IsSkirmish => Skirmish != null;
}

/// <summary>
/// The score screen after a game (the original's ScoreScreen.cpp, single player and skirmish parts):
/// one row per player (skirmish, best score first) or the player plus their allies and enemies by
/// side (campaign), then OK back to the menus or Continue to the next mission / Retry.
/// </summary>
public sealed class ScoreScreen : ShellMenu
{
    const string L = "ScoreScreen.wnd";
    const int MaxSlots = 8;

    readonly FinishedGame? finished;
    bool isSinglePlayer, buttonIsFinishCampaign, needToFinishSinglePlayerInit, playMusic;

    public ScoreScreen(ShellScreen screen, GuiLayout layout) : base(screen, layout)
    {
        finished = screen.LastGame;
        OnClick($"{L}:ButtonOk", OkPressed);
        OnClick($"{L}:ButtonContinue", ContinuePressed);
    }

    GuiWindow? W(string name) => Window($"{L}:{name}");

    void Hide(string name, bool hide = true)
    {
        if (W(name) is { } w)
            w.Hidden = hide;
    }

    public override void Init()
    {
        base.Init();
        playMusic = true;
        buttonIsFinishCampaign = false;
        Hide("StaticTextGameSaveComplete");
        // There are no replays yet: the button stays off, as when the recorder is not recording.
        if (W("ButtonSaveReplay") is { } saveReplay)
            saveReplay.Enabled = false;
        needToFinishSinglePlayerInit = false;
        if (finished == null)
            return;
        if (finished.IsSkirmish)
        {
            InitSkirmish();
            Transitions.SetGroup("ScoreScreenShow");
        }
        else
        {
            InitSinglePlayer();
            Hide("ButtonSaveReplay");
        }
        if (Screen.Campaigns.CurrentCampaign is { IsChallengeCampaign: true })
            Hide("ButtonSaveReplay");
    }

    public override void Update()
    {
        if (needToFinishSinglePlayerInit)
        {
            needToFinishSinglePlayerInit = false;
            FinishSinglePlayerInit();
        }
        if (playMusic && finished?.LocalPlayer?.Faction is { } faction)
        {
            playMusic = false;
            string music = faction.GetString("ScoreScreenMusic");
            if (music.Length > 0)
                Screen.Ui.Audio.AddAudioEvent(music);
        }
    }

    public override bool OnKey(char character)
    {
        if (character == '\u001b' && Button($"{L}:ButtonOk") is { Hidden: false } ok)
        {
            ok.SimulateClick();
            return true;
        }
        return false;
    }

    public override void Shutdown(bool immediate)
    {
        Layout.Hidden = true;
        Shell.ShutdownComplete(this);
    }

    // ---- Buttons ----

    void OkPressed()
    {
        Transitions.Remove("ScoreScreenShow", true);
        Shell.Pop();
        Screen.Campaigns.SetCampaign("");
    }

    void ContinuePressed()
    {
        Transitions.Remove("ScoreScreenShow", true);
        if (!isSinglePlayer)
            return;
        string map = Screen.Campaigns.CurrentMap;
        if (map.Length == 0)
            Shell.Pop();
        else
            StartNextCampaignGame(map);
    }

    /// <summary>The original's startNextCampaignGame: the shell goes away and the (next or same) mission loads.</summary>
    void StartNextCampaignGame(string map)
    {
        Shell.PopImmediate();
        Screen.StartCampaignMission(map, Screen.Campaigns.GameDifficulty);
    }

    // ---- Skirmish ----

    void InitSkirmish()
    {
        isSinglePlayer = false;
        GrabMultiPlayerInfo();
        foreach (var name in new[] { "TextEntryChat", "ButtonEmote", "ChatBoxBorder", "ButtonBuddy", "ButtonContinue",
                     "ListboxChatWindowScoreScreen", "StaticTextGameSaveComplete" })
            Hide(name);
    }

    void GrabMultiPlayerInfo()
    {
        var game = finished!.Game;
        if (finished.LocalPlayer != null)
            SetParentImage("MutiPlayer_ScoreScreen");
        // The slot players ("player0".."player7"), best score first; equal scores are nudged apart.
        var scores = new SortedDictionary<int, Player>();
        int adder = 1, count = 0;
        for (int i = 0; i < MaxSlots; i++)
        {
            if (game.Players.Find($"player{i}") is not { } player)
                continue;
            int score = player.Score.CalculateScore();
            if (scores.ContainsKey(score))
                score += adder++;
            scores[score] = player;
            count++;
        }
        HideWindows(count);
        int pos = 0;
        foreach (var player in scores.Values.Reverse())
        {
            if (IsObserver(player))
                SetObserverWindows(player, pos);
            else
                PopulatePlayerInfo(player, pos);
            pos++;
        }
    }

    static bool IsObserver(Player p) => p.FactionName.Equals("FactionObserver", StringComparison.OrdinalIgnoreCase);

    // ---- Single player ----

    void InitSinglePlayer()
    {
        isSinglePlayer = true;
        var local = finished!.LocalPlayer;
        if (local != null)
            Screen.Campaigns.RankPoints = local.SkillPoints;
        if (finished.Game.Scripts != null)
            Screen.Campaigns.GameDifficulty = finished.Game.Scripts.Difficulty;
        GrabSinglePlayerInfo();
        needToFinishSinglePlayerInit = true;
    }

    void FinishSinglePlayerInit()
    {
        var campaigns = Screen.Campaigns;
        var continueButton = Button($"{L}:ButtonContinue");
        if (campaigns.IsVictorious)
        {
            if (campaigns.CurrentCampaign is { IsChallengeCampaign: true })
                DisplayChallengeWinLoss(won: true);
            campaigns.GotoNextMission();
            if (campaigns.CurrentMap.Length == 0)
            {
                if (continueButton != null)
                    continueButton.Text = Screen.Ui.Gui.Text("GUI:EndCampaign");
                buttonIsFinishCampaign = true;
                // (Battle honours for finishing a campaign and the final victory movie are not written yet.)
            }
            else
            {
                if (continueButton != null)
                    continueButton.Text = Screen.Ui.Gui.Text("GUI:SaveAndContinue");
                // (The original saves the mission here; saved games are not written yet.)
            }
        }
        else
        {
            if (campaigns.CurrentCampaign is { IsChallengeCampaign: true })
                DisplayChallengeWinLoss(won: false);
            if (continueButton != null)
                continueButton.Text = Screen.Ui.Gui.Text("GUI:Retry");
        }

        Hide("ButtonOk", false);
        Hide("ButtonContinue", false);
        foreach (var name in new[] { "TextEntryChat", "ButtonEmote", "ListboxChatWindowScoreScreen", "ListboxWarschoolAdvice",
                     "StaticTextWarSchool", "ChatBoxBorder", "ButtonBuddy" })
            Hide(name);
        if (campaigns.CurrentCampaign is { IsChallengeCampaign: false })
            Transitions.SetGroup("ScoreScreenShow");
    }

    /// <summary>The Generals Challenge result: the general's portrait and remark instead of the table.</summary>
    void DisplayChallengeWinLoss(bool won)
    {
        var mission = Screen.Campaigns.CurrentMission;
        var general = mission == null ? null : ChallengeGeneral(mission.GeneralName);
        Hide("MainBackdrop");
        Hide("GadgetParent");
        Hide("ChallengeWinLossText", false);
        Hide("GeneralRemarks", false);
        Hide("BigPortrait", false);
        if (W("ParentScoreScreen") is { } parent)
            parent.EnabledImageOverride = "GeneralsChallengeWinLoss";
        if (general == null || mission == null)
            return;
        if (W("BigPortrait") is { } portrait)
            portrait.EnabledImageOverride = general.GetString(won ? "ImageDefeated" : "ImageVictorious");
        string name = Screen.Ui.Gui.Text(mission.GeneralName);
        if (W("ChallengeWinLossText") is { } header)
            header.Text = Screen.Ui.Gui.Text(won ? "GUI:ChallengeWinText" : "GUI:ChallengeLossText").Replace("%s", name).Replace("%ls", name);
        if (W("GeneralRemarks") is { } remarks)
            remarks.Text = Screen.Ui.Gui.Text(general.GetString(won ? "StringDefeated" : "StringVictorious"));
        string sound = general.GetString(won ? "WinSound" : "LossSound");
        if (sound.Length > 0)
            Screen.Ui.Audio.AddAudioEvent(sound);
    }

    Core.Rules.RuleNode? ChallengeGeneral(string generalName)
    {
        var generals = Screen.Ui.Content.Rules.Section("ChallengeGenerals");
        foreach (var (_, node) in generals.Entries)
            if (node.GetString("GeneralName").Equals(generalName, StringComparison.OrdinalIgnoreCase))
                return node;
        return null;
    }

    void GrabSinglePlayerInfo()
    {
        var game = finished!.Game;
        int playerCount = 0;
        var local = finished.LocalPlayer;
        if (local != null)
        {
            if (!IsObserver(local))
            {
                PopulatePlayerInfo(local, playerCount++);
            }
            else
            {
                local = game.Players.All.FirstOrDefault(p => p.IsHuman);
                if (local != null)
                    PopulatePlayerInfo(local, playerCount++);
            }
            if (finished.LocalPlayer!.Faction is { } faction && faction.GetString("ScoreScreenImage") is { Length: > 0 } image)
                SetParentImage(image);
        }
        if (local == null)
            return;

        // Allies, then enemies, of each side, summed (USA, China, GLA).
        foreach (bool isFriend in new[] { true, false })
            foreach (string side in new[] { "USA", "China", "GLA" })
            {
                int unitsBuilt = 0, unitsLost = 0, unitsDestroyed = 0, buildingsBuilt = 0, buildingsLost = 0, buildingsDestroyed = 0, earned = 0;
                string? sideImage = null;
                uint color = 0xFFFFFF;
                bool populate = false;
                foreach (var player in game.Players.All)
                {
                    if (player == local || !side.Equals(player.BaseSide, StringComparison.Ordinal) || !player.ListInScoreScreen)
                        continue;
                    var relation = local.RelationshipTo(player);
                    if (isFriend ? relation != Relationship.Allies : relation != Relationship.Enemies)
                        continue;
                    var sk = player.Score;
                    unitsBuilt += sk.TotalUnitsBuilt;
                    unitsLost += sk.TotalUnitsLost;
                    unitsDestroyed += sk.TotalUnitsDestroyed;
                    buildingsBuilt += sk.TotalBuildingsBuilt;
                    buildingsLost += sk.TotalBuildingsLost;
                    buildingsDestroyed += sk.TotalBuildingsDestroyed;
                    earned += sk.TotalMoneyEarned;
                    sideImage = player.Faction?.GetString("SideIconImage");
                    color = player.Color;
                    populate = true;
                }
                if (!populate)
                    continue;
                string label = Screen.Ui.Gui.Text($"GUI:{side}{(isFriend ? "Allies" : "Enemies")}");
                FillRow(playerCount++, label, ToColor(color),
                    unitsBuilt, unitsLost, unitsDestroyed, buildingsBuilt, buildingsLost, buildingsDestroyed, earned, sideImage);
            }
        HideWindows(playerCount);
    }

    // ---- Rows ----

    void SetParentImage(string image)
    {
        if (W("ParentScoreScreen") is { } parent)
            parent.EnabledImageOverride = image;
    }

    static Vector4 ToColor(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1);

    void PopulatePlayerInfo(Player player, int pos)
    {
        if (pos > MaxSlots)
            return;
        var sk = player.Score;
        // A campaign shows "Player" rather than the map's player name.
        string name = isSinglePlayer ? Screen.Ui.Gui.Text("GUI:Player") : player.DisplayName;
        FillRow(pos, name, ToColor(player.Color), sk.TotalUnitsBuilt, sk.TotalUnitsLost, sk.TotalUnitsDestroyed,
            sk.TotalBuildingsBuilt, sk.TotalBuildingsLost, sk.TotalBuildingsDestroyed, sk.TotalMoneyEarned,
            player.Faction?.GetString("SideIconImage"));
        // (The War School advice list for the local player needs the academy statistics, not written yet.)
        if (player == finished?.LocalPlayer)
        {
            Hide("ListboxWarschoolAdvice", false);
            Hide("StaticTextWarSchool", false);
        }
    }

    void FillRow(int pos, string name, Vector4 color, int unitsBuilt, int unitsLost, int unitsDestroyed,
        int buildingsBuilt, int buildingsLost, int buildingsDestroyed, int earned, string? sideImage)
    {
        void Set(string window, string text)
        {
            if (W($"{window}{pos}") is not { } w)
                return;
            w.Text = text;
            w.EnabledTextColorOverride = color;
            w.Hidden = false;
        }
        Set("StaticTextPlayer", name);
        Hide($"StaticTextObserver{pos}");
        Set("StaticTextUnitsBuilt", unitsBuilt.ToString());
        Set("StaticTextUnitsLost", unitsLost.ToString());
        Set("StaticTextUnitsDestroyed", unitsDestroyed.ToString());
        Set("StaticTextBuildingsBuilt", buildingsBuilt.ToString());
        Set("StaticTextBuildingsLost", buildingsLost.ToString());
        Set("StaticTextBuildingsDestroyed", buildingsDestroyed.ToString());
        Set("StaticTextResources", earned.ToString());
        if (W($"GameWindowWinner{pos}") is { } winner && !string.IsNullOrEmpty(sideImage))
        {
            winner.Hidden = false;
            winner.EnabledImageOverride = sideImage;
        }
    }

    static readonly string[] RowWindows =
    [
        "StaticTextPlayer", "StaticTextObserver", "StaticTextUnitsBuilt", "StaticTextUnitsLost", "StaticTextUnitsDestroyed",
        "StaticTextBuildingsBuilt", "StaticTextBuildingsLost", "StaticTextBuildingsDestroyed", "StaticTextResources", "GameWindowWinner",
    ];

    void HideWindows(int pos)
    {
        if (pos < 0 || pos >= MaxSlots)
            return;
        for (int i = pos; i < MaxSlots; i++)
            foreach (var window in RowWindows)
                Hide($"{window}{i}");
    }

    void SetObserverWindows(Player player, int i)
    {
        if (i < 0 || i >= MaxSlots)
            return;
        foreach (var window in RowWindows)
            Hide($"{window}{i}");
        if (W($"StaticTextPlayer{i}") is { } name)
        {
            name.Text = player.DisplayName;
            name.EnabledTextColorOverride = Vector4.One;
            name.Hidden = false;
        }
        Hide($"StaticTextObserver{i}", false);
        if (W($"GameWindowWinner{i}") is { } winner)
        {
            winner.Hidden = false;
            winner.EnabledImageOverride = player.Faction?.GetString("SideIconImage");
        }
    }
}

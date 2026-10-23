using System.Numerics;
using OpenGZH.Client.UI.Gui;
using OpenGZH.Logic.Scripting;

namespace OpenGZH.Client.Shell;

/// <summary>
/// The main menu (the original's MainMenu.cpp): five drop-down panels (main, single player,
/// multiplayer, load/replay, difficulty) swapped with window transitions, faction logos that grow
/// when their campaign button is hovered, and the start of campaigns and the Generals Challenge.
/// The very first time, the menu waits for the mouse to move or a key before it appears.
/// </summary>
public sealed class MainMenu : ShellMenu
{
    enum Show { None, Training, Usa, Gla, China, Skirmish }

    // Kept between visits, as the original's statics.
    static bool firstTimeRunningTheGame = true;
    static bool notShown = true;
    static Vector2? firstMousePosition;

    /// <summary>Debug: bring the menu up at once instead of waiting for input (for screenshots).</summary>
    public static bool RevealAtStart { get; set; }

    bool buttonPushed, isShuttingDown, startGame, dontAllowTransitions, campaignSelected, launchChallengeMenu;
    bool showLogo, justEntered;
    int initialGadgetDelay = 210;
    Show showSide = Show.None;
    Difficulty pendingDifficulty;
    string pendingMap = "";

    GuiWindow? dropDownSingle, dropDownMultiplayer, dropDownMain, dropDownLoadReplay, dropDownDifficulty;

    public MainMenu(ShellScreen screen, GuiLayout layout) : base(screen, layout)
    {
        OnClick("ButtonSinglePlayer", () =>
        {
            if (buttonPushed || dontAllowTransitions)
                return;
            dontAllowTransitions = true;
            dropDownSingle!.Hidden = false;
            Transitions.Remove("MainMenuDefaultMenu");
            Transitions.Reverse("MainMenuDefaultMenuBack");
            Transitions.SetGroup("MainMenuSinglePlayerMenu");
        });
        OnClick("ButtonSingleBack", () =>
        {
            if (buttonPushed || campaignSelected || dontAllowTransitions)
                return;
            dropDownMain!.Hidden = false;
            Transitions.Remove("MainMenuSinglePlayerMenu");
            Transitions.Reverse("MainMenuSinglePlayerMenuBack");
            Transitions.SetGroup("MainMenuDefaultMenu");
            dontAllowTransitions = true;
        });
        OnClick("ButtonMultiBack", () => BackToMain("MainMenuMultiPlayerMenu", "MainMenuMultiPlayerMenuReverse"));
        OnClick("ButtonLoadReplayBack", () => BackToMain("MainMenuLoadReplayMenu", "MainMenuLoadReplayMenuBack"));
        OnClick("ButtonCredits", () =>
        {
            if (buttonPushed || dontAllowTransitions)
                return;
            dontAllowTransitions = true;
            buttonPushed = true;
            Shell.Push("Menus/CreditsMenu.wnd");
            dropDownMain!.Hidden = false;
            Transitions.Reverse("MainMenuDefaultMenu");
        });
        OnClick("ButtonMultiplayer", () => OpenDropDown(dropDownMultiplayer, "MainMenuMultiPlayerMenu"));
        OnClick("ButtonLoadReplay", () => OpenDropDown(dropDownLoadReplay, "MainMenuLoadReplayMenu"));
        OnClick("ButtonLoadGame", () => PushFromLoadReplay("Menus/SaveLoad.wnd"));
        OnClick("ButtonReplay", () => PushFromLoadReplay("Menus/ReplayMenu.wnd"));
        OnClick("ButtonSkirmish", () =>
        {
            if (buttonPushed || campaignSelected || dontAllowTransitions)
                return;
            buttonPushed = true;
            campaignSelected = true;
            dropDownSingle!.Hidden = false;
            Transitions.Remove("MainMenuFactionSkirmish");
            Transitions.Reverse("MainMenuSinglePlayerMenuBackSkirmish");
            Shell.Push("Menus/SkirmishGameOptionsMenu.wnd");
        });
        OnClick("ButtonOnline", () =>
        {
            // GameSpy is gone; online play goes the way of LAN play here.
            PushFromMultiplayer("Menus/LanLobbyMenu.wnd");
        });
        OnClick("ButtonNetwork", () => PushFromMultiplayer("Menus/LanLobbyMenu.wnd"));
        OnClick("ButtonOptions", () =>
        {
            if (buttonPushed || dontAllowTransitions)
                return;
            dontAllowTransitions = true;
            Screen.ShowOptions();
        });
        OnClick("ButtonExit", () =>
        {
            if (buttonPushed)
                return;
            // (The original asks first when running full screen; this window always is windowed.)
            buttonPushed = true;
            Shell.Pop();
            Screen.Quit();
        });
        OnClick("ButtonChallenge", () =>
        {
            if (buttonPushed || campaignSelected || dontAllowTransitions)
                return;
            launchChallengeMenu = false;
            Transitions.SetGroup("MainMenuFactionTraining");
            Hide("MainMenu.wnd:WinFactionTraining");
            Transitions.Reverse("MainMenuSinglePlayerMenuBackTraining");
            Transitions.SetGroup("MainMenuDifficultyMenuTraining");
            campaignSelected = true;
            showLogo = false;
            showSide = Show.Training;
            launchChallengeMenu = true;
        });
        OnClick("ButtonUSA", () => ChooseCampaign("USA", "US", Show.Usa));
        OnClick("ButtonGLA", () => ChooseCampaign("GLA", "GLA", Show.Gla));
        OnClick("ButtonChina", () => ChooseCampaign("China", "China", Show.China));
        OnClick("ButtonEasy", () => ChooseDifficulty(Difficulty.Easy));
        OnClick("ButtonMedium", () => ChooseDifficulty(Difficulty.Normal));
        OnClick("ButtonHard", () => ChooseDifficulty(Difficulty.Hard));
        OnClick("ButtonDiffBack", () =>
        {
            if (buttonPushed || dontAllowTransitions)
                return;
            dontAllowTransitions = true;
            Screen.Campaigns.SetCampaign("");
            DiffReverseSide();
            campaignSelected = false;
        });

        // Hovering a campaign button grows its faction logo.
        Hover("ButtonChallenge", Show.Training, "MainMenuFactionTraining");
        Hover("ButtonSkirmish", Show.Skirmish, "MainMenuFactionSkirmish");
        Hover("ButtonUSA", Show.Usa, "MainMenuFactionUS");
        Hover("ButtonGLA", Show.Gla, "MainMenuFactionGLA");
        Hover("ButtonChina", Show.China, "MainMenuFactionChina");
    }

    void Hide(string name)
    {
        if (Window(name) is { } w)
            w.Hidden = true;
    }

    void Hover(string buttonName, Show side, string group)
    {
        if (Button(buttonName) is not { } button)
            return;
        button.MouseEntered += _ =>
        {
            if (dontAllowTransitions && !campaignSelected)
            {
                showLogo = true;
                showSide = side;
            }
            if (campaignSelected || dontAllowTransitions)
                return;
            Transitions.SetGroup(group);
        };
        button.MouseLeft += _ =>
        {
            if (dontAllowTransitions && !campaignSelected && showLogo)
            {
                showLogo = false;
                showSide = Show.None;
            }
            if (campaignSelected || dontAllowTransitions)
                return;
            Transitions.Reverse(group);
        };
    }

    void BackToMain(string removeGroup, string reverseGroup)
    {
        if (buttonPushed || dontAllowTransitions)
            return;
        dontAllowTransitions = true;
        dropDownMain!.Hidden = false;
        Transitions.Remove(removeGroup);
        Transitions.Reverse(reverseGroup);
        Transitions.SetGroup("MainMenuDefaultMenu");
    }

    void OpenDropDown(GuiWindow? dropDown, string group)
    {
        if (buttonPushed || dontAllowTransitions)
            return;
        dontAllowTransitions = true;
        dropDown!.Hidden = false;
        Transitions.Remove("MainMenuDefaultMenu");
        Transitions.Reverse("MainMenuDefaultMenuBack");
        Transitions.SetGroup(group);
    }

    void PushFromLoadReplay(string file)
    {
        if (buttonPushed || dontAllowTransitions)
            return;
        dontAllowTransitions = true;
        buttonPushed = true;
        dropDownLoadReplay!.Hidden = false;
        Transitions.Reverse("MainMenuLoadReplayMenuBackTransition");
        Shell.Push(file);
    }

    void PushFromMultiplayer(string file)
    {
        if (buttonPushed || dontAllowTransitions)
            return;
        dontAllowTransitions = true;
        buttonPushed = true;
        dropDownMultiplayer!.Hidden = false;
        Transitions.Reverse("MainMenuMultiPlayerMenuTransitionToNext");
        Shell.Push(file);
    }

    void ChooseCampaign(string campaign, string groupSuffix, Show side)
    {
        if (buttonPushed || campaignSelected || dontAllowTransitions)
            return;
        launchChallengeMenu = false;
        Screen.Campaigns.SetCampaign(campaign);
        Transitions.SetGroup($"MainMenuFaction{groupSuffix}");
        Transitions.Remove($"MainMenuFaction{groupSuffix}", skipPending: true);
        Hide($"MainMenu.wnd:WinFaction{groupSuffix}");
        Transitions.Reverse($"MainMenuSinglePlayerMenuBack{(side == Show.Usa ? "US" : groupSuffix)}");
        Transitions.SetGroup($"MainMenuDifficultyMenu{groupSuffix}");
        campaignSelected = true;
        showLogo = false;
        showSide = side;
    }

    /// <summary>The original's checkCDBeforeCampaign / prepareCampaignGame / setupGameStart.</summary>
    void ChooseDifficulty(Difficulty difficulty)
    {
        if (buttonPushed || dontAllowTransitions)
            return;
        dontAllowTransitions = true;
        buttonPushed = false;
        Transitions.Reverse("MainMenuDifficultyMenuBack");
        Screen.Campaigns.GameDifficulty = difficulty;
        if (launchChallengeMenu)
        {
            Screen.ChallengeDifficulty = difficulty;
            campaignSelected = true;
            Shell.Push("Menus/ChallengeMenu.wnd");
            Transitions.Reverse("MainMenuDifficultyMenuTraining");
            return;
        }
        startGame = true;
        pendingDifficulty = difficulty;
        pendingMap = Screen.Campaigns.CurrentMap;
        Transitions.SetGroup("FadeWholeScreen");
    }

    void DiffReverseSide()
    {
        (string back, string from) = showSide switch
        {
            Show.Training => ("MainMenuDifficultyMenuTrainingBack", "MainMenuSinglePlayerTrainingMenuFromDiff"),
            Show.Usa => ("MainMenuDifficultyMenuUSBack", "MainMenuSinglePlayerUSAMenuFromDiff"),
            Show.Gla => ("MainMenuDifficultyMenuGLABack", "MainMenuSinglePlayerGLAMenuFromDiff"),
            Show.China => ("MainMenuDifficultyMenuChinaBack", "MainMenuSinglePlayerChinaMenuFromDiff"),
            _ => ("", ""),
        };
        if (back.Length == 0)
            return;
        Transitions.Reverse(back);
        Transitions.SetGroup(from);
    }

    public override void Init()
    {
        Screen.ShowShellMap(true);
        buttonPushed = isShuttingDown = startGame = false;
        dropDownSingle = Window("MainMenu.wnd:MapBorder");
        dropDownMultiplayer = Window("MainMenu.wnd:MapBorder1");
        dropDownMain = Window("MainMenu.wnd:MapBorder2");
        dropDownLoadReplay = Window("MainMenu.wnd:MapBorder3");
        dropDownDifficulty = Window("MainMenu.wnd:MapBorder4");
        foreach (var dropDown in new[] { dropDownSingle, dropDownMultiplayer, dropDownMain, dropDownLoadReplay, dropDownDifficulty })
            if (dropDown != null)
                dropDown.Hidden = true;
        foreach (string side in new[] { "GLA", "China", "US", "Training", "Skirmish" })
            foreach (string size in new[] { "", "Small", "Medium" })
                Hide($"MainMenu.wnd:WinFaction{side}{size}");
        Hide("MainMenu.wnd:WinGrowMarker");
        // The quick save/load buttons per side are hidden until a side shows them (SHOW_NONE).
        foreach (string side in new[] { "USA", "GLA", "China" })
        {
            Hide($"MainMenu.wnd:Button{side}RecentSave");
            Hide($"MainMenu.wnd:Button{side}LoadGame");
        }
        // The debug-only World Builder, update and MOTD buttons are not in the release game.
        Layout.Hidden = false;
        var ruler = Window("MainMenu.wnd:MainMenuRuler");
        if (ruler != null)
            ruler.Hidden = true;
        campaignSelected = false;
        if (firstTimeRunningTheGame)
        {
            Transitions.Reverse("FadeWholeScreen");
            firstTimeRunningTheGame = false;
            if (RevealAtStart)
                Reveal();
        }
        else
        {
            justEntered = true;
            initialGadgetDelay = 2;
            if (ruler != null)
                ruler.Hidden = false;
        }
    }

    public override void Shutdown(bool immediate)
    {
        if (!startGame)
            isShuttingDown = true;
        if (immediate)
            ShutdownComplete();
    }

    void ShutdownComplete()
    {
        isShuttingDown = false;
        Layout.Hidden = true;
        Shell.ShutdownComplete(this);
    }

    public override void Update()
    {
        if (justEntered)
        {
            if (initialGadgetDelay == 1)
            {
                Transitions.SetGroup("MainMenuDefaultMenuLogoFade");
                initialGadgetDelay = 2;
                justEntered = false;
            }
            else
                initialGadgetDelay--;
        }
        if (dontAllowTransitions && Transitions.IsFinished)
            dontAllowTransitions = false;
        if (showLogo && !dontAllowTransitions)
        {
            string? group = showSide switch
            {
                Show.Training => "MainMenuFactionTraining",
                Show.China => "MainMenuFactionChina",
                Show.Gla => "MainMenuFactionGLA",
                Show.Usa => "MainMenuFactionUS",
                Show.Skirmish => "MainMenuFactionSkirmish",
                _ => null,
            };
            if (group != null)
                Transitions.SetGroup(group);
            showLogo = false;
        }
        if (startGame && Shell.IsAnimFinished && Transitions.IsFinished)
        {
            startGame = false;
            isShuttingDown = true;
            Screen.StartCampaignMission(pendingMap, pendingDifficulty);
            return;
        }
        if (isShuttingDown && Shell.IsAnimFinished && Transitions.IsFinished)
            ShutdownComplete();
    }

    /// <summary>The first time, any real mouse movement or key brings the menu up (MainMenuInput).</summary>
    public override bool OnMouseMove(Vector2 point)
    {
        if (!notShown)
            return false;
        firstMousePosition ??= point;
        if (Math.Abs(point.X - firstMousePosition.Value.X) > 20 || Math.Abs(point.Y - firstMousePosition.Value.Y) > 20)
        {
            Reveal();
            return true;
        }
        return false;
    }

    public override bool OnKey(char character)
    {
        if (!notShown)
            return false;
        Reveal();
        return true;
    }

    void Reveal()
    {
        initialGadgetDelay = 1;
        if (dropDownMain != null)
            dropDownMain.Hidden = false;
        Transitions.SetGroup("MainMenuFade", immediate: true);
        Transitions.SetGroup("MainMenuDefaultMenu");
        notShown = false;
    }
}

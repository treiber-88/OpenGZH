using System.Numerics;
using OpenGZH.Client.UI;
using OpenGZH.Client.World;
using OpenGZH.Logic;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Scripting;

namespace OpenGZH.Client.InGame;

/// <summary>
/// The screen side of mission scripts (the original's tactical view, InGameUI and Display calls
/// from ScriptActions): camera moves, letterbox and black-and-white, fades, input lock, military
/// captions, cinematic text, named timers and counters, radar switches and pop-up messages.
/// </summary>
public sealed class ScriptDirector
{
    readonly GameLogic game;
    readonly WorldView world;
    readonly UiResources ui;
    readonly Player player;

    public ScriptedCamera Camera { get; }

    public bool InputEnabled { get; private set; } = true;
    public bool Letterbox { get; private set; }
    public bool RadarHidden { get; private set; }
    public bool RadarForcedOn { get; private set; }
    public bool SpecialPowerDisplayEnabled { get; private set; } = true;
    public bool TimerDisplayEnabled { get; private set; } = true;
    public bool BorderShroud { get; private set; } = true;

    // Black and white (the original's BW view filter fading in and out).
    float bwAmount, bwStep;

    /// <summary>Script timers and counters on screen (DISPLAY_COUNTDOWN_TIMER / DISPLAY_COUNTER), in order added.</summary>
    readonly List<(string Name, string Text, bool IsCountdown)> namedTimers = new();

    // Military caption (InGameUI::militarySubtitle): typed letter by letter, then fades.
    sealed class Caption
    {
        public required string Text;
        public int Index;
        public string Shown = "";
        public int Lifetime, IncrementOnFrame, BlockBeginFrame;
        public bool BlockDrawn = true;
        public float Alpha = 1;
    }
    Caption? caption;
    int lastCaptionFrame = -1;
    const int MilitaryCaptionSpeed = 1;
    const float MilitaryCaptionDelayMs = 750;

    // Cinematic text (DISPLAY_CINEMATIC_TEXT) and pop-up messages.
    string? cinematicText;
    int cinematicUntilFrame;
    (string Text, int X, int Y, int Width, bool Pause)? popup;

    public event Action<string>? MovieRequested;

    /// <summary>A victory / defeat window to show (null closes the current one).</summary>
    public event Action<string?>? MessageWindowRequested;

    void ShowMessageWindow(string? file) => MessageWindowRequested?.Invoke(file);

    bool ObserverWindow => game.Victory.AmIObserver || game.Scripts?.HasShownMPLocalDefeatWindow == true;

    public ScriptDirector(GameLogic game, WorldView world, UiResources ui, Player player)
    {
        this.game = game;
        this.world = world;
        this.ui = ui;
        this.player = player;
        Camera = new ScriptedCamera(world.Camera, game);
        game.Events.OnScriptClientAction += Execute;
        game.IsCameraMovementFinished = () => Camera.IsMovementFinished;
    }

    Vector3? Way(string name) => game.Waypoints?.Find(name)?.Position;
    GameObject? Unit(string name) => game.Scripts?.GetUnitNamed(name);
    string Text(string label) => ui.Strings.TryGet(label, out var text) ? text : label;

    void Execute(ScriptAction a)
    {
        switch (a.Name)
        {
            // ---- Camera ----
            case "MOVE_CAMERA_TO":
                if (Way(a[0].String) is { } to)
                    Camera.MoveCameraTo(to, a[1].Real * 1000, a[2].Real * 1000, true, a[3].Real * 1000, a[4].Real * 1000);
                break;
            case "SETUP_CAMERA":
                if (Way(a[0].String) is { } pos && Way(a[3].String) is { } lookAt)
                {
                    Camera.MoveCameraTo(pos, 0, 0, true, 0, 0);
                    Camera.CameraModLookToward(lookAt);
                    Camera.CameraModFinalPitch(a[2].Real, 0, 0);
                    Camera.CameraModFinalZoom(a[1].Real, 0, 0);
                }
                break;
            case "ZOOM_CAMERA": Camera.ZoomCamera(a[0].Real, a[1].Real * 1000, a[2].Real * 1000, a[3].Real * 1000); break;
            case "PITCH_CAMERA": Camera.PitchCamera(a[0].Real, a[1].Real * 1000, a[2].Real * 1000, a[3].Real * 1000); break;
            case "CAMERA_FOLLOW_NAMED":
                if (Unit(a[0].String) is { } follow)
                {
                    Camera.LockedObjectId = follow.Id;
                    Camera.SnapToLock = a[1].Bool;
                    Camera.TetherDistance = 0;
                }
                break;
            case "CAMERA_STOP_FOLLOW":
            case "CAMERA_STOP_TETHER_NAMED":
                Camera.LockedObjectId = 0;
                break;
            case "CAMERA_TETHER_NAMED":
                if (Unit(a[0].String) is { } tether)
                {
                    Camera.LockedObjectId = tether.Id;
                    Camera.SnapToLock = a[1].Bool;
                    Camera.TetherDistance = a[2].Real;
                }
                break;
            case "CAMERA_MOD_LOOK_TOWARD": if (Way(a[0].String) is { } look) Camera.CameraModLookToward(look); break;
            case "CAMERA_MOD_FINAL_LOOK_TOWARD": if (Way(a[0].String) is { } finalLook) Camera.CameraModFinalLookToward(finalLook); break;
            case "MOVE_CAMERA_ALONG_WAYPOINT_PATH":
                if (game.Waypoints?.Find(a[0].String) is { } start)
                {
                    var path = new List<Vector3>();
                    var visited = new HashSet<Logic.Terrain.Waypoint>();
                    for (var w = start; w != null && visited.Add(w); w = w.Next.Count > 0 ? w.Next[0] : null)
                        path.Add(w.Position);
                    Camera.MoveCameraAlongWaypointPath(path, a[1].Real * 1000, a[2].Real * 1000, true, a[3].Real * 1000, a[4].Real * 1000);
                }
                break;
            case "ROTATE_CAMERA": Camera.RotateCamera(a[0].Real, a[1].Real * 1000, a[2].Real * 1000, a[3].Real * 1000); break;
            case "CAMERA_LOOK_TOWARD_OBJECT":
                if (Unit(a[0].String) is { } lookObj)
                    Camera.RotateCameraTowardObject(lookObj.Id, a[1].Real * 1000, a[2].Real * 1000, a[3].Real * 1000, a[4].Real * 1000);
                break;
            case "CAMERA_LOOK_TOWARD_WAYPOINT":
                if (Way(a[0].String) is { } lookWay)
                    Camera.RotateCameraTowardPosition(lookWay, a[1].Real * 1000, a[2].Real * 1000, a[3].Real * 1000, a[4].Bool);
                break;
            case "RESET_CAMERA": if (Way(a[0].String) is { } reset) Camera.ResetCamera(reset, a[1].Real * 1000, a[2].Real * 1000, a[3].Real * 1000); break;
            case "MOVE_CAMERA_TO_SELECTION": break; // (the selection is the player's; the original averages selected drawables)
            case "CAMERA_MOD_FREEZE_ANGLE": Camera.CameraModFreezeAngle(); break;
            case "CAMERA_MOD_SET_FINAL_ZOOM": Camera.CameraModFinalZoom(a[0].Real, a[1].Real, a[2].Real); break;
            case "CAMERA_MOD_SET_FINAL_PITCH": Camera.CameraModFinalPitch(a[0].Real, a[1].Real, a[2].Real); break;
            case "CAMERA_MOD_SET_ROLLING_AVERAGE": Camera.CameraModRollingAverage(a[0].Int); break;
            case "CAMERA_MOD_FREEZE_TIME":
            case "CAMERA_MOD_SET_FINAL_SPEED_MULTIPLIER":
            case "SET_VISUAL_SPEED_MULTIPLIER":
                break;
            case "CAMERA_SET_DEFAULT":
                break;
            case "SCREEN_SHAKE":
                world.Effects.Shake(world.Camera.Target, a[0].Int switch { 0 => "SUBTLE", 1 => "NORMAL", 2 => "STRONG", 3 => "SEVERE", 4 => "CINE_EXTREME", _ => "CINE_INSANE" });
                break;
            case "CAMERA_ADD_SHAKER_AT":
                if (Way(a[0].String) is { } shakeAt)
                    world.Effects.Shake(shakeAt, a[1].Real > 5 ? "SEVERE" : a[1].Real > 2.5f ? "STRONG" : "NORMAL");
                break;

            // ---- Screen ----
            case "CAMERA_LETTERBOX_BEGIN": Letterbox = true; break;
            case "CAMERA_LETTERBOX_END": Letterbox = false; break;
            case "CAMERA_BW_MODE_BEGIN": bwStep = 1f / Math.Max(1, a[0].Int); break;
            case "CAMERA_BW_MODE_END": bwStep = -1f / Math.Max(1, a[0].Int); break;
            case "DISABLE_INPUT": InputEnabled = false; break;
            case "ENABLE_INPUT": InputEnabled = true; break;
            case "DISABLE_BORDER_SHROUD": BorderShroud = false; break;
            case "ENABLE_BORDER_SHROUD": BorderShroud = true; break;
            case "SHOW_MILITARY_CAPTION": StartCaption(a[0].String, a[1].Int); break;
            case "DISPLAY_CINEMATIC_TEXT":
                cinematicText = Text(a[0].String);
                cinematicUntilFrame = game.Frame + LogicTime.FramesPerSecond * a[2].Int;
                break;
            case "INGAME_POPUP_MESSAGE": popup = (Text(a[0].String), a[1].Int, a[2].Int, a[3].Int, a[4].Bool); break;
            case "MOVIE_PLAY_FULLSCREEN":
            case "MOVIE_PLAY_RADAR":
                MovieRequested?.Invoke(a[0].String);
                // No video playback yet: the movie counts as finished at once (VIDEO_HAS_COMPLETED).
                game.Scripts?.NotifyOfCompletedVideo(a[0].String);
                break;

            // ---- Radar, timers and the special power display ----
            case "RADAR_DISABLE": RadarHidden = true; break;
            case "RADAR_ENABLE": RadarHidden = false; break;
            case "RADAR_FORCE_ENABLE": RadarForcedOn = true; break;
            case "RADAR_REVERT_TO_NORMAL": RadarForcedOn = false; break;
            case "DISPLAY_COUNTDOWN_TIMER": AddTimer(a[0].String, a[1].String, true); break;
            case "DISPLAY_COUNTER": AddTimer(a[0].String, a[1].String, false); break;
            case "HIDE_COUNTDOWN_TIMER":
            case "HIDE_COUNTER":
                namedTimers.RemoveAll(t => t.Name == a[0].String);
                break;
            case "DISABLE_COUNTDOWN_TIMER_DISPLAY": TimerDisplayEnabled = false; break;
            case "ENABLE_COUNTDOWN_TIMER_DISPLAY": TimerDisplayEnabled = true; break;
            case "DISABLE_SPECIAL_POWER_DISPLAY": SpecialPowerDisplayEnabled = false; break;
            case "ENABLE_SPECIAL_POWER_DISPLAY": SpecialPowerDisplayEnabled = true; break;
            // ---- The end of the game (the original's ScriptActions message window) ----
            case "VICTORY": ShowMessageWindow(ObserverWindow ? "Menus/ObserverQuit.wnd" : "Menus/Victorious.wnd"); break;
            case "DEFEAT": ShowMessageWindow(ObserverWindow ? "Menus/ObserverQuit.wnd" : "Menus/Defeat.wnd"); break;
            case "LOCALDEFEAT": ShowMessageWindow(game.Victory.AmIObserver ? null : "Menus/LocalDefeat.wnd"); break;
            case "CLOSE_WINDOWS": ShowMessageWindow(null); break;
        }
    }

    void AddTimer(string name, string label, bool countdown)
    {
        namedTimers.RemoveAll(t => t.Name == name);
        namedTimers.Add((name, Text(label), countdown));
    }

    void StartCaption(string label, int durationMs)
    {
        string text = Text(label);
        if (text.Length == 0 || durationMs <= 0)
        {
            caption = null;
            return;
        }
        int now = game.Frame;
        caption = new Caption
        {
            Text = text.Replace("\\n", "\n"),
            Lifetime = now + (int)(LogicTime.FramesPerSecond * durationMs / 1000f),
            BlockBeginFrame = now,
            IncrementOnFrame = now + (int)(LogicTime.FramesPerSecond * MilitaryCaptionDelayMs / 1000f),
        };
    }

    /// <summary>Per logic frame things (typing captions, black and white fading).</summary>
    public void Update(float seconds)
    {
        Camera.Update(seconds);
        if (game.Frame == lastCaptionFrame)
            return;
        lastCaptionFrame = game.Frame;
        bwAmount = Math.Clamp(bwAmount + bwStep, 0, 1);
        if (caption is { } c)
        {
            int now = game.Frame;
            if (game.Scripts is { IsTimeFrozenByScript: true })
            {
                c.Lifetime++;
                c.BlockBeginFrame++;
                c.IncrementOnFrame++;
            }
            if (c.Lifetime < now)
            {
                c.Alpha -= (now - c.Lifetime) * 0.1f / 255f * 25;
                if (c.Alpha <= 0)
                    caption = null;
            }
            else
            {
                if (c.BlockBeginFrame + 9 < now)
                {
                    c.BlockBeginFrame = now;
                    c.BlockDrawn = !c.BlockDrawn;
                }
                if (c.IncrementOnFrame < now && c.Index < c.Text.Length)
                {
                    char ch = c.Text[c.Index];
                    c.Shown += ch;
                    if (ch == '\n')
                        c.IncrementOnFrame = now + (int)(LogicTime.FramesPerSecond * MilitaryCaptionDelayMs / 1000f);
                    else
                    {
                        game.Events.PlaySound("MilitarySubtitlesTyping", null, player);
                        c.IncrementOnFrame = now + MilitaryCaptionSpeed;
                    }
                    c.Index++;
                    if (c.Index >= c.Text.Length)
                        c.IncrementOnFrame = c.Lifetime + 1;
                }
            }
        }
        if (cinematicText != null && game.Frame > cinematicUntilFrame)
            cinematicText = null;
    }

    /// <summary>Screen overlays: fades, black and white, letterbox bars, captions, timers.</summary>
    public void Draw(SpriteBatch batch, int width, int height)
    {
        if (bwAmount > 0)
            batch.FillRect(new Rect(0, 0, width, height), new Vector4(0.45f, 0.45f, 0.45f, bwAmount * 0.55f));
        if (game.Scripts is { Fade: not ScriptEngine.FadeType.None } engine)
        {
            float v = Math.Clamp(engine.CurrentFadeValue, 0, 1);
            var color = engine.Fade switch
            {
                ScriptEngine.FadeType.Multiply => new Vector4(0, 0, 0, 1 - v),
                ScriptEngine.FadeType.Subtract => new Vector4(0, 0, 0, v),
                ScriptEngine.FadeType.Add => new Vector4(1, 1, 1, v),
                _ => new Vector4(1, 1, 1, v * 0.5f),
            };
            batch.FillRect(new Rect(0, 0, width, height), color);
        }
        if (Letterbox)
        {
            float bar = height * 0.125f;
            batch.FillRect(new Rect(0, 0, width, bar), new Vector4(0, 0, 0, 1));
            batch.FillRect(new Rect(0, height - bar, width, bar), new Vector4(0, 0, 0, 1));
        }
        if (caption is { } c)
        {
            float sx = width / 800f, sy = height / 600f;
            var font = ui.Fonts.Get("Courier New", 12, bold: true);
            var pos = new Vector2(10 * sx, 340 * sy);
            var color = new Vector4(1, 1, 1, Math.Clamp(c.Alpha, 0, 1));
            var lines = c.Shown.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                font.DrawWithShadow(batch, lines[i], pos, color, new Vector4(0, 0, 0, color.W));
                if (i < lines.Length - 1)
                    pos.Y += font.LineHeight;
            }
            if (c.BlockDrawn && c.Lifetime >= game.Frame)
            {
                float h = font.LineHeight;
                batch.FillRect(new Rect(pos.X + font.Measure(lines[^1]), pos.Y, h * 0.8f, h), color);
            }
        }
        if (cinematicText != null)
        {
            var font = ui.Fonts.Get("Arial", 16, bold: true);
            float w = font.Measure(cinematicText);
            font.DrawWithShadow(batch, cinematicText, new Vector2((width - w) / 2, height * 0.8f), Vector4.One, new Vector4(0, 0, 0, 1));
        }
        if (TimerDisplayEnabled && namedTimers.Count > 0 && game.Scripts is { } scripts)
        {
            var font = ui.Fonts.Get("Arial", 10, bold: true);
            float x = width * 0.90f, y = height * 0.666f;
            foreach (var (name, text, countdown) in namedTimers)
            {
                string value;
                if (countdown)
                {
                    int secondsLeft = scripts.TimerRemaining(name) / LogicTime.FramesPerSecond;
                    value = $"{secondsLeft / 60}:{secondsLeft % 60:00}";
                }
                else
                    value = scripts.CounterValue(name).ToString();
                string line = $"{text} {value}";
                float w = font.Measure(line);
                font.DrawWithShadow(batch, line, new Vector2(Math.Min(x, width - w - 8), y), Vector4.One, new Vector4(0, 0, 0, 1));
                y += font.LineHeight;
            }
        }
        if (popup is { } p)
        {
            var font = ui.Fonts.Get("Arial", 12);
            float sx = width / 800f, sy = height / 600f;
            var lines = font.WrapLines(p.Text, p.Width * sx);
            var rect = new Rect(p.X * sx, p.Y * sy, p.Width * sx + 12, lines.Count * font.LineHeight + 12);
            batch.FillRect(rect, new Vector4(0, 0, 0, 0.8f));
            batch.DrawRect(rect, new Vector4(0.8f, 0.7f, 0.2f, 1));
            float y = rect.Y + 6;
            foreach (string line in lines)
            {
                font.Draw(batch, line, new Vector2(rect.X + 6, y), Vector4.One);
                y += font.LineHeight;
            }
        }
    }

    /// <summary>Any click closes a pop-up message.</summary>
    public bool DismissPopup()
    {
        if (popup == null)
            return false;
        popup = null;
        return true;
    }
}

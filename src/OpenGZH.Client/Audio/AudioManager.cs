using System.Numerics;
using OpenGZH.Core.FileSystem;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Audio;
using Silk.NET.OpenAL;

namespace OpenGZH.Client.Audio;

/// <summary>
/// Plays the game's sounds, speech and music (the original's AudioManager, SoundManager and
/// MilesAudioManager), through OpenAL. Requests go through the same rules as the original: who may
/// hear a sound (player / allies / enemies / everyone), distance and shroud culling, per-event limits,
/// priorities and interrupting, voices one at a time per unit, attack / sound / decay portions,
/// loops, delays and fades. Volume falls off as MinRange / distance and is cut at MaxRange.
///
/// Without an audio device everything is accepted and nothing is heard.
/// </summary>
public sealed unsafe class AudioManager : IDisposable
{
    const float MsPerLogicFrame = 1000f / 30f;

    enum PlayingType { Sample, Sample3D, Stream }
    enum PlayingStatus { Playing, Stopped }

    sealed class PlayingAudio
    {
        public required AudioEventRts Event;
        public PlayingType Type;
        public uint Source;
        public PlayingStatus Status = PlayingStatus.Playing;
        public bool RequestStop, CleanupEvent = true, HasFile;
        public int FramesFaded;
        public MusicStream? Stream;
        /// <summary>How many times this playing stream has played through (counts from its start).</summary>
        public int Completions;
    }

    sealed class Request
    {
        public bool Play;
        public AudioEventRts? Event;
        public int Handle;
        public bool RequiresCheckForSample;
    }

    readonly VirtualFileSystem files;
    readonly Dictionary<string, AudioEventInfo> infos = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, uint> bufferCache = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> missingFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly List<PlayingAudio> playingSounds = new(), playing3DSounds = new(), playingStreams = new(), fading = new();
    readonly List<Request> requests = new();
    readonly Stack<uint> available2D = new(), available3D = new();
    int num2DSamples, num3DSamples, numPlaying2D, numPlaying3D;
    int nextHandle = 6;
    readonly List<string> musicTracks = new();

    readonly AL? al;
    readonly ALContext? alc;
    readonly Device* device;
    readonly Context* context;

    public AudioSettings Settings { get; }
    public string Language { get; set; } = "English";
    public bool IsAvailable => al != null;

    // Volumes (system settings times script settings; 3D also times the camera zoom adjustment).
    float systemMusic, systemSound, systemSound3D, systemSpeech;
    float scriptMusic = 1, scriptSound = 1, scriptSound3D = 1, scriptSpeech = 1;
    float musicVolume, soundVolume, sound3DVolume, speechVolume;
    bool volumeHasChanged;
    bool disallowSpeech;

    Vector3 listenerPosition;

    /// <summary>Where an object is (null once it is gone); set by the game.</summary>
    public Func<uint, Vector3?> ObjectPosition { get; set; } = _ => null;
    /// <summary>The owning player's index of an object, if it exists.</summary>
    public Func<uint, int?> ObjectOwnerIndex { get; set; } = _ => null;
    /// <summary>Who may hear "player", "allies" and "enemies" sounds.</summary>
    public Func<int, int, AudioRelationship> Relationship { get; set; } = (a, b) => a == b ? AudioRelationship.Self : AudioRelationship.Enemy;
    /// <summary>The local player's index (-1 for none).</summary>
    public int LocalPlayerIndex { get; set; } = -1;
    /// <summary>Is a point under fog or shroud for the local player? (ST_SHROUDED sounds are not heard there.)</summary>
    public Func<Vector3, bool> IsPointShrouded { get; set; } = _ => false;
    /// <summary>Is a point on screen? (Off-screen sounds with LowPassCutoff are muffled.)</summary>
    public Func<Vector3, bool> IsOnScreen { get; set; } = _ => true;

    /// <param name="openDevice">False for a silent manager (tests): all the rules run, nothing is heard.</param>
    public AudioManager(VirtualFileSystem files, RuleSet rules, bool openDevice = true)
    {
        this.files = files;
        Settings = AudioSettings.From(rules.Section("AudioSettings"));
        LoadInfos(rules, "AudioEvents", "DefaultSoundEffect", AudioType.SoundEffect);
        LoadInfos(rules, "DialogEvents", "DefaultDialog", AudioType.Streaming);
        LoadInfos(rules, "MusicTracks", "DefaultMusicTrack", AudioType.Music);

        systemMusic = Settings.DefaultMusicVolume;
        systemSound = Settings.DefaultSoundVolume;
        systemSound3D = Settings.Default3DSoundVolume;
        systemSpeech = Settings.DefaultSpeechVolume;
        musicVolume = systemMusic;
        soundVolume = systemSound;
        sound3DVolume = systemSound3D;
        speechVolume = systemSpeech;

        try
        {
            if (!openDevice)
                throw new InvalidOperationException("silent");
            alc = ALContext.GetApi(true);
            al = AL.GetApi(true);
            device = alc.OpenDevice("");
            if (device == null)
                throw new InvalidOperationException("no audio device");
            context = alc.CreateContext(device, null);
            alc.MakeContextCurrent(context);
            al.DistanceModel(DistanceModel.None);
            for (int i = 0; i < Settings.SampleCount2D; i++)
            {
                uint s = al.GenSource();
                al.SetSourceProperty(s, SourceBoolean.SourceRelative, true);
                available2D.Push(s);
                num2DSamples++;
            }
            for (int i = 0; i < Settings.SampleCount3D; i++)
            {
                available3D.Push(al.GenSource());
                num3DSamples++;
            }
        }
        catch (Exception e)
        {
            if (openDevice)
                Console.Error.WriteLine($"Audio is off: {e.Message}");
            al = null;
            alc = null;
            num2DSamples = Settings.SampleCount2D;
            num3DSamples = Settings.SampleCount3D;
        }
    }

    void LoadInfos(RuleSet rules, string section, string defaultName, AudioType type)
    {
        var node = rules.Section(section);
        var defaults = new AudioEventInfo { Kind = type };
        if (node[defaultName] is { } d)
            defaults.Apply(d);
        foreach (var key in node.Keys)
        {
            if (node[key] is not { } entry)
                continue;
            var info = defaults.Clone();
            info.Name = key;
            info.Kind = type;
            info.Apply(entry);
            infos[key] = info;
            if (type == AudioType.Music)
                musicTracks.Add(key);
        }
    }

    public AudioEventInfo? FindInfo(string name) => infos.GetValueOrDefault(name);


    /// <summary>
    /// The original's hasMusicTrackCompleted: the track has played through this many times. Only the
    /// track playing now counts, from when it started (a track that played earlier doesn't).
    /// </summary>
    public bool HasMusicTrackCompleted(string track, int times) =>
        playingStreams.Any(s => s.Event.Info.Kind == AudioType.Music && s.Event.EventName.Equals(track, StringComparison.OrdinalIgnoreCase) && s.Completions >= times);

    // ---- Script controls ----

    /// <summary>Per-event volume overrides set by scripts (0 mutes an event).</summary>
    readonly List<(string Name, float Volume)> adjustedVolumes = new();
    bool soundsPaused;

    /// <summary>The original's setAudioEventVolumeOverride: a volume for every copy of an event; -1 restores, empty name clears all.</summary>
    public void SetAudioEventVolumeOverride(string name, float volume)
    {
        if (string.IsNullOrEmpty(name))
        {
            adjustedVolumes.Clear();
            return;
        }
        if (volume != -1)
            foreach (var p in playingSounds.Concat(playing3DSounds).Concat(playingStreams))
                if (p.Event.EventName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    p.Event.VolumeOverride = volume;
                    SetGain(p, EffectiveVolume(p.Event));
                }
        int index = adjustedVolumes.FindIndex(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            if (volume == -1)
                adjustedVolumes.RemoveAt(index);
            else
                adjustedVolumes[index] = (name, volume);
        }
        else if (volume != -1)
            adjustedVolumes.Insert(0, (name, volume));
    }

    public void SetAudioEventEnabled(string name, bool enabled) => SetAudioEventVolumeOverride(name, enabled ? -1 : 0);

    /// <summary>Stops every playing copy of an event at once.</summary>
    public void RemovePlayingAudio(string name)
    {
        foreach (var list in new[] { playingSounds, playing3DSounds, playingStreams })
            for (int i = 0; i < list.Count; i++)
                if (list[i].Event.EventName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Release(list[i]);
                    list.RemoveAt(i--);
                }
    }

    /// <summary>Stops every sound, speech and music track, and drops pending ones (a game or the shell map is ending).</summary>
    public void StopAllAudio()
    {
        requests.Clear();
        foreach (var list in new[] { playingSounds, playing3DSounds, playingStreams, fading })
        {
            foreach (var p in list)
                Release(p);
            list.Clear();
        }
    }

    /// <summary>Stops everything muted to volume 0.</summary>
    public void RemoveAllDisabledAudio()
    {
        foreach (var list in new[] { playingSounds, playing3DSounds, playingStreams })
            for (int i = 0; i < list.Count; i++)
                if (list[i].Event.Volume == 0)
                {
                    Release(list[i]);
                    list.RemoveAt(i--);
                }
    }

    /// <summary>Pauses or resumes all sound effects (not speech or music).</summary>
    public void PauseSounds(bool pause)
    {
        soundsPaused = pause;
        if (al == null)
            return;
        foreach (var p in playingSounds.Concat(playing3DSounds))
            if (pause) al.SourcePause(p.Source); else al.SourcePlay(p.Source);
    }

    /// <summary>A script sets a master volume ("MUSIC", "SOUND" or "SPEECH") as a fraction of the player's setting.</summary>
    public void SetScriptVolume(string which, float volume)
    {
        switch (which)
        {
            case "MUSIC": scriptMusic = volume; musicVolume = scriptMusic * systemMusic; break;
            case "SOUND": scriptSound = volume; soundVolume = scriptSound * systemSound; scriptSound3D = volume; sound3DVolume = scriptSound3D * systemSound3D; break;
            case "SPEECH": scriptSpeech = volume; speechVolume = scriptSpeech * systemSpeech; break;
        }
        volumeHasChanged = true;
    }

    /// <summary>Changes the music (the original's doMusicTrackChange): the old track stops (fading if asked) and the new one starts.</summary>
    public int ChangeMusic(string track, bool fadeOut, int playerIndex)
    {
        StopMusic(fadeOut);
        return AddAudioEvent(track, playerIndex: playerIndex);
    }

    internal Vector3? FindObjectPosition(uint id) => ObjectPosition(id);
    internal int? FindObjectOwnerIndex(uint id) => ObjectOwnerIndex(id);
    internal bool FileExists(string path) => files.Exists(path);

    // ---- Volumes ----

    public float MusicVolume { get => systemMusic; set { systemMusic = value; musicVolume = scriptMusic * systemMusic; volumeHasChanged = true; } }
    public float SoundVolume { get => systemSound; set { systemSound = value; soundVolume = scriptSound * systemSound; volumeHasChanged = true; } }
    public float Sound3DVolume { get => systemSound3D; set { systemSound3D = value; sound3DVolume = scriptSound3D * systemSound3D; volumeHasChanged = true; } }
    public float SpeechVolume { get => systemSpeech; set { systemSpeech = value; speechVolume = scriptSpeech * systemSpeech; volumeHasChanged = true; } }

    /// <summary>
    /// The microphone sits between the ground under the camera and the camera (the original's
    /// AudioManager::update), and 3D sounds get louder as the camera zooms in.
    /// </summary>
    public void SetCamera(Vector3 groundPosition, Vector3 cameraPosition, float yaw, float groundHeight)
    {
        float desired = Settings.MicrophoneDesiredHeightAboveTerrain;
        float maxPct = Settings.MicrophoneMaxPercentageBetweenGroundAndCamera;
        var groundToCamera = cameraPosition - groundPosition;
        float scale = cameraPosition.Z <= desired || groundToCamera.Z <= 0 ? maxPct : Math.Min(maxPct, desired / groundToCamera.Z);
        groundPosition.Z = groundHeight;
        listenerPosition = groundPosition + groundToCamera * scale;
        var forward = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0);

        float boost = Settings.ZoomSoundVolumePercentageAmount;
        float zoomVolume = 1f - boost;
        if (boost > 0)
        {
            float distance = Vector3.Distance(cameraPosition, listenerPosition);
            if (distance < Settings.ZoomMinDistance)
                zoomVolume = 1;
            else if (distance < Settings.ZoomMaxDistance)
                zoomVolume = 1f - (distance - Settings.ZoomMinDistance) / (Settings.ZoomMaxDistance - Settings.ZoomMinDistance) * boost;
        }
        sound3DVolume = Math.Clamp(zoomVolume * scriptSound3D * systemSound3D, 0, 1);
        volumeHasChanged = true;

        if (al != null)
        {
            al.SetListenerProperty(ListenerVector3.Position, listenerPosition.X, listenerPosition.Y, listenerPosition.Z);
            var orientation = stackalloc float[6] { forward.X, forward.Y, forward.Z, 0, 0, 1 };
            al.SetListenerProperty(ListenerFloatArray.Orientation, orientation);
        }
    }

    // ---- Adding sounds ----

    /// <summary>
    /// Plays an audio event (the original's addAudioEvent). Give a position or an object for sounds in
    /// the world, and the player it belongs to for player / allies / enemies sounds. Returns a handle
    /// to stop it with, or 0 when it will not play.
    /// </summary>
    public int AddAudioEvent(string name, Vector3? position = null, uint objectId = 0, int playerIndex = -1, bool uninterruptable = false)
    {
        if (string.IsNullOrEmpty(name) || name == "NoSound")
            return 0;
        if (FindInfo(name) is not { } info)
            return 0;
        if (info.Kind == AudioType.Streaming && disallowSpeech)
            return 0;
        var e = new AudioEventRts(this, info) { PlayerIndex = playerIndex, Uninterruptable = uninterruptable, Handle = nextHandle++ };
        if (objectId != 0)
            e.SetObject(objectId);
        else if (position is { } p)
            e.SetPosition(p);
        e.GenerateFilename();
        e.GeneratePlayInfo();
        foreach (var (adjusted, volume) in adjustedVolumes)
            if (adjusted.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                e.VolumeOverride = volume;
                break;
            }
        if (!uninterruptable && !ShouldPlayLocally(e))
            return 0;
        if (e.Volume < Settings.MinVolume)
            return 0;
        if (info.Kind == AudioType.Music)
            requests.Add(new Request { Play = true, Event = e });
        else if (CanPlayNow(e))
            requests.Add(new Request { Play = true, Event = e });
        else
            return 0;
        return e.Handle;
    }

    /// <summary>Stops a sound: loops end (playing their decay), streams stop.</summary>
    public void RemoveAudioEvent(int handle)
    {
        if (handle > 0)
            requests.Add(new Request { Play = false, Handle = handle });
    }

    bool ShouldPlayLocally(AudioEventRts e)
    {
        var info = e.Info;
        if (info.Kind == AudioType.Music)
            return true;
        if ((info.Type & (SoundType.Player | SoundType.Allies | SoundType.Enemies | SoundType.Everyone)) == 0)
            return true;
        if (info.Type.HasFlag(SoundType.Everyone))
            return true;
        int owner = e.GetPlayerIndex();
        if (info.Type.HasFlag(SoundType.Player) && info.Type.HasFlag(SoundType.UI) && owner < 0)
            return true;
        if (owner < 0 || LocalPlayerIndex < 0)
            return false;
        var relation = Relationship(owner, LocalPlayerIndex);
        if (info.Type.HasFlag(SoundType.Player))
            return relation == AudioRelationship.Self;
        if (info.Type.HasFlag(SoundType.Allies))
            return relation == AudioRelationship.Ally;
        if (info.Type.HasFlag(SoundType.Enemies))
            return relation == AudioRelationship.Enemy;
        return false;
    }

    bool CanPlayNow(AudioEventRts e)
    {
        var info = e.Info;
        if (e.IsPositionalAudio && !info.Type.HasFlag(SoundType.Global) && info.Priority != AudioPriority.Critical && e.CurrentPosition() is { } pos)
        {
            if (Vector3.Distance(listenerPosition, pos) >= info.MaxDistance)
                return false;
            if (info.Type.HasFlag(SoundType.Shrouded) && IsPointShrouded(pos))
                return false;
        }
        if (ViolatesVoice(e))
            return IsInterrupting(e);
        if (DoesViolateLimit(e))
            return false;
        if (IsInterrupting(e))
            return true;
        if (e.IsPositionalAudio ? numPlaying3D < num3DSamples : numPlaying2D < num2DSamples)
            return true;
        if (IsPlayingLowerPriority(e))
            return true;
        if (IsInterrupting(e))
            return IsPlayingAlready(e);
        return false;
    }

    static bool IsInterrupting(AudioEventRts e) => e.Info.Control.HasFlag(AudioControl.Interrupt);

    bool ViolatesVoice(AudioEventRts e) =>
        e.Info.Type.HasFlag(SoundType.Voice) && e.Owner == OwnerType.Object && e.ObjectId != 0 && IsObjectPlayingVoice(e.ObjectId);

    bool IsObjectPlayingVoice(uint id) =>
        playingSounds.Concat(playing3DSounds).Any(p => p.Event.Owner == OwnerType.Object && p.Event.ObjectId == id && p.Event.Info.Type.HasFlag(SoundType.Voice));

    List<PlayingAudio> ListFor(AudioEventRts e) => e.IsPositionalAudio ? playing3DSounds : playingSounds;

    bool DoesViolateLimit(AudioEventRts e)
    {
        int limit = e.Info.Limit;
        if (limit == 0)
            return false;
        int total = 0, totalRequests = 0;
        foreach (var p in ListFor(e))
            if (p.Event.EventName == e.EventName)
            {
                if (total == 0)
                    e.HandleToKill = p.Event.Handle;
                total++;
            }
        foreach (var r in requests)
            if (r.Play && r.Event?.EventName == e.EventName)
            {
                totalRequests++;
                total++;
            }
        if (IsInterrupting(e) && totalRequests < limit)
        {
            if (total < limit)
                e.HandleToKill = 0;
            return false;
        }
        if (total < limit)
        {
            e.HandleToKill = 0;
            return false;
        }
        return true;
    }

    bool IsPlayingAlready(AudioEventRts e) => ListFor(e).Any(p => p.Event.EventName == e.EventName);

    bool IsPlayingLowerPriority(AudioEventRts e) =>
        e.Info.Priority != AudioPriority.Lowest && ListFor(e).Any(p => p.Event.Info.Priority < e.Info.Priority);

    PlayingAudio? FindLowestPriority(AudioEventRts e)
    {
        if (e.Info.Priority == AudioPriority.Lowest)
            return null;
        PlayingAudio? lowest = null;
        foreach (var p in ListFor(e))
        {
            var priority = p.Event.Info.Priority;
            if (priority < e.Info.Priority && (lowest == null || lowest.Event.Info.Priority > priority))
            {
                lowest = p;
                if (priority == AudioPriority.Lowest)
                    break;
            }
        }
        return lowest;
    }

    // ---- Per-frame processing ----

    /// <summary>Runs once per logic frame (requests and their delays step in logic frames, as in the original).</summary>
    public void Update()
    {
        ProcessRequests();
        ProcessPlaying();
        ProcessFading();
        foreach (var s in playingStreams)
            s.Stream?.Pump(al!, s.Source);
    }

    void ProcessRequests()
    {
        for (int i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            if (r.Play && r.Event!.Delay >= MsPerLogicFrame)
            {
                r.Event.Delay -= MsPerLogicFrame;
                r.RequiresCheckForSample = true;
                continue;
            }
            requests.RemoveAt(i--);
            if (!r.Play)
                StopAudioEvent(r.Handle);
            else if (!r.RequiresCheckForSample || CheckForSample(r.Event!))
                PlayAudioEvent(r.Event!);
        }
    }

    bool CheckForSample(AudioEventRts e) =>
        e.Info.Kind != AudioType.SoundEffect || (e.IsPositionalAudio ? available3D.Count > 0 : available2D.Count > 0) || FindLowestPriority(e) != null;

    void PlayAudioEvent(AudioEventRts e)
    {
        var info = e.Info;
        if (info.Kind is AudioType.Music or AudioType.Streaming)
        {
            if (info.Kind == AudioType.Streaming && e.Uninterruptable)
                StopAllSpeech();
            bool replace = false;
            if (e.HandleToKill != 0 && playingStreams.FirstOrDefault(p => p.Event.Handle == e.HandleToKill) is { } old)
            {
                Release(old);
                playingStreams.Remove(old);
                replace = true;
            }
            if (e.HandleToKill != 0 && !replace)
                return;
            if (Logic.GameProfiler.Enabled && info.Kind == AudioType.Music)
                Console.WriteLine($"[perf] music start {e.EventName} ({playingStreams.Count} streams playing)");
            // Only one music track plays at a time: a new one replaces the current one.
            if (info.Kind == AudioType.Music && !replace)
                StopMusic(fade: false);
            var stream = OpenStream(e);
            if (stream == null)
                return;
            if (info.Kind == AudioType.Streaming && e.Uninterruptable)
                disallowSpeech = true;
            playingStreams.Add(stream);
            return;
        }

        var list = e.IsPositionalAudio ? playing3DSounds : playingSounds;
        if (e.HandleToKill != 0)
        {
            if (list.FirstOrDefault(p => p.Event.Handle == e.HandleToKill) is not { } toKill)
                return;
            Release(toKill);
            list.Remove(toKill);
        }
        var pool = e.IsPositionalAudio ? available3D : available2D;
        if (pool.Count == 0 && FindLowestPriority(e) is { } lowest)
        {
            Release(lowest);
            ListFor(e).Remove(lowest);
        }
        if (pool.Count == 0 && al != null)
            return;
        var playing = new PlayingAudio { Event = e, Type = e.IsPositionalAudio ? PlayingType.Sample3D : PlayingType.Sample };
        if (al != null)
            playing.Source = pool.Pop();
        list.Add(playing);
        if (e.IsPositionalAudio) numPlaying3D++; else numPlaying2D++;
        if (!PlaySample(playing))
        {
            Release(playing);
            list.Remove(playing);
        }
    }

    /// <summary>Starts the event's current portion on the playing source; false if there is nothing to play.</summary>
    bool PlaySample(PlayingAudio p)
    {
        var e = p.Event;
        Vector3? position = null;
        if (p.Type == PlayingType.Sample3D && (position = e.CurrentPosition()) == null)
            return false;
        string file = e.FileForPortion;
        if (file.Length == 0 || LoadBuffer(file) is not { } buffer)
        {
            // Headless or a missing file: count it as played so the bookkeeping matches.
            p.HasFile = al == null && file.Length > 0;
            return p.HasFile;
        }
        p.HasFile = true;
        if (al == null)
            return true;
        al.SourceStop(p.Source);
        al.SetSourceProperty(p.Source, SourceInteger.Buffer, (int)buffer);
        al.SetSourceProperty(p.Source, SourceFloat.Pitch, e.PitchShift > 0 ? e.PitchShift : 1);
        al.SetSourceProperty(p.Source, SourceBoolean.Looping, false);
        if (position is { } at)
        {
            al.SetSourceProperty(p.Source, SourceBoolean.SourceRelative, false);
            al.SetSourceProperty(p.Source, SourceVector3.Position, at.X, at.Y, at.Z);
        }
        else
        {
            al.SetSourceProperty(p.Source, SourceBoolean.SourceRelative, true);
            al.SetSourceProperty(p.Source, SourceVector3.Position, 0, 0, 0);
        }
        SetGain(p, EffectiveVolume(e));
        al.SourcePlay(p.Source);
        return true;
    }

    void SetGain(PlayingAudio p, float gain)
    {
        if (al == null)
            return;
        // Off-screen sounds with a low-pass cutoff are muffled (Miles' occlusion); approximated as quieter.
        if (p.Type == PlayingType.Sample3D && p.Event.Info.LowPassFrequency > 0 && p.Event.CurrentPosition() is { } at && !IsOnScreen(at))
            gain *= 0.5f + 0.5f * p.Event.Info.LowPassFrequency;
        al.SetSourceProperty(p.Source, SourceFloat.Gain, Math.Max(0, gain));
    }

    /// <summary>The original's getEffectiveVolume: event volume, the right master volume, and distance falloff.</summary>
    float EffectiveVolume(AudioEventRts e)
    {
        float volume = e.Volume * e.VolumeShift;
        switch (e.Info.Kind)
        {
            case AudioType.Music:
                return volume * musicVolume;
            case AudioType.Streaming:
                return volume * speechVolume;
        }
        if (!e.IsPositionalAudio)
            return volume * soundVolume;
        volume *= sound3DVolume;
        if (e.CurrentPosition() is { } pos)
        {
            bool global = e.Info.Type.HasFlag(SoundType.Global);
            float min = global ? Settings.GlobalMinRange : e.Info.MinDistance;
            float max = global ? Settings.GlobalMaxRange : e.Info.MaxDistance;
            float distance = Vector3.Distance(listenerPosition, pos);
            if (distance > min && min > 0)
                volume *= min / distance;
            if (distance >= max)
                volume = 0;
        }
        return volume;
    }

    bool IsSourceStopped(PlayingAudio p)
    {
        if (al == null)
            return true;
        al.GetSourceProperty(p.Source, GetSourceInteger.SourceState, out int state);
        return state != (int)SourceState.Playing && state != (int)SourceState.Paused;
    }

    void ProcessPlaying()
    {
        void Sweep(List<PlayingAudio> list, bool positional)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p.Status == PlayingStatus.Playing && p.Type != PlayingType.Stream && IsSourceStopped(p))
                    OnCompleted(p);
                if (p.Status == PlayingStatus.Stopped)
                {
                    Release(p);
                    list.RemoveAt(i--);
                    continue;
                }
                if (positional)
                {
                    var pos = p.Event.CurrentPosition();
                    if (pos == null)
                    {
                        Release(p);
                        list.RemoveAt(i--);
                        continue;
                    }
                    if (p.Event.IsDead)
                    {
                        p.RequestStop = true;
                        continue;
                    }
                    float consider = EffectiveVolume(p.Event) / (sound3DVolume > 0 ? soundVolume : 1);
                    bool anyway = p.Event.Info.Type.HasFlag(SoundType.Global) || p.Event.Info.Priority == AudioPriority.Critical;
                    if (consider < Settings.MinVolume && !anyway)
                    {
                        Release(p);
                        list.RemoveAt(i--);
                        continue;
                    }
                    if (al != null)
                        al.SetSourceProperty(p.Source, SourceVector3.Position, pos.Value.X, pos.Value.Y, pos.Value.Z);
                    SetGain(p, EffectiveVolume(p.Event));
                }
                else if (volumeHasChanged)
                    SetGain(p, EffectiveVolume(p.Event));
            }
        }
        Sweep(playingSounds, false);
        Sweep(playing3DSounds, true);
        for (int i = 0; i < playingStreams.Count; i++)
        {
            var s = playingStreams[i];
            if (s.Status == PlayingStatus.Playing && (s.Stream?.IsFinished(al, s.Source) ?? true))
                OnCompleted(s);
            if (s.Status == PlayingStatus.Stopped)
            {
                Release(s);
                playingStreams.RemoveAt(i--);
            }
            else if (volumeHasChanged)
                SetGain(s, EffectiveVolume(s.Event));
        }
        volumeHasChanged = false;
    }

    /// <summary>A portion finished (the original's notifyOfAudioCompletion): loop, go on to the next portion, or stop.</summary>
    void OnCompleted(PlayingAudio p)
    {
        var e = p.Event;
        if (e.Info.Kind == AudioType.Streaming && disallowSpeech)
            disallowSpeech = false;
        if (e.Info.Control.HasFlag(AudioControl.Loop))
        {
            if (e.NextPortion == PortionToPlay.Attack)
                e.NextPortion = PortionToPlay.Sound;
            if (e.NextPortion == PortionToPlay.Sound)
            {
                e.DecreaseLoopCount();
                if (StartNextLoop(p))
                    return;
            }
        }
        e.AdvanceNextPortion();
        if (e.NextPortion != PortionToPlay.Done && p.Type != PlayingType.Stream && PlaySample(p))
            return;
        if (p.Type == PlayingType.Stream && e.Info.Kind == AudioType.Music && !p.RequestStop)
        {
            p.Completions++;
            p.Stream?.Restart(al, p.Source);
            return;
        }
        p.Status = PlayingStatus.Stopped;
    }

    bool StartNextLoop(PlayingAudio p)
    {
        if (p.RequestStop || !p.Event.HasMoreLoops)
            return false;
        p.Event.GenerateFilename();
        if (p.Event.Delay > MsPerLogicFrame)
        {
            // The next loop waits: hand the event back to the request list.
            p.CleanupEvent = false;
            p.RequestStop = true;
            p.Status = PlayingStatus.Stopped;
            requests.Add(new Request { Play = true, Event = p.Event, RequiresCheckForSample = true });
            return true;
        }
        return PlaySample(p);
    }

    void StopAudioEvent(int handle)
    {
        foreach (var s in playingStreams.Where(s => s.Event.Handle == handle).ToList())
        {
            s.RequestStop = true;
            s.Status = PlayingStatus.Stopped;
        }
        foreach (var p in playingSounds.Concat(playing3DSounds))
            if (p.Event.Handle == handle)
                p.RequestStop = true;
        // A request not yet started is simply dropped.
        requests.RemoveAll(r => r.Play && r.Event?.Handle == handle);
    }

    /// <summary>Stops and forgets a sound at once (the original's killAudioEventImmediately).</summary>
    public void KillAudioEventImmediately(int handle)
    {
        if (requests.RemoveAll(r => r.Play && r.Event?.Handle == handle) > 0)
            return;
        foreach (var list in new[] { playing3DSounds, playingSounds, playingStreams })
            if (list.FirstOrDefault(p => p.Event.Handle == handle) is { } p)
            {
                Release(p);
                list.Remove(p);
                return;
            }
    }

    public bool IsCurrentlyPlaying(int handle) =>
        playingSounds.Concat(playing3DSounds).Concat(playingStreams).Any(p => p.Event.Handle == handle)
        || requests.Any(r => r.Play && r.Event?.Handle == handle);

    void StopAllSpeech()
    {
        foreach (var s in playingStreams.Where(s => s.Event.Info.Kind == AudioType.Streaming).ToList())
        {
            Release(s);
            playingStreams.Remove(s);
        }
    }

    /// <summary>Fades out and stops the music (AHSV_StopTheMusicFade).</summary>
    public void StopMusic(bool fade)
    {
        // Music asked for but not started yet is dropped too.
        requests.RemoveAll(r => r.Play && r.Event?.Info.Kind == AudioType.Music);
        foreach (var s in playingStreams.Where(s => s.Event.Info.Kind == AudioType.Music).ToList())
        {
            playingStreams.Remove(s);
            if (fade)
                fading.Add(s);
            else
                Release(s);
        }
    }

    void ProcessFading()
    {
        for (int i = 0; i < fading.Count; i++)
        {
            var p = fading[i];
            if (p.FramesFaded >= Settings.FadeAudioFrames)
            {
                Release(p);
                fading.RemoveAt(i--);
                continue;
            }
            p.FramesFaded++;
            SetGain(p, EffectiveVolume(p.Event) * (1f - (float)p.FramesFaded / Math.Max(1, Settings.FadeAudioFrames)));
            p.Stream?.Pump(al!, p.Source);
        }
    }

    void Release(PlayingAudio p)
    {
        if (p.Event.Info.Kind == AudioType.SoundEffect)
        {
            if (p.Type == PlayingType.Sample) numPlaying2D = Math.Max(0, numPlaying2D - 1);
            else if (p.Type == PlayingType.Sample3D) numPlaying3D = Math.Max(0, numPlaying3D - 1);
        }
        if (al != null)
        {
            al.SourceStop(p.Source);
            switch (p.Type)
            {
                case PlayingType.Sample:
                    al.SetSourceProperty(p.Source, SourceInteger.Buffer, 0);
                    available2D.Push(p.Source);
                    break;
                case PlayingType.Sample3D:
                    al.SetSourceProperty(p.Source, SourceInteger.Buffer, 0);
                    available3D.Push(p.Source);
                    break;
                case PlayingType.Stream:
                    p.Stream?.Dispose(al, p.Source);
                    al.DeleteSource(p.Source);
                    break;
            }
        }
        p.Status = PlayingStatus.Stopped;
    }

    // ---- Files ----

    uint? LoadBuffer(string path)
    {
        if (al == null)
            return files.Exists(path) ? 0u : null;
        if (bufferCache.TryGetValue(path, out uint cached))
            return cached;
        if (missingFiles.Contains(path))
            return null;
        try
        {
            var pcm = WavFile.Decode(files.ReadAllBytes(path));
            uint buffer = al.GenBuffer();
            var format = pcm.Channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;
            fixed (short* data = pcm.Samples)
                al.BufferData(buffer, format, data, pcm.Samples.Length * sizeof(short), pcm.SampleRate);
            bufferCache[path] = buffer;
            return buffer;
        }
        catch (Exception ex)
        {
            if (missingFiles.Add(path))
                Console.Error.WriteLine($"Sound {path}: {ex.Message}");
            return null;
        }
    }

    PlayingAudio? OpenStream(AudioEventRts e)
    {
        var p = new PlayingAudio { Event = e, Type = PlayingType.Stream };
        if (al == null)
            return files.Exists(e.Filename) ? p : null;
        try
        {
            var stream = MusicStream.Open(files, e.Filename);
            if (stream == null)
                return null;
            p.Stream = stream;
            p.Source = al.GenSource();
            al.SetSourceProperty(p.Source, SourceBoolean.SourceRelative, true);
            al.SetSourceProperty(p.Source, SourceVector3.Position, 0, 0, 0);
            SetGain(p, EffectiveVolume(e));
            stream.Start(al, p.Source);
            return p;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Stream {e.Filename}: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<string> MusicTracks => musicTracks;

    public void Dispose()
    {
        if (al == null || alc == null)
            return;
        foreach (var list in new[] { playingSounds, playing3DSounds, playingStreams, fading })
            foreach (var p in list.ToList())
                Release(p);
        foreach (uint s in available2D.Concat(available3D))
            al.DeleteSource(s);
        foreach (uint b in bufferCache.Values)
            al.DeleteBuffer(b);
        alc.MakeContextCurrent(null);
        alc.DestroyContext(context);
        alc.CloseDevice(device);
    }
}

public enum AudioRelationship { Self, Ally, Enemy, Neutral }

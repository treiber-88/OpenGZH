using System.Numerics;
using OpenGZH.Core.Rules;

namespace OpenGZH.Client.Audio;

public enum AudioType { Music, Streaming, SoundEffect }
public enum AudioPriority { Lowest, Low, Normal, High, Critical }

[Flags]
public enum SoundType
{
    None = 0,
    UI = 0x0001, World = 0x0002, Shrouded = 0x0004, Global = 0x0008, Voice = 0x0010,
    Player = 0x0020, Allies = 0x0040, Enemies = 0x0080, Everyone = 0x0100,
}

[Flags]
public enum AudioControl { None = 0, Loop = 0x01, Random = 0x02, All = 0x04, PostDelay = 0x08, Interrupt = 0x10 }

/// <summary>
/// One sound definition (the original's AudioEventInfo) from sound-effects.yaml / voices.yaml
/// (AudioEvents), speech.yaml (DialogEvents) or music.yaml (MusicTracks). Each starts as a copy of the
/// section's default (DefaultSoundEffect / DefaultDialog / DefaultMusicTrack). Volumes are 0..1.
/// </summary>
public sealed class AudioEventInfo
{
    public string Name = "";
    public string Filename = "";
    public float Volume, VolumeShift, MinVolume;
    public float PitchShiftMin = 1, PitchShiftMax = 1;
    public int DelayMin, DelayMax;
    public int Limit, LoopCount;
    public AudioPriority Priority = AudioPriority.Lowest;
    public SoundType Type;
    public AudioControl Control;
    public List<string> Sounds = new(), SoundsMorning = new(), SoundsNight = new(), SoundsEvening = new();
    public List<string> AttackSounds = new(), DecaySounds = new();
    public float LowPassFrequency, MinDistance, MaxDistance;
    /// <summary>Sound effect, speech (streamed) or music.</summary>
    public AudioType Kind;

    public bool IsPermanentSound => Control.HasFlag(AudioControl.Loop) && LoopCount == 0;

    public AudioEventInfo Clone()
    {
        var c = (AudioEventInfo)MemberwiseClone();
        c.Sounds = new(Sounds); c.SoundsMorning = new(SoundsMorning); c.SoundsNight = new(SoundsNight); c.SoundsEvening = new(SoundsEvening);
        c.AttackSounds = new(AttackSounds); c.DecaySounds = new(DecaySounds);
        return c;
    }

    static float Percent(string text) => RuleValue.Float(text.Trim().TrimEnd('%')) / 100f;

    /// <summary>Applies one definition's fields on top of this (a copy of the default).</summary>
    public void Apply(RuleNode node)
    {
        foreach (var (key, value) in node.Entries)
        {
            string v = value.IsScalar ? value.Value ?? "" : string.Join(' ', value.AsList().Select(n => n.Value));
            switch (key)
            {
                case "Filename": Filename = v; break;
                case "Volume": Volume = Percent(v); break;
                case "VolumeShift": VolumeShift = Percent(v); break;
                case "MinVolume": MinVolume = Percent(v); break;
                case "PitchShift":
                {
                    var w = RuleValue.Words(v);
                    float min = w.Length > 0 ? RuleValue.Float(w[0]) : 0, max = w.Length > 1 ? RuleValue.Float(w[1]) : min;
                    PitchShiftMin = 1f + min / 100f;
                    PitchShiftMax = 1f + max / 100f;
                    break;
                }
                case "Delay":
                {
                    var w = RuleValue.Words(v);
                    DelayMin = w.Length > 0 ? RuleValue.Int(w[0]) : 0;
                    DelayMax = w.Length > 1 ? RuleValue.Int(w[1]) : DelayMin;
                    break;
                }
                case "Limit": Limit = RuleValue.Int(v); break;
                case "LoopCount": LoopCount = RuleValue.Int(v); break;
                case "Priority":
                    Priority = v.Trim().ToUpperInvariant() switch
                    {
                        "LOWEST" => AudioPriority.Lowest, "LOW" => AudioPriority.Low, "NORMAL" => AudioPriority.Normal,
                        "HIGH" => AudioPriority.High, "CRITICAL" => AudioPriority.Critical, _ => Priority,
                    };
                    break;
                case "Type":
                    Type = SoundType.None;
                    foreach (var word in RuleValue.Words(v))
                        if (Enum.TryParse<SoundType>(word, true, out var t))
                            Type |= t;
                    break;
                case "Control":
                    Control = AudioControl.None;
                    foreach (var word in RuleValue.Words(v))
                        if (Enum.TryParse<AudioControl>(word, true, out var c))
                            Control |= c;
                    break;
                case "Sounds": Sounds = RuleValue.Words(v).ToList(); break;
                case "SoundsNight": SoundsNight = RuleValue.Words(v).ToList(); break;
                case "SoundsEvening": SoundsEvening = RuleValue.Words(v).ToList(); break;
                case "SoundsMorning": SoundsMorning = RuleValue.Words(v).ToList(); break;
                case "Attack": AttackSounds = RuleValue.Words(v).ToList(); break;
                case "Decay": DecaySounds = RuleValue.Words(v).ToList(); break;
                case "MinRange": MinDistance = RuleValue.Float(v); break;
                case "MaxRange": MaxDistance = RuleValue.Float(v); break;
                case "LowPassCutoff": LowPassFrequency = Percent(v); break;
            }
        }
    }
}

/// <summary>The audio folders and output settings (the original's AudioSettings, audio-settings.yaml).</summary>
public sealed class AudioSettings
{
    public string AudioRoot = @"Data\Audio", SoundsFolder = "Sounds", MusicFolder = "Tracks", StreamingFolder = "Speech", SoundsExtension = "wav";
    public int SampleCount2D = 4, SampleCount3D = 25, StreamCount = 3;
    public float GlobalMinRange = 5000, GlobalMaxRange = 500000;
    public int FadeAudioFrames = 60;
    public float MinVolume = 0.02f, Relative2DVolume = -0.1f;
    public float DefaultSoundVolume = 0.8f, Default3DSoundVolume = 0.8f, DefaultSpeechVolume = 0.7f, DefaultMusicVolume = 0.55f;
    public float MicrophoneDesiredHeightAboveTerrain = 50, MicrophoneMaxPercentageBetweenGroundAndCamera = 0.333f;
    public float ZoomMinDistance = 130, ZoomMaxDistance = 425, ZoomSoundVolumePercentageAmount = 0.2f;

    public static AudioSettings From(RuleNode node)
    {
        var s = new AudioSettings();
        string S(string key, string fallback) => node.GetString(key, fallback);
        float P(string key, float fallback) => node.Has(key) ? RuleValue.Float(node.GetString(key).Trim().TrimEnd('%')) / 100f : fallback;
        s.AudioRoot = S("AudioRoot", s.AudioRoot);
        s.SoundsFolder = S("SoundsFolder", s.SoundsFolder);
        s.MusicFolder = S("MusicFolder", s.MusicFolder);
        s.StreamingFolder = S("StreamingFolder", s.StreamingFolder);
        s.SoundsExtension = S("SoundsExtension", s.SoundsExtension);
        s.SampleCount2D = RuleValue.Int(S("SampleCount2D", "4"), 4);
        s.SampleCount3D = RuleValue.Int(S("SampleCount3D", "25"), 25);
        s.StreamCount = RuleValue.Int(S("StreamCount", "3"), 3);
        s.GlobalMinRange = RuleValue.Float(S("GlobalMinRange", "5000"));
        s.GlobalMaxRange = RuleValue.Float(S("GlobalMaxRange", "500000"));
        s.FadeAudioFrames = (int)MathF.Ceiling(RuleValue.Float(S("TimeToFadeAudio", "2000")) * 30 / 1000f);
        s.MinVolume = P("MinSampleVolume", s.MinVolume);
        s.Relative2DVolume = P("Relative2DVolume", s.Relative2DVolume);
        s.DefaultSoundVolume = P("DefaultSoundVolume", s.DefaultSoundVolume);
        s.Default3DSoundVolume = P("Default3DSoundVolume", s.Default3DSoundVolume);
        s.DefaultSpeechVolume = P("DefaultSpeechVolume", s.DefaultSpeechVolume);
        s.DefaultMusicVolume = P("DefaultMusicVolume", s.DefaultMusicVolume);
        s.MicrophoneDesiredHeightAboveTerrain = RuleValue.Float(S("MicrophoneDesiredHeightAboveTerrain", "50"));
        s.MicrophoneMaxPercentageBetweenGroundAndCamera = P("MicrophoneMaxPercentageBetweenGroundAndCamera", s.MicrophoneMaxPercentageBetweenGroundAndCamera);
        s.ZoomMinDistance = RuleValue.Float(S("ZoomMinDistance", "130"));
        s.ZoomMaxDistance = RuleValue.Float(S("ZoomMaxDistance", "425"));
        s.ZoomSoundVolumePercentageAmount = P("ZoomSoundVolumePercentageAmount", s.ZoomSoundVolumePercentageAmount);
        return s;
    }
}

public enum OwnerType { Positional, Object, Dead, Invalid }
public enum PortionToPlay { Attack, Sound, Decay, Done }

/// <summary>
/// One request to play a sound and its progress (the original's AudioEventRTS): which of the event's
/// files plays, attack / main / decay portions, loops, pitch and volume variation, and where it is.
/// </summary>
public sealed class AudioEventRts
{
    public AudioEventInfo Info { get; }
    public string EventName => Info.Name;
    public int Handle;
    public int HandleToKill;
    public float VolumeOverride = -1;
    public float PitchShift = 1, VolumeShift;
    public float Delay;
    public int LoopCount = 1;
    public int PlayingAudioIndex = -1;
    int allCount;
    public int PlayerIndex = -1;
    public bool Uninterruptable;
    public PortionToPlay NextPortion = PortionToPlay.Attack;
    public string Filename = "", AttackName = "", DecayName = "";
    public OwnerType Owner = OwnerType.Invalid;
    public uint ObjectId;
    public Vector3 Position;

    readonly AudioManager manager;

    public AudioEventRts(AudioManager manager, AudioEventInfo info)
    {
        this.manager = manager;
        Info = info;
    }

    public AudioEventRts CloneForPlay() => (AudioEventRts)MemberwiseClone();

    public void SetPosition(Vector3 position)
    {
        if (Owner is OwnerType.Positional or OwnerType.Invalid)
        {
            Position = position;
            Owner = OwnerType.Positional;
        }
    }

    public void SetObject(uint id)
    {
        if (Owner is OwnerType.Object or OwnerType.Invalid)
        {
            ObjectId = id;
            Owner = OwnerType.Object;
        }
    }

    public float Volume => VolumeOverride == -1 ? Info.Volume : VolumeOverride;

    public bool IsDead => Owner == OwnerType.Dead;

    public bool IsPositionalAudio =>
        Info.Type.HasFlag(SoundType.World) && Owner != OwnerType.Invalid && (ObjectId != 0 || Owner is OwnerType.Positional or OwnerType.Dead);

    /// <summary>Where it is now: an object's sound follows the object until it dies.</summary>
    public Vector3? CurrentPosition()
    {
        switch (Owner)
        {
            case OwnerType.Positional:
            case OwnerType.Dead:
                return Position;
            case OwnerType.Object:
                if (manager.FindObjectPosition(ObjectId) is { } p)
                    Position = p;
                else
                    Owner = OwnerType.Dead;
                return Position;
            default:
                return null;
        }
    }

    public int GetPlayerIndex() => Owner == OwnerType.Object && manager.FindObjectOwnerIndex(ObjectId) is { } index ? index : PlayerIndex;

    string Prefix(bool localized)
    {
        var s = manager.Settings;
        string folder = Info.Kind switch
        {
            AudioType.Music => s.MusicFolder,
            AudioType.Streaming => s.StreamingFolder,
            _ => s.SoundsFolder,
        };
        return $@"{s.AudioRoot}\{folder}\" + (localized ? $@"{manager.Language}\" : "");
    }

    string Extension => Info.Kind == AudioType.Music ? "" : "." + manager.Settings.SoundsExtension;

    /// <summary>Prefers the localized copy (Sounds\English\x.wav) when it exists.</summary>
    string Localize(string path)
    {
        int slash = path.LastIndexOf('\\');
        if (slash < 0)
            return path;
        string localized = Prefix(true) + path[(slash + 1)..];
        return manager.FileExists(localized) ? localized : path;
    }

    /// <summary>Picks the file for the next play (random without immediate repeats, or in order).</summary>
    public void GenerateFilename()
    {
        Filename = Prefix(false);
        if (Info.Kind is AudioType.Music or AudioType.Streaming)
        {
            Filename = Localize(Filename + Info.Filename);
            return;
        }
        if (Info.Sounds.Count == 0)
        {
            Filename = "";
            return;
        }
        int which;
        if (Info.Control.HasFlag(AudioControl.Random))
        {
            which = Random.Shared.Next(Info.Sounds.Count);
            if (which == PlayingAudioIndex && Info.Sounds.Count > 2)
                which = (which + 1) % Info.Sounds.Count;
            PlayingAudioIndex = which;
        }
        else
            which = ++PlayingAudioIndex % Info.Sounds.Count;
        Filename = Localize(Filename + Info.Sounds[which] + Extension);
        Delay = Info.DelayMin == Info.DelayMax ? Info.DelayMin : Info.DelayMin + (float)Random.Shared.NextDouble() * (Info.DelayMax - Info.DelayMin);
    }

    static float RandomReal(float a, float b) => a == b ? a : a + (float)Random.Shared.NextDouble() * (b - a);

    public void GeneratePlayInfo()
    {
        PitchShift = RandomReal(Info.PitchShiftMin, Info.PitchShiftMax);
        VolumeShift = RandomReal(1f + Info.VolumeShift, 1f);
        LoopCount = Info.LoopCount;
        NextPortion = PortionToPlay.Attack;
        if (Info.AttackSounds.Count > 0)
            AttackName = Localize(Prefix(false) + Info.AttackSounds[Random.Shared.Next(Info.AttackSounds.Count)] + Extension);
        else
            NextPortion = PortionToPlay.Sound;
        if (Info.DecaySounds.Count > 0)
            DecayName = Localize(Prefix(false) + Info.DecaySounds[Random.Shared.Next(Info.DecaySounds.Count)] + Extension);
    }

    public void AdvanceNextPortion()
    {
        switch (NextPortion)
        {
            case PortionToPlay.Attack:
                NextPortion = PortionToPlay.Sound;
                break;
            case PortionToPlay.Sound:
                if (Info.Control.HasFlag(AudioControl.All))
                {
                    if (allCount == Info.Sounds.Count)
                        NextPortion = PortionToPlay.Decay;
                    ++allCount;
                }
                NextPortion = DecayName.Length > 0 ? PortionToPlay.Decay : PortionToPlay.Done;
                break;
            case PortionToPlay.Decay:
                NextPortion = PortionToPlay.Done;
                break;
        }
    }

    public void DecreaseLoopCount()
    {
        if (LoopCount == 1)
            LoopCount = -1;
        else if (LoopCount > 1)
            --LoopCount;
    }

    public bool HasMoreLoops => LoopCount >= 0;

    /// <summary>The file for the portion about to play.</summary>
    public string FileForPortion => NextPortion switch
    {
        PortionToPlay.Attack => AttackName,
        PortionToPlay.Decay => DecayName,
        _ => Filename,
    };
}

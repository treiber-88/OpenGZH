using System.Numerics;
using OpenGZH.Client.Audio;
using OpenGZH.Client.Effects;
using OpenGZH.Formats.Audio;
using OpenGZH.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Particle systems, FX lists and audio: every definition in the game data works.</summary>
public class EffectsAndAudioTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    sealed class FakeWorld : IParticleWorld, IFxHost
    {
        public int Frame { get; set; }
        public ParticleSystemManager Particles { get; set; } = null!;
        public int Sounds, Shakes, Scorches, Tracers;
        public GameObject? FindObject(uint id) => null;
        public Matrix4x4 ObjectTransform(GameObject obj) => Matrix4x4.Identity;
        public bool IsHiddenFromLocalPlayer(GameObject obj) => false;
        public float GroundHeight(float x, float y) => 0;
        public float LayerHeight(float x, float y) => 0;
        public void PlaySound(string name, Vector3? position, GameObject? source) => Sounds++;
        public void Shake(Vector3 epicenter, string type) => Shakes++;
        public void Scorch(Vector3 position, float radius, int type) => Scorches++;
        public void LightPulse(Vector3 position, Vector3 color, float radius, int increaseFrames, int decreaseFrames) { }
        public void Tracer(Vector3 from, Vector3 to, float speed, float length, float width, Vector3 color, float decayAt) => Tracers++;
        public bool IsPointVisible(Vector3 position) => true;
        public bool IsObjectVisible(GameObject obj) => true;
        public bool TryGetBone(GameObject obj, string bone, out Matrix4x4 world)
        {
            world = Matrix4x4.Identity;
            return false;
        }
    }

    [Fact]
    public void EveryParticleSystemRuns()
    {
        if (!content.Available) return;
        var world = new FakeWorld();
        var manager = new ParticleSystemManager(world, name => content.Rules!.Get("ParticleSystems", name)) { MaxParticleCount = 100000 };
        world.Particles = manager;
        int created = 0;
        foreach (var name in content.Rules!.Section("ParticleSystems").Keys)
        {
            var system = manager.CreateSystem(name);
            Assert.NotNull(system);
            system!.SetPosition(new Vector3(100, 100, 10));
            created++;
        }
        int maxParticles = 0;
        for (int frame = 1; frame <= 120; frame++)
        {
            world.Frame = frame;
            manager.Update();
            maxParticles = Math.Max(maxParticles, manager.ParticleCount);
        }
        output.WriteLine($"{created} systems, up to {maxParticles} particles, {manager.Systems.Count} systems still alive");
        Assert.True(created > 1000);
        Assert.True(maxParticles > 1000, "the systems emit particles");
    }

    [Fact]
    public void EveryFxListPlays()
    {
        if (!content.Available) return;
        var world = new FakeWorld();
        world.Particles = new ParticleSystemManager(world, name => content.Rules!.Get("ParticleSystems", name));
        var fx = new FxPlayer(world, name => content.Rules!.Get("FXLists", name));
        int lists = 0;
        foreach (var name in content.Rules!.Section("FXLists").Keys)
        {
            fx.DoFxPos(name, new Vector3(50, 50, 0), Matrix4x4.Identity, 10, new Vector3(150, 50, 0), 20);
            lists++;
        }
        output.WriteLine($"{lists} FX lists: {world.Particles.Systems.Count} particle systems, {world.Sounds} sounds, {world.Shakes} shakes, {world.Scorches} scorches, {world.Tracers} tracers");
        Assert.True(world.Particles.Systems.Count > 500);
        Assert.True(world.Sounds > 200 && world.Shakes > 100 && world.Scorches > 30 && world.Tracers > 5);
    }

    [Fact]
    public void EveryGameSoundDecodes()
    {
        if (!content.Available) return;
        var fs = content.FileSystem!;
        int ok = 0;
        var failures = new List<string>();
        foreach (var path in fs.List("Data/Audio", ".wav", recursive: true))
        {
            try
            {
                var pcm = WavFile.Decode(fs.ReadAllBytes(path));
                if (pcm.Samples.Length == 0)
                    failures.Add($"{path}: empty");
                else
                    ok++;
            }
            catch (Exception e)
            {
                failures.Add($"{path}: {e.Message}");
            }
        }
        output.WriteLine($"{ok} sounds decoded, {failures.Count} failed");
        foreach (var f in failures.Take(10))
            output.WriteLine(f);
        Assert.True(ok > 5000);
        Assert.Empty(failures);
    }

    [Fact]
    public void AudioEventsFindTheirFiles()
    {
        if (!content.Available) return;
        var audio = new AudioManager(content.FileSystem!, content.Rules!, openDevice: false);
        int found = 0;
        var missing = new List<string>();
        foreach (var section in new[] { "AudioEvents", "DialogEvents", "MusicTracks" })
            foreach (var name in content.Rules!.Section(section).Keys)
            {
                var info = audio.FindInfo(name)!;
                if (name.StartsWith("Default"))
                    continue;
                var e = new AudioEventRts(audio, info);
                int count = info.Kind is AudioType.Music or AudioType.Streaming ? 1 : info.Sounds.Count;
                for (int i = 0; i < count; i++)
                {
                    e.GenerateFilename();
                    if (e.Filename.Length == 0)
                        continue;
                    if (content.FileSystem!.Exists(e.Filename))
                        found++;
                    else
                        missing.Add($"{name}: {e.Filename}");
                }
            }
        output.WriteLine($"{found} files found, {missing.Count} missing");
        foreach (var m in missing.Take(15))
            output.WriteLine(m);
        Assert.True(found > 3000);
        // The original data refers to a few files it never shipped; nearly everything must be there.
        Assert.True(missing.Count < found / 50, $"{missing.Count} sounds are missing");
    }

    [Fact]
    public void PlayerSoundsAreOnlyHeardByTheirPlayer()
    {
        if (!content.Available) return;
        var audio = new AudioManager(content.FileSystem!, content.Rules!, openDevice: false) { LocalPlayerIndex = 1 };
        // RangerVoiceSelect is "ui voice player": only Ranger's owner hears it.
        Assert.NotEqual(0, audio.AddAudioEvent("RangerVoiceSelect", playerIndex: 1));
        Assert.Equal(0, audio.AddAudioEvent("RangerVoiceSelect", playerIndex: 2));
        // A world sound for everyone, far away from the microphone, is not played.
        audio.SetCamera(Vector3.Zero, new Vector3(0, -100, 300), 0, 0);
        Assert.Equal(0, audio.AddAudioEvent("Explosion", new Vector3(5000, 5000, 0)));
        Assert.NotEqual(0, audio.AddAudioEvent("Explosion", new Vector3(20, 20, 0)));
    }
}

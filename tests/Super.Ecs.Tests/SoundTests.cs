using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Super.Testing;
using AudioService = Super.Audio;

namespace Super.Ecs.Tests;

public class SoundTests
{
    public enum Screen { Menu, Forest, Cave }

    /// <summary>Looks from the origin down −Z, so −X is straight to the left and +X to the right.</summary>
    private sealed class Hearing(World world, AudioService audio, Content content) : IGame
    {
        public World World => world;

        public AudioService Audio => audio;

        public SoundRef Creak { get; private set; }

        public SoundRef Hum { get; private set; }

        public AmbienceRef Forest { get; private set; }

        public AmbienceRef Cave { get; private set; }

        /// <summary>What Update does, for a test that moves things during a tick.</summary>
        public Action? Step { get; set; }

        public void Load()
        {
            Creak = content.Sound("creak.wav");
            Hum = content.Sound("hum.wav");
            Forest = content.Ambience("forest.ambience.json");
            Cave = content.Ambience("cave.ambience.json");
        }

        public void Update(GameTime time) => Step?.Invoke();

        public void Draw(View view) => view.Camera(Vector3.Zero, -Vector3.UnitZ);
    }

    private static byte[] Wav(double seconds)
    {
        const int rate = 48_000;
        int frames = (int)(seconds * rate);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(36 + frames * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(frames * 2);
        for (int i = 0; i < frames; i++)
        {
            w.Write((short)16384);
        }

        w.Flush();
        return stream.ToArray();
    }

    private static HeadlessRun<Hearing> Start()
    {
        var files = new MemoryContent()
            .Add("creak.wav", Wav(4))
            .Add("hum.wav", Wav(4))
            .Add("forest.ambience.json", System.Text.Encoding.UTF8.GetBytes("""{ "layers": [ { "file": "creak.wav" } ] }"""))
            .Add("cave.ambience.json", System.Text.Encoding.UTF8.GetBytes("""{ "layers": [ { "file": "hum.wav" } ] }"""));
        return HeadlessGame.Create<Hearing>(1, services => services.AddSuperEcs().AddContentFiles(files));
    }

    private static Entity Make(HeadlessRun<Hearing> run, SoundRef clip, Vector3? at = null, float volume = 1)
    {
        World world = run.Game.World;
        Entity entity = world.Create();
        world.Add(entity, new Sound(clip, volume));
        if (at is Vector3 position)
        {
            world.Add(entity, Transform.At(position));
        }

        return entity;
    }

    private static PlayingSound Find(HeadlessRun<Hearing> run, string clip)
    {
        return Assert.Single(run.Sounds, sound => sound.Clip == clip);
    }

    [Fact]
    public void AddedSoundPlaysFromTheEntity()
    {
        using var run = Start();
        Make(run, run.Game.Creak, new Vector3(-10f, 0f, 0f));

        run.Ticks(1);

        PlayingSound creak = Find(run, "creak.wav");
        Assert.Equal(Channel.Effects, creak.Channel);
        Assert.True(creak.Looping);
        Assert.Equal(-1f, creak.Pan, 0.001f);
    }

    [Fact]
    public void SoundFollowsTheEntity()
    {
        using var run = Start();
        Entity entity = Make(run, run.Game.Creak, new Vector3(-10f, 0f, 0f));
        run.Ticks(2);

        run.Game.World.Add(entity, Transform.At(new Vector3(10f, 0f, 0f)));
        run.Ticks(1);

        Assert.Equal(1f, Find(run, "creak.wav").Pan, 0.001f);
    }

    [Fact]
    public void RemovedSoundFadesOut()
    {
        using var run = Start();
        Entity entity = Make(run, run.Game.Creak);
        run.Ticks(6);
        float before = Find(run, "creak.wav").Gain;

        run.Game.World.Remove<Sound>(entity);
        run.Ticks(2);
        Assert.True(Find(run, "creak.wav").Gain < before);

        // 0.3 s is 9 ticks at 30 a second.
        run.Ticks(9);
        Assert.Empty(run.Sounds);
    }

    [Fact]
    public void DestroyedEntityStopsItsSound()
    {
        using var run = Start();
        Entity entity = Make(run, run.Game.Creak, new Vector3(3f, 0f, 0f));
        run.Ticks(3);

        run.Game.World.Destroy(entity);
        run.Ticks(12);

        Assert.Empty(run.Sounds);
    }

    [Fact]
    public void ReusedIdStartsAFreshSound()
    {
        using var run = Start();
        World world = run.Game.World;
        Entity old = Make(run, run.Game.Creak, new Vector3(-10f, 0f, 0f));
        run.Ticks(6);

        // Within one tick, so the next entity takes the id and the frame sees the same clip under the same id.
        world.Destroy(old);
        Entity fresh = Make(run, run.Game.Creak, new Vector3(10f, 0f, 0f));
        Assert.Equal(old.Id, fresh.Id);
        run.Ticks(1);

        Assert.Equal(2, run.Sounds.Count);
        Assert.Contains(run.Sounds, sound => sound.Pan < -0.99f);
        Assert.Contains(run.Sounds, sound => sound.Pan > 0.99f);
        run.Ticks(12);
        PlayingSound only = Assert.Single(run.Sounds);
        Assert.Equal(1f, only.Pan, 0.001f);
    }

    [Fact]
    public void ReusedIdWithAnotherClipSwapsThem()
    {
        using var run = Start();
        World world = run.Game.World;
        Entity old = Make(run, run.Game.Creak);
        run.Ticks(6);

        world.Destroy(old);
        Make(run, run.Game.Hum);
        run.Ticks(12);

        Assert.Equal("hum.wav", Assert.Single(run.Sounds).Clip);
    }

    [Fact]
    public void LeavingTheScopedStateFadesTheAmbience()
    {
        using var run = Start();
        run.States<Screen>().Set(Screen.Forest);
        run.Ticks(1);
        World world = run.Game.World;
        Entity forest = world.Create();
        world.Add(forest, new Ambience(run.Game.Forest));
        world.Add(forest, new ScopedTo<Screen>(Screen.Forest));

        // Fades in over 2 s, which is 60 ticks.
        run.Ticks(70);
        PlayingSound layer = Find(run, "creak.wav");
        Assert.Equal(Channel.Ambience, layer.Channel);
        float full = layer.Gain;
        Assert.True(full > 0f);

        run.States<Screen>().Set(Screen.Cave);
        run.Ticks(4);
        Assert.False(world.IsAlive(forest));
        Assert.True(Find(run, "creak.wav").Gain < full);

        run.Ticks(70);
        Assert.Empty(run.Sounds);
    }

    [Fact]
    public void ReplacedAmbienceFadesTheOldLayersAndStartsTheNew()
    {
        using var run = Start();
        Entity entity = run.Game.World.Create();
        run.Game.World.Add(entity, new Ambience(run.Game.Forest));
        run.Ticks(70);
        float full = Find(run, "creak.wav").Gain;

        run.Game.World.Add(entity, new Ambience(run.Game.Cave));
        run.Ticks(4);
        Assert.True(Find(run, "creak.wav").Gain < full);
        Assert.Equal(Channel.Ambience, Find(run, "hum.wav").Channel);

        run.Ticks(70);
        Assert.Equal("hum.wav", Assert.Single(run.Sounds).Clip);
    }

    [Fact]
    public void ChangingAmbienceVolumeFadesToIt()
    {
        using var run = Start();
        Entity entity = run.Game.World.Create();
        run.Game.World.Add(entity, new Ambience(run.Game.Forest));
        run.Ticks(70);
        float full = Find(run, "creak.wav").Gain;

        run.Game.World.Add(entity, new Ambience(run.Game.Forest, 0.5f));
        run.Ticks(4);
        float midway = Find(run, "creak.wav").Gain;
        Assert.True(midway < full && midway > full * 0.5f);

        // The fade takes 2 s, 60 ticks.
        run.Ticks(70);
        Assert.Equal(full * 0.5f, Find(run, "creak.wav").Gain, 0.001f);
    }

    [Fact]
    public void SmoothedSoundUsesTheBlendedPosition()
    {
        using var run = Start();
        World world = run.Game.World;
        Entity entity = Make(run, run.Game.Creak, new Vector3(-10f, 0f, -5f));
        world.Add(entity, new Smoothed());
        run.Ticks(2);

        run.Game.Step = () => world.Add(entity, Transform.At(new Vector3(10f, 0f, -5f)));
        run.Ticks(1);
        run.Game.Step = null;
        Assert.True(Find(run, "creak.wav").Pan > 0.8f);

        // Half way between the last two ticks it is straight ahead.
        run.Frame(0.5f);

        Assert.Equal(0f, Find(run, "creak.wav").Pan, 0.001f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABadVolumeThrowsAndLeavesTheOtherSoundsAlone(bool badFirst)
    {
        using var run = Start();
        Entity bad = default;
        if (badFirst)
        {
            bad = Make(run, run.Game.Hum, volume: -1f);
        }

        Make(run, run.Game.Creak);
        if (!badFirst)
        {
            bad = Make(run, run.Game.Hum, volume: -1f);
        }

        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => run.Frame(1f));
        Assert.StartsWith($"Entity {bad.Id}'s Sound has Volume -1; it must be a number of at least 0.", problem.Message);

        // Throwing again, as every frame would, changes nothing.
        Assert.Throws<ArgumentOutOfRangeException>(() => run.Frame(1f));
        run.Game.World.Add(bad, new Sound(run.Game.Hum));
        run.Ticks(15);

        Assert.Equal(2, run.Sounds.Count);
        Assert.Single(run.Sounds, sound => sound.Clip == "creak.wav");
        Assert.Single(run.Sounds, sound => sound.Clip == "hum.wav");
    }

    [Fact]
    public void ABadValueOnAPlayingSoundKeepsItPlayingUntilFixed()
    {
        using var run = Start();
        Entity entity = Make(run, run.Game.Creak);
        run.Ticks(10);
        double heard = Find(run, "creak.wav").Seconds;

        run.Game.World.Add(entity, new Sound(run.Game.Creak, Far: 1f));
        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => run.Frame(1f));
        Assert.StartsWith($"Entity {entity.Id}'s Sound has Far 1; it must be finite and further than its Near, 2.", problem.Message);
        run.Game.World.Add(entity, new Sound(run.Game.Creak));
        run.Ticks(3);

        // The same voice carried on: it wasn't stopped and started over.
        PlayingSound creak = Find(run, "creak.wav");
        Assert.True(creak.Seconds > heard);
    }

    [Fact]
    public void ANonFinitePositionThrowsNamingTheEntity()
    {
        using var run = Start();
        Entity entity = Make(run, run.Game.Creak, new Vector3(float.NaN, 0f, 0f));

        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => run.Frame(1f));

        Assert.StartsWith($"Entity {entity.Id}'s Sound is at", problem.Message);
        Assert.Contains("isn't a finite position", problem.Message);
    }

    [Fact]
    public void ABadAmbienceVolumeThrowsNamingTheEntity()
    {
        using var run = Start();
        Entity entity = run.Game.World.Create();
        run.Game.World.Add(entity, new Ambience(run.Game.Forest, -2f));

        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => run.Frame(1f));

        Assert.StartsWith($"Entity {entity.Id}'s Ambience has Volume -2; it must be a number of at least 0.", problem.Message);
    }

    [Fact]
    public void SoundWithoutTransformIsFlat()
    {
        using var run = Start();
        Make(run, run.Game.Creak);

        run.Ticks(1);

        PlayingSound creak = Find(run, "creak.wav");
        Assert.Equal(0f, creak.Pan);
        Assert.Equal(Channel.Effects, creak.Channel);
    }

    [Fact]
    public void ChangingVolumeFadesToIt()
    {
        using var run = Start();
        Entity entity = Make(run, run.Game.Creak, volume: 1f);
        run.Ticks(6);
        float full = Find(run, "creak.wav").Gain;
        Assert.True(full > 0f);

        // The fade takes 0.1 s, 3 ticks, and the first tick only hands the change over.
        run.Game.World.Add(entity, new Sound(run.Game.Creak, 0.5f));
        run.Ticks(2);
        float midway = Find(run, "creak.wav").Gain;
        run.Ticks(5);

        Assert.True(midway < full);
        Assert.Equal(full * 0.5f, Find(run, "creak.wav").Gain, 0.001f);
    }

    [Fact]
    public void ASoundThatFoundNoFreeIdStartsOnceOneIsFree()
    {
        using var run = Start();
        var quiet = new Entity[Super.Audio.MaxVoiceIds];
        for (int i = 0; i < quiet.Length; i++)
        {
            quiet[i] = Make(run, run.Game.Creak, volume: 0.1f);
        }

        // Every id is taken, so this one can't start yet.
        Make(run, run.Game.Hum);
        run.Ticks(2);
        Assert.DoesNotContain(run.Sounds, sound => sound.Clip == "hum.wav");

        run.Game.World.Destroy(quiet[0]);
        run.Ticks(2);

        Assert.Contains(run.Sounds, sound => sound.Clip == "hum.wav");
    }

    [Fact]
    public void ChildSoundUsesParentPosition()
    {
        using var run = Start();
        World world = run.Game.World;
        Entity parent = world.Create();
        world.Add(parent, Transform.At(new Vector3(-10f, 0f, 0f)));
        Entity child = Make(run, run.Game.Creak, Vector3.Zero);
        world.Add(child, new Parent(parent));

        run.Ticks(1);

        Assert.Equal(-1f, Find(run, "creak.wav").Pan, 0.001f);
    }

    [Fact]
    public void AddingATransformMovesAFlatSoundIntoTheWorld()
    {
        using var run = Start();
        Entity entity = Make(run, run.Game.Creak);
        run.Ticks(2);
        Assert.Equal(0f, Find(run, "creak.wav").Pan);

        run.Game.World.Add(entity, Transform.At(new Vector3(10f, 0f, 0f)));
        run.Ticks(1);

        Assert.Contains(run.Sounds, sound => sound.Pan > 0.99f);
    }

    [Fact]
    public void AStillEntitySendsNothingFromFrameToFrame()
    {
        using var run = Start();
        Entity entity = Make(run, run.Game.Creak, new Vector3(5f, 0f, 0f));
        AudioService audio = run.Game.Audio;
        long empty = EmptyFrameCommands();

        // The first frame starts the sound.
        run.Frame(1f);
        long before = audio.CommandsQueued;
        run.Frame(1f);
        Assert.Equal(empty, audio.CommandsQueued - before);

        before = audio.CommandsQueued;
        run.Frame(1f);
        Assert.Equal(empty, audio.CommandsQueued - before);

        // The same check does see a move, so a still frame sending nothing means something.
        run.Game.World.Add(entity, Transform.At(new Vector3(6f, 0f, 0f)));
        before = audio.CommandsQueued;
        run.Frame(1f);
        Assert.Equal(empty + 1, audio.CommandsQueued - before);
    }

    /// <summary>What a frame sends with no sound at all: the listener and the time scale.</summary>
    private static long EmptyFrameCommands()
    {
        using var run = Start();
        run.Frame(1f);
        long before = run.Game.Audio.CommandsQueued;
        run.Frame(1f);
        return run.Game.Audio.CommandsQueued - before;
    }

    [Fact]
    public void SyncAllocatesNothingOnceWarm()
    {
        using var run = Start();
        World world = run.Game.World;
        var entities = new Entity[10];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = Make(run, run.Game.Creak, new Vector3(i, 0f, -5f));
            world.Add(entities[i], new Smoothed());
        }

        Entity ambient = world.Create();
        world.Add(ambient, new Ambience(run.Game.Forest));
        for (int i = 0; i < 5; i++)
        {
            run.Frame(1f);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            // Moving, so the sync sends moves too.
            world.Add(entities[i % entities.Length], Transform.At(new Vector3(i % entities.Length, 0f, -5f - i)));
            run.Frame(1f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Super.Testing;
using AudioService = Super.Audio;

namespace Super.Tests.Audio;

public class AudioTests
{
    // At 30 ticks a second each tick mixes 1600 frames.
    private const int TicksPerSecond = 30;

    /// <summary>What the test game does in its Updates. A test changes it between ticks.</summary>
    private sealed class Plan
    {
        public Action<AudioService, Content, GameTime>? Update { get; set; }
    }

    /// <summary>Looks from the origin down −Z, so −X is straight to the left.</summary>
    private sealed class Listening(AudioService audio, Content content, GameRandom random, Plan plan) : IGame
    {
        public AudioService Audio => audio;

        public GameRandom Random => random;

        public void Update(GameTime time) => plan.Update?.Invoke(audio, content, time);

        public void Draw(View view) => view.Camera(Vector3.Zero, -Vector3.UnitZ);
    }

    private static byte[] Constant(double seconds)
    {
        var samples = new float[(int)Math.Round(seconds * Mixer.SampleRate)];
        Array.Fill(samples, 0.5f);
        return TestWav.Make(Mixer.SampleRate, 1, samples, asFloat: false);
    }

    private static MemoryContent Files()
    {
        return new MemoryContent()
            .Add("click.wav", Constant(0.2))
            .Add("bang.wav", Constant(5))
            .Add("a.wav", Constant(4))
            .Add("b.wav", Constant(4))
            .Add("forest.ambience.json", System.Text.Encoding.UTF8.GetBytes("""{ "layers": [ { "file": "a.wav" } ] }"""));
    }

    private static HeadlessRun<Listening> Start(Plan plan, int seed = 1)
    {
        return HeadlessGame.Create<Listening>(seed, services => services
            .AddContentFiles(Files())
            .AddSingleton(plan));
    }

    /// <summary>Runs one tick whose Update does this, then goes back to doing nothing.</summary>
    private static void Once(HeadlessRun<Listening> run, Plan plan, Action<AudioService, Content, GameTime> update)
    {
        plan.Update = update;
        run.Ticks(1);
        plan.Update = null;
    }

    private static PlayingSound Find(HeadlessRun<Listening> run, string clip)
    {
        return Assert.Single(run.Sounds, sound => sound.Clip == clip);
    }

    [Fact]
    public void FlatPlayIsOnTheInterfaceChannel()
    {
        var plan = new Plan();
        using var run = Start(plan);

        Once(run, plan, (audio, content, _) => audio.Play(content.Sound("click.wav")));

        PlayingSound click = Assert.Single(run.Sounds);
        Assert.Equal("click.wav", click.Clip);
        Assert.Equal(Channel.Interface, click.Channel);
        Assert.False(click.Looping);
        Assert.Equal(0f, click.Pan);

        // The clip lasts 0.2 s, six ticks. The mixer ends a voice in the block after its last frame.
        run.Ticks(5);
        Assert.Single(run.Sounds);
        run.Ticks(2);
        Assert.Empty(run.Sounds);
    }

    [Fact]
    public void PlacedPlayIsOnEffectsAndPanned()
    {
        var plan = new Plan();
        using var run = Start(plan);

        Once(run, plan, (audio, content, _) => audio.Play(content.Sound("bang.wav"), at: new Vector3(-10, 0, 0)));

        PlayingSound bang = Assert.Single(run.Sounds);
        Assert.Equal(Channel.Effects, bang.Channel);
        Assert.Equal(-1f, bang.Pan, 0.0001f);

        // Falloff at 10 m with near 2 and far 50: (2/10 − 2/50) / (1 − 2/50) = 0.16 / 0.96.
        Assert.Equal(0.16f / 0.96f, bang.Gain, 0.0001f);
    }

    [Fact]
    public void ChannelOverrideWins()
    {
        var plan = new Plan();
        using var run = Start(plan);

        Once(run, plan, (audio, content, _) =>
        {
            audio.Play(content.Sound("click.wav"), channel: Channel.Music);
            audio.Play(content.Sound("bang.wav"), at: new Vector3(-10, 0, 0), channel: Channel.Interface);
        });

        Assert.Equal(Channel.Music, Find(run, "click.wav").Channel);
        Assert.Equal(Channel.Interface, Find(run, "bang.wav").Channel);
    }

    [Fact]
    public void ChannelVolumesScaleWhatPlays()
    {
        var plan = new Plan();
        using var run = Start(plan);

        Once(run, plan, (audio, content, _) =>
        {
            audio.Volume(Channel.Interface, 0.5f);
            audio.Volume(Channel.Master, 0.4f);
            audio.Play(content.Sound("bang.wav"), volume: 0.5f);
        });

        Assert.Equal(0.5f * 0.5f * 0.4f, Assert.Single(run.Sounds).Gain, 0.0001f);
        Assert.Equal(0.5f, run.Game.Audio.Volume(Channel.Interface));
        Assert.Equal(0.4f, run.Game.Audio.Volume(Channel.Master));
        Assert.Equal(1f, run.Game.Audio.Volume(Channel.Effects));
    }

    [Fact]
    public void MusicCrossFades()
    {
        var plan = new Plan();
        using var run = Start(plan);
        Once(run, plan, (audio, content, _) => audio.Music(content.Sound("a.wav")));

        // The first track fades in over two seconds too.
        run.Ticks(10);
        Assert.Equal(10f / 60f, Find(run, "a.wav").Gain, 0.0001f);
        run.Ticks(50);
        Assert.Equal(1f, Find(run, "a.wav").Gain, 0.0001f);

        Once(run, plan, (audio, content, _) => audio.Music(content.Sound("b.wav")));
        run.Ticks(1);
        PlayingSound a = Find(run, "a.wav");
        PlayingSound b = Find(run, "b.wav");
        Assert.Equal(Channel.Music, b.Channel);
        Assert.True(b.Looping);

        run.Ticks(1);
        Assert.True(Find(run, "a.wav").Gain < a.Gain, "a should be fading out");
        Assert.True(Find(run, "b.wav").Gain > b.Gain, "b should be fading in");

        // Two ticks of the two-second fade have been mixed. Just before it ends a is still there.
        run.Ticks(55);
        Find(run, "a.wav");

        run.Ticks(4);
        PlayingSound only = Assert.Single(run.Sounds);
        Assert.Equal("b.wav", only.Clip);
        Assert.Equal(1f, only.Gain, 0.0001f);
    }

    [Fact]
    public void SameMusicAgainDoesNothing()
    {
        var plan = new Plan { Update = (audio, content, _) => audio.Music(content.Sound("a.wav")) };
        using var run = Start(plan);

        run.Ticks(90);

        PlayingSound music = Assert.Single(run.Sounds);
        Assert.Equal("a.wav", music.Clip);
        Assert.Equal(1f, music.Gain, 0.0001f);
    }

    [Fact]
    public void MusicNullFadesOut()
    {
        var plan = new Plan();
        using var run = Start(plan);
        Once(run, plan, (audio, content, _) => audio.Music(content.Sound("a.wav")));
        run.Ticks(70);

        Once(run, plan, (audio, _, _) => audio.Music(null));
        run.Ticks(1);
        float fading = Find(run, "a.wav").Gain;
        Assert.InRange(fading, 0.9f, 0.99f);

        run.Ticks(58);
        Assert.True(Find(run, "a.wav").Gain < fading);
        run.Ticks(2);
        Assert.Empty(run.Sounds);
    }

    [Fact]
    public void ScaleZeroPausesPlacedButNotMusic()
    {
        var plan = new Plan();
        using var run = Start(plan);
        Once(run, plan, (audio, content, _) =>
        {
            audio.Music(content.Sound("a.wav"));
            audio.Play(content.Sound("bang.wav"), at: new Vector3(0, 0, -1));
        });
        run.Ticks(10);

        Once(run, plan, (_, _, time) => time.Scale = 0);
        double heldAt = Find(run, "bang.wav").Seconds;
        double musicAt = Find(run, "a.wav").Seconds;
        run.Ticks(30);

        // A held voice plays on for 5 ms while it fades out, so it may move that far and no further.
        double held = Find(run, "bang.wav").Seconds;
        Assert.InRange(held - heldAt, 0, 0.006);
        Assert.Equal(1.0, Find(run, "a.wav").Seconds - musicAt, 0.05);
        run.Ticks(10);
        Assert.Equal(held, Find(run, "bang.wav").Seconds);

        Once(run, plan, (_, _, time) => time.Scale = 1);
        run.Ticks(30);
        Assert.Equal(held + 1.0, Find(run, "bang.wav").Seconds, 0.05);
    }

    [Fact]
    public void PitchVariationChangesHowFastEachPlayRuns()
    {
        var plan = new Plan();
        using var run = Start(plan);

        Once(run, plan, (audio, content, _) =>
        {
            audio.Play(content.Sound("bang.wav"), pitchVariation: 0.5f);
            audio.Play(content.Sound("bang.wav"), pitchVariation: 0.5f);
            audio.Play(content.Sound("bang.wav"));
        });
        run.Ticks(30);

        // Unvaried, a second of ticks plays a second of the clip. Varied, each play runs at its own pitch.
        double[] seconds = run.Sounds.Select(sound => sound.Seconds).Order().ToArray();
        Assert.Equal(3, seconds.Length);
        Assert.Contains(seconds, played => Math.Abs(played - 1.0) < 1e-6);
        Assert.Equal(3, seconds.Distinct().Count());
        Assert.All(seconds, played => Assert.InRange(played, 0.5, 1.5));
    }

    [Fact]
    public void PitchVariationIsTheSameWhateverTheGameSeed()
    {
        double[] Played(int seed)
        {
            var plan = new Plan();
            using var run = Start(plan, seed);
            Once(run, plan, (audio, content, _) => audio.Play(content.Sound("bang.wav"), pitchVariation: 0.5f));
            run.Ticks(30);
            return run.Sounds.Select(sound => sound.Seconds).ToArray();
        }

        Assert.Equal(Played(1), Played(2));
    }

    [Fact]
    public void PitchVariationDoesNotTouchGameRandom()
    {
        ulong After30Ticks(bool playing)
        {
            var plan = new Plan();
            if (playing)
            {
                plan.Update = (audio, content, _) => audio.Play(content.Sound("click.wav"), pitchVariation: 0.3f);
            }

            using var run = Start(plan, seed: 7);
            run.Ticks(30);
            return run.Game.Random.Stream("x").NextBits();
        }

        Assert.Equal(After30Ticks(playing: false), After30Ticks(playing: true));
    }

    [Fact]
    public void VolumeOutsideRangeExplains()
    {
        var audio = new AudioService(new Content(Files()), seed: 1);

        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => audio.Volume(Channel.Music, 1.5f));

        Assert.Equal("volume", problem.ParamName);
        Assert.Contains("Music", problem.Message);
        Assert.Equal(1f, audio.Volume(Channel.Music));
    }

    [Fact]
    public void PitchVariationOfOneExplains()
    {
        var content = new Content(Files());
        var audio = new AudioService(content, seed: 1);

        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => audio.Play(content.Sound("click.wav"), pitchVariation: 1));

        Assert.Equal("pitchVariation", problem.ParamName);
        Assert.Contains("up to, but not including, 1", problem.Message);
    }

    [Fact]
    public void FarNotBeyondNearExplains()
    {
        var content = new Content(Files());
        var audio = new AudioService(content, seed: 1);

        var problem = Assert.Throws<ArgumentOutOfRangeException>(
            () => audio.Play(content.Sound("click.wav"), at: Vector3.Zero, near: 5, far: 5));

        Assert.Equal("far", problem.ParamName);
        Assert.Contains("further than near", problem.Message);
    }

    [Fact]
    public void PlacedAtNaNExplains()
    {
        var content = new Content(Files());
        var audio = new AudioService(content, seed: 1);

        var problem = Assert.Throws<ArgumentOutOfRangeException>(
            () => audio.Play(content.Sound("click.wav"), at: new Vector3(float.NaN, 0, 0)));

        Assert.Equal("at", problem.ParamName);
        Assert.Contains("finite", problem.Message);
    }

    [Fact]
    public void AdvanceHandsOverWhatNoFrameDid()
    {
        var content = new Content(Files());
        var audio = new AudioService(content, seed: 1);
        audio.Play(content.Sound("bang.wav"));

        audio.Advance(1f / TicksPerSecond);

        PlayingSound bang = Assert.Single(audio.Playing);
        Assert.Equal(1.0 / TicksPerSecond, bang.Seconds, 1e-6);
    }

    [Fact]
    public void PlayingShowsNothingUntilItIsHandedOver()
    {
        var content = new Content(Files());
        var audio = new AudioService(content, seed: 1);
        audio.Play(content.Sound("bang.wav"));

        Assert.Empty(audio.Playing);
        audio.EndFrame(new Camera(Vector3.Zero, -Vector3.UnitZ), 1);
        Assert.Single(audio.Playing);
    }

    [Fact]
    public void AdvanceCarriesTheFractionOfAFrame()
    {
        var content = new Content(Files());
        var audio = new AudioService(content, seed: 1);
        audio.Play(content.Sound("bang.wav"));

        // A seventieth of a second is 685 5/7 frames. Rounding each call alone would mix 686, 20 frames too many
        // over 70 calls; carried over, the second comes to 48 000 frames.
        for (int i = 0; i < 70; i++)
        {
            audio.Advance(1f / 70);
        }

        Assert.Equal(1.0, Assert.Single(audio.Playing).Seconds, 0.5 / Mixer.SampleRate);
    }

    [Fact]
    public void StaleLoopIdDoesNotReachTheNextLoop()
    {
        var content = new Content(Files());
        var audio = new AudioService(content, seed: 1);
        int first = audio.StartLoop(content.Sound("a.wav"), 1, Channel.Effects, at: null, near: 2, far: 50);
        audio.StopLoop(first);
        int second = audio.StartLoop(content.Sound("b.wav"), 1, Channel.Effects, at: null, near: 2, far: 50);

        audio.ChangeVolume(first, 0);
        audio.StopLoop(first);
        audio.Advance(1);

        Assert.NotEqual(first, second);
        PlayingSound b = Assert.Single(audio.Playing);
        Assert.Equal("b.wav", b.Clip);
        Assert.Equal(1f, b.Gain, 0.0001f);
    }

    [Fact]
    public void LoopFadesInAndOut()
    {
        var content = new Content(Files());
        var audio = new AudioService(content, seed: 1);
        int loop = audio.StartLoop(content.Sound("a.wav"), 1, Channel.Effects, at: null, near: 2, far: 50);

        audio.Advance(0.05f);
        Assert.Equal(0.5f, Assert.Single(audio.Playing).Gain, 0.001f);
        audio.Advance(0.05f);
        Assert.Equal(1f, Assert.Single(audio.Playing).Gain, 0.001f);

        audio.StopLoop(loop);
        audio.Advance(0.15f);
        Assert.Equal(0.5f, Assert.Single(audio.Playing).Gain, 0.001f);
        audio.Advance(0.16f);
        Assert.Empty(audio.Playing);
    }

    [Fact]
    public void LoopMovesAndChangesVolume()
    {
        var content = new Content(Files());
        var audio = new AudioService(content, seed: 1);
        int loop = audio.StartLoop(content.Sound("a.wav"), 1, Channel.Effects, at: new Vector3(-1, 0, 0), near: 2, far: 50);
        audio.Advance(0.2f);

        audio.Move(loop, new Vector3(10, 0, 0));
        audio.ChangeVolume(loop, 0.5f);
        audio.Advance(0.2f);

        PlayingSound sound = Assert.Single(audio.Playing);
        Assert.Equal(1f, sound.Pan, 0.0001f);
        Assert.Equal(0.5f * 0.16f / 0.96f, sound.Gain, 0.0001f);
    }

    [Fact]
    public void AmbienceFadesInAndOutOverTwoSeconds()
    {
        var content = new Content(Files());
        var audio = new AudioService(content, seed: 1);
        int forest = audio.StartAmbience(content.Ambience("forest.ambience.json"), 0.8f);

        audio.Advance(1);
        PlayingSound layer = Assert.Single(audio.Playing);
        Assert.Equal(Channel.Ambience, layer.Channel);
        Assert.Equal(0.4f, layer.Gain, 0.001f);

        audio.Advance(1);
        audio.ChangeAmbienceVolume(forest, 0.4f);
        audio.Advance(1);
        Assert.Equal(0.6f, Assert.Single(audio.Playing).Gain, 0.001f);

        audio.Advance(1);
        audio.StopAmbience(forest);
        audio.Advance(1.9f);
        Assert.Single(audio.Playing);
        audio.Advance(0.2f);
        Assert.Empty(audio.Playing);
    }
}

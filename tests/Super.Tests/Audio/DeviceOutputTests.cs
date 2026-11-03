using System.Diagnostics;
using System.Numerics;
using Super.Testing;
using AudioService = Super.Audio;

namespace Super.Tests.Audio;

/// <summary>What the game does when the sound device won't open. Nothing here starts a thread.</summary>
public class DeviceOutputTests
{
    private const int TicksPerSecond = 30;

    private sealed class Silent(AudioService audio, Content content) : IGame
    {
        public AudioService Audio => audio;

        public Content Content => content;

        public void Update(GameTime time)
        {
        }

        public void Draw(View view) => view.Camera(Vector3.Zero, -Vector3.UnitZ);
    }

    private static byte[] Constant(double seconds)
    {
        var samples = new float[(int)Math.Round(seconds * Mixer.SampleRate)];
        Array.Fill(samples, 0.5f);
        return TestWav.Make(Mixer.SampleRate, 1, samples, asFloat: false);
    }

    [Fact]
    public void FailedOpenStaysManualAndAdvances()
    {
        using var run = HeadlessGame.Create<Silent>(1, services => services.AddContentFiles(new MemoryContent().Add("click.wav", Constant(0.2))));
        AudioService audio = run.Game.Audio;

        Assert.False(audio.UseDevice(TextWriter.Null, () => "forced"));
        Assert.True(audio.Manual);

        // 0.2 s is six ticks: still playing after five, gone after seven, because each tick mixed its own share.
        // Ticking needs no Draw in between, since each tick's Advance hands over what's queued.
        audio.Play(run.Game.Content.Sound("click.wav"));
        run.Ticks(1);
        Assert.Single(run.Sounds);
        run.Ticks(4);
        Assert.Single(run.Sounds);
        run.Ticks(2);
        Assert.Empty(run.Sounds);
    }

    [Fact]
    public void FailedOpenLogsOneLine()
    {
        var audio = new AudioService(new Content(new MemoryContent()), seed: 1);
        var log = new StringWriter();

        audio.UseDevice(log, () => "forced");

        // WriteLine ends the line with Environment.NewLine, which is "\r\n" on Windows.
        Assert.Equal("No sound device (forced); playing silently." + Environment.NewLine, log.ToString());
    }

    [Fact]
    public void ClosingEndsStreamedReaders()
    {
        var content = new Content(new MemoryContent());
        var reader = new WatchedReader();
        SoundRef music = content.AddClip(SoundClip.Streaming("long.ogg", Mixer.SampleRate, 1, 20, () => reader));
        var audio = new AudioService(content, seed: 1);
        audio.Music(music, fadeSeconds: 0);
        audio.Advance(0.1f);
        Assert.False(reader.Disposed);

        audio.Close();

        Assert.True(reader.Disposed);
    }

    /// <summary>A mono reader of quarter-volume samples that remembers being disposed.</summary>
    internal sealed class WatchedReader : ISampleReader
    {
        public bool Disposed { get; private set; }

        public long Frames => 20 * Mixer.SampleRate;

        public int Read(Span<float> interleaved)
        {
            interleaved.Fill(0.25f);
            return interleaved.Length;
        }

        public void Seek(long frame)
        {
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}

/// <summary>An audio thread runs in real time, so these run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioThreadCollection
{
    public const string Name = "Audio thread";
}

/// <summary>The audio thread, mostly with a stand-in for the speakers so no sound card is needed.</summary>
[Collection(AudioThreadCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class AudioThreadTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(2);

    /// <summary>Takes one block a pass, as fast as the thread offers them, and keeps the loudest sample it saw.</summary>
    private sealed class StandInSpeakers : IAudioSink
    {
        private int _blocks;
        private int _inside;
        private float _loudest;

        /// <summary>When set, each block is held this long, so a test can catch the thread in the middle of one.</summary>
        public TimeSpan Hold { get; init; }

        public ManualResetEventSlim Entered { get; } = new(false);

        public int Blocks => Volatile.Read(ref _blocks);

        public bool Inside => Volatile.Read(ref _inside) != 0;

        public float Loudest => Volatile.Read(ref _loudest);

        public bool Disposed { get; private set; }

        public int Free() => 1;

        public void Queue(ReadOnlySpan<float> stereo)
        {
            Volatile.Write(ref _inside, 1);
            Entered.Set();
            float loudest = _loudest;
            foreach (float sample in stereo)
            {
                loudest = Math.Max(loudest, Math.Abs(sample));
            }

            Volatile.Write(ref _loudest, loudest);
            if (Hold > TimeSpan.Zero)
            {
                Thread.Sleep(Hold);
            }

            Interlocked.Increment(ref _blocks);
            Volatile.Write(ref _inside, 0);
        }

        public void KeepPlaying()
        {
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private static byte[] Constant(double seconds)
    {
        var samples = new float[(int)Math.Round(seconds * Mixer.SampleRate)];
        Array.Fill(samples, 0.5f);
        return TestWav.Make(Mixer.SampleRate, 1, samples, asFloat: false);
    }

    private static (AudioService Audio, Content Content) Make()
    {
        var content = new Content(new MemoryContent().Add("bang.wav", Constant(5)));
        return (new AudioService(content, seed: 1), content);
    }

    private static void Frame(AudioService audio)
    {
        audio.EndFrame(new Camera(Vector3.Zero, -Vector3.UnitZ), 1);
    }

    /// <summary>Polls until the condition holds, failing after two seconds.</summary>
    private static void WaitFor(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Cap, $"Waited {Cap.TotalSeconds} s for {what}.");
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void OnTheThreadAdvanceThrows()
    {
        (AudioService audio, _) = Make();
        audio.UseOutput(new StandInSpeakers(), TextWriter.Null);
        try
        {
            Assert.False(audio.Manual);
            var problem = Assert.Throws<InvalidOperationException>(() => audio.Advance(0.1f));
            Assert.Equal("The audio can only be advanced by hand when the game drives it, as in tests.", problem.Message);
        }
        finally
        {
            audio.Close();
        }
    }

    [Fact]
    public void OnTheThreadPlayingThrows()
    {
        (AudioService audio, _) = Make();
        audio.UseOutput(new StandInSpeakers(), TextWriter.Null);
        try
        {
            var problem = Assert.Throws<InvalidOperationException>(() => audio.Playing);
            Assert.Equal("What's playing can only be read when the game drives the audio, as in tests.", problem.Message);
        }
        finally
        {
            audio.Close();
        }
    }

    [Fact]
    public void TheThreadMixesWhatTheGameSends()
    {
        (AudioService audio, Content content) = Make();
        var speakers = new StandInSpeakers();
        audio.UseOutput(speakers, TextWriter.Null);
        try
        {
            audio.Play(content.Sound("bang.wav"));
            Frame(audio);

            // The clip is a constant 0.5, played flat at full volume.
            WaitFor(() => speakers.Loudest > 0, "the bang to reach the speakers");
            Assert.Equal(0.5f, speakers.Loudest, 0.001f);
        }
        finally
        {
            audio.Close();
        }
    }

    [Fact]
    public void PauseWaitsForTheBlockInHand()
    {
        (AudioService audio, _) = Make();
        var speakers = new StandInSpeakers { Hold = TimeSpan.FromMilliseconds(100) };
        audio.UseOutput(speakers, TextWriter.Null);
        try
        {
            Assert.True(speakers.Entered.Wait(Cap), "The thread never started a block.");

            audio.PauseDevice();

            Assert.False(speakers.Inside);
            Assert.True(audio.Manual);
        }
        finally
        {
            audio.Close();
        }
    }

    [Fact]
    public void WhilePausedTheGameMixesAndResumeHandsItBack()
    {
        (AudioService audio, Content content) = Make();
        var speakers = new StandInSpeakers();
        audio.UseOutput(speakers, TextWriter.Null);
        try
        {
            WaitFor(() => audio.MixedFrames > 0, "the thread to mix");
            audio.PauseDevice();
            long paused = audio.MixedFrames;

            audio.Play(content.Sound("bang.wav"));
            audio.Advance(0.1f);

            Assert.Equal(paused + 4800, audio.MixedFrames);
            Assert.Equal(0.1, Assert.Single(audio.Playing).Seconds, 1e-6);

            audio.ResumeDevice();

            Assert.False(audio.Manual);
            WaitFor(() => audio.MixedFrames > paused + 4800, "the thread to mix again");
        }
        finally
        {
            audio.Close();
        }
    }

    [Fact]
    public void ASoundThatFailsOnTheThreadIsLogged()
    {
        var content = new Content(new MemoryContent());
        SoundRef broken = content.AddClip(SoundClip.Streaming("broken.ogg", Mixer.SampleRate, 1, 20, () => throw new InvalidDataException("can't open")));
        var audio = new AudioService(content, seed: 1);
        var log = new SharedLog();
        audio.UseOutput(new StandInSpeakers(), log);
        try
        {
            audio.Play(broken);
            Frame(audio);

            WaitFor(() => log.Text.Length > 0, "a line in the log");
        }
        finally
        {
            audio.Close();
        }

        Assert.Equal("A sound failed and stopped (1 so far): can't open" + Environment.NewLine, log.Text);
    }

    /// <summary>A log the audio thread writes and the test reads at the same time.</summary>
    private sealed class SharedLog : TextWriter
    {
        private readonly Lock _gate = new();
        private readonly System.Text.StringBuilder _text = new();

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _text.ToString();
                }
            }
        }

        public override void Write(char value)
        {
            lock (_gate)
            {
                _text.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                _text.Append(value);
            }
        }
    }

    [Fact]
    public void CloseStopsTheThreadAndClosesTheSpeakers()
    {
        (AudioService audio, _) = Make();
        var speakers = new StandInSpeakers();
        audio.UseOutput(speakers, TextWriter.Null);
        WaitFor(() => speakers.Blocks > 0, "the thread to start");

        audio.Close();

        Assert.True(speakers.Disposed);
        Assert.False(speakers.Inside);
        Assert.True(audio.Manual);
    }

    [Fact]
    public void ARealDeviceMixes()
    {
        (AudioService audio, _) = Make();
        var log = new StringWriter();
        if (!audio.UseDevice(log))
        {
            // No sound card here: there's nothing to check, and the log says why.
            Assert.StartsWith("No sound device (", log.ToString());
            return;
        }

        try
        {
            Assert.False(audio.Manual);
            WaitFor(() => audio.MixedFrames > 0, "the device to take a block");
        }
        finally
        {
            audio.Close();
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Super.Testing;
using AudioService = Super.Audio;

namespace Super.Tests.Audio;

public sealed class AudioRecordingTests : IDisposable
{
    private const int TicksPerSecond = 30;

    private readonly string _folder = Directory.CreateTempSubdirectory("super-recording-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class Listening(AudioService audio, Content content) : IGame
    {
        public AudioService Audio => audio;

        public Content Content => content;

        public void Update(GameTime time)
        {
            // Scattered sounds come from the ambience; these add pitch-varied one-shots from audio's own randomness.
            if (time.Tick % 7 == 0)
            {
                audio.Play(content.Sound("click.wav"), at: new Vector3(time.Tick % 5, 0, -3), pitchVariation: 0.3f);
            }
        }

        public void Draw(View view) => view.Camera(Vector3.Zero, -Vector3.UnitZ);
    }

    private static byte[] Tone(double seconds)
    {
        var samples = new float[(int)Math.Round(seconds * Mixer.SampleRate)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.5f * MathF.Sin(i * 0.05f);
        }

        return TestWav.Make(Mixer.SampleRate, 1, samples, asFloat: false);
    }

    private static HeadlessRun<Listening> Start()
    {
        string ambience = """
            {
              "layers": [ { "file": "bed.wav", "volume": 0.5 } ],
              "scattered": [ { "files": ["click.wav", "bed.wav"], "every": [0.05, 0.2], "volume": [0.2, 1], "pitchVariation": 0.2 } ]
            }
            """;
        return HeadlessGame.Create<Listening>(1, services => services.AddContentFiles(new MemoryContent()
            .Add("click.wav", Tone(0.1))
            .Add("bed.wav", Tone(1))
            .Add("wood.ambience.json", Encoding.UTF8.GetBytes(ambience))));
    }

    private string RecordTicks(long ticks, string name)
    {
        string wav = Path.Combine(_folder, name);
        using var run = Start();
        AudioService audio = run.Game.Audio;
        audio.StartRecording(wav);
        audio.StartAmbience(run.Game.Content.Ambience("wood.ambience.json"), 1);
        run.Ticks(ticks);
        audio.StopRecording();
        return wav;
    }

    [Fact]
    public void RecordingWritesAReadableWav()
    {
        string path = Path.Combine(_folder, "pattern.wav");
        var pattern = new float[Mixer.SampleRate * 2];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = ((i % 200) - 100) / 128f;
        }

        using (var recording = new AudioRecording(path))
        {
            recording.Write(pattern.AsSpan(0, 30000));
            recording.Write(pattern.AsSpan(30000));
            recording.Finish();
            Assert.Equal(Mixer.SampleRate, recording.Frames);
        }

        WavReader wav = WavReader.Parse(File.ReadAllBytes(path));
        Assert.Equal(Mixer.SampleRate, wav.SampleRate);
        Assert.Equal(2, wav.Channels);
        Assert.Equal(Mixer.SampleRate, wav.Frames);
        var back = new float[pattern.Length];
        Assert.Equal(Mixer.SampleRate, wav.Read(back));
        Assert.Equal(pattern, back);
    }

    [Fact]
    public void RecordingRefusesHalfAFrame()
    {
        using var recording = new AudioRecording(Path.Combine(_folder, "odd.wav"));

        var thrown = Assert.Throws<ArgumentException>(() => recording.Write(new float[3]));
        Assert.StartsWith("Stereo samples come in left/right pairs, so the length must be even.", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordedAudioLengthMatchesTicks()
    {
        string path = RecordTicks(90, "ninety.wav");

        WavReader wav = WavReader.Parse(File.ReadAllBytes(path));
        Assert.Equal(144_000, wav.Frames);
        Assert.Equal(2, wav.Channels);
        Assert.Equal(Mixer.SampleRate, wav.SampleRate);
    }

    [Fact]
    public void FractionsOfAFrameAreCarriedSoLongRecordingsDontDrift()
    {
        string path = Path.Combine(_folder, "odd-rate.wav");
        using var run = Start();
        run.Game.Audio.StartRecording(path);

        // 44 ticks a second is 1090.909... frames a tick, which never lands on a whole frame.
        for (int tick = 0; tick < 44 * 5; tick++)
        {
            run.Game.Audio.Advance(1f / 44);
        }

        run.Game.Audio.StopRecording();
        Assert.Equal(Mixer.SampleRate * 5, WavReader.Parse(File.ReadAllBytes(path)).Frames);
    }

    [Fact]
    public void SameSeedRecordsTheSameAudio()
    {
        string first = RecordTicks(60, "one.wav");
        string second = RecordTicks(60, "two.wav");

        byte[] bytes = File.ReadAllBytes(first);
        Assert.Equal(bytes, File.ReadAllBytes(second));

        // Silence would match too, so make sure there is something in it.
        var samples = new float[(int)WavReader.Parse(bytes).Frames * 2];
        WavReader.Parse(bytes).Read(samples);
        Assert.Contains(samples, s => Math.Abs(s) > 0.05f);
    }

    [Fact]
    public void AudioStaysManualForTheRecordingAndCannotBeStartedTwice()
    {
        using var run = Start();
        AudioService audio = run.Game.Audio;
        audio.StartRecording(Path.Combine(_folder, "a.wav"));

        var thrown = Assert.Throws<InvalidOperationException>(() => audio.StartRecording(Path.Combine(_folder, "b.wav")));
        Assert.Equal("The audio is already being recorded.", thrown.Message);
        audio.StopRecording();
        audio.StopRecording();
    }

    [Fact]
    public void AGameCaptureKeepsNoWavWhenNoFrameWasSaved()
    {
        using var run = Start();
        string video = Path.Combine(_folder, "clip.mp4");
        using var capture = new GameCapture(run.Game.Audio);

        capture.StartRecording(video, TicksPerSecond);
        run.Ticks(3);
        Assert.True(File.Exists(Path.Combine(_folder, "clip.wav")));
        Assert.Equal(4800, run.Game.Audio.MixedFrames);
        capture.StopRecording();

        Assert.False(File.Exists(Path.Combine(_folder, "clip.wav")));
        Assert.False(File.Exists(video));
    }

    [Fact]
    public void AddingSoundReplacesTheVideoAndDeletesTheWav()
    {
        string video = Path.Combine(_folder, "game.mp4");
        string wav = Path.Combine(_folder, "game.wav");
        File.WriteAllText(video, "silent video");
        File.WriteAllText(wav, "sound");
        IReadOnlyList<string>? seen = null;

        VideoFile.AddSound(video, wav, arguments =>
        {
            seen = arguments;
            File.WriteAllText(arguments[^1], "video with sound");
            return null;
        });

        Assert.Equal("video with sound", File.ReadAllText(video));
        Assert.False(File.Exists(wav));
        Assert.NotNull(seen);
        Assert.Equal(
            ["-y", "-hide_banner", "-loglevel", "error", "-i", video, "-i", wav, "-c:v", "copy", "-c:a", "aac", "-shortest", seen![^1]],
            seen);
        Assert.False(File.Exists(seen[^1]));
        Assert.Equal([video], Directory.GetFiles(_folder));
    }

    [Fact]
    public void AFailedMuxKeepsTheVideoAndTheWavAndSaysSo()
    {
        string video = Path.Combine(_folder, "game.mp4");
        string wav = Path.Combine(_folder, "game.wav");
        File.WriteAllText(video, "silent video");
        File.WriteAllText(wav, "sound");
        TextWriter original = Console.Error;
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Console.SetError(error);
        try
        {
            VideoFile.AddSound(video, wav, arguments =>
            {
                File.WriteAllText(arguments[^1], "half a file");
                return "no audio encoder";
            });
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal("silent video", File.ReadAllText(video));
        Assert.Equal("sound", File.ReadAllText(wav));
        Assert.Equal([video, wav], Directory.GetFiles(_folder).OrderBy(f => f, StringComparer.Ordinal).ToArray());
        Assert.Equal($"Couldn't add sound to {video}: no audio encoder. The sound is in {wav}.{Environment.NewLine}", error.ToString());
    }
}

/// <summary>Runs ffmpeg for real, which takes a moment, and sets the process-wide console error.</summary>
[Collection(AudioThreadCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class AudioInVideoTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("super-video-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class Quiet(AudioService audio, Content content) : IGame
    {
        public AudioService Audio => audio;

        public Content Content => content;

        public void Update(GameTime time)
        {
        }

        public void Draw(View view) => view.Camera(Vector3.Zero, -Vector3.UnitZ);
    }

    [Fact]
    public void ARecordedVideoHasASoundTrackAndNoWavLeft()
    {
        var samples = new float[Mixer.SampleRate];
        Array.Fill(samples, 0.25f);
        using var run = HeadlessGame.Create<Quiet>(1, services => services.AddContentFiles(
            new MemoryContent().Add("bed.wav", TestWav.Make(Mixer.SampleRate, 1, samples, asFloat: false))));
        string video = Path.Combine(_folder, "clip.mp4");
        using var capture = new GameCapture(run.Game.Audio);

        capture.StartRecording(video, 30);
        run.Game.Audio.Play(run.Game.Content.Sound("bed.wav"));
        for (int frame = 0; frame < 30; frame++)
        {
            run.Ticks(1);
            capture.Accept(new byte[16 * 16 * 4], 16, 16);
        }

        capture.StopRecording();

        Assert.True(File.Exists(video));
        Assert.False(File.Exists(Path.Combine(_folder, "clip.wav")));
        using var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg", ["-hide_banner", "-i", video])
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        string report = ffmpeg.StandardError.ReadToEnd();
        ffmpeg.WaitForExit();
        Assert.Contains("Audio: aac", report, StringComparison.Ordinal);
        Assert.Contains("Video: h264", report, StringComparison.Ordinal);
    }
}

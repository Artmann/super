using Silk.NET.OpenAL;

namespace Super;

/// <summary>
/// Where the audio thread sends each finished block of 48 kHz stereo floats: the speakers, or a stand-in in tests.
/// Only the audio thread calls it, apart from <see cref="IDisposable.Dispose"/> once that thread has stopped.
/// </summary>
internal interface IAudioSink : IDisposable
{
    /// <summary>How many blocks can be queued now: those that have finished playing, and at first all of them.</summary>
    int Free();

    /// <summary>Queues one block behind those already playing.</summary>
    void Queue(ReadOnlySpan<float> stereo);

    /// <summary>Starts playing if it isn't: at the start, and again after it ran dry because the thread fell behind.</summary>
    void KeepPlaying();
}

/// <summary>
/// Plays the mix through a sound device. A background thread keeps <see cref="Blocks"/> blocks of
/// <see cref="BlockFrames"/> frames queued: each pass it renders one block through <see cref="AudioMixing.Render"/> for
/// every block that finished playing, which drains the game's commands and mixes.
/// </summary>
/// <remarks>
/// The thread does each pass while holding a lock. <see cref="Pause"/> takes the same lock, so once it returns the
/// thread has finished its block and won't touch the mixer again until <see cref="Resume"/>; the lock also makes
/// what either side wrote to the mixer visible to the other.
/// </remarks>
internal sealed class DeviceOutput : IDisposable
{
    public const int BlockFrames = 512;
    public const int Blocks = 4;

    // A block lasts 10.7 ms and four are queued. The wait is often longer than asked for (about 15.6 ms on Windows),
    // which still leaves most of the queue in hand.
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(2);

    private readonly AudioMixing _mixing;
    private readonly IAudioSink _sink;
    private readonly AudioThreadLog _log;
    private readonly float[] _block = new float[BlockFrames * 2];
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;

    // Under _gate.
    private bool _paused;
    private bool _closing;

    // Audio thread only.
    private bool _broken;

    /// <summary>Starts the audio thread, which plays <paramref name="mixing"/>'s mix into <paramref name="sink"/>.</summary>
    /// <param name="log">Where the audio thread writes a line when a sound fails, or when it has to stop.</param>
    public DeviceOutput(AudioMixing mixing, IAudioSink sink, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(mixing);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(log);
        _mixing = mixing;
        _sink = sink;
        _log = new AudioThreadLog(log, mixing.Failures);
        _thread = new Thread(Run)
        {
            Name = "Super audio",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>
    /// Opens the default sound device and starts playing <paramref name="mixing"/> through it. If the device can't be
    /// opened, writes <c>No sound device ({reason}); playing silently.</c> to <paramref name="log"/> and returns null.
    /// </summary>
    /// <param name="openOverride">
    /// For tests: called first, and a reason it returns fails the open with that reason. Null from it opens as usual.
    /// </param>
    public static DeviceOutput? TryOpen(AudioMixing mixing, TextWriter log, Func<string?>? openOverride = null)
    {
        ArgumentNullException.ThrowIfNull(mixing);
        ArgumentNullException.ThrowIfNull(log);
        string? reason = openOverride?.Invoke();
        IAudioSink? speakers = null;
        if (reason is null)
        {
            try
            {
                speakers = OpenAlSpeakers.Open(Blocks);
            }
            catch (Exception problem)
            {
                reason = problem.Message;
            }
        }

        if (speakers is null)
        {
            log.WriteLine($"No sound device ({reason}); playing silently.");
            return null;
        }

        return new DeviceOutput(mixing, speakers, log);
    }

    /// <summary>
    /// Takes the mixer off the audio thread, waiting for it to finish the block it's on. Until <see cref="Resume"/>
    /// the thread renders nothing and the speakers run dry, so the caller may mix by hand.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
        }
    }

    /// <summary>Hands the mixer back to the audio thread, which refills the speakers and starts them again.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
        }

        _wake.Set();
    }

    /// <summary>Stops the audio thread, waits for it to end, then closes the device.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
        }

        _wake.Set();
        _thread.Join();
        _sink.Dispose();
        _wake.Dispose();
    }

    private void Run()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_closing)
                {
                    return;
                }

                if (!_paused)
                {
                    if (_broken)
                    {
                        // The game keeps sending; taking its commands keeps the queue from filling up and dropping.
                        _mixing.Discard();
                    }
                    else
                    {
                        Fill();
                    }
                }
            }

            _wake.WaitOne(Poll);
        }
    }

    /// <summary>Refills every block that finished playing, so a thread that fell behind catches up in one pass.</summary>
    private void Fill()
    {
        try
        {
            int free = _sink.Free();
            for (int i = 0; i < free; i++)
            {
                _mixing.Render(_block);
                _sink.Queue(_block);
            }

            _sink.KeepPlaying();
            _log.NewFailures();
        }
        catch (Exception problem)
        {
            // Nothing may escape a background thread, or the game would crash. The mixer may be half way through a
            // change, so it isn't touched again: the game carries on in silence, and its commands are thrown away.
            _broken = true;
            _log.Line($"Sound stopped: {problem.Message}");
        }
    }
}

/// <summary>What the audio thread writes to its log: a line for sounds that fail, and one if it has to stop.</summary>
internal sealed class AudioThreadLog
{
    private readonly TextWriter _log;
    private readonly MixFailures _failures;
    private int _failuresLogged;

    public AudioThreadLog(TextWriter log, MixFailures failures)
    {
        _log = log;
        _failures = failures;
        _failuresLogged = failures.Failed;
    }

    /// <summary>A line for the sounds that failed since the last one, which reads only the mixer's own counters.</summary>
    public void NewFailures()
    {
        int failed = _failures.Failed;
        if (failed == _failuresLogged)
        {
            return;
        }

        _failuresLogged = failed;
        Line($"A sound failed and stopped ({failed} so far): {_failures.LastMessage}");
    }

    public void Line(string line)
    {
        try
        {
            _log.WriteLine(line);
        }
        catch (Exception)
        {
            // A log that can't be written to has nowhere to report that either.
        }
    }
}

/// <summary>The default sound device through OpenAL Soft: one streaming source with a buffer for each block.</summary>
internal sealed unsafe class OpenAlSpeakers : IAudioSink
{
    private readonly ALContext _alc;
    private readonly uint[] _free;
    private AL? _al;
    private Device* _device;
    private Context* _context;
    private BufferFormat _format;
    private uint? _source;
    private uint[]? _buffers;
    private int _freeCount;

    private OpenAlSpeakers(ALContext alc, int blocks)
    {
        _alc = alc;
        _free = new uint[blocks];
    }

    /// <summary>
    /// Opens the default device for 48 kHz stereo float with this many buffers. Throws with a short reason when it
    /// can't, after deleting whatever it made, while its context is still current.
    /// </summary>
    public static OpenAlSpeakers Open(int blocks)
    {
        var speakers = new OpenAlSpeakers(ALContext.GetApi(soft: true), blocks);
        try
        {
            speakers.OpenContext();
            speakers.MakeSource();
            return speakers;
        }
        catch
        {
            speakers.Release();
            throw;
        }
    }

    public int Free()
    {
        AL al = _al!;
        uint source = _source!.Value;
        al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out int processed);
        if (processed > 0)
        {
            uint* done = stackalloc uint[_free.Length];
            processed = Math.Min(processed, _free.Length - _freeCount);
            al.SourceUnqueueBuffers(source, processed, done);
            for (int i = 0; i < processed; i++)
            {
                _free[_freeCount++] = done[i];
            }
        }

        return _freeCount;
    }

    public void Queue(ReadOnlySpan<float> stereo)
    {
        AL al = _al!;
        uint buffer = _free[--_freeCount];
        fixed (float* samples = stereo)
        {
            al.BufferData(buffer, _format, samples, stereo.Length * sizeof(float), MixRate.SampleRate);
        }

        al.SourceQueueBuffers(_source!.Value, 1, &buffer);

        // A failed queue would leave the buffer lost and the source short; throwing stops the thread with one line.
        AudioError error = al.GetError();
        if (error != AudioError.NoError)
        {
            throw new InvalidOperationException($"OpenAL error {error} while queueing sound");
        }
    }

    public void KeepPlaying()
    {
        uint source = _source!.Value;
        _al!.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
        if (state != (int)SourceState.Playing)
        {
            _al.SourcePlay(source);
        }
    }

    public void Dispose()
    {
        _al!.SourceStop(_source!.Value);
        Release();
    }

    private void OpenContext()
    {
        AL al = AL.GetApi(soft: true);
        _al = al;
        _device = _alc.OpenDevice(null!);
        if (_device == null)
        {
            throw new InvalidOperationException("the default device didn't open");
        }

        int* attributes = stackalloc int[] { (int)ContextAttributes.Frequency, MixRate.SampleRate, 0 };
        _context = _alc.CreateContext(_device, attributes);
        if (_context == null || !_alc.MakeContextCurrent(_context))
        {
            throw new InvalidOperationException("the device has no context");
        }

        if (!al.IsExtensionPresent("AL_EXT_float32"))
        {
            throw new InvalidOperationException("the device can't play float samples");
        }

        _format = (BufferFormat)al.GetEnumValue("AL_FORMAT_STEREO_FLOAT32");
    }

    private void MakeSource()
    {
        AL al = _al!;
        al.GetError();
        uint source = al.GenSource();
        ThrowOnError(al);
        _source = source;
        uint[] buffers = al.GenBuffers(_free.Length);
        ThrowOnError(al);
        _buffers = buffers;
        buffers.CopyTo(_free, 0);
        _freeCount = buffers.Length;
    }

    private static void ThrowOnError(AL al)
    {
        AudioError error = al.GetError();
        if (error != AudioError.NoError)
        {
            throw new InvalidOperationException($"OpenAL error {error}");
        }
    }

    /// <summary>Deletes the source and buffers, then closes the context, the device and both APIs, as far as each was made.</summary>
    private void Release()
    {
        if (_source is uint source)
        {
            _al!.DeleteSource(source);
        }

        if (_buffers is not null)
        {
            _al!.DeleteBuffers(_buffers);
        }

        if (_context != null)
        {
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
        }

        if (_device != null)
        {
            _alc.CloseDevice(_device);
        }

        _al?.Dispose();
        _alc.Dispose();
    }
}

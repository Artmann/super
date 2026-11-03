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
/// <see cref="BlockFrames"/> frames queued: each pass it renders one block through <see cref="Audio.Render"/> for every
/// block that finished playing, which drains the game's commands and mixes.
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

    private readonly Audio _audio;
    private readonly IAudioSink _sink;
    private readonly TextWriter _log;
    private readonly float[] _block = new float[BlockFrames * 2];
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;

    // Under _gate.
    private bool _paused;
    private bool _closing;

    // Audio thread only.
    private bool _broken;
    private int _failuresLogged;

    /// <summary>Starts the audio thread, which plays <paramref name="audio"/>'s mix into <paramref name="sink"/>.</summary>
    /// <param name="log">Where the audio thread writes a line when a sound fails, or when it has to stop.</param>
    public DeviceOutput(Audio audio, IAudioSink sink, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(log);
        _audio = audio;
        _sink = sink;
        _log = log;
        _failuresLogged = audio.FailedVoices;
        _thread = new Thread(Run)
        {
            Name = "Super audio",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>
    /// Opens the default sound device and starts playing <paramref name="audio"/> through it. If the device can't be
    /// opened, writes <c>No sound device ({reason}); playing silently.</c> to <paramref name="log"/> and returns null.
    /// </summary>
    /// <param name="openOverride">
    /// For tests: called first, and a reason it returns fails the open with that reason. Null from it opens as usual.
    /// </param>
    public static DeviceOutput? TryOpen(Audio audio, TextWriter log, Func<string?>? openOverride = null)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(log);
        string? reason = openOverride?.Invoke();
        IAudioSink? speakers = null;
        if (reason is null)
        {
            try
            {
                speakers = OpenAlSpeakers.Open();
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

        return new DeviceOutput(audio, speakers, log);
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
                        _audio.Discard();
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
                _audio.Render(_block);
                _sink.Queue(_block);
            }

            _sink.KeepPlaying();
            LogFailures();
        }
        catch (Exception problem)
        {
            // Nothing may escape a background thread, or the game would crash. The mixer may be half way through a
            // change, so it isn't touched again: the game carries on in silence, and its commands are thrown away.
            _broken = true;
            Log($"Sound stopped: {problem.Message}");
        }
    }

    /// <summary>A line for the sounds that failed since the last one, which reads only the mixer's own counters.</summary>
    private void LogFailures()
    {
        int failed = _audio.FailedVoices;
        if (failed == _failuresLogged)
        {
            return;
        }

        _failuresLogged = failed;
        Log($"A sound failed and stopped ({failed} so far): {_audio.LastFailure}");
    }

    private void Log(string line)
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

/// <summary>The default sound device through OpenAL Soft: one streaming source with <see cref="DeviceOutput.Blocks"/> buffers.</summary>
internal sealed unsafe class OpenAlSpeakers : IAudioSink
{
    private readonly ALContext _alc;
    private readonly AL _al;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly BufferFormat _format;
    private readonly uint _source;
    private readonly uint[] _buffers;
    private readonly uint[] _free = new uint[DeviceOutput.Blocks];
    private int _freeCount;

    private OpenAlSpeakers(ALContext alc, AL al, Device* device, Context* context, BufferFormat format, uint source, uint[] buffers)
    {
        _alc = alc;
        _al = al;
        _device = device;
        _context = context;
        _format = format;
        _source = source;
        _buffers = buffers;
        buffers.CopyTo(_free, 0);
        _freeCount = buffers.Length;
    }

    /// <summary>Opens the default device for 48 kHz stereo float. Throws with a short reason when it can't.</summary>
    public static OpenAlSpeakers Open()
    {
        ALContext alc = ALContext.GetApi(soft: true);
        AL? al = null;
        Device* device = null;
        Context* context = null;
        uint source = 0;
        bool haveSource = false;
        uint[]? buffers = null;
        try
        {
            al = AL.GetApi(soft: true);
            device = alc.OpenDevice(null!);
            if (device == null)
            {
                throw new InvalidOperationException("the default device didn't open");
            }

            int* attributes = stackalloc int[] { (int)ContextAttributes.Frequency, Mixer.SampleRate, 0 };
            context = alc.CreateContext(device, attributes);
            if (context == null || !alc.MakeContextCurrent(context))
            {
                throw new InvalidOperationException("the device has no context");
            }

            if (!al.IsExtensionPresent("AL_EXT_float32"))
            {
                throw new InvalidOperationException("the device can't play float samples");
            }

            var format = (BufferFormat)al.GetEnumValue("AL_FORMAT_STEREO_FLOAT32");
            al.GetError();
            source = al.GenSource();
            AudioError error = al.GetError();
            if (error != AudioError.NoError)
            {
                throw new InvalidOperationException($"OpenAL error {error}");
            }

            haveSource = true;
            buffers = al.GenBuffers(DeviceOutput.Blocks);
            error = al.GetError();
            if (error != AudioError.NoError)
            {
                buffers = null;
                throw new InvalidOperationException($"OpenAL error {error}");
            }

            return new OpenAlSpeakers(alc, al, device, context, format, source, buffers);
        }
        catch
        {
            // What was made before the failure goes again, while its context is still current.
            if (buffers is not null)
            {
                al!.DeleteBuffers(buffers);
            }

            if (haveSource)
            {
                al!.DeleteSource(source);
            }

            if (context != null)
            {
                alc.MakeContextCurrent(null);
                alc.DestroyContext(context);
            }

            if (device != null)
            {
                alc.CloseDevice(device);
            }

            al?.Dispose();
            alc.Dispose();
            throw;
        }
    }

    public int Free()
    {
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        if (processed > 0)
        {
            uint* done = stackalloc uint[DeviceOutput.Blocks];
            processed = Math.Min(processed, DeviceOutput.Blocks - _freeCount);
            _al.SourceUnqueueBuffers(_source, processed, done);
            for (int i = 0; i < processed; i++)
            {
                _free[_freeCount++] = done[i];
            }
        }

        return _freeCount;
    }

    public void Queue(ReadOnlySpan<float> stereo)
    {
        uint buffer = _free[--_freeCount];
        fixed (float* samples = stereo)
        {
            _al.BufferData(buffer, _format, samples, stereo.Length * sizeof(float), Mixer.SampleRate);
        }

        _al.SourceQueueBuffers(_source, 1, &buffer);

        // A failed queue would leave the buffer lost and the source short; throwing stops the thread with one line.
        AudioError error = _al.GetError();
        if (error != AudioError.NoError)
        {
            throw new InvalidOperationException($"OpenAL error {error} while queueing sound");
        }
    }

    public void KeepPlaying()
    {
        _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
        if (state != (int)SourceState.Playing)
        {
            _al.SourcePlay(_source);
        }
    }

    public void Dispose()
    {
        _al.SourceStop(_source);
        _al.DeleteSource(_source);
        _al.DeleteBuffers(_buffers);
        _alc.MakeContextCurrent(null);
        _alc.DestroyContext(_context);
        _alc.CloseDevice(_device);
        _al.Dispose();
        _alc.Dispose();
    }
}

namespace Super;

/// <summary>
/// Who mixes: the audio thread of a <see cref="DeviceOutput"/>, or the game loop by hand. It also holds the WAV
/// recording, which makes the game loop mix while it runs. Game thread only.
/// </summary>
internal sealed class AudioDevice
{
    private readonly AudioMixing _mixing;
    private DeviceOutput? _output;
    private AudioRecording? _recording;

    // Who mixes. Set with _output, which a state other than None plays through. See "Lifecycle state in engine code"
    // in docs/design.md.
    private DeviceState _state;

    public AudioDevice(AudioMixing mixing)
    {
        _mixing = mixing;
    }

    private enum DeviceState
    {
        /// <summary>No device: the game loop mixes.</summary>
        None,

        /// <summary>The audio thread mixes into the device.</summary>
        Playing,

        /// <summary><see cref="Pause"/> took the mixer back, so the game loop mixes.</summary>
        Paused,

        /// <summary>A recording took the mixer back, and gives it back to the audio thread when it stops.</summary>
        PausedForRecording,
    }

    /// <summary>
    /// True when the game loop drives the mixer, a tick at a time: in tests and headless runs, when no device opened,
    /// and while the device is paused. False while an audio thread plays it through a device.
    /// </summary>
    public bool Manual => _state != DeviceState.Playing;

    /// <summary>
    /// Plays through the default sound device from now on, mixed on an audio thread, and returns true. If the device
    /// won't open, writes one line saying so to <paramref name="log"/>, stays manual and returns false. The audio
    /// thread also writes to <paramref name="log"/> when a sound fails.
    /// </summary>
    /// <param name="openOverride">For tests: a reason it returns fails the open with that reason.</param>
    public bool Open(TextWriter log, Func<string?>? openOverride = null)
    {
        CheckNoOutput();
        if (DeviceOutput.TryOpen(_mixing, log, openOverride) is not { } output)
        {
            return false;
        }

        Play(output);
        return true;
    }

    /// <summary>As <see cref="Open"/>, into a stand-in for the speakers, so tests can run the audio thread.</summary>
    public void UseOutput(IAudioSink sink, TextWriter log)
    {
        CheckNoOutput();
        Play(new DeviceOutput(_mixing, sink, log));
    }

    /// <summary>
    /// Takes the mixer back from the audio thread, waiting for it to finish the block it's on. From then until
    /// <see cref="Resume"/> the audio is <see cref="Manual"/>: <see cref="Audio.Advance"/> and
    /// <see cref="AudioMixing.Render"/> mix on the calling thread, and the game loop advances it each tick. Does
    /// nothing without a device or when already paused.
    /// </summary>
    public void Pause() => PauseFor(DeviceState.Paused);

    /// <summary>Hands the mixer back to the audio thread. Does nothing without a device or when it isn't paused.</summary>
    public void Resume()
    {
        if (_state is not (DeviceState.Paused or DeviceState.PausedForRecording))
        {
            return;
        }

        _state = DeviceState.Playing;
        _output!.Resume();
    }

    /// <summary>Manual mode only: mixes this many seconds, writing them to the recording if one is running.</summary>
    public void MixFor(float seconds)
    {
        _mixing.MixFor(seconds, _recording);
    }

    /// <summary>
    /// Stops the audio thread and closes the device, if there is one, then ends every sound so streamed clips close
    /// their readers. The game host calls this as it shuts down.
    /// </summary>
    public void Close()
    {
        var recording = _recording;
        _recording = null;
        try
        {
            recording?.Finish();
        }
        finally
        {
            _output?.Dispose();
            _output = null;
            _state = DeviceState.None;
            _mixing.StopAll();
        }
    }

    /// <summary>
    /// Writes everything <see cref="Audio.Advance"/> mixes from now on to a WAV file, until <see cref="StopRecording"/>.
    /// A device is paused for the time, which makes the audio <see cref="Manual"/>: the game loop mixes, a tick at
    /// a time, so the file holds exactly the game's own time.
    /// </summary>
    public void StartRecording(string wavPath)
    {
        if (_recording is not null)
        {
            throw new InvalidOperationException("The audio is already being recorded.");
        }

        StartRecording(new AudioRecording(wavPath));
    }

    /// <summary>As above, into a recording that is already open.</summary>
    public void StartRecording(AudioRecording recording)
    {
        if (_recording is not null)
        {
            recording.Dispose();
            throw new InvalidOperationException("The audio is already being recorded.");
        }

        _recording = recording;
        try
        {
            PauseFor(DeviceState.PausedForRecording);
        }
        catch
        {
            _recording = null;
            recording.Dispose();
            throw;
        }
    }

    /// <summary>Finishes the WAV file and gives a paused device back to the audio thread. Does nothing when not recording.</summary>
    public void StopRecording()
    {
        var recording = _recording;
        if (recording is null)
        {
            return;
        }

        _recording = null;
        try
        {
            recording.Finish();
        }
        finally
        {
            if (_state == DeviceState.PausedForRecording)
            {
                Resume();
            }
        }
    }

    private void Play(DeviceOutput output)
    {
        _output = output;
        _state = DeviceState.Playing;
    }

    private void PauseFor(DeviceState paused)
    {
        if (_state != DeviceState.Playing)
        {
            return;
        }

        _output!.Pause();
        _state = paused;
    }

    private void CheckNoOutput()
    {
        if (_output is not null)
        {
            throw new InvalidOperationException("The audio already plays through a device.");
        }
    }
}

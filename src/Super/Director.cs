namespace Super;

/// <summary>
/// Plays a <see cref="Cutscene"/>. Ask for one in your game's constructor. While a
/// cutscene plays, the director holds the camera, whatever Draw sets, and the time scale,
/// and runs its moments just before each Update. When it ends, both go back to the game.
/// </summary>
public sealed class Director(GameTime time, Transitions transitions)
{
    private Cutscene? _scene;
    private long _ticks;
    private int _shot = -1;
    private float _scaleBefore = 1f;

    /// <summary>Whether a cutscene is playing.</summary>
    public bool IsPlaying => _scene is not null;

    /// <summary>The cutscene playing, or none.</summary>
    public Cutscene? Playing => _scene;

    /// <summary>How far into the cutscene, in seconds of real ticks.</summary>
    public float Seconds => _ticks * time.TickSeconds;

    /// <summary>The camera on the last tick and the one before, which a frame blends between.</summary>
    internal Camera Previous { get; private set; }

    internal Camera Current { get; private set; }

    /// <summary>
    /// Whether a cutscene has placed a camera. It starts on the tick after Play, so until
    /// then the game's camera shows, or the last cutscene's, and a blend into the first
    /// shot starts from it.
    /// </summary>
    internal bool HasCamera { get; private set; }

    /// <summary>Starts a cutscene from its beginning on the next tick, ending any that's playing.</summary>
    public void Play(Cutscene cutscene)
    {
        if (cutscene.ShotCount == 0)
        {
            throw new ArgumentException("The cutscene has no shots. Add one with Then.", nameof(cutscene));
        }

        if (_scene is null)
        {
            _scaleBefore = time.Scale;
        }

        _scene = cutscene;
        _ticks = -1;
        _shot = -1;
    }

    /// <summary>Ends the cutscene now, giving back the camera and the time scale.</summary>
    public void Stop()
    {
        if (_scene is null)
        {
            return;
        }

        _scene = null;
        HasCamera = false;
        time.Scale = _scaleBefore;
    }

    /// <summary>Moves the clock on a tick and runs what's due, before the game's Update.</summary>
    internal void BeforeUpdate()
    {
        if (_scene is null)
        {
            return;
        }

        float from = _ticks < 0 ? float.NegativeInfinity : Seconds;
        _ticks++;
        if (Seconds > _scene.Seconds)
        {
            // Started before Stop, so a blend sets off from the cutscene's last camera.
            var scene = _scene;
            scene.StartTransitions(from, scene.Seconds, transitions);
            Stop();
            if (scene.EndTransition is { } end)
            {
                transitions.Start(end, null, 0f);
            }

            return;
        }

        time.Scale = _scene.ScaleAt(Seconds);
        _scene.StartTransitions(from, Seconds, transitions);
        _scene.Cue(from, Seconds);
    }

    /// <summary>Places the camera once the tick has moved everything, so it sees where things are now.</summary>
    internal void AfterUpdate()
    {
        if (_scene is null || _ticks < 0)
        {
            return;
        }

        var camera = _scene.CameraAt(Seconds);
        int shot = _scene.ShotAt(Seconds).Index;

        // A cut jumps: there's nothing between two shots to blend.
        Previous = shot == _shot ? Current : camera;
        Current = camera;
        _shot = shot;
        HasCamera = true;
    }

    /// <summary>The camera a frame shows, between the last two ticks.</summary>
    internal Camera At(float blend) => Camera.Lerp(Previous, Current, blend);
}

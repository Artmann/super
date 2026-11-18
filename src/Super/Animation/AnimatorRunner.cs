namespace Super;

/// <summary>
/// The animators the game loop moves on, once a tick after the game's Update. It holds
/// them weakly, so a character the game drops stops being run once it's collected. An
/// animator nothing can reach can't change what the game sees, so when that happens
/// doesn't matter to a replay.
/// </summary>
/// <param name="sounds">Names a sound in content, for the clip events that play one.</param>
internal sealed class AnimatorRunner(Func<string, SoundRef> sounds)
{
    private readonly List<WeakReference<Animator>> _animators = [];

    /// <summary>How many animators it still holds, including any not yet collected.</summary>
    public int Count => _animators.Count;

    /// <summary>Names a sound in content, for the clip events that play one.</summary>
    public Func<string, SoundRef> Sounds => sounds;

    /// <summary>Plays the clips' sounds, such as footsteps. The game loop sets it to play on its audio; without it they're silent.</summary>
    public ClipSoundPlayer? Play { get; set; }

    public void Add(Animator animator) => _animators.Add(new WeakReference<Animator>(animator));

    /// <summary>Moves every live animator on, playing their clips' sounds, and forgets the collected ones.</summary>
    public void Advance(float seconds)
    {
        int kept = 0;
        for (int i = 0; i < _animators.Count; i++)
        {
            if (!_animators[i].TryGetTarget(out var animator))
            {
                continue;
            }

            animator.Advance(seconds, Play);
            _animators[kept++] = _animators[i];
        }

        _animators.RemoveRange(kept, _animators.Count - kept);
    }
}

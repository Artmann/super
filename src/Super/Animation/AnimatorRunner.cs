namespace Super;

/// <summary>
/// The animators the game loop moves on, once a tick after the game's Update. It holds
/// them weakly, so a character the game drops stops being run once it's collected. An
/// animator nothing can reach can't change what the game sees, so when that happens
/// doesn't matter to a replay.
/// </summary>
internal sealed class AnimatorRunner
{
    private readonly List<WeakReference<Animator>> _animators = [];

    /// <summary>How many animators it still holds, including any not yet collected.</summary>
    public int Count => _animators.Count;

    public void Add(Animator animator) => _animators.Add(new WeakReference<Animator>(animator));

    /// <summary>Moves every live animator on and forgets the collected ones.</summary>
    public void Advance(float seconds)
    {
        int kept = 0;
        for (int i = 0; i < _animators.Count; i++)
        {
            if (!_animators[i].TryGetTarget(out var animator))
            {
                continue;
            }

            animator.Advance(seconds);
            _animators[kept++] = _animators[i];
        }

        _animators.RemoveRange(kept, _animators.Count - kept);
    }
}

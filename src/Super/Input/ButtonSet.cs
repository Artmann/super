namespace Super;

/// <summary>A set of buttons with the same press, hold and release rules.</summary>
internal sealed class ButtonSet(int count)
{
    private readonly State[] _states = new State[count];

    public bool Down(int index) => _states[index].Down;

    public bool Physical(int index) => _states[index].Physical;

    public bool Pressed(int index) => _states[index].Pressed;

    public bool Released(int index) => _states[index].Released;

    /// <summary>A down event. Repeats while already down are ignored.</summary>
    public void Press(int index)
    {
        ref var state = ref _states[index];
        if (state.Physical)
        {
            return;
        }

        state.Physical = true;
        state.WentDown = true;
    }

    public void Release(int index)
    {
        ref var state = ref _states[index];
        if (!state.Physical)
        {
            return;
        }

        state.Physical = false;
        state.WentUp = true;
    }

    /// <summary>Whether the button went down since the last tick, before the tick sees it.</summary>
    public bool WentDown(int index) => _states[index].WentDown;

    /// <summary>
    /// Keeps this press from the next ticks: the button reads as up until it's let go, and its release
    /// isn't seen either, so whoever reads it sees no part of the press. The interface takes the buttons it uses.
    /// </summary>
    public void Take(int index)
    {
        ref var state = ref _states[index];
        state.Taken = state.Physical || state.WentDown;
        state.WentDown = false;
        state.WentUp = false;
    }

    public void Roll()
    {
        foreach (ref var state in _states.AsSpan())
        {
            if (state.Taken)
            {
                state = new State { Physical = state.Physical, Taken = state.Physical };
                continue;
            }

            state.Pressed = state.WentDown;
            // A tap owed a release from the last tick still gets it, even if the button went down again.
            if (state.Physical)
            {
                state.Down = true;
                state.Released = state.WentUp || state.PendingRelease;
                state.PendingRelease = false;
            }
            else if (state.WentDown)
            {
                // A tap inside one tick: held for this tick, let go on the next.
                state.Down = true;
                state.Released = state.PendingRelease;
                state.PendingRelease = true;
            }
            else
            {
                state.Down = false;
                state.Released = state.WentUp || state.PendingRelease;
                state.PendingRelease = false;
            }

            state.WentDown = false;
            state.WentUp = false;
        }
    }

    private struct State
    {
        public bool Physical;
        public bool Down;
        public bool Pressed;
        public bool Released;
        public bool PendingRelease;
        public bool WentDown;
        public bool WentUp;
        public bool Taken;
    }
}

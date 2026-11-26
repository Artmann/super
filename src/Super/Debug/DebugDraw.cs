namespace Super;

/// <summary>
/// Debug shapes drawn from anywhere, such as Update or a system, that stay on screen across frames. Take it as a
/// service. A shape drawn here shows until the next tick, so one drawn every Update stays up while it's drawn; use
/// <see cref="For"/> to keep one for longer, such as where a shot landed. Shapes with no channel always show, and named
/// channels start hidden: <see cref="Show"/> them from code or with <c>--debug ai,physics</c>. See docs/debugging.md.
/// </summary>
public sealed class DebugDraw : DebugCanvas, ITickHook
{
    private readonly GameTime _time;
    private readonly HashSet<string> _shown = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(DebugLine Line, long Until)> _lines = [];
    private readonly List<(DebugLabel Label, long Until)> _labels = [];
    private readonly Dictionary<long, Kept> _kept = [];

    public DebugDraw(GameTime time)
    {
        _time = time;
    }

    /// <summary>Shows the shapes in a channel. Case doesn't matter.</summary>
    public void Show(string channel) => _shown.Add(channel);

    /// <summary>Hides the shapes in a channel. Shapes with no channel can't be hidden.</summary>
    public void Hide(string channel) => _shown.Remove(channel);

    /// <summary>Shows a hidden channel or hides a shown one, as for a key that turns it on and off.</summary>
    public void Toggle(string channel)
    {
        if (!_shown.Remove(channel))
        {
            _shown.Add(channel);
        }
    }

    /// <summary>Whether a channel is shown.</summary>
    public bool Shows(string channel) => _shown.Contains(channel);

    /// <summary>
    /// A canvas whose shapes stay on screen for <paramref name="seconds"/> of game time, at least until the next tick.
    /// Keep it or ask again: the same time gives the same canvas, without allocating.
    /// </summary>
    public DebugCanvas For(float seconds)
    {
        if (!(seconds >= 0f && float.IsFinite(seconds)))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A debug shape is kept for 0 seconds or more.");
        }

        long ticks = Math.Max(1L, (long)MathF.Round(seconds / _time.TickSeconds));
        if (!_kept.TryGetValue(ticks, out var kept))
        {
            kept = new Kept(this, ticks);
            _kept.Add(ticks, kept);
        }

        return kept;
    }

    /// <summary>Lets the shapes whose time is up go, before the tick draws new ones.</summary>
    void ITickHook.BeforeUpdate()
    {
        Prune(_lines, _time.Tick);
        Prune(_labels, _time.Tick);
    }

    // A loop and not RemoveAll, whose lambda would allocate every tick.
    private static void Prune<T>(List<(T Shape, long Until)> kept, long now)
    {
        int stays = 0;
        for (int i = 0; i < kept.Count; i++)
        {
            if (kept[i].Until > now)
            {
                kept[stays++] = kept[i];
            }
        }

        kept.RemoveRange(stays, kept.Count - stays);
    }

    /// <summary>Copies the shapes still kept into a frame's view.</summary>
    /// <summary>The channels <c>--debug</c> names, split by commas.</summary>
    internal static string[] ChannelNames(string text)
    {
        string[] names = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0)
        {
            throw new ArgumentException($"--debug needs the debug channels to show, split by commas, such as ai,physics, not '{text}'.");
        }

        return names;
    }

    internal void CopyTo(ViewDebug view)
    {
        foreach (var (line, _) in _lines)
        {
            view.AddKept(line);
        }

        foreach (var (label, _) in _labels)
        {
            view.AddKept(label);
        }
    }

    internal override bool Showing(string? channel) => channel is null || _shown.Contains(channel);

    private protected override void Add(in DebugLine line) => _lines.Add((line, _time.Tick + 1));

    private protected override void Add(in DebugLabel label) => _labels.Add((label, _time.Tick + 1));

    /// <summary>A canvas that keeps its shapes for some ticks.</summary>
    private sealed class Kept(DebugDraw owner, long ticks) : DebugCanvas
    {
        internal override bool Showing(string? channel) => owner.Showing(channel);

        private protected override void Add(in DebugLine line) => owner._lines.Add((line, owner._time.Tick + ticks));

        private protected override void Add(in DebugLabel label) => owner._labels.Add((label, owner._time.Tick + ticks));
    }
}

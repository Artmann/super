namespace Super.Ui;

/// <summary>Finds the places focus can stop, in tree order, and the next one in each direction.</summary>
internal static class UiFocusStops
{
    // Far enough to scroll so the ring shows too.
    private const float ScrollMargin = 6f;

    /// <summary>Every stop that's on and shows, in the order the tree draws them. A stop inside another is part of it.</summary>
    public static List<UiNode> All(UiNode? root)
    {
        var found = new List<UiNode>();
        Collect(root, found);
        return found;
    }

    /// <summary>Whether this stop can hold focus: on screen, not turned off, and not squashed to nothing.</summary>
    public static bool IsOn(UiNode node)
    {
        var area = PrimitiveView.ShownArea(node);
        return node is { Mounted: true, View: FocusModifier { Keys.Disabled: false }, Environment.Disabled: false } && area.Width > 0f && area.Height > 0f;
    }

    /// <summary>The outermost stop this node is in, or null.</summary>
    public static UiNode? Outermost(UiNode? node)
    {
        UiNode? stop = null;
        for (; node is not null; node = UiPointerSearch.Up(node))
        {
            if (node.View is FocusModifier)
            {
                stop = node;
            }
        }

        return stop;
    }

    /// <summary>The first node of this kind inside this one, in tree order.</summary>
    public static UiNode? Below<T>(UiNode node)
    {
        if (node.View is T)
        {
            return node;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            // A popup is a place of its own, searched only from its top.
            if (node.Children[i] is { View: not LayerView } child && PrimitiveView.IsShown(node, i) && Below<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>What the click inside a stop does, as Enter presses it, unless the click is off.</summary>
    public static Action? ClickInside(UiNode node) =>
        Below<ClickModifier>(node) is { View: ClickModifier { Disabled: false, Click: { } click }, Environment.Disabled: false } ? click : null;

    /// <summary>The first view marked to take focus by default.</summary>
    public static UiNode? DefaultMark(UiNode? root) => root is null ? null : Below<DefaultFocusModifier>(root);

    /// <summary>The first stop inside the default mark.</summary>
    public static UiNode? Default(UiNode? root) => DefaultMark(root) is { } marked ? All(marked).FirstOrDefault() : null;

    /// <summary>The stop <paramref name="by"/> places on in tree order, round from the end to the start.</summary>
    public static UiNode? InOrder(List<UiNode> stops, UiNode from, int by)
    {
        if (stops.Count == 0)
        {
            return null;
        }

        int at = stops.IndexOf(from);
        if (at < 0)
        {
            return stops[0];
        }

        return stops[(at + by + stops.Count) % stops.Count];
    }

    /// <summary>
    /// The stop an arrow moves to: the nearest that way, or inside <see cref="ControlExtensions.FocusWraps"/> with
    /// nothing more that way inside it, the one furthest back the other way, as if it came in from the far edge.
    /// </summary>
    public static UiNode? Toward(List<UiNode> stops, UiNode from, UiNav way)
    {
        var next = Nearest(stops, from, PrimitiveView.ShownArea(from), way);
        if (UiPointerSearch.Find<FocusWrapModifier>(from) is not { } wrap || next is not null && UiPointerSearch.Inside(next, wrap))
        {
            return next;
        }

        var inside = stops.FindAll(stop => UiPointerSearch.Inside(stop, wrap));
        var edge = PrimitiveView.ShownArea(wrap);
        var area = PrimitiveView.ShownArea(from);
        var start = way switch
        {
            UiNav.Down => area with { Y = edge.Y - area.Height - 1f },
            UiNav.Up => area with { Y = edge.Bottom + 1f },
            UiNav.Right => area with { X = edge.X - area.Width - 1f },
            _ => area with { X = edge.Right + 1f },
        };
        return Nearest(inside, null, start, way) is { } round && round != from ? round : next;
    }

    /// <summary>
    /// The nearest stop that way from <paramref name="start"/>, other than <paramref name="from"/>: the smallest gap
    /// ahead, with a sideways miss counting double, and the lined-up one when two tie. Null at the edge.
    /// </summary>
    private static UiNode? Nearest(List<UiNode> stops, UiNode? from, UiRect start, UiNav way)
    {
        UiNode? best = null;
        float bestScore = float.MaxValue;
        foreach (var stop in stops)
        {
            if (stop != from && Score(start, PrimitiveView.ShownArea(stop), way) is { } score && score < bestScore)
            {
                best = stop;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Scrolls the nearest scroll view round this node so all of it shows, with room for the ring.</summary>
    public static void ScrollIntoView(UiTree tree, UiNode node)
    {
        if (UiPointerSearch.Find<ScrollView>(node.Parent) is not { View: ScrollView scroll } view)
        {
            return;
        }

        bool across = scroll.Axis == Axis.Horizontal;
        var (low, high) = Along(PrimitiveView.ShownArea(node), across);
        var (start, end) = Along(view.Frame, across);
        low -= ScrollMargin;
        high += ScrollMargin;
        float by = low < start ? low - start : high > end ? Math.Min(high - end, low - start) : 0f;
        if (by != 0f)
        {
            UiScrolling.ScrollTo(tree, view, view.Scroll.Offset + by);
        }
    }

    private static void Collect(UiNode? node, List<UiNode> found)
    {
        if (node is null)
        {
            return;
        }

        if (node.View is FocusModifier)
        {
            if (IsOn(node))
            {
                found.Add(node);
            }

            return;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            if (node.Children[i] is { View: not LayerView } child && PrimitiveView.IsShown(node, i))
            {
                Collect(child, found);
            }
        }
    }

    // More than any screen is wide, in units.
    private const float OffLine = 1_000_000f;

    /// <summary>How far a stop is that way, or null when it isn't ahead.</summary>
    private static float? Score(UiRect from, UiRect to, UiNav way)
    {
        bool across = way is UiNav.Left or UiNav.Right;
        float sign = way is UiNav.Left or UiNav.Up ? -1f : 1f;
        var (fromLow, fromHigh) = Along(from, across);
        var (toLow, toHigh) = Along(to, across);
        if ((toLow + toHigh - fromLow - fromHigh) * sign <= 1f)
        {
            return null;
        }

        float gap = MathF.Max(0f, sign > 0f ? toLow - fromHigh : fromLow - toHigh);
        var (fromSide, fromSideEnd) = Along(from, !across);
        var (toSide, toSideEnd) = Along(to, !across);
        float miss = MathF.Max(0f, MathF.Max(toSide - fromSideEnd, fromSide - toSideEnd));
        float drift = MathF.Abs(toSide + toSideEnd - fromSide - fromSideEnd) / 2f;
        // A stop in line wins over any that isn't, however near, so the arrow follows a row past a gap.
        bool inLine = MathF.Min(toSideEnd, fromSideEnd) - MathF.Max(toSide, fromSide) > 0f;
        float offLine = inLine ? 0f : OffLine;
        return offLine + gap + 2f * miss + 0.01f * drift;
    }

    /// <summary>Where a rectangle starts and ends across, or down.</summary>
    private static (float Low, float High) Along(UiRect rect, bool across) =>
        across ? (rect.X, rect.Right) : (rect.Y, rect.Bottom);
}

/// <summary>
/// Keeps focus inside the popup on top. When one opens, focus moves into it, onto its default focus or its
/// first stop, and when it closes focus goes back to what had it before, if that's still there.
/// </summary>
internal sealed class UiFocusTrap(UiTree tree)
{
    private readonly List<(UiNode Layer, UiNode? Opener)> _open = [];

    /// <summary>Where focus may go: the popup on top, or the whole tree when none is open.</summary>
    public UiNode? Root => tree.Layers.Trapping ?? tree.Root;

    /// <summary>
    /// Where focus moves, now that the popup on top has changed, given where it is. Moves is false while the
    /// same popup is on top, or none, as most of the time.
    /// </summary>
    public (bool Moves, UiNode? To) Follow(UiNode? current)
    {
        var top = tree.Layers.Trapping;
        if (_open.Count == 0 ? top is null : _open[^1].Layer == top)
        {
            return (false, null);
        }

        var (closed, back) = Close(top);
        if (top is not null && (_open.Count == 0 || _open[^1].Layer != top))
        {
            _open.Add((top, current));
            tree.Cues.Raise(top, UiCue.Open);
            return (true, UiFocusStops.Default(top) ?? UiFocusStops.All(top).FirstOrDefault());
        }

        return closed ? (true, back is not null && UiFocusStops.IsOn(back) ? back : null) : (false, null);
    }

    /// <summary>Forgets the popups that closed above this one, and returns what opened the last of them.</summary>
    private (bool Closed, UiNode? Opener) Close(UiNode? top)
    {
        bool closed = false;
        UiNode? opener = null;
        while (_open.Count > 0 && _open[^1].Layer != top && !_open[^1].Layer.Mounted)
        {
            opener = _open[^1].Opener;
            _open.RemoveAt(_open.Count - 1);
            closed = true;
        }

        return (closed, opener);
    }

    /// <summary>
    /// Escape or the gamepad's East button, outside a text field: goes back in a navigation stack, or closes
    /// the popup on top as it says. Returns whether either took it: an open popup takes the key even when it
    /// can't be closed that way.
    /// </summary>
    public bool Back(List<Action> callbacks)
    {
        // A stack inside the popup on top goes back before the popup closes.
        if (tree.Layers.GoBack is { } back)
        {
            callbacks.Add(back);
            tree.Cues.Raise(Root!, UiCue.Back);
            return true;
        }

        if (tree.Layers.Trapping is not { View: LayerView layer } top)
        {
            return false;
        }

        if (layer.Dismiss is { } dismiss)
        {
            callbacks.Add(dismiss);
            tree.Cues.Raise(top, UiCue.Back);
        }

        return true;
    }
}

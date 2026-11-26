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
        for (; node is not null; node = node.Parent)
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

        foreach (var child in node.Children)
        {
            if (child is not null && Below<T>(child) is { } found)
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
    /// The nearest stop that way: the smallest gap ahead, with a sideways miss counting double, and the
    /// lined-up one when two tie. Null at the edge.
    /// </summary>
    public static UiNode? Nearest(List<UiNode> stops, UiNode from, UiNav way)
    {
        var start = PrimitiveView.ShownArea(from);
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

        foreach (var child in node.Children)
        {
            Collect(child, found);
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

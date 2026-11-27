namespace Super.Ui;

/// <summary>Looks up the tree from the node under the mouse, for what the mouse can act on there.</summary>
internal static class UiPointerSearch
{
    /// <summary>
    /// The node above this one, short of a popup: what's in a popup belongs to it alone, so a click there
    /// never reaches a button or a scroll view round the view that opened it.
    /// </summary>
    public static UiNode? Up(UiNode node) => node.View is LayerView ? null : node.Parent;

    /// <summary>The nearest node at or above this one that holds a view of this kind.</summary>
    public static UiNode? Find<T>(UiNode? node)
    {
        for (; node is not null; node = Up(node))
        {
            if (node.View is T)
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// A press let go on this clickable node, which held <paramref name="click"/> when it went down. Returns the click,
    /// and asks for its sound, while the node is still on, and asks for the sound for no when it isn't.
    /// </summary>
    public static Action? Clicked(UiNode node, Action? click)
    {
        if (node is not { Mounted: true, View: ClickModifier clickable })
        {
            return null;
        }

        if (clickable.Disabled || node.Environment.Disabled)
        {
            node.Tree.Cues.Raise(node, UiCue.Denied);
            return null;
        }

        if (click is not null)
        {
            node.Tree.Cues.Raise(node, clickable.Cue);
        }

        return click;
    }

    public static bool Inside(UiNode? node, UiNode ancestor)
    {
        for (; node is not null; node = Up(node))
        {
            if (node == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The nearest slider at or above this node that's on, unless something clickable is nearer.</summary>
    public static UiNode? Slidable(UiNode? node)
    {
        for (; node is not null; node = Up(node))
        {
            if (node.View is ClickModifier)
            {
                return null;
            }

            if (node.View is SlideModifier slide)
            {
                return slide.Disabled || node.Environment.Disabled ? null : node;
            }
        }

        return null;
    }

    public static UiNode? Clickable(UiNode? node) => Find<ClickModifier>(node) is { View: ClickModifier { Disabled: false }, Environment.Disabled: false } found ? found : null;

    /// <summary>The nearest drop target that takes this payload, as long as the node isn't inside the drag's own source.</summary>
    public static UiNode? DropTargetUnder(UiNode? node, UiNode source, object payload)
    {
        if (Inside(node, source))
        {
            return null;
        }

        for (; node is not null; node = Up(node))
        {
            if (node.View is DropModifier target && target.Accepts(payload))
            {
                return node;
            }
        }

        return null;
    }
}

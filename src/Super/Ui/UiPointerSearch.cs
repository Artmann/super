namespace Super.Ui;

/// <summary>Looks up the tree from the node under the mouse, for what the mouse can act on there.</summary>
internal static class UiPointerSearch
{
    /// <summary>The nearest node at or above this one that holds a view of this kind.</summary>
    public static UiNode? Find<T>(UiNode? node)
    {
        for (; node is not null; node = node.Parent)
        {
            if (node.View is T)
            {
                return node;
            }
        }

        return null;
    }

    public static bool Inside(UiNode? node, UiNode ancestor)
    {
        for (; node is not null; node = node.Parent)
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
        for (; node is not null; node = node.Parent)
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

        for (; node is not null; node = node.Parent)
        {
            if (node.View is DropModifier target && target.Accepts(payload))
            {
                return node;
            }
        }

        return null;
    }
}

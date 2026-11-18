namespace Super.Ui;

/// <summary>
/// Brings a node's children up to date with new views. This is the reconciler, as in
/// React: a new view takes over an old child when they match, and keeps its state.
/// </summary>
internal static class UiChildren
{
    /// <summary>
    /// Pairs up new views with the parent's children. A pair keeps its state when the type
    /// matches and the position does too, or the <see cref="UiView.Identity"/> if it has one.
    /// Children left without a view are unmounted. Returns the new children.
    /// </summary>
    public static List<UiNode?> Reconcile(UiNode parent, IReadOnlyList<UiView?> views, TextStyle style)
    {
        var previous = parent.Children;
        var next = new List<UiNode?>(views.Count);
        for (int i = 0; i < views.Count; i++)
        {
            if (views[i] is not { } view)
            {
                next.Add(null);
                continue;
            }

            var node = TakeMatch(previous, i, view) ?? new UiNode(parent.Tree, parent, view);
            node.Update(view, style);
            next.Add(node);
        }

        foreach (var left in previous)
        {
            left?.Unmount();
        }

        return next;
    }

    /// <summary>Nothing about the parent changed, but something further down did. Returns whether anything changed.</summary>
    public static bool UpdateInPlace(List<UiNode?> children, TextStyle style)
    {
        bool changed = false;
        foreach (var child in children)
        {
            if (child is not null)
            {
                changed |= child.Update(child.View, style);
            }
        }

        return changed;
    }

    private static bool SameIdentity(UiNode node, UiView view) =>
        node.View.GetType() == view.GetType() && Equals(node.View.Identity, view.Identity);

    private static UiNode? TakeMatch(List<UiNode?> previous, int position, UiView view)
    {
        if (view.Identity is null)
        {
            return position < previous.Count && previous[position] is { } node && SameIdentity(node, view)
                ? Take(previous, position)
                : null;
        }

        for (int i = 0; i < previous.Count; i++)
        {
            if (previous[i] is { } node && SameIdentity(node, view))
            {
                return Take(previous, i);
            }
        }

        return null;
    }

    private static UiNode? Take(List<UiNode?> previous, int index)
    {
        var node = previous[index];
        previous[index] = null;
        return node;
    }
}

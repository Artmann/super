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
    /// Children left without a view are unmounted, and views left without a child get one
    /// from <paramref name="make"/>. Returns the new children.
    /// </summary>
    public static List<TNode?> Reconcile<TNode>(TNode parent, List<TNode?> previous, IReadOnlyList<UiView?> views, TextStyle style, Func<TNode, UiView, TNode> make)
        where TNode : class, IUiReconciled
    {
        var next = new List<TNode?>(views.Count);
        for (int i = 0; i < views.Count; i++)
        {
            if (views[i] is not { } view)
            {
                next.Add(null);
                continue;
            }

            var node = TakeMatch(previous, i, view) ?? make(parent, view);
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
    public static bool UpdateInPlace<TNode>(List<TNode?> children, TextStyle style)
        where TNode : class, IUiReconciled
    {
        bool changed = false;
        foreach (var child in children)
        {
            if (child is not null)
            {
                changed |= child.Update(child.ShownView, style);
            }
        }

        return changed;
    }

    private static bool SameIdentity(IUiReconciled node, UiView view) =>
        node.ShownView.GetType() == view.GetType() && Equals(node.ShownView.Identity, view.Identity);

    private static TNode? TakeMatch<TNode>(List<TNode?> previous, int position, UiView view)
        where TNode : class, IUiReconciled
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

    private static TNode? Take<TNode>(List<TNode?> previous, int index)
        where TNode : class
    {
        var node = previous[index];
        previous[index] = null;
        return node;
    }
}

/// <summary>
/// What the reconciler needs from a node: the view it shows, a way to bring it up to date,
/// and a way to take it out of the tree.
/// </summary>
internal interface IUiReconciled
{
    /// <summary>The view the node shows now.</summary>
    UiView ShownView { get; }

    bool Update(UiView view, TextStyle style);

    void Unmount();
}

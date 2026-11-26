namespace Super.Ui;

/// <summary>
/// What the tree shows above itself: the popups open in it, as they signed up while the tree was placed, and
/// over those the drag preview and the tooltip. A popup that traps, such as a menu or a dialog, takes the
/// pointer and focus while it's on top. Toasts don't trap, and float over the rest.
/// </summary>
internal sealed class UiLayers(UiTree tree)
{
    // The drag preview and the tooltip.
    private readonly UiNode?[] _overlays = new UiNode?[2];
    private readonly List<UiNode> _open = [];
    private (UiNode Node, Action Run)? _back;
    private (UiNode Node, Action<int> Run)? _tabs;

    /// <summary>The drag preview and the tooltip, as the last frame showed them.</summary>
    public IReadOnlyList<UiNode?> Overlays => _overlays;

    /// <summary>The top popup that traps the pointer and focus, or null when none is open.</summary>
    public UiNode? Trapping
    {
        get
        {
            for (int i = _open.Count - 1; i >= 0; i--)
            {
                if (_open[i].View is LayerView { Traps: true })
                {
                    return _open[i];
                }
            }

            return null;
        }
    }

    /// <summary>A popup is open here this layout. It's laid out and drawn once the tree under it is.</summary>
    public void Open(UiNode layer)
    {
        if (!_open.Contains(layer))
        {
            _open.Add(layer);
        }
    }

    /// <summary>
    /// What Escape or the gamepad's East button does when no popup takes it: go back in the innermost
    /// navigation stack that can, if focus may reach it.
    /// </summary>
    public Action? GoBack => _back is { } back && Reaches(back.Node) ? back.Run : null;

    /// <summary>What the gamepad's bumpers do: switch tabs, by -1 or 1, in the innermost tab view focus may reach.</summary>
    public Action<int>? SwitchTab => _tabs is { } tabs && Reaches(tabs.Node) ? tabs.Run : null;

    /// <summary>A navigation stack that can go back was placed. The last one placed, the innermost, wins.</summary>
    public void CanGoBack(UiNode node, Action back) => _back = (node, back);

    /// <summary>A tab view was placed. The last one placed, the innermost, wins.</summary>
    public void SwitchesTabs(UiNode node, Action<int> by) => _tabs = (node, by);

    /// <summary>Forgets the popups, the way back and the tabs, before a layout signs them up again.</summary>
    public void Clear()
    {
        _open.Clear();
        _back = null;
        _tabs = null;
    }

    /// <summary>Whether focus may reach this node: it's in the tree and no popup is open, or it's inside the one on top.</summary>
    private bool Reaches(UiNode node)
    {
        if (!node.Mounted)
        {
            return false;
        }

        var top = Trapping;
        for (var at = node; at is not null && top is not null; at = at.Parent)
        {
            if (at == top)
            {
                return true;
            }
        }

        return top is null;
    }

    /// <summary>
    /// Lays out each popup on the whole screen, along with the popups opened inside them, and draws them above
    /// the tree: those that trap in the order they opened, then the toasts.
    /// </summary>
    public void Show(UiDrawList draw, UiRect screen)
    {
        // Placing a popup can open the ones inside it, which join the end of the list.
        for (int i = 0; i < _open.Count; i++)
        {
            _open[i].Place(screen);
        }

        Emit(draw, traps: true);
        Emit(draw, traps: false);
    }

    /// <summary>What the pointer touches here: a toast, then the top popup, which hides everything under it, then the tree.</summary>
    public UiNode? Hit(float x, float y)
    {
        foreach (var layer in _open)
        {
            if (layer.View is LayerView { Traps: false } && LayerView.HitInside(layer, x, y) is { } toast)
            {
                return toast;
            }
        }

        return Trapping is { } top ? LayerView.HitInside(top, x, y) : tree.Root?.Hit(x, y);
    }

    /// <summary>Ages the toasts on screen by a tick, so they leave when their time is up.</summary>
    public void Tick(float seconds)
    {
        foreach (var layer in _open)
        {
            if (layer.View is LayerView { Content: ToastStack stack })
            {
                stack.Toasts.Age(seconds);
            }
        }
    }

    /// <summary>Shows this view in an overlay slot, laid out afresh, or nothing with null.</summary>
    public UiNode? Overlay(int slot, UiView? view)
    {
        Mount(tree, ref _overlays[slot], view);
        var node = _overlays[slot];
        node?.ForgetLayout();
        return node;
    }

    /// <summary>
    /// Brings the node in a slot up to date with a view: the same node when the view's type
    /// is the same, a new one when it isn't, and none for no view. Returns whether anything changed.
    /// </summary>
    public static bool Mount(UiTree tree, ref UiNode? node, UiView? view)
    {
        if (view is null)
        {
            bool had = node is not null;
            node?.Unmount();
            node = null;
            return had;
        }

        if (node is not null && node.View.GetType() == view.GetType())
        {
            return node.Update(view, UiEnvironment.Default);
        }

        node?.Unmount();
        node = new UiNode(tree, null, view);
        node.Update(view, UiEnvironment.Default);
        return true;
    }

    private void Emit(UiDrawList draw, bool traps)
    {
        foreach (var layer in _open)
        {
            if (layer.View is LayerView view && view.Traps == traps)
            {
                layer.Emit(draw);
            }
        }
    }
}

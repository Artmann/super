namespace Super.Ui;

/// <summary>
/// One place in the interface. Views come and go, but a node stays as long as its place
/// does, and holds what must last: state and layout. Bringing a node up to date with a
/// new view is the reconciler, as in React.
/// </summary>
internal sealed class UiNode(UiTree tree, UiNode? parent, UiView view) : IUiReconciled
{
    private readonly UiSizeMemo _sizes = new();

    // Only components have hooks, made on their first run of Body.
    private UiBodyHooks? _hooks;
    private UiNodeChanges _changes = UiNodeChanges.Self;

    public UiTree Tree { get; } = tree;

    public UiNode? Parent { get; } = parent;

    public UiView View { get; private set; } = view;

    UiView IUiReconciled.ShownView => View;

    public UiEnvironment Environment { get; private set; } = UiEnvironment.Default;

    public List<UiNode?> Children { get; private set; } = [];

    public UiRect Frame { get; private set; }

    /// <summary>A scroll view's position and lengths. It's kept here so it survives new descriptions.</summary>
    public UiScrollState Scroll => field ??= new UiScrollState();

    /// <summary>Whether this is still in the tree. The mouse lets go of anything that isn't.</summary>
    public bool Mounted { get; private set; } = true;

    /// <summary>
    /// Brings this node up to date with a view. Equal views stop here, which is what makes
    /// redescribing the whole interface cheap. Returns whether anything changed.
    /// </summary>
    public bool Update(UiView view, UiEnvironment style)
    {
        bool changed = _changes.HasFlag(UiNodeChanges.Self) || style != Environment || (!ReferenceEquals(view, View) && !view.Equals(View));
        if (!changed && !_changes.HasFlag(UiNodeChanges.Below))
        {
            return false;
        }

        View = view;
        Environment = style;
        _changes = UiNodeChanges.None;
        if (!changed)
        {
            var inherited = view is PrimitiveView same ? same.Inherit(style) : style;
            return UiChildren.UpdateInPlace(Children, inherited);
        }

        if (view is PrimitiveView primitive)
        {
            Children = UiChildren.Reconcile(this, Children, primitive.Children, primitive.Inherit(style), static (parent, child) => new UiNode(parent.Tree, parent, child));
        }
        else
        {
            // A component's one child is what its Body describes, run with its hooks.
            _hooks ??= new UiBodyHooks(MarkDirty);
            var body = _hooks.Run(view, style, static component => component.Body);
            Children = UiChildren.Reconcile(this, Children, [body], style, static (parent, child) => new UiNode(parent.Tree, parent, child));
        }

        return true;
    }

    /// <summary>Runs this node again on the next update, and lets the nodes above know to look down here.</summary>
    public void MarkDirty()
    {
        _changes |= UiNodeChanges.Self;
        for (var node = Parent; node is not null; node = node.Parent)
        {
            node._changes |= UiNodeChanges.Below;
        }
    }

    public UiSize Measure(Proposal proposal)
    {
        if (_sizes.TryRecall(proposal, out var size))
        {
            return size;
        }

        size = View is PrimitiveView primitive ? primitive.Measure(this, proposal) : Children[0]!.Measure(proposal);
        _sizes.Remember(proposal, size);
        return size;
    }

    public void Place(UiRect frame)
    {
        Frame = frame;
        if (View is PrimitiveView primitive)
        {
            primitive.Place(this, frame);
        }
        else
        {
            Children[0]!.Place(frame);
        }
    }

    public void Emit(UiDrawList draw)
    {
        if (View is PrimitiveView primitive)
        {
            primitive.Emit(this, draw);
        }

        // A popup is drawn above the whole tree, not in its place.
        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i] is { View: not LayerView } child && PrimitiveView.IsShown(this, i))
            {
                child.Emit(draw);
            }
        }

        (View as PrimitiveView)?.EmitAfter(this, draw);
    }

    /// <summary>What the mouse touches at this point, front to back, from this node down.</summary>
    public UiNode? Hit(float x, float y)
    {
        if (View is not PrimitiveView primitive)
        {
            return Children[0]!.Hit(x, y);
        }

        if (primitive.Misses(this, x, y))
        {
            return null;
        }

        if (primitive.HitsFirst(this, x, y))
        {
            return this;
        }

        for (int i = Children.Count - 1; i >= 0; i--)
        {
            if (primitive.Shows(this, i) && Children[i]?.Hit(x, y) is { } hit)
            {
                return hit;
            }
        }

        return primitive.Blocks && primitive.HitArea(this).Contains(x, y) ? this : null;
    }

    public void ForgetLayout()
    {
        _sizes.Clear();
        foreach (var child in Children)
        {
            child?.ForgetLayout();
        }
    }

    public void Unmount()
    {
        Mounted = false;
        _hooks?.StopReading();
        foreach (var child in Children)
        {
            child?.Unmount();
        }
    }
}

/// <summary>What changed at and below a node since it was last brought up to date.</summary>
[Flags]
internal enum UiNodeChanges
{
    None = 0,

    /// <summary>A state this node read changed, so it runs again.</summary>
    Self = 1,

    /// <summary>Something further down changed.</summary>
    Below = 2,
}

namespace Super.Ui;

/// <summary>
/// One place in the interface. Views come and go, but a node stays as long as its place
/// does, and holds what must last: state and layout. Bringing a node up to date with a
/// new view is the reconciler, as in React.
/// </summary>
internal sealed class UiNode(UiTree tree, UiNode? parent, UiView view)
{
    /// <summary>Layout asks the same question a few times. Four answers cover what stacks ask.</summary>
    private const int MaxMeasurements = 4;

    [ThreadStatic]
    private static UiNode? _rendering;

    private readonly List<object> _states = [];
    private readonly List<IWatchedState> _watching = [];
    private readonly (Proposal Proposal, UiSize Size)[] _measurements = new (Proposal, UiSize)[MaxMeasurements];
    private int _measurementCount;
    private int _nextState;
    private bool _dirty = true;
    private bool _dirtyBelow;

    /// <summary>The component whose Body is running, which is who reads of state belong to.</summary>
    public static UiNode? Rendering => _rendering;

    public UiTree Tree { get; } = tree;

    public UiNode? Parent { get; } = parent;

    public UiView View { get; private set; } = view;

    public TextStyle Style { get; private set; }

    public List<UiNode?> Children { get; private set; } = [];

    public UiRect Frame { get; private set; }

    /// <summary>How far a scroll view has moved. It's kept here so it survives new descriptions.</summary>
    public float ScrollOffset { get; set; }

    /// <summary>The length of a scroll view's content, from its last layout.</summary>
    public float ContentLength { get; set; }

    /// <summary>The length of a scroll view's window on its content, from its last layout.</summary>
    public float ViewportLength { get; set; }

    public float MaxScroll => Math.Max(ContentLength - ViewportLength, 0f);

    /// <summary>Whether a scroll view's bar is under the mouse.</summary>
    public bool BarLit { get; set; }

    /// <summary>Whether this is still in the tree. The mouse lets go of anything that isn't.</summary>
    public bool Mounted { get; private set; } = true;

    /// <summary>
    /// Brings this node up to date with a view. Equal views stop here, which is what makes
    /// redescribing the whole interface cheap. Returns whether anything changed.
    /// </summary>
    public bool Update(UiView view, TextStyle style)
    {
        bool changed = _dirty || style != Style || (!ReferenceEquals(view, View) && !view.Equals(View));
        if (!changed && !_dirtyBelow)
        {
            return false;
        }

        View = view;
        Style = style;
        _dirtyBelow = false;
        if (!changed)
        {
            return UpdateChildrenInPlace();
        }

        _dirty = false;
        if (view is PrimitiveView primitive)
        {
            Reconcile(primitive.Children, primitive.Inherit(style));
        }
        else
        {
            Reconcile([RunBody(view)], style);
        }

        return true;
    }

    public State<T> UseState<T>(T initial)
    {
        if (_nextState == _states.Count)
        {
            _states.Add(new State<T>(initial));
        }

        if (_states[_nextState++] is not State<T> state)
        {
            throw new InvalidOperationException(
                $"{View.GetType().Name} called UseState in a different order than last time. Call it the same way on every run of Body.");
        }

        return state;
    }

    public void Watch(IWatchedState state) => _watching.Add(state);

    public void MarkDirty()
    {
        _dirty = true;
        for (var node = Parent; node is not null; node = node.Parent)
        {
            node._dirtyBelow = true;
        }
    }

    public UiSize Measure(Proposal proposal)
    {
        for (int i = 0; i < _measurementCount; i++)
        {
            if (_measurements[i].Proposal == proposal)
            {
                return _measurements[i].Size;
            }
        }

        var size = View is PrimitiveView primitive ? primitive.Measure(this, proposal) : Children[0]!.Measure(proposal);
        _measurements[_measurementCount % MaxMeasurements] = (proposal, size);
        _measurementCount = Math.Min(_measurementCount + 1, MaxMeasurements);
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

    public void Emit()
    {
        if (View is PrimitiveView primitive)
        {
            primitive.Emit(this, Tree);
        }

        foreach (var child in Children)
        {
            child?.Emit();
        }

        (View as PrimitiveView)?.EmitAfter(this, Tree);
    }

    /// <summary>What the mouse touches at this point, front to back, from this node down.</summary>
    public UiNode? Hit(float x, float y)
    {
        if (View is not PrimitiveView primitive)
        {
            return Children[0]!.Hit(x, y);
        }

        if (primitive.PassesPointer || primitive.ClipArea(this) is { } clip && !clip.Contains(x, y))
        {
            return null;
        }

        if (primitive.HitsFirst(this, x, y))
        {
            return this;
        }

        for (int i = Children.Count - 1; i >= 0; i--)
        {
            if (Children[i]?.Hit(x, y) is { } hit)
            {
                return hit;
            }
        }

        return primitive.Blocks && primitive.HitArea(this).Contains(x, y) ? this : null;
    }

    public void ForgetLayout()
    {
        _measurementCount = 0;
        foreach (var child in Children)
        {
            child?.ForgetLayout();
        }
    }

    public void Unmount()
    {
        Mounted = false;
        StopWatching();
        foreach (var child in Children)
        {
            child?.Unmount();
        }
    }

    private static bool SameIdentity(UiNode node, UiView view) =>
        node.View.GetType() == view.GetType() && Equals(node.View.Identity, view.Identity);

    private UiView RunBody(UiView view)
    {
        StopWatching();
        _nextState = 0;
        var outer = _rendering;
        _rendering = this;
        try
        {
            return view.Body;
        }
        finally
        {
            _rendering = outer;
        }
    }

    private void StopWatching()
    {
        foreach (var state in _watching)
        {
            state.Forget(this);
        }

        _watching.Clear();
    }

    /// <summary>Nothing here changed, but something further down did.</summary>
    private bool UpdateChildrenInPlace()
    {
        var style = View is PrimitiveView primitive ? primitive.Inherit(Style) : Style;
        bool changed = false;
        foreach (var child in Children)
        {
            if (child is not null)
            {
                changed |= child.Update(child.View, style);
            }
        }

        return changed;
    }

    /// <summary>
    /// Pairs up new views with existing nodes. A pair keeps its state when the type matches
    /// and the position does too, or the <see cref="UiView.Identity"/> if it has one.
    /// </summary>
    private void Reconcile(IReadOnlyList<UiView?> views, TextStyle style)
    {
        var previous = Children;
        var next = new List<UiNode?>(views.Count);
        for (int i = 0; i < views.Count; i++)
        {
            if (views[i] is not { } view)
            {
                next.Add(null);
                continue;
            }

            var node = TakeMatch(previous, i, view) ?? new UiNode(Tree, this, view);
            node.Update(view, style);
            next.Add(node);
        }

        foreach (var left in previous)
        {
            left?.Unmount();
        }

        Children = next;
    }

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

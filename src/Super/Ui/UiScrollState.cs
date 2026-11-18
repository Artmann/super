namespace Super.Ui;

/// <summary>
/// How far a scroll view has moved, and the lengths its last layout found. It's kept on
/// the view's node, so it survives new descriptions.
/// </summary>
internal sealed class UiScrollState
{
    public float Offset { get; set; }

    /// <summary>The length of the content, from the last layout.</summary>
    public float ContentLength { get; set; }

    /// <summary>The length of the window on the content, from the last layout.</summary>
    public float ViewportLength { get; set; }

    public float MaxScroll => Math.Max(ContentLength - ViewportLength, 0f);

    /// <summary>Whether the bar is under the mouse.</summary>
    public bool BarLit { get; set; }
}

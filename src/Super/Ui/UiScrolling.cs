namespace Super.Ui;

/// <summary>Moves scroll views, from the wheel or a dragged thumb.</summary>
internal static class UiScrolling
{
    private const float WheelStep = 48f;

    /// <summary>
    /// Turns the wheel over this node. A wheel turns up and down, so a sideways strip inside
    /// a page leaves it to the page. Returns whether anything scrolled.
    /// </summary>
    public static bool Wheel(UiTree tree, UiNode hit, float notches) =>
        WheelOn(tree, hit, Axis.Vertical, notches) || WheelOn(tree, hit, Axis.Horizontal, notches);

    /// <summary>Moves a scroll view as close to this offset as it goes. Returns whether it moved.</summary>
    public static bool ScrollTo(UiTree tree, UiNode node, float offset)
    {
        var scroll = node.Scroll;
        offset = Math.Clamp(offset, 0f, scroll.MaxScroll);
        if (offset == scroll.Offset)
        {
            return false;
        }

        scroll.Offset = offset;
        tree.RequestLayout();
        return true;
    }

    /// <summary>The nearest scroll view along this axis that can still move takes the wheel.</summary>
    private static bool WheelOn(UiTree tree, UiNode hit, Axis axis, float notches)
    {
        for (var node = UiPointerSearch.Find<ScrollView>(hit); node is not null; node = UiPointerSearch.Find<ScrollView>(node.Parent))
        {
            if (((ScrollView)node.View).Axis == axis && ScrollTo(tree, node, node.Scroll.Offset - notches * WheelStep))
            {
                return true;
            }
        }

        return false;
    }
}

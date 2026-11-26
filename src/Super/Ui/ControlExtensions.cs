namespace Super.Ui;

/// <summary>Modifiers for making controls of your own, the way the built-in ones are made.</summary>
public static class ControlExtensions
{
    /// <summary>
    /// Says what this view means, so a test can find it by role and read its state, as it does a built-in
    /// control's. See <see cref="UiTree.Elements"/>.
    /// </summary>
    public static UiView Semantics(this UiView view, UiRole role, string label, string? value = null, bool? isChecked = null) =>
        new SemanticsModifier(view, new UiSemantics(role, label, value, isChecked));

    /// <summary>
    /// Follows the left button while it's held down on this view, from the press until it lets go, wherever
    /// the cursor goes. <paramref name="slide"/> gets how far across the view the cursor is, from 0 at the
    /// leading edge to 1 at the trailing one, once a tick on the tick, as a click does. <paramref name="inset"/>
    /// keeps that much of each end out of the range, for a thumb whose middle stops short of the edge.
    /// It's what a <see cref="Slider"/> is made with.
    /// </summary>
    public static UiView OnSlide(this UiView view, Action<float> slide, float inset = 0f) => new SlideModifier(view, slide, inset);
}

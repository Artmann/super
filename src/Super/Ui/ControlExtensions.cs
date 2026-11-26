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

    /// <summary>
    /// Lets Tab, the arrows and the gamepad stop on this view, with a ring round it. Enter, Space and the
    /// gamepad's South button call <paramref name="onActivate"/>, or press the click inside when it's null.
    /// <paramref name="onAdjust"/> gets -1 or 1 for left and right, for a control that changes a value. Every
    /// built-in control can take focus already. A view that can take focus inside this one is part of it.
    /// </summary>
    public static UiView Focusable(this UiView view, Action? onActivate = null, Action<int>? onAdjust = null) =>
        new FocusModifier(view, new FocusKeys { Activate = onActivate, Adjust = onAdjust });

    /// <summary>
    /// Gives focus to the first view inside that can take it, as soon as this appears, with its ring hidden until
    /// a key or the gamepad moves it. Then a menu answers the gamepad and Enter from the start. With nothing
    /// focused, the arrows, the d-pad and the stick come back here.
    /// </summary>
    public static UiView DefaultFocus(this UiView view) => new DefaultFocusModifier(view);

    /// <summary>
    /// Calls <paramref name="action"/> when a finger rests on this view for half a second without moving, on the
    /// tick, as a click does. The finger lifting after that doesn't tap. A view turned off with
    /// <see cref="UiExtensions.Disabled"/> ignores it.
    /// </summary>
    public static UiView OnLongPress(this UiView view, Action action) => new LongPressModifier(view, action);
}

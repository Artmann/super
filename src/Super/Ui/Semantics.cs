using System.Globalization;

namespace Super.Ui;

/// <summary>What kind of control a view is, for tests and anything else that reads the interface by meaning.</summary>
public enum UiRole
{
    Button,
    Switch,
    Checkbox,
    Slider,
    Stepper,

    /// <summary>A choice of one from several, such as a segmented control or a group of radio buttons.</summary>
    Picker,

    /// <summary>One of a picker's choices. It's checked when it's the one picked.</summary>
    Option,

    ProgressBar,
    Gauge,

    /// <summary>A text field. Its label is the prompt and its value the text, or dots for a secure field.</summary>
    TextField,

    /// <summary>A button that opens a menu. It's checked while the menu is open.</summary>
    Menu,

    /// <summary>One item in an open menu. It's checked when it shows a check, as the picked one in a picker's menu does.</summary>
    MenuItem,

    /// <summary>An alert or a confirmation dialog. Its label is the title and its value the message.</summary>
    Dialog,

    /// <summary>A toast. Its label is the title and its value the message.</summary>
    Toast,
}

/// <summary>
/// What a view means: what kind of control it is, what it's called, and the state it shows. Every built-in
/// control describes itself with this, and a game's own controls can too, with <c>.Semantics(...)</c>.
/// </summary>
public readonly record struct UiSemantics(UiRole Role, string Label, string? Value = null, bool? Checked = null)
{
    /// <summary>Whether the control is turned off by itself. One inside <c>.Disabled()</c> is off as well.</summary>
    public bool Disabled { get; init; }

    /// <summary>A number as a value, written the same way in every language, so a test can compare it.</summary>
    public static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// A control on screen, as a test sees it: what it is, what it's called, the state it shows, and where it is.
/// Read them with <see cref="UiTree.Elements"/>, or in a test one by its id with <c>HeadlessRun.Element</c>.
/// </summary>
public sealed record UiElement
{
    /// <summary>The id given to the control, or to a view it's the whole of, with <c>.Id(...)</c>.</summary>
    public object? Id { get; init; }

    public required UiRole Role { get; init; }

    public required string Label { get; init; }

    /// <summary>What it's set to, such as a slider's number or the picked option's label, in invariant text.</summary>
    public string? Value { get; init; }

    /// <summary>Whether a switch, checkbox or option is on. Null for a control that can't be.</summary>
    public bool? Checked { get; init; }

    /// <summary>Whether it's turned off, by itself or by a <c>.Disabled()</c> around it.</summary>
    public bool Disabled { get; init; }

    /// <summary>Where it shows, in interface units.</summary>
    public UiRect Frame { get; init; }

    /// <summary>Whether it has focus, so Enter, the arrows and the gamepad act on it.</summary>
    public bool Focused { get; init; }
}

internal sealed record SemanticsModifier(UiView Child, UiSemantics Meaning) : Modifier(Child);

/// <summary>Reads the controls on screen from the tree.</summary>
internal static class UiElements
{
    /// <summary>Adds the controls at and below this node. <paramref name="focused"/> is the one focus is on, from <see cref="FocusedOf"/>.</summary>
    public static void Collect(UiNode node, List<UiElement> found, UiNode? focused)
    {
        if (node.View is SemanticsModifier { Meaning: var meaning })
        {
            found.Add(new UiElement
            {
                Id = IdOf(node),
                Role = meaning.Role,
                Label = meaning.Label,
                Value = meaning.Value,
                Checked = meaning.Checked,
                Disabled = meaning.Disabled || node.Environment.Disabled,
                Frame = PrimitiveView.ShownArea(node),
                Focused = node == focused,
            });
        }

        foreach (var child in node.Children)
        {
            if (child is not null)
            {
                Collect(child, found, focused);
            }
        }
    }

    /// <summary>
    /// The control focus is on: the first one described inside the focused view, as a picker holds its options, or
    /// else the nearest one round it, as a button is described round what it presses.
    /// </summary>
    public static UiNode? FocusedOf(UiNode? focus) =>
        focus is null ? null : UiFocusStops.Below<SemanticsModifier>(focus) ?? UiPointerSearch.Find<SemanticsModifier>(focus);

    /// <summary>The control with this id. It throws if none has it, listing the ids there are, or if more than one has it.</summary>
    public static UiElement Find(IReadOnlyList<UiElement> elements, object id)
    {
        var matches = elements.Where(element => Equals(element.Id, id)).ToArray();
        var ids = elements.Where(element => element.Id is not null).Select(element => element.Id).Distinct().ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(ids.Length > 0
                ? $"No control on screen has the id '{id}'. These ids are: {string.Join(", ", ids)}."
                : $"No control on screen has the id '{id}', and none has an id."),
            _ => throw new InvalidOperationException($"{matches.Length} controls on screen have the id '{id}', so it doesn't say which one."),
        };
    }

    /// <summary>
    /// The id on this node, or on the views around it that are only it, such as the control that
    /// described it. A view with more than one child is more than the control, so the search stops there.
    /// </summary>
    private static object? IdOf(UiNode node)
    {
        for (UiNode? at = node; at is not null; at = at.Parent)
        {
            if (at.View.Identity is { } identity)
            {
                return identity;
            }

            // A popup isn't more of the control it hangs off.
            if (at.Parent is { Children.Count: > 1, View: not PopupModifier })
            {
                return null;
            }
        }

        return null;
    }
}

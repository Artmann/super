using Super.Ui;

namespace StorybookExample;

/// <summary>
/// One page of the storybook: an element on its own and in its variations, or a bigger example built from
/// several. <paramref name="Show"/> builds it fresh each frame, as a game's interface is.
/// </summary>
public sealed record Story(string Group, string Name, string Summary, Func<UiView> Show)
{
    /// <summary>The name in lower case with dashes, for <c>--story</c>: "Key hints" is <c>key-hints</c>.</summary>
    public string Id => Name.ToLowerInvariant().Replace(' ', '-');
}

/// <summary>Which story to open and in which theme, from the command line.</summary>
public sealed record StoryChoice(string? Story = null, bool Light = false)
{
    /// <summary>Reads <c>--story</c> and <c>--theme</c>, leaving every other option to the game.</summary>
    public static StoryChoice Parse(IReadOnlyList<string> args)
    {
        var choice = new StoryChoice();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--story")
            {
                choice = choice with { Story = ValueAfter(args, i) };
            }
            else if (args[i] == "--theme")
            {
                choice = choice with { Light = ThemeIsLight(ValueAfter(args, i)) };
            }
        }

        return choice;
    }

    private static string ValueAfter(IReadOnlyList<string> args, int index) =>
        index + 1 < args.Count ? args[index + 1] : throw new ArgumentException($"{args[index]} needs a value after it.");

    private static bool ThemeIsLight(string name) => name switch
    {
        "light" => true,
        "dark" => false,
        _ => throw new ArgumentException($"--theme takes dark or light, not \"{name}\"."),
    };
}

/// <summary>Every story, in the order the sidebar shows them.</summary>
public static class Stories
{
    public static IReadOnlyList<Story> All { get; } =
    [
        new("Foundations", "Typography", "Text styles, weights, alignment and colours from the theme.", Foundations.Typography),
        new("Foundations", "Colours", "Every colour in the theme, which built-in controls take.", Foundations.Colours),
        new("Foundations", "Shadows", "The theme's three shadows, and shadows of your own.", Foundations.Shadows),
        new("Foundations", "Shapes", "Shapes fill the space they're offered, filled or stroked.", Foundations.Shapes),
        new("Foundations", "Gradients", "Linear and radial gradients, for backgrounds and shapes.", Foundations.Gradients),
        new("Foundations", "Icons", "Line icons that take the size and colour of the text around them.", Foundations.Icons),
        new("Elements", "Badges", "Short tags for counts, statuses and rarities.", Elements.Badges),
        new("Elements", "Labels", "Text with an icon before it.", Elements.Labels),
        new("Elements", "Dividers", "A line across a stack, either way.", Elements.Dividers),
        new("Elements", "Key hints", "Keycaps for prompts and shortcuts.", Elements.KeyHints),
        new("Controls", "Buttons", "Six styles, icons, control sizes, and turned off alone or in a group.", Controls.Buttons),
        new("Controls", "Toggles", "Switches and checkboxes for settings that are on or off.", Controls.Toggles),
        new("Controls", "Sliders", "A number picked by dragging, smoothly or in steps.", Controls.Sliders),
        new("Controls", "Steppers", "A whole number with minus and plus.", Controls.Steppers),
        new("Controls", "Pickers", "One choice from several, segmented or as radio buttons.", Controls.Pickers),
        new("Controls", "Progress", "How far along a task is, as a bar or a ring.", Controls.Progress),
        new("Controls", "Gauges", "A reading on a scale, as a ring or a bar.", Controls.Gauges),
        new("Keyboard and gamepad", "Text fields", "A line of text to type, plain or secure.", Typing.TextFields),
        new("Keyboard and gamepad", "Focus", "Moving between controls with the keys, the d-pad or the stick.", Typing.Focus),
        new("Fingers", "Touch", "Taps, long presses, and drags that scroll and fling.", Touching.Touch),
        new("Popups", "Menus", "Menus from a button, a picker as a menu, and a context menu.", Popping.Menus),
        new("Popups", "Dialogs", "Alerts and confirmation dialogs that need an answer.", Popping.Dialogs),
        new("Popups", "Sheets", "A form in a card over a scrim, and a popover under a button.", Popping.Sheets),
        new("Popups", "Toasts", "Short notes in the corner that leave on their own.", Popping.ToastStory),
        new("Layout", "Layers", "Overlays, backgrounds, offsets, hiding and aspect ratios.", Arranging.Layers),
        new("Layout", "Grids", "Rows whose columns line up, and equal columns.", Arranging.Grids),
        new("Layout", "Flows", "Rows that wrap, views that fit, and which view gets its room first.", Arranging.Flows),
        new("Layout", "Lazy lists", "Long lists that only make the rows near the screen.", Arranging.LazyLists),
        new("Containers", "Cards", "Raised panels, and labels with a value at the end of the row.", Containing.Cards),
        new("Containers", "Forms", "Sections of settings with lines between, and groups that open.", Containing.Forms),
        new("Containers", "Tabs", "Pages a bar of tabs switches between.", Containing.Tabs),
        new("Containers", "Navigation", "Pages that go deeper, with a way back.", Containing.Navigation),
    ];

    /// <summary>The story with this id. An unknown id throws, listing the ones there are.</summary>
    public static Story Find(string id) =>
        All.FirstOrDefault(story => story.Id == id)
        ?? throw new ArgumentException($"There's no story \"{id}\". The stories are {string.Join(", ", All.Select(story => story.Id))}.");
}

using Super.Ui;

namespace Super;

/// <summary>What the screen shows when an error stopped the game while hot reload is on.</summary>
internal static class StoppedPanel
{
    internal const string Title = "The game stopped";
    internal const string CarryOn = "Fix it and save. The game carries on once the change is applied.";

    /// <summary>Enough of the stack to find the line, without filling the screen.</summary>
    private const int StackLines = 6;

    private static readonly Color Fill = Color.Hex("#2A1216", 0.92f);
    private static readonly Color Edge = Color.Hex("#C0505A");
    private static readonly Color Muted = Color.Hex("#D8B4B8");

    internal static UiView For(Exception error)
    {
        var panel = new VStack(HorizontalAlignment.Leading, Spacing: 6f)
        {
            new Text(Title).Bold().FontSize(22f),
            new Text($"{error.GetType().Name}: {error.Message}"),
        };

        foreach (string line in Stack(error))
        {
            panel.Add(new Text(line).FontSize(13f).Foreground(Muted));
        }

        panel.Add(new Text(CarryOn).Foreground(Muted).Padding(8f, 0f, 0f, 0f));

        return panel
            .Padding(16f)
            .Frame(maxWidth: UiExtensions.Infinity, alignment: Alignment.TopLeading)
            .Background(Fill)
            .Border(1f, Edge)
            .CornerRadius(8f)
            .Padding(16f)
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);
    }

    private static IEnumerable<string> Stack(Exception error) =>
        (error.StackTrace ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(StackLines);
}

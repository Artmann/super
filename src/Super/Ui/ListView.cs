namespace Super.Ui;

/// <summary>
/// A scrolling list you can pick from. <paramref name="Row"/> describes an item, and this
/// handles how rows look under the mouse and when selected. Items name their rows, so
/// they should differ from each other. Hand it a new list when the items change: a list
/// changed in place looks the same as the last one, so the rows wouldn't update.
/// </summary>
public sealed record ListView<T>(IReadOnlyList<T> Items, Func<T, UiView> Row, T? Selected = null, Action<T>? OnSelect = null) : UiView
    where T : class
{
    private static readonly Color Clear = new(0f, 0f, 0f, 0f);
    private static readonly Color Hovered = new(1f, 1f, 1f, 0.07f);
    private static readonly Color Held = new(1f, 1f, 1f, 0.04f);
    private static readonly Color Chosen = new(0.36f, 0.52f, 0.78f, 0.45f);

    public override UiView Body
    {
        get
        {
            var scroll = new ScrollView();
            foreach (var item in Items)
            {
                scroll.Add(new Pressable(look => Describe(item, look), () => OnSelect?.Invoke(item)).Id(item));
            }

            return scroll;
        }
    }

    private UiView Describe(T item, PressState look)
    {
        var color = Equals(item, Selected) ? Chosen : look.Pressed ? Held : look.Hovered ? Hovered : Clear;
        return Row(item)
            .Frame(maxWidth: UiExtensions.Infinity, alignment: Alignment.Leading)
            .Padding(10f, 6f)
            .Background(color)
            .CornerRadius(4f);
    }
}

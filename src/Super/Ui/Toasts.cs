namespace Super.Ui;

/// <summary>A short note that shows in the corner of the screen for a few seconds, such as "Game saved".</summary>
public sealed record Toast(string Title, string? Message = null)
{
    public IconName? Icon { get; init; }
}

/// <summary>
/// The toasts a game has on screen. Make one, put it on the root of the interface with
/// <c>.Toasts(toasts)</c>, and call <see cref="Show(Toast, float)"/> from anywhere in the game. Each toast
/// leaves when its time is up, or when it's clicked or tapped. Time only runs while toasts show.
/// </summary>
public sealed class Toasts
{
    /// <summary>How long a toast shows when nothing says otherwise.</summary>
    public const float DefaultSeconds = 4f;

    private readonly State<IReadOnlyList<ShownToast>> _shown = new([]);
    private float _clock;

    /// <summary>How many show at once. Showing another lets the oldest go.</summary>
    public int MaxShown { get; init; } = 3;

    /// <summary>The toasts on screen, oldest first.</summary>
    public IReadOnlyList<Toast> Shown => _shown.Value.Select(shown => shown.Toast).ToArray();

    /// <summary>What's on screen, for the view that shows it, which this signs up for changes.</summary>
    internal IReadOnlyList<ShownToast> Current => _shown.Value;

    /// <summary>Shows a toast for <paramref name="seconds"/>, under the ones already there.</summary>
    public void Show(Toast toast, float seconds = DefaultSeconds)
    {
        ArgumentNullException.ThrowIfNull(toast);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        var shown = _shown.Value.Append(new ShownToast(toast, _clock + seconds));
        _shown.Value = shown.Skip(Math.Max(0, _shown.Value.Count + 1 - Math.Max(1, MaxShown))).ToArray();
    }

    /// <summary>Shows a toast with only a title.</summary>
    public void Show(string title, float seconds = DefaultSeconds) => Show(new Toast(title), seconds);

    /// <summary>Takes the first toast equal to this one off the screen, before its time is up.</summary>
    public void Dismiss(Toast toast)
    {
        if (_shown.Value.FirstOrDefault(shown => shown.Toast == toast) is { } found)
        {
            Dismiss(found);
        }
    }

    internal void Dismiss(ShownToast shown) => _shown.Value = _shown.Value.Where(other => !ReferenceEquals(other, shown)).ToArray();

    /// <summary>Runs time on by a tick, and takes off the toasts whose time is up. It only makes a new list when one goes.</summary>
    internal void Age(float seconds)
    {
        _clock += seconds;
        var shown = _shown.Value;
        for (int i = 0; i < shown.Count; i++)
        {
            if (shown[i].Until <= _clock)
            {
                float clock = _clock;
                _shown.Value = shown.Where(other => other.Until > clock).ToArray();
                return;
            }
        }
    }
}

/// <summary>A toast on screen, and when its time is up, on its <see cref="Toasts"/>' clock.</summary>
internal sealed record ShownToast(Toast Toast, float Until);

/// <summary>A view with the toasts floating over the bottom trailing corner of the screen.</summary>
internal sealed record ToastsView(UiView Child, Toasts Toasts) : UiView
{
    public override UiView Body
    {
        get
        {
            var shown = Toasts.Current;
            return new PopupModifier(Child, shown.Count > 0 ? new LayerView(new ToastStack(Toasts, shown), LayerPlace.Corner, null, Traps: false) : null);
        }
    }
}

/// <summary>The toasts on screen, one under the other, each a card a click takes away.</summary>
internal sealed record ToastStack(Toasts Toasts, IReadOnlyList<ShownToast> Shown) : UiView
{
    public override UiView Body
    {
        get
        {
            var stack = new VStack(HorizontalAlignment.Trailing, 8f);
            foreach (var shown in Shown)
            {
                // Each rises in as it arrives. The newest is always added at the end, so it's the one that's new.
                stack.Add(Card(shown).Transition(Entrance.Rise));
            }

            return stack;
        }
    }

    private UiView Card(ShownToast shown)
    {
        var theme = Environment.Theme;
        var toast = shown.Toast;
        var words = new VStack(HorizontalAlignment.Leading, 2f)
        {
            new Text(toast.Title).Weight(FontWeight.SemiBold),
            toast.Message is { } message ? new Text(message).Foreground(Tone.Muted) : null,
        };
        var row = new HStack(VerticalAlignment.Top, 10f)
        {
            toast.Icon is { } icon ? new Icon(icon).Padding(0f, 2f) : null,
            words,
            new Spacer(),
        };
        var toasts = Toasts;
        var card = row
            .Padding(14f, 12f)
            .Frame(minWidth: 260f, maxWidth: 360f)
            .Background(theme.Colors.Raised)
            .Border(1f, theme.Colors.Border)
            .CornerRadius(theme.RadiusLarge)
            .Shadow(theme.Shadows.Medium);
        var click = new ClickModifier(card, () => toasts.Dismiss(shown));
        return new SemanticsModifier(click, new UiSemantics(UiRole.Toast, toast.Title, toast.Message));
    }
}

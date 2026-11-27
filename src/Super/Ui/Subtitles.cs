using System.Globalization;

namespace Super.Ui;

/// <summary>A line of speech for <see cref="Subtitles"/>, shown from <paramref name="Start"/> to <paramref name="End"/> seconds.</summary>
public sealed record SubtitleLine(string Text, float Start, float End)
{
    /// <summary>Who's speaking, shown before the line in their colour, or nobody.</summary>
    public string? Speaker { get; init; }

    /// <summary>The colour of the speaker's name, the theme's primary when it isn't set.</summary>
    public Color? SpeakerColor { get; init; }
}

/// <summary>
/// The lines of a scene or a voice clip, in the order they're said, for finding the one to show at a time: start a clock
/// when the speech starts and pass <c>track.At(seconds)</c> to <see cref="Subtitles"/>.
/// </summary>
public sealed class SubtitleTrack
{
    private readonly SubtitleLine[] _lines;

    public SubtitleTrack(IEnumerable<SubtitleLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _lines = lines.OrderBy(line => line.Start).ToArray();
        foreach (var line in _lines)
        {
            if (!(line.End > line.Start))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"The subtitle \"{line.Text}\" ends at {line.End} seconds, which isn't after it starts at {line.Start}."), nameof(lines));
            }
        }
    }

    /// <summary>The lines, earliest first.</summary>
    public IReadOnlyList<SubtitleLine> Lines => _lines;

    /// <summary>When the last line ends, in seconds.</summary>
    public float Length => _lines.Length == 0 ? 0f : _lines.Max(line => line.End);

    /// <summary>The line said at <paramref name="seconds"/>, the one that started last when they overlap, or null between lines.</summary>
    public SubtitleLine? At(float seconds)
    {
        for (int i = _lines.Length - 1; i >= 0; i--)
        {
            if (_lines[i].Start <= seconds && seconds < _lines[i].End)
            {
                return _lines[i];
            }
        }

        return null;
    }
}

/// <summary>
/// The words being said, centred at the bottom of the screen on a dark backing so they read over anything, with the
/// speaker's name before them. Null shows nothing. Put it in a full-screen layer; it keeps clear of the bottom edge.
/// Give players a setting for <see cref="Size"/> and <see cref="Backing"/>, as subtitles that are too small or too faint
/// are no use.
/// </summary>
public sealed record Subtitles(SubtitleLine? Line) : UiView
{
    /// <summary>The size of the words in interface units.</summary>
    public float Size { get; init; } = 22f;

    /// <summary>How dark the backing is, from 0, none, to 1, black.</summary>
    public float Backing { get; init; } = 0.6f;

    // The widest the words go before they wrap, and the room kept to the bottom of the screen.
    private const float MaxWidth = 900f;
    private const float BottomRoom = 48f;

    public override UiView Body
    {
        get
        {
            if (Line is not { } line)
            {
                return new Spacer();
            }

            var colors = Environment.Theme.Colors;
            var words = new HStack(VerticalAlignment.Top, Size * 0.35f)
            {
                line.Speaker is { } speaker ? new Text(speaker + ":").Weight(FontWeight.Bold).Foreground(line.SpeakerColor ?? colors.Primary) : null,
                new Text(line.Text).Foreground(Color.White),
            };
            var backed = words.FontSize(Size)
                .Padding(Size * 0.6f, Size * 0.3f)
                .Background(Color.Black with { A = Math.Clamp(Backing, 0f, 1f) })
                .CornerRadius(Size * 0.25f)
                .Frame(maxWidth: MaxWidth);
            return backed.Id(line).Transition(Entrance.Fade, 0.12f).Padding(0f, 0f, BottomRoom, 0f)
                .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.Bottom);
        }
    }
}

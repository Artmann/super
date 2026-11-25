using Super.Ui;

namespace Super;

/// <summary>The typefaces a game's interface uses besides the built-in one. Reached through the content's <c>Fonts</c>.</summary>
public sealed class ContentFonts
{
    private readonly IContentFiles _files;
    private readonly Dictionary<(string Path, string? Bold), GameFont> _fonts = [];

    internal ContentFonts(IContentFiles files) => _files = files;

    /// <summary>
    /// Names a cooked font by the name of its file, such as <c>fonts/pirata-one.ttf</c>, with the
    /// file of its bold weight if it has one. Naming the same files again gives the same font. A
    /// name that isn't a cooked font stops here with a <see cref="ContentException"/>.
    /// </summary>
    public GameFont Font(string path, string? bold = null)
    {
        if (!_fonts.TryGetValue((path, bold), out var font))
        {
            font = new GameFont(path, Read(path), bold, bold is null ? null : Read(bold));
            _fonts.Add((path, bold), font);
        }

        return font;
    }

    private StbTrueTypeSharp.StbTrueType.stbtt_fontinfo Read(string path)
    {
        ContentNames.Require(_files, path, ContentKind.Data);
        return GameFont.Read(path, CookedBytes.Read(_files, path, "font"));
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Super.Cook;

/// <summary>What a cooker is given: one source, its settings, and a way to read the files beside it.</summary>
public sealed class CookInput
{
    private static readonly JsonSerializerOptions SettingsOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SourceFile _file;
    private readonly SourceAccess _files;

    internal CookInput(SourceFile file, byte[] bytes, JsonObject settings, string target, StepCache? steps = null)
    {
        _file = file;
        _files = new SourceAccess(file);
        Bytes = bytes;
        Settings = settings;
        Target = target;
        StepResults = new StepResults(steps);
    }

    /// <summary>
    /// The results of slow steps an earlier cook kept, to look up with <see cref="StepResults.Recall"/> and keep with
    /// <see cref="StepResults.Keep"/>.
    /// </summary>
    public StepResults StepResults { get; }

    /// <summary>The source's name, such as <c>items/axe.json</c>.</summary>
    public string Name => _file.Name;

    public byte[] Bytes { get; }

    /// <summary>The path on disk, or null when the source isn't a file.</summary>
    public string? FullPath => _file.Root.FullPath(_file.Name);

    public JsonObject Settings { get; }

    public string Target { get; }

    /// <summary>Every file the cooker read through <see cref="Read"/>, <see cref="Find"/> or <see cref="Track"/>, relative to the source's folder.</summary>
    public IReadOnlyList<string> Inputs => _files.Inputs;

    /// <summary>Where this source's cook spent its time, phase by phase.</summary>
    internal PhaseClock Clock { get; } = new();

    /// <summary>
    /// Times a phase of the cook until the result is disposed, as in <c>using (input.Time("decode")) { ... }</c>.
    /// Time in a phase of the same name adds up. It shows in <c>super-cook --timings</c> and <c>--verbose</c>.
    /// </summary>
    public PhaseClock.Measure Time(string phase) => Clock.Start(phase);

    /// <summary>Reads the settings as <typeparamref name="T"/>. A setting that <typeparamref name="T"/> doesn't have is an error.</summary>
    public T SettingsAs<T>() where T : new()
    {
        return Settings.Deserialize<T>(SettingsOptions) ?? new T();
    }

    /// <summary>
    /// Reads a file by a path relative to the source's folder. The name must match the root's listing
    /// exactly, case included, on every OS, so a cook that works on Windows works everywhere.
    /// </summary>
    /// <exception cref="FileNotFoundException">The root has no file by that exact name. The message names one differing only in case.</exception>
    public byte[] Read(string relative) => _files.Read(relative);

    /// <summary>Looks for a file beside the source, then in every folder under it.</summary>
    /// <returns>The path relative to the source's folder, or null when there is none.</returns>
    public string? Find(string fileName) => _files.Find(fileName);

    /// <summary>
    /// The name in the root of a file by its path from the source's folder, such as <c>textures/ripples.png</c> for
    /// <c>../textures/ripples.png</c> beside <c>models/a.glb</c>, or null when the root has no such file. It isn't read
    /// or tracked, so for a file that's cooked as an asset of its own and only named by this one.
    /// </summary>
    public string? NameOf(string relative) => _files.NameOf(relative);

    /// <summary>
    /// Whether the root's Only picks <paramref name="name"/>, a name in the root such as <see cref="NameOf"/> gives, so it's
    /// cooked as an asset of its own. Without an Only every file is. A file Only leaves out can still be read, but the game
    /// can't load it by name.
    /// </summary>
    public bool IsPicked(string name) => Sources.Picks(_file.Root, name);

    /// <summary>
    /// Records a file the cooker opened some other way, so a change to it counts as a change to this source. Like
    /// <see cref="Read"/>, <see cref="Find"/> and <see cref="Time"/>, it may be called from several threads at once.
    /// </summary>
    public void Track(string relative) => _files.Track(relative);
}

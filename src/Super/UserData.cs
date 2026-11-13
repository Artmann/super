using System.Runtime.InteropServices;
using System.Text.Json;

namespace Super;

/// <summary>
/// What belongs to the person playing rather than to a save slot: settings, unlocks,
/// whether they've seen the intro. Each class you store gets its own JSON file in the
/// player's own folder for this game.
/// </summary>
public sealed class UserData
{
    private const string Extension = ".json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    // Characters no file system takes in a folder name, so an id stays the same on every platform.
    private static readonly char[] NotInNames = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly Lock _lock = new();
    private readonly Dictionary<Type, object> _loaded = [];
    private readonly Dictionary<string, Type> _names = [];
    private readonly TextWriter _warnings;

    /// <summary>Keeps files in <paramref name="folder"/>. Nothing is written until the first save.</summary>
    public UserData(string folder, TextWriter? warnings = null)
    {
        Folder = Path.GetFullPath(folder);
        _warnings = warnings ?? Console.Error;
    }

    /// <summary>The folder the files go in.</summary>
    public string Folder { get; }

    /// <summary>
    /// The one shared copy of <typeparamref name="T"/>, read from its file the first time.
    /// With no file it starts from the class's own defaults. A broken file is kept aside as
    /// <c>.broken</c> and the defaults are used, so a bad file never stops the game.
    /// </summary>
    public T Get<T>()
        where T : class, new()
    {
        lock (_lock)
        {
            if (_loaded.TryGetValue(typeof(T), out object? loaded))
            {
                return (T)loaded;
            }

            string path = PathFor(typeof(T));
            T value = Read<T>(path) ?? new T();
            _loaded[typeof(T)] = value;
            return value;
        }
    }

    /// <summary>
    /// Writes the shared copy of <typeparamref name="T"/> to its file now. The old file is
    /// only replaced once the new one is complete, so a crash part way leaves the old one.
    /// </summary>
    public void Save<T>()
        where T : class, new()
    {
        lock (_lock)
        {
            T value = Get<T>();
            string path = PathFor(typeof(T));
            string unfinished = path + ".tmp";
            Directory.CreateDirectory(Folder);
            try
            {
                using (var stream = File.Create(unfinished))
                {
                    JsonSerializer.Serialize(stream, value, Json);
                }

                File.Move(unfinished, path, overwrite: true);
            }
            catch
            {
                File.Delete(unfinished);
                throw;
            }
        }
    }

    /// <summary>The usual folder for a game's user data on this machine.</summary>
    public static string FolderFor(string gameId)
    {
        var platform = OperatingSystem.IsWindows() ? UserDataPlatform.Windows
            : OperatingSystem.IsMacOS() ? UserDataPlatform.MacOS
            : UserDataPlatform.Linux;
        return FolderFor(gameId, platform, Environment.GetEnvironmentVariable);
    }

    internal static string FolderFor(string gameId, UserDataPlatform platform, Func<string, string?> environment)
    {
        CheckId(gameId);
        return platform switch
        {
            UserDataPlatform.Windows => Join(Needed(environment, "APPDATA"), '\\', gameId),
            UserDataPlatform.MacOS => Join(Needed(environment, "HOME"), '/', "Library/Application Support/" + gameId),
            _ => environment("XDG_CONFIG_HOME") is { Length: > 0 } config
                ? Join(config, '/', gameId)
                : Join(Needed(environment, "HOME"), '/', ".config/" + gameId),
        };
    }

    /// <summary>Turns a window title into a folder name by dropping the characters folders can't hold.</summary>
    internal static string IdFromTitle(string title)
    {
        string kept = new([.. title.Where(c => !char.IsControl(c) && !NotInNames.Contains(c))]);
        return string.Join(' ', kept.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
    }

    internal static void CheckId(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId) || gameId.Trim('.').Length == 0)
        {
            throw new ArgumentException($"A game id must name a folder, so '{gameId}' won't do. Set GameOptions.Id to something like \"reef\".");
        }

        if (gameId.IndexOfAny(NotInNames) >= 0 || gameId.Any(char.IsControl) || gameId != gameId.Trim())
        {
            throw new ArgumentException(
                $"The game id '{gameId}' can't be a folder name. Leave out {string.Join(' ', NotInNames)} and spaces at the ends.");
        }
    }

    internal static string Needed(Func<string, string?> environment, string name) =>
        environment(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} isn't set, so there's no user folder to keep user data in. Pass --user-data to choose one.");

    internal static string Join(string root, char separator, string rest) => root.TrimEnd(separator) + separator + rest;

    private string PathFor(Type type)
    {
        if (type.IsGenericType)
        {
            throw new ArgumentException($"{type.Name} is generic, so it has no file name. Store a plain class instead.");
        }

        if (_names.TryGetValue(type.Name, out Type? other) && other != type)
        {
            throw new ArgumentException(
                $"{type.FullName} and {other.FullName} would share the file {type.Name}{Extension}. Rename one of them.");
        }

        _names[type.Name] = type;
        return Path.Combine(Folder, type.Name + Extension);
    }

    private T? Read<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string? problem;
        try
        {
            using var stream = File.OpenRead(path);
            if (JsonSerializer.Deserialize<T>(stream, Json) is { } value)
            {
                return value;
            }

            problem = "it holds null";
        }
        catch (JsonException e)
        {
            problem = e.Message;
        }

        string broken = path + ".broken";
        File.Move(path, broken, overwrite: true);
        _warnings.WriteLine($"{path} couldn't be read ({problem}). It's kept as {broken}, and {typeof(T).Name} starts from its defaults.");
        return null;
    }
}

internal enum UserDataPlatform
{
    Windows,
    MacOS,
    Linux,
}

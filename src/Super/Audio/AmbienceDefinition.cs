using System.Text.Json;

namespace Super;

/// <summary>A bed of looping sounds with one-shots scattered over it, read from an .ambience.json file.</summary>
internal sealed record AmbienceDefinition(Layer[] Layers, Scattered[] Scattered);

/// <summary>A clip that loops for as long as the ambience plays.</summary>
internal sealed record Layer(SoundClip Clip, float Volume);

/// <summary>Clips played now and then: one picked at random every MinSeconds to MaxSeconds.</summary>
internal sealed record Scattered(
    SoundClip[] Clips,
    float MinSeconds,
    float MaxSeconds,
    float MinVolume,
    float MaxVolume,
    float PitchVariation);

/// <summary>Reads .ambience.json files.</summary>
internal static class AmbienceReader
{
    /// <summary>
    /// Reads an ambience from its file bytes. Clip paths in the file are relative to the file's folder,
    /// and loadClip reads one by its path inside the content folder.
    /// </summary>
    public static AmbienceDefinition Read(string path, byte[] bytes, Func<string, SoundClip> loadClip)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException problem)
        {
            throw Mistake(path, $"isn't valid JSON: {problem.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Mistake(path, "must hold one JSON object with layers and scattered.");
            }

            Func<string, SoundClip> clipAt = file => Clip(path, FolderOf(path), file, loadClip);
            Layer[] layers = ReadList(path, root, "layers", (layer, place) => ReadLayer(path, layer, place, clipAt));
            Scattered[] scattered = ReadList(path, root, "scattered", (entry, place) => ReadScattered(path, entry, place, clipAt));
            return new AmbienceDefinition(layers, scattered);
        }
    }

    /// <summary>Reads each item of the optional list <paramref name="name"/>, naming it as name[index] in errors.</summary>
    private static T[] ReadList<T>(string path, JsonElement root, string name, Func<JsonElement, string, T> read)
    {
        if (!root.TryGetProperty(name, out JsonElement list))
        {
            return [];
        }

        return [.. Items(path, list, name).Select((item, i) => read(item, $"{name}[{i}]"))];
    }

    private static Layer ReadLayer(string path, JsonElement layer, string place, Func<string, SoundClip> clipAt)
    {
        string file = RequireString(path, layer, "file", place);
        float volume = layer.TryGetProperty("volume", out JsonElement v) ? Number(path, v, $"{place}.volume") : 1f;
        if (volume < 0)
        {
            throw Mistake(path, $"{place}.volume is {Text(volume)}, but it can't be below 0.");
        }

        return new Layer(clipAt(file), volume);
    }

    private static Scattered ReadScattered(string path, JsonElement entry, string place, Func<string, SoundClip> clipAt)
    {
        SoundClip[] clips = ReadClips(path, entry, place, clipAt);

        (float minSeconds, float maxSeconds) = Range(path, entry, "every", place, required: true);
        if (minSeconds <= 0)
        {
            throw Mistake(path, $"{place}.every starts at {Text(minSeconds)}, but it must be above 0.");
        }

        (float minVolume, float maxVolume) = Range(path, entry, "volume", place, required: false);
        if (minVolume < 0)
        {
            throw Mistake(path, $"{place}.volume starts at {Text(minVolume)}, but it can't be below 0.");
        }

        float pitch = entry.TryGetProperty("pitchVariation", out JsonElement p) ? Number(path, p, $"{place}.pitchVariation") : 0f;
        if (!(pitch >= 0 && pitch < 1))
        {
            throw Mistake(path, $"{place}.pitchVariation is {Text(pitch)}, but it must be from 0 up to, but not including, 1.");
        }

        return new Scattered(clips, minSeconds, maxSeconds, minVolume, maxVolume, pitch);
    }

    private static SoundClip[] ReadClips(string path, JsonElement entry, string place, Func<string, SoundClip> clipAt)
    {
        if (!entry.TryGetProperty("files", out JsonElement files))
        {
            throw Mistake(path, $"{place} has no files.");
        }

        var clips = new List<SoundClip>();
        foreach (JsonElement file in Items(path, files, $"{place}.files"))
        {
            if (file.ValueKind != JsonValueKind.String)
            {
                throw Mistake(path, $"{place}.files must hold file names.");
            }

            clips.Add(clipAt(file.GetString()!));
        }

        if (clips.Count == 0)
        {
            throw Mistake(path, $"{place}.files is empty, so there is nothing to play.");
        }

        return [.. clips];
    }

    private static (float Min, float Max) Range(string path, JsonElement entry, string name, string place, bool required)
    {
        if (!entry.TryGetProperty(name, out JsonElement range))
        {
            if (required)
            {
                throw Mistake(path, $"{place} has no {name}. Give it as [min, max].");
            }

            return (1f, 1f);
        }

        if (range.ValueKind != JsonValueKind.Array || range.GetArrayLength() != 2)
        {
            throw Mistake(path, $"{place}.{name} must be two numbers, [min, max].");
        }

        float min = Number(path, range[0], $"{place}.{name}");
        float max = Number(path, range[1], $"{place}.{name}");
        if (min > max)
        {
            throw Mistake(path, $"{place}.{name} is [{Text(min)}, {Text(max)}], but the first number can't be above the second.");
        }

        return (min, max);
    }

    private static IEnumerable<JsonElement> Items(string path, JsonElement list, string place)
    {
        if (list.ValueKind != JsonValueKind.Array)
        {
            throw Mistake(path, $"{place} must be a list.");
        }

        return list.EnumerateArray();
    }

    private static float Number(string path, JsonElement value, string place)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw Mistake(path, $"{place} must be a number.");
        }

        return value.GetSingle();
    }

    private static string RequireString(string path, JsonElement entry, string name, string place)
    {
        if (!entry.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw Mistake(path, $"{place} needs a {name}, as a file name.");
        }

        return value.GetString()!;
    }

    private static SoundClip Clip(string path, string folder, string file, Func<string, SoundClip> loadClip)
    {
        try
        {
            return loadClip(Join(folder, file));
        }
        catch (ContentException problem)
        {
            throw new ContentException([.. problem.Problems.Select(line => $"{path}: {line}")]);
        }
    }

    private static string FolderOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    /// <summary>Joins a folder and a relative path inside the content folder, resolving . and .. parts.</summary>
    private static string Join(string folder, string relative)
    {
        var parts = new List<string>(folder.Length == 0 ? [] : folder.Split('/'));
        foreach (string part in relative.Replace('\\', '/').Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part.Length > 0 && part != ".")
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }

    private static ContentException Mistake(string path, string what) => new([$"{path}: {what}"]);

    /// <summary>A number as the file writes it, whatever the culture: 0.5, never 0,5.</summary>
    private static string Text(float value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

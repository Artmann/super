using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Super;

/// <summary>A file that passed its checks, waiting to become a record.</summary>
internal sealed record CheckedFile(string Path, ReadOnlyMemory<byte> Bytes, string Id)
{
    public ReadOnlySpan<byte> Json => Bytes.Span;
}

/// <summary>Reads JSON content. It belongs to one <see cref="Content"/>, so nothing is shared between games.</summary>
internal sealed class ContentReader
{
    private readonly IContentFiles _files;

    public ContentReader(Content content, IContentFiles files)
    {
        _files = files;
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new ModelRefConverter(content));
        options.Converters.Add(new RefConverterFactory());
        options.MakeReadOnly();
        Options = options;
    }

    public JsonSerializerOptions Options { get; }

    /// <summary>Stops a type that can't be loaded before any file is read. This is a mistake in code, not data.</summary>
    public void RequireId(Type type)
    {
        if (!Options.GetTypeInfo(type).Properties.Any(property => property.Name == "id" && property.PropertyType == typeof(string)))
        {
            throw new InvalidOperationException($"{type.Name} can't be loaded with LoadAll, because it has no string Id.");
        }
    }

    /// <summary>Reads a folder and writes down what's wrong with it. Only files that passed come back.</summary>
    public IReadOnlyList<CheckedFile> Check(string folder, Type type, List<string> problems, List<ContentReference> references)
    {
        if (_files.List(folder) is not { } paths)
        {
            problems.Add($"content/{folder}: there is no such folder");
            return [];
        }

        var found = new List<CheckedFile>(paths.Count);
        var ids = new Dictionary<string, string>();
        foreach (string path in paths)
        {
            var json = WithoutByteOrderMark(_files.Read(path));
            var validator = new ContentValidator(path, json, Options, _files, problems, references);
            validator.Check(type);
            if (!validator.Passed)
            {
                continue;
            }

            CheckId(validator, path, ids);
            if (validator.Passed)
            {
                found.Add(new CheckedFile(path, json, validator.Id!));
            }
        }

        return found;
    }

    private static void CheckId(ContentValidator validator, string path, Dictionary<string, string> ids)
    {
        if (validator.Id is null)
        {
            validator.Report(validator.IdLine, "id is missing");
        }
        else if (validator.Id.Length == 0)
        {
            validator.Report(validator.IdLine, "id must not be empty");
        }
        else if (!ids.TryAdd(validator.Id, path))
        {
            validator.Report(validator.IdLine, $"id {validator.Id} is already used by {ids[validator.Id]}");
        }
    }

    private static ReadOnlyMemory<byte> WithoutByteOrderMark(byte[] bytes)
    {
        bool marked = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return marked ? bytes.AsMemory(3) : bytes;
    }

    private sealed class RefConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Ref<>);

        public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter?)Activator.CreateInstance(typeof(RefConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]));
    }

    private sealed class RefConverter<T> : JsonConverter<Ref<T>>
        where T : notnull
    {
        public override Ref<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, Ref<T> value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Id);
    }

    private sealed class ModelRefConverter(Content content) : JsonConverter<ModelRef>
    {
        public override ModelRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            content.Model(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, ModelRef value, JsonSerializerOptions options) =>
            throw new NotSupportedException("Content is only read.");
    }
}

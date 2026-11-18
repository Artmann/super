using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Super;

/// <summary>
/// Walks one JSON file alongside the shape of its record, and writes down everything
/// wrong with it. The serializer can't do this: it stops at the first problem, and it
/// doesn't report the line an object started on.
/// </summary>
internal sealed class ContentValidator
{
    private const string SchemaField = "$schema";
    private const string IdField = "id";

    private readonly JsonProblemLog _log;
    private readonly ReadOnlyMemory<byte> _json;
    private readonly JsonSerializerOptions _options;
    private readonly IContentFiles _files;
    private readonly List<ContentReference> _references;

    /// <summary>A validator for one file, whose problems go into <paramref name="log"/> and references into <paramref name="references"/>.</summary>
    public ContentValidator(JsonProblemLog log, ReadOnlyMemory<byte> json, JsonSerializerOptions options, IContentFiles files, List<ContentReference> references)
    {
        _log = log;
        _json = json;
        _options = options;
        _files = files;
        _references = references;
    }

    /// <summary>The record's id, if the file gave one.</summary>
    public string? Id { get; private set; }

    /// <summary>The line the id is on, or the first line if there isn't one.</summary>
    public int IdLine { get; private set; } = 1;

    public void Check(Type type)
    {
        var reader = new Utf8JsonReader(_json.Span, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        try
        {
            reader.Read();
            Value(ref reader, type, string.Empty, nullable: false, top: true);
            while (reader.Read())
            {
                // Reading on finds anything after the record, which isn't valid JSON.
            }
        }
        catch (JsonException error)
        {
            _log.Report((int)(error.LineNumber ?? 0) + 1, $"this is not valid JSON: {JsonProblemLog.Plain(error.Message)}");
        }
    }

    private static bool IsRef(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Ref<>);

    /// <summary>What a value of this type must be, for the message when it's missing.</summary>
    private static string Word(Type type) =>
        type == typeof(ModelRef) || type == typeof(Rig) || IsRef(type) ? "text" : JsonLeafCheck.Word(type);

    private void Value(ref Utf8JsonReader reader, Type type, string field, bool nullable, bool top = false)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            type = inner;
            nullable = true;
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            if (!nullable)
            {
                _log.Wrong(ref reader, field, Word(type));
            }

            return;
        }

        if (NamedValue(ref reader, type, field))
        {
            return;
        }

        var info = _options.GetTypeInfo(type);
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object:
                Fields(ref reader, info, field, top);
                break;
            case JsonTypeInfoKind.Enumerable:
                Elements(ref reader, info, field);
                break;
            case JsonTypeInfoKind.Dictionary:
                Entries(ref reader, info, field);
                break;
            default:
                JsonLeafCheck.Leaf(ref reader, type, field, _log, _options.PropertyNamingPolicy!);
                break;
        }
    }

    /// <summary>Checks a value written in a way of its own: a model or rig by name, a reference, or a vector. False for anything else.</summary>
    private bool NamedValue(ref Utf8JsonReader reader, Type type, string field)
    {
        if (type == typeof(ModelRef) || type == typeof(Rig))
        {
            NamedModel(ref reader, field, type == typeof(Rig));
            return true;
        }

        if (IsRef(type))
        {
            Reference(ref reader, type.GetGenericArguments()[0], field);
            return true;
        }

        if (type == typeof(Vector3))
        {
            JsonLeafCheck.ThreeNumbers(ref reader, field, _log);
            return true;
        }

        return false;
    }

    private void Fields(ref Utf8JsonReader reader, JsonTypeInfo info, string field, bool top)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            _log.Wrong(ref reader, field, "an object");
            return;
        }

        int opened = _log.LineOf(reader.TokenStartIndex);
        var seen = new HashSet<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            Field(ref reader, info, field, top, seen);
        }

        RequireFields(info, field, seen, opened);
    }

    /// <summary>Checks one field of an object, from its name on.</summary>
    private void Field(ref Utf8JsonReader reader, JsonTypeInfo info, string field, bool top, HashSet<string> seen)
    {
        string name = reader.GetString()!;
        int line = _log.LineOf(reader.TokenStartIndex);
        reader.Read();
        var property = info.Properties.FirstOrDefault(candidate => candidate.Name == name);
        if (property is null)
        {
            if (!(top && name == SchemaField))
            {
                _log.Report(line, $"{JsonProblemLog.FieldPath(field, name)} is not a field of {info.Type.Name}");
            }

            reader.Skip();
            return;
        }

        seen.Add(name);
        if (top && name == IdField && reader.TokenType == JsonTokenType.String)
        {
            Id = reader.GetString();
            IdLine = line;
        }

        Value(ref reader, property.PropertyType, JsonProblemLog.FieldPath(field, name), property.IsSetNullable);
    }

    /// <summary>Reports every field an object must have but didn't, on the line the object opened.</summary>
    private void RequireFields(JsonTypeInfo info, string field, HashSet<string> seen, int opened)
    {
        foreach (var property in info.Properties)
        {
            bool required = property.IsRequired || property.AssociatedParameter is { HasDefaultValue: false, IsMemberInitializer: false };
            if (required && !seen.Contains(property.Name))
            {
                _log.Report(opened, $"{JsonProblemLog.FieldPath(field, property.Name)} is missing");
            }
        }
    }

    private void Elements(ref Utf8JsonReader reader, JsonTypeInfo info, string field)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            _log.Wrong(ref reader, field, "a list");
            return;
        }

        int index = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            Value(ref reader, info.ElementType!, $"{field}[{index}]", nullable: false);
            index++;
        }
    }

    private void Entries(ref Utf8JsonReader reader, JsonTypeInfo info, string field)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            _log.Wrong(ref reader, field, "an object");
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string key = reader.GetString()!;
            reader.Read();
            Value(ref reader, info.ElementType!, JsonProblemLog.FieldPath(field, key), nullable: false);
        }
    }

    /// <summary>Checks a cooked model named by text, and for a rig that the model has one.</summary>
    private void NamedModel(ref Utf8JsonReader reader, string field, bool rig)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            _log.Wrong(ref reader, field, "text");
            return;
        }

        string name = reader.GetString()!;
        string? problem = ContentNames.Problem(_files, name, ContentKind.Model);
        if (rig)
        {
            problem ??= ContentNames.RigProblem(_files, name);
        }

        if (problem is not null)
        {
            _log.Report(_log.LineOf(reader.TokenStartIndex), $"{field} {problem}");
        }
    }

    private void Reference(ref Utf8JsonReader reader, Type target, string field)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            _log.Wrong(ref reader, field, "text");
            return;
        }

        _references.Add(new ContentReference(target, reader.GetString()!, _log.PlaceOf(reader.TokenStartIndex), field));
    }
}

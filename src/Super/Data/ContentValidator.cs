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

    private readonly string _path;
    private readonly ReadOnlyMemory<byte> _json;
    private readonly JsonSerializerOptions _options;
    private readonly IContentFiles _files;
    private readonly List<string> _problems;
    private readonly List<int> _lineStarts = [0];
    private readonly int _problemsBefore;

    public ContentValidator(string path, ReadOnlyMemory<byte> json, JsonSerializerOptions options, IContentFiles files, List<string> problems)
    {
        _path = path;
        _json = json;
        _options = options;
        _files = files;
        _problems = problems;
        _problemsBefore = problems.Count;

        var bytes = json.Span;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                _lineStarts.Add(i + 1);
            }
        }
    }

    /// <summary>The record's id, if the file gave one.</summary>
    public string? Id { get; private set; }

    /// <summary>The line the id is on, or the first line if there isn't one.</summary>
    public int IdLine { get; private set; } = 1;

    public bool Passed => _problems.Count == _problemsBefore;

    /// <summary>Strips the serializer's position off its message, since the place is already named.</summary>
    public static string Plain(string message)
    {
        int position = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        int path = message.IndexOf(" Path:", StringComparison.Ordinal);
        int end = path >= 0 ? path : position;
        return end >= 0 ? message[..end].TrimEnd(' ', '.', '|') : message;
    }

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
            Report((int)(error.LineNumber ?? 0) + 1, $"this is not valid JSON: {Plain(error.Message)}");
        }
    }

    public void Report(int line, string problem) => _problems.Add($"{_path}:{line}: {problem}");

    private int Line(long offset)
    {
        int found = _lineStarts.BinarySearch((int)offset);
        return found >= 0 ? found + 1 : ~found;
    }

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
                Wrong(ref reader, field, Word(type));
            }

            return;
        }

        if (type == typeof(ModelRef))
        {
            Model(ref reader, field);
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
                Leaf(ref reader, type, field);
                break;
        }
    }

    private void Fields(ref Utf8JsonReader reader, JsonTypeInfo info, string field, bool top)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            Wrong(ref reader, field, "an object");
            return;
        }

        int opened = Line(reader.TokenStartIndex);
        var seen = new HashSet<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            int line = Line(reader.TokenStartIndex);
            reader.Read();
            var property = info.Properties.FirstOrDefault(candidate => candidate.Name == name);
            if (property is null)
            {
                if (!(top && name == SchemaField))
                {
                    Report(line, $"{Join(field, name)} is not a field of {info.Type.Name}");
                }

                reader.Skip();
                continue;
            }

            seen.Add(name);
            if (top && name == IdField && reader.TokenType == JsonTokenType.String)
            {
                Id = reader.GetString();
                IdLine = line;
            }

            Value(ref reader, property.PropertyType, Join(field, name), property.IsSetNullable);
        }

        foreach (var property in info.Properties)
        {
            bool required = property.IsRequired || property.AssociatedParameter is { HasDefaultValue: false, IsMemberInitializer: false };
            if (required && !seen.Contains(property.Name))
            {
                Report(opened, $"{Join(field, property.Name)} is missing");
            }
        }
    }

    private void Elements(ref Utf8JsonReader reader, JsonTypeInfo info, string field)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            Wrong(ref reader, field, "a list");
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
            Wrong(ref reader, field, "an object");
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string key = reader.GetString()!;
            reader.Read();
            Value(ref reader, info.ElementType!, Join(field, key), nullable: false);
        }
    }

    private void Leaf(ref Utf8JsonReader reader, Type type, string field)
    {
        if (type == typeof(string))
        {
            Expect(ref reader, JsonTokenType.String, field, "text");
        }
        else if (type == typeof(bool))
        {
            if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
            {
                Wrong(ref reader, field, "true or false");
            }
        }
        else if (type.IsEnum)
        {
            Choice(ref reader, type, field);
        }
        else if (IsWhole(type))
        {
            Whole(ref reader, type, field);
        }
        else if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            Expect(ref reader, JsonTokenType.Number, field, "a number");
        }
        else
        {
            // Anything else has a converter of its own, which reports its own mistakes.
            reader.Skip();
        }
    }

    private void Model(ref Utf8JsonReader reader, string field)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            Wrong(ref reader, field, "text");
            return;
        }

        string path = reader.GetString()!;
        if (!_files.HasModel(path))
        {
            Report(Line(reader.TokenStartIndex), $"{field} {path} does not exist");
        }
    }

    private void Choice(ref Utf8JsonReader reader, Type type, string field)
    {
        string[] names = Enum.GetNames(type).Select(name => _options.PropertyNamingPolicy!.ConvertName(name)).ToArray();
        if (reader.TokenType != JsonTokenType.String || !names.Contains(reader.GetString()))
        {
            Wrong(ref reader, field, $"one of {string.Join(", ", names)}");
        }
    }

    private void Whole(ref Utf8JsonReader reader, Type type, string field)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            Wrong(ref reader, field, "a number");
            return;
        }

        bool fits = Type.GetTypeCode(type) switch
        {
            TypeCode.Byte => reader.TryGetByte(out _),
            TypeCode.SByte => reader.TryGetSByte(out _),
            TypeCode.Int16 => reader.TryGetInt16(out _),
            TypeCode.UInt16 => reader.TryGetUInt16(out _),
            TypeCode.Int32 => reader.TryGetInt32(out _),
            TypeCode.UInt32 => reader.TryGetUInt32(out _),
            TypeCode.Int64 => reader.TryGetInt64(out _),
            _ => reader.TryGetUInt64(out _),
        };
        if (!fits)
        {
            bool fraction = reader.TryGetDouble(out double value) && value != Math.Floor(value);
            Wrong(ref reader, field, fraction ? "a whole number" : $"small enough for {type.Name}");
        }
    }

    private void Expect(ref Utf8JsonReader reader, JsonTokenType token, string field, string word)
    {
        if (reader.TokenType != token)
        {
            Wrong(ref reader, field, word);
        }
    }

    private void Wrong(ref Utf8JsonReader reader, string field, string word)
    {
        string what = field.Length > 0 ? field : "this file";
        Report(Line(reader.TokenStartIndex), $"{what} must be {word}");
        reader.Skip();
    }

    private static string Join(string field, string name) => field.Length > 0 ? $"{field}.{name}" : name;

    private static bool IsWhole(Type type) => Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte
        or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64;

    private static string Word(Type type)
    {
        if (type == typeof(string) || type == typeof(ModelRef))
        {
            return "text";
        }

        if (type == typeof(bool))
        {
            return "true or false";
        }

        return IsWhole(type) || type == typeof(float) || type == typeof(double) || type == typeof(decimal)
            ? "a number"
            : "filled in";
    }
}

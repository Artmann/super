using System.Numerics;
using System.Text.Json;

namespace Super;

/// <summary>
/// Checks the plain values of a JSON file against the types they're read into: text, true or false,
/// numbers that fit, enum names and vectors. Each mistake goes into a <see cref="JsonProblemLog"/>.
/// </summary>
internal static class JsonLeafCheck
{
    private const string VectorWord = "a list of three numbers, such as [1, 0, 2]";

    /// <summary>
    /// Checks a value with no fields of its own. A type with a converter of its own is skipped,
    /// since the converter reports its own mistakes.
    /// </summary>
    public static void Leaf(ref Utf8JsonReader reader, Type type, string field, JsonProblemLog log, JsonNamingPolicy naming)
    {
        if (type == typeof(string))
        {
            Expect(ref reader, JsonTokenType.String, field, "text", log);
        }
        else if (type == typeof(bool))
        {
            if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
            {
                log.Wrong(ref reader, field, "true or false");
            }
        }
        else if (type.IsEnum)
        {
            OneOf(ref reader, type, field, log, naming);
        }
        else if (IsWhole(type))
        {
            Whole(ref reader, type, field, log);
        }
        else if (IsFraction(type))
        {
            Expect(ref reader, JsonTokenType.Number, field, "a number", log);
        }
        else
        {
            reader.Skip();
        }
    }

    /// <summary>Checks a <see cref="Vector3"/>, which is a list of exactly three numbers.</summary>
    public static void ThreeNumbers(ref Utf8JsonReader reader, string field, JsonProblemLog log)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            log.Wrong(ref reader, field, VectorWord);
            return;
        }

        int line = log.LineOf(reader.TokenStartIndex);
        int count = 0;
        bool numbers = true;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            numbers &= reader.TokenType == JsonTokenType.Number;
            reader.Skip();
            count++;
        }

        if (!numbers || count != 3)
        {
            log.Report(line, $"{(field.Length > 0 ? field : "this file")} must be {VectorWord}");
        }
    }

    /// <summary>What a value of a plain type must be, for the message when it's missing.</summary>
    public static string Word(Type type)
    {
        if (type == typeof(string))
        {
            return "text";
        }

        if (type == typeof(bool))
        {
            return "true or false";
        }

        if (type == typeof(Vector3))
        {
            return VectorWord;
        }

        return IsWhole(type) || IsFraction(type) ? "a number" : "filled in";
    }

    private static void OneOf(ref Utf8JsonReader reader, Type type, string field, JsonProblemLog log, JsonNamingPolicy naming)
    {
        string[] names = Enum.GetNames(type).Select(naming.ConvertName).ToArray();
        if (reader.TokenType != JsonTokenType.String || !names.Contains(reader.GetString()))
        {
            log.Wrong(ref reader, field, $"one of {string.Join(", ", names)}");
        }
    }

    private static void Whole(ref Utf8JsonReader reader, Type type, string field, JsonProblemLog log)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            log.Wrong(ref reader, field, "a number");
            return;
        }

        if (!Fits(ref reader, Type.GetTypeCode(type)))
        {
            bool fraction = reader.TryGetDouble(out double value) && value != Math.Floor(value);
            log.Wrong(ref reader, field, fraction ? "a whole number" : $"small enough for {type.Name}");
        }
    }

    /// <summary>Whether the number the reader is on fits a whole number type.</summary>
    private static bool Fits(ref Utf8JsonReader reader, TypeCode code) => code switch
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

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType token, string field, string word, JsonProblemLog log)
    {
        if (reader.TokenType != token)
        {
            log.Wrong(ref reader, field, word);
        }
    }

    private static bool IsWhole(Type type) => Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte
        or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64;

    private static bool IsFraction(Type type) => type == typeof(float) || type == typeof(double) || type == typeof(decimal);
}

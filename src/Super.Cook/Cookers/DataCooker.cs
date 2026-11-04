using System.Text.Json;

namespace Super.Cook;

/// <summary>Checks that JSON content parses, then keeps its bytes as they are.</summary>
public sealed class DataCooker : ICooker
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Id => "data";

    public int Version => 1;

    public IReadOnlyList<string> Extensions { get; } = [".json"];

    public void Cook(CookInput input, CookOutput output)
    {
        try
        {
            using var document = JsonDocument.Parse(input.Bytes, Lenient);
        }
        catch (JsonException exception)
        {
            output.Error(exception.Message, exception.LineNumber is { } n ? (int)n + 1 : null);
            return;
        }

        output.Add(ContentKind.Data, input.Bytes);
    }
}

/// <summary>The cookers that ship with Super.</summary>
public static class BuiltInCookers
{
    public static IReadOnlyList<ICooker> All { get; } = [new DataCooker()];
}

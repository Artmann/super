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
            // Editors on Windows may start a file with a byte order mark. The runtime skips it, so it isn't a mistake.
            var json = input.Bytes.AsMemory();
            if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                json = json[3..];
            }

            using var document = JsonDocument.Parse(json, Lenient);
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
    public static IReadOnlyList<ICooker> All { get; } = [new DataCooker(), new TextureCooker(), new SoundCooker(), new LightBakeCooker()];
}

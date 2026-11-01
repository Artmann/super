using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Super;

/// <summary>Describes a record for editors, so they can offer autocomplete and mark mistakes as you type.</summary>
internal static class ContentSchema
{
    private const string Folder = ".schemas";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" };

    public static string Text(Type type, JsonSerializerOptions options)
    {
        var schema = options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Describe,
        });
        return schema.ToJsonString(Indented) + "\n";
    }

    /// <summary>Writes one schema, leaving the file alone if nothing changed so it doesn't show up as an edit.</summary>
    public static void Write(string root, string folder, Type type, JsonSerializerOptions options)
    {
        string path = Path.Combine(root, Folder, folder.Replace('/', '-') + ".schema.json");
        string text = Text(type, options);
        if (File.Exists(path) && File.ReadAllText(path) == text)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static JsonNode Describe(JsonSchemaExporterContext context, JsonNode node)
    {
        var type = context.TypeInfo.Type;
        if (type == typeof(ModelRef) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Ref<>)))
        {
            return new JsonObject { ["type"] = "string" };
        }

        if (node is JsonObject described && context.TypeInfo.Kind == JsonTypeInfoKind.Object)
        {
            described["additionalProperties"] = false;
            if (context.Path.IsEmpty)
            {
                // Editors reject comments in .json files unless a schema says otherwise.
                described["allowComments"] = true;
                described["allowTrailingCommas"] = true;
                described["properties"]!.AsObject()["$schema"] = new JsonObject { ["type"] = "string" };
            }
        }

        return node;
    }
}

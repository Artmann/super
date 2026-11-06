using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Super.Cook.Materials;

/// <summary>A <c>*.material.json</c> file: the graph it sets values for, and the values.</summary>
internal sealed class Variant
{
    private const string GraphKey = "graph";

    private readonly List<(string Name, JsonElement Value)> _values = [];

    private Variant(string graph)
    {
        Graph = graph;
    }

    /// <summary>The path of the graph file, relative to the variant.</summary>
    public string Graph { get; }

    /// <returns>The variant, or null after reporting what is wrong with the file to <paramref name="output"/>.</returns>
    public static Variant? Read(string name, byte[] bytes, CookOutput output)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException exception)
        {
            output.Error($"{name}: {exception.Message}");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                output.Error($"{name}: a variant is a JSON object, such as {{ \"graph\": \"bricks.material.cs\" }}.");
                return null;
            }

            string? graph = null;
            var values = new List<(string, JsonElement)>();
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, GraphKey, StringComparison.OrdinalIgnoreCase))
                {
                    graph = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
                }
                else
                {
                    // The document is disposed below, so the value must outlive it.
                    values.Add((property.Name, property.Value.Clone()));
                }
            }

            if (string.IsNullOrEmpty(graph))
            {
                output.Error($"{name} needs \"graph\", the .material.cs file it sets values for.");
                return null;
            }

            if (!graph.EndsWith(".material.cs", StringComparison.OrdinalIgnoreCase))
            {
                output.Error($"{name}: graph must be a .material.cs file, not {graph}.");
                return null;
            }

            var variant = new Variant(graph);
            variant._values.AddRange(values);
            return variant;
        }
    }

    /// <summary>Sets the values on <paramref name="graph"/>. Reports each one that can't be set.</summary>
    /// <returns>Whether every value was set.</returns>
    public bool Apply(object graph, string name, CookOutput output)
    {
        var type = graph.GetType();
        var parameters = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(IsParameter)
            .ToDictionary(property => property.Name.ToLowerInvariant(), StringComparer.Ordinal);

        bool ok = true;
        foreach (var (key, value) in _values)
        {
            string lower = key.ToLowerInvariant();
            if (!parameters.TryGetValue(lower, out var property))
            {
                string[] known = [.. parameters.Keys.Order(StringComparer.Ordinal)];
                output.Error($"{name}: {type.Name} has no parameter called {key}. It has {string.Join(", ", known)}.");
                ok = false;
                continue;
            }

            if (!TryConvert(property.PropertyType, value, out object? converted, out string wanted))
            {
                output.Error($"{name}: {lower} is {wanted}, not {value.GetRawText()}.");
                ok = false;
                continue;
            }

            property.SetValue(graph, converted);
        }

        return ok;
    }

    private static bool IsParameter(PropertyInfo property)
    {
        if (property.PropertyType != typeof(float) && property.PropertyType != typeof(int)
            && property.PropertyType != typeof(bool) && property.PropertyType != typeof(string))
        {
            return false;
        }

        var setter = property.SetMethod;
        return setter is { IsPublic: true }
            && setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
    }

    private static bool TryConvert(Type type, JsonElement value, out object? converted, out string wanted)
    {
        converted = null;
        if (type == typeof(float))
        {
            wanted = "a number";
            if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float number) && float.IsFinite(number))
            {
                converted = number;
            }
        }
        else if (type == typeof(int))
        {
            wanted = "a whole number";
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int whole))
            {
                converted = whole;
            }
        }
        else if (type == typeof(bool))
        {
            wanted = "true or false";
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                converted = value.GetBoolean();
            }
        }
        else
        {
            wanted = "text";
            if (value.ValueKind == JsonValueKind.String)
            {
                converted = value.GetString();
            }
        }

        return converted is not null;
    }
}

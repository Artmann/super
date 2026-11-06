using System.Text.Json;
using Rendition;
using Super.Materials;

namespace Super.Cook.Materials;

/// <summary>Turns a worked out material into its cooked parts: four textures and its width.</summary>
internal static class MaterialWriter
{
    public static void Write(MaterialImages images, string name, CookOutput output)
    {
        int size = images.Size;
        int pixels = size * size;

        // The images may share arrays, so they're only ever read.
        var color = new Texture(name, "color", pixels);
        var normal = new Texture(name, "normal", pixels);
        var surface = new Texture(name, "surface", pixels);
        var height = new Texture(name, "height", pixels);
        for (int i = 0; i < pixels; i++)
        {
            color.Set(i, images.Color[i * 3], images.Color[i * 3 + 1], images.Color[i * 3 + 2], 1f);
            normal.Set(i, images.Normal[i * 3] * 0.5f + 0.5f, images.Normal[i * 3 + 1] * 0.5f + 0.5f, images.Normal[i * 3 + 2] * 0.5f + 0.5f, 1f);
            surface.Set(i, images.Metallic[i], images.Roughness[i], 0f, images.Opacity[i]);
            height.Set(i, images.Height[i], images.Height[i], images.Height[i], 1f);
        }

        color.Add(output, size, TextureKind.Color, normalMap: false);
        normal.Add(output, size, TextureKind.Linear, normalMap: true);
        surface.Add(output, size, TextureKind.Linear, normalMap: false);
        height.Add(output, size, TextureKind.Linear, normalMap: false);

        using var json = new MemoryStream();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            writer.WriteNumber("width", images.Width);
            writer.WriteEndObject();
        }

        output.AddPart("material", ContentKind.Data, json.ToArray());
    }

    /// <summary>The RGBA8 pixels of one part, and whether any value wasn't a number.</summary>
    private sealed class Texture(string name, string part, int pixels)
    {
        private readonly byte[] _bytes = new byte[pixels * 4];
        private bool _hasNaN;

        public void Set(int pixel, float r, float g, float b, float a)
        {
            _bytes[pixel * 4] = ToByte(r);
            _bytes[pixel * 4 + 1] = ToByte(g);
            _bytes[pixel * 4 + 2] = ToByte(b);
            _bytes[pixel * 4 + 3] = ToByte(a);
        }

        public void Add(CookOutput output, int size, TextureKind kind, bool normalMap)
        {
            if (_hasNaN)
            {
                output.Warning($"{name}: {part} has pixels that aren't numbers. They were written as 0.");
            }

            output.AddPart(part, ContentKind.Texture, new CookedTexture(size, size, kind, normalMap, _bytes).Write());
        }

        private byte ToByte(float value)
        {
            if (float.IsNaN(value))
            {
                _hasNaN = true;
                return 0;
            }

            return (byte)Math.Clamp((int)MathF.Round(value * 255f, MidpointRounding.AwayFromZero), 0, 255);
        }
    }
}

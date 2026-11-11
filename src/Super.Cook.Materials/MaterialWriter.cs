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
        var pixels = MaterialPixels.Pack(images);
        foreach (string part in pixels.NotNumbers)
        {
            output.Warning($"{name}: {part} has pixels that aren't numbers. They were written as 0.");
        }

        output.AddPart("color", ContentKind.Texture, new CookedTexture(size, size, TextureKind.Color, false, pixels.Color).Write());
        output.AddPart("normal", ContentKind.Texture, new CookedTexture(size, size, TextureKind.Linear, true, pixels.Normal).Write());
        output.AddPart("surface", ContentKind.Texture, new CookedTexture(size, size, TextureKind.Linear, false, pixels.Surface).Write());
        output.AddPart("height", ContentKind.Texture, new CookedTexture(size, size, TextureKind.Linear, false, pixels.Height).Write());

        using var json = new MemoryStream();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            writer.WriteNumber("width", images.Width);
            writer.WriteEndObject();
        }

        output.AddPart("material", ContentKind.Data, json.ToArray());
    }
}

/// <summary>
/// The RGBA8 pixels of a worked out material's four textures, row by row, as the material cooker writes them and a
/// model with a graph on one of its materials uses them.
/// </summary>
/// <param name="Color">The colour, sRGB, with an alpha of 1.</param>
/// <param name="Normal">The normal map, each axis mapped from -1..1 to 0..1.</param>
/// <param name="Surface">Metalness in red, roughness in green, 0 in blue and opacity in alpha.</param>
/// <param name="Height">The height in red, green and blue.</param>
/// <param name="NotNumbers">The parts, of color, normal, surface and height, that had values that weren't numbers. Those were written as 0.</param>
public sealed record MaterialPixels(byte[] Color, byte[] Normal, byte[] Surface, byte[] Height, IReadOnlyList<string> NotNumbers)
{
    public static MaterialPixels Pack(MaterialImages images)
    {
        int pixels = images.Size * images.Size;

        // The images may share arrays, so they're only ever read.
        var color = new Texture("color", pixels);
        var normal = new Texture("normal", pixels);
        var surface = new Texture("surface", pixels);
        var height = new Texture("height", pixels);
        for (int i = 0; i < pixels; i++)
        {
            color.Set(i, images.Color[i * 3], images.Color[i * 3 + 1], images.Color[i * 3 + 2], 1f);
            normal.Set(i, images.Normal[i * 3] * 0.5f + 0.5f, images.Normal[i * 3 + 1] * 0.5f + 0.5f, images.Normal[i * 3 + 2] * 0.5f + 0.5f, 1f);
            surface.Set(i, images.Metallic[i], images.Roughness[i], 0f, images.Opacity[i]);
            height.Set(i, images.Height[i], images.Height[i], images.Height[i], 1f);
        }

        string[] notNumbers = [.. new[] { color, normal, surface, height }.Where(texture => texture.HasNaN).Select(texture => texture.Part)];
        return new MaterialPixels(color.Bytes, normal.Bytes, surface.Bytes, height.Bytes, notNumbers);
    }

    /// <summary>The RGBA8 pixels of one part, and whether any value wasn't a number.</summary>
    private sealed class Texture(string part, int pixels)
    {
        public string Part { get; } = part;

        public byte[] Bytes { get; } = new byte[pixels * 4];

        public bool HasNaN { get; private set; }

        public void Set(int pixel, float r, float g, float b, float a)
        {
            Bytes[pixel * 4] = ToByte(r);
            Bytes[pixel * 4 + 1] = ToByte(g);
            Bytes[pixel * 4 + 2] = ToByte(b);
            Bytes[pixel * 4 + 3] = ToByte(a);
        }

        private byte ToByte(float value)
        {
            if (float.IsNaN(value))
            {
                HasNaN = true;
                return 0;
            }

            return (byte)Math.Clamp((int)MathF.Round(value * 255f, MidpointRounding.AwayFromZero), 0, 255);
        }
    }
}

/// <summary>A graph's images packed into RGBA8 pixels, as <see cref="MaterialGraphs.RunPacked"/> gives them.</summary>
/// <param name="Size">The pixels across, and down.</param>
/// <param name="Width">How many metres the images span, as <see cref="Super.Materials.MaterialImages.Width"/>.</param>
/// <param name="HeightDepth">How many metres a height of 1 stands above 0, as <see cref="Super.Materials.MaterialImages.HeightDepth"/>.</param>
public sealed record PackedImages(int Size, float Width, float HeightDepth, MaterialPixels Pixels);

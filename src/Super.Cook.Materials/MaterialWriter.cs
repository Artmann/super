using System.Text.Json;
using Rendition;
using Super.Materials;

namespace Super.Cook.Materials;

/// <summary>Turns a worked out material into its cooked parts: four textures and its width.</summary>
internal static class MaterialWriter
{
    /// <summary>
    /// Writes the parts. With <paramref name="compress"/>, each texture is block-compressed with its whole mip chain,
    /// kept in <paramref name="compress"/>'s step cache: the colour and surface as BC7, the normal map as BC5 and the
    /// height, red only, as BC4. Without it they're RGBA8, full size only, and the game builds their mips.
    /// </summary>
    public static void Write(MaterialImages images, string name, CookOutput output, CookInput? compress = null)
    {
        Write(new PackedImages(images.Size, images.Width, images.HeightDepth, MaterialPixels.Pack(images)), name, output, compress);
    }

    /// <summary>
    /// Writes images already packed, such as a graph run another source shared, compressed as the other overload says.
    /// The packed pixels may be shared with other sources, so they're only read.
    /// </summary>
    public static void Write(PackedImages images, string name, CookOutput output, CookInput? compress = null)
    {
        int size = images.Size;
        var pixels = images.Pixels;
        foreach (string part in pixels.NotNumbers)
        {
            output.Warning($"{name}: {part} has pixels that aren't numbers. They were written as 0.");
        }

        (string Part, TextureKind Kind, bool NormalMap, TextureFormat Format, byte[] Pixels)[] parts =
        [
            ("color", TextureKind.Color, false, TextureFormat.Bc7, pixels.Color),
            ("normal", TextureKind.Linear, true, TextureFormat.Bc5, pixels.Normal),
            ("surface", TextureKind.Linear, false, TextureFormat.Bc7, pixels.Surface),
            ("height", TextureKind.Linear, false, TextureFormat.Bc4, pixels.Height),
        ];

        var cooked = new byte[parts.Length][];
        if (compress is null)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                cooked[i] = new CookedTexture(size, size, parts[i].Kind, parts[i].NormalMap, parts[i].Pixels).Write();
            }
        }
        else
        {
            Parallel.For(0, parts.Length, i =>
            {
                var part = parts[i];
                var mips = TextureData.FromPixels(size, size, part.Kind, part.Pixels, part.NormalMap);
                cooked[i] = TextureCompression.CompressCooked(mips, part.Format, part.NormalMap, compress);
            });
        }

        for (int i = 0; i < parts.Length; i++)
        {
            output.AddPart(parts[i].Part, ContentKind.Texture, cooked[i]);
        }

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
/// <param name="Normal">
/// The normal map, each axis mapped from -1..1 to 0..1. Compressed to BC5, the cooked part keeps only red and green.
/// </param>
/// <param name="Surface">Metalness in red, roughness in green, 0 in blue and opacity in alpha.</param>
/// <param name="Height">The height in red, green and blue. Compressed to BC4, the cooked part keeps only red.</param>
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

using Rendition;
using StbImageSharp;

namespace Super.Cook;

public sealed record TextureSettings
{
    /// <summary>Non-colour data, kept as it is instead of read as sRGB.</summary>
    public bool Linear { get; init; }

    /// <summary>A normal map. Always linear.</summary>
    public bool NormalMap { get; init; }

    /// <summary>
    /// Block-compresses the texture with its whole mip chain, as BC5 for a normal map and BC7 for
    /// anything else, so it takes a quarter of the memory or less. Off keeps the decoded RGBA8
    /// pixels exactly, and the game builds the mips as it loads them.
    /// </summary>
    public bool CompressTextures { get; init; } = true;
}

/// <summary>
/// Decodes PNG, JPEG and TGA files into the cooked texture format: block-compressed with every mip
/// unless <see cref="TextureSettings.CompressTextures"/> is off, then RGBA8 pixels.
/// </summary>
public sealed class TextureCooker : ICooker
{
    public string Id => "texture";

    // 2: block-compressed with mips. The BC7 encoder's version is folded in, so a change to the bytes it writes cooks every texture again.
    public int Version { get; } = unchecked(2 * 31 + Bc7Encoder.Version);

    public IReadOnlyList<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".tga"];

    public void Cook(CookInput input, CookOutput output)
    {
        var settings = input.SettingsAs<TextureSettings>();

        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(input.Bytes, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            output.Error($"{input.Name} is not an image this can read");
            return;
        }

        var kind = settings.Linear || settings.NormalMap ? TextureKind.Linear : TextureKind.Color;
        if (!settings.CompressTextures)
        {
            output.Add(ContentKind.Texture, new CookedTexture(image.Width, image.Height, kind, settings.NormalMap, image.Data).Write());
            return;
        }

        byte[] cooked;
        using (input.Time("compress texture"))
        {
            var mips = TextureData.FromPixels(image.Width, image.Height, kind, image.Data, settings.NormalMap);
            cooked = TextureCompression.CompressCooked(mips, settings.NormalMap ? TextureFormat.Bc5 : TextureFormat.Bc7, settings.NormalMap, input);
        }

        output.Add(ContentKind.Texture, cooked);
    }
}

using Rendition;
using StbImageSharp;

namespace Super.Cook;

public sealed record TextureSettings
{
    /// <summary>Non-colour data, kept as it is instead of read as sRGB.</summary>
    public bool Linear { get; init; }

    /// <summary>A normal map. Always linear.</summary>
    public bool NormalMap { get; init; }
}

/// <summary>Decodes PNG, JPEG and TGA files into RGBA8 pixels in the cooked texture format.</summary>
public sealed class TextureCooker : ICooker
{
    public string Id => "texture";

    public int Version => 1;

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
        output.Add(ContentKind.Texture, new CookedTexture(image.Width, image.Height, kind, settings.NormalMap, image.Data).Write());
    }
}

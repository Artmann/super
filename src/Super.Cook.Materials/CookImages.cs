using Super.Materials;
using StbImageSharp;

namespace Super.Cook.Materials;

/// <summary>Reads the pictures a graph asks for through <see cref="CookInput.Read"/>, so the cook tracks them.</summary>
internal sealed class CookImages(CookInput input) : IImageSource
{
    public DecodedImage Read(string path)
    {
        byte[] bytes = input.Read(path);

        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException($"{path} is not an image this can read.", exception);
        }

        float[] rgba = new float[image.Data.Length];
        for (int i = 0; i < rgba.Length; i++)
        {
            rgba[i] = image.Data[i] / 255f;
        }

        return new DecodedImage(image.Width, image.Height, rgba);
    }
}

using Super.Materials;
using StbImageSharp;

namespace Super.Cook.Materials;

/// <summary>
/// Reads the pictures a graph asks for through <see cref="CookInput.Read"/>, so the cook tracks them. A path is relative
/// to <paramref name="folder"/>, the folder of the graph or variant from the source's folder, such as <c>../materials/</c>.
/// </summary>
internal sealed class CookImages(CookInput input, string folder) : IImageSource
{
    public DecodedImage Read(string path)
    {
        byte[] bytes = input.Read(folder + path);

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

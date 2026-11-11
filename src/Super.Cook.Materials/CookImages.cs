using System.Security.Cryptography;
using Super.Materials;
using StbImageSharp;

namespace Super.Cook.Materials;

/// <summary>
/// Reads the pictures a graph asks for through <see cref="CookInput.Read"/>, so the cook tracks them. A path is relative
/// to <paramref name="folder"/>, the folder of the graph or variant from the source's folder, such as <c>../materials/</c>.
/// When <paramref name="reads"/> is given, each picture's path and the SHA-256 of its bytes is added to it, once.
/// </summary>
internal sealed class CookImages(CookInput input, string folder, List<(string Path, byte[] Hash)>? reads = null) : IImageSource
{
    public DecodedImage Read(string path)
    {
        byte[] bytes = input.Read(folder + path);
        if (reads is not null)
        {
            byte[] hash = SHA256.HashData(bytes);
            lock (reads)
            {
                if (!reads.Any(read => read.Path == path))
                {
                    reads.Add((path, hash));
                }
            }
        }

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

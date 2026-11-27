using Super.Ui;

namespace Super;

/// <summary>The pictures a game shows in its interface. Reached through the content's <c>UiImages</c>.</summary>
public sealed class ContentUiImages
{
    private readonly ContentItems _items;

    internal ContentUiImages(ContentItems items) => _items = items;

    /// <summary>
    /// Names an image, by the name of its cooked texture. Naming it again gives the same one.
    /// Its size comes from the cooked header, so layout never waits for it. A name that isn't
    /// a cooked texture stops here with a <see cref="ContentException"/>. It's a catalog's
    /// mistake, not a drawing one.
    /// </summary>
    public GameImage Image(string name)
    {
        if (_items.TryNamed(name, out GameImage known))
        {
            return known;
        }

        var files = _items.Files;
        ContentNames.Require(files, name, ContentKind.Texture);
        byte[] cooked = CookedBytes.Read(files, name, "image");
        var (width, height) = CookedTexture.ReadSize(cooked, name);

        // The image holds its own place, so it's built for the place it's about to take.
        var (index, generation) = _items.Slots.Next;
        var image = new GameImage(name, width, height, cooked, index) { Generation = generation };
        _items.AddReusable(image);
        _items.Remember(name, image);
        return image;
    }

    /// <summary>
    /// Hands over an image painted in code, to show in the interface like a cooked one. Each
    /// call makes a new one, so a picture that changes is painted again and the old one let go
    /// with <see cref="Unload(GameImage)"/>. <paramref name="pixels"/> are rows from the top,
    /// four bytes each (red, green, blue, alpha), in sRGB with straight alpha, and are kept as
    /// they are, so don't change them afterwards.
    /// </summary>
    public GameImage Image(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException($"A {width} by {height} image takes {width * height * 4} bytes, four a pixel, not {pixels.Length}.", nameof(pixels));
        }

        var (index, generation) = _items.Slots.Next;
        var image = new GameImage(width, height, pixels, index) { Generation = generation };
        _items.AddReusable(image);
        return image;
    }

    /// <summary>
    /// Lets go of an image. The window frees it, and showing it afterwards fails. Naming the
    /// same file again later makes a new one.
    /// </summary>
    public void Unload(GameImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ItemChecks.RequireNamed(_items, image.Index, nameof(image));
        ItemChecks.RequireLoaded(_items, image.Index, image.Generation, nameof(image));
        if (!ReferenceEquals(_items.Items[image.Index - 1], image))
        {
            throw new ArgumentException($"{nameof(image)} isn't one this content made.", nameof(image));
        }

        _items.MarkUnloaded(image.Index);
        if (image.Cooked is not null)
        {
            _items.ForgetName<GameImage>(image.Path);
        }
    }
}

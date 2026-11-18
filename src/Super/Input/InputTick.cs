using System.Numerics;

namespace Super;

/// <summary>
/// What the host sets around each tick and every part of the input service shares: whether
/// reads are allowed, and the size of the screen.
/// </summary>
internal sealed class InputTick
{
    private const string NotReadable = "Input can only be read in Update. Draw and Ui describe state, so they can't look at input.";

    /// <summary>Whether reads are allowed right now. The host turns it on for Update only.</summary>
    public bool Readable { get; set; }

    public Vector2 ScreenSize { get; set; }

    public void Check()
    {
        if (!Readable)
        {
            throw new InvalidOperationException(NotReadable);
        }
    }

    public T Read<T>(T value)
    {
        Check();
        return value;
    }
}

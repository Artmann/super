namespace Super;

/// <summary>A group of sounds with one volume. Master scales all the others.</summary>
public enum Channel
{
    Master,
    Music,
    Ambience,
    Effects,
    Interface,

    /// <summary>Speech. Music and Ambience dip under it, unless <see cref="Audio.Duck"/> says otherwise.</summary>
    Dialogue,
}

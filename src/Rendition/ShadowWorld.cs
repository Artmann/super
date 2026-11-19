namespace Rendition;

/// <summary>
/// Counts the changes that can change any point or spot light's shadow tile, such as a mesh, texture or material going
/// or the shaders being built again, so <see cref="ShadowTileCache"/> forgets every kept tile when one happens.
/// </summary>
internal sealed class ShadowWorld
{
    /// <summary>How many such changes there have been.</summary>
    public ulong Version { get; private set; }

    /// <summary>Notes a change that can change any tile.</summary>
    public void Changed() => Version++;
}

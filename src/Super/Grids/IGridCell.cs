namespace Super;

/// <summary>
/// What <see cref="Cell"/> and <see cref="Hex"/> share, so footprints, <see cref="GridMap{TCell, T}"/> and flood fills
/// work on squares and hexes alike. A game uses the two types and doesn't need to implement this.
/// </summary>
public interface IGridCell<TSelf> : IEquatable<TSelf>
    where TSelf : struct, IGridCell<TSelf>
{
    /// <summary>The steps to the cells that share a side, going round. There are as many as a cell has corners.</summary>
    static abstract ReadOnlySpan<TSelf> Sides { get; }

    static abstract TSelf operator +(TSelf a, TSelf b);

    static abstract TSelf operator -(TSelf a, TSelf b);

    /// <summary>Turns a cell round another, by quarter turns for squares and sixth turns for hexes.</summary>
    static abstract TSelf Rotate(TSelf cell, TSelf around, int turns);

    /// <summary>The cell's two coordinates, for storing it.</summary>
    internal (int A, int B) Pair { get; }

    /// <summary>The cell with these two coordinates.</summary>
    internal static abstract TSelf FromPair(int a, int b);
}

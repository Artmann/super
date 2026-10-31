namespace Reef;

/// <summary>One kind of fish and how it behaves.</summary>
internal sealed record Species(
    string Model,
    float Length,
    int Count,
    int Schools,
    float MinSpeed,
    float MaxSpeed,
    float Personal, // Fish closer than this push apart, in metres.
    float Sight); // How far a fish sees its school, in metres. At most NeighbourCell.

/// <summary>How big the reef is. Tests use a smaller one.</summary>
internal class ReefSettings
{
    public virtual IReadOnlyList<Species> Species { get; } =
    [
        new("reef/tilapia.rmodel", 0.5f, 6000, 16, 1.6f, 3.2f, 0.7f, 2.5f),
        new("reef/tuna.rmodel", 1.8f, 240, 3, 4.5f, 7f, 2.5f, 5f),
    ];

    public virtual int CoralClusters => 26;

    /// <summary>Fish stay within this distance of the centre, sideways.</summary>
    public float Radius => 55f;

    public float SurfaceHeight => 30f;
}

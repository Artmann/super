using Super;

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
        new("reef/tilapia/tilapia.glb", 0.5f, 6000, 16, 1.6f, 3.2f, 0.7f, 2.5f),
        new("reef/tuna/tuna.fbx", 1.8f, 240, 3, 4.5f, 7f, 2.5f, 5f),
    ];

    public virtual int CoralClusters => 26;

    public virtual string Coral => "reef/coral/coral.glb";

    public virtual string SoftCoral => "reef/softcoral/softcoral.fbx";

    /// <summary>Loads a model named above. Tests draw built-in shapes, so they don't need the licensed models.</summary>
    public virtual ModelRef Model(Content content, string name) => content.Model(name);

    /// <summary>The seabed's sand, a material graph. Tests use a plain surface, so they don't need the cook.</summary>
    public virtual Surface Sand(Content content) => content.Surface("materials/sand.material.cs") with { WorldVariation = true };

    /// <summary>The boulders' rock, a material graph, plain in tests like <see cref="Sand"/>.</summary>
    public virtual Surface Rock(Content content) => content.Surface("materials/reef-rock.material.cs") with { WorldVariation = true };

    /// <summary>Fish stay within this distance of the centre, sideways.</summary>
    public float Radius => 55f;

    public float SurfaceHeight => 30f;
}

using System.Numerics;
using Rendition;

namespace Super.Tests;

public class FoliageNormalsTests
{
    private static Vertex Leaf(float x, float y, float z) =>
        new(new Vector3(x, y, z), -Vector3.UnitY, new Vector4(Vector3.UnitX, -1f), Vector2.Zero);

    // A tall crown, 2 wide and 4 high, centred on (0, 2, 0).
    private static Vertex[] TallCrown(Vertex side) =>
    [
        Leaf(-1f, 0f, -1f),
        Leaf(1f, 4f, 1f),
        side,
    ];

    [Fact]
    public void A_leaf_on_the_side_of_a_tall_crown_faces_mostly_sideways()
    {
        var crown = TallCrown(Leaf(1f, 3f, 0f));

        FoliageNormals.PointOutOfCrown(crown);

        // Worked by hand: radii are (1, 2, 1), so the egg's gradient at (1, 1, 0) from
        // the centre is (1/1, 1/4, 0), which normalizes to (0.9701, 0.2425, 0).
        var normal = crown[2].Normal;
        Assert.Equal(0.9701f, normal.X, 3);
        Assert.Equal(0.2425f, normal.Y, 3);
        Assert.Equal(0f, normal.Z, 3);
    }

    [Fact]
    public void A_leaf_at_the_top_of_the_crown_faces_straight_up()
    {
        var crown = TallCrown(Leaf(0f, 4f, 0f));

        FoliageNormals.PointOutOfCrown(crown);

        Assert.Equal(Vector3.UnitY, crown[2].Normal);
    }

    [Fact]
    public void Tangents_turn_to_stay_at_right_angles_and_keep_their_handedness()
    {
        var crown = TallCrown(Leaf(1f, 3f, 0f));

        FoliageNormals.PointOutOfCrown(crown);

        foreach (var vertex in crown)
        {
            var tangent = new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
            Assert.Equal(0f, Vector3.Dot(tangent, vertex.Normal), 4);
            Assert.Equal(1f, tangent.Length(), 4);
            Assert.Equal(-1f, vertex.Tangent.W);
        }
    }

    [Fact]
    public void A_tangent_along_the_new_normal_is_replaced_by_one_at_right_angles()
    {
        // The side leaf's normal becomes +x, which is exactly where its tangent pointed.
        var crown = new[] { Leaf(-1f, 0f, 0f), Leaf(1f, 0f, 0f) };

        FoliageNormals.PointOutOfCrown(crown);

        var tangent = new Vector3(crown[1].Tangent.X, crown[1].Tangent.Y, crown[1].Tangent.Z);
        Assert.Equal(Vector3.UnitX, crown[1].Normal);
        Assert.Equal(0f, Vector3.Dot(tangent, crown[1].Normal), 4);
        Assert.Equal(1f, tangent.Length(), 4);
    }

    [Fact]
    public void A_flat_card_faces_along_its_thin_axis()
    {
        // Two corners lie at z = 0 and one leaf sits a millimetre in front of them.
        var card = new[]
        {
            Leaf(-1f, -1f, 0f), Leaf(1f, 1f, 0f), Leaf(0f, 0f, 0.001f),
        };

        FoliageNormals.PointOutOfCrown(card);

        Assert.True(card[2].Normal.Z > 0.99f, $"Expected a normal along +z, got {card[2].Normal}.");
        Assert.All(card, vertex => Assert.False(float.IsNaN(vertex.Normal.X)));
    }
}

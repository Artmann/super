using System.Numerics;
using Rendition;

namespace Super.Tests;

public class BakeAreaTests
{
    private static readonly BakedSpace Crypt = new("crypt", new Vector3(0f, 0f, 0f), new Vector3(10f, 5f, 10f), 1f);

    private static BakedChunk Cottage(Vector3 position) => new(
        "cottage",
        Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(position),
        new Vector3(-2f, 0f, -2f),
        new Vector3(2f, 3f, 2f));

    [Fact]
    public void Draw_overlapping_the_space_belongs_to_it()
    {
        var sphere = new BakeSphere(new Vector3(11f, 1f, 5f), 1.5f);

        Assert.Equal(BakeAreas.Space, BakeAreas.OwnerOfDraw(sphere, Crypt, [Cottage(new Vector3(20f, 0f, 0f))]));
    }

    [Fact]
    public void Draw_just_out_of_reach_of_the_space_is_not_in_it()
    {
        // The centre is 1.5 past the box's x = 10 face, and the radius stops short of it.
        var sphere = new BakeSphere(new Vector3(11.5f, 1f, 5f), 1.4f);

        Assert.Equal(BakeAreas.None, BakeAreas.OwnerOfDraw(sphere, Crypt, []));
    }

    [Fact]
    public void Draw_in_both_the_space_and_a_chunk_belongs_to_the_space()
    {
        var sphere = new BakeSphere(new Vector3(9f, 1f, 5f), 0.5f);

        Assert.Equal(BakeAreas.Space, BakeAreas.OwnerOfDraw(sphere, Crypt, [Cottage(new Vector3(9f, 0f, 5f))]));
    }

    [Fact]
    public void Draw_whose_centre_is_in_a_chunk_belongs_to_it()
    {
        var sphere = new BakeSphere(new Vector3(21f, 1f, 0.5f), 0.5f);

        Assert.Equal(0, BakeAreas.OwnerOfDraw(sphere, Crypt, [Cottage(new Vector3(20f, 0f, 0f))]));
    }

    [Fact]
    public void Draw_with_no_space_whose_centre_is_in_the_second_chunk_belongs_to_it()
    {
        var sphere = new BakeSphere(new Vector3(-29f, 1f, 1f), 0.5f);

        Assert.Equal(1, BakeAreas.OwnerOfDraw(
            sphere, null, [Cottage(new Vector3(20f, 0f, 0f)), Cottage(new Vector3(-30f, 0f, 0f))]));
    }

    [Fact]
    public void Draw_reaching_into_a_chunk_without_its_centre_is_not_in_it()
    {
        var sphere = new BakeSphere(new Vector3(23f, 1f, 0f), 2f);

        Assert.Equal(BakeAreas.None, BakeAreas.OwnerOfDraw(sphere, Crypt, [Cottage(new Vector3(20f, 0f, 0f))]));
    }

    [Fact]
    public void Draw_in_two_chunks_belongs_to_the_first()
    {
        var sphere = new BakeSphere(new Vector3(21f, 1f, 0f), 0.5f);

        Assert.Equal(0, BakeAreas.OwnerOfDraw(
            sphere, Crypt, [Cottage(new Vector3(20f, 0f, 0f)), Cottage(new Vector3(22f, 0f, 0f))]));
    }

    [Fact]
    public void Chunk_box_turns_with_the_chunk()
    {
        BakedChunk[] chunks = [Cottage(new Vector3(20f, 0f, 0f))];

        Assert.Equal(0, BakeAreas.OwnerOfLight(new Vector3(20f, 1f, 1.9f), null, chunks));
        Assert.Equal(0, BakeAreas.OwnerOfLight(new Vector3(21.9f, 1f, 0f), null, chunks));
        Assert.Equal(BakeAreas.None, BakeAreas.OwnerOfLight(new Vector3(20f, 1f, 2.5f), null, chunks));
    }

    [Fact]
    public void Lopsided_chunk_box_turns_the_right_way()
    {
        // Turning 90 degrees about Y takes the chunk's +X to the world's -Z, so a box on the chunk's +X side
        // lies on the world's -Z side.
        var chunk = new BakedChunk(
            "shed",
            Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(20f, 0f, 0f),
            new Vector3(0f, 0f, -1f),
            new Vector3(4f, 3f, 1f));

        Assert.Equal(0, BakeAreas.OwnerOfLight(new Vector3(20f, 1f, -3f), null, [chunk]));
        Assert.Equal(BakeAreas.None, BakeAreas.OwnerOfLight(new Vector3(20f, 1f, 3f), null, [chunk]));
        Assert.Equal(BakeAreas.None, BakeAreas.OwnerOfLight(new Vector3(23f, 1f, 0f), null, [chunk]));
    }

    [Fact]
    public void Chunk_far_from_the_origin_and_turned_37_degrees_holds_what_is_in_it()
    {
        // The chunk stands at (400, 0, -300), turned 37 degrees about Y, with cos 37 = 0.79863551 and
        // sin 37 = 0.60181502. A point (x, y, z) in chunk space is at
        // (400 + x cos + z sin, y, -300 - x sin + z cos) in the world.
        var chunk = new BakedChunk(
            "tower",
            Matrix4x4.CreateRotationY(37f * MathF.PI / 180f) * Matrix4x4.CreateTranslation(400f, 0f, -300f),
            new Vector3(0f, 0f, -1f),
            new Vector3(4f, 3f, 1f));

        // Chunk space (3, 1, 0): inside.
        Assert.Equal(0, BakeAreas.OwnerOfLight(new Vector3(402.39590653f, 1f, -301.80544507f), null, [chunk]));
        // Chunk space (3.9, 1, 0.9): inside, near a corner.
        Assert.Equal(0, BakeAreas.OwnerOfLight(new Vector3(403.65631201f, 1f, -301.62830662f), null, [chunk]));
        // Chunk space (3, 1, 1.5): half a metre past the box's +Z face.
        Assert.Equal(BakeAreas.None, BakeAreas.OwnerOfLight(new Vector3(403.29862906f, 1f, -300.6074918f), null, [chunk]));
        // Chunk space (-0.5, 1, 0): half a metre behind the box's -X face.
        Assert.Equal(BakeAreas.None, BakeAreas.OwnerOfLight(new Vector3(399.60068225f, 1f, -299.69909249f), null, [chunk]));
    }

    [Fact]
    public void Light_in_the_space_belongs_to_it()
    {
        Assert.Equal(BakeAreas.Space,
            BakeAreas.OwnerOfLight(new Vector3(5f, 4f, 5f), Crypt, [Cottage(new Vector3(5f, 0f, 5f))]));
    }

    [Fact]
    public void Light_in_a_chunk_belongs_to_it()
    {
        Assert.Equal(0, BakeAreas.OwnerOfLight(new Vector3(21f, 2f, -1f), Crypt, [Cottage(new Vector3(20f, 0f, 0f))]));
    }

    [Fact]
    public void Light_outside_everything_belongs_to_nothing()
    {
        // Just past the space's x = 10 face, where a draw with any reach would still count.
        Assert.Equal(BakeAreas.None,
            BakeAreas.OwnerOfLight(new Vector3(10.1f, 1f, 5f), Crypt, [Cottage(new Vector3(20f, 0f, 0f))]));
    }

    [Fact]
    public void Nothing_owns_anything_when_the_frame_names_no_area()
    {
        Assert.Equal(BakeAreas.None, BakeAreas.OwnerOfDraw(new BakeSphere(Vector3.Zero, 100f), null, []));
        Assert.Equal(BakeAreas.None, BakeAreas.OwnerOfLight(Vector3.Zero, null, []));
    }

    [Fact]
    public void Union_holds_every_part()
    {
        BakeSphere[] parts = [new(new Vector3(0f, 0f, 0f), 1f), new(new Vector3(4f, 0f, 0f), 1f)];

        var union = BakeAreas.Union(parts);

        Vector3[] extremes =
        [
            new(-1f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, -1f, 0f), new(0f, 0f, 1f), new(0f, 0f, -1f),
            new(3f, 0f, 0f), new(5f, 0f, 0f), new(4f, 1f, 0f), new(4f, -1f, 0f), new(4f, 0f, 1f), new(4f, 0f, -1f),
        ];
        foreach (var point in extremes)
        {
            Assert.True(Vector3.Distance(point, union.Center) <= union.Radius + 1e-5f,
                $"{point} is {Vector3.Distance(point, union.Center)} from {union.Center}, past {union.Radius}");
        }

        // The tightest sphere is centred at (2, 0, 0) with radius 3.
        Assert.Equal(3f, union.Radius, 1e-4f);
    }

    [Fact]
    public void Union_of_a_sphere_inside_another_is_the_outer_one()
    {
        BakeSphere[] parts = [new(new Vector3(1f, 0f, 0f), 0.5f), new(new Vector3(0f, 0f, 0f), 3f)];

        var union = BakeAreas.Union(parts);

        Assert.Equal(new BakeSphere(Vector3.Zero, 3f), union);
    }

    [Fact]
    public void Transform_moves_the_centre_and_grows_the_radius_by_the_largest_scale()
    {
        var transform = Matrix4x4.CreateScale(1f, 3f, 2f) * Matrix4x4.CreateTranslation(10f, 0f, 0f);

        var world = BakeAreas.Transform(new BakeSphere(new Vector3(1f, 1f, 1f), 2f), transform);

        Assert.Equal(new Vector3(11f, 3f, 2f), world.Center);
        Assert.Equal(6f, world.Radius, 1e-5f);
    }
}

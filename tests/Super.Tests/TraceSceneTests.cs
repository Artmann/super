using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rendition;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Super.Tests;

public class TraceSceneTests
{
    private static readonly MeshHandle Rock = new(3), Tree = new(4);
    private static readonly MaterialHandle Stone = new(1), Leaves = new(2), Glass = new(5);

    private static TraceFlags Flags(MaterialHandle material)
    {
        if (material == Leaves)
        {
            return TraceFlags.Cutout;
        }

        return material == Glass ? TraceFlags.SeeThrough : TraceFlags.None;
    }

    [Fact]
    public void Parts_of_one_placed_mesh_become_one_instance()
    {
        var at = Matrix4x4.CreateTranslation(1, 2, 3);
        DrawItem[] draws =
        [
            new() { Mesh = Rock, FirstIndex = 0, IndexCount = 30, Material = Stone, Transform = at },
            new() { Mesh = Rock, FirstIndex = 30, IndexCount = 12, Material = Leaves, Transform = at },
        ];

        var instance = Assert.Single(new TraceScene(Flags).Collect(draws));

        Assert.Equal([new TracePart(0, 30, Stone), new TracePart(30, 12, Leaves)], instance.Parts.ToArray());
        Assert.Equal(TraceFlags.Cutout, instance.Flags);
        Assert.Equal(Rock, instance.Mesh);
        Assert.Equal(at, instance.Transform);
    }

    [Fact]
    public void Each_placement_and_each_mesh_is_its_own_instance_in_the_order_first_drawn()
    {
        var here = Matrix4x4.CreateTranslation(1, 0, 0);
        var there = Matrix4x4.CreateTranslation(-4, 0, 2);
        DrawItem[] draws =
        [
            new() { Mesh = Rock, IndexCount = 6, Material = Stone, Transform = here },
            new() { Mesh = Tree, IndexCount = 9, Material = Leaves, Transform = here },
            new() { Mesh = Rock, IndexCount = 6, Material = Stone, Transform = there },
            new() { Mesh = Tree, FirstIndex = 9, IndexCount = 3, Material = Glass, Transform = here },
        ];

        var instances = new TraceScene(Flags).Collect(draws);

        Assert.Equal(3, instances.Count);
        Assert.Equal((Rock, here), (instances[0].Mesh, instances[0].Transform));
        Assert.Equal((Tree, here), (instances[1].Mesh, instances[1].Transform));
        Assert.Equal((Rock, there), (instances[2].Mesh, instances[2].Transform));
        Assert.Equal([new TracePart(0, 6, Stone)], instances[0].Parts.ToArray());
        Assert.Equal([new TracePart(0, 9, Leaves), new TracePart(9, 3, Glass)], instances[1].Parts.ToArray());
        Assert.Equal([new TracePart(0, 6, Stone)], instances[2].Parts.ToArray());
        Assert.Equal(TraceFlags.Cutout | TraceFlags.SeeThrough, instances[1].Flags);
        Assert.Equal(TraceFlags.None, instances[0].Flags);
    }

    [Fact]
    public void Leaves_out_ghosts_skinned_and_swimming_draws()
    {
        DrawItem[] draws =
        [
            new() { Mesh = Rock, IndexCount = 3, Material = Stone, Transform = Matrix4x4.Identity, Ghost = new Ghost(Vector3.One) },
            new() { Mesh = Rock, IndexCount = 3, Material = Stone, Transform = Matrix4x4.Identity, Bones = new BoneRange(0, 20) },
            new() { Mesh = Rock, IndexCount = 3, Material = Stone, Transform = Matrix4x4.Identity, Swim = new SwimMotion(0.1f, 1f, 0f) },
        ];

        Assert.Empty(new TraceScene(Flags).Collect(draws));
    }

    [Fact]
    public void Leaves_out_draws_with_no_triangles()
    {
        DrawItem[] draws = [new() { Mesh = Rock, IndexCount = 0, Material = Stone, Transform = Matrix4x4.Identity }];

        Assert.Empty(new TraceScene(Flags).Collect(draws));
    }

    [Fact]
    public void A_second_collect_forgets_the_first_frames_draws()
    {
        var scene = new TraceScene(Flags);
        scene.Collect(
        [
            new() { Mesh = Rock, IndexCount = 6, Material = Stone, Transform = Matrix4x4.Identity },
            new() { Mesh = Tree, IndexCount = 9, Material = Glass, Transform = Matrix4x4.Identity },
        ]);

        var instance = Assert.Single(scene.Collect(
            [new() { Mesh = Tree, FirstIndex = 3, IndexCount = 3, Material = Leaves, Transform = Matrix4x4.Identity }]));

        Assert.Equal(Tree, instance.Mesh);
        Assert.Equal([new TracePart(3, 3, Leaves)], instance.Parts.ToArray());
        Assert.Equal(TraceFlags.Cutout, instance.Flags);
    }

    [Fact]
    public void Each_part_keeps_its_draws_tint_and_an_untinted_draw_is_white()
    {
        var red = new Vector3(1f, 0.1f, 0.1f);
        DrawItem[] draws =
        [
            new() { Mesh = Rock, FirstIndex = 0, IndexCount = 6, Material = Stone, Transform = Matrix4x4.Identity, Tint = red },
            new() { Mesh = Rock, FirstIndex = 6, IndexCount = 6, Material = Stone, Transform = Matrix4x4.Identity },
        ];

        var instance = Assert.Single(new TraceScene(Flags).Collect(draws));

        Assert.Equal([red, Vector3.One], instance.Tints.ToArray());
        Assert.Equal(red, instance.TintOf(0));
        Assert.Equal(Vector3.One, instance.TintOf(1));
    }

    [Theory]
    [InlineData(1f, 0.5f, 0x3800_3C00u)]
    [InlineData(0f, 0f, 0u)]
    [InlineData(2f, 0.25f, 0x3400_4000u)]
    public void Two_floats_pack_as_half_floats_the_first_in_the_low_half(float low, float high, uint expected)
    {
        // 1.0 is 0x3C00 as a half, 0.5 is 0x3800, 2.0 is 0x4000 and 0.25 is 0x3400.
        Assert.Equal(expected, RayTracing.Half2(low, high));
    }

    [Fact]
    public void Collecting_the_same_frame_again_allocates_nothing()
    {
        var scene = new TraceScene(Flags);
        var draws = new DrawItem[60];
        for (int i = 0; i < draws.Length; i++)
        {
            // Twenty placements of two meshes, the rock in two parts, and every tenth draw a ghost left out.
            draws[i] = new DrawItem
            {
                Mesh = i % 3 == 2 ? Tree : Rock,
                FirstIndex = (uint)(i % 3) * 6,
                IndexCount = 6,
                Material = i % 3 == 2 ? Leaves : Stone,
                Transform = Matrix4x4.CreateTranslation(i / 3, 0, 0),
                Ghost = i % 10 == 9 ? new Ghost(Vector3.One) : null,
            };
        }

        int count = 0;
        for (int frame = 0; frame < 3; frame++)
        {
            count = scene.Collect(draws).Count;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 100; frame++)
        {
            count = scene.Collect(draws).Count;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.True(count >= 30, $"only {count} instances were collected");
    }

    [Fact]
    public void Vulkan_transform_moves_a_point_where_numerics_does()
    {
        var transform = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(5, -1, 3);
        var point = new Vector3(0.3f, 1.1f, -2f);
        var expected = Vector3.Transform(point, transform);

        var m = TraceScene.ToVulkan(transform);

        var actual = new Vector3(
            At(m, 0, 0) * point.X + At(m, 0, 1) * point.Y + At(m, 0, 2) * point.Z + At(m, 0, 3),
            At(m, 1, 0) * point.X + At(m, 1, 1) * point.Y + At(m, 1, 2) * point.Z + At(m, 1, 3),
            At(m, 2, 0) * point.X + At(m, 2, 1) * point.Y + At(m, 2, 2) * point.Z + At(m, 2, 3));
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }

    [Fact]
    public void Vulkan_transform_keeps_the_translation_in_the_last_column()
    {
        var m = TraceScene.ToVulkan(Matrix4x4.CreateTranslation(5, -1, 3));

        Assert.Equal(5f, At(m, 0, 3));
        Assert.Equal(-1f, At(m, 1, 3));
        Assert.Equal(3f, At(m, 2, 3));
    }

    [Fact]
    public void Each_instance_points_at_its_first_geometry_and_the_table_lists_every_part_in_order()
    {
        TraceInstance[] instances =
        [
            new(Matrix4x4.Identity, Rock, new TracePart[] { new(0, 30, Stone), new(30, 12, Leaves) }, TraceFlags.Cutout)
            {
                Tints = new Vector3[] { new(1f, 0.2f, 0.1f), new(0.5f, 0.5f, 1f) },
            },
            new(Matrix4x4.CreateTranslation(1, 0, 0), Tree, new TracePart[] { new(0, 9, Glass) }, TraceFlags.SeeThrough),
            new(Matrix4x4.CreateTranslation(2, 0, 0), Rock, new TracePart[] { new(0, 6, Stone) }, TraceFlags.None),
        ];
        ulong[] bottomLevels = [0x1000, 0x2000, 0x3000];
        var packed = new AccelerationStructureInstanceKHR[3];
        var geometries = new List<TraceGeometry>();

        RayTracing.Pack(instances, bottomLevels, packed, geometries);

        Assert.Equal([0u, 2u, 3u], packed.Select(i => i.InstanceCustomIndex).ToArray());
        Assert.Equal([0x1000ul, 0x2000ul, 0x3000ul], packed.Select(i => i.AccelerationStructureReference).ToArray());
        // Each part keeps its own draw's tint; an instance with no tints is white.
        Assert.Equal(
            [
                new TraceGeometry(Rock, new(0, 30, Stone), new(1f, 0.2f, 0.1f)),
                new TraceGeometry(Rock, new(30, 12, Leaves), new(0.5f, 0.5f, 1f)),
                new TraceGeometry(Tree, new(0, 9, Glass), Vector3.One),
                new TraceGeometry(Rock, new(0, 6, Stone), Vector3.One),
            ],
            geometries);
        Assert.Equal(1f, At(packed[1].Transform, 0, 3));
    }

    [Fact]
    public void See_through_instances_clear_the_see_through_mask_bit()
    {
        TraceInstance[] instances =
        [
            new(Matrix4x4.Identity, Rock, new TracePart[] { new(0, 6, Stone) }, TraceFlags.None),
            new(Matrix4x4.Identity, Rock, new TracePart[] { new(0, 6, Leaves) }, TraceFlags.Cutout),
            new(Matrix4x4.Identity, Tree, new TracePart[] { new(0, 6, Glass) }, TraceFlags.SeeThrough),
        ];
        var packed = new AccelerationStructureInstanceKHR[3];

        RayTracing.Pack(instances, [1, 2, 3], packed, []);

        Assert.Equal([0xFFu, 0xFFu, 0xFDu], packed.Select(i => i.Mask).ToArray());
    }

    [Theory]
    [InlineData(false, MaterialFlags.None, TraceFlags.None)]
    [InlineData(true, MaterialFlags.None, TraceFlags.Cutout)]
    [InlineData(false, MaterialFlags.Transparent, TraceFlags.SeeThrough)]
    [InlineData(true, MaterialFlags.Transparent, TraceFlags.Cutout | TraceFlags.SeeThrough)]
    public void A_materials_trace_flags_come_from_its_cutouts_and_transparency(
        bool cutouts, MaterialFlags flags, TraceFlags expected)
    {
        Assert.Equal(expected, RayTracing.TraceFlagsFor(cutouts, flags));
    }

    private static float At(TransformMatrixKHR m, int row, int column) =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<TransformMatrixKHR, float>(ref m), 12)[row * 4 + column];
}

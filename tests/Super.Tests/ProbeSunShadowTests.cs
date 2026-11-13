using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

public sealed class ProbeSunShadowTests
{
    private static readonly MeshHandle Rock = new(3);
    private static readonly MaterialHandle Stone = new(1);
    private static readonly MaterialHandle Glass = new(2);

    [Fact]
    public void The_probes_sun_map_leaves_out_ghosts_skinned_swimming_and_see_through_draws()
    {
        var here = Matrix4x4.CreateTranslation(1f, 0f, 0f);
        var there = Matrix4x4.CreateTranslation(2f, 0f, 0f);
        DrawItem[] draws =
        [
            new() { Mesh = Rock, IndexCount = 3, Material = Stone, Transform = here },
            new() { Mesh = Rock, IndexCount = 3, Material = Stone, Transform = Matrix4x4.Identity, Ghost = new Ghost(Vector3.One) },
            new() { Mesh = Rock, IndexCount = 3, Material = Stone, Transform = Matrix4x4.Identity, Bones = new BoneRange(0, 20) },
            new() { Mesh = Rock, IndexCount = 3, Material = Stone, Transform = Matrix4x4.Identity, Swim = new SwimMotion(0.1f, 1f, 0f) },
            new() { Mesh = Rock, IndexCount = 3, Material = Glass, Transform = Matrix4x4.Identity },
            new() { Mesh = Rock, IndexCount = 3, Material = Stone, Transform = there },
        ];
        DrawItem[] casters = [];

        int count = ProbeSunShadow.Casters(
            draws, material => material == Glass ? MaterialFlags.Transparent : MaterialFlags.None, ref casters);

        Assert.Equal([here, there], casters.Take(count).Select(draw => draw.Transform));
    }
}

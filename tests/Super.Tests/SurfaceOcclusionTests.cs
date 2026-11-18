using System.Numerics;
using Rendition;
using Super.Testing;

namespace Super.Tests;

/// <summary>A generated surface's occlusion texture: its flag, the second UV set it can be read through, and its bake id.</summary>
public class SurfaceOcclusionTests
{
    private static readonly Vertex[] Triangle =
    [
        new(new Vector3(0f, 0f, 0f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.Zero),
        new(new Vector3(1f, 0f, 0f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.UnitX),
        new(new Vector3(0f, 0f, 1f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.UnitY),
    ];

    private static TextureRef Grey(Content content, byte red) =>
        content.Texture(new GeneratedTexture(1, [red, red, red, 255]), TextureKind.Linear);

    private static string? Problem(GeneratedMesh mesh) => LightmapUnwrap.Problem(
        mesh.Vertices.Select(v => v.Position).ToArray(), mesh.Vertices.Select(v => v.Uv2).ToArray(), mesh.Indices);

    [Fact]
    public void Occlusion_on_the_second_uv_set_sets_the_materials_flag()
    {
        Assert.Equal(MaterialFlags.OcclusionOnUv2, SurfaceMaterials.FlagsOf(new Surface { Occlusion = new OcclusionMap(new TextureRef(1), OnUv2: true) }));
        Assert.Equal(MaterialFlags.None, SurfaceMaterials.FlagsOf(new Surface()) & MaterialFlags.OcclusionOnUv2);
    }

    [Fact]
    public void A_mesh_whose_uv2_fits_keeps_it_with_occlusion_on_it()
    {
        var content = new Content(new MemoryContent());
        var box = Shapes.Box(Vector3.One) with { Surface = new Surface { Occlusion = new OcclusionMap(Grey(content, 90), OnUv2: true) } };

        Assert.Same(box, box.WithLightmapUvs());
    }

    [Fact]
    public void A_mesh_whose_uv2_must_be_replaced_is_refused_while_occlusion_is_on_it()
    {
        var content = new Content(new MemoryContent());
        var box = Shapes.Box(Vector3.One);
        var bare = box with
        {
            Vertices = [.. box.Vertices.Select(v => v with { Uv2 = Vector2.Zero })],
            Surface = new Surface { Occlusion = new OcclusionMap(Grey(content, 90), OnUv2: true) },
        };
        string? problem = Problem(bare);
        Assert.NotNull(problem);

        var error = Assert.Throws<InvalidOperationException>(() => content.Mesh(bare, isStatic: true));

        Assert.Equal(
            $"The surface reads its occlusion texture through Uv2, so Uv2 can't be replaced to fit a lightmap. {problem} Fix the mesh's Uv2, or read the occlusion through Uv.",
            error.Message);
    }

    [Fact]
    public void Occlusion_on_the_first_uv_set_lets_the_second_be_replaced()
    {
        var content = new Content(new MemoryContent());
        var box = Shapes.Box(Vector3.One);
        var bare = box with
        {
            Vertices = [.. box.Vertices.Select(v => v with { Uv2 = Vector2.Zero })],
            Surface = new Surface { Occlusion = new OcclusionMap(Grey(content, 90)) },
        };

        var made = bare.WithLightmapUvs();

        Assert.Null(Problem(made));
    }

    [Fact]
    public void An_occlusion_texture_named_nowhere_is_refused()
    {
        var content = new Content(new MemoryContent());
        var surface = new Surface { Occlusion = new OcclusionMap(new TextureRef(41)) };

        var error = Assert.Throws<ArgumentException>(() => content.Mesh(new GeneratedMesh(Triangle, [0, 1, 2], surface)));

        Assert.Equal("Occlusion.Map names 41, but nothing was named with that index. (Parameter 'Occlusion.Map')", error.Message);
    }

    [Fact]
    public void Occlusion_changes_a_meshs_identity_and_which_texture_it_is_counts()
    {
        var content = new Content(new MemoryContent());
        var dark = Grey(content, 40);
        var light = Grey(content, 200);
        ulong Identity(Surface surface) => BakeIdentity.Model(
            content.Mesh(new GeneratedMesh(Triangle, [0, 1, 2], surface)), content.Store.Items, content.Store.Files, _ => []);

        ulong plain = Identity(new Surface());
        ulong darkOnFirst = Identity(new Surface { Occlusion = new OcclusionMap(dark) });
        ulong lightOnFirst = Identity(new Surface { Occlusion = new OcclusionMap(light) });
        ulong darkOnSecond = Identity(new Surface { Occlusion = new OcclusionMap(dark, OnUv2: true) });

        Assert.Equal(4, new[] { plain, darkOnFirst, lightOnFirst, darkOnSecond }.Distinct().Count());
    }
}

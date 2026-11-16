using System.Numerics;
using Rendition;

namespace Super.Tests;

/// <summary>Generated meshes with the same surface share one material, and it is destroyed when the last of them goes.</summary>
public sealed class SharedMaterialsTests
{
    private readonly List<MaterialHandle> _destroyed = [];
    private int _made;

    private SharedMaterials Materials() => new(_destroyed.Add);

    /// <summary>What the renderer does for each mesh: share the surface's material, or make one.</summary>
    private MaterialHandle Upload(SharedMaterials materials, Surface surface)
    {
        if (materials.TryShare(surface, out var handle))
        {
            return handle;
        }

        handle = new MaterialHandle(_made++);
        materials.Add(surface, handle);
        return handle;
    }

    [Fact]
    public void Meshes_with_one_surface_share_one_material()
    {
        var materials = Materials();
        var surface = new Surface { Color = new(0.2f, 0.6f, 0.2f) };

        var first = Upload(materials, surface);
        var second = Upload(materials, surface);

        Assert.Equal(first, second);
        Assert.Equal(1, _made);
    }

    [Fact]
    public void Surfaces_built_again_with_equal_arrays_share()
    {
        var materials = Materials();

        Upload(materials, new Surface { Parameters = [new Vector4(1f, 2f, 3f, 4f)], ShaderTextures = [new TextureRef(5)] });
        Upload(materials, new Surface { Parameters = [new Vector4(1f, 2f, 3f, 4f)], ShaderTextures = [new TextureRef(5)] });

        Assert.Equal(1, _made);
    }

    [Theory]
    [InlineData("color")]
    [InlineData("parameter")]
    [InlineData("texture")]
    [InlineData("array")]
    public void Surfaces_that_differ_get_their_own_materials(string what)
    {
        var materials = Materials();
        var surface = new Surface { Parameters = [Vector4.One], ShaderTextures = [new TextureRef(1)], ShaderArrays = [new TextureArrayRef(2)] };
        var other = what switch
        {
            "color" => surface with { Color = new(0.1f) },
            "parameter" => surface with { Parameters = [Vector4.Zero] },
            "texture" => surface with { ShaderTextures = [new TextureRef(3)] },
            _ => surface with { ShaderArrays = [new TextureArrayRef(4)] },
        };

        var first = Upload(materials, surface);
        var second = Upload(materials, other);

        Assert.NotEqual(first, second);
        Assert.Equal(2, materials.Count);
    }

    [Fact]
    public void A_parameter_changed_in_place_gets_a_new_material_for_later_meshes()
    {
        var materials = Materials();
        var surface = new Surface { Parameters = [new Vector4(0.2f)] };
        var before = Upload(materials, surface);

        surface.Parameters[0] = new Vector4(0.9f);
        var after = Upload(materials, surface);

        Assert.NotEqual(before, after);
        Assert.Equal(after, Upload(materials, new Surface { Parameters = [new Vector4(0.9f)] }));

        materials.Release(before);
        Assert.Equal([before], _destroyed);
        Assert.True(materials.TryShare(surface, out var kept));
        Assert.Equal(after, kept);
    }

    [Fact]
    public void The_material_is_destroyed_only_when_its_last_mesh_goes()
    {
        var materials = Materials();
        var surface = new Surface();
        var handle = Upload(materials, surface);
        Upload(materials, surface);

        materials.Release(handle);
        Assert.Empty(_destroyed);

        materials.Release(handle);
        Assert.Equal([handle], _destroyed);
        Assert.Equal(0, materials.Count);
    }

    [Fact]
    public void A_surface_used_again_after_its_material_went_gets_a_new_one()
    {
        var materials = Materials();
        var surface = new Surface();
        materials.Release(Upload(materials, surface));

        Upload(materials, surface);

        Assert.Equal(2, _made);
    }

    [Fact]
    public void Releasing_one_of_two_surfaces_leaves_the_other()
    {
        var materials = Materials();
        var grass = Upload(materials, new Surface { Color = new(0.2f, 0.6f, 0.2f) });
        var rock = Upload(materials, new Surface { Color = new(0.5f) });

        materials.Release(grass);

        Assert.Equal([grass], _destroyed);
        Assert.True(materials.TryShare(new Surface { Color = new(0.5f) }, out var shared));
        Assert.Equal(rock, shared);
    }
}

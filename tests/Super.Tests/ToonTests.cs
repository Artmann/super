using System.Numerics;
using Rendition;
using Rendition.Passes;
using Super.Testing;

namespace Super.Tests;

/// <summary>Toon shading and outlines, from a surface and a view down to what the shaders are handed.</summary>
public class ToonTests
{
    private static readonly Vertex[] Triangle =
    [
        new(new Vector3(0f, 0f, 0f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.Zero),
        new(new Vector3(1f, 0f, 0f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.UnitX),
        new(new Vector3(0f, 0f, 1f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.UnitY),
    ];

    [Theory]
    [InlineData(0, "toon steps 0 must be from 1 to 16.")]
    [InlineData(17, "toon steps 17 must be from 1 to 16.")]
    public void Steps_outside_1_to_16_are_refused(int steps, string message)
    {
        Assert.Equal(message, new ToonShading { Steps = steps }.Problem());
    }

    [Fact]
    public void A_negative_rim_is_refused_and_good_settings_pass()
    {
        Assert.Equal("toon rim <0, -0.1, 0> must be 0 or more in each channel.", new ToonShading { Rim = new Vector3(0f, -0.1f, 0f) }.Problem());
        Assert.Null(new ToonShading { Steps = 16, Rim = new Vector3(0.2f) }.Problem());
        Assert.Null(new ToonShading { Steps = 1 }.Problem());
    }

    [Fact]
    public void A_material_with_bad_toon_settings_has_a_problem()
    {
        Assert.Equal("toon steps 0 must be from 1 to 16.", new Material { Toon = new ToonShading { Steps = 0 } }.Problem());
        Assert.Null(new Material { Toon = new ToonShading() }.Problem());
    }

    [Fact]
    public void The_outline_thickness_is_scaled_to_scene_pixels()
    {
        var settings = new OutlineSettings(new Vector3(0.1f, 0.2f, 0.3f), Thickness: 2f, FadeStart: 10f, FadeEnd: 20f);

        var (outline, fade) = Outlines.Pack(settings, sceneToPicture: 0.75f);

        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 1.5f), outline);
        Assert.Equal(new Vector4(10f, 20f, 0f, 0f), fade);
    }

    [Theory]
    [InlineData(-1f, 30f, 60f, "The outline thickness must be from 0 to 8 pixels, not -1.")]
    [InlineData(9f, 30f, 60f, "The outline thickness must be from 0 to 8 pixels, not 9.")]
    [InlineData(1f, 60f, 30f, "The outline must start fading at zero or further and end no nearer than it starts, but it runs from 60 to 30 metres.")]
    [InlineData(1f, -1f, 30f, "The outline must start fading at zero or further and end no nearer than it starts, but it runs from -1 to 30 metres.")]
    public void A_view_refuses_bad_outlines(float thickness, float fadeStart, float fadeEnd, string message)
    {
        var view = new View();
        view.Begin(0f);

        var error = Assert.Throws<ArgumentException>(() => view.Outlines(thickness: thickness, fadeStart: fadeStart, fadeEnd: fadeEnd));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void A_view_refuses_a_negative_outline_colour()
    {
        var view = new View();
        view.Begin(0f);

        var error = Assert.Throws<ArgumentException>(() => view.Outlines(new Vector3(-1f, 0f, 0f)));

        Assert.Equal("The outline colour <-1, 0, 0> must be 0 or more in each channel.", error.Message);
    }

    [Fact]
    public void Outlines_set_for_one_frame_go_back_to_the_defaults_on_the_next()
    {
        var view = new View();
        view.Begin(0f);
        view.Outlines(new Vector3(1f, 0f, 0f), thickness: 3f, fadeStart: 5f, fadeEnd: 8f);
        Assert.Equal(new OutlineSettings(new Vector3(1f, 0f, 0f), 3f, 5f, 8f), view.OutlineSettings);

        view.Begin(0.1f);

        Assert.Equal(new OutlineSettings(Vector3.Zero, 1.5f, 30f, 60f), view.OutlineSettings);
    }

    [Fact]
    public void An_outlined_surface_sets_the_outline_flag()
    {
        Assert.Equal(MaterialFlags.Outline, SceneRenderer.FlagsOf(new Surface { Outline = true }) & MaterialFlags.Outline);
        Assert.Equal(MaterialFlags.None, SceneRenderer.FlagsOf(new Surface()) & MaterialFlags.Outline);
    }

    [Fact]
    public void A_surface_with_bad_toon_steps_is_refused_when_it_is_handed_over()
    {
        var content = new Content(new MemoryContent());
        var surface = new Surface { Toon = new Toon(Steps: 20) };

        var error = Assert.Throws<ArgumentException>(() => content.Mesh(new GeneratedMesh(Triangle, [0, 1, 2], surface)));

        Assert.Equal("The surface's toon steps 20 must be from 1 to 16. (Parameter 'surface')", error.Message);
    }

    [Fact]
    public void A_toon_ramp_named_nowhere_is_refused()
    {
        var content = new Content(new MemoryContent());
        var surface = new Surface { Toon = new Toon(Ramp: new TextureRef(41)) };

        var error = Assert.Throws<ArgumentException>(() => content.Mesh(new GeneratedMesh(Triangle, [0, 1, 2], surface)));

        Assert.Equal("Toon.Ramp names 41, but nothing was named with that index. (Parameter 'Toon.Ramp')", error.Message);
    }

    [Fact]
    public void Toon_and_outline_change_a_meshs_identity_and_which_toon_counts()
    {
        var content = new Content(new MemoryContent());
        ulong Identity(Surface surface) => BakeIdentity.Model(
            content.Mesh(new GeneratedMesh(Triangle, [0, 1, 2], surface)), content.Items, content.Files, _ => []);

        ulong[] ids =
        [
            Identity(new Surface()),
            Identity(new Surface { Toon = new Toon() }),
            Identity(new Surface { Toon = new Toon(Steps: 2) }),
            Identity(new Surface { Toon = new Toon(Rim: new Vector3(0.2f)) }),
            Identity(new Surface { Outline = true }),
        ];

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }
}

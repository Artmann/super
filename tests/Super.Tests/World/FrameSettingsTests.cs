using System.Numerics;
using Rendition;

namespace Super.Tests;

public class FrameSettingsTests
{
    private readonly World _world = new();
    private readonly View _view = new();

    private static SkySettings Plain(float zenith) => new()
    {
        Zenith = new Vector3(zenith),
        Horizon = new Vector3(0.5f),
        Ground = new Vector3(0.2f),
    };

    private void Describe()
    {
        _view.Begin(0f);
        WorldFrame.Describe(_view, _world);
    }

    [Fact]
    public void The_newest_sky_wins()
    {
        _world.Spawn(new Sky(Plain(1f)));
        var dusk = _world.Spawn(new Sky(Plain(2f)));

        Describe();
        Assert.Equal(new Vector3(2f), _view.Surroundings.SkySettings?.Zenith);

        _world.Destroy(dusk);
        Describe();
        Assert.Equal(new Vector3(1f), _view.Surroundings.SkySettings?.Zenith);
    }

    [Fact]
    public void Draw_overrides_the_world_sky()
    {
        _world.Spawn(new Sky(Plain(1f)));

        Describe();
        _view.Surroundings.Sky(Plain(3f));

        Assert.Equal(new Vector3(3f), _view.Surroundings.SkySettings?.Zenith);
    }

    [Fact]
    public void A_world_with_no_sky_leaves_the_studio()
    {
        Describe();

        Assert.Null(_view.Surroundings.SkySettings);
    }

    [Fact]
    public void A_bad_shadow_radius_throws_with_the_view_message()
    {
        _world.Spawn(new Shadows(0f));

        var error = Assert.Throws<ArgumentOutOfRangeException>(Describe);

        Assert.StartsWith("The shadow radius must be more than zero.", error.Message);
    }

    [Fact]
    public void Shadows_set_the_frames_shadows()
    {
        _world.Spawn(new Shadows(20f, Cascades: 3, SunSize: 0.05f, Contact: 0f));

        Describe();

        Assert.Equal(20f, _view.Lights.ShadowRadius);
        Assert.Equal(3, _view.Lights.ShadowCascades);
        Assert.Equal(0.05f, _view.Lights.SunSize);
        Assert.Equal(0f, _view.Lights.ShadowContact);
    }

    [Fact]
    public void Shadows_left_at_their_defaults_match_the_view_calls()
    {
        _world.Spawn(new Shadows(20f));
        Describe();
        var fromEntity = (_view.Lights.ShadowCascades, _view.Lights.SunSize, _view.Lights.ShadowContact);

        _view.Begin(0f);
        _view.Lights.Shadows(20f);

        Assert.Equal((_view.Lights.ShadowCascades, _view.Lights.SunSize, _view.Lights.ShadowContact), fromEntity);
    }

    [Fact]
    public void Ambient_occlusion_sets_the_frames_occlusion()
    {
        _world.Spawn(new AmbientOcclusion(Radius: 2f, Strength: 0.25f));

        Describe();

        Assert.Equal(2f, _view.Indirect.Occlusion.Radius);
        Assert.Equal(0.25f, _view.Indirect.Occlusion.Strength);
    }

    [Fact]
    public void Finish_sets_the_frames_look()
    {
        _world.Spawn(new Finish(new PostSettings { Exposure = 1.5f }));

        Describe();

        Assert.Equal(1.5f, _view.Effects.Post.Exposure);
    }

    [Fact]
    public void Outlines_set_the_frames_ink()
    {
        _world.Spawn(new Outlines(Color: new Vector3(1f, 0f, 0f), Thickness: 3f));

        Describe();

        Assert.Equal(new Vector3(1f, 0f, 0f), _view.Effects.OutlineSettings.Color);
        Assert.Equal(3f, _view.Effects.OutlineSettings.Thickness);
        Assert.Equal(OutlineSettings.DefaultFadeEnd, _view.Effects.OutlineSettings.FadeEnd);
    }

    [Fact]
    public void Screen_bounce_sets_the_frames_bounce()
    {
        _world.Spawn(new ScreenBounce(Strength: 1.5f, Radius: 3f));

        Describe();

        Assert.Equal(new BounceSettings(1.5f, 3f), _view.Indirect.Bounce);
    }

    [Fact]
    public void Light_probes_set_the_frames_probe_box()
    {
        _world.Spawn(new LightProbes(new Vector3(-1f, 0f, -4f), new Vector3(1f, 2.5f, 4f), 0.25f));

        Describe();

        Assert.Equal(new ProbeVolumeSettings(new Vector3(-1f, 0f, -4f), new Vector3(1f, 2.5f, 4f), 0.25f), _view.Indirect.LightProbeVolume);
    }

    [Fact]
    public void Baked_lighting_names_the_frames_baked_space()
    {
        _world.Spawn(new BakedLighting("crypt", new Vector3(-8f, -1f, -12f), new Vector3(8f, 6f, 12f), 1f, TexelsPerMetre: 10));

        Describe();

        var space = _view.Indirect.BakedSpace;
        Assert.Equal("crypt", space?.Name);
        Assert.Equal(new Vector3(8f, 6f, 12f), space?.Max);
        Assert.Equal(10, space?.TexelsPerMetre);
    }

    [Fact]
    public void Underwater_puts_the_frame_underwater()
    {
        _world.Spawn(new Underwater(new UnderwaterSettings { Absorption = new Vector3(0.045f, 0.024f, 0.018f), SurfaceHeight = 30f }));

        Describe();

        Assert.Equal(new Vector3(0.045f, 0.024f, 0.018f), _view.Surroundings.WaterSettings.Absorption);
        Assert.Equal(30f, _view.Surroundings.WaterSettings.SurfaceHeight);
    }

    [Fact]
    public void Puddles_set_the_frames_water_level()
    {
        _world.Spawn(new Puddles(-0.045f, Band: 0.035f));

        Describe();

        Assert.Equal(new PuddleSettings(-0.045f, 0.035f), _view.Surroundings.PuddleSettings);
    }

    [Fact]
    public void Wind_sets_the_frames_wind()
    {
        _world.Spawn(new Wind(new Vector3(0.6f, 0f, -0.3f), 0.35f, Gustiness: 0.3f));

        Describe();

        Assert.Equal(new WindSettings(new Vector3(0.6f, 0f, -0.3f), 0.35f, 0.3f), _view.Surroundings.WindSettings);
    }

    [Fact]
    public void The_newest_wind_wins()
    {
        _world.Spawn(new Wind(Vector3.UnitX, 0.1f));
        var gale = _world.Spawn(new Wind(Vector3.UnitZ, 1f));

        Describe();
        Assert.Equal(1f, _view.Surroundings.WindSettings?.Strength);

        _world.Destroy(gale);
        Describe();
        Assert.Equal(0.1f, _view.Surroundings.WindSettings?.Strength);
    }

    [Fact]
    public void A_bad_wind_throws_with_the_view_message()
    {
        _world.Spawn(new Wind(Vector3.UnitX, -1f));

        var error = Assert.Throws<ArgumentOutOfRangeException>(Describe);

        Assert.StartsWith("The wind's strength must be 0 or more metres", error.Message);
    }

    [Fact]
    public void Draw_overrides_the_world_baked_space()
    {
        DrawOverTheWorldBakedSpace();

        Assert.Equal("crypt-lit", _view.Indirect.BakedSpace?.Name);
    }

    [Fact]
    public void Drawing_two_baked_spaces_by_hand_still_throws()
    {
        DrawOverTheWorldBakedSpace();
        var error = Assert.Throws<InvalidOperationException>(
            () => _view.Indirect.BakedLighting("crypt-dark", new Vector3(-4f), new Vector3(4f), 1f));

        Assert.Equal(
            "The baked space \"crypt-lit\" was already named this frame, so \"crypt-dark\" can't be. A frame has at most one baked space.",
            error.Message);
    }

    // Names the baked space "crypt" in the world, describes the frame, then names "crypt-lit" from Draw.
    private void DrawOverTheWorldBakedSpace()
    {
        _world.Spawn(new BakedLighting("crypt", new Vector3(-8f, -1f, -12f), new Vector3(8f, 6f, 12f), 1f));
        Describe();
        _view.Indirect.BakedLighting("crypt-lit", new Vector3(-4f), new Vector3(4f), 1f);
    }
}

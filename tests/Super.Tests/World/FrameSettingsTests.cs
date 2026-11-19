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
}

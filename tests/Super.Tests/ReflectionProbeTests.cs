using System.Numerics;
using Rendition;

namespace Super.Tests;

public class ReflectionProbeTests
{
    private static readonly Vector3 BoxMin = new(-3f, -1f, -10f);
    private static readonly Vector3 BoxMax = new(10f, 6f, 7f);

    [Fact]
    public void A_fifth_probe_is_refused()
    {
        var view = new View();
        for (int key = 0; key < 4; key++)
        {
            view.ReflectionProbe(key, new Vector3(key, 1.6f, 0f), BoxMin, BoxMax);
        }

        var error = Assert.Throws<InvalidOperationException>(() => view.ReflectionProbe(4, Vector3.Zero, BoxMin, BoxMax));

        Assert.Contains("4", error.Message);
        Assert.Equal(4, view.ReflectionProbes.Length);
    }

    [Fact]
    public void A_probe_whose_box_excludes_its_position_is_refused()
    {
        var view = new View();

        var error = Assert.Throws<ArgumentException>(() => view.ReflectionProbe(1, new Vector3(0f, 7f, 0f), BoxMin, BoxMax));

        Assert.Equal("position", error.ParamName);
        Assert.Contains("position", error.Message);
        Assert.True(view.ReflectionProbes.IsEmpty);
    }

    [Fact]
    public void The_same_key_twice_in_a_frame_is_refused()
    {
        var view = new View();
        view.ReflectionProbe(7, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax);

        var error = Assert.Throws<ArgumentException>(() => view.ReflectionProbe(7, new Vector3(1f, 1.6f, -1f), BoxMin, BoxMax));

        Assert.Equal("key", error.ParamName);
        Assert.Contains("7", error.Message);
        Assert.Equal(1, view.ReflectionProbes.Length);
    }

    [Fact]
    public void A_probe_is_captured_once_until_it_changes()
    {
        var schedule = new ReflectionProbeSchedule();
        var probe = new ReflectionProbeRequest(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, 0.5f);
        var moved = probe with { Position = new Vector3(4f, 1.6f, -1f) };

        Assert.True(CaptureIfDue(schedule, probe, hash: 10));
        Assert.False(CaptureIfDue(schedule, probe, hash: 10));
        Assert.True(CaptureIfDue(schedule, probe, hash: 11));
        Assert.False(CaptureIfDue(schedule, probe, hash: 11));
        Assert.True(CaptureIfDue(schedule, moved, hash: 11));
        Assert.False(CaptureIfDue(schedule, moved, hash: 11));
    }

    [Fact]
    public void A_capture_that_never_happened_is_still_due()
    {
        var schedule = new ReflectionProbeSchedule();
        var probe = new ReflectionProbeRequest(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, 0.5f);
        schedule.Captured(probe, 10);

        // Asked, but the renderer threw before Captured, as with a fifth probe.
        Assert.True(schedule.ShouldCapture(probe, 11));
        Assert.True(schedule.ShouldCapture(probe, 11));
        Assert.True(schedule.ShouldCapture(probe with { Key = 2 }, 10));
        Assert.Equal(-1, schedule.SlotOf(2));
    }

    [Fact]
    public void Moving_the_camera_keeps_the_lighting_hash_but_turning_the_sun_changes_it()
    {
        var sun = new[] { new DirectionalLight { Direction = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.3f)), Radiance = new Vector3(3f), CastsShadow = true } };
        var turned = new[] { sun[0] with { Direction = Vector3.Normalize(new Vector3(-0.4f, 0.8f, 0.3f)) } };
        var view = new FrameView { Lights = sun, EnvironmentIntensity = 1f };
        view.Shadows.Center = new Vector3(1f, 0f, -5f);
        view.Shadows.Radius = 12f;
        int before = Renderer.ReflectionLightingHash(view, environmentVersion: 1);

        // What SceneRenderer does when the camera moves: the shadows follow its target.
        var reframed = view;
        reframed.Shadows.Center = new Vector3(6f, 0f, 2f);
        reframed.Shadows.Radius = 20f;
        reframed.CameraPosition = new Vector3(5f, 2f, 4f);

        var sunTurned = view;
        sunTurned.Lights = turned;

        Assert.Equal(before, Renderer.ReflectionLightingHash(reframed, environmentVersion: 1));
        Assert.Equal(Renderer.LightingHash(view, 1), Renderer.LightingHash(reframed, 1));
        Assert.NotEqual(before, Renderer.ReflectionLightingHash(sunTurned, environmentVersion: 1));
        Assert.NotEqual(Renderer.LightingHash(view, 1), Renderer.LightingHash(sunTurned, 1));
    }

    [Fact]
    public void The_lighting_hash_changes_with_the_sun_colour_the_sky_and_the_switches_that_show()
    {
        var sun = new[] { new DirectionalLight { Direction = Vector3.UnitY, Radiance = new Vector3(3f), CastsShadow = true } };
        var view = new FrameView { Lights = sun, EnvironmentIntensity = 1f };
        view.Shadows.Radius = 12f;
        int before = Renderer.ReflectionLightingHash(view, environmentVersion: 1);

        var brighter = view;
        brighter.Lights = new[] { sun[0] with { Radiance = new Vector3(4f) } };
        var dimSky = view;
        dimSky.EnvironmentIntensity = 0.5f;
        var noOcclusion = view;
        noOcclusion.Off = RenderEffects.AmbientOcclusion;
        var noContact = view;
        noContact.Off = RenderEffects.ContactShadows | RenderEffects.LightShadows;
        var noMultiscatter = view;
        noMultiscatter.Off = RenderEffects.MultiscatterSpecular;

        Assert.NotEqual(before, Renderer.ReflectionLightingHash(brighter, 1));
        Assert.NotEqual(before, Renderer.ReflectionLightingHash(dimSky, 1));
        Assert.NotEqual(before, Renderer.ReflectionLightingHash(view, 2));
        Assert.NotEqual(before, Renderer.ReflectionLightingHash(noOcclusion, 1));
        Assert.NotEqual(before, Renderer.ReflectionLightingHash(noMultiscatter, 1));
        Assert.Equal(before, Renderer.ReflectionLightingHash(noContact, 1));
    }

    [Fact]
    public void A_capture_frames_the_sun_shadows_on_its_box()
    {
        var shadows = new ShadowSettings { Center = new Vector3(40f, 0f, 40f), Radius = 30f, Cascades = 2, LightAngle = 0.02f };

        var framed = Renderer.CaptureShadows(shadows, BoxMin, BoxMax);

        // The box is 13 x 7 x 17 m: its diagonal is sqrt(507), about 22.52 m, and half that is the radius.
        Assert.Equal(new Vector3(3.5f, 2.5f, -1.5f), framed.Center);
        Assert.Equal(11.258f, framed.Radius, 0.001f);
        Assert.Equal(2, framed.Cascades);
        Assert.Equal(0.02f, framed.LightAngle);
        Assert.Equal(0f, Renderer.CaptureShadows(shadows with { Radius = 0f }, BoxMin, BoxMax).Radius);
    }

    private static bool CaptureIfDue(ReflectionProbeSchedule schedule, in ReflectionProbeRequest request, int hash)
    {
        if (!schedule.ShouldCapture(request, hash))
        {
            return false;
        }

        schedule.Captured(request, hash);
        return true;
    }

    [Fact]
    public void Each_key_keeps_its_own_slot_and_a_new_key_takes_the_next()
    {
        var schedule = new ReflectionProbeSchedule();
        var first = new ReflectionProbeRequest(5, new Vector3(1f, 1.6f, 0f), BoxMin, BoxMax, 0.5f);
        var second = new ReflectionProbeRequest(2, new Vector3(2f, 1.6f, 0f), BoxMin, BoxMax, 0.5f);

        schedule.Captured(first, 1);
        schedule.Captured(second, 1);
        schedule.Captured(first with { Blend = 1f }, 1);

        Assert.Equal(0, schedule.SlotOf(5));
        Assert.Equal(1, schedule.SlotOf(2));
        Assert.Equal(-1, schedule.SlotOf(9));
    }

    [Fact]
    public void A_probe_no_longer_asked_for_clears_them_all_so_the_rest_are_captured_again()
    {
        var schedule = new ReflectionProbeSchedule();
        var first = new ReflectionProbeRequest(1, new Vector3(1f, 1.6f, 0f), BoxMin, BoxMax, 0.5f);
        var second = new ReflectionProbeRequest(2, new Vector3(2f, 1.6f, 0f), BoxMin, BoxMax, 0.5f);
        schedule.Captured(first, 1);
        schedule.Captured(second, 1);

        Assert.False(schedule.Forget([first, second]));
        Assert.True(schedule.Forget([second]));

        Assert.True(CaptureIfDue(schedule, second, 1));
        Assert.Equal(0, schedule.SlotOf(2));
    }

    [Fact]
    public void With_reflections_off_no_probe_is_wanted()
    {
        var view = new View();
        view.ReflectionProbe(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax);

        Assert.Equal(1, SceneRenderer.WantedReflectionProbes(view, RenderEffects.Bloom).Length);
        Assert.True(SceneRenderer.WantedReflectionProbes(view, RenderEffects.Reflections | RenderEffects.Bloom).IsEmpty);
    }

    [Fact]
    public void The_probes_last_one_frame()
    {
        var view = new View();
        view.Begin(0f);
        view.ReflectionProbe(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, blend: 0.75f);

        Assert.Equal(
            new ReflectionProbeRequest(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, 0.75f),
            Assert.Single(view.ReflectionProbes.ToArray()));

        view.Begin(0f);

        Assert.True(view.ReflectionProbes.IsEmpty);
    }
}

using System.Numerics;
using Rendition;

namespace Super.Tests;

public class ReflectionProbeTests
{
    private static readonly Vector3 BoxMin = new(-3f, -1f, -10f);
    private static readonly Vector3 BoxMax = new(10f, 6f, 7f);

    [Fact]
    public void Sixty_four_probes_fit_and_a_sixty_fifth_is_refused()
    {
        var view = new View();
        for (int key = 0; key < 64; key++)
        {
            view.ReflectionProbe(key, new Vector3(key % 10, 1.6f, 0f), BoxMin, BoxMax);
        }

        var error = Assert.Throws<InvalidOperationException>(() => view.ReflectionProbe(64, Vector3.Zero, BoxMin, BoxMax));

        Assert.Equal("At most 64 reflection probes fit in a frame.", error.Message);
        Assert.Equal(64, view.ReflectionProbes.Length);
    }

    [Fact]
    public void A_probe_keeps_its_priority_and_an_endless_one_is_refused()
    {
        var view = new View();
        view.ReflectionProbe(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, priority: 2f);

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => view.ReflectionProbe(2, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, priority: float.PositiveInfinity));

        Assert.Equal(2f, Assert.Single(view.ReflectionProbes.ToArray()).Priority);
        Assert.Equal("priority", error.ParamName);
        Assert.StartsWith("The reflection probe's priority must be a finite number.", error.Message);
    }

    [Fact]
    public void A_probe_keeps_how_and_when_it_updates_and_an_unknown_way_is_refused()
    {
        var view = new View();
        view.ReflectionProbe(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, update: ProbeUpdate.OnRequest, version: 4);

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => view.ReflectionProbe(2, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, update: (ProbeUpdate)7));

        var placed = Assert.Single(view.ReflectionProbes.ToArray());
        Assert.Equal(ProbeUpdate.OnRequest, placed.Update);
        Assert.Equal(4, placed.Version);
        Assert.Equal("update", error.ParamName);
        Assert.StartsWith("The reflection probe's update must be OnChange, OnRequest or Always.", error.Message);
    }

    [Fact]
    public void A_probe_whose_box_excludes_its_position_is_refused()
    {
        var view = new View();

        var error = Assert.Throws<ArgumentException>(() => view.ReflectionProbe(1, new Vector3(0f, 7f, 0f), BoxMin, BoxMax));

        Assert.Equal("position", error.ParamName);
        Assert.Equal(
            "The reflection probe's position <0, 7, 0> must be inside its box, from <-3, -1, -10> to <10, 6, 7>. (Parameter 'position')",
            error.Message);
        Assert.True(view.ReflectionProbes.IsEmpty);
    }

    [Fact]
    public void The_same_key_twice_in_a_frame_is_refused()
    {
        var view = new View();
        view.ReflectionProbe(7, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax);

        var error = Assert.Throws<ArgumentException>(() => view.ReflectionProbe(7, new Vector3(1f, 1.6f, -1f), BoxMin, BoxMax));

        Assert.Equal("key", error.ParamName);
        Assert.Equal(
            "Reflection probe 7 was already placed this frame. Give each probe its own key. (Parameter 'key')", error.Message);
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
        schedule.Asked(probe, 10, slot: 0);

        // Due, but the renderer threw before Asked, as with a sixty-fifth probe.
        Assert.True(schedule.ShouldCapture(probe, 11, waiting: false));
        Assert.True(schedule.ShouldCapture(probe, 11, waiting: false));
        Assert.True(schedule.ShouldCapture(probe with { Key = 2 }, 10, waiting: false));
        Assert.Equal(-1, schedule.SlotOf(2));
    }

    [Fact]
    public void A_probe_on_request_is_captured_again_for_a_new_version_and_not_for_new_light()
    {
        var schedule = new ReflectionProbeSchedule();
        var probe = new ReflectionProbeRequest(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, 0.5f, Update: ProbeUpdate.OnRequest);

        Assert.True(CaptureIfDue(schedule, probe, hash: 10));
        Assert.False(CaptureIfDue(schedule, probe, hash: 11));
        Assert.True(CaptureIfDue(schedule, probe with { Version = 1 }, hash: 11));
        Assert.False(CaptureIfDue(schedule, probe with { Version = 1 }, hash: 12));
    }

    [Fact]
    public void A_probe_always_updated_is_asked_for_again_once_its_last_picture_is_drawn()
    {
        var schedule = new ReflectionProbeSchedule();
        var probe = new ReflectionProbeRequest(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, 0.5f, Update: ProbeUpdate.Always);
        schedule.Asked(probe, 10, slot: 0);

        Assert.False(schedule.ShouldCapture(probe, 10, waiting: true));
        Assert.True(schedule.ShouldCapture(probe, 10, waiting: false));
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
        Assert.NotEqual(before, Renderer.ReflectionLightingHash(sunTurned, environmentVersion: 1));
    }

    [Fact]
    public void The_lighting_hash_changes_with_what_a_capture_draws_and_not_with_what_only_the_screen_shows()
    {
        var sun = new[] { new DirectionalLight { Direction = Vector3.UnitY, Radiance = new Vector3(3f), CastsShadow = true } };
        var view = new FrameView { Lights = sun, EnvironmentIntensity = 1f };
        view.Shadows.Radius = 12f;
        int before = Renderer.ReflectionLightingHash(view, environmentVersion: 1);

        var brighter = view;
        brighter.Lights = new[] { sun[0] with { Radiance = new Vector3(4f) } };
        var dimSky = view;
        dimSky.EnvironmentIntensity = 0.5f;
        var clearSky = view;
        clearSky.Sky = new AnalyticSky { Turbidity = 2.5f };
        var noLightShadows = view;
        noLightShadows.Off = RenderEffects.LightShadows;
        var noLocalLights = view;
        noLocalLights.Off = RenderEffects.LocalLights;
        var noOcclusion = view;
        noOcclusion.Off = RenderEffects.AmbientOcclusion;
        var noContact = view;
        noContact.Off = RenderEffects.ContactShadows;
        var noMultiscatter = view;
        noMultiscatter.Off = RenderEffects.MultiscatterSpecular;
        var noWear = view;
        noWear.Off = RenderEffects.WearAndDirt;

        Assert.NotEqual(before, Renderer.ReflectionLightingHash(brighter, 1));
        Assert.NotEqual(before, Renderer.ReflectionLightingHash(dimSky, 1));
        Assert.NotEqual(before, Renderer.ReflectionLightingHash(view, 2));
        Assert.NotEqual(before, Renderer.ReflectionLightingHash(clearSky, 1));
        Assert.NotEqual(before, Renderer.ReflectionLightingHash(noLightShadows, 1));
        Assert.NotEqual(before, Renderer.ReflectionLightingHash(noLocalLights, 1));
        Assert.Equal(before, Renderer.ReflectionLightingHash(noOcclusion, 1));
        Assert.Equal(before, Renderer.ReflectionLightingHash(noContact, 1));
        Assert.Equal(before, Renderer.ReflectionLightingHash(noMultiscatter, 1));
        Assert.Equal(before, Renderer.ReflectionLightingHash(noWear, 1));
    }

    [Fact]
    public void The_lights_hash_follows_a_lamp_that_reaches_the_box_but_not_its_flicker_its_sway_or_one_out_of_reach()
    {
        // The lamp is 1 m outside the box's -x side, with a range of 2 m, so it reaches in. The far one is 20 m off.
        var lamp = new LocalLight { Key = 1, Position = new Vector3(-4f, 2f, 0f), Range = 2f, Radiance = new Vector3(5f) };
        var far = new LocalLight { Key = 2, Position = new Vector3(-24f, 2f, 0f), Range = 2f, Radiance = new Vector3(5f) };
        int dark = Renderer.ReflectionLightsHash([], BoxMin, BoxMax);
        int lit = Renderer.ReflectionLightsHash([lamp], BoxMin, BoxMax);

        Assert.NotEqual(dark, lit);
        Assert.NotEqual(lit, Renderer.ReflectionLightsHash([lamp with { Range = 3f }], BoxMin, BoxMax));
        Assert.NotEqual(lit, Renderer.ReflectionLightsHash([lamp with { CastsShadow = true }], BoxMin, BoxMax));
        Assert.Equal(lit, Renderer.ReflectionLightsHash([lamp with { Radiance = new Vector3(4f) }], BoxMin, BoxMax));
        Assert.Equal(lit, Renderer.ReflectionLightsHash([lamp with { Position = new Vector3(-4.5f, 2.1f, 0.1f) }], BoxMin, BoxMax));
        Assert.Equal(lit, Renderer.ReflectionLightsHash([lamp, far], BoxMin, BoxMax));

        // Swung out of reach, it's gone as far as the box can tell.
        Assert.Equal(dark, Renderer.ReflectionLightsHash([lamp with { Position = new Vector3(-6f, 2f, 0f) }], BoxMin, BoxMax));
    }

    private static bool CaptureIfDue(ReflectionProbeSchedule schedule, in ReflectionProbeRequest request, int hash)
    {
        if (!schedule.ShouldCapture(request, hash, waiting: false))
        {
            return false;
        }

        schedule.Asked(request, hash, slot: request.Key);
        return true;
    }

    [Fact]
    public void Each_key_keeps_the_slot_the_renderer_gave_it()
    {
        var schedule = new ReflectionProbeSchedule();
        var first = new ReflectionProbeRequest(5, new Vector3(1f, 1.6f, 0f), BoxMin, BoxMax, 0.5f);
        var second = new ReflectionProbeRequest(2, new Vector3(2f, 1.6f, 0f), BoxMin, BoxMax, 0.5f);

        schedule.Asked(first, 1, slot: 3);
        schedule.Asked(second, 1, slot: 0);
        schedule.Asked(first with { Blend = 1f }, 1, slot: 3);

        Assert.Equal(3, schedule.SlotOf(5));
        Assert.Equal(0, schedule.SlotOf(2));
        Assert.Equal(-1, schedule.SlotOf(9));
    }

    [Fact]
    public void A_probe_no_longer_asked_for_is_forgotten_alone_and_the_rest_keep_their_pictures()
    {
        var schedule = new ReflectionProbeSchedule();
        var first = new ReflectionProbeRequest(1, new Vector3(1f, 1.6f, 0f), BoxMin, BoxMax, 0.5f);
        var second = new ReflectionProbeRequest(2, new Vector3(2f, 1.6f, 0f), BoxMin, BoxMax, 0.5f);
        schedule.Asked(first, 1, slot: 4);
        schedule.Asked(second, 1, slot: 6);

        Assert.Empty(schedule.Forget([first, second]));
        Assert.Equal(new[] { 4 }, schedule.Forget([second]));

        Assert.Equal(-1, schedule.SlotOf(1));
        Assert.Equal(6, schedule.SlotOf(2));
        Assert.False(schedule.ShouldCapture(second, 1, waiting: false));
        Assert.True(schedule.ShouldCapture(first, 1, waiting: false));
    }

    [Fact]
    public void With_reflections_off_no_probe_is_wanted()
    {
        var view = new View();
        view.ReflectionProbe(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax);

        Assert.Equal(1, ReflectionProbeUpdater.WantedReflectionProbes(view, RenderEffects.Bloom).Length);
        Assert.True(ReflectionProbeUpdater.WantedReflectionProbes(view, RenderEffects.Reflections | RenderEffects.Bloom).IsEmpty);
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

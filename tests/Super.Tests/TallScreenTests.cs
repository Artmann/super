using System.Numerics;

namespace Super.Tests;

/// <summary>A camera with <see cref="Camera.WideAs"/> keeps its sides on a screen held upright.</summary>
public class TallScreenTests
{
    private const float Widescreen = 16f / 9f;
    private static readonly Vector2 Wide = new(1280f, 720f);
    private static readonly Vector2 Tall = new(720f, 1280f);

    /// <summary>Where the renderer's own matrices put a point on this screen, in pixels.</summary>
    private static Vector2 Project(in Camera camera, Vector3 point, Vector2 screen)
    {
        var view = Matrix4x4.CreateLookAt(camera.Position, camera.Target, camera.Up);
        var clip = Vector4.Transform(new Vector4(point, 1f), view * camera.Matrix(screen.X / screen.Y));
        return new Vector2((clip.X / clip.W + 1f) * 0.5f * screen.X, (clip.Y / clip.W + 1f) * 0.5f * screen.Y);
    }

    private static void AssertNear(Vector2 expected, Vector2 actual, float within = 0.01f)
    {
        Assert.True(Vector2.Distance(expected, actual) < within, $"Expected {expected}, got {actual}");
    }

    // A quarter turn of view from 10 units back shows 10 units above and below the middle,
    // so a 16:9 screen shows 160/9 units either side.
    private static readonly Camera Square = new(new Vector3(0f, 0f, 10f), Vector3.Zero, MathF.PI / 2f);
    private static readonly Vector3 WideEdge = new(160f / 9f, 0f, 0f);

    [Fact]
    public void A_tall_screen_shows_as_much_from_side_to_side_as_a_widescreen_one()
    {
        var camera = Square with { WideAs = Widescreen };

        AssertNear(new Vector2(1280f, 360f), Project(camera, WideEdge, Wide));
        AssertNear(new Vector2(720f, 640f), Project(camera, WideEdge, Tall));
    }

    [Fact]
    public void Without_wide_as_a_tall_screen_loses_its_sides()
    {
        // Only 10 * 720/1280 = 5.625 units either side show, so the widescreen edge is
        // (160/9) / 5.625 = 3.16 half screens right of the middle, far off the screen.
        AssertNear(new Vector2((1280f / 405f + 1f) * 360f, 640f), Project(Square, WideEdge, Tall));
    }

    [Fact]
    public void A_screen_wider_than_wide_as_keeps_the_field_of_view()
    {
        var camera = Square with { WideAs = 4f / 3f };

        AssertNear(new Vector2(640f, 0f), Project(camera, new Vector3(0f, 10f, 0f), Wide));
    }

    [Fact]
    public void An_orthographic_camera_keeps_its_width_on_a_tall_screen()
    {
        // Size 4 shows 2 units above the middle, so 16:9 shows 32/9 units either side.
        var camera = Camera.Orthographic(new Vector3(0f, 0f, 10f), Vector3.Zero, 4f) with { WideAs = Widescreen };

        AssertNear(new Vector2(720f, 640f), Project(camera, new Vector3(32f / 9f, 0f, 0f), Tall));
    }

    public static TheoryData<Camera> Cameras() =>
    [
        new Camera(new Vector3(3f, 6f, 9f), Vector3.Zero) { WideAs = Widescreen },
        new Camera(new Vector3(3f, 6f, 9f), Vector3.Zero) { WideAs = Widescreen, Shift = new Vector2(0.4f, -0.25f) },
        Camera.Orthographic(new Vector3(3f, 6f, 9f), Vector3.Zero, 12f) with { WideAs = Widescreen },
    ];

    [Theory]
    [MemberData(nameof(Cameras))]
    public void A_ray_on_a_tall_screen_runs_through_what_is_drawn_at_that_pixel(Camera camera)
    {
        foreach (var pixel in new[] { new Vector2(60f, 90f), new Vector2(650f, 1200f), Tall / 2f })
        {
            var ray = camera.Ray(pixel, Tall);
            AssertNear(pixel, Project(camera, ray.Origin + ray.Direction * 7f, Tall));
            AssertNear(pixel, Project(camera, ray.Origin + ray.Direction * 25f, Tall));
        }
    }

    [Fact]
    public void An_orthographic_camera_casts_shadows_over_all_it_shows_on_a_tall_screen()
    {
        var camera = Camera.Orthographic(new Vector3(0f, 10f, 0f), Vector3.Zero, 9f) with { WideAs = Widescreen };

        // 9 units high on 16:9 is 16 across, and a 9:16 screen as wide shows 256/9 from top to bottom.
        Assert.Equal(256f / 9f, camera.ShadowRadius(1f, Tall.X / Tall.Y), 0.001f);
    }

    [Fact]
    public void Every_camera_rig_hands_its_wide_as_to_the_camera()
    {
        var orbit = new OrbitCamera { WideAs = Widescreen };
        orbit.Update(Vector3.Zero);
        var follow = new FollowCamera { WideAs = Widescreen };
        follow.Update(Vector3.Zero, 1f / 30f);
        var boom = new BoomCamera { WideAs = Widescreen };
        boom.Update(Vector3.Zero, 1f / 30f);
        var eyes = new FirstPersonCamera { WideAs = Widescreen };
        eyes.Update(Vector3.Zero);
        var overhead = new TopDownCamera { WideAs = Widescreen };
        overhead.Update(1f / 30f);

        Assert.All(
            new[] { orbit.Current, follow.Current, boom.Current, eyes.Current, overhead.Current },
            camera => Assert.Equal(Widescreen, camera.WideAs));
    }
}

using System.Numerics;
using Rendition;

namespace Super.Tests;

public class CameraKindsTests
{
    private static readonly Vector2 Screen = new(1280f, 720f);

    /// <summary>Where the renderer's own matrices put a point, in pixels and depth.</summary>
    private static Vector3 Project(in Camera camera, Vector3 point)
    {
        var view = Matrix4x4.CreateLookAt(camera.Position, camera.Target, camera.Up);
        var clip = Vector4.Transform(new Vector4(point, 1f), view * camera.Matrix(Screen.X / Screen.Y));
        return new Vector3(
            (clip.X / clip.W + 1f) * 0.5f * Screen.X,
            (clip.Y / clip.W + 1f) * 0.5f * Screen.Y,
            clip.Z / clip.W);
    }

    private static void AssertNear(Vector2 expected, Vector3 actual, float within = 0.01f)
    {
        var flat = new Vector2(actual.X, actual.Y);
        Assert.True(Vector2.Distance(expected, flat) < within, $"Expected {expected}, got {flat}");
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float within = 0.001f)
    {
        Assert.True(Vector3.Distance(expected, actual) < within, $"Expected {expected}, got {actual}");
    }

    public static TheoryData<Camera> Cameras() =>
    [
        new Camera(new Vector3(3f, 6f, 9f), Vector3.Zero),
        new Camera(new Vector3(3f, 6f, 9f), Vector3.Zero) { Shift = new Vector2(0.4f, -0.25f) },
        new Camera(new Vector3(3f, 2f, -4f), Vector3.Zero),
        new Camera(new Vector3(0f, 10f, 0f), Vector3.Zero),
        new Camera(new Vector3(0f, -10f, 0f), Vector3.Zero),
        Camera.Orthographic(new Vector3(3f, 6f, 9f), Vector3.Zero, 12f),
        Camera.Orthographic(new Vector3(3f, 6f, 9f), Vector3.Zero, 12f) with { Shift = new Vector2(-0.5f, 0.3f) },
        Camera.Orthographic(new Vector3(0f, 30f, 0f), Vector3.Zero, 20f),
    ];

    [Theory]
    [MemberData(nameof(Cameras))]
    public void A_ray_runs_through_what_is_drawn_at_that_pixel(Camera camera)
    {
        foreach (var pixel in new[] { new Vector2(100f, 80f), new Vector2(1100f, 650f), Screen / 2f })
        {
            var ray = camera.Ray(pixel, Screen);
            AssertNear(pixel, Project(camera, ray.Origin + ray.Direction * 7f));
            AssertNear(pixel, Project(camera, ray.Origin + ray.Direction * 25f));
        }
    }

    [Fact]
    public void An_orthographic_camera_shows_its_size_from_bottom_to_top()
    {
        var camera = Camera.Orthographic(new Vector3(0f, 0f, 10f), Vector3.Zero, 4f);
        AssertNear(new Vector2(640f, 0f), Project(camera, new Vector3(0f, 2f, 0f)));
        AssertNear(new Vector2(640f, 720f), Project(camera, new Vector3(0f, -2f, -30f)));
    }

    [Fact]
    public void Orthographic_rays_run_side_by_side()
    {
        var camera = Camera.Orthographic(new Vector3(0f, 10f, 10f), Vector3.Zero, 8f);
        var left = camera.Ray(new Vector2(0f, 300f), Screen);
        var right = camera.Ray(new Vector2(1280f, 500f), Screen);
        AssertNear(left.Direction, right.Direction);
        Assert.NotEqual(left.Origin, right.Origin);
    }

    [Fact]
    public void Orthographic_depth_is_reversed_and_ends_at_the_far_edge()
    {
        var camera = Camera.Orthographic(new Vector3(0f, 0f, 100f), Vector3.Zero, 8f);
        float close = Project(camera, new Vector3(0f, 0f, 99f)).Z;
        float away = Project(camera, new Vector3(0f, 0f, -100f)).Z;
        float end = Project(camera, new Vector3(0f, 0f, 100f - Camera.OrthographicReach)).Z;
        Assert.True(close > away);
        Assert.True(away > 0f);
        Assert.InRange(end, -0.0001f, 0.0001f);
    }

    [Fact]
    public void A_shift_moves_the_target_across_the_screen()
    {
        var camera = new Camera(new Vector3(0f, 2f, 9f), Vector3.Zero) { Shift = new Vector2(0.5f, 0.5f) };
        AssertNear(new Vector2(960f, 180f), Project(camera, camera.Target));
    }

    [Fact]
    public void Blending_moves_size_and_shift_too()
    {
        var from = Camera.Orthographic(Vector3.UnitZ, Vector3.Zero, 10f) with { Shift = Vector2.Zero };
        var to = Camera.Orthographic(Vector3.UnitZ, Vector3.Zero, 20f) with { Shift = Vector2.UnitX };
        var middle = Camera.Lerp(from, to, 0.5f);
        Assert.Equal(15f, middle.Size);
        Assert.Equal(new Vector2(0.5f, 0f), middle.Shift);
    }

    [Fact]
    public void Blending_between_kinds_takes_the_nearer_one()
    {
        var flat = Camera.Orthographic(Vector3.UnitZ, Vector3.Zero, 10f);
        var deep = new Camera(Vector3.UnitZ, Vector3.Zero);
        Assert.True(Camera.Lerp(flat, deep, 0.25f).IsOrthographic);
        Assert.False(Camera.Lerp(flat, deep, 0.75f).IsOrthographic);
    }

    [Fact]
    public void A_perspective_eye_is_the_camera()
    {
        var camera = new Camera(new Vector3(1f, 2f, 3f), Vector3.Zero);
        Assert.Equal(camera.Position, camera.Eye);
    }

    [Fact]
    public void An_orthographic_eye_sits_far_behind_so_shading_sees_nearly_parallel_rays()
    {
        var camera = Camera.Orthographic(new Vector3(0f, 0f, 10f), Vector3.Zero, 20f);
        var middle = Vector3.Normalize(camera.Eye);
        var edge = Vector3.Normalize(camera.Eye - new Vector3(20f, 0f, 0f));
        Assert.True(Vector3.Dot(middle, Vector3.UnitZ) > 0.9999f);
        Assert.True(Vector3.Dot(middle, edge) > 0.9999f);
    }

    [Fact]
    public void Shadows_reach_across_an_orthographic_view()
    {
        var wide = Camera.Orthographic(Vector3.UnitZ, Vector3.Zero, 40f);
        Assert.True(wide.ShadowRadius(8f, 16f / 9f) >= 40f);
        Assert.Equal(100f, wide.ShadowRadius(100f, 16f / 9f));
        Assert.Equal(8f, new Camera(Vector3.UnitZ, Vector3.Zero).ShadowRadius(8f, 16f / 9f));
    }

    [Fact]
    public void The_view_keeps_the_whole_camera()
    {
        var view = new View();
        view.Begin(0f);
        var camera = Camera.Orthographic(Vector3.UnitY, Vector3.UnitX, 5f) with { Shift = Vector2.One };
        view.Camera(camera);
        Assert.Equal(camera, view.Shot);
    }

    private static readonly Matrix4x4 Parallel =
        Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 100f), Vector3.Zero, Vector3.UnitY)
        * Projection.OrthographicReversedZ(20f, 1f, 0.1f, 1000f);

    private static int Seen(Vector3 position)
    {
        var view = new DetailView(Parallel, new Vector3(0f, 0f, 100f), 0f, 36f, 1f);
        DrawItem[] draws = [new() { IndexCount = 3, Transform = Matrix4x4.CreateTranslation(position), Detail = new DetailHandle(1) }];
        List<PartDetail> details = [default, new PartDetail(Vector3.Zero, 1f, [])];
        return DrawSelection.Select(view, draws, details, new DrawItem[1]);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 1)]
    [InlineData(9f, 0f, -500f, 1)]
    [InlineData(30f, 0f, 0f, 0)]
    [InlineData(0f, -30f, 0f, 0)]
    [InlineData(0f, 0f, 150f, 0)]
    public void Orthographic_culling_keeps_what_is_in_the_box(float x, float y, float z, int expected)
    {
        Assert.Equal(expected, Seen(new Vector3(x, y, z)));
    }

    private static DrawItem Chosen(Vector3 position)
    {
        var view = new DetailView(Parallel, new Vector3(0f, 0f, 100f), 0f, 36f, 1f);
        DrawItem[] draws = [new() { IndexCount = 300, Transform = Matrix4x4.CreateTranslation(position), Detail = new DetailHandle(1) }];
        List<PartDetail> details =
        [
            default,
            new PartDetail(Vector3.Zero, 1f, [new DetailLevel(300, 100, 0.01f), new DetailLevel(400, 30, 0.05f)]),
        ];
        var selected = new DrawItem[1];
        Assert.Equal(1, DrawSelection.Select(view, draws, details, selected));
        return selected[0];
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-500f)]
    public void Orthographic_models_get_the_same_detail_at_any_depth(float z)
    {
        // At 36 pixels a unit, an error of 0.01 shows as 0.36 pixels and 0.05 as 1.8, over the limit of 1.
        var item = Chosen(new Vector3(0f, 0f, z));

        Assert.Equal(300u, item.FirstIndex);
        Assert.Equal(100u, item.IndexCount);
    }

    [Fact]
    public void Pixel_scale_is_per_unit_for_a_parallel_view_and_per_distance_for_a_perspective_one()
    {
        var projection = Projection.OrthographicReversedZ(20f, 1f, 0.1f, 1000f);
        var (atOneUnit, perUnit) = Projection.PixelScale(projection, 720);
        Assert.Equal(0f, atOneUnit);
        Assert.Equal(36f, perUnit, 3);

        var deep = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 2f, 1f, 0.1f);
        (atOneUnit, perUnit) = Projection.PixelScale(deep, 720);
        Assert.Equal(360f, atOneUnit, 2);
        Assert.Equal(0f, perUnit);
    }
}

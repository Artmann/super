using System.Numerics;
using static Rendition.Gpu.Tests.TestPixels;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// A red glowing panel hangs above a smooth metal floor and out of the camera's view: the camera looks down at the floor
// and never sees the panel itself, so the screen's reflections can't show it, and the floor reflects the grey studio.
// Only the planar reflection, drawn from a camera mirrored under the floor, sees the panel, so with it on the floor
// shows it red, and with it off the floor stays grey.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class PlanarReflectionGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 256;

    // The camera stands 1.5 m over the floor looking down at it. The panel's reflection, worked out by hand along the
    // line from the mirrored camera to the panel, lies on the floor about 0.4 m in front of the origin, which is a little
    // below the middle of the picture.
    private static readonly Vector3 Eye = new(0f, 1.5f, 2f);
    private static readonly Vector3 Target = new(0f, 0f, -1f);
    private const float ReflectionDown = 0.6f;

    private const int Frames = 3;

    // In the post's picture the post stands in the middle and its reflection runs down from its foot, a little below
    // the middle of the picture. Beside is a little to one side of the reflection's sharp width.
    private const float Beside = 0.63f;
    private const float NearFoot = 0.47f;
    private const float FurtherDown = 0.85f;

    [Fact]
    public void A_glowing_panel_out_of_view_shows_in_the_floor_only_with_planar_reflections_on()
    {
        WithPanel(draws =>
        {
            var on = Render(RenderEffects.None, draws);
            var off = Render(RenderEffects.PlanarReflections, draws);
            var reflectionOn = Around(on, Size, 0.5f, ReflectionDown);
            var reflectionOff = Around(off, Size, 0.5f, ReflectionDown);
            var topOn = Around(on, Size, 0.5f, 0.05f);
            output.WriteLine($"Reflection: planar on {Show(reflectionOn)}, off {Show(reflectionOff)}; top of the picture {Show(topOn)}.");

            Assert.True(topOn.X - topOn.Y < 10f, $"The top of the picture was {Show(topOn)}, so the camera sees the panel itself.");
            Assert.True(
                reflectionOn.X - reflectionOn.Y > 60f,
                $"The floor under the panel's reflection was {Show(reflectionOn)} with planar reflections on, which is not the panel's red.");
            Assert.True(
                MathF.Abs(reflectionOff.X - reflectionOff.Y) < 10f,
                $"With planar reflections off the floor was {Show(reflectionOff)}, where it should reflect the grey studio.");
        });
    }

    // A thin glowing post 3 m tall stands on a rough metal floor. Its reflection near the foot comes from rays that
    // barely leave the floor, so it stays as narrow as the post; further down the picture it shows the post's upper
    // part, which the rays reach after going far past the floor, so it spreads out to the side.
    [Fact]
    public void A_post_on_a_rough_floor_reflects_sharp_at_its_foot_and_blurred_up_its_height()
    {
        WithPost(draws =>
        {
            // The screen's own reflections are off, so only the planar reflection shows the post.
            var on = Render(RenderEffects.Reflections, draws);
            var besideFoot = Around(on, Size, Beside, NearFoot);
            var besideLower = Around(on, Size, Beside, FurtherDown);
            var underFoot = Around(on, Size, 0.5f, NearFoot);
            output.WriteLine($"Beside the reflection: near the foot {Show(besideFoot)}, further down {Show(besideLower)}; under the foot {Show(underFoot)}.");

            Assert.True(underFoot.X - underFoot.Y > 60f, $"The post's reflection at its foot was {Show(underFoot)}, which is not the post's red.");
            Assert.True(
                besideFoot.X - besideFoot.Y < 10f,
                $"Beside the reflection near the post's foot the floor was {Show(besideFoot)}: the blur reaches out where the post touches the floor.");
            Assert.True(
                besideLower.X - besideLower.Y > 25f,
                $"Beside the reflection further from the floor the floor was {Show(besideLower)}: the blur doesn't widen with the distance to the plane.");
        });
    }

    // The same panel through an orthographic camera 4 m high. Its rays run parallel, so the panel's reflection, worked
    // out by hand along them, lies on the floor 0.4 m in front of the origin, 0.63 m below the middle of the picture.
    [Fact]
    public void An_orthographic_camera_sees_the_panel_out_of_view_in_the_floor()
    {
        WithPanel(draws =>
        {
            var lens = Projection.OrthographicReversedZ(4f, 1f, 0.1f, 30f);
            var on = Render(RenderEffects.None, draws, lens);
            var off = Render(RenderEffects.PlanarReflections, draws, lens);
            var reflectionOn = Around(on, Size, 0.5f, 0.66f);
            var reflectionOff = Around(off, Size, 0.5f, 0.66f);
            var topOn = Around(on, Size, 0.5f, 0.05f);
            output.WriteLine($"Reflection: planar on {Show(reflectionOn)}, off {Show(reflectionOff)}; top of the picture {Show(topOn)}.");

            Assert.True(topOn.X - topOn.Y < 10f, $"The top of the picture was {Show(topOn)}, so the camera sees the panel itself.");
            Assert.True(
                reflectionOn.X - reflectionOn.Y > 60f,
                $"The floor under the panel's reflection was {Show(reflectionOn)} with planar reflections on, which is not the panel's red.");
            Assert.True(
                MathF.Abs(reflectionOff.X - reflectionOff.Y) < 10f,
                $"With planar reflections off the floor was {Show(reflectionOff)}, where it should reflect the grey studio.");
        });
    }

    // A smooth floor and, facing the camera 2.2 m up and 4 m behind the origin, a panel above the top of its view.
    private void WithPanel(Action<DrawItem[]> body) =>
        WithScene(0.05f, Matrix4x4.CreateScale(3f, 1.5f, 1f) * Matrix4x4.CreateTranslation(0f, 2.2f, -4f), body);

    // A rough floor and a post 0.2 m wide and 3 m tall standing on it, 1.5 m behind the origin.
    private void WithPost(Action<DrawItem[]> body) =>
        WithScene(0.3f, Matrix4x4.CreateScale(0.2f, 3f, 1f) * Matrix4x4.CreateTranslation(0f, 1.5f, -1.5f), body);

    // Runs the body with a metal floor of the given roughness and a red glowing quad placed by the transform, then
    // destroys them and checks the validation errors did not grow.
    private void WithScene(float floorRoughness, Matrix4x4 glowTransform, Action<DrawItem[]> body)
    {
        int errors = gpu.ValidationErrors;
        var quad = TestMeshes.Quad();
        var metal = gpu.Renderer.Assets.CreateMaterial(new Material { BaseColor = Vector4.One, Roughness = floorRoughness, Metallic = 1f });
        var glow = gpu.Renderer.Assets.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0f, 0f, 0f, 1f),
            Roughness = 1f,
            Emissive = new Vector3(6f, 0.2f, 0.2f),
        });
        var floor = gpu.Renderer.Assets.CreateDrawItem(quad.Vertices, quad.Indices, metal)
            with { Transform = Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateScale(20f) };
        var glowing = gpu.Renderer.Assets.CreateDrawItem(quad.Vertices, quad.Indices, glow) with { Transform = glowTransform };
        try
        {
            body([floor, glowing]);
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(floor.Mesh);
            gpu.Renderer.Assets.DestroyMesh(glowing.Mesh);
            gpu.Renderer.Assets.DestroyMaterial(metal);
            gpu.Renderer.Assets.DestroyMaterial(glow);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // No sun or probes, only the plain grey studio, and none of the effects that change the exposure or spread the
    // glow. Reflections are scaled by the environment's intensity, so it stays at 1.
    private byte[] Render(RenderEffects off, DrawItem[] draws, Matrix4x4? projection = null)
    {
        var view = gpu.View(DebugView.None);
        if (projection is { } lens)
        {
            view.Projection = lens;
        }

        view.View = Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);
        view.CameraPosition = Eye;
        view.Off = off | RenderEffects.AntiAliasing | RenderEffects.LightProbes | RenderEffects.AutoExposure | RenderEffects.Bloom;
        view.Lights = [];
        view.Environment.Intensity = 1f;
        view.Extras = view.Extras with { ReflectionPlane = new ReflectionPlane(0f) };
        for (int frame = 0; frame < Frames; frame++)
        {
            gpu.Renderer.Render(view, draws);
        }

        return gpu.Renderer.Capture(view, draws, out _, out _);
    }
}

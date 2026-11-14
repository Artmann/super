using System.Numerics;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// With every effect off, each effect's GPU pass must run no shaders at all. The GPU counts the shaders each pass runs,
// so the check is exact: a pass that still runs counts every shader, even one that leaves at once. The same scene with
// the effects on must run shaders in that pass, which shows the scene really exercises it.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class EffectsOffGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 256;

    // The counts are read a frame or two late, so a few frames go first.
    private const int Frames = 4;

    // Everything the renderer can switch off. Culling, simpler models and depth-first drawing cost more when off, so
    // they stay on in the off frames.
    private static readonly RenderEffects EverythingOff =
        RenderEffects.All & ~(RenderEffects.Culling | RenderEffects.SimplerModels | RenderEffects.DepthFirst);

    // Dust in front of the panel, for the particle pass. The scene has no glass, so with particles off that pass is empty.
    private static readonly Particle[] Dust = BuildDust();

    [Theory]
    [InlineData("Shadows")]
    [InlineData("Light probes")]
    [InlineData("Volumetric fog")]
    [InlineData("Ambient occlusion")]
    [InlineData("AO blur")]
    [InlineData("Reflections")]
    [InlineData("Anti-aliasing")]
    [InlineData("Depth of field")]
    [InlineData("Bloom")]
    [InlineData("Subsurface")]
    [InlineData("Light clusters")]
    [InlineData("Glass and particles")]
    public void Every_effect_off_runs_no_shaders_in_its_pass(string pass)
    {
        var (on, off) = MeasureOnAndOff();

        Assert.True(on.Passes.ContainsKey(pass), $"The GPU timer has no pass called {pass}.");
        Assert.True(on.Passes[pass].Shaders > 0, $"{pass} ran no shaders with every effect on, so the scene doesn't exercise it.");
        Assert.True(off.Passes[pass].Shaders == 0, $"{pass} ran {Show(off.Passes[pass])} with every effect off.");
    }

    [Fact]
    public void Every_effect_off_draws_no_shadow_triangles()
    {
        var (on, off) = MeasureOnAndOff();

        Assert.True(on.ShadowTriangles > 0, $"The scene drew {on.ShadowTriangles} shadow triangles with every effect on.");
        Assert.True(off.ShadowTriangles == 0, $"The scene drew {off.ShadowTriangles} shadow triangles with every effect off.");
    }

    // Measures the scene with every effect on and with everything off. The scene is destroyed and the validation
    // error count checked before the caller asserts anything.
    private (Measurement On, Measurement Off) MeasureOnAndOff()
    {
        int errors = gpu.ValidationErrors;
        var scene = BuildScene();
        Measurement on;
        Measurement off;
        try
        {
            on = Measure(scene, RenderEffects.None);
            off = Measure(scene, EverythingOff);
        }
        finally
        {
            Destroy(scene);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
        return (on, off);
    }

    // Renders a few frames of the scene with the effects given off, then reads the shaders the newest finished frame ran.
    private Measurement Measure(Scene scene, RenderEffects off)
    {
        gpu.Renderer.MeasureGpu = true;
        try
        {
            for (int frame = 0; frame < Frames; frame++)
            {
                gpu.Renderer.Render(View(scene, off), scene.Draws);
            }

            var passes = new Dictionary<string, GpuPassWork>();
            foreach (var work in gpu.Renderer.GpuWork)
            {
                passes[work.Name] = work;
                output.WriteLine($"{work.Name,-20} {Show(work)}  (effects {(off == RenderEffects.None ? "on" : "off")})");
            }

            Assert.True(passes.Count > 0, "This GPU can't count the shaders its passes run, so these passes can't be checked.");
            return new Measurement(passes, gpu.Renderer.DrawnTriangles.Shadows);
        }
        finally
        {
            gpu.Renderer.MeasureGpu = false;
        }
    }

    private static string Show(GpuPassWork work) =>
        $"{work.VertexShaders} vertex, {work.FragmentShaders} fragment and {work.ComputeShaders} compute shaders";

    // Everything the effects can act on: a floor that gets puddles, a swaying panel, a cube and a glowing quad for bloom
    // and reflections, a decal on the floor, a sun and a point light that cast shadows, fog, a light probe box, depth of
    // field and auto exposure. Also a skin-like quad for subsurface, the dust for particles, and the point light's
    // clusters.
    private Scene BuildScene()
    {
        var renderer = gpu.Renderer;
        var quad = TestMeshes.Quad();
        var cube = TestMeshes.Cube();

        var floorMaterial = renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0.6f, 0.6f, 0.6f, 1f), Roughness = 0.4f, Flags = MaterialFlags.Puddles,
        });
        var panelMaterial = renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0.2f, 0.6f, 0.2f, 1f), Roughness = 0.8f, Wind = 1f,
        });
        var glowMaterial = renderer.CreateMaterial(new Material
        {
            BaseColor = Vector4.One, Roughness = 1f, Emissive = new Vector3(4f),
        });
        var cubeMaterial = renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0.8f, 0.5f, 0.3f, 1f), Roughness = 0.3f, Metallic = 1f,
        });
        var decalMaterial = renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0.1f, 0.05f, 0.02f, 1f), Roughness = 1f,
        });
        var skinMaterial = renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0.85f, 0.6f, 0.48f, 1f),
            Roughness = 0.5f,
            Subsurface = new Subsurface { Radius = 0.01f, Color = new Vector3(1f, 0.4f, 0.25f), Transmission = 0.3f },
        });

        DrawItem Place((Vertex[] Vertices, uint[] Indices) mesh, MaterialHandle material, Matrix4x4 transform) =>
            renderer.CreateDrawItem(mesh.Vertices, mesh.Indices, material) with { Transform = transform };

        DrawItem[] draws =
        [
            Place(quad, floorMaterial, Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateScale(8f) * Matrix4x4.CreateTranslation(0f, -1f, 0f)),
            Place(quad, panelMaterial, Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(0f, 0f, -1.5f)),
            Place(quad, glowMaterial, Matrix4x4.CreateScale(0.3f) * Matrix4x4.CreateTranslation(0.7f, 0.2f, 0.3f)),
            Place(cube, cubeMaterial, Matrix4x4.CreateTranslation(-0.6f, -0.5f, 0.4f)),
            Place(quad, skinMaterial, Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateTranslation(-0.9f, 0.1f, 0.9f)),
        ];

        return new Scene(
            draws,
            [floorMaterial, panelMaterial, glowMaterial, cubeMaterial, decalMaterial, skinMaterial],
            decalMaterial);
    }

    private FrameView View(Scene scene, RenderEffects off)
    {
        DirectionalLight[] sun =
        [
            new() { Direction = Vector3.Normalize(new Vector3(0.4f, 1f, 0.6f)), Radiance = Vector3.One, CastsShadow = true },
        ];
        LocalLight[] point =
        [
            new()
            {
                Key = 1,
                Position = new Vector3(-0.2f, 0.8f, 0.8f),
                Radiance = new Vector3(5f),
                Range = 6f,
                CastsShadow = true,
            },
        ];
        DecalDraw[] decals =
        [
            new(Matrix4x4.CreateScale(1.2f, 1f, 1.2f) * Matrix4x4.CreateTranslation(0.3f, -0.9f, 0.3f), scene.DecalMaterial, new DecalBlend()),
        ];

        return new FrameView
        {
            Width = Size,
            Height = Size,
            View = Matrix4x4.CreateLookAt(new Vector3(0f, 0.5f, 3f), new Vector3(0f, -0.2f, 0f), Vector3.UnitY),
            Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
            CameraPosition = new Vector3(0f, 0.5f, 3f),
            Lights = sun,
            LocalLights = point,
            Shadows = new ShadowSettings { Center = Vector3.Zero, Radius = 4f, Cascades = 4 },
            LightProbes = new ProbeVolumeSettings(new Vector3(-2f, -1.5f, -2f), new Vector3(2f, 2f, 2f), 1f),
            Fog = new FogSettings(Density: 0.1f),
            Post = new PostSettings
            {
                Bloom = 0.1f,
                AutoExposure = true,
                Aperture = 2.8f,
                FocalLength = 50f,
                FocusDistance = 2f,
            },
            Puddles = new PuddleSettings(Level: 0.05f),
            Wind = new WindSettings(Vector3.UnitX, Strength: 0.5f),
            Decals = decals,
            Particles = Dust,
            Off = off,
        };
    }

    // A grid of soft puffs between the cube and the camera.
    private static Particle[] BuildDust()
    {
        var dust = new Particle[48];
        for (int i = 0; i < dust.Length; i++)
        {
            dust[i] = new Particle
            {
                Position = new Vector3(-0.8f + (i % 8) * 0.22f, -0.3f + (i / 8) * 0.18f, 1.2f),
                Size = 0.25f,
                Color = new Vector4(0.9f, 0.9f, 0.9f, 0.8f),
                Variant = i * 0.37f,
            };
        }

        return dust;
    }

    private void Destroy(Scene scene)
    {
        foreach (var draw in scene.Draws)
        {
            gpu.Renderer.DestroyMesh(draw.Mesh);
        }

        foreach (var material in scene.Materials)
        {
            gpu.Renderer.DestroyMaterial(material);
        }
    }

    private sealed record Measurement(Dictionary<string, GpuPassWork> Passes, long ShadowTriangles);

    private sealed record Scene(DrawItem[] Draws, MaterialHandle[] Materials, MaterialHandle DecalMaterial);
}

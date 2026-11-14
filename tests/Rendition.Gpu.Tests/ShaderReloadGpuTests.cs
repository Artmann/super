using System.Numerics;

namespace Rendition.Gpu.Tests;

// A shader saved while the game runs has to show in the next frame, with nothing else rebuilt. The test compiles a
// copy of finish.frag that paints every pixel magenta, puts it where the renderer reads compiled shaders, and puts
// the real one back afterwards.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class ShaderReloadGpuTests(GpuFixture shared) : IDisposable
{
    private const string LastLine = "outColor = vec4(display * display, 1.0);";
    private const string SurfaceName = "reload_test_surface.frag";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rendition-shader-reload-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_shader_compiled_again_shows_in_the_next_frame_and_so_does_the_old_one_put_back()
    {
        string magenta = CompileMagentaFinish();
        string compiled = Path.Combine(ShaderWatch.DefaultFolder, "finish.frag.spv");
        byte[] original = File.ReadAllBytes(compiled);

        using var gpu = new GpuFixture(disableRayTracing: false, liveShaders: true);
        var before = Middle(gpu);
        Assert.False(before is { R: > 200, G: < 50, B: > 200 }, $"The frame reads {before} before any reload, which is already magenta.");

        try
        {
            File.Copy(magenta, compiled, overwrite: true);

            // One pipeline draws with finish.frag, and nothing else is built again.
            Assert.Equal(1, gpu.Renderer.ReloadShaders(new HashSet<string> { "finish.frag" }));
            Assert.Equal((255, 0, 255), Middle(gpu));
        }
        finally
        {
            File.WriteAllBytes(compiled, original);
        }

        Assert.Equal(1, gpu.Renderer.ReloadShaders(new HashSet<string> { "finish.frag" }));
        var after = Middle(gpu);
        int furthest = Math.Max(Math.Abs(after.R - before.R), Math.Max(Math.Abs(after.G - before.G), Math.Abs(after.B - before.B)));
        Assert.True(furthest <= 8, $"The frame reads {after} with the real shader back, not the {before} it started as.");

        Assert.Equal(0, gpu.Renderer.ReloadShaders(new HashSet<string> { "no_such_shader.frag" }));
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void A_named_surface_shader_is_read_again_from_its_file()
    {
        string plain = CompileSurface("plain", "0.0");
        string glowing = CompileSurface("glowing", "50.0");
        string compiled = Path.Combine(ShaderWatch.DefaultFolder, SurfaceName + ".spv");
        File.Copy(plain, compiled, overwrite: true);

        try
        {
            using var gpu = new GpuFixture(disableRayTracing: false, liveShaders: true);
            var shader = gpu.Renderer.CreateSurfaceShader(File.ReadAllBytes(compiled), SurfaceName);
            var material = gpu.Renderer.CreateMaterial(new Material
            {
                BaseColor = new Vector4(0.5f, 0.5f, 0.5f, 1f),
                Roughness = 0.8f,
                Shader = shader,
            });
            var (vertices, indices) = TestMeshes.Quad();
            var quad = new DrawItem
            {
                Mesh = gpu.Renderer.CreateMesh(vertices, indices),
                IndexCount = (uint)indices.Length,
                Material = material,
                Transform = Matrix4x4.Identity,
            };
            var before = Middle(gpu, quad);

            File.Copy(glowing, compiled, overwrite: true);
            int built = gpu.Renderer.ReloadShaders(new HashSet<string> { SurfaceName });

            var after = Middle(gpu, quad);
            Assert.True(built > 0, "No pipeline was built again for the surface shader.");
            Assert.True(after.G > before.G + 100, $"The quad reads {after} after the glowing shader was put in, and read {before} before.");
            Assert.Equal(0, gpu.ValidationErrors);
        }
        finally
        {
            File.Delete(compiled);
        }
    }

    [Fact]
    public void A_renderer_made_without_live_shaders_builds_nothing_again()
    {
        Assert.Equal(0, shared.Renderer.ReloadShaders(new HashSet<string> { "finish.frag" }));
    }

    private static (int R, int G, int B) Middle(GpuFixture gpu, params DrawItem[] draws)
    {
        byte[] pixels = gpu.Renderer.Capture(gpu.View(DebugView.None), draws, out int width, out int height);
        int at = (height / 2 * width + width / 2) * 4;
        return (pixels[at], pixels[at + 1], pixels[at + 2]);
    }

    /// <summary>Compiles finish.frag with its last line painting magenta and returns the compiled file.</summary>
    private string CompileMagentaFinish()
    {
        string text = File.ReadAllText(Path.Combine(RealSources()[3], "finish.frag"));
        Assert.Contains(LastLine, text);
        return Compile("magenta", "finish.frag", text.Replace(LastLine, "outColor = vec4(1.0, 0.0, 1.0, 1.0);"));
    }

    /// <summary>Compiles a surface shader that draws the material with this much green light added to it.</summary>
    private string CompileSurface(string folder, string glow)
    {
        return Compile(
            folder,
            SurfaceName,
            "#version 460\n#extension GL_GOOGLE_include_directive : require\n#include \"surface.glsl\"\n"
            + "void main() {\n    Surface surface = materialSurface();\n"
            + $"    surface.emissive += vec3(0.0, {glow}, 0.0);\n    writeSurface(surface);\n}}\n");
    }

    /// <summary>Compiles a shader in a folder of its own the way a saved one is, and returns the compiled file.</summary>
    private string Compile(string folder, string name, string text)
    {
        string[] real = RealSources();
        string sources = Path.Combine(_root, folder, "src");
        string output = Path.Combine(_root, folder, "out");
        Directory.CreateDirectory(sources);
        Directory.CreateDirectory(output);
        string source = Path.Combine(sources, name);
        File.WriteAllText(source, text);
        File.WriteAllLines(Path.Combine(output, "Test.sources"), [real[0], real[1], real[2], sources]);

        var log = new List<string>();
        using var watch = ShaderWatch.Open(output, log.Add)!;
        watch.Compile([source]);
        Assert.True(watch.TakeChanged().Contains(name), string.Join(Environment.NewLine, log));
        return Path.Combine(output, name + ".spv");
    }

    // Where this build's shaders came from and how they were compiled.
    private static string[] RealSources() => File.ReadAllLines(Path.Combine(ShaderWatch.DefaultFolder, "Rendition.sources"));
}

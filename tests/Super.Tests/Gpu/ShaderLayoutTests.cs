using System.Text.RegularExpressions;
using Rendition.Gpu;

namespace Super.Tests.GpuLayer;

public partial class ShaderLayoutTests
{
    [Fact]
    public void Finds_a_binding_by_the_name_the_shader_gives_it()
    {
        var layout = Load("tonemap.frag");

        Assert.True(layout.TryFind("bloomTexture", out var binding));
        Assert.Equal(new ShaderBinding(1, 1, ResourceKind.ImageWithSampler, 1), binding);
    }

    [Fact]
    public void Finds_buffers_by_their_instance_names()
    {
        var layout = Load("tonemap.frag");

        Assert.True(layout.TryFind("frame", out var frame));
        Assert.True(layout.TryFind("exposure", out var exposure));
        Assert.Equal(new ShaderBinding(0, 0, ResourceKind.UniformBuffer, 1), frame);
        Assert.Equal(new ShaderBinding(2, 0, ResourceKind.StorageBuffer, 1), exposure);
    }

    [Fact]
    public void Finds_storage_images()
    {
        var layout = Load("atmosphere_transmittance.comp");

        Assert.True(layout.TryFind("target", out var target));
        Assert.Equal(new ShaderBinding(0, 0, ResourceKind.StorageImage, 1), target);
    }

    [Fact]
    public void An_array_without_a_size_has_a_count_of_zero()
    {
        var layout = Load("overlay.frag");

        Assert.True(layout.TryFind("textures", out var textures));
        Assert.Equal(new ShaderBinding(1, 0, ResourceKind.ImageWithSampler, 0), textures);
    }

    [Fact]
    public void An_unknown_name_is_not_found()
    {
        var layout = Load("tonemap.frag");

        Assert.False(layout.TryFind("nope", out _));
    }

    [Fact]
    public void The_constants_size_is_the_end_of_the_last_push_constant()
    {
        // ivec2 step at 0 and int scale at 8.
        Assert.Equal(12, Load("ao_blur.frag").ConstantsSize);
        Assert.Equal(0, Load("tonemap.frag").ConstantsSize);
    }

    [Fact]
    public void Every_sampler_the_tonemap_source_declares_is_in_its_layout()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Rendition", "Shaders", "tonemap.frag"));
        var names = SamplerDeclaration().Matches(source).Select(match => match.Groups[1].Value).ToArray();
        var layout = Load("tonemap.frag");

        Assert.Equal(4, names.Length);
        Assert.All(names, name => Assert.True(layout.TryFind(name, out _), $"{name} is not in the layout."));
    }

    [Fact]
    public void Lists_every_binding_once()
    {
        var layout = Load("tonemap.frag");

        string[] names = layout.Bindings.ToArray().Select(named => named.Name).Order().ToArray();

        Assert.Equal(["bloomTexture", "exposure", "flareTexture", "frame", "gradeTable", "hdrTexture"], names);
    }

    [Fact]
    public void Every_shader_the_renderer_ships_has_a_layout_that_reads()
    {
        string[] files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "shaders"), "*.layout.spv");

        Assert.True(files.Length > 50, $"Only {files.Length} layouts were found.");
        Assert.All(files, file => ShaderLayout.FromSpirv(File.ReadAllBytes(file)));
    }

    [Fact]
    public void Something_that_is_not_SPIR_V_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() => ShaderLayout.FromSpirv([1, 2, 3, 4, 5, 6, 7, 8]));

        Assert.Equal("This is not SPIR-V: it does not start with the magic number.", error.Message);
    }

    private static ShaderLayout Load(string shader) =>
        ShaderLayout.FromSpirv(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "shaders", shader + ".layout.spv")));

    [GeneratedRegex(@"uniform\s+sampler\w*\s+(\w+)\s*;")]
    private static partial Regex SamplerDeclaration();

    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "src", "Rendition", "Shaders", "tonemap.frag")))
            {
                return folder.FullName;
            }
        }

        throw new DirectoryNotFoundException("No folder above the tests holds src/Rendition/Shaders/tonemap.frag.");
    }
}

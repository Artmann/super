using System.Numerics;
using Rendition;
using Super.Testing;
using Super.Tests.Ui;

namespace Super.Tests;

public class ModelShaderLoadTests
{
    /// <summary>Cooked content with one model added by its bytes, since models need files on disk to cook.</summary>
    private sealed class WithModel(MemoryContent inner, string model, byte[] bytes) : IContentFiles
    {
        public ContentKind? KindOf(string name) => name == model ? ContentKind.Model : inner.KindOf(name);

        public IReadOnlyList<string>? List(string folder) => inner.List(folder);

        public byte[] Read(string name) => name == model ? bytes : inner.Read(name);

        public IReadOnlyList<string> Parts(string name) => inner.Parts(name);
    }

    private static byte[] ModelWithShader(string texture) => new ModelData
    {
        Vertices = [],
        Indices = [],
        Parts = [],
        Materials = [new ModelMaterial { BaseColor = Vector4.One }],
        MaterialShaders = [new ModelMaterialShader("puddles", [Vector4.One], [texture])],
        Textures = [],
    }.ToBytes();

    private static object Prepare(MemoryContent files, string texture) =>
        SceneRenderer.PrepareModel("models/a.glb", new WithModel(files, "models/a.glb", ModelWithShader(texture)), _ => [1, 2, 3, 4]);

    [Fact]
    public void A_shader_texture_that_isnt_cooked_is_named_when_the_model_loads()
    {
        var files = new MemoryContent().Add("textures/ripples.png", ImageTests.Png(2, 2));

        var problem = Assert.Throws<ContentException>(() => Prepare(files, "textures/ripple.png"));

        Assert.Equal("textures/ripple.png isn't in the cooked content. Check the name, including its case and extension.", problem.Message);
    }

    [Fact]
    public void A_shader_texture_is_read_by_its_cooked_name_with_the_shader()
    {
        var files = new MemoryContent().Add("textures/ripples.png", ImageTests.Png(2, 2));

        var prepared = Assert.IsType<SceneRenderer.PreparedModel>(Prepare(files, "textures/ripples.png"));

        var texture = Assert.Single(prepared.Textures);
        Assert.Equal("textures/ripples.png", texture.Key);
        Assert.Equal(2, texture.Value.Width);
        Assert.Equal([1, 2, 3, 4], prepared.Shaders["puddles"]);
        Assert.Equal("puddles", prepared.Model.ShaderOf(0)!.Name);
    }

    [Fact]
    public void A_model_without_shaders_is_prepared_as_it_was_cooked()
    {
        var files = new WithModel(new MemoryContent(), "models/a.glb", new ModelData
        {
            Vertices = [],
            Indices = [],
            Parts = [],
            Materials = [new ModelMaterial { BaseColor = Vector4.One }],
            Textures = [],
        }.ToBytes());

        var prepared = SceneRenderer.PrepareModel("models/a.glb", files, _ => throw new InvalidOperationException("No shader should be read."));

        Assert.IsType<ModelData>(prepared);
    }
}

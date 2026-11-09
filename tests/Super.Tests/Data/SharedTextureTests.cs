using System.Security.Cryptography;
using Rendition;
using Super.Cook;

namespace Super.Tests;

/// <summary>Models that point to their textures by hash, as the model cooker writes them, read back through cooked content.</summary>
public sealed class SharedTextureTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "super-shared-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static TextureData Texture(byte red) =>
        TextureData.FromPixels(2, 1, TextureKind.Linear, [red, 0, 0, 255, red, 0, 0, 255]);

    private static ModelData Model(params TextureData[] textures) => new()
    {
        Vertices = [],
        Indices = [],
        Parts = [],
        Materials = [],
        Textures = textures,
    };

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Writes a cooked folder with the model as <c>hero.glb</c> and each texture as an object it points to.</summary>
    private CookedFolder Folder(ModelData model, bool listObjects = true)
    {
        var store = new FolderStore(_folder);
        var objects = new List<string>();
        byte[] modelBytes = model.ToBytes(texture =>
        {
            byte[] bytes = texture.ToBytes();
            string hash = Hash(bytes);
            store.Put(hash, bytes);
            objects.Add(hash);
            return hash;
        });
        string modelHash = Hash(modelBytes);
        store.Put(modelHash, modelBytes);
        store.Commit(new ContentManifest("desktop",
            new Dictionary<string, ManifestEntry> { ["hero.glb"] = new(ContentKind.Model, modelHash, modelBytes.Length) },
            listObjects ? objects : null));
        return new CookedFolder(_folder);
    }

    [Fact]
    public void A_model_reads_its_shared_textures_through_the_cooked_folder()
    {
        var model = SceneRenderer.LoadModel("hero.glb", Folder(Model(Texture(10), Texture(200))));

        Assert.Equal(2, model.Textures.Length);
        Assert.Equal(new byte[] { 10, 0, 0, 255, 10, 0, 0, 255 }, model.Textures[0].Mips[0]);
        Assert.Equal(new byte[] { 200, 0, 0, 255, 200, 0, 0, 255 }, model.Textures[1].Mips[0]);
    }

    [Fact]
    public void A_texture_the_manifest_does_not_list_is_not_opened()
    {
        var folder = Folder(Model(Texture(10)), listObjects: false);

        var exception = Assert.Throws<FileNotFoundException>(() => SceneRenderer.LoadModel("hero.glb", folder));

        Assert.Matches("^The cooked content keeps no object [0-9a-f]{64}\\. Cook it again\\.$", exception.Message);
    }

    [Fact]
    public void OpenObject_refuses_a_name_that_is_not_a_hash()
    {
        var folder = Folder(Model(Texture(10)));
        File.WriteAllText(Path.Combine(_folder, "secret.txt"), "no");

        var exception = Assert.Throws<FileNotFoundException>(() => folder.OpenObject("../secret.txt"));

        Assert.Equal("The cooked content keeps no object ../secret.txt. Cook it again.", exception.Message);
    }

    [Fact]
    public void A_model_with_shared_textures_read_without_content_says_how_to_read_it()
    {
        byte[] bytes = Model(Texture(10)).ToBytes(_ => new string('a', 64));

        var exception = Assert.Throws<InvalidDataException>(() => ModelData.Read(new MemoryStream(bytes), "hero.rmodel"));

        Assert.Equal("hero.rmodel keeps its textures in the cooked content beside it, so read it through Content, or cook it with super-cook file.",
            exception.Message);
    }

    [Fact]
    public void Content_that_cannot_open_objects_says_so()
    {
        var files = new ModelOnly(Model(Texture(10)).ToBytes(_ => new string('a', 64)));

        var exception = Assert.Throws<NotSupportedException>(() => SceneRenderer.LoadModel("hero.glb", files));

        Assert.Equal("ModelOnly can't open objects by hash, so it can't read models that share their textures.", exception.Message);
    }

    [Fact]
    public void A_model_with_its_textures_inside_needs_no_objects()
    {
        var model = SceneRenderer.LoadModel("hero.glb", new ModelOnly(Model(Texture(10)).ToBytes()));

        Assert.Equal(new byte[] { 10, 0, 0, 255, 10, 0, 0, 255 }, Assert.Single(model.Textures).Mips[0]);
    }

    [Fact]
    public void A_texture_form_the_reader_does_not_know_is_reported_as_damage()
    {
        byte[] bytes = Model(Texture(10)).ToBytes();
        // The model ends with the texture: its form byte, then its body. A texture file is that body
        // after a 4-byte magic and a 4-byte version.
        int body = Texture(10).ToBytes().Length - 8;
        int form = bytes.Length - body - 1;
        Assert.Equal(0, bytes[form]);
        bytes[form] = 7;

        var exception = Assert.Throws<InvalidDataException>(() => ModelData.Read(new MemoryStream(bytes), "hero.rmodel"));

        Assert.Equal("hero.rmodel is damaged: texture 0 has the unknown form 7. Cook it again.", exception.Message);
    }

    private sealed class ModelOnly(byte[] bytes) : IContentFiles
    {
        public ContentKind? KindOf(string name) => name == "hero.glb" ? ContentKind.Model : null;

        public IReadOnlyList<string>? List(string folder) => null;

        public byte[] Read(string name) => bytes;
    }
}

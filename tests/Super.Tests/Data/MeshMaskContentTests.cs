using Rendition;
using Super.Testing;

namespace Super.Tests;

public class MeshMaskContentTests
{
    /// <summary>Content with one cooked model, whose bytes can be streamed only when <paramref name="streamOnly"/> is set.</summary>
    private sealed class ModelFiles(string name, byte[] bytes, bool streamOnly) : IContentFiles
    {
        public ContentKind? KindOf(string asked) => asked == name ? ContentKind.Model : null;

        public IReadOnlyList<string>? List(string folder) => null;

        public byte[] Read(string asked) => streamOnly ? throw new InvalidOperationException($"{asked} was read whole.") : bytes;

        public Stream Open(string asked) => asked == name ? new MemoryStream(bytes) : throw new FileNotFoundException(asked);
    }

    /// <summary>A model with one part per name, and no geometry, since mesh names need none.</summary>
    private static byte[] Model(params string[] meshNames) => new ModelData
    {
        Vertices = [],
        Indices = [],
        Parts = meshNames.Select(_ => new ModelPart()).ToArray(),
        Materials = [],
        Textures = [],
        MeshNames = meshNames,
    }.ToBytes();

    private static bool[] Shown(MeshMask mask) => Enumerable.Range(0, mask.Count).Select(part => mask.Shows(part)).ToArray();

    [Fact]
    public void Names_pick_the_meshes_with_them()
    {
        var content = new Content(new ModelFiles("hero.glb", Model("Body_03a", "Head_05b", "w_spear"), streamOnly: false));
        var hero = content.Model("hero.glb");

        var mask = content.Meshes(hero, "Head_05b", "w_spear");

        Assert.Equal(3, mask.Count);
        Assert.Equal(2, mask.Kept);
        Assert.Equal([false, true, true], Shown(mask));
    }

    [Fact]
    public void A_repeated_name_keeps_every_part_with_it()
    {
        var content = new Content(new ModelFiles("hero.glb", Model("Hair", "Body", "Hair"), streamOnly: false));
        var hero = content.Model("hero.glb");

        var mask = content.Meshes(hero, "Hair");

        Assert.Equal(2, mask.Kept);
        Assert.Equal([true, false, true], Shown(mask));
    }

    [Fact]
    public void Unknown_name_lists_the_meshes_it_has()
    {
        var content = new Content(new ModelFiles("hero.glb", Model("Body_03a", "Head_05b", "w_spear"), streamOnly: false));
        var hero = content.Model("hero.glb");

        var problem = Assert.Throws<ArgumentException>(() => content.Meshes(hero, "Head_05b", "Helmet"));

        Assert.Equal(
            "hero.glb has no mesh called Helmet. It has 3 meshes: Body_03a, Head_05b, w_spear. (Parameter 'names')",
            problem.Message);
    }

    [Fact]
    public void Names_match_exactly_including_case()
    {
        var content = new Content(new ModelFiles("hero.glb", Model("Body_03a"), streamOnly: false));
        var hero = content.Model("hero.glb");

        var problem = Assert.Throws<ArgumentException>(() => content.Meshes(hero, "body_03a"));

        Assert.StartsWith("hero.glb has no mesh called body_03a.", problem.Message);
    }

    [Fact]
    public void Long_mesh_lists_show_the_first_twenty_and_the_count()
    {
        string[] names = Enumerable.Range(0, 25).Select(i => $"P{i:D2}").ToArray();
        var content = new Content(new ModelFiles("crowd.glb", Model(names), streamOnly: false));
        var crowd = content.Model("crowd.glb");

        var problem = Assert.Throws<ArgumentException>(() => content.Meshes(crowd, "Nope"));

        Assert.Equal(
            "crowd.glb has no mesh called Nope. It has 25 meshes: P00, P01, P02, P03, P04, P05, P06, P07, P08, P09, " +
            "P10, P11, P12, P13, P14, P15, P16, P17, P18, P19, and 5 more. (Parameter 'names')",
            problem.Message);
    }

    [Fact]
    public void An_empty_name_list_is_refused()
    {
        var content = new Content(new ModelFiles("hero.glb", Model("Body"), streamOnly: false));
        var hero = content.Model("hero.glb");

        var problem = Assert.Throws<ArgumentException>(() => content.Meshes(hero));

        Assert.Equal("A mesh mask needs at least one mesh name. (Parameter 'names')", problem.Message);
    }

    [Fact]
    public void Built_in_shapes_have_no_mesh_names()
    {
        var content = new Content(new MemoryContent());

        var problem = Assert.Throws<ArgumentException>(() => content.Meshes(ModelRef.Cube, "Body"));

        Assert.Equal(
            "The built-in shapes, such as the cube, are not cooked, so they have no mesh names. (Parameter 'model')",
            problem.Message);
    }

    [Fact]
    public void A_model_this_content_never_named_is_refused()
    {
        var content = new Content(new MemoryContent());

        var problem = Assert.Throws<ArgumentException>(() => content.Meshes(new ModelRef(3), "Body"));

        Assert.Equal("model names 3, which this content never named, so it has no mesh names. (Parameter 'model')", problem.Message);
    }

    [Fact]
    public void A_model_with_no_mesh_names_says_why_and_how_to_get_them()
    {
        var bytes = new ModelData
        {
            Vertices = [],
            Indices = [],
            Parts = [new ModelPart()],
            Materials = [],
            Textures = [],
        }.ToBytes();
        var content = new Content(new ModelFiles("old.glb", bytes, streamOnly: false));
        var old = content.Model("old.glb");

        var problem = Assert.Throws<InvalidOperationException>(() => content.Meshes(old, "Body"));

        Assert.Equal(
            "old.glb has no mesh names, because its file was cooked without them. A rigged model always has them. " +
            "A static model needs \"keepMeshes\": true in old.glb.cook.json, and then to be cooked again.",
            problem.Message);
    }

    [Fact]
    public void A_model_file_from_before_mesh_names_says_to_cook_it_again()
    {
        var bytes = Model("Body");
        bytes[4] = 6; // The format version follows the 4-byte magic number, as a little-endian int.
        var content = new Content(new ModelFiles("old.glb", bytes, streamOnly: false));
        var old = content.Model("old.glb");

        var problem = Assert.Throws<InvalidDataException>(() => content.Meshes(old, "Body"));

        Assert.Equal("old.glb is not a current model file. Cook it again.", problem.Message);
    }

    [Fact]
    public void A_model_is_streamed_not_read_whole()
    {
        var content = new Content(new ModelFiles("hero.glb", Model("Body_03a", "Head_05b"), streamOnly: true));
        var hero = content.Model("hero.glb");

        var mask = content.Meshes(hero, "Head_05b");

        Assert.Equal([false, true], Shown(mask));
    }

    [Fact]
    public void MeshNames_lists_each_name_once_in_cook_order()
    {
        var content = new Content(new ModelFiles("hero.glb", Model("Body_03a", "Head_05b", "Body_03a"), streamOnly: true));
        var hero = content.Model("hero.glb");

        Assert.Equal(new[] { "Body_03a", "Head_05b" }, content.MeshNames(hero));
    }

    /// <summary>Counts how often the model file is opened.</summary>
    private sealed class CountingFiles(string name, byte[] bytes) : IContentFiles
    {
        public int Opened { get; private set; }

        public ContentKind? KindOf(string asked) => asked == name ? ContentKind.Model : null;

        public IReadOnlyList<string>? List(string folder) => null;

        public byte[] Read(string asked) => bytes;

        public Stream Open(string asked)
        {
            Opened++;
            return new MemoryStream(bytes);
        }
    }

    [Fact]
    public void Reading_a_rig_keeps_its_mesh_names_so_the_file_is_not_opened_again()
    {
        var bytes = new ModelData
        {
            Vertices = [],
            Indices = [],
            Parts = [new ModelPart(), new ModelPart()],
            Materials = [],
            Textures = [],
            MeshNames = ["Body", "Sword"],
            Skeleton = new Skeleton(["root"], [-1], [JointPose.Identity], [System.Numerics.Matrix4x4.Identity], System.Numerics.Matrix4x4.Identity),
        }.ToBytes();
        var files = new CountingFiles("hero.glb", bytes);
        var content = new Content(files);
        var rig = content.Rig("hero.glb");

        var mask = content.Meshes(rig.Model!.Value, "Sword");

        Assert.Equal(1, files.Opened);
        Assert.Equal([false, true], Shown(mask));
    }

    [Fact]
    public void Mesh_names_round_trip_through_the_model_file()
    {
        var model = new ModelData
        {
            Vertices = [],
            Indices = [],
            Parts = [new ModelPart(), new ModelPart(), new ModelPart()],
            Materials = [],
            Textures = [],
            MeshNames = ["Body", "Head", "Body"],
        };

        var bytes = model.ToBytes();

        Assert.Equal(new[] { "Body", "Head", "Body" }, ModelData.Read(new MemoryStream(bytes), "hero.rmodel").MeshNames);
        Assert.Equal(new[] { "Body", "Head", "Body" }, ModelData.ReadMeshNames(new MemoryStream(bytes), "hero.rmodel"));
    }

    [Fact]
    public void Mesh_names_must_be_one_for_each_part_or_none()
    {
        var model = new ModelData
        {
            Vertices = [],
            Indices = [],
            Parts = [new ModelPart(), new ModelPart()],
            Materials = [],
            Textures = [],
            MeshNames = ["Body"],
        };

        var problem = Assert.Throws<InvalidOperationException>(() => model.ToBytes());

        Assert.Equal("The model has 2 parts and 1 mesh names. Give one name to each part, or none.", problem.Message);
    }
}

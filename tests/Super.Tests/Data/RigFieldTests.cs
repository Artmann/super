using Rendition;
using Super.Testing;

namespace Super.Tests;

public class RigFieldTests
{
    public record Hero(string Id, Rig Rig, string Pelvis);

    private static (Content Content, MemoryContent Files) Make(string json)
    {
        var memory = new MemoryContent { ["heroes/knight.json"] = json };
        memory.AddModel("props/axe.glb");
        var files = new WithModels(memory)
        {
            ["characters/knight.glb"] = Model(TestRigSkeleton: true),
            ["props/crate.glb"] = Model(TestRigSkeleton: false),
        };
        return (new Content(files), memory);
    }

    private static string Knight(string rig) => $"{{ \"id\": \"knight\", \"rig\": \"{rig}\", \"pelvis\": \"hips\" }}";

    [Fact]
    public void A_rig_is_written_as_its_model_name_and_read_through_the_content()
    {
        var (content, _) = Make(Knight("characters/knight.glb"));

        var hero = content.LoadAll<Hero>("heroes")["knight"];

        Assert.Same(content.Rig("characters/knight.glb"), hero.Rig);
        Assert.Equal(content.Model("characters/knight.glb"), hero.Rig.Model);
        Assert.Equal("idle", Assert.Single(hero.Rig.Clips).Name);
    }

    [Fact]
    public void A_rig_stays_the_same_object_through_a_reload()
    {
        var (content, memory) = Make(Knight("characters/knight.glb"));
        var heroes = content.LoadAll<Hero>("heroes");
        var rig = heroes["knight"].Rig;

        memory["heroes/knight.json"] = Knight("characters/knight.glb").Replace("hips", "pelvis");
        Assert.True(content.Reload(TextWriter.Null));

        Assert.Equal("pelvis", heroes["knight"].Pelvis);
        Assert.Same(rig, heroes["knight"].Rig);
    }

    [Theory]
    [InlineData("characters/Knight.glb", "rig characters/Knight.glb isn't in the cooked content. Check the name, including its case and extension.")]
    [InlineData("heroes/knight.json", "rig heroes/knight.json is a data, not a model.")]
    [InlineData("props/crate.glb", "rig props/crate.glb was cooked without a skeleton, so it has no rig to animate.")]
    public void A_rig_that_cant_be_animated_is_reported_with_its_line(string rig, string problem)
    {
        var (content, _) = Make(Knight(rig));

        var thrown = Assert.Throws<ContentException>(() => content.LoadAll<Hero>("heroes"));

        Assert.Equal($"heroes/knight.json:1: {problem}", Assert.Single(thrown.Problems));
    }

    [Fact]
    public void A_rig_is_text_in_the_schema()
    {
        var content = new Content(new MemoryContent());

        Assert.Contains("\"rig\":{\"type\":\"string\"}", content.SchemaText(typeof(Hero)).Replace(" ", "").Replace("\n", "").Replace("\r", ""));
    }

    private static byte[] Model(bool TestRigSkeleton) => new ModelData
    {
        Vertices = [],
        Indices = [],
        Parts = [],
        Materials = [],
        Textures = [],
        Skeleton = TestRigSkeleton ? Animation.TestRig.Skeleton() : null,
        Clips = TestRigSkeleton ? [Animation.TestRig.Idle()] : [],
    }.ToBytes();

    /// <summary>Memory content with cooked models beside it, since a rig needs a model's real bytes.</summary>
    private sealed class WithModels(MemoryContent inner) : IContentFiles
    {
        private readonly Dictionary<string, byte[]> _models = [];

        public byte[] this[string name]
        {
            set => _models[name] = value;
        }

        public ContentKind? KindOf(string name) => _models.ContainsKey(name) ? ContentKind.Model : inner.KindOf(name);

        public IReadOnlyList<string>? List(string folder) => inner.List(folder);

        public byte[] Read(string name) => _models.TryGetValue(name, out var bytes) ? bytes : inner.Read(name);
    }
}

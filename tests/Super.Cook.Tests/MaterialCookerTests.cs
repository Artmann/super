using System.Text;
using Rendition;
using Super.Cook.Materials;

namespace Super.Cook.Tests;

public sealed class MaterialCookerTests
{
    private const string SmallSize = """{ "rules": [{ "match": "*.material.*", "material": { "size": 16 } }] }""";

    private const string Bricks = """
        using Super.Materials;

        public sealed class Bricks : MaterialGraph
        {
            public override Material Build()
            {
                var bricks = Tiles.Bricks(columns: 2, rows: 4, gap: 0.1f);
                return new Material
                {
                    Width = 2f,
                    Height = bricks,
                    Color = bricks.GradientMap(Ramp.Of("#5a2a1e", "#a8604a")),
                    Roughness = bricks.Remap(0.9f, 0.6f),
                };
            }
        }
        """;

    private const string NanHeight = """
        using Super.Materials;

        public sealed class NanHeight : MaterialGraph
        {
            public override Material Build() => new Material { Height = Gray.Of(float.NaN) };
        }
        """;

    private static (CookResult Result, MemoryStore Store) Cook(params (string Name, string Text)[] files)
    {
        var root = new MemoryRoot { ["cook.json"] = SmallSize };
        foreach (var (name, text) in files)
        {
            root[name] = text;
        }

        var store = new MemoryStore();
        var result = new CookPipeline([.. BuiltInCookers.All, new MaterialCooker()], new CookOptions()).Cook([root], store);
        return (result, store);
    }

    private static byte[] Data(MemoryStore store, string name)
    {
        return store.Get(store.Manifest.Assets[name].Hash);
    }

    private static CookedTexture Texture(MemoryStore store, string name)
    {
        var entry = store.Manifest.Assets[name];
        Assert.Equal(ContentKind.Texture, entry.Kind);
        return CookedTexture.Read(store.Get(entry.Hash), name);
    }

    private static string Graph(string build)
    {
        return "using System;\nusing System.Linq;\nusing Super.Materials;\npublic sealed class G : MaterialGraph\n{\n"
            + "    public override Material Build()\n    {\n" + build + "\n    }\n}\n";
    }

    [Fact]
    public void A_graph_cooks_into_four_textures_and_its_width()
    {
        var (result, store) = Cook(("bricks.material.cs", Bricks));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var color = Texture(store, "bricks.material.cs#color");
        Assert.Equal((16, 16, TextureKind.Color, false), (color.Width, color.Height, color.Kind, color.NormalMap));
        Assert.True(Texture(store, "bricks.material.cs#normal").NormalMap);
        Assert.Equal(TextureKind.Linear, Texture(store, "bricks.material.cs#surface").Kind);
        Assert.Equal(TextureKind.Linear, Texture(store, "bricks.material.cs#height").Kind);
        Assert.Equal("""{"width":2}""", Encoding.UTF8.GetString(Data(store, "bricks.material.cs#material")));
        Assert.Equal(ContentKind.Data, store.Manifest.Assets["bricks.material.cs#material"].Kind);
    }

    [Fact]
    public void The_parts_hold_colour_normal_surface_and_height()
    {
        // Flat height, so the normal points straight out and nothing is occluded.
        var (result, store) = Cook(("flat.material.cs", Graph("""
                    return new Material
                    {
                        Height = Gray.Of(0.2f),
                        Color = Rgb.Of(0.2f, 0.4f, 0.6f),
                        Metallic = Gray.Of(1f),
                        Roughness = Gray.Of(0.4f),
                        Opacity = Gray.Of(0.8f),
                    };
            """)));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal([51, 102, 153, 255], Texture(store, "flat.material.cs#color").Pixels[..4]);
        Assert.Equal([128, 128, 255, 255], Texture(store, "flat.material.cs#normal").Pixels[..4]);
        Assert.Equal([255, 102, 0, 204], Texture(store, "flat.material.cs#surface").Pixels[..4]);
        Assert.Equal([51, 51, 51, 255], Texture(store, "flat.material.cs#height").Pixels[..4]);
    }

    [Fact]
    public void Cooking_twice_gives_the_same_bytes()
    {
        var (first, firstStore) = Cook(("bricks.material.cs", Bricks));
        var (second, secondStore) = Cook(("bricks.material.cs", Bricks));

        Assert.True(first.Succeeded, string.Join("; ", first.Problems));
        Assert.True(second.Succeeded, string.Join("; ", second.Problems));
        string[] parts = ["color", "normal", "surface", "height", "material"];
        Assert.All(parts, part => Assert.Equal(
            firstStore.Manifest.Assets[$"bricks.material.cs#{part}"].Hash,
            secondStore.Manifest.Assets[$"bricks.material.cs#{part}"].Hash));
    }

    [Fact]
    public void A_compile_error_names_the_line()
    {
        var (result, _) = Cook(("bad.material.cs", "using Super.Materials;\npublic sealed class Bad : MaterialGraph\n{\n    public override Material Build() => new Material { Width = nope };\n}\n"));

        var error = Assert.Single(result.Problems, p => p.IsError);
        Assert.Equal(4, error.Line);
        Assert.Contains("nope", error.Message);
    }

    [Fact]
    public void A_type_the_cooker_does_not_reference_is_a_cook_error_not_a_crash()
    {
        var (result, _) = Cook(("net.material.cs", Graph("        var client = new System.Net.Http.HttpClient();\n        return new Material();")));

        var error = Assert.Single(result.Problems, p => p.IsError);
        Assert.Equal(8, error.Line);
        Assert.Contains("Net", error.Message);
    }

    [Fact]
    public void A_file_with_no_graph_class_says_what_it_needs()
    {
        var (result, _) = Cook(("empty.material.cs", "public sealed class NotAGraph { }\npublic abstract class Half : Super.Materials.MaterialGraph { }\n"));

        Assert.Equal("empty.material.cs must hold one class that derives from MaterialGraph. It has none.",
            Assert.Single(result.Problems, p => p.IsError).Message);
    }

    [Fact]
    public void A_file_with_two_graph_classes_names_both()
    {
        string source = """
            using Super.Materials;

            public sealed class B : MaterialGraph
            {
                public override Material Build() => new Material();
            }

            public sealed class A : MaterialGraph
            {
                public override Material Build() => new Material();
            }
            """;

        var (result, _) = Cook(("two.material.cs", source));

        Assert.Equal("two.material.cs must hold one class that derives from MaterialGraph. It has 2: A, B.",
            Assert.Single(result.Problems, p => p.IsError).Message);
    }

    [Fact]
    public void A_graph_that_throws_names_its_line_and_message()
    {
        var (result, _) = Cook(("boom.material.cs", Graph("        throw new InvalidOperationException(\"boom\");")));

        var error = Assert.Single(result.Problems, p => p.IsError);
        Assert.Equal(8, error.Line);
        Assert.Contains("boom", error.Message);
    }

    [Fact]
    public void A_bad_node_argument_names_the_line_in_the_graph_that_made_the_node()
    {
        var (result, _) = Cook(("perlin.material.cs", Graph("        var noise = Noise.Perlin(scale: 0);\n        return new Material { Height = noise };")));

        var error = Assert.Single(result.Problems, p => p.IsError);
        Assert.Equal(8, error.Line);
        Assert.Contains("scale must be at least 1, not 0.", error.Message);
    }

    [Fact]
    public void Nan_is_written_as_zero_with_a_warning()
    {
        // NanHeight sets Height = Gray.Of(float.NaN). The normal and colour may warn too,
        // so this only looks for the height's warning.
        var (result, store) = Cook(("nan.material.cs", NanHeight));

        Assert.Contains(result.Problems, p => !p.IsError && p.Message == "nan.material.cs: height has pixels that aren't numbers. They were written as 0.");
        Assert.All(Texture(store, "nan.material.cs#height").Pixels.Where((_, i) => i % 4 == 0), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Values_outside_zero_to_one_are_clamped()
    {
        var (result, store) = Cook(("bright.material.cs", Graph("        return new Material { Color = Rgb.Of(2f, -1f, 0.5f), Occlusion = 0f };")));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal([255, 0, 128, 255], Texture(store, "bright.material.cs#color").Pixels[..4]);
    }

    [Fact]
    public void A_size_that_is_not_a_power_of_two_is_an_error()
    {
        var (result, _) = Cook(
            ("bricks.material.cs", Bricks),
            ("bricks.material.cs.cook.json", """{ "size": 100 }"""));

        Assert.Equal("bricks.material.cs: size must be a power of two from 16 to 4096, not 100.",
            Assert.Single(result.Problems, p => p.IsError).Message);
    }

    [Fact]
    public void Linq_works_in_a_graph()
    {
        var (result, store) = Cook(("linq.material.cs", Graph("        return new Material { Width = Enumerable.Range(1, 3).Sum() };")));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal("""{"width":6}""", Encoding.UTF8.GetString(Data(store, "linq.material.cs#material")));
    }
}

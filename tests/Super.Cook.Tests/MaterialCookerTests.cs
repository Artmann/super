using System.Text;
using Rendition;
using Super.Cook.Materials;

namespace Super.Cook.Tests;

public sealed class MaterialCookerTests : IDisposable
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
    public void A_graph_can_move_an_image_by_a_vector()
    {
        // Transform's offset is a Vector2, so the cooker must reference System.Numerics like the game does.
        var (result, store) = Cook(("moved.material.cs", """
            using System.Numerics;
            using Super.Materials;

            public sealed class Moved : MaterialGraph
            {
                public override Material Build()
                {
                    return new Material { Width = 2f, Height = Noise.Perlin(4).Transform(new Vector2(0.25f, 0f), tile: 2) };
                }
            }
            """));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal("""{"width":2}""", Encoding.UTF8.GetString(Data(store, "moved.material.cs#material")));
        Assert.Equal(16, Texture(store, "moved.material.cs#height").Width);
    }

    [Fact]
    public void A_graph_gets_the_usings_a_game_build_gives_it()
    {
        // Collections and LINQ with no using lines, as the game's ImplicitUsings allow, so the IDE and the cook agree.
        var (result, store) = Cook(("implicit.material.cs", """
            using Super.Materials;

            public sealed class Implicit : MaterialGraph
            {
                public override Material Build()
                {
                    var widths = new List<float> { 1f, 2f };
                    return new Material { Width = widths.Sum() };
                }
            }
            """));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal("""{"width":3}""", Encoding.UTF8.GetString(Data(store, "implicit.material.cs#material")));
    }

    [Fact]
    public void System_IO_is_not_among_the_usings_a_graph_gets()
    {
        var (result, _) = Cook(("io.material.cs", """
            using Super.Materials;

            public sealed class Io : MaterialGraph
            {
                public override Material Build()
                {
                    return new Material { Width = File.Exists("a") ? 1f : 2f };
                }
            }
            """));

        var error = Assert.Single(result.Problems, p => p.IsError);
        Assert.Equal(7, error.Line);
        Assert.Contains("File", error.Message);
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
        Assert.Equal("scale must be at least 1, not 0. (Parameter 'scale')", error.Message);
    }

    [Fact]
    public void An_error_message_of_several_lines_is_reported_as_its_first_line()
    {
        var (result, _) = Cook(("lines.material.cs", Graph("        throw new InvalidOperationException(\"first line\\nsecond line\");")));

        var error = Assert.Single(result.Problems, p => p.IsError);
        Assert.Equal("first line", error.Message);
        Assert.DoesNotContain('\n', error.Message);
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

    private const string Moss = """
        using Super.Materials;

        public sealed class Bricks : MaterialGraph
        {
            public float Moss { get; init; } = 0.2f;

            public float Mortar { get; init; } = 0.5f;

            public override Material Build() => new Material { Color = Rgb.Of(Moss, Mortar, 0f) };
        }
        """;

    private const string Typed = """
        using Super.Materials;

        public sealed class Typed : MaterialGraph
        {
            public int Count { get; init; } = 3;

            public bool Wet { get; init; }

            public string Label { get; init; } = "";

            public float Moss { get; init; }

            // Not parameters: one has no setter, one a plain set, and one a type a variant can't set.
            public float Fixed { get; } = 1f;

            public float Loose { get; set; }

            public int[] Many { get; init; } = [];

            public override Material Build() => new Material { Width = Count + (Wet ? 100 : 0) + Label.Length };
        }
        """;

    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, true);
        }
    }

    private string Temp()
    {
        string folder = Path.Combine(Path.GetTempPath(), "super-materials-" + Guid.NewGuid().ToString("N"));
        _folders.Add(folder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string FirstProblem(CookResult result)
    {
        return Assert.Single(result.Problems, p => p.IsError).Message;
    }

    private static byte[] Png(int width, int height, byte[] rgba)
    {
        string path = Path.Combine(Path.GetTempPath(), "super-materials-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            PngFile.Write(path, rgba, width, height);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Hash(MemoryStore store, string name) => store.Manifest.Assets[name].Hash;

    private static CookResult CookFolder(string source, string cache, MemoryStore store)
    {
        return new CookPipeline([.. BuiltInCookers.All, new MaterialCooker()], new CookOptions(CacheFolder: cache)).Cook([new FolderRoot(source)], store);
    }

    [Fact]
    public void A_variant_changes_the_pixels()
    {
        var (result, store) = Cook(("bricks.material.cs", Moss), ("mossy.material.json", """{ "graph": "bricks.material.cs", "moss": 0.8 }"""));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal([51, 128, 0, 255], Texture(store, "bricks.material.cs#color").Pixels[..4]);
        Assert.Equal([204, 128, 0, 255], Texture(store, "mossy.material.json#color").Pixels[..4]);
        Assert.NotEqual(Hash(store, "bricks.material.cs#color"), Hash(store, "mossy.material.json#color"));
    }

    [Fact]
    public void A_variant_sets_ints_bools_and_text()
    {
        var (result, store) = Cook(("typed.material.cs", Typed), ("v.material.json", """{ "graph": "typed.material.cs", "COUNT": 4, "wet": true, "Label": "ab" }"""));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal("""{"width":106}""", Encoding.UTF8.GetString(Data(store, "v.material.json#material")));
    }

    [Fact]
    public void A_variant_finds_its_graph_beside_itself()
    {
        var (result, store) = Cook(("a/bricks.material.cs", Moss), ("a/mossy.material.json", """{ "graph": "bricks.material.cs", "moss": 0.8 }"""));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal([204, 128, 0, 255], Texture(store, "a/mossy.material.json#color").Pixels[..4]);
    }

    [Fact]
    public void A_variant_cooks_again_when_its_graph_changes()
    {
        string source = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(source, "cook.json"), SmallSize);
        File.WriteAllText(Path.Combine(source, "bricks.material.cs"), Moss);
        File.WriteAllText(Path.Combine(source, "mossy.material.json"), """{ "graph": "bricks.material.cs", "mortar": 1 }""");

        var firstStore = new MemoryStore();
        Assert.True(CookFolder(source, cache, firstStore).Succeeded);
        File.WriteAllText(Path.Combine(source, "bricks.material.cs"), Moss.Replace("0.2f", "0.4f"));
        var secondStore = new MemoryStore();
        Assert.True(CookFolder(source, cache, secondStore).Succeeded);

        Assert.Equal([51, 255, 0, 255], Texture(firstStore, "mossy.material.json#color").Pixels[..4]);
        Assert.Equal([102, 255, 0, 255], Texture(secondStore, "mossy.material.json#color").Pixels[..4]);
        Assert.NotEqual(Hash(firstStore, "mossy.material.json#color"), Hash(secondStore, "mossy.material.json#color"));
    }

    [Fact]
    public void A_variant_without_a_graph_says_what_it_needs()
    {
        var (result, _) = Cook(("mossy.material.json", """{ "moss": 0.8 }"""));

        Assert.Equal("mossy.material.json needs \"graph\", the .material.cs file it sets values for.", FirstProblem(result));
    }

    [Fact]
    public void A_variant_cannot_name_another_variant()
    {
        var (result, _) = Cook(("a.material.json", """{ "graph": "b.material.json" }"""));

        Assert.Equal("a.material.json: graph must be a .material.cs file, not b.material.json.", FirstProblem(result));
    }

    [Fact]
    public void An_unknown_parameter_lists_the_ones_there_are()
    {
        var (result, _) = Cook(("bricks.material.cs", Moss), ("mossy.material.json", """{ "graph": "bricks.material.cs", "mos": 0.8 }"""));

        Assert.Equal("mossy.material.json: Bricks has no parameter called mos. It has mortar, moss, seed.", FirstProblem(result));
    }

    [Fact]
    public void A_parameter_of_the_wrong_type_says_what_it_wants()
    {
        var (result, _) = Cook(
            ("bricks.material.cs", Moss),
            ("typed.material.cs", Typed),
            ("number.material.json", """{ "graph": "bricks.material.cs", "moss": "lots" }"""),
            ("whole.material.json", """{ "graph": "typed.material.cs", "count": 1.5 }"""),
            ("bool.material.json", """{ "graph": "typed.material.cs", "wet": 1 }"""),
            ("text.material.json", """{ "graph": "typed.material.cs", "label": 7 }"""),
            ("seed.material.json", """{ "graph": "typed.material.cs", "seed": "x" }"""));

        string[] messages = [.. result.Problems.Where(p => p.IsError).Select(p => p.Message).Order(StringComparer.Ordinal)];
        Assert.Equal(
            [
                "bool.material.json: wet is true or false, not 1.",
                "number.material.json: moss is a number, not \"lots\".",
                "seed.material.json: seed is a whole number, not \"x\".",
                "text.material.json: label is text, not 7.",
                "whole.material.json: count is a whole number, not 1.5.",
            ],
            messages);
    }

    [Fact]
    public void Only_public_init_parameters_of_simple_types_are_listed()
    {
        var (result, _) = Cook(("typed.material.cs", Typed), ("v.material.json", """{ "graph": "typed.material.cs", "fixed": 2 }"""));

        Assert.Equal("v.material.json: Typed has no parameter called fixed. It has count, label, moss, seed, wet.", FirstProblem(result));
    }

    [Fact]
    public void A_graph_that_is_missing_is_an_error()
    {
        var (result, _) = Cook(("mossy.material.json", """{ "graph": "nope.material.cs" }"""));

        Assert.Equal("nope.material.cs isn't in the source root.", FirstProblem(result));
    }

    [Fact]
    public void A_graph_named_in_the_wrong_case_says_so()
    {
        var (result, _) = Cook(("bricks.material.cs", Moss), ("mossy.material.json", """{ "graph": "Bricks.material.cs" }"""));

        Assert.Contains("but bricks.material.cs is. Names are case-sensitive.", FirstProblem(result));
    }

    [Fact]
    public void A_variant_that_is_not_json_is_an_error()
    {
        var (result, _) = Cook(("mossy.material.json", "{ nope"));

        Assert.StartsWith("mossy.material.json: ", FirstProblem(result));
    }

    [Fact]
    public void Seed_in_a_variant_reaches_the_graph()
    {
        const string noisy = """
            using Super.Materials;

            public sealed class Noisy : MaterialGraph
            {
                public override Material Build() => new Material { Height = Noise.Perlin(scale: 4) };
            }
            """;

        var (result, store) = Cook(
            ("noisy.material.cs", noisy),
            ("one.material.json", """{ "graph": "noisy.material.cs", "seed": 1 }"""),
            ("two.material.json", """{ "graph": "noisy.material.cs", "seed": 2 }"""));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.NotEqual(Hash(store, "one.material.json#height"), Hash(store, "two.material.json#height"));
        Assert.NotEqual(Hash(store, "noisy.material.cs#height"), Hash(store, "one.material.json#height"));
    }

    [Fact]
    public void An_image_is_resampled_with_bilinear_wrap()
    {
        var root = new MemoryRoot { ["cook.json"] = SmallSize, ["rock.material.cs"] = Graph("        return new Material { Color = Image(\"rock.png\").Multiply(Gray.Of(1f)) };") };
        root.Add("rock.png", Png(2, 1, [255, 0, 0, 255, 0, 0, 255, 255]));
        var store = new MemoryStore();

        var result = new CookPipeline([.. BuiltInCookers.All, new MaterialCooker()], new CookOptions()).Cook([root], store);

        // Pixel 0 is at u 1/32, so it is 0.5625 of the way from the wrapped-round blue to the red, and not linearised.
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal([143, 0, 112, 255], Texture(store, "rock.material.cs#color").Pixels[..4]);
    }

    [Fact]
    public void An_image_input_is_tracked()
    {
        string source = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(source, "cook.json"), SmallSize);
        File.WriteAllText(Path.Combine(source, "rock.material.cs"), Graph("        return new Material { Color = Image(\"rock.png\").Multiply(Gray.Of(1f)) };"));
        File.WriteAllBytes(Path.Combine(source, "rock.png"), Png(1, 1, [255, 0, 0, 255]));

        var firstStore = new MemoryStore();
        Assert.True(CookFolder(source, cache, firstStore).Succeeded);
        File.WriteAllBytes(Path.Combine(source, "rock.png"), Png(1, 1, [0, 0, 255, 255]));
        var secondStore = new MemoryStore();
        Assert.True(CookFolder(source, cache, secondStore).Succeeded);

        Assert.Equal([255, 0, 0, 255], Texture(firstStore, "rock.material.cs#color").Pixels[..4]);
        Assert.Equal([0, 0, 255, 255], Texture(secondStore, "rock.material.cs#color").Pixels[..4]);
    }

    [Fact]
    public void An_image_that_is_not_there_is_an_error_at_the_graph_line()
    {
        var (result, _) = Cook(("rock.material.cs", Graph("        return new Material { Color = Image(\"nope.png\").Multiply(Gray.Of(1f)) };")));

        var error = Assert.Single(result.Problems, p => p.IsError);
        Assert.Equal("nope.png isn't in the source root.", error.Message);
        Assert.Equal(8, error.Line);
    }

    private static string[] Warnings(CookResult result) => [.. result.Problems.Where(p => !p.IsError).Select(p => p.Message)];

    private const string Grain = "        var grain = Noise.Perlin(scale: 4);\n";

    [Fact]
    public void A_dark_base_colour_is_a_warning_not_an_error()
    {
        var (result, _) = Cook(("dark.material.cs", Graph(Grain + "        return new Material { Color = Rgb.Of(\"#101010\"), Roughness = grain.Remap(0.5f, 0.8f) };")));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal(
            ["dark.material.cs: 100% of the base colour is darker than sRGB 30. Nothing real is that dark. Charcoal is about 50."],
            Warnings(result));
    }

    [Fact]
    public void A_bright_base_colour_warns()
    {
        var (result, _) = Cook(("bright.material.cs", Graph(Grain + "        return new Material { Color = Rgb.Of(\"#f8f8f8\"), Roughness = grain.Remap(0.5f, 0.8f) };")));

        Assert.Equal(
            ["bright.material.cs: 100% of the base colour is brighter than sRGB 240. Fresh snow is about 240."],
            Warnings(result));
    }

    [Fact]
    public void Half_metal_warns()
    {
        var (result, _) = Cook(("half.material.cs", Graph(Grain + "        return new Material { Metallic = Gray.Of(0.5f), Roughness = grain.Remap(0.5f, 0.8f) };")));

        Assert.Equal(
            ["half.material.cs: most of the metalness is between 0.1 and 0.9. Surfaces are metal or not, and in-between values look like plastic."],
            Warnings(result));
    }

    [Fact]
    public void Flat_roughness_warns()
    {
        var (result, _) = Cook(("flat.material.cs", Graph("        return new Material { Roughness = Gray.Of(0.6f) };")));

        Assert.Equal(
            ["flat.material.cs: the roughness hardly varies. Real surfaces have smudges and wear, so vary it by a few percent at least."],
            Warnings(result));
    }

    [Fact]
    public void A_good_material_has_no_warnings()
    {
        var (result, _) = Cook(("good.material.cs", Graph(Grain
            + "        return new Material\n        {\n"
            + "            Color = grain.GradientMap(Ramp.Of(\"#707070\", \"#b0b0b0\")),\n"
            + "            Roughness = Noise.Perlin(scale: 4, seed: 3).Remap(0.5f, 0.8f),\n"
            + "            Metallic = Gray.Of(0f),\n        };")));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Empty(Warnings(result));
    }
}

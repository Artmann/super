using System.Numerics;
using Crypt;
using Super;
using Microsoft.Extensions.DependencyInjection;
using Super.Testing;

namespace Crypt.Tests;

public sealed class BakedSpaceTests
{
    // The smallest PNG there is, one white pixel, to stand in for the stone textures that aren't in git.
    private static readonly byte[] Pixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // The hall, worked out by hand from the walls and the vault. The walls are 0.6 m thick, the ceiling 0.4 m, and the
    // end walls stand 0.6 m past the hall's ends. So the side walls' outer faces are 0.6 m beyond half the width, the
    // ceiling's top is 0.4 m above the hall's height, and the end walls' outer faces are 0.6 m past the front and back.
    private static readonly Vector3 HallMin = new(-(CryptGame.HallWidth / 2f) - 0.6f, 0f, CryptGame.HallBack - 0.6f);
    private static readonly Vector3 HallMax = new(CryptGame.HallWidth / 2f + 0.6f, CryptGame.HallHeight + 0.4f, CryptGame.HallFront + 0.6f);

    // The game running headless, with these files as its content, after its first tick, and the content to look in.
    private static (HeadlessRun<CryptGame> Run, Content Content) Started()
    {
        var files = new MemoryContent();
        foreach (string name in new[] { "floor", "wall", "blocks" })
        {
            files.Add($"textures/{name}.jpg", Pixel);
            files.Add($"textures/{name}-normal.jpg", Pixel);
            files.Add($"textures/{name}-surface.png", Pixel);
        }

        foreach (string model in new[]
        {
            "props/gothic_statue/gothic_statue_1k.gltf",
            "props/treasure_chest/treasure_chest_1k.gltf",
            "props/Lantern_01/Lantern_01_1k.gltf",
            "props/wine_barrel_01/wine_barrel_01_1k.gltf",
            "props/brass_candleholders/brass_candleholders_1k.gltf#seven",
            "props/brass_candleholders/brass_candleholders_1k.gltf#three",
            "props/brass_candleholders/brass_candleholders_1k.gltf#one",
        })
        {
            files.AddModel(model);
        }

        var content = new Content(files);
        var run = HeadlessGame.Create<CryptGame>(1, services => services.AddSingleton(content));
        run.Ticks(1);
        return (run, content);
    }

    // The mesh behind a draw, when the test can see it: the built-in shapes and the meshes the game makes in code. A
    // model read from a glTF file has no geometry here, because the files aren't in git.
    private static Rendition.Vertex[]? VerticesOf(Content content, ModelRef model)
    {
        if (model.Index <= 0)
        {
            return Shapes.BuiltIn()[-model.Index - 1].Vertices;
        }

        return content.Store.Items[model.Index - 1] is GeneratedMesh mesh ? mesh.Vertices : null;
    }

    private static (Vector3 Min, Vector3 Max) WorldBounds(Rendition.Vertex[] vertices, Matrix4x4 transform)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in vertices)
        {
            var at = Vector3.Transform(vertex.Position, transform);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }

        return (min, max);
    }

    private static bool Holds(Vector3 outerMin, Vector3 outerMax, Vector3 innerMin, Vector3 innerMax)
    {
        return Vector3.Min(outerMin, innerMin) == outerMin && Vector3.Max(outerMax, innerMax) == outerMax;
    }

    [Fact]
    public void Crypt_names_one_baked_space_and_static_hall()
    {
        var (started, content) = Started();
        using var run = started;

        // The baked space is an entity, which the world describes into every frame.
        var view = new View();
        view.Begin(0f);
        WorldFrame.Describe(view, run.World);
        var space = view.Indirect.BakedSpace;
        Assert.NotNull(space);
        Assert.Equal("crypt", space.Value.Name);
        Assert.True(Holds(space.Value.Min, space.Value.Max, HallMin, HallMax),
            $"The box {space.Value.Min} to {space.Value.Max} should hold the hall, {HallMin} to {HallMax}.");

        var statics = run.Drawn.Where(draw => draw.Static).ToArray();
        Assert.NotEmpty(statics);

        int measured = 0;
        foreach (var draw in statics)
        {
            var vertices = VerticesOf(content, draw.Model);
            if (vertices is null)
            {
                // A glTF prop: its geometry isn't here, so only the spot it stands on can be checked.
                var at = draw.Transform.Translation;
                Assert.True(
                    Holds(space.Value.Min, space.Value.Max, at, at),
                    $"Static draw {draw.Key} stands at {at}, outside the baked box {space.Value.Min} to {space.Value.Max}.");
                continue;
            }

            var (min, max) = WorldBounds(vertices, draw.Transform);
            Assert.True(
                Holds(space.Value.Min, space.Value.Max, min, max),
                $"Static draw {draw.Key} reaches {min} to {max}, outside the baked box {space.Value.Min} to {space.Value.Max}.");
            measured++;
        }

        // The hall's walls, floor and ceiling are all meshes the game makes, so they must have been measured.
        Assert.True(measured >= 10, $"Only {measured} static draws had geometry to measure.");
    }

    [Fact]
    public void Crypt_keeps_only_the_swinging_lantern_and_its_chain_unbaked()
    {
        using var run = Started().Run;

        var moving = run.Drawn.Where(draw => !draw.Static).ToArray();

        // The chain and the lantern hang from the vault over the chest, at 0, -3.1, between the lantern's foot and the
        // vault 5 m up. Everything else in the hall stands still.
        Assert.Equal(2, moving.Length);
        foreach (var draw in moving)
        {
            var at = draw.Transform.Translation;
            Assert.InRange(at.X, -0.1f, 0.1f);
            Assert.InRange(at.Z, -3.3f, -2.9f);
            Assert.InRange(at.Y, 2.4f, 5f);
        }
    }
}

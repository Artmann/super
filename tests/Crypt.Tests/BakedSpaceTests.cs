using System.Numerics;
using Crypt;
using Super;
using Super.Testing;

namespace Crypt.Tests;

public sealed class BakedSpaceTests
{
    // The smallest PNG there is, one white pixel, to stand in for the stone textures that aren't in git.
    private static readonly byte[] Pixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // The hall, worked out by hand from the walls and the vault: the side walls' outer faces stand 4.5 + 0.6 m
    // either side of the middle, the ceiling's top is 5 + 0.4 m up, and the end walls reach 0.6 m past the hall's
    // ends at z = -9.5 and z = 4.
    private static readonly Vector3 HallMin = new(-5.1f, 0f, -10.1f);
    private static readonly Vector3 HallMax = new(5.1f, 5.4f, 4.6f);

    private static View DrawnFrame()
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

        var game = new CryptGame(new Content(files));
        game.Load();
        var view = new View();
        view.Begin(0f);
        game.Draw(view);
        return view;
    }

    [Fact]
    public void Crypt_names_one_baked_space_and_static_hall()
    {
        var view = DrawnFrame();

        var space = view.BakedSpace;
        Assert.NotNull(space);
        Assert.Equal("crypt", space.Value.Name);
        Assert.True(Vector3.Min(space.Value.Min, HallMin) == space.Value.Min && Vector3.Max(space.Value.Max, HallMax) == space.Value.Max,
            $"The box {space.Value.Min} to {space.Value.Max} should hold the hall, {HallMin} to {HallMax}.");

        var statics = view.Drawn.ToArray().Where(draw => draw.Static).ToArray();
        Assert.NotEmpty(statics);
        foreach (var draw in statics)
        {
            var at = draw.Transform.Translation;
            Assert.True(
                Vector3.Min(space.Value.Min, at) == space.Value.Min && Vector3.Max(space.Value.Max, at) == space.Value.Max,
                $"Static draw {draw.Key} stands at {at}, outside the baked box {space.Value.Min} to {space.Value.Max}.");
        }
    }

    [Fact]
    public void Crypt_keeps_only_the_swinging_lantern_and_its_chain_unbaked()
    {
        var view = DrawnFrame();

        var moving = view.Drawn.ToArray().Where(draw => !draw.Static).Select(draw => draw.Key).Order().ToArray();

        // The chain and the lantern are drawn under keys 330 and 331. Everything else in the hall stands still.
        Assert.Equal([330, 331], moving);
    }
}

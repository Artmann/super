using System.Numerics;
using System.Runtime.CompilerServices;
using Rendition;
using Super;
using Super.Testing;

namespace Courtyard.Tests;

public sealed class BakedSpaceTests
{
    private static string Model([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "examples", "Courtyard", "content", "models", "courtyard.glb"));
    }

    [Fact]
    public void Courtyard_names_one_baked_space_and_static_yard()
    {
        // Draw doesn't need the cooked content, only the names Load would have made, so the game is not loaded.
        var game = new CourtyardGame(new Content(new MemoryContent()), new Input(), new Graphics());
        var view = new View();
        view.Begin(0f);

        game.Draw(view);

        var space = view.BakedSpace;
        Assert.NotNull(space);
        Assert.Equal("courtyard", space.Value.Name);

        // The model is the yard. Its stored extent, from the file, has to be inside the box, or the part outside is
        // lit live while the rest is baked.
        var (min, max) = Glb.Bounds(Model());
        Assert.True(
            Vector3.Min(space.Value.Min, min) == space.Value.Min && Vector3.Max(space.Value.Max, max) == space.Value.Max,
            $"The box {space.Value.Min} to {space.Value.Max} should hold the model, {min} to {max}.");

        var model = view.Drawn.ToArray().Single(draw => draw.Key == 0);
        Assert.True(model.Static, "The courtyard model is drawn without isStatic, so the bake can't hold it.");
        Assert.Equal(Matrix4x4.Identity, model.Transform);
        Assert.All(view.Drawn.ToArray(), draw => Assert.True(draw.Static, $"Draw {draw.Key} is not static."));
    }
}

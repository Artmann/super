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
        // Draw doesn't need the cooked content, only the names Load would have made, so the game is not loaded: the yard
        // is spawned on its own, as Load spawns it.
        var world = new World();
        var memory = new MemoryContent();
        memory.AddModel("models/courtyard.glb");
        var content = new Content(memory);
        CourtyardYard.Spawn(world, content.Model("models/courtyard.glb"));
        var game = new CourtyardGame(world, content, new Input(), new Graphics());
        var view = new View();
        view.Begin(0f);

        WorldFrame.Describe(view, world);
        game.Draw(view);

        var space = view.Indirect.BakedSpace;
        Assert.NotNull(space);
        Assert.Equal("courtyard", space.Value.Name);

        // The model is the yard. Its stored extent, from the file, has to be inside the box, or the part outside is
        // lit live while the rest is baked.
        var (min, max) = Glb.Bounds(Model());
        Assert.True(
            Vector3.Min(space.Value.Min, min) == space.Value.Min && Vector3.Max(space.Value.Max, max) == space.Value.Max,
            $"The box {space.Value.Min} to {space.Value.Max} should hold the model, {min} to {max}.");

        var model = Assert.Single(view.Drawn.ToArray());
        Assert.True(model.Static, "The courtyard model is drawn without Static, so the bake can't hold it.");
        Assert.Equal(Matrix4x4.Identity, model.Transform);
        Assert.All(view.Drawn.ToArray(), draw => Assert.True(draw.Static, $"Draw {draw.Key} is not static."));
    }
}

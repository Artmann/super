using System.Numerics;
using Rendition;
using Super;
using Super.Physics;

namespace SkylarkExample;

/// <summary>Builds the level the journey is on into a scene: the light, the islands and everything on them.</summary>
public sealed class Scenery(Look look, Levels levels, Chase chase)
{
    /// <summary>The beacon's fire: small flames that rise and fade.</summary>
    private static readonly ParticleEmitter Fire = new ParticleEmitter(count: 60, lifetime: 0.6f)
        .Over(1f)
        .FromSphere(0.15f)
        .Velocity(new Vector3(0f, 1.2f, 0f))
        .Scatter(0.2f)
        .Size(0.35f, 0.05f)
        .Color(new Vector3(1f, 0.8f, 0.4f), new Vector3(0.95f, 0.3f, 0.08f))
        .Glow(4f)
        .Fade(0.1f, 0.5f);

    /// <summary>Spawns the journey's level into a scene. Hand it to <c>scenes.Go</c>.</summary>
    public void Build(Scene scene)
    {
        var level = levels.Current;
        Light(scene);

        foreach (var island in level.Islands)
        {
            Island(scene, island);
        }

        foreach (var path in level.Lifts)
        {
            Lift(scene, path);
        }

        foreach (var spot in level.Gems)
        {
            Gem(scene, spot);
        }

        foreach (var spot in level.Crates)
        {
            Crate(scene, spot);
        }

        foreach (var spot in level.Trees)
        {
            Tree(scene, spot);
        }

        Beacon(scene, level.Goal);
        Bird(scene, level.Start);
        chase.Build(scene, level.Start);
    }

    /// <summary>A warm sun with crisp shadows, a soft sky, and brown ink lines.</summary>
    private static void Light(Scene scene)
    {
        scene.Spawn(
            new Sun(Direction: new Vector3(0.7f, 1.6f, 1.1f), Color: new Vector3(2.6f, 2.4f, 2.1f)),
            new Shadows(Radius: 40f, SunSize: 0.03f),
            new Sky(new SkySettings
            {
                Zenith = new Vector3(0.16f, 0.42f, 0.9f),
                Horizon = new Vector3(0.6f, 0.8f, 1f),
                Ground = new Vector3(0.3f, 0.55f, 0.95f),
                Brightness = 0.55f,
            }),
            new Outlines(Color: new Vector3(0.12f, 0.09f, 0.07f), Thickness: 1.5f));
        scene.Spawn(new Finish(new PostSettings { Contrast = 0.15f, Saturation = 0.25f, Vignette = 0.2f }));
    }

    /// <summary>A solid slab, with two smaller blocks of soil hanging under a grass one so it looks afloat.</summary>
    private void Island(Scene scene, Island island)
    {
        bool rock = island.Height > 0.5f;
        var slab = new Transform(
            island.Top - new Vector3(0f, island.Height / 2f, 0f),
            Quaternion.Identity,
            new Vector3(island.Width, island.Height, island.Depth));
        scene.Spawn(slab, new Model(look.Cube, rock ? Palette.Rock : Palette.Grass), Collider.Box(Vector3.One), Body.Static);
        if (rock)
        {
            return;
        }

        var upper = new Transform(island.Top - new Vector3(0f, 0.95f, 0f), Quaternion.Identity, new Vector3(island.Width * 0.8f, 0.9f, island.Depth * 0.8f));
        var lower = new Transform(island.Top - new Vector3(0f, 1.75f, 0f), Quaternion.Identity, new Vector3(island.Width * 0.5f, 0.7f, island.Depth * 0.5f));
        scene.Spawn(upper, new Model(look.Cube, Palette.Soil));
        scene.Spawn(lower, new Model(look.Cube, Palette.Soil));
    }

    /// <summary>A wooden platform that glides between the two ends of its path.</summary>
    private void Lift(Scene scene, LiftPath path)
    {
        // No Body makes it kinematic: it goes where its Transform says.
        var size = new Vector3(3f, 0.4f, 3f);
        var middle = new Vector3(0f, size.Y / 2f, 0f);
        var glide = Tween.Between(path.From - middle, path.To - middle, path.Seconds, Easing.SineInOut).Yoyo();
        scene.Spawn(new Transform(path.From - middle, Quaternion.Identity, size), new Model(look.Cube, Palette.Plank), Collider.Box(Vector3.One), new Lift(glide));
    }

    /// <summary>A gem hovering a metre over a spot on the ground.</summary>
    private void Gem(Scene scene, Vector3 spot)
    {
        // Gems need a Trigger, and the game says so once with Require, so it isn't listed here.
        float height = spot.Y + 1f;
        scene.Spawn(Transform.At(spot with { Y = height }, 0.6f), new Model(look.Gem, Palette.Jewel), Collider.Ball(1f), new Gem(height));
    }

    /// <summary>A crate to push, standing on a spot on the ground.</summary>
    private void Crate(Scene scene, Vector3 spot)
    {
        var home = spot + new Vector3(0f, 0.5f, 0f);
        scene.Spawn(Transform.At(home), new Model(look.Cube, Palette.Wood), Collider.Box(Vector3.One), Body.Dynamic(mass: 15f, friction: 0.8f), new Crate(home));
    }

    /// <summary>A trunk with a cone of leaves on top.</summary>
    private void Tree(Scene scene, Vector3 spot)
    {
        scene.Spawn(new Transform(spot + new Vector3(0, 0.4f, 0), Quaternion.Identity, new Vector3(0.3f, 0.8f, 0.3f)), new Model(look.Cylinder, Palette.Wood));
        scene.Spawn(new Transform(spot + new Vector3(0, 1.7f, 0), Quaternion.Identity, new Vector3(1.5f, 2f, 1.5f)), new Model(look.Cone, Palette.Leaves));
    }

    /// <summary>A stone bowl on a post, an area round it that notices the bird, and a flame hidden until the gems are found.</summary>
    private void Beacon(Scene scene, Vector3 foot)
    {
        scene.Spawn(new Transform(foot + new Vector3(0, 0.7f, 0), Quaternion.Identity, new Vector3(0.4f, 1.4f, 0.4f)), new Model(look.Cylinder, Palette.Rock));
        scene.Spawn(new Transform(foot + new Vector3(0, 1.5f, 0), Quaternion.Identity, new Vector3(1.1f, 0.3f, 1.1f)), new Model(look.Cylinder, Palette.Rock));

        var flame = scene.Spawn(
            Transform.At(foot + new Vector3(0, 1.8f, 0)),
            new Emitter(Fire),
            new PointLight(default, new Vector3(6f, 3.5f, 1.2f), Range: 9f),
            default(Hidden));
        scene.Spawn(Transform.At(foot + new Vector3(0, 1f, 0)), Collider.Box(new Vector3(2.4f, 2f, 2.4f)), new Trigger(), new Goal(flame));
    }

    /// <summary>The bird: a capsule that walks and jumps, with its body, beak, eyes and tail riding along as children.</summary>
    private void Bird(Scene scene, Vector3 start)
    {
        var bird = scene.Spawn(
            Transform.Facing(start, -Vector3.UnitZ),
            Collider.Capsule(height: 1.1f, radius: 0.4f),
            new Character(),
            new Jumper(JumpSpeed: 9.5f, Gravity: 24f),
            new Hero(start));

        // A cone points up, so tip it forward for the beak and back for the tail. The bird faces +z.
        var straight = Quaternion.Identity;
        var forward = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
        var back = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI * 0.7f);
        Part(scene, bird, new Transform(new Vector3(0, 0.55f, 0), straight, new Vector3(0.9f, 0.9f, 1f)), look.Sphere, Palette.Feathers);
        Part(scene, bird, new Transform(new Vector3(0, 0.6f, 0.55f), forward, new Vector3(0.25f, 0.3f, 0.25f)), look.Cone, Palette.Beak);
        Part(scene, bird, new Transform(new Vector3(0, 0.7f, -0.5f), back, new Vector3(0.3f, 0.5f, 0.12f)), look.Cone, Palette.Feathers);
        Part(scene, bird, new Transform(new Vector3(0.45f, 0.5f, -0.05f), straight, new Vector3(0.15f, 0.5f, 0.7f)), look.Sphere, Palette.Wing);
        Part(scene, bird, new Transform(new Vector3(-0.45f, 0.5f, -0.05f), straight, new Vector3(0.15f, 0.5f, 0.7f)), look.Sphere, Palette.Wing);
        Part(scene, bird, new Transform(new Vector3(0.2f, 0.78f, 0.38f), straight, new Vector3(0.12f)), look.Sphere, Palette.Ink);
        Part(scene, bird, new Transform(new Vector3(-0.2f, 0.78f, 0.38f), straight, new Vector3(0.12f)), look.Sphere, Palette.Ink);
    }

    /// <summary>One piece of the bird. Its place is measured from the bird's feet.</summary>
    private static void Part(Scene scene, Entity bird, Transform place, ModelRef shape, Vector3 color) =>
        scene.Spawn(new Parent(bird), place, new Model(shape, color));
}

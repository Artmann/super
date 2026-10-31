using System.Numerics;
using Rendition;
using Super;
using Super.Ecs;
using Super.Ui;

namespace Reef;

/// <summary>A wide view of thousands of schooling fish over a coral reef.</summary>
internal sealed class ReefGame(World world, ReefSettings settings, GameRandom random, GameTime time, Content content, GameStats stats) : IGame
{
    private const float SwimAmplitude = 0.09f; // Of the model's length, which the transform scales to the real one.
    private const int SeabedKey = -1; // Entity IDs count up from 0.
    private const float CameraOrbitSeconds = 240f;
    private const float CameraDistance = 52f;
    private const float CameraHeight = 9f;

    private static readonly UnderwaterSettings Water = new()
    {
        Absorption = new Vector3(0.075f, 0.026f, 0.018f),
        ShallowColor = new Vector3(0.11f, 0.42f, 0.55f),
        DeepColor = new Vector3(0.01f, 0.09f, 0.17f),
        SurfaceHeight = 30f,
        CausticStrength = 2.2f,
        CausticScale = 0.11f,
        ShaftStrength = 0.11f,
        ShaftDistance = 55f,
    };

    internal World World => world;

    internal ReefSettings Settings => settings;

    private Boids _boids = null!;
    private ModelRef[] _fishModels = [];
    private ModelRef _seabed;

    public void Load()
    {
        var species = settings.Species;
        _fishModels = species.Select(kind => content.Model(kind.Model)).ToArray();
        _seabed = content.Mesh(Seabed.CreateMesh(random.Stream("sand")));

        SpawnCoral(random.Stream("coral"));
        int schools = SpawnFish(random.Stream("fish"));
        _boids = new Boids(world, settings, random.Stream("schools"), schools);
    }

    public UiView? Ui => new StatsPanel(stats.Latest, settings.Species.Sum(kind => kind.Count))
        .Padding(16f)
        .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    public void Update(GameTime gameTime) => _boids.Update(gameTime.TickSeconds);

    public void Draw(View view)
    {
        float angle = (float)(time.Tick + view.Blend) * time.TickSeconds * MathF.Tau / CameraOrbitSeconds;
        var camera = new Vector3(MathF.Sin(angle) * CameraDistance, CameraHeight, MathF.Cos(angle) * CameraDistance);
        view.Camera(camera, new Vector3(0f, 12f, 0f), 58f * MathF.PI / 180f);
        view.Sun(new Vector3(0.35f, 1f, 0.2f), new Vector3(7f, 7.4f, 7f));
        view.Shadows(75f);
        view.Water(Water);
        view.Finish(new PostSettings { Contrast = 0.12f, Saturation = 0.12f, Vignette = 0.4f, Sharpen = 0.35f });

        view.Model(SeabedKey, _seabed, Matrix4x4.Identity);
        view.Models(world);
    }

    private int SpawnFish(RandomStream stream)
    {
        int firstSchool = 0;
        var species = settings.Species;
        for (int kind = 0; kind < species.Count; kind++)
        {
            var details = species[kind];
            var centers = new Vector3[details.Schools];
            for (int i = 0; i < centers.Length; i++)
            {
                var spot = stream.InsideCircle(settings.Radius * 0.7f);
                centers[i] = new Vector3(spot.X, stream.Between(8f, settings.SurfaceHeight - 8f), spot.Y);
            }

            for (int i = 0; i < details.Count; i++)
            {
                int school = i % details.Schools;
                var position = centers[school] + stream.InsideSphere(MathF.Cbrt(details.Count / (float)details.Schools) * details.Personal * 1.2f);
                position.Y = Math.Clamp(position.Y, Seabed.Height(position.X, position.Z) + 1f, settings.SurfaceHeight - 1f);
                var velocity = stream.Direction3D() * details.MinSpeed;

                var entity = world.Create();
                world.Add(entity, Transform.Facing(position, velocity, details.Length));
                world.Add(entity, new Velocity(velocity));
                world.Add(entity, new Fish(kind, firstSchool + school));
                world.Add(entity, new Model(_fishModels[kind]));
                world.Add(entity, new Swim(SwimAmplitude, 1f, stream.Angle()));
                world.Add(entity, new Smoothed());
            }

            firstSchool += details.Schools;
        }

        return firstSchool;
    }

    private void SpawnCoral(RandomStream stream)
    {
        var coral = content.Model("reef/coral.rmodel");
        var softCoral = content.Model("reef/softcoral.rmodel");
        for (int cluster = 0; cluster < settings.CoralClusters; cluster++)
        {
            var center = stream.InsideCircle(settings.Radius * 1.3f);
            AddScenery(softCoral, center, stream.Between(0.8f, 1.4f), stream);

            int pieces = stream.Between(5, 11);
            for (int i = 0; i < pieces; i++)
            {
                AddScenery(coral, center + stream.InsideCircle(9f), stream.Between(0.7f, 2f), stream);
            }
        }
    }

    private void AddScenery(ModelRef model, Vector2 spot, float scale, RandomStream stream)
    {
        var transform = new Transform(
            new Vector3(spot.X, Seabed.Height(spot.X, spot.Y) - 0.15f, spot.Y),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, stream.Angle()),
            new Vector3(scale));
        var entity = world.Create();
        world.Add(entity, transform);
        world.Add(entity, new Model(model));
    }
}

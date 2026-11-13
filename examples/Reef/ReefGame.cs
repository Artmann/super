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
    private const int BoulderShapes = 4;
    private const int GrowthShapes = 4; // Of each kind of coral grown in code.
    private const int SeagrassMeadows = 40;
    private const float SurgeSeconds = 7f;
    private const float SurgeStrength = 0.45f;

    private static readonly Vector3 SurgeDirection = Vector3.Normalize(new Vector3(0.8f, 0f, 0.6f));

    // Living coral is bright: these tint the pale coral skin, one colony each. The water soaks up red first, so
    // orange turns brown a few metres off; pinks, lilacs and yellows keep their colour further.
    private static readonly Vector3[] CoralTints =
    [
        new(1f, 0.35f, 0.55f), new(0.7f, 0.4f, 1f), new(1f, 0.85f, 0.25f), new(1f, 0.5f, 0.25f), new(0.55f, 1f, 0.7f),
        new(1f, 0.95f, 0.85f),
    ];

    private static readonly Vector3[] FanTints = [new(0.85f, 0.25f, 0.75f), new(1f, 0.3f, 0.3f), new(1f, 0.75f, 0.2f)];

    private static readonly UnderwaterSettings Water = new()
    {
        Absorption = new Vector3(0.045f, 0.024f, 0.018f),
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
        _fishModels = species.Select(kind => settings.Model(content, kind.Model)).ToArray();
        _seabed = content.Mesh(Seabed.CreateMesh(settings.Material(content, "sand")));

        var clusters = SpawnCoral(random.Stream("coral"), random.Stream("rocks"));
        SpawnGrowth(clusters, random.Stream("growth"));
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
        view.Shadows(25f, 3);
        view.Water(Water);

        // The swell rocks the seagrass and sea fans back and forth: the wind's direction flips with the surge, and its
        // strength goes through zero, so the lean swings smoothly from one side to the other.
        float seconds = (float)(time.Tick + view.Blend) * time.TickSeconds;
        float surge = MathF.Sin(seconds * MathF.Tau / SurgeSeconds);
        view.Wind(SurgeDirection * MathF.Sign(surge), SurgeStrength * MathF.Abs(surge), gustiness: 0.3f);
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

    /// <summary>Lays the reef's clusters of rock and modelled coral, and gives back where each one is.</summary>
    private Vector2[] SpawnCoral(RandomStream stream, RandomStream rockStream)
    {
        var coral = settings.Model(content, settings.Coral);
        var softCoral = settings.Model(content, settings.SoftCoral);
        var rock = settings.Material(content, "reef-rock");
        var boulders = new ModelRef[BoulderShapes];
        for (int i = 0; i < boulders.Length; i++)
        {
            boulders[i] = content.Mesh(Boulders.Create(rock, rockStream));
        }

        var centers = new Vector2[settings.CoralClusters];
        for (int cluster = 0; cluster < centers.Length; cluster++)
        {
            var center = stream.InsideCircle(settings.Radius * 1.3f);
            centers[cluster] = center;
            AddScenery(softCoral, center, stream.Between(0.8f, 1.4f), stream);

            // The coral grows on old reef rock: a few boulders round the cluster, half sunk in the sand.
            int rocks = rockStream.Between(2, 5);
            for (int i = 0; i < rocks; i++)
            {
                float size = rockStream.Between(1.5f, 4.5f);
                var scale = new Vector3(size * rockStream.Between(0.8f, 1.3f), size * rockStream.Between(0.4f, 0.75f), size);
                AddScenery(boulders[rockStream.Between(0, BoulderShapes - 1)], center + rockStream.InsideCircle(8f), scale, scale.Y * 0.2f, rockStream.Angle());
            }

            int pieces = stream.Between(2, 5);
            for (int i = 0; i < pieces; i++)
            {
                AddScenery(coral, center + stream.InsideCircle(9f), stream.Between(0.7f, 2f), stream);
            }
        }

        return centers;
    }

    /// <summary>
    /// Grows the coral made in code round each cluster: staghorn, brain and table coral in bright colours, sea fans and
    /// tufts of seagrass, and meadows of seagrass on the open sand between.
    /// </summary>
    private void SpawnGrowth(Vector2[] clusters, RandomStream stream)
    {
        var polyps = settings.Material(content, "coral-polyps");
        var staghorn = new Growth(Grow(() => Corals.Staghorn(polyps, stream)), CoralTints, 0.9f, 1.6f);
        var tables = new Growth(Grow(() => Corals.Table(polyps, stream)), CoralTints, 0.8f, 1.4f);
        var fans = new Growth(Grow(() => Corals.SeaFan(polyps with { Wind = 0.3f }, stream)), FanTints, 1f, 2f, AcrossSurge: true);
        var brain = Grow(() => Boulders.Create(settings.Material(content, "brain-coral"), stream, typicalWidth: 1.4f, lumpiness: 0.35f));
        var seagrassShapes = Grow(() => Corals.Seagrass(settings.Material(content, "seagrass") with { Foliage = true, Translucency = 0.5f, Wind = 1f }, stream));
        var seagrass = new Growth(seagrassShapes, null, 0.8f, 1.6f);
        var meadow = seagrass with { MinScale = 0.9f, MaxScale = 1.7f };

        foreach (var center in clusters)
        {
            Scatter(staghorn, center, 7f, stream.Between(3, 6), stream);
            Scatter(tables, center, 8f, stream.Between(0, 2), stream);
            Scatter(fans, center, 9f, stream.Between(1, 3), stream);
            Scatter(seagrass, center, 14f, stream.Between(4, 8), stream);
            int brains = stream.Between(1, 3);
            for (int i = 0; i < brains; i++)
            {
                float size = stream.Between(0.9f, 1.8f);
                var scale = new Vector3(size, size * stream.Between(0.55f, 0.75f), size);
                AddScenery(brain[stream.Between(0, GrowthShapes - 1)], center + stream.InsideCircle(8f), scale, 0.1f * size, stream.Angle());
            }
        }

        // Meadows of seagrass out on the open sand: a few dozen tufts close together.
        for (int i = 0; i < SeagrassMeadows; i++)
        {
            var center = stream.InsideCircle(settings.Radius * 1.4f);
            Scatter(meadow, center, 5f, stream.Between(10, 20), stream);
        }
    }

    /// <summary>A few shapes of one kind, so neighbours differ.</summary>
    private ModelRef[] Grow(Func<GeneratedMesh> create) => [.. Enumerable.Range(0, GrowthShapes).Select(_ => content.Mesh(create()))];

    /// <summary>Places <paramref name="count"/> of <paramref name="growth"/> within <paramref name="spread"/> metres of <paramref name="center"/>.</summary>
    private void Scatter(Growth growth, Vector2 center, float spread, int count, RandomStream stream)
    {
        var (shapes, tints, minScale, maxScale, acrossSurge) = growth;
        for (int i = 0; i < count; i++)
        {
            var shape = shapes[stream.Between(0, shapes.Length - 1)];
            Vector3? tint = tints is null ? null : tints[stream.Between(0, tints.Length - 1)];
            float yaw = acrossSurge ? MathF.Atan2(SurgeDirection.X, SurgeDirection.Z) + stream.Between(-0.35f, 0.35f) : stream.Angle();
            AddScenery(shape, center + stream.InsideCircle(spread), new Vector3(stream.Between(minScale, maxScale)), 0.05f, yaw, tint);
        }
    }

    /// <summary>
    /// One kind of coral or plant grown in code: its shapes, the tints its colonies pick from (none keeps the
    /// material's own colour) and how much each is scaled. Shapes that face along z, such as sea fans, can be turned
    /// <paramref name="AcrossSurge"/>, broadside to the swell as real ones grow; the rest face anywhere.
    /// </summary>
    private sealed record Growth(ModelRef[] Shapes, Vector3[]? Tints, float MinScale, float MaxScale, bool AcrossSurge = false);

    private void AddScenery(ModelRef model, Vector2 spot, float scale, RandomStream stream) =>
        AddScenery(model, spot, new Vector3(scale), 0.15f, stream.Angle());

    private void AddScenery(ModelRef model, Vector2 spot, Vector3 scale, float sink, float yaw, Vector3? tint = null)
    {
        var transform = new Transform(
            new Vector3(spot.X, Seabed.Height(spot.X, spot.Y) - sink, spot.Y),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw),
            scale);
        var entity = world.Create();
        world.Add(entity, transform);
        world.Add(entity, new Model(model, tint));
    }
}

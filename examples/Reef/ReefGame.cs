using System.Numerics;
using Rendition;
using Super;
using Super.Ecs;
using Super.Ui;

namespace Reef;

/// <summary>A wide view of thousands of schooling fish over a coral reef.</summary>
internal sealed class ReefGame(World world, ReefSettings settings, GameRandom random, GameTime time, Content content, GameStats stats) : IGame
{
    private const float SwimAmplitude = 0.09f; // Of the model's length.
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

        view.Model(0, _seabed, Matrix4x4.Identity);
        foreach (var row in world.Query<Scenery>())
        {
            view.Model(row.Entity.Id, row.First.Model, row.First.Transform);
        }

        float blend = view.Blend;
        foreach (var row in world.Query<Position, Velocity, Fish, PreviousPosition>())
        {
            var position = Vector3.Lerp(row.Fourth.Position, row.First.Value, blend);
            var velocity = Vector3.Lerp(row.Fourth.Velocity, row.Second.Value, blend);
            var kind = settings.Species[row.Third.Species];
            float speed = velocity.Length();
            float phase = row.Third.Phase + speed * time.TickSeconds * blend * Boids.StrokesPerMetre / kind.Length;
            view.Model(
                row.Entity.Id,
                _fishModels[row.Third.Species],
                FishTransform(position, velocity / speed, kind.Length),
                new SwimMotion(SwimAmplitude, 1f, phase));
        }
    }

    private static Matrix4x4 FishTransform(Vector3 position, Vector3 forward, float length)
    {
        var side = Vector3.Cross(Vector3.UnitY, forward);
        side = side.LengthSquared() > 1e-5f ? Vector3.Normalize(side) : Vector3.UnitX;
        var up = Vector3.Cross(forward, side);
        return new Matrix4x4(
            side.X * length, side.Y * length, side.Z * length, 0f,
            up.X * length, up.Y * length, up.Z * length, 0f,
            forward.X * length, forward.Y * length, forward.Z * length, 0f,
            position.X, position.Y, position.Z, 1f);
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
                world.Add(entity, new Position(position));
                world.Add(entity, new Velocity(velocity));
                world.Add(entity, new PreviousPosition(position, velocity));
                world.Add(entity, new Fish(kind, firstSchool + school, stream.Angle()));
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
        var transform = Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateRotationY(stream.Angle())
            * Matrix4x4.CreateTranslation(spot.X, Seabed.Height(spot.X, spot.Y) - 0.15f, spot.Y);
        world.Add(world.Create(), new Scenery(model, transform));
    }
}

using System.Numerics;
using Super;

namespace OutpostExample;

public sealed class OutpostGame(Field field, Player player, Enemies enemies) : IGame
{
    private static readonly Vector3 Grass = new(0.25f, 0.55f, 0.18f);
    private static readonly Vector3 Sand = new(0.8f, 0.6f, 0.3f);
    private static readonly Vector3 Stone = new(0.3f, 0.3f, 0.32f);

    public Camera Camera { get; } = new(new Vector3(0, 22, 16), Vector3.Zero) { WideAs = 16f / 9f };

    public Player Player => player;

    public Enemies Enemies => enemies;

    public void Load()
    {
        // Three walkers so there's something to watch. Waves replace these in chapter 6.
        for (int i = 0; i < 3; i++)
        {
            enemies.Spawn(EnemyKind.Walker).Distance = i * 3f;
        }
    }

    public void Update(GameTime time)
    {
        enemies.Update(time);
    }

    public void Draw(View view)
    {
        view.Camera(Camera);
        view.Lights.Sun(direction: new Vector3(1, 2, 1), color: new Vector3(3f));
        view.Surroundings.Sky(new SkySettings
        {
            Zenith = new Vector3(0.25f, 0.45f, 0.85f),
            Horizon = new Vector3(0.75f, 0.85f, 0.95f),
            Ground = new Vector3(0.08f, 0.12f, 0.06f),
        });

        for (int z = 0; z < Field.Depth; z++)
        {
            for (int x = 0; x < Field.Width; x++)
            {
                var tile = Matrix4x4.CreateScale(1.96f, 0.2f, 1.96f)
                    * Matrix4x4.CreateTranslation(field.Center(x, z) - new Vector3(0, 0.1f, 0));
                view.Model(10_000 + z * Field.Width + x, ModelRef.Cube, tile, field.IsRoad(x, z) ? Sand : Grass);
            }
        }

        var baseBox = Matrix4x4.CreateScale(1.6f) * Matrix4x4.CreateTranslation(field.BaseCenter + new Vector3(0, 0.8f, 0));
        view.Model(1, ModelRef.Cube, baseBox, Stone);

        foreach (var enemy in enemies.All)
        {
            var stats = Enemies.Stats(enemy.Kind);
            // The capsule is 2 metres tall, so lifting it by its size stands it on the road.
            var lift = new Vector3(0, stats.Size, 0);
            var scale = Matrix4x4.CreateScale(stats.Size);
            var previous = scale * Matrix4x4.CreateTranslation(enemy.PreviousPosition + lift);
            var current = scale * Matrix4x4.CreateTranslation(enemy.Position + lift);
            view.Model(100_000 + enemy.Id, ModelRef.Capsule, view.Smooth(previous, current), stats.Color);
        }
    }
}

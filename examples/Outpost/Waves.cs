using Super;

namespace OutpostExample;

/// <summary>Sends the enemies out, one wave after another.</summary>
public sealed class Waves : ISystem
{
    public const int Count = 10;

    private readonly Enemies _enemies;
    private readonly Queue<EnemyKind> _left = new();
    private float _wait;

    public Waves(Enemies enemies)
    {
        _enemies = enemies;
        Reset();
    }

    /// <summary>Seconds before the first enemy of the first wave, so the player can look around.</summary>
    public float FirstDelay { get; set; } = 3f;

    /// <summary>Seconds between two enemies of the same wave.</summary>
    public float Gap { get; set; } = 0.8f;

    /// <summary>Seconds between the last enemy of a wave and the first of the next.</summary>
    public float Pause { get; set; } = 5f;

    /// <summary>The wave that's coming or walking now, counting from 1.</summary>
    public int Number { get; private set; }

    /// <summary>True once the last enemy of the last wave is on the road.</summary>
    public bool AllSpawned => Number == Count && _left.Count == 0;

    /// <summary>Who walks in a wave: more walkers each time, runners from wave 3, brutes from wave 5.</summary>
    public static IReadOnlyList<EnemyKind> Makeup(int wave)
    {
        int walkers = 4 + wave;
        int runners = Math.Max(0, wave - 2);
        int brutes = Math.Max(0, (wave - 3) / 2);

        return
        [
            .. Enumerable.Repeat(EnemyKind.Walker, walkers),
            .. Enumerable.Repeat(EnemyKind.Runner, runners),
            .. Enumerable.Repeat(EnemyKind.Brute, brutes),
        ];
    }

    public void Update(GameTime time)
    {
        if (AllSpawned)
        {
            return;
        }

        _wait -= time.DeltaSeconds;
        if (_wait > 0f)
        {
            return;
        }

        if (_left.Count == 0)
        {
            Number++;
            Queue(Number);
        }

        _enemies.Spawn(_left.Dequeue());
        _wait = _left.Count > 0 ? Gap : Pause;
    }

    /// <summary>Back to the start of wave 1, for a new game.</summary>
    public void Reset()
    {
        _left.Clear();
        Number = 1;
        Queue(1);
        _wait = FirstDelay;
    }

    private void Queue(int wave)
    {
        foreach (var kind in Makeup(wave))
        {
            _left.Enqueue(kind);
        }
    }
}

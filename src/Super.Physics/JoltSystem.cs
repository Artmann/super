using JoltPhysicsSharp;

namespace Super.Physics;

/// <summary>
/// The Jolt physics system one <see cref="Physics"/> runs on, with the layer setup it was made with and the job system
/// that steps it. Starts Jolt the first time any is made.
/// </summary>
internal sealed class JoltSystem : IDisposable
{
    /// <summary>The most bodies one system holds.</summary>
    public const int MaxBodies = 65536;

    // joltc shares two things between every physics system, and neither is safe to use from two threads at once: one
    // scratch allocator, which stepping and walking characters use, and one table of the systems there are, which
    // making and disposing a system write and a step reads. So games running side by side, such as in tests, take
    // turns at all four.
    public static readonly Lock Shared = new();

    private static readonly Lock Starting = new();
    private static bool _started;

    private JobSystem? _jobs;

    public JoltSystem()
    {
        Start();

        // The system owns its three layer filters from here on, and frees them when it's disposed.
        var broadPhase = new BroadPhaseLayerInterfaceMask(1);
        broadPhase.ConfigureLayer(new BroadPhaseLayer(0), uint.MaxValue, 0);
        var settings = new PhysicsSystemSettings
        {
            MaxBodies = MaxBodies,
            MaxBodyPairs = MaxBodies,
            MaxContactConstraints = MaxBodies,
            ObjectLayerPairFilter = new ObjectLayerPairFilterMask(),
            BroadPhaseLayerInterface = broadPhase,
            ObjectVsBroadPhaseLayerFilter = new ObjectVsBroadPhaseLayerFilterMask(broadPhase),
        };
        lock (Shared)
        {
            System = new PhysicsSystem(settings);
        }
    }

    public PhysicsSystem System { get; }

    /// <summary>The threads that step the system, made the first time a step simulates anything.</summary>
    public JobSystem Jobs => _jobs ??= new JobSystemThreadPool();

    /// <summary>The object layer of a body on <paramref name="layers"/> that collides with <paramref name="mask"/>.</summary>
    public static ObjectLayer Layer(uint layers, uint mask) => ObjectLayerPairFilterMask.GetObjectLayer(layers, mask);

    public void Dispose()
    {
        lock (Shared)
        {
            System.Dispose();
        }

        _jobs?.Dispose();
    }

    private static void Start()
    {
        lock (Starting)
        {
            if (!_started)
            {
                if (!Foundation.Init())
                {
                    throw new InvalidOperationException("Jolt Physics didn't start. Check that joltc.dll is beside the game.");
                }

                _started = true;
            }
        }
    }
}

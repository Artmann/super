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

    // joltc gives every physics system one shared scratch allocator, which isn't safe to use from two threads at once.
    // Stepping and walking characters use it, so games running side by side, such as in tests, take turns.
    public static readonly Lock Scratch = new();

    private static readonly Lock Starting = new();
    private static bool _started;

    private readonly ObjectLayerPairFilterMask _pairs;
    private readonly BroadPhaseLayerInterfaceMask _broadPhase;
    private readonly ObjectVsBroadPhaseLayerFilterMask _objectVsBroadPhase;
    private JobSystem? _jobs;

    public JoltSystem()
    {
        Start();
        _pairs = new ObjectLayerPairFilterMask();
        _broadPhase = new BroadPhaseLayerInterfaceMask(1);
        _broadPhase.ConfigureLayer(new BroadPhaseLayer(0), uint.MaxValue, 0);
        _objectVsBroadPhase = new ObjectVsBroadPhaseLayerFilterMask(_broadPhase);
        System = new PhysicsSystem(new PhysicsSystemSettings
        {
            MaxBodies = MaxBodies,
            MaxBodyPairs = MaxBodies,
            MaxContactConstraints = MaxBodies,
            ObjectLayerPairFilter = _pairs,
            BroadPhaseLayerInterface = _broadPhase,
            ObjectVsBroadPhaseLayerFilter = _objectVsBroadPhase,
        });
    }

    public PhysicsSystem System { get; }

    /// <summary>The threads that step the system, made the first time a step simulates anything.</summary>
    public JobSystem Jobs => _jobs ??= new JobSystemThreadPool();

    /// <summary>The object layer of a body on <paramref name="layers"/> that collides with <paramref name="mask"/>.</summary>
    public static ObjectLayer Layer(uint layers, uint mask) => ObjectLayerPairFilterMask.GetObjectLayer(layers, mask);

    public void Dispose()
    {
        System.Dispose();
        _jobs?.Dispose();
        _objectVsBroadPhase.Dispose();
        _broadPhase.Dispose();
        _pairs.Dispose();
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

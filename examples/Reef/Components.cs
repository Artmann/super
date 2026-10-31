using System.Numerics;
using Super;

namespace Reef;

internal record struct Position(Vector3 Value);

internal record struct Velocity(Vector3 Value);

/// <summary>Where a fish was on the last tick, so frames can blend between ticks.</summary>
internal record struct PreviousPosition(Vector3 Position, Vector3 Velocity);

/// <summary>A fish's kind, its school, and how far through its tail beat it is.</summary>
internal record struct Fish(int Species, int School, float Phase);

/// <summary>Something fixed to the sea floor.</summary>
internal record struct Scenery(ModelRef Model, Matrix4x4 Transform);

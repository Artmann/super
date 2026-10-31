using System.Numerics;

namespace Reef;

internal record struct Velocity(Vector3 Value);

/// <summary>A fish's kind and its school. Where it is and how it looks are Super's Transform, Model, Swim and Smoothed.</summary>
internal record struct Fish(int Species, int School);

using System.Numerics;

namespace Super.Physics;

/// <summary>Where one part of a ragdoll is, to draw it: a box, ball or capsule placed by <see cref="Transform"/>, in metres.</summary>
/// <param name="Joint">The part's joint.</param>
/// <param name="Shape">Its shape. A capsule runs along the transform's y.</param>
/// <param name="Transform">Its middle and turn in the world.</param>
/// <param name="HalfSize">A box's half size, or for a capsule, half the length of its straight middle in y.</param>
/// <param name="Radius">A ball's or capsule's radius.</param>
/// <param name="Simulated">Whether physics moves it, rather than the animation.</param>
public readonly record struct RagdollShapeView(string Joint, RagdollShape Shape, Matrix4x4 Transform, Vector3 HalfSize, float Radius, bool Simulated);

using System.Numerics;

namespace SkylarkExample;

/// <summary>An island to stand on, given by the middle of its top. A tall one is a rock; the rest are grass on soil.</summary>
public readonly record struct Island(Vector3 Top, float Width, float Depth, float Height = 0.5f);

/// <summary>A platform's two ends, by the middle of its top, and the seconds it takes one way.</summary>
public readonly record struct LiftPath(Vector3 From, Vector3 To, float Seconds);

/// <summary>Everything in one level. Gems, crates, trees and the goal are placed by where they stand.</summary>
public sealed record Level(string Name, Vector3 Start, Island[] Islands, LiftPath[] Lifts, Vector3[] Gems, Vector3[] Crates, Vector3[] Trees, Vector3 Goal);

/// <summary>The levels, and which one the journey is on.</summary>
public sealed class Levels(Journey journey)
{
    /// <summary>Fall this far below where a level starts and the bird goes back home.</summary>
    public const float FallDepth = 8f;

    private static readonly Level FirstHops = new(
        "First hops",
        Start: new Vector3(0, 0, 1),
        Islands:
        [
            new Island(new Vector3(0, 0, 0), 8, 8),
            new Island(new Vector3(2.5f, 2.9f, -2.5f), 2, 2, Height: 2.9f),
            new Island(new Vector3(0, 0.6f, -8), 4, 4),
            new Island(new Vector3(0, 1.2f, -14), 4, 4),
            new Island(new Vector3(17, 1.2f, -14), 6, 6),
        ],
        Lifts: [new LiftPath(new Vector3(4.5f, 1.2f, -14), new Vector3(11.5f, 1.2f, -14), 3f)],
        Gems:
        [
            new Vector3(-2.5f, 0, -2.5f), new Vector3(2.5f, 2.9f, -2.5f), new Vector3(0, 0.6f, -8),
            new Vector3(0, 1.2f, -14), new Vector3(8, 1.4f, -14),
        ],
        Crates: [new Vector3(2.5f, 0, 0.5f)],
        Trees: [new Vector3(3, 0, 2.8f), new Vector3(-3, 0, 2.5f), new Vector3(19.2f, 1.2f, -16.2f)],
        Goal: new Vector3(18, 1.2f, -14));

    private static readonly Level TheClimb = new(
        "The climb",
        Start: new Vector3(0, 0, 2),
        Islands:
        [
            new Island(new Vector3(0, 0, 0), 8, 8),
            new Island(new Vector3(0, 1, -9), 4, 4),
            new Island(new Vector3(0, 5, -19), 5, 5),
            new Island(new Vector3(-17, 5.6f, -19), 6, 6),
        ],
        Lifts:
        [
            new LiftPath(new Vector3(0, 1, -13.5f), new Vector3(0, 5, -13.5f), 3f),
            new LiftPath(new Vector3(-5, 5, -19), new Vector3(-12, 5, -19), 3f),
        ],
        Gems: [new Vector3(0, 0, -2), new Vector3(0, 1, -9), new Vector3(1.5f, 5, -20.5f), new Vector3(-8.5f, 5.2f, -19)],
        Crates: [],
        Trees: [new Vector3(3, 0, 3), new Vector3(-1.6f, 5, -20.6f), new Vector3(-19, 5.6f, -21)],
        Goal: new Vector3(-18, 5.6f, -19));

    /// <summary>Every level, in the order they're played.</summary>
    public static IReadOnlyList<Level> All { get; } = [FirstHops, TheClimb];

    /// <summary>The level the journey is on.</summary>
    public Level Current => All[journey.Level];
}

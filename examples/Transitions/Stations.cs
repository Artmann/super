using System.Numerics;
using Super;

namespace TransitionsExample;

/// <summary>The plaza's three stations of shapes, warm, blue and green, some of them turning and bouncing.</summary>
internal sealed class Stations(World world)
{
    // The stations' middles, left to right.
    public static readonly Vector3 Warm = new(-7f, 0f, 0f);
    public static readonly Vector3 Blue = new(0f, 0f, -3f);
    public static readonly Vector3 Green = new(7f, 0f, 0f);

    private const int Cones = 6;

    private readonly Entity[] _cones = new Entity[Cones];
    private Entity _ball;
    private Entity _ring;
    private Entity _capsule;

    /// <summary>Spawns the three stations' shapes, where they stand before anything turns.</summary>
    public void Spawn()
    {
        SpawnWarm();
        SpawnBlue();
        SpawnGreen();
        Turn(0f);
    }

    /// <summary>Moves the shapes that move to where <paramref name="angle"/> puts them.</summary>
    public void Turn(float angle)
    {
        world.Get<Transform>(_ball) = Transform.At(Warm + new Vector3(0.9f, 0.5f + MathF.Abs(MathF.Sin(angle * 3f)) * 1.2f, 0.4f));

        // Stood on its edge, then spun round the up axis.
        var ring = Quaternion.Concatenate(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle));
        world.Get<Transform>(_ring) = new Transform(Blue + new Vector3(0f, 1.45f, 0f), ring, new Vector3(1.3f));

        for (int i = 0; i < Cones; i++)
        {
            float around = i * MathF.Tau / Cones + angle * 0.5f;
            var place = Blue + new Vector3(MathF.Sin(around) * 2.4f, 0.5f, MathF.Cos(around) * 2.4f);
            world.Get<Transform>(_cones[i]) = new Transform(place, Quaternion.Identity, new Vector3(0.6f, 1f, 0.6f));
        }

        var rock = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Sin(angle * 2f) * 0.3f);
        world.Get<Transform>(_capsule) = new Transform(Green + new Vector3(1.6f, 1f, 1.2f), rock, Vector3.One);
    }

    private void SpawnWarm()
    {
        Place(ModelRef.Cube, Transform.At(Warm + new Vector3(-0.8f, 0.6f, 0f), 1.2f), new Vector3(0.85f, 0.25f, 0.18f));
        Place(ModelRef.Cube, new Transform(Warm + new Vector3(-0.8f, 1.6f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f), new Vector3(0.8f)), new Vector3(0.95f, 0.55f, 0.2f));
        _ball = Place(ModelRef.Sphere, Transform.Identity, new Vector3(0.95f, 0.75f, 0.3f));
        Place(ModelRef.Cone, Transform.At(Warm + new Vector3(0.2f, 0.5f, -1.4f)), new Vector3(0.75f, 0.35f, 0.25f));
    }

    private void SpawnBlue()
    {
        Place(ModelRef.Cylinder, new Transform(Blue + new Vector3(0f, 0.3f, 0f), Quaternion.Identity, new Vector3(1.4f, 0.6f, 1.4f)), new Vector3(0.75f, 0.78f, 0.82f));
        _ring = Place(ModelRef.Torus, Transform.Identity, new Vector3(0.2f, 0.4f, 0.9f));
        for (int i = 0; i < Cones; i++)
        {
            _cones[i] = Place(ModelRef.Cone, Transform.Identity, new Vector3(0.3f, 0.55f, 0.85f));
        }
    }

    private void SpawnGreen()
    {
        ReadOnlySpan<float> heights = [0.8f, 1.6f, 2.4f, 1.2f];
        for (int i = 0; i < heights.Length; i++)
        {
            var place = Green + new Vector3(-1.2f + i * 0.8f, heights[i] / 2f, -0.6f + (i % 2) * 0.6f);
            Place(ModelRef.Cylinder, new Transform(place, Quaternion.Identity, new Vector3(0.55f, heights[i], 0.55f)), new Vector3(0.25f, 0.6f + i * 0.08f, 0.3f));
        }

        _capsule = Place(ModelRef.Capsule, Transform.Identity, new Vector3(0.6f, 0.85f, 0.35f));
    }

    private Entity Place(ModelRef model, Transform at, Vector3 color) => world.Spawn(at, new Model(model, color));
}

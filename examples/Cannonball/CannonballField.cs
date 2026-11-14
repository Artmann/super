using System.Numerics;
using Super;
using Super.Ecs;
using Super.Physics;

namespace Cannonball;

/// <summary>
/// The cannon and the field it fires over: crates stacked into a wall and two pyramids, a few big balls and hay bales,
/// all free to fly, on a wide static ground.
/// </summary>
public sealed class CannonballField(World world)
{
    /// <summary>How steeply the cannon points up, in radians.</summary>
    public const float Elevation = 0.7f;

    /// <summary>How long the barrel is, in metres.</summary>
    public const float BarrelLength = 3f;

    private const float CrateSize = 0.8f;

    // The wall sits a little to the camera's left of the line he flies along.
    private const float WallShift = 0.8f;

    // Balls are drawn and collide at this radius: a unit sphere scaled to twice it.
    private const float BallRadius = 0.6f;

    // Above any entity's ID, so these keys never meet the ones view.Models gives entities.
    private const int CannonKey = 1_000_000;

    /// <summary>The point the cannon turns about, at the back of the barrel.</summary>
    public static readonly Vector3 Breech = new(0f, 1.2f, 0f);

    private static readonly Vector3[] Woods = [new(0.62f, 0.42f, 0.22f), new(0.7f, 0.5f, 0.28f), new(0.55f, 0.36f, 0.2f)];

    private readonly List<Entity> _props = [];

    /// <summary>Which way the barrel points.</summary>
    public static Vector3 Aim => new(0f, MathF.Sin(Elevation), MathF.Cos(Elevation));

    /// <summary>Clears away the props, wherever they flew, and stacks them up again.</summary>
    public void Build()
    {
        foreach (var prop in _props)
        {
            world.Destroy(prop);
        }

        _props.Clear();
        var ground = Prop(new Transform(new Vector3(0f, -0.5f, 20f), Quaternion.Identity, new Vector3(120f, 1f, 160f)), new Vector3(0.32f, 0.5f, 0.22f));
        world.Add(ground, Collider.Box(Vector3.One));
        world.Add(ground, Body.Static);
        BuildWall();
        BuildPyramids();
        BuildBalls();
        BuildBales();
    }

    /// <summary>Draws the cannon. It's scenery the stuntman flies out of, so it has no collider to catch on.</summary>
    public static void DrawCannon(View view)
    {
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (MathF.PI / 2f) - Elevation);
        var barrel = Matrix4x4.CreateScale(1.1f, BarrelLength, 1.1f) * Matrix4x4.CreateTranslation(0f, BarrelLength / 2f, 0f)
            * Matrix4x4.CreateFromQuaternion(tilt) * Matrix4x4.CreateTranslation(Breech);
        view.Model(CannonKey, ModelRef.Cylinder, barrel, new Vector3(0.12f, 0.12f, 0.14f));
        var muzzle = Matrix4x4.CreateScale(1.4f, 0.25f, 1.4f) * Matrix4x4.CreateTranslation(0f, BarrelLength - 0.1f, 0f)
            * Matrix4x4.CreateFromQuaternion(tilt) * Matrix4x4.CreateTranslation(Breech);
        view.Model(CannonKey + 1, ModelRef.Cylinder, muzzle, new Vector3(0.7f, 0.15f, 0.12f));
        view.Model(CannonKey + 2, ModelRef.Cube, Matrix4x4.CreateScale(1.6f, 0.9f, 2.4f) * Matrix4x4.CreateTranslation(0f, 0.45f, -0.2f), new Vector3(0.45f, 0.25f, 0.12f));
        foreach (float side in (ReadOnlySpan<float>)[-1f, 1f])
        {
            var wheel = Matrix4x4.CreateScale(1.5f, 0.2f, 1.5f) * Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(side * 0.95f, 0.75f, 0f);
            view.Model(CannonKey + (side < 0f ? 3 : 4), ModelRef.Cylinder, wheel, new Vector3(0.5f, 0.12f, 0.1f));
        }
    }

    /// <summary>A wall of crates, each row set half a crate over from the one below.</summary>
    private void BuildWall()
    {
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 6; column++)
            {
                float x = WallShift + ((column - 2.5f + (row % 2 * 0.5f)) * CrateSize);
                Crate(new Vector3(x, (row + 0.5f) * CrateSize, 30f), Woods[(row + column) % Woods.Length]);
            }
        }
    }

    /// <summary>A pyramid of crates either side of the way he flies.</summary>
    private void BuildPyramids()
    {
        foreach (float side in (ReadOnlySpan<float>)[-4.5f, 4.5f])
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4 - row; column++)
                {
                    float x = side + ((column - ((3 - row) / 2f)) * CrateSize);
                    Crate(new Vector3(x, (row + 0.5f) * CrateSize, 26f), Woods[(row + column + 1) % Woods.Length]);
                }
            }
        }
    }

    /// <summary>A ring of balls before the pyramids, three along the top of the wall and a few on the ground around it.</summary>
    private void BuildBalls()
    {
        Vector3[] paints = [new(0.85f, 0.2f, 0.15f), new(0.95f, 0.75f, 0.15f), new(0.2f, 0.45f, 0.85f)];
        for (int i = 0; i < 6; i++)
        {
            float angle = i * 1.1f;
            Ball(new Vector3(MathF.Cos(angle) * 3f, BallRadius, 22f + (MathF.Sin(angle) * 2f)), paints[i % paints.Length]);
        }

        // Each sits on the middle of a top-row crate, which is set half a crate over from the row below.
        float top = (4f * CrateSize) + BallRadius;
        Vector3[] atWall =
        [
            new(WallShift - CrateSize, top, 30f), new(WallShift + CrateSize, top, 30f), new(WallShift + (3f * CrateSize), top, 30f),
            new(WallShift - (3.5f * CrateSize), BallRadius, 29f), new(WallShift + (4f * CrateSize), BallRadius, 30.6f),
            new(WallShift + 0.5f, BallRadius, 32.5f), new(WallShift - 1.5f, BallRadius, 33.2f),
        ];
        for (int i = 0; i < atWall.Length; i++)
        {
            Ball(atWall[i], paints[(i + 1) % paints.Length]);
        }
    }

    private void Ball(Vector3 at, Vector3 paint)
    {
        var ball = Prop(Transform.At(at, 2f * BallRadius), paint, ModelRef.Sphere);
        world.Add(ball, Collider.Ball(0.5f));
        world.Add(ball, Body.Dynamic(mass: 6f, bounciness: 0.5f));
    }

    /// <summary>Hay bales to land on, light enough to scatter.</summary>
    private void BuildBales()
    {
        for (int i = 0; i < 8; i++)
        {
            var at = new Vector3(((i % 4) - 1.5f) * 1.4f, 0.3f, 18f + ((i / 4) * 1.3f));
            var bale = Prop(new Transform(at, Quaternion.CreateFromYawPitchRoll(i * 0.4f, 0f, 0f), new Vector3(1.2f, 0.6f, 0.7f)), new Vector3(0.85f, 0.72f, 0.35f));
            world.Add(bale, Collider.Box(Vector3.One));
            world.Add(bale, Body.Dynamic(mass: 15f, friction: 0.9f));
        }
    }

    private void Crate(Vector3 at, Vector3 color)
    {
        var crate = Prop(new Transform(at, Quaternion.Identity, new Vector3(CrateSize)), color);
        world.Add(crate, Collider.Box(Vector3.One));
        world.Add(crate, Body.Dynamic(mass: 8f, friction: 0.6f));
    }

    private Entity Prop(Transform transform, Vector3 color, ModelRef? model = null)
    {
        var entity = world.Create();
        world.Add(entity, transform);
        world.Add(entity, new Model(model ?? ModelRef.Cube, color));
        world.Add(entity, new Smoothed());
        _props.Add(entity);
        return entity;
    }
}

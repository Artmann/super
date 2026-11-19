using System.Numerics;
using Super;

namespace Crypt;

/// <summary>
/// A lantern on a chain from the vault, swinging a little, its beam kept on a spot below. The chain, the lantern and
/// its two lights all hang from one entity at the hook, which <see cref="Swing"/> turns.
/// </summary>
internal sealed class SwingingLantern
{
    private readonly World _world;
    private readonly Vector3 _hook;
    private readonly Vector3 _aim;
    private readonly Entity _pivot;
    private readonly Entity _beam;

    /// <summary>Hangs <paramref name="model"/> from <paramref name="hook"/> down to <paramref name="at"/>, its beam on <paramref name="aim"/>.</summary>
    public SwingingLantern(World world, ModelRef model, Vector3 hook, Vector3 at, Vector3 aim)
    {
        _world = world;
        _hook = hook;
        _aim = aim;
        float chain = hook.Y - at.Y;

        _pivot = world.Spawn(Transform.At(hook));
        world.Spawn(new Transform(new Vector3(0f, -chain / 2f, 0f), Quaternion.Identity, new Vector3(0.015f, chain, 0.015f)), new Model(ModelRef.Cylinder, new Vector3(0.05f)), new Parent(_pivot));
        world.Spawn(Transform.At(new Vector3(0f, -chain - 0.47f, 0f), 1.6f), new Model(model), new Parent(_pivot));
        _beam = world.Spawn(
            Transform.Identity,
            new SpotLight(new Vector3(0f, -chain - 0.52f, 0f), -Vector3.UnitY, new Vector3(30f, 21f, 10f), 6f) { InnerAngle = 0.3f, OuterAngle = 0.55f },
            new Parent(_pivot));
        world.Spawn(Transform.Identity, new PointLight(new Vector3(0f, -chain - 0.32f, 0f), new Vector3(0.35f, 0.22f, 0.08f), 3f) { Shadows = false }, new Parent(_pivot));
        Swing(0f);
    }

    /// <summary>Swings the lantern to where it is at <paramref name="seconds"/> of game time.</summary>
    public void Swing(float seconds)
    {
        var swing = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.04f * MathF.Sin(seconds * 1.3f));
        _world.Get<Transform>(_pivot).Rotation = swing;

        // The beam's direction counts from the pivot, so turn the way to the aim back by the swing.
        ref var beam = ref _world.Get<SpotLight>(_beam);
        var from = _hook + Vector3.Transform(beam.Position, swing);
        var toAim = Vector3.Normalize(_aim - from);
        beam = beam with { Direction = Vector3.Transform(toAim, Quaternion.Conjugate(swing)) };
    }
}

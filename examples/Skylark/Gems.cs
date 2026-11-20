using System.Numerics;
using Rendition;
using Super;
using Super.Physics;

namespace SkylarkExample;

/// <summary>Spins the gems, collects the ones the bird touches, and lights the beacon when none are left.</summary>
public sealed class Gems(World world, Physics physics, Levels levels) : ISystem
{
    /// <summary>The sparkle where a gem was: a quick ring of bright bits.</summary>
    private static readonly ParticleEmitter Sparkle = new ParticleEmitter(count: 24, lifetime: 0.5f)
        .FromSphere(0.2f)
        .Outward(3f)
        .Gravity(new Vector3(0f, -4f, 0f))
        .Size(0.18f, 0.02f)
        .Color(new Vector3(0.7f, 1f, 1f), new Vector3(0.3f, 0.8f, 1f))
        .Glow(3f)
        .Fade(0f, 0.3f);

    /// <summary>How many gems the level has.</summary>
    public int Total => levels.Current.Gems.Length;

    /// <summary>How many of them the bird has found: the ones no longer in the world.</summary>
    public int Found => Total - world.Count<Gem>();

    public void Update(GameTime time)
    {
        Spin(time);
        Collect();
        Beacon();
    }

    /// <summary>Turns each gem and bobs it up and down.</summary>
    private void Spin(GameTime time)
    {
        float turn = (float)time.TotalSeconds;
        foreach (var row in world.Query<Gem, Transform>())
        {
            row.Second.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn * 2f);
            row.Second.Position.Y = row.First.Height + MathF.Sin(turn * 3f + row.Second.Position.X) * 0.12f;
        }
    }

    /// <summary>Takes the gems the bird walked into, leaving a sparkle where each was.</summary>
    private void Collect()
    {
        foreach (var touch in physics.Triggers.Entered<Gem, Hero>())
        {
            // A Burst plays once and then clears itself away. It belongs to no scene, so it outlives a level change.
            world.Spawn(Transform.At(world.Get<Transform>(touch.Trigger).Position), new Burst(Sparkle));
            world.Destroy(touch.Trigger);
        }
    }

    /// <summary>Lights the beacon once every gem is found, and notices the bird reaching it.</summary>
    private void Beacon()
    {
        if (world.Count<Gem>() > 0)
        {
            return;
        }

        var flame = world.Only<Goal>().Flame;
        if (world.Has<Hidden>(flame))
        {
            world.Remove<Hidden>(flame);
        }

        foreach (var _ in physics.Triggers.Inside<Goal, Hero>())
        {
            world.Only<Goal>().Reached = true;
        }
    }
}

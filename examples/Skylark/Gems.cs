using System.Numerics;
using Rendition;
using Super;
using Super.Physics;

namespace SkylarkExample;

/// <summary>Spins the gems, collects the ones the bird touches, and lights the beacon when none are left.</summary>
public sealed class Gems(World world, Physics physics, Journey journey, Levels levels) : ISystem
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

    private readonly List<(Vector3 Position, long Tick)> _sparkles = [];

    /// <summary>How many gems the level has.</summary>
    public int Total => levels.Current.Gems.Length;

    /// <summary>True once every gem is found and the beacon burns.</summary>
    public bool AllFound => journey.Gems >= Total;

    public void Update(GameTime time)
    {
        Spin(time);
        Collect(time);
        Beacon();
    }

    /// <summary>Forgets the sparkles, for a new level.</summary>
    public void Clear() => _sparkles.Clear();

    /// <summary>Draws the sparkles. A burst that plays once is drawn, not spawned.</summary>
    public void Draw(View view)
    {
        for (int index = 0; index < _sparkles.Count; index++)
        {
            var (position, tick) = _sparkles[index];
            view.Effects.Particles(HashCode.Combine(position, tick), Sparkle, position, view.SecondsSince(tick));
        }
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
    private void Collect(GameTime time)
    {
        // Everything that walked into an area this tick. Keep the pairs that are a gem and the bird.
        foreach (var touch in physics.Triggers.Entered)
        {
            if (world.Has<Gem>(touch.Trigger) && world.Has<Hero>(touch.Other))
            {
                _sparkles.Add((world.Get<Transform>(touch.Trigger).Position, time.Tick));
                world.Destroy(touch.Trigger);
                journey.Gems++;
            }
        }

        _sparkles.RemoveAll(sparkle => time.Tick - sparkle.Tick > 30);
    }

    /// <summary>Lights the beacon once every gem is found, and notices the bird reaching it.</summary>
    private void Beacon()
    {
        foreach (var row in world.Query<Goal, Transform>())
        {
            var flame = row.First.Flame;
            if (AllFound && world.Has<Hidden>(flame))
            {
                world.Remove<Hidden>(flame);
            }

            if (!AllFound)
            {
                continue;
            }

            foreach (var visitor in physics.Triggers.Inside(row.Entity))
            {
                if (world.Has<Hero>(visitor))
                {
                    journey.ReachedBeacon = true;
                }
            }
        }
    }
}

using System.Numerics;

namespace Super;

/// <summary>
/// An emitter's nodes sorted into the stages every particle runs through: start speeds, the
/// forces folded into one drag and one steady pull, motions, then looks. Each node keeps the
/// salt that keeps its dice its own.
/// </summary>
internal sealed class ParticleStages
{
    private readonly (SpeedNode Node, uint Salt)[] _speeds;
    private readonly (MotionNode Node, uint Salt)[] _motions;
    private readonly (LookNode Node, uint Salt)[] _looks;
    private readonly float _drag;
    private readonly Vector3 _pull;

    public ParticleStages(ParticleNode[] nodes)
    {
        var speeds = new List<(SpeedNode, uint)>();
        var motions = new List<(MotionNode, uint)>();
        var looks = new List<(LookNode, uint)>();
        var wind = Vector3.Zero;
        for (int i = 0; i < nodes.Length; i++)
        {
            uint salt = (uint)(i + 2) * 64;
            switch (nodes[i])
            {
                case SpeedNode speed:
                    speeds.Add((speed, salt));
                    break;
                case MotionNode motion:
                    motions.Add((motion, salt));
                    break;
                case LookNode look:
                    looks.Add((look, salt));
                    break;
                case DragNode drag:
                    _drag += drag.PerSecond;
                    break;
                case GravityNode gravity:
                    _pull += gravity.Pull;
                    break;
                case WindNode w:
                    wind += w.Velocity;
                    break;
            }
        }

        // Drag pulls a particle's speed towards the air's, which is the same as a steady
        // push of drag times the wind's speed.
        _pull += wind * _drag;
        _speeds = [.. speeds];
        _motions = [.. motions];
        _looks = [.. looks];
    }

    /// <summary>
    /// Where a particle is in the world <paramref name="age"/> seconds after its birth: thrown from
    /// its start by the speeds, slowed and pulled by the forces, then moved by the motions in turn.
    /// </summary>
    public Vector3 PositionAt(uint particle, float age, Vector3 local, Vector3 outward, in ParticlePlacement placement)
    {
        var velocity = Vector3.Zero;
        foreach (var (node, salt) in _speeds)
        {
            velocity += node.Speed(new ParticleDice(particle, salt), outward);
        }

        // Closed form of drag slowing the start speed while a steady force pulls:
        // travel is v (1 - e^-kt) / k, and the pull's share is g (t - that) / k.
        float travelled;
        float pulled;
        if (_drag > 0f)
        {
            travelled = (1f - MathF.Exp(-_drag * age)) / _drag;
            pulled = (age - travelled) / _drag;
        }
        else
        {
            travelled = age;
            pulled = 0.5f * age * age;
        }

        var position = Vector3.Transform(local, placement.ToWorld)
            + Vector3.TransformNormal(velocity, placement.ToWorld) * travelled
            + _pull * pulled;
        foreach (var (node, salt) in _motions)
        {
            position = node.Move(new ParticleDice(particle, salt), age, position, placement.MotionCentre);
        }

        return position;
    }

    /// <summary>
    /// How a particle looks at this age and fraction of its life: the looks in turn, starting
    /// from one metre wide, white and solid, turned by <paramref name="rotation"/>.
    /// </summary>
    public ParticleLook AppearanceAt(uint particle, float age, float life, float rotation)
    {
        var look = new ParticleLook { Size = 1f, Color = Vector3.One, Opacity = 1f, Rotation = rotation };
        foreach (var (node, salt) in _looks)
        {
            node.Apply(new ParticleDice(particle, salt), age, life, ref look);
        }

        return look;
    }
}

using System.Numerics;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>A ragdoll's bodies while it's on, one per part, joined to their parents, and where physics last had each.</summary>
internal sealed class RagdollParts
{
    private readonly PhysicsSystem _system;
    private readonly RagdollBody _body;
    private readonly Dictionary<uint, Entity> _owners;

    /// <param name="owners">Which entity each body in the world belongs to, which <see cref="Destroy"/> forgets these in.</param>
    public RagdollParts(PhysicsSystem system, RagdollBody body, Dictionary<uint, Entity> owners)
    {
        _system = system;
        _body = body;
        _owners = owners;
        int parts = body.PartTree.Joints.Length;
        Ids = new BodyID[parts];
        Constraints = new TwoBodyConstraint?[parts];
        Dynamic = new bool[parts];
        Positions = new Vector3[parts];
        Rotations = new Quaternion[parts];
    }

    /// <summary>The bodies, or null while the ragdoll is off.</summary>
    public JoltPhysicsSharp.Body[]? Handles { get; set; }

    public BodyID[] Ids { get; }

    /// <summary>Each part's joint to its parent; the pelvis, part 0, has none.</summary>
    public TwoBodyConstraint?[] Constraints { get; }

    /// <summary>Keeps parts that touch at rest from colliding.</summary>
    public GroupFilterTable? Filter { get; set; }

    /// <summary>Whether physics moves each part; the others follow the animation.</summary>
    public bool[] Dynamic { get; }

    /// <summary>Where each part's joint was after the last step, from <see cref="Read"/>.</summary>
    public Vector3[] Positions { get; }

    public Quaternion[] Rotations { get; }

    public bool Active => Handles is not null;

    /// <summary>Whether the bodies are there and physics moves at least one.</summary>
    public bool Simulating => Active && Array.IndexOf(Dynamic, true) >= 0;

    /// <summary>Whether the pelvis lies face up, by which way it faces. False when it isn't simulated.</summary>
    public bool IsFaceUp()
    {
        if (!Active || !Dynamic[0])
        {
            return false;
        }

        var rotation = _system.BodyInterface.GetRotation(in Ids[0]);
        return Vector3.Transform(_body.Fitting.PelvisForward, rotation).Y > 0f;
    }

    /// <summary>The mass-weighted velocity of the simulated parts.</summary>
    public Vector3 Velocity()
    {
        var bodies = _system.BodyInterface;
        var momentum = Vector3.Zero;
        float mass = 0f;
        for (int i = 0; i < Ids.Length; i++)
        {
            if (Dynamic[i])
            {
                momentum += bodies.GetLinearVelocity(in Ids[i]) * _body.Fitting.Masses[i];
                mass += _body.Fitting.Masses[i];
            }
        }

        return momentum / mass;
    }

    public void SetVelocity(Vector3 velocity)
    {
        var bodies = _system.BodyInterface;
        for (int i = 0; i < Ids.Length; i++)
        {
            if (Dynamic[i])
            {
                bodies.SetLinearVelocity(in Ids[i], in velocity);
                bodies.ActivateBody(in Ids[i]);
            }
        }
    }

    /// <summary>Gives every simulated part the same change in velocity, as a push spread over its mass.</summary>
    public void Push(Vector3 impulse, bool force)
    {
        var bodies = _system.BodyInterface;
        float mass = 0f;
        for (int i = 0; i < Ids.Length; i++)
        {
            mass += Dynamic[i] ? _body.Fitting.Masses[i] : 0f;
        }

        for (int i = 0; i < Ids.Length; i++)
        {
            if (!Dynamic[i])
            {
                continue;
            }

            var share = impulse * (_body.Fitting.Masses[i] / mass);
            if (force)
            {
                bodies.AddForce(in Ids[i], in share);
            }
            else
            {
                bodies.AddImpulse(in Ids[i], in share);
            }
        }
    }

    /// <summary>The simulated part nearest a point in the world.</summary>
    public int Nearest(Vector3 point)
    {
        var bodies = _system.BodyInterface;
        int nearest = -1;
        float best = float.MaxValue;
        for (int i = 0; i < Ids.Length; i++)
        {
            float distance = Vector3.DistanceSquared(bodies.GetCenterOfMassPosition(in Ids[i]), point);
            if (Dynamic[i] && distance < best)
            {
                (nearest, best) = (i, distance);
            }
        }

        return nearest;
    }

    /// <summary>Notes where physics has each part's joint now.</summary>
    public void Read()
    {
        var bodies = _system.BodyInterface;
        for (int i = 0; i < Ids.Length; i++)
        {
            Positions[i] = bodies.GetPosition(in Ids[i]);
            Rotations[i] = Quaternion.Normalize(bodies.GetRotation(in Ids[i]));
        }
    }

    /// <summary>Takes the joints and bodies out of the world, forgetting which entity owned them.</summary>
    public void Destroy()
    {
        if (Handles is null)
        {
            return;
        }

        foreach (var constraint in Constraints)
        {
            if (constraint is not null)
            {
                _system.RemoveConstraint(constraint);
                constraint.Dispose();
            }
        }

        Array.Clear(Constraints);
        var bodies = _system.BodyInterface;
        for (int i = 0; i < Ids.Length; i++)
        {
            if (Handles[i] is not null)
            {
                bodies.RemoveAndDestroyBody(in Ids[i]);
                _owners.Remove(Ids[i].ID);
            }
        }

        Handles = null;
        Filter?.Dispose();
        Filter = null;
    }
}

using System.Numerics;
using JoltPhysicsSharp;

namespace Super.Physics;

/// <summary>One entity's collider in the physics world, and what it was built from, to tell when it must be built again.</summary>
internal struct TrackedBody
{
    public BodyID Body;
    public JoltPhysicsSharp.Body Handle;
    public Collider Collider;
    public Body Settings;
    public bool Trigger;
    public bool Character;
    public uint Mask;
    public Vector3 Scale;
    public Vector3 Position;
    public Quaternion Rotation;
    public bool Moving;
}

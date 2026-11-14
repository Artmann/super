using System.Numerics;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>What started touching in a step, gathered from Jolt's reports into one contact per pair of entities.</summary>
internal sealed class ContactSet(Dictionary<uint, Entity> owners)
{
    // Jolt reports new contacts from its worker threads, so they're gathered under a lock and tidied after the step.
    private readonly List<Contact> _found = [];
    private readonly List<Contact> _contacts = [];

    public IReadOnlyList<Contact> List => _contacts;

    public void Added(PhysicsSystem system, in JoltPhysicsSharp.Body first, in JoltPhysicsSharp.Body second, in ContactManifold manifold, ref ContactSettings settings)
    {
        // Triggers collide with nothing in Jolt, so they never get here; a ragdoll's parts touching each other share an entity.
        if (!owners.TryGetValue(first.ID.ID, out var a) || !owners.TryGetValue(second.ID.ID, out var b) || a == b)
        {
            return;
        }

        // The normal points from the first body into the second; they close when the first moves along it faster.
        var normal = manifold.WorldSpaceNormal;
        var point = manifold.GetWorldSpaceContactPointOn1(0);
        float speed = MathF.Max(0f, Vector3.Dot(first.GetPointVelocity(in point) - second.GetPointVelocity(in point), normal));
        lock (_found)
        {
            _found.Add(new Contact(a, b, point, normal, speed));
        }
    }

    /// <summary>Turns what the step found into one contact per pair, the hardest, with the lower entity first.</summary>
    public void Gather()
    {
        _contacts.Clear();
        foreach (var found in _found)
        {
            var contact = EntityOrder.Instance.Compare(found.Entity, found.Other) <= 0
                ? found
                : new Contact(found.Other, found.Entity, found.Point, -found.Normal, found.Speed);
            int same = _contacts.FindIndex(known => known.Entity == contact.Entity && known.Other == contact.Other);
            if (same < 0)
            {
                _contacts.Add(contact);
            }
            else if (Harder(contact, _contacts[same]))
            {
                _contacts[same] = contact;
            }
        }

        _found.Clear();
        _contacts.Sort(static (x, y) =>
        {
            int order = EntityOrder.Instance.Compare(x.Entity, y.Entity);
            return order != 0 ? order : EntityOrder.Instance.Compare(x.Other, y.Other);
        });
    }

    /// <summary>
    /// Whether one contact between a pair beats another: the faster, and between equally fast ones, such as resting
    /// touches, the one with the lower point, so Jolt's thread order never picks.
    /// </summary>
    private static bool Harder(Contact contact, Contact known)
    {
        if (contact.Speed != known.Speed)
        {
            return contact.Speed > known.Speed;
        }

        var (a, b) = (contact.Point, known.Point);
        return a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;
    }
}

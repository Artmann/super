namespace Super.Physics;

public sealed partial class Physics
{
    private readonly ContactSet _contacts;

    /// <summary>
    /// What started touching in the last step, one contact for each pair of entities, in entity order: a crate landing,
    /// a ball hitting a wall, a ragdoll's limb hitting the floor. Things that keep touching aren't listed again. Use
    /// <see cref="Contact.Speed"/> to tell a tap from a crash, such as for how loud a thud is. Triggers and a
    /// ragdoll's parts touching each other don't count. Read it in Update; the next step replaces it.
    /// </summary>
    public IReadOnlyList<Contact> Contacts => _contacts.List;
}

namespace Super;

/// <summary>
/// Three joints that bend like an arm or a leg: the upper joint (a shoulder or hip), the
/// lower one (an elbow or knee) and the end (a wrist or ankle), and the joint whose point
/// <see cref="AnimationPose.Reach(Limb, System.Numerics.Vector3, float, System.Numerics.Vector3)"/>
/// carries to its goal: the end itself, or something it holds, such as a sword. From
/// <see cref="Rig.Limb"/>.
/// </summary>
public sealed class Limb
{
    private Limb(Rig rig, LimbJoints joints)
    {
        Rig = rig;
        Joints = joints;
    }

    public Rig Rig { get; }

    public int Upper => Joints.Upper;

    public int Lower => Joints.Lower;

    public int End => Joints.End;

    /// <summary>The end, or a joint under it whose point is carried to the goal.</summary>
    public int Tip => Joints.Tip;

    /// <summary>The joints by number, one set per limb, so a pose tells one limb's reach from another's.</summary>
    internal LimbJoints Joints { get; }

    /// <summary>Finds the limb's joints in the rig's skeleton, each of which must be under the one before.</summary>
    internal static Limb Find(Rig rig, string upper, string lower, string end, string? tip)
    {
        var skeleton = rig.Skeleton;

        int Index(string name)
        {
            int joint = skeleton.IndexOf(name);
            return joint >= 0
                ? joint
                : throw new ArgumentException($"The rig has no joint called {name}. It has: {string.Join(", ", skeleton.Names)}.", nameof(upper));
        }

        int u = Index(upper), l = Index(lower), e = Index(end), t = tip is null ? e : Index(tip);
        CheckUnder(skeleton.Parents, l, u, lower, upper);
        CheckUnder(skeleton.Parents, e, l, end, lower);
        if (t != e)
        {
            CheckUnder(skeleton.Parents, t, e, tip!, end);
        }

        return new Limb(rig, new LimbJoints(u, l, e, t));
    }

    /// <summary>Throws unless <paramref name="joint"/> is somewhere under <paramref name="above"/>.</summary>
    private static void CheckUnder(int[] parents, int joint, int above, string name, string aboveName)
    {
        for (int i = parents[joint]; i >= 0; i = parents[i])
        {
            if (i == above)
            {
                return;
            }
        }

        throw new ArgumentException($"A limb's joints must each be under the one before, but {name} isn't under {aboveName}.");
    }
}

/// <summary>
/// A limb's joints by number: upper, lower, end and the tip whose point reaches. It's a class,
/// one per <see cref="Limb"/>, so a reach is told from another by which limb made it.
/// </summary>
internal sealed class LimbJoints(int upper, int lower, int end, int tip)
{
    public int Upper => upper;

    public int Lower => lower;

    public int End => end;

    public int Tip => tip;
}

using Rendition;

namespace Super.Physics;

/// <summary>How a ragdoll's parts hang together: the joint each starts at, its parent part, and the part that moves each joint.</summary>
internal sealed class RagdollTree
{
    public RagdollTree(Skeleton skeleton, IReadOnlyList<RagdollPart> parts)
    {
        Joints = new int[parts.Count];
        ParentParts = new int[parts.Count];
        PartOfJoint = new int[skeleton.Count];
        PartOfJoint.AsSpan().Fill(-1);
        for (int i = 0; i < parts.Count; i++)
        {
            int joint = Find(skeleton, parts[i].Joint);
            if (PartOfJoint[joint] >= 0)
            {
                throw new ArgumentException($"Two parts start at joint {skeleton.Names[joint]}.", nameof(parts));
            }

            Joints[i] = joint;
            PartOfJoint[joint] = i;
        }

        // Each joint belongs to the nearest part at or above it. Parents come first, so one pass does it.
        for (int j = 0; j < skeleton.Count; j++)
        {
            int parent = skeleton.Parents[j];
            if (PartOfJoint[j] < 0 && parent >= 0)
            {
                PartOfJoint[j] = PartOfJoint[parent];
            }
        }

        Link(skeleton, parts);
    }

    /// <summary>Each part's joint.</summary>
    public int[] Joints { get; }

    /// <summary>Each part's parent part, or -1 for the first.</summary>
    public int[] ParentParts { get; }

    /// <summary>For each joint of the skeleton, the part that moves it, or -1 for a joint above the first part.</summary>
    public int[] PartOfJoint { get; }

    /// <summary>A joint by its name, or by the name after a prefix such as <c>mixamorig:</c>. An unknown name throws, listing the joints.</summary>
    public static int Find(Skeleton skeleton, string name)
    {
        int exact = skeleton.IndexOf(name);
        if (exact >= 0)
        {
            return exact;
        }

        for (int i = 0; i < skeleton.Count; i++)
        {
            string candidate = skeleton.Names[i];
            int colon = candidate.LastIndexOf(':');
            if (colon >= 0 && candidate.AsSpan(colon + 1).SequenceEqual(name))
            {
                return i;
            }
        }

        throw new ArgumentException($"The rig has no joint called {name}. It has: {string.Join(", ", skeleton.Names)}.", nameof(name));
    }

    /// <summary>Gives each part its parent, checking that the first part is the root of all the others.</summary>
    private void Link(Skeleton skeleton, IReadOnlyList<RagdollPart> parts)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            int parent = skeleton.Parents[Joints[i]];
            ParentParts[i] = parent >= 0 ? PartOfJoint[parent] : -1;
            if (i == 0 && ParentParts[i] >= 0)
            {
                throw new ArgumentException($"The first part, {parts[0].Joint}, must be the root of the others, but it's under {parts[ParentParts[i]].Joint}.", nameof(parts));
            }

            if (i > 0 && ParentParts[i] < 0)
            {
                throw new ArgumentException($"Part {parts[i].Joint} isn't under the first part, {parts[0].Joint}, so it can't be joined to the ragdoll.", nameof(parts));
            }
        }
    }
}

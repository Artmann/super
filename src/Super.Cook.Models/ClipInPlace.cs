using System.Numerics;
using Rendition;

namespace Super.Cook.Models;

/// <summary>Cooks a clip that walks so it plays on the spot, for an animation's <c>inPlace</c> setting.</summary>
internal static class ClipInPlace
{
    /// <summary>
    /// Holds the clip's root over the ground where its first frame puts it. The root is the first joint, parents
    /// first, whose position the clip moves, such as Mixamo's hips. Only the across part of its travel goes, measured
    /// in the scene with its parent at rest, so it still rises and falls. Changes <paramref name="frames"/> and
    /// returns them.
    /// </summary>
    public static JointPose[] Held(JointPose[] frames, int[] parents, Matrix4x4[] restGlobal)
    {
        int jointCount = parents.Length;
        int frameCount = frames.Length / jointCount;
        int root = Root(frames, jointCount);
        if (root < 0)
        {
            return frames;
        }

        var parent = parents[root] >= 0 ? restGlobal[parents[root]] : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(parent, out var inverseParent))
        {
            return frames;
        }

        var start = Vector3.Transform(frames[root].Position, parent);
        for (int frame = 1; frame < frameCount; frame++)
        {
            ref var pose = ref frames[frame * jointCount + root];
            var travel = Vector3.Transform(pose.Position, parent) - start;
            pose.Position -= Vector3.TransformNormal(new Vector3(travel.X, 0f, travel.Z), inverseParent);
        }

        return frames;
    }

    /// <summary>The first joint whose position changes over the clip, or -1 when none does.</summary>
    private static int Root(JointPose[] frames, int jointCount)
    {
        for (int joint = 0; joint < jointCount; joint++)
        {
            for (int at = joint + jointCount; at < frames.Length; at += jointCount)
            {
                if (frames[at].Position != frames[joint].Position)
                {
                    return joint;
                }
            }
        }

        return -1;
    }
}

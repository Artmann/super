using System.Numerics;
using Rendition;
using Silk.NET.Assimp;

namespace Super.Cook.Models;

/// <summary>Samples an Assimp animation's channels into frames of joint poses at <see cref="FrameRate"/>.</summary>
internal static unsafe class ClipSampler
{
    /// <summary>Frames per second of every clip read.</summary>
    public const float FrameRate = 30f;

    /// <summary>The channel that drives each joint, by node name, or null when the animation drives none.</summary>
    public static NodeAnim*[]? Channels(Animation* animation, JointNames joints)
    {
        var channels = new NodeAnim*[joints.Names.Length];
        int matched = 0;
        for (uint c = 0; c < animation->MNumChannels; c++)
        {
            var channel = animation->MChannels[c];
            if (joints.Of(channel->MNodeName.AsString) is { } joint)
            {
                channels[joint] = channel;
                matched++;
            }
        }

        return matched == 0 ? null : channels;
    }

    /// <summary>Every frame's pose of every joint, joint by joint within a frame. Joints without a channel hold their rest pose.</summary>
    public static JointPose[] Frames(Animation* animation, NodeAnim*[] channels, JointPose[] rest)
    {
        double ticksPerSecond = animation->MTicksPerSecond > 0 ? animation->MTicksPerSecond : 25.0;
        float seconds = (float)(animation->MDuration / ticksPerSecond);
        int frameCount = Math.Max(1, (int)MathF.Round(seconds * FrameRate) + 1);
        int jointCount = rest.Length;
        var frames = new JointPose[frameCount * jointCount];
        for (int frame = 0; frame < frameCount; frame++)
        {
            double ticks = Math.Min(frame / (double)FrameRate * ticksPerSecond, animation->MDuration);
            for (int joint = 0; joint < jointCount; joint++)
            {
                var channel = channels[joint];
                frames[frame * jointCount + joint] = channel == null ? rest[joint] : Sample(channel, ticks, rest[joint]);
            }
        }

        return frames;
    }

    private static JointPose Sample(NodeAnim* channel, double ticks, JointPose rest) =>
        new(
            SampleVector(channel->MPositionKeys, channel->MNumPositionKeys, ticks, rest.Position),
            SampleRotation(channel->MRotationKeys, channel->MNumRotationKeys, ticks, rest.Rotation),
            SampleVector(channel->MScalingKeys, channel->MNumScalingKeys, ticks, rest.Scale));

    private static Vector3 SampleVector(VectorKey* keys, uint count, double ticks, Vector3 rest)
    {
        if (count == 0)
        {
            return rest;
        }

        int next = FindNext(keys, count, ticks);
        if (next == 0)
        {
            return keys[0].MValue;
        }

        if (next >= count)
        {
            return keys[count - 1].MValue;
        }

        var before = keys[next - 1];
        var after = keys[next];
        return Vector3.Lerp(before.MValue, after.MValue, Share(before.MTime, after.MTime, ticks));
    }

    private static Quaternion SampleRotation(QuatKey* keys, uint count, double ticks, Quaternion rest)
    {
        if (count == 0)
        {
            return rest;
        }

        int next = FindNext(keys, count, ticks);
        if (next == 0)
        {
            return ToQuaternion(keys[0].MValue);
        }

        if (next >= count)
        {
            return ToQuaternion(keys[count - 1].MValue);
        }

        var before = keys[next - 1];
        var after = keys[next];
        return Quaternion.Slerp(ToQuaternion(before.MValue), ToQuaternion(after.MValue), Share(before.MTime, after.MTime, ticks));
    }

    private static int FindNext(VectorKey* keys, uint count, double ticks)
    {
        int next = 0;
        while (next < count && keys[next].MTime <= ticks)
        {
            next++;
        }

        return next;
    }

    private static int FindNext(QuatKey* keys, uint count, double ticks)
    {
        int next = 0;
        while (next < count && keys[next].MTime <= ticks)
        {
            next++;
        }

        return next;
    }

    private static float Share(double from, double to, double at) =>
        to > from ? (float)Math.Clamp((at - from) / (to - from), 0.0, 1.0) : 0f;

    private static Quaternion ToQuaternion(AssimpQuaternion q) => Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W));
}

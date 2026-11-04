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
    internal Limb(Rig rig, int upper, int lower, int end, int tip)
    {
        Rig = rig;
        Upper = upper;
        Lower = lower;
        End = end;
        Tip = tip;
    }

    public Rig Rig { get; }

    public int Upper { get; }

    public int Lower { get; }

    public int End { get; }

    /// <summary>The end, or a joint under it whose point is carried to the goal.</summary>
    public int Tip { get; }
}

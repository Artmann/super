namespace Super.Physics;

/// <summary>What a <see cref="Ragdoll"/> is doing.</summary>
public enum RagdollMode
{
    /// <summary>No bodies: the animator moves the character, and any ragdoll pose left blends back into the clips.</summary>
    Off,

    /// <summary>
    /// Bodies that follow the animation, so the character pushes things, rays hit its limbs, and switching to
    /// <see cref="Simulated"/> carries on at the speed it was moving.
    /// </summary>
    Kinematic,

    /// <summary>Bodies the simulation moves, which fall, tumble and are pushed, and the character takes their pose.</summary>
    Simulated,
}

/// <summary>
/// Makes an entity with a <see cref="Transform"/> and an <see cref="Animated"/> fall like a
/// ragdoll. Physics builds one body per <see cref="RagdollPart"/>, joined at the joints, while
/// <see cref="Mode"/> isn't <see cref="RagdollMode.Off"/>. Switching to <see cref="RagdollMode.Simulated"/> starts
/// the bodies where the animation has the limbs, moving as fast as they were, and the entity then follows the pelvis.
/// Switching back to <see cref="RagdollMode.Off"/> stands the entity under the pelvis, facing the way a body lying
/// there would get up, and blends the ragdoll's pose into the clips over <see cref="BlendSeconds"/>, so play a get-up
/// animation at the same time.
/// </summary>
/// <param name="Body">The parts and joints, from <see cref="RagdollBody.Humanoid"/> or made by hand.</param>
public readonly record struct Ragdoll(RagdollBody Body)
{
    public RagdollMode Mode { get; init; } = RagdollMode.Off;

    /// <summary>
    /// How hard the muscles pull each simulated part towards the pose the animation gives, from 0, limp, to 1, which
    /// holds the pose against gravity but still gives under a blow. Between, a character stumbles and braces.
    /// </summary>
    public float Strength { get; init; }

    /// <summary>
    /// The joints that are simulated, such as <c>rig.Mask("Spine")</c> for a body that flops above the waist while
    /// the legs keep walking. Parts outside it follow the animation. Null simulates every part.
    /// </summary>
    public BoneMask? Mask { get; init; }

    /// <summary>How long the ragdoll's pose takes to blend back into the clips once <see cref="Mode"/> is <see cref="RagdollMode.Off"/>.</summary>
    public float BlendSeconds { get; init; } = 0.4f;

    /// <summary>Whether physics moves a part of <see cref="Body"/>, as the mode and mask ask, rather than the animation.</summary>
    internal bool Simulates(int part) =>
        Mode == RagdollMode.Simulated && (Mask is null || Mask.Weights[Body.PartTree.Joints[part]] >= 0.5f);
}

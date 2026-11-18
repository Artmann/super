using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// A model to draw. It only exists until the next frame describes the world again. A
/// pose skins a rigged model; PoseAhead is how many seconds past the last tick the
/// display is, so clips keep moving between ticks. A ghost draws it in one colour. Meshes,
/// when set, shows only the masked meshes of the model; null shows them all. Static marks a
/// draw that never moves, so baked lighting can hold it; its parts get bake ids.
/// </summary>
public readonly record struct ModelDraw(
    int Key,
    ModelRef Model,
    Matrix4x4 Transform,
    Vector3? Color,
    SwimMotion Swim = default,
    AnimationPose? Pose = null,
    float PoseAhead = 0f,
    Ghost? Ghost = null,
    MeshMask? Meshes = null,
    bool Static = false,
    DistanceFade? Fade = null);

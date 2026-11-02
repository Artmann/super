// Skin matrices for every skinned draw this frame, one after another. A draw says where
// its own start with firstBone in its push constants, or -1 when it has no skin.
// Filled from FrameView.Bones in Renderer.cs.
layout(std430, set = 0, binding = 1) readonly buffer Bones {
    mat4 bones[];
} skin;

// The matrix that moves a vertex from the mesh into the posed model, from its four
// joints and their weights. The identity when the draw has no skin.
mat4 skinMatrix(int firstBone, uvec4 joints, vec4 weights) {
    if (firstBone < 0) {
        return mat4(1.0);
    }
    return weights.x * skin.bones[firstBone + int(joints.x)]
         + weights.y * skin.bones[firstBone + int(joints.y)]
         + weights.z * skin.bones[firstBone + int(joints.z)]
         + weights.w * skin.bones[firstBone + int(joints.w)];
}

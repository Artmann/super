// The model matrix of every copy drawn this frame, one after another. An instanced draw
// of copies that look the same starts at its own place, so gl_InstanceIndex, which counts
// from the draw's first instance, picks this copy's matrix.
// Filled by InstanceBuffer in Vulkan/InstanceBuffer.cs, from SceneDraws.Record.
layout(std430, set = 0, binding = 2) readonly buffer Instances {
    mat4 models[];
} instances;

mat4 instanceModel() {
    return instances.models[gl_InstanceIndex];
}

// Where each copy stood last frame, beside its matrix above, for motion vectors. With no last frame,
// history is 0 and the rest holds this frame's placing. Must match PreviousInstance in Passes/Motion.cs.
struct PreviousInstance {
    mat4 model;
    vec3 swim;      // amplitude, body length and phase
    int firstBone;  // where last frame's skin matrices start, or -1 for no skin
    int history;    // 1 when the copy has a last frame
    int pad0;
    int pad1;
    int pad2;
};

layout(std430, set = 0, binding = 9) readonly buffer PreviousInstances {
    PreviousInstance copies[];
} previousInstances;

PreviousInstance previousInstance() {
    return previousInstances.copies[gl_InstanceIndex];
}

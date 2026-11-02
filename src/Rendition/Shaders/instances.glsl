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

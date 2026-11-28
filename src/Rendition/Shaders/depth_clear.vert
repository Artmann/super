#version 460

// A fullscreen triangle at the far plane, which is depth 0 with reversed Z. Drawn depth-only with compare Always, it
// clears the part of a depth target its viewport covers: WebGPU has no clearAttachments, so a shadow atlas tile is
// cleared this way.
void main() {
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
}

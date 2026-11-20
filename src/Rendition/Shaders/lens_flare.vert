#version 460

// One element of a light's flare, dimmed by how much of the light lens_flare_visibility.comp saw. Where none of it was
// seen, the quad folds to a point and draws nothing.

layout(location = 0) in vec2 inCenter;
layout(location = 1) in vec2 inHalfSize;
layout(location = 2) in vec4 inColor;
layout(location = 3) in vec4 inShape;

layout(set = 0, binding = 0) readonly buffer Visibility
{
    float values[];
} visibility;

layout(location = 0) out vec2 local;
layout(location = 1) flat out vec3 color;
layout(location = 2) flat out vec4 shape;

const int SOURCE_FLOATS = 8;
const int DRAWN = 4;

const vec2 Corners[6] = vec2[](
    vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(-1.0, 1.0),
    vec2(-1.0, 1.0), vec2(1.0, -1.0), vec2(1.0, 1.0));

void main() {
    float seen = visibility.values[int(inColor.a + 0.5) * SOURCE_FLOATS + DRAWN];
    local = Corners[gl_VertexIndex];
    color = inColor.rgb * seen;
    shape = inShape;
    if (!(seen > 0.0)) {
        gl_Position = vec4(-2.0, -2.0, 0.0, 1.0);
        return;
    }

    vec2 uv = inCenter + inHalfSize * local;
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
}

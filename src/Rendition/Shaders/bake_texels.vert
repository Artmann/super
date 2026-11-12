#version 460

// The baker's texel setup: draws one static draw into its lightmap rectangle, with its second UV set as the position, so
// each texel it covers gets the world position and normals of the surface there. See "Texel setup" in docs/design.md
// and Baking/Baker.cs.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 6) in vec2 inUv2; // The lightmap's 0 to 1 for this draw's material; the masks read the same set.

// Must match TexelConstants in Baking/Baker.cs.
layout(push_constant) uniform Draw {
    mat4 model;
    vec4 rect; // xy scale and zw offset from Uv2 to 0 to 1 on the page: the draw's LightmapRect.
    float chart; // The rectangle's number, from 1, which the filter keeps texels of different rectangles apart by.
} draw;

layout(location = 0) out vec3 outPosition;
layout(location = 1) out vec3 outNormal;
layout(location = 2) out vec3 outPlanePosition;
layout(location = 3) flat out float outChart;

void main() {
    vec3 world = (draw.model * vec4(inPosition, 1.0)).xyz;
    mat3 model = mat3(draw.model);
    // The cofactor matrix is the inverse transpose times the determinant; the sign keeps mirrored draws right. As in
    // gbuffer.vert.
    mat3 cofactor = mat3(cross(model[1], model[2]), cross(model[2], model[0]), cross(model[0], model[1]));
    outNormal = cofactor * inNormal * sign(dot(model[0], cofactor[0]));
    outPosition = world;
    outPlanePosition = world;
    outChart = draw.chart;

    // 0 to 1 on the page is -1 to 1 in clip space. The viewport covers the page, or a tile of it, so texel (x, y)
    // holds page uv ((x + 0.5) / size, (y + 0.5) / size), the same way a texture lookup reads it back.
    vec2 page = inUv2 * draw.rect.xy + draw.rect.zw;
    gl_Position = vec4(page * 2.0 - 1.0, 0.0, 1.0);
}

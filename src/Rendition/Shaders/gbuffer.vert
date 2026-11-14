#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "skinning.glsl"
#include "instances.glsl"
#include "wind.glsl"
#include "chunks.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 4) in uvec4 inJoints;
layout(location = 5) in vec4 inWeights;
layout(location = 6) in vec2 inUv2; // For baked masks; see applyLayers in surface.glsl.

// Must match DrawConstants in Passes/SceneDraws.cs.
layout(push_constant) uniform Draw {
    vec4 tint;
    vec4 ghost; // rgb is the colour of a ghost and a its opacity, or 0 for no ghost.
    int material;
    int cascade;
    int firstBone;
    float windBase; // the lowest height of the draw's part, in its own space; see wind.glsl
    float windSway; // the material's share of the wind's lean, 0 to stand still
} draw;

// The depth-first draws and the shading draws that follow must land on the very same depth, whatever else each
// pipeline's compiler does with this shader.
invariant gl_Position;

layout(location = 0) out vec3 outNormal;
layout(location = 1) out vec4 outTangent;
layout(location = 2) out vec2 outUv;
layout(location = 3) out vec3 outPosition;
layout(location = 4) out vec2 outUv2;
// Where the vertex is on screen this frame, without the anti-aliasing shift, and where it was last frame, as clip x, y
// and w, for surface.glsl's motion vectors. See Passes/Motion.cs.
layout(location = 5) out vec3 outClipNow;
layout(location = 6) out vec3 outClipBefore;
// Where the vertex is on its lightmap page, and the page's irradiance, direction and shadow mask textures, -1 for a
// copy with no lightmap, and in w the placed chunk the copy belongs to, in chunks.glsl, or -1 for none. The chunk rides
// in the same ivec4 because a lone int output that a shader such as transparent.frag doesn't read draws a validation
// warning. The second uv itself goes on unchanged, for the masks. See writeSurface in surface.glsl.
layout(location = 7) out vec2 outLightmapUv;
layout(location = 8) flat out ivec4 outLightmap;
// Where the vertex is in its chunk's own space, for its relighting in writeBaked.
layout(location = 9) out vec3 outChunkPosition;

// Where the vertex stood in the world last frame: placed by last frame's matrix, skin, swim and wind. A copy with no
// last frame stands where it is now, so it moves with the camera alone.
vec4 placedBefore(vec4 now) {
    PreviousInstance previous = previousInstance();
    if (previous.history == 0) {
        return now;
    }

    vec3 posed = (skinMatrix(previous.firstBone, inJoints, inWeights) * vec4(inPosition, 1.0)).xyz;
    vec3 swum = swim(posed, previous.swim.x, previous.swim.y, previous.swim.z);
    vec3 world = (previous.model * vec4(swum, 1.0)).xyz;
    return vec4(windSwayPlacedAt(world, previous.model, posed, draw.windBase, draw.windSway, frame.previousTime), 1.0);
}

void main() {
    mat4 skinned = skinMatrix(draw.firstBone, inJoints, inWeights);
    mat4 placed = instanceModel();
    mat3 model = mat3(placed) * mat3(skinned);
    // The cofactor matrix is the inverse transpose times the determinant. The size doesn't
    // matter, because the fragment shader normalizes, but the sign keeps mirrored models
    // right. This avoids inverting a matrix for every vertex.
    mat3 cofactor = mat3(cross(model[1], model[2]), cross(model[2], model[0]), cross(model[0], model[1]));
    outNormal = cofactor * inNormal * sign(dot(model[0], cofactor[0]));
    outTangent = vec4(model * inTangent.xyz, inTangent.w);
    outUv = inUv;
    outUv2 = inUv2;
    LightmapRect lightmap = lightmapRect();
    outLightmapUv = inUv2 * lightmap.scaleOffset.xy + lightmap.scaleOffset.zw;
    int chunk = lightmap.chunk.x;
    outLightmap = ivec4(lightmap.textures.xyz, chunk);
    vec3 posed = (skinned * vec4(inPosition, 1.0)).xyz;
    vec3 swimming = instanceSwim();
    vec3 swum = swim(posed, swimming.x, swimming.y, swimming.z);
    vec4 position = vec4(windSwayPlaced((placed * vec4(swum, 1.0)).xyz, placed, posed, draw.windBase, draw.windSway), 1.0);
    outPosition = position.xyz;
    outChunkPosition = chunk >= 0 ? position * chunkData.chunks[chunk].intoChunk : vec3(0.0);
    gl_Position = frame.viewProjection * position;

    // The shift moves clip x and y by itself times w; see Renderer.WriteUniforms.
    outClipNow = vec3(gl_Position.xy - frame.finish.zw * gl_Position.w, gl_Position.w);
    outClipBefore = (frame.previousViewProjection * placedBefore(position)).xyw;
}

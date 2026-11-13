// Tracing rays through the scene's acceleration structures with ray queries, and what a hit landed on. Include it
// after common.glsl, with TRACE_GEOMETRY_SET and TRACE_GEOMETRY_BINDING saying where the geometry table is bound and
// TRACE_RESOURCE_SET where the resource table is (textures at binding 0, materials at binding 1). See "Ray tracing" in
// docs/design.md.

#extension GL_EXT_ray_query : require
#extension GL_EXT_buffer_reference : require
#extension GL_EXT_buffer_reference_uvec2 : require
#extension GL_EXT_nonuniform_qualifier : require

#include "material_data.glsl"

#if !defined(TRACE_GEOMETRY_SET) || !defined(TRACE_GEOMETRY_BINDING) || !defined(TRACE_RESOURCE_SET)
#error "Define TRACE_GEOMETRY_SET, TRACE_GEOMETRY_BINDING and TRACE_RESOURCE_SET before including trace.glsl."
#endif

// One part of a mesh, as the acceleration structures hold it. Must match GpuTraceGeometry in Vulkan/RayTracing.cs.
// The addresses are read as two halves, so no 64-bit integers are needed.
struct TraceGeometry {
    uvec2 vertices;
    uvec2 indices;
    uint firstIndex; // Where the part's triangles start among the mesh's indices.
    uint material;
    uint tintRG;     // The draw's tint as half floats: red and green,
    uint tintB;      // and blue.
};

vec3 traceTint(TraceGeometry geometry) {
    return vec3(unpackHalf2x16(geometry.tintRG), unpackHalf2x16(geometry.tintB).x);
}

layout(std430, set = TRACE_GEOMETRY_SET, binding = TRACE_GEOMETRY_BINDING) readonly buffer TraceGeometries {
    TraceGeometry traceGeometries[]; // A hit's entry is its instance's custom index plus its geometry index.
};

layout(set = TRACE_RESOURCE_SET, binding = 0) uniform sampler2D traceTextures[];
layout(std430, set = TRACE_RESOURCE_SET, binding = 1) readonly buffer TraceMaterials {
    Material traceMaterials[];
};

// A mesh's vertices as floats, and its indices.
layout(buffer_reference, std430, buffer_reference_align = 4) readonly buffer TraceFloats {
    float values[];
};
layout(buffer_reference, std430, buffer_reference_align = 4) readonly buffer TraceIndices {
    uint values[];
};

// Must match Vertex in Types.cs: 16 floats a vertex, the normal at float 3 and the uv at float 10.
const uint TRACE_VERTEX_FLOATS = 16u;
const uint TRACE_NORMAL_AT = 3u;
const uint TRACE_UV_AT = 10u;

// Where a ray landed, in world space, and on what. The normal faces back along the ray; frontFace says whether the
// ray hit the side the triangle's winding faces.
struct TraceHit {
    vec3 position;
    vec3 normal;
    vec2 uv;
    uint material;
    vec3 tint; // The draw's tint, white when it has none.
    bool frontFace;
};

uvec3 traceTriangle(TraceGeometry geometry, uint primitive) {
    TraceIndices indices = TraceIndices(geometry.indices);
    uint first = geometry.firstIndex + 3u * primitive;
    return uvec3(indices.values[first], indices.values[first + 1u], indices.values[first + 2u]);
}

vec2 traceUv(TraceFloats vertices, uint vertex) {
    uint at = vertex * TRACE_VERTEX_FLOATS + TRACE_UV_AT;
    return vec2(vertices.values[at], vertices.values[at + 1u]);
}

vec3 traceNormal(TraceFloats vertices, uint vertex) {
    uint at = vertex * TRACE_VERTEX_FLOATS + TRACE_NORMAL_AT;
    return vec3(vertices.values[at], vertices.values[at + 1u], vertices.values[at + 2u]);
}

vec3 traceWeights(vec2 barycentrics) {
    return vec3(1.0 - barycentrics.x - barycentrics.y, barycentrics.x, barycentrics.y);
}

vec2 traceInterpolatedUv(TraceGeometry geometry, uint primitive, vec2 barycentrics) {
    uvec3 triangle = traceTriangle(geometry, primitive);
    TraceFloats vertices = TraceFloats(geometry.vertices);
    vec3 w = traceWeights(barycentrics);
    return traceUv(vertices, triangle.x) * w.x + traceUv(vertices, triangle.y) * w.y + traceUv(vertices, triangle.z) * w.z;
}

// Whether the material is solid at this uv: the same test the shadow pass makes, base colour alpha by the surface
// texture's alpha against the cut-out threshold.
bool traceSolid(uint material, vec2 uv) {
    Material m = traceMaterials[nonuniformEXT(material)];
    float opacity = textureLod(traceTextures[nonuniformEXT(m.baseColorTexture)], uv, 0.0).a * m.baseColor.a
        * textureLod(traceTextures[nonuniformEXT(m.surfaceTexture)], uv, 0.0).a;
    return opacity >= m.alphaCutoff;
}

// Whether the candidate a ray query has just met stops the ray: solid at its uv, and, when skipSeeThrough, not a
// see-through material such as glass or water. Opaque parts never get here; glass and cut-out parts always do, as
// RayTracing.GeometryFlagsFor builds them non-opaque.
bool traceCandidateStops(rayQueryEXT query, bool skipSeeThrough) {
    uint entry = uint(rayQueryGetIntersectionInstanceCustomIndexEXT(query, false))
        + uint(rayQueryGetIntersectionGeometryIndexEXT(query, false));
    TraceGeometry geometry = traceGeometries[entry];
    if (skipSeeThrough && (traceMaterials[nonuniformEXT(geometry.material)].flags & MATERIAL_TRANSPARENT) != 0u) {
        return false;
    }

    vec2 uv = traceInterpolatedUv(geometry, uint(rayQueryGetIntersectionPrimitiveIndexEXT(query, false)),
        rayQueryGetIntersectionBarycentricsEXT(query, false));
    return traceSolid(geometry.material, uv);
}

// Whether nothing blocks a ray within maxDistance: a shadow ray. It stops at the first blocker it finds rather than the
// closest, and passes through cut-outs as traceClosest does. With skipSeeThrough, glass and water block nothing.
bool traceVisible(accelerationStructureEXT scene, vec3 origin, vec3 direction, float maxDistance, bool skipSeeThrough) {
    rayQueryEXT query;
    rayQueryInitializeEXT(query, scene, gl_RayFlagsTerminateOnFirstHitEXT, 0xFFu, origin, 0.0, direction, maxDistance);
    while (rayQueryProceedEXT(query)) {
        if (rayQueryGetIntersectionTypeEXT(query, false) == gl_RayQueryCandidateIntersectionTriangleEXT
            && traceCandidateStops(query, skipSeeThrough)) {
            rayQueryConfirmIntersectionEXT(query);
        }
    }

    return rayQueryGetIntersectionTypeEXT(query, true) == gl_RayQueryCommittedIntersectionNoneEXT;
}

bool traceVisible(accelerationStructureEXT scene, vec3 origin, vec3 direction, float maxDistance) {
    return traceVisible(scene, origin, direction, maxDistance, false);
}

// Traces a ray to the closest surface it hits within maxDistance. Opaque parts are hit outright; the rest are checked
// against their material's cut-out first, so a ray passes through the holes in leaves and fences. With skipSeeThrough
// it passes through glass and water too.
bool traceClosest(
    accelerationStructureEXT scene, vec3 origin, vec3 direction, float maxDistance, bool skipSeeThrough, out TraceHit hit) {
    rayQueryEXT query;
    rayQueryInitializeEXT(query, scene, gl_RayFlagsNoneEXT, 0xFFu, origin, 0.0, direction, maxDistance);
    while (rayQueryProceedEXT(query)) {
        if (rayQueryGetIntersectionTypeEXT(query, false) == gl_RayQueryCandidateIntersectionTriangleEXT
            && traceCandidateStops(query, skipSeeThrough)) {
            rayQueryConfirmIntersectionEXT(query);
        }
    }

    if (rayQueryGetIntersectionTypeEXT(query, true) == gl_RayQueryCommittedIntersectionNoneEXT) {
        hit = TraceHit(vec3(0.0), vec3(0.0), vec2(0.0), 0u, vec3(1.0), false);
        return false;
    }

    uint entry = uint(rayQueryGetIntersectionInstanceCustomIndexEXT(query, true))
        + uint(rayQueryGetIntersectionGeometryIndexEXT(query, true));
    TraceGeometry geometry = traceGeometries[entry];
    uvec3 triangle = traceTriangle(geometry, uint(rayQueryGetIntersectionPrimitiveIndexEXT(query, true)));
    TraceFloats vertices = TraceFloats(geometry.vertices);
    vec3 w = traceWeights(rayQueryGetIntersectionBarycentricsEXT(query, true));

    vec3 normal = traceNormal(vertices, triangle.x) * w.x + traceNormal(vertices, triangle.y) * w.y
        + traceNormal(vertices, triangle.z) * w.z;
    // Normals go to world space by the inverse transpose, which is the world-to-object matrix applied from the left.
    normal = normalize(normal * mat3(rayQueryGetIntersectionWorldToObjectEXT(query, true)));
    if (dot(normal, direction) > 0.0) {
        normal = -normal;
    }

    hit.position = origin + direction * rayQueryGetIntersectionTEXT(query, true);
    hit.normal = normal;
    hit.uv = traceUv(vertices, triangle.x) * w.x + traceUv(vertices, triangle.y) * w.y + traceUv(vertices, triangle.z) * w.z;
    hit.material = geometry.material;
    hit.tint = traceTint(geometry);
    hit.frontFace = rayQueryGetIntersectionFrontFaceEXT(query, true);
    return true;
}

bool traceClosest(accelerationStructureEXT scene, vec3 origin, vec3 direction, float maxDistance, out TraceHit hit) {
    return traceClosest(scene, origin, direction, maxDistance, false, hit);
}

// The surface's own colour where the ray landed: the material's base colour by its base colour texture, by the draw's
// tint, as probe_capture.frag colours it. No surface shader, variation, wear, dirt or puddles.
vec3 hitAlbedo(TraceHit hit) {
    Material m = traceMaterials[nonuniformEXT(hit.material)];
    return m.baseColor.rgb * textureLod(traceTextures[nonuniformEXT(m.baseColorTexture)], hit.uv, 0.0).rgb * hit.tint;
}

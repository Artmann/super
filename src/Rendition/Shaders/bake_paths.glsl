// The baker's paths, shared by bake_trace.comp (lightmap texels) and bake_probes.comp (light probes). Include it after
// trace.glsl and bake_lights.glsl, with these declared first: the top-level structure `scene`, the `lights` buffer of
// BakeLight, the `sky` cube, and a push constant block `trace` holding lightCount, lift and skyIntensity. Define
// BAKE_TRANSFER, with a `chunk` in `trace`, to sum a chunk's transfer too. See "The path tracer" in docs/design.md.

const float PI = 3.14159265;
const uint MAX_BOUNCES = 4u;
const uint ROULETTE_AFTER = 2u; // Bounces made before Russian roulette may end a path.
const uint ALL_LIGHTS_UP_TO = 16u;
const uint CANDIDATES = 8u;
const uint MAX_LAYERS = 8u; // The see-through surfaces a ray is weakened by, nearest first.

uint randomState;

uint pcg(uint v) {
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float random01() {
    randomState = pcg(randomState);
    return float(randomState >> 8) / 16777216.0;
}

float luminance(vec3 c) {
    return dot(c, vec3(0.2126, 0.7152, 0.0722));
}

bool isTransparent(uint material) {
    return (traceMaterials[nonuniformEXT(material)].flags & MATERIAL_TRANSPARENT) != 0u;
}

// How much of the light a see-through surface stops: its alpha, as traceSolid reads it.
float opacity(uint material, vec2 uv) {
    Material m = traceMaterials[nonuniformEXT(material)];
    return clamp(textureLod(traceTextures[nonuniformEXT(m.baseColorTexture)], uv, 0.0).a * m.baseColor.a
        * textureLod(traceTextures[nonuniformEXT(m.surfaceTexture)], uv, 0.0).a, 0.0, 1.0);
}

// The material and uv of a query's candidate (committed false) or committed hit. Macros, since which one must be a
// constant.
#define HIT_ENTRY(query, committed) (uint(rayQueryGetIntersectionInstanceCustomIndexEXT(query, committed)) \
    + uint(rayQueryGetIntersectionGeometryIndexEXT(query, committed)))
#define candidateMaterial(query, committed) (traceGeometries[HIT_ENTRY(query, committed)].material)
#define candidateUv(query, committed) traceInterpolatedUv(traceGeometries[HIT_ENTRY(query, committed)], \
    uint(rayQueryGetIntersectionPrimitiveIndexEXT(query, committed)), rayQueryGetIntersectionBarycentricsEXT(query, committed))

// What share of the light gets through the see-through surfaces between origin and maxDistance, which must hold
// nothing solid. Each layer is found as the nearest beyond the last, so a surface reported twice counts once.
float throughLayers(vec3 origin, vec3 direction, float maxDistance) {
    float passed = 1.0;
    float from = 0.0;
    for (uint layer = 0u; layer < MAX_LAYERS && passed > 0.0; layer++) {
        rayQueryEXT query;
        // See-through parts are never opaque to the acceleration structures, so culling opaque ones loses none.
        rayQueryInitializeEXT(query, scene, gl_RayFlagsCullOpaqueEXT, 0xFFu, origin, from, direction, maxDistance);
        while (rayQueryProceedEXT(query)) {
            if (rayQueryGetIntersectionTypeEXT(query, false) == gl_RayQueryCandidateIntersectionTriangleEXT
                && isTransparent(candidateMaterial(query, false))) {
                rayQueryConfirmIntersectionEXT(query);
            }
        }

        if (rayQueryGetIntersectionTypeEXT(query, true) == gl_RayQueryCommittedIntersectionNoneEXT) {
            break;
        }

        uint material = candidateMaterial(query, true);

        passed *= 1.0 - opacity(material, candidateUv(query, true));
        from = rayQueryGetIntersectionTEXT(query, true) * 1.0001 + 1e-4;
    }

    return passed;
}

// What share of the light reaches along a shadow ray: 0 behind anything solid, and weakened by see-through surfaces.
float transmission(vec3 origin, vec3 direction, float maxDistance) {
    rayQueryEXT query;
    rayQueryInitializeEXT(query, scene, gl_RayFlagsTerminateOnFirstHitEXT, 0xFFu, origin, 0.0, direction, maxDistance);
    while (rayQueryProceedEXT(query)) {
        if (rayQueryGetIntersectionTypeEXT(query, false) != gl_RayQueryCandidateIntersectionTriangleEXT) {
            continue;
        }

        uint material = candidateMaterial(query, false);
        if (!isTransparent(material) && traceSolid(material, candidateUv(query, false))) {
            rayQueryConfirmIntersectionEXT(query);
        }
    }

    if (rayQueryGetIntersectionTypeEXT(query, true) != gl_RayQueryCommittedIntersectionNoneEXT) {
        return 0.0;
    }

    return throughLayers(origin, direction, maxDistance);
}

// The nearest solid surface a path reaches: as traceClosest, but passing through see-through surfaces, which weaken
// what comes back by their alpha instead of reflecting.
bool closestSolid(vec3 origin, vec3 direction, out TraceHit hit, out float passed) {
    const float far = BAKE_SUN_DISTANCE;
    rayQueryEXT query;
    rayQueryInitializeEXT(query, scene, gl_RayFlagsNoneEXT, 0xFFu, origin, 0.0, direction, far);
    while (rayQueryProceedEXT(query)) {
        if (rayQueryGetIntersectionTypeEXT(query, false) != gl_RayQueryCandidateIntersectionTriangleEXT) {
            continue;
        }

        uint material = candidateMaterial(query, false);
        if (!isTransparent(material) && traceSolid(material, candidateUv(query, false))) {
            rayQueryConfirmIntersectionEXT(query);
        }
    }

    if (rayQueryGetIntersectionTypeEXT(query, true) == gl_RayQueryCommittedIntersectionNoneEXT) {
        hit = TraceHit(vec3(0.0), vec3(0.0), vec2(0.0), 0u, vec3(1.0), false);
        passed = throughLayers(origin, direction, far);
        return false;
    }

    float t = rayQueryGetIntersectionTEXT(query, true);
    uint entry = uint(rayQueryGetIntersectionInstanceCustomIndexEXT(query, true))
        + uint(rayQueryGetIntersectionGeometryIndexEXT(query, true));
    TraceGeometry geometry = traceGeometries[entry];
    uvec3 triangle = traceTriangle(geometry, uint(rayQueryGetIntersectionPrimitiveIndexEXT(query, true)));
    TraceFloats vertices = TraceFloats(geometry.vertices);
    vec3 w = traceWeights(rayQueryGetIntersectionBarycentricsEXT(query, true));
    vec3 normal = traceNormal(vertices, triangle.x) * w.x + traceNormal(vertices, triangle.y) * w.y
        + traceNormal(vertices, triangle.z) * w.z;
    normal = normalize(normal * mat3(rayQueryGetIntersectionWorldToObjectEXT(query, true)));
    if (dot(normal, direction) > 0.0) {
        normal = -normal;
    }

    hit.position = origin + direction * t;
    hit.normal = normal;
    hit.uv = traceUv(vertices, triangle.x) * w.x + traceUv(vertices, triangle.y) * w.y + traceUv(vertices, triangle.z) * w.z;
    hit.material = geometry.material;
    hit.tint = traceTint(geometry);
    hit.frontFace = rayQueryGetIntersectionFrontFaceEXT(query, true);
    passed = throughLayers(origin, direction, t);
    return true;
}

// The light reaching p directly from every light of the bake, as irradiance, with soft shadows. At the texel itself
// (first is true) Mixed lights give nothing, since the runtime draws their direct light; their random numbers are drawn
// all the same, so the paths after are the same whichever mode a light has. towards gets each light's direction
// weighted by the luminance it gave.
vec3 directLight(vec3 p, vec3 n, bool first, inout vec4 towards) {
    vec3 total = vec3(0.0);
    vec3 origin = p + n * trace.lift;
    if (trace.lightCount <= ALL_LIGHTS_UP_TO) {
        for (uint i = 0u; i < trace.lightCount; i++) {
            BakeLight light = lights[i];
            vec2 u = vec2(random01(), random01());
            vec3 l;
            float distance;
            vec3 unshadowed = bakeSampleLight(light, p, n, u, l, distance);
            if (first && bakeIsMixed(light)) {
                continue;
            }

            if (luminance(unshadowed) <= 0.0) {
                continue;
            }

            vec3 reaching = unshadowed * transmission(origin, l, distance);
            total += reaching;
            towards += vec4(l, 1.0) * luminance(reaching);
        }

        return total;
    }

    // Resampled importance sampling: candidates drawn evenly, one kept by how much it would give unshadowed, and only
    // that one traced.
    float weightSum = 0.0;
    float keptWeight = 0.0;
    vec3 kept = vec3(0.0);
    vec3 keptL = vec3(0.0, 1.0, 0.0);
    float keptDistance = 0.0;
    for (uint c = 0u; c < CANDIDATES; c++) {
        uint index = min(uint(random01() * float(trace.lightCount)), trace.lightCount - 1u);
        BakeLight light = lights[index];
        vec2 u = vec2(random01(), random01());
        vec3 l;
        float distance;
        vec3 unshadowed = bakeSampleLight(light, p, n, u, l, distance);
        if (first && bakeIsMixed(light)) {
            unshadowed = vec3(0.0);
        }

        float weight = luminance(unshadowed);
        weightSum += weight;
        if (weight > 0.0 && random01() * weightSum < weight) {
            kept = unshadowed;
            keptWeight = weight;
            keptL = l;
            keptDistance = distance;
        }
    }

    if (keptWeight <= 0.0) {
        return vec3(0.0);
    }

    vec3 reaching = kept / keptWeight * (weightSum / float(CANDIDATES)) * float(trace.lightCount)
        * transmission(origin, keptL, keptDistance);
    towards += vec4(keptL, 1.0) * luminance(reaching);
    return reaching;
}

// A direction about n, more of them near n, as the cosine weights light arriving.
vec3 cosineDirection(vec3 n) {
    float u = random01();
    float phi = BAKE_TWO_PI * random01();
    float r = sqrt(u);
    vec3 t, b;
    bakeAcross(n, t, b);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0.0, 1.0 - u)));
}

vec3 emitted(TraceHit hit) {
    Material m = traceMaterials[nonuniformEXT(hit.material)];
    return m.emissive.rgb * textureLod(traceTextures[nonuniformEXT(m.emissiveTexture)], hit.uv, 0.0).rgb;
}

// What firstSeen holds when a path meets nothing.
const float NOTHING_SEEN = 60000.0;

#ifdef BAKE_TRANSFER
// A chunk's transfer from one path, summed by pathFrom when trace.chunk is set; bake_trace.comp defines BAKE_TRANSFER
// and declares trace.chunk. Each vec4 is the L1 spherical harmonic coefficients in ShL1's order: constant, then y, z, x.
// The sky's are per colour. The sun's are one set by luminance, and sunTint is their colour. See "Chunk transfer" in
// docs/design.md.
vec4 skyTransfer[3];
vec4 sunTransfer;
vec3 sunTint;

void resetTransfer() {
    skyTransfer[0] = vec4(0.0);
    skyTransfer[1] = vec4(0.0);
    skyTransfer[2] = vec4(0.0);
    sunTransfer = vec4(0.0);
    sunTint = vec3(0.0);
}

// The L1 basis in direction d, as ShL1.Direction in Baking/ShL1.cs.
vec4 shBasis(vec3 d) {
    return vec4(0.282094792, 0.488602512 * d.y, 0.488602512 * d.z, 0.488602512 * d.x);
}

// Next-event estimation for a sun from any direction, at a path vertex p with normal n that throughput reaches: one
// ray in a uniformly drawn direction w, which, if it escapes, adds throughput cos 4 pi Y(w). Throughput already holds
// the vertex's albedo and is in irradiance, so this is pi times the brief's throughput albedo / pi cos 4 pi Y.
void sunBounce(vec3 p, vec3 n, vec3 throughput) {
    float z = 1.0 - 2.0 * random01();
    float phi = BAKE_TWO_PI * random01();
    float r = sqrt(max(0.0, 1.0 - z * z));
    vec3 w = vec3(r * cos(phi), r * sin(phi), z);
    float c = dot(n, w);
    if (c <= 0.0) {
        return;
    }

    vec3 reaching = throughput * (c * 4.0 * PI * transmission(p + n * trace.lift, w, BAKE_SUN_DISTANCE));
    sunTransfer += luminance(reaching) * shBasis(w);
    sunTint += reaching;
}
#endif

// A path from at along direction that bounces up to four times. Returns the light it brings back as pi times the
// radiance arriving along direction, so a cosine-weighted direction carries irradiance. firstSeen is how far the first
// solid surface is, negative when the path met its back, and NOTHING_SEEN when it met nothing.
vec3 pathFrom(vec3 at, vec3 direction, out float firstSeen) {
    firstSeen = NOTHING_SEEN;
    vec3 throughput = vec3(1.0);
    vec3 pathLight = vec3(0.0);
    for (uint bounce = 0u; bounce < MAX_BOUNCES; bounce++) {
        TraceHit hit;
        float passed;
        bool found = closestSolid(at, direction, hit, passed);
        throughput *= passed;
        if (!found) {
            pathLight += throughput * PI * textureLod(sky, direction, 0.0).rgb * trace.skyIntensity;
#ifdef BAKE_TRANSFER
            // An escaped path's share of the sky from direction, in the chunk's own space: pi times the mean of
            // throughput Y, since the first direction was drawn by the cosine.
            if (trace.chunk != 0u) {
                vec4 y = PI * shBasis(direction);
                skyTransfer[0] += throughput.r * y;
                skyTransfer[1] += throughput.g * y;
                skyTransfer[2] += throughput.b * y;
            }
#endif
            break;
        }

        if (bounce == 0u) {
            float seen = length(hit.position - at);
            firstSeen = hit.frontFace ? seen : -seen;
        }

        // The back of a one-sided surface is inside something, where no light is.
        if (!hit.frontFace && (traceMaterials[nonuniformEXT(hit.material)].flags & MATERIAL_TWO_SIDED) == 0u) {
            break;
        }

        pathLight += throughput * PI * emitted(hit);
        throughput *= hitAlbedo(hit);
        if (bounce + 1u >= ROULETTE_AFTER) {
            float survive = clamp(max(throughput.r, max(throughput.g, throughput.b)), 0.05, 1.0);
            if (random01() >= survive) {
                break;
            }

            throughput /= survive;
        }

        if (all(lessThanEqual(throughput, vec3(0.0)))) {
            break;
        }

        vec4 unused = vec4(0.0);
        pathLight += throughput * directLight(hit.position, hit.normal, false, unused);
#ifdef BAKE_TRANSFER
        // The sun's bounce from every vertex the path reaches; the texel's own view of the sun is the runtime's.
        if (trace.chunk != 0u) {
            sunBounce(hit.position, hit.normal, throughput);
        }
#endif
        at = hit.position + hit.normal * trace.lift;
        direction = cosineDirection(hit.normal);
    }

    return pathLight;
}

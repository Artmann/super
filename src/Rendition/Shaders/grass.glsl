// What the grass shaders share: the field's uniforms, the blades the scatter pass leaves, and the blade's shape. Must
// match GrassUniforms and GrassBlade in GrassStage.cs. Needs common.glsl and wind.glsl. See "Grass" in docs/design.md.

// One look of grass, as GrassSettings gives it.
struct GrassKind {
    vec4 base;  // rgb: the colour at the root; a: translucency
    vec4 tip;   // rgb: the colour at the tip; a: sway
    vec4 shape; // x: height in metres; y: height variation; z: width at the root; w: bend
    vec4 more;  // x: density; y: clump size in metres
};

layout(std140, set = 2, binding = 0) uniform GrassField {
    vec4 planes[5];   // the camera's left, right, bottom, top and near planes, normals in and of unit length
    vec4 capture;     // xy: the ground map's corner with the least x and z; z: a texel's size in metres; w: texels across
    vec4 heights;     // x: the height the ground map's depth 0 stands for; y: the metres depth 1 is above it
    vec4 rings;       // how far each level of the lattice reaches, in metres
    vec4 camera;      // xyz: where the camera is; w: the share of each ring over which its blades shrink away
    ivec4 counts;     // x: squares to scatter; y: the most blades the buffer holds
    GrassKind kinds[8];
} grass;

// One blade, as the scatter pass leaves it.
struct GrassBlade {
    vec4 root;  // xyz: where it stands; w: its height
    vec4 shape; // xy: the way it faces, across the ground; z: its width; w: how far it bends over
    vec4 look;  // xy: the way out from its clump's middle, over the clump's size; z: its shade, -1 to 1; w: its look
};

#ifndef GRASS_SCATTER
layout(std430, set = 2, binding = 2) readonly buffer GrassBlades {
    GrassBlade blades[];
};
#endif

// The lattice's finest spacing, in metres. Must match GrassField.Spacing.
const float GRASS_SPACING = 0.125;
// The finest lattice's points along a square's side. Must match GrassField.TilePoints.
const int GRASS_TILE_POINTS = 32;
const float GRASS_PI = 3.14159265359;

// A blade's seven points: two at each of three heights up it, and the tip, joined into five triangles.
const int GRASS_INDICES[15] = int[15](0, 1, 2, 2, 1, 3, 2, 3, 4, 4, 3, 5, 4, 5, 6);
const float GRASS_ROWS[4] = float[4](0.0, 0.4, 0.75, 1.0);

// A point on a blade, from root to tip.
struct BladePoint {
    vec3 position;
    vec3 normal;
    float up; // how far up the blade, 0 at the root and 1 at the tip
};

// The tip of a blade, swayed by the wind at time: a curve from the root, bent over the way it faces.
vec3 bladeTip(GrassBlade blade, vec3 forward, float time) {
    float height = blade.root.w;
    float angle = blade.shape.w * 0.5 * GRASS_PI;
    vec3 tip = forward * (sin(angle) * height) + vec3(0.0, cos(angle) * height, 0.0);
    if ((frame.off & OFF_WIND) == 0) {
        float sway = grass.kinds[int(blade.look.w)].tip.a;
        tip += windSwayAt(blade.root.xyz, height, sway, time);
    }

    // The wind bends the blade round; it doesn't stretch it.
    return normalize(tip) * height;
}

// Where the blade's vertex in [0, 15) stands at time, and how it faces. The blade is a ribbon along a curve from its
// root, up, to its tip, narrowing to a point. Seen edge on it turns a little toward the camera, so it never thins to
// nothing, and its normal is rounded across it and leans out from its clump's middle, so a clump shades as one tuft.
BladePoint bladePoint(GrassBlade blade, int vertex, float time) {
    int point = GRASS_INDICES[vertex];
    int row = point >> 1;
    float t = GRASS_ROWS[row];
    float across = point == 6 ? 0.0 : ((point & 1) == 0 ? -1.0 : 1.0);

    vec3 root = blade.root.xyz;
    float height = blade.root.w;
    vec3 forward = vec3(blade.shape.x, 0.0, blade.shape.y);
    vec3 side = vec3(-forward.z, 0.0, forward.x);
    vec3 tip = bladeTip(blade, forward, time);
    // The curve's middle control point stands over the root, drawn a little toward the tip, so the blade rises
    // straight and bows over toward the tip.
    vec3 middle = vec3(0.3 * tip.x, 0.6 * height, 0.3 * tip.z);
    vec3 along = 2.0 * (1.0 - t) * middle + 2.0 * t * (tip - middle);
    vec3 at = 2.0 * (1.0 - t) * t * middle + t * t * tip;

    vec3 toCamera = frame.cameraPosition.xyz - root;
    vec3 view = normalize(toCamera);
    vec3 face = normalize(cross(side, along));
    float edgeOn = 1.0 - abs(dot(face, view));
    vec3 facing = cross(vec3(0.0, 1.0, 0.0), view);
    if (dot(facing, side) < 0.0) {
        facing = -facing;
    }

    if (dot(facing, facing) > 1e-6) {
        side = normalize(mix(side, normalize(facing), 0.5 * edgeOn * edgeOn));
    }

    float width = blade.shape.z * pow(1.0 - t, 0.7);
    BladePoint result;
    result.position = root + at + side * (0.5 * width * across);
    result.up = t;

    vec3 normal = face * (dot(face, view) < 0.0 ? -1.0 : 1.0);
    normal = normalize(normal + side * (0.4 * across));
    vec3 clump = normalize(vec3(blade.look.x * 0.7, 1.0, blade.look.y * 0.7));
    result.normal = normalize(mix(normal, clump, 0.45));
    return result;
}

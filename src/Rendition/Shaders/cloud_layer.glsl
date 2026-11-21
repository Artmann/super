// Where the clouds' layer is: two shells round the planet, at the base's height and the top's,
// and the height above the ground of a point near the camera. Needs common.glsl. Shared by the
// clouds' passes, which march between the shells, and the lighting pass, which puts the clouds
// over what lies past the base. See "Clouds" in docs/design.md.

#include "cloud_shell.glsl"

// Where a ray from the camera enters and leaves the layer, in metres, or a start past the end when it never meets
// it: from below, it climbs through the base and leaves through the top, unless it turns down to the ground; from
// above, it sinks through the top and leaves through the base, or the top again; from inside, it starts at once.
vec2 cloudLayerSpan(vec3 direction) {
    float camera = frame.cameraPosition.y;
    float base = frame.cloudShape.z;
    float top = base + frame.cloudShape.w;
    if (camera < base) {
        if (direction.y < 0.0 && cloudShellHits(camera, 0.0, direction).x > 0.0) {
            return vec2(1.0, 0.0);
        }

        return vec2(cloudShellHits(camera, base, direction).y, cloudShellHits(camera, top, direction).y);
    }

    vec2 below = cloudShellHits(camera, base, direction);
    if (camera > top) {
        vec2 hits = cloudShellHits(camera, top, direction);
        if (hits.y <= 0.0) {
            return vec2(1.0, 0.0);
        }

        return vec2(max(hits.x, 0.0), below.x > 0.0 ? below.x : hits.y);
    }

    return vec2(0.0, below.x > 0.0 ? below.x : cloudShellHits(camera, top, direction).y);
}

// The height above the ground of a point this far from the camera: the planet curves away, so a point level with
// the camera but far off stands higher above the ground beneath it. Exact to well under a metre within the clouds'
// reach.
float cloudHeight(vec3 relative) {
    return frame.cameraPosition.y + relative.y + dot(relative.xz, relative.xz) / (2.0 * CLOUD_PLANET_RADIUS);
}

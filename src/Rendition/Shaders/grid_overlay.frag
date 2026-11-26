#version 460
#extension GL_GOOGLE_include_directive : require
#include "grid_overlay.glsl"

// Lays a grid overlay onto whatever the camera sees inside its box, over the finished picture. The surface at each
// pixel comes from the scene's depth, read at this pixel's place on the screen, so it may be smaller than the window.
// That point is found on the grid, its cell and the cell's edges are worked out the way SquareGrid and HexGrid do,
// and the lines, the fill and the border are drawn from how far the point is from each edge. Colours arrive in
// sRGB, as people write them, and the screen wants them linear. See "Grid overlays" in docs/design.md.

layout(set = 0, binding = 0) uniform sampler2D sceneDepth;

layout(push_constant) uniform GridOverlayConstants {
    vec4 screen; // The window's size in pixels, then one over it.
} constants;

layout(location = 0) flat in int inOverlay;

layout(location = 0) out vec4 outColor;

const float ROOT3 = 1.7320508;

// A hexagon's six neighbours, in the order of its edges.
const ivec2 HEX_STEPS[6] = ivec2[](ivec2(1, 0), ivec2(0, 1), ivec2(-1, 1), ivec2(-1, 0), ivec2(0, -1), ivec2(1, -1));

// A square's four neighbours, in the order of its edges: -X, +X, -Z and +Z.
const ivec2 SQUARE_STEPS[4] = ivec2[](ivec2(-1, 0), ivec2(1, 0), ivec2(0, -1), ivec2(0, 1));

// Surfaces facing the grid's up take all of it, down to about 45 degrees away, and none past 60.
const float FACING_FULL = 0.7;
const float FACING_NONE = 0.5;

// The overlay fades out over the last part of its depth, so it doesn't stop at a hard line on a slope.
const float DEPTH_FADE_START = 0.8;

// How much bigger than on a surface facing the camera a pixel may be on the grid, so a surface seen almost edge on
// doesn't blur its lines away.
const float MAX_STRETCH = 16.0;

GridOverlay overlay;

bool finite(vec3 v) {
    return !any(isnan(v)) && !any(isinf(v));
}

vec3 onGrid(vec2 uv, float depth) {
    vec4 h = overlay.fromClip * vec4(uv * 2.0 - 1.0, depth, 1.0);
    vec3 local = h.xyz / h.w;
    return vec3(
        overlay.area.x + (local.x + 0.5) * overlay.area.z,
        local.y * 2.0 * overlay.cell.z,
        overlay.area.y + (local.z + 0.5) * overlay.area.w);
}

bool covered(ivec2 cell) {
    ivec2 at = cell - overlay.window.xy;
    if (any(lessThan(at, ivec2(0))) || at.x >= overlay.window.z || at.y >= overlay.window.w) {
        return false;
    }

    int index = at.x + at.y * overlay.window.z;
    return ((cellWords[overlay.info.x + (index >> 5)] >> uint(index & 31)) & 1u) != 0u;
}

vec2 hexMiddle(vec2 hex) {
    vec2 middle = overlay.info.y == SHAPE_POINTY
        ? vec2(ROOT3 * hex.x + ROOT3 / 2.0 * hex.y, 1.5 * hex.y)
        : vec2(1.5 * hex.x, ROOT3 / 2.0 * hex.x + ROOT3 * hex.y);
    return middle * overlay.cell.x;
}

// Rounds all three cube coordinates and mends the one that moved most, as Hex.Round does.
ivec2 hexRound(vec2 qr) {
    vec3 cube = vec3(qr, -qr.x - qr.y);
    vec3 rounded = roundEven(cube);
    vec3 moved = abs(rounded - cube);
    if (moved.x > moved.y && moved.x > moved.z) {
        rounded.x = -rounded.y - rounded.z;
    } else if (moved.y > moved.z) {
        rounded.y = -rounded.x - rounded.z;
    }

    return ivec2(rounded.xy);
}

ivec2 hexOn(vec2 point) {
    vec2 at = point / overlay.cell.x;
    return overlay.info.y == SHAPE_POINTY
        ? hexRound(vec2(ROOT3 / 3.0 * at.x - at.y / 3.0, 2.0 / 3.0 * at.y))
        : hexRound(vec2(2.0 / 3.0 * at.x, -at.x / 3.0 + ROOT3 / 3.0 * at.y));
}

// How much of a stroke from an edge out to width covers a point this far in from the edge.
float stroke(float distance, float width, float pixel) {
    return width > 0.0 ? 1.0 - smoothstep(width - pixel * 0.5, width + pixel * 0.5, distance) : 0.0;
}

vec4 premultiplied(vec4 color, float coverage) {
    float alpha = color.a * coverage;
    return vec4(pow(color.rgb, vec3(2.2)) * alpha, alpha);
}

vec4 over(vec4 top, vec4 under) {
    return top + under * (1.0 - top.a);
}

// The point on the grid that the scene's depth puts at a place on the screen.
vec3 seen(vec2 uv) {
    return onGrid(uv, texture(sceneDepth, uv).r);
}

// The step to the neighbour on the same surface: of the one before and the one after, the nearer. At the edge of
// something the other one is whatever lies behind it, which would tilt the surface. A sky neighbour is infinitely far.
vec3 surfaceStep(vec3 point, vec2 uv, vec2 step) {
    vec3 before = point - seen(uv - step);
    vec3 after = seen(uv + step) - point;
    bool beforeOk = finite(before) && texture(sceneDepth, uv - step).r > 0.0;
    bool afterOk = finite(after) && texture(sceneDepth, uv + step).r > 0.0;
    if (!afterOk || (beforeOk && dot(before, before) < dot(after, after))) {
        return beforeOk ? before : vec3(0.0);
    }

    return after;
}

void main() {
    overlay = overlays[inOverlay];
    vec2 uv = gl_FragCoord.xy * constants.screen.zw;
    float depth = texture(sceneDepth, uv).r;
    vec3 point = onGrid(uv, depth);

    // The far plane is infinitely far off, so a sky pixel has no position.
    vec2 low = overlay.area.xy;
    vec2 high = low + overlay.area.zw;
    bool sky = depth <= 0.0 || !finite(point);
    if (sky || any(lessThan(point.xz, low)) || any(greaterThan(point.xz, high)) || abs(point.y) > overlay.cell.z) {
        discard;
    }

    // Which way the surface faces and how much of the grid a pixel covers, from the neighbouring pixels' depth. Not
    // from screen derivatives, which mix in what's behind at the edge of something and leave a dotted rim there.
    vec3 stepX = surfaceStep(point, uv, vec2(constants.screen.z, 0.0));
    vec3 stepY = surfaceStep(point, uv, vec2(0.0, constants.screen.w));
    vec3 normal = cross(stepX, stepY);
    float facing = dot(normal, normal) > 0.0 ? abs(normal.y) * inversesqrt(dot(normal, normal)) : 1.0;
    float flatSize = distance(onGrid(uv + vec2(constants.screen.z, 0.0), depth), point);
    float pixel = clamp(max(length(stepX.xz), length(stepY.xz)), flatSize, flatSize * MAX_STRETCH);

    // The point's cell, how far it is in from each of the cell's edges, and the cell across each.
    vec2 p = point.xz;
    ivec2 cell;
    float edges[6];
    ivec2 across[6];
    int sides;
    if (overlay.info.y == SHAPE_SQUARE) {
        vec2 cells = p / overlay.cell.xy;
        cell = ivec2(floor(cells));
        vec2 inCell = (cells - vec2(cell)) * overlay.cell.xy;
        edges[0] = inCell.x;
        edges[1] = overlay.cell.x - inCell.x;
        edges[2] = inCell.y;
        edges[3] = overlay.cell.y - inCell.y;
        sides = 4;
        for (int i = 0; i < 4; i++) {
            across[i] = cell + SQUARE_STEPS[i];
        }
    } else {
        cell = hexOn(p);
        vec2 offset = p - hexMiddle(vec2(cell));
        sides = 6;
        for (int i = 0; i < 6; i++) {
            vec2 toNext = hexMiddle(vec2(HEX_STEPS[i]));
            float apart = length(toNext);
            edges[i] = apart * 0.5 - dot(offset, toNext / apart);
            across[i] = cell + HEX_STEPS[i];
        }
    }

    // Lines run along every edge of a covered cell, half on each side. The fill fades in from the edges where the
    // covered cells end, and the border runs inside them, with a pixel of softening outside.
    float lineHalf = overlay.widths.x * 0.5;
    float border = overlay.widths.y;
    float softness = overlay.widths.z;
    bool inside = covered(cell);
    float line = 0.0;
    float fill = inside ? 1.0 : 0.0;
    float rim = 0.0;
    for (int i = 0; i < sides; i++) {
        bool next = covered(across[i]);
        float d = edges[i];
        if (inside || next) {
            line = max(line, stroke(d, lineHalf, pixel));
        }

        if (inside && !next) {
            fill *= softness > 0.0 ? smoothstep(0.0, softness, d) : 1.0;
            rim = max(rim, stroke(d, border, pixel));
        } else if (!inside && next && border > 0.0) {
            rim = max(rim, 1.0 - smoothstep(0.0, pixel, d));
        }
    }

    // A square's corner is shared by four cells. Where the cell across the corner is covered, its lines end here, so
    // they're capped; where both cells beside this one are covered and that one isn't, the fill and the border turn
    // the inside corner.
    if (overlay.info.y == SHAPE_SQUARE) {
        for (int corner = 0; corner < 4; corner++) {
            int x = corner & 1;
            int z = 2 + (corner >> 1);
            float dx = edges[x];
            float dz = edges[z];
            bool besideX = covered(across[x]);
            bool besideZ = covered(across[z]);
            bool diagonal = covered(cell + SQUARE_STEPS[x] + SQUARE_STEPS[z]);
            if (diagonal) {
                line = max(line, stroke(max(dx, dz), lineHalf, pixel));
            }

            if (inside && besideX && besideZ && !diagonal) {
                fill *= softness > 0.0 ? smoothstep(0.0, softness, length(vec2(dx, dz))) : 1.0;
                rim = max(rim, stroke(max(dx, dz), border, pixel));
            }
        }
    }

    float reach = overlay.widths.w;
    if (reach > 0.0) {
        line *= 1.0 - smoothstep(reach * 0.3, reach, distance(p, overlay.focus.xy));
    }

    vec4 color = premultiplied(overlay.fillColor, fill);
    color = over(premultiplied(overlay.lineColor, line), color);
    color = over(premultiplied(overlay.borderColor, rim), color);
    color *= smoothstep(FACING_NONE, FACING_FULL, facing) * (1.0 - smoothstep(DEPTH_FADE_START, 1.0, abs(point.y) / overlay.cell.z));
    if (color.a <= 0.0) {
        discard;
    }

    outColor = color;
}

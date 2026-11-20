// The curves that fit the scene's light into what a screen shows, for the grade's bake in
// grade_lut.comp. Each takes linear light and gives linear display colour, which may go a
// little past 0 and 1: the bake keeps that so its table interpolates smoothly, and clamps later.

// ACES expects its input about this much brighter than our lights produce. Middle
// grey would otherwise come out far too dark.
const float ACES_EXPOSURE = 1.0 / 0.6;

// Into and out of the colour space the ACES curve works in, written column by column.
const mat3 ACES_INPUT = mat3(
    0.59719, 0.07600, 0.02840,
    0.35458, 0.90834, 0.13383,
    0.04823, 0.01566, 0.83777);
const mat3 ACES_OUTPUT = mat3(
    1.60475, -0.10208, -0.00327,
    -0.53108, 1.10813, -0.07276,
    -0.07367, -0.00605, 1.07602);

// Stephen Hill's fit of the ACES film transform. Working in the ACES colour space lets
// very bright colours fade towards white without shifting hue.
vec3 acesFilmic(vec3 color) {
    vec3 x = ACES_INPUT * color * ACES_EXPOSURE;
    x = (x * (x + 0.0245786) - 0.000090537) / (x * (0.983729 * x + 0.4329510) + 0.238081);
    return ACES_OUTPUT * x;
}

// AgX, as Blender 4 uses with its default look, in Benjamin Wrensch's minimal form. The inset
// matrix pulls every colour a little towards white before the curve, so a bright, strongly
// coloured light fades towards white instead of clipping to one flat hue. Written column by column.
const mat3 AGX_INSET = mat3(
    0.842479062253094, 0.0423282422610123, 0.0423756549057051,
    0.0784335999999992, 0.878468636469772, 0.0784336,
    0.0792237451477643, 0.0791661274605434, 0.879142973793104);
const mat3 AGX_OUTSET = mat3(
    1.19687900512017, -0.0528968517574562, -0.0529716355144438,
    -0.0980208811401368, 1.15190312990417, -0.0980434501171241,
    -0.0990297440797205, -0.0989611768448433, 1.15107367264116);

// The log2 range the curve spans: about 10 stops below middle grey and 6.5 above.
const float AGX_MIN_EV = -12.47393;
const float AGX_MAX_EV = 4.026069;

// A polynomial fit of AgX's default contrast curve, over log-encoded 0 to 1.
vec3 agxContrast(vec3 x) {
    vec3 x2 = x * x;
    vec3 x4 = x2 * x2;
    return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
}

// look.x is a power and look.y a saturation, worked on the curve's display values the way
// Blender's looks are: Punchy is 1.35 and 1.4, the base look 1 and 1.
vec3 agx(vec3 color, vec2 look) {
    vec3 x = AGX_INSET * color;
    x = clamp(log2(max(x, vec3(1e-10))), AGX_MIN_EV, AGX_MAX_EV);
    x = max(agxContrast((x - AGX_MIN_EV) / (AGX_MAX_EV - AGX_MIN_EV)), vec3(0.0));
    float luma = dot(x, vec3(0.2126, 0.7152, 0.0722));
    x = luma + look.y * (pow(x, vec3(look.x)) - luma);
    // The curve gives display values for a 2.2 gamma screen; the target wants them linear. The sign is
    // kept, so a value just under 0 stays just under instead of folding back up.
    x = AGX_OUTSET * x;
    return sign(x) * pow(abs(x), vec3(2.2));
}

// Khronos PBR Neutral, version 1.0, written from the specification at
// https://github.com/KhronosGroup/ToneMapping (Apache License 2.0, copyright 2024 The Khronos
// Group Inc.). Colours whose brightest channel is under 0.76 pass through but for a small
// offset that keeps the darks from going muddy; above it the peak is rolled off towards 1 and
// the colour eased towards white, so base colours stay true until the light is truly bright.
const float NEUTRAL_START = 0.8 - 0.04;
const float NEUTRAL_DESATURATION = 0.15;

vec3 pbrNeutral(vec3 color) {
    float darkest = min(color.r, min(color.g, color.b));
    float offset = darkest < 0.08 ? darkest - 6.25 * darkest * darkest : 0.04;
    color -= offset;

    float peak = max(color.r, max(color.g, color.b));
    if (peak < NEUTRAL_START) {
        return color;
    }

    float room = 1.0 - NEUTRAL_START;
    float newPeak = 1.0 - room * room / (peak + room - NEUTRAL_START);
    color *= newPeak / peak;
    float toWhite = 1.0 - 1.0 / (NEUTRAL_DESATURATION * (peak - newPeak) + 1.0);
    return mix(color, vec3(newPeak), toWhite);
}

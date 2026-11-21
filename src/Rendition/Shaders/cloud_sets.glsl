// The clouds' own set, which cloud_shadow.comp, cloud_march.comp and cloud_resolve.comp share.
// Must match CloudSets.cs. See "Clouds" in docs/design.md.

layout(set = 1, binding = 0) uniform sampler3D cloudShapeTexture;
layout(set = 1, binding = 1) uniform sampler3D cloudDetailTexture;
layout(set = 1, binding = 2) uniform sampler3D cloudWeatherTexture;
// The physical sky's sky-view table, which the clouds take their soft light and their haze from.
layout(set = 1, binding = 3) uniform sampler2D cloudSkyView;
layout(set = 1, binding = 4, rgba16f) uniform writeonly image3D cloudMarchTarget;
layout(set = 1, binding = 5) uniform sampler3D cloudMarchTexture;
layout(set = 1, binding = 6) uniform sampler3D cloudHistoryTexture;
layout(set = 1, binding = 7, rgba16f) uniform writeonly image3D cloudPictureTarget;
layout(set = 1, binding = 8, rgba16f) uniform writeonly image3D cloudShadowTarget;
layout(set = 1, binding = 9, rgba16f) uniform writeonly image3D cloudNextHistoryTarget;

// Which pixel of each 2 by 2 of the picture the march takes, by frame.cloudFrame.x, so four frames take them all.
const ivec2 CLOUD_SUBPIXELS[4] = ivec2[](ivec2(0, 0), ivec2(1, 1), ivec2(1, 0), ivec2(0, 1));

// Where a pixel of the picture at uv looks: a point next to the camera, relative to it, and a direction. Two points
// on the pixel's ray, so an orthographic camera's parallel rays work too.
void cloudRay(vec2 uv, out vec3 origin, out vec3 direction) {
    vec2 ndc = uv * 2.0 - 1.0;
    vec4 near = frame.cloudRays * vec4(ndc, 1.0, 1.0);
    vec4 farther = frame.cloudRays * vec4(ndc, 0.5, 1.0);
    origin = near.xyz / near.w;
    direction = normalize(farther.xyz / farther.w - origin);
}

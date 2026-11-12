// How much light water swallows on its way down from the surface, red first. Any stage can use
// it; water.glsl adds the caustics and shafts, which need a fragment shader.
// Needs common.glsl.
#ifndef WATER_ABSORPTION_GLSL
#define WATER_ABSORPTION_GLSL

bool isUnderwater() {
    return frame.waterAbsorption.a > 0.5;
}

float metresToSurface(vec3 position, vec3 l) {
    return max(frame.waterShallow.a - position.y, 0.0) / max(l.y, 0.1);
}

vec3 waterAbsorbed(float metres) {
    return exp(-frame.waterAbsorption.rgb * metres);
}

#endif

// The shells round the planet the clouds' layers lie between, shared by the clouds' passes and the
// sky captures in environment.comp. See "Clouds" in docs/design.md.
#ifndef CLOUD_SHELL_GLSL
#define CLOUD_SHELL_GLSL

// The planet's radius in metres; atmosphere.glsl's PLANET_RADIUS is in kilometres.
const float CLOUD_PLANET_RADIUS = 6360000.0;

// The distances along a ray from a point height metres above the ground to the shell at shell metres: the nearer
// and the farther, both negative when the ray misses it. Worked out from the difference of the two heights, so the
// planet's radius doesn't swamp them in single precision.
vec2 cloudShellHits(float height, float shell, vec3 direction) {
    float from = CLOUD_PLANET_RADIUS + height;
    float to = CLOUD_PLANET_RADIUS + shell;
    float b = from * direction.y;
    float c = (height - shell) * (from + to);
    float h = b * b - c;
    if (h < 0.0) {
        return vec2(-1.0);
    }

    float q = -(b + (b >= 0.0 ? 1.0 : -1.0) * sqrt(h));
    float first = q;
    float second = abs(q) > 1e-6 ? c / q : 0.0;
    return vec2(min(first, second), max(first, second));
}

#endif

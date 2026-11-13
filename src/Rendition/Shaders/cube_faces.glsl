// The six faces of a light probe's capture cube, as probe_fold.comp reads them and the baker's bake_probes.comp writes
// them. The same faces as reflection_probe.comp. See "Light probes" and "Baking probes" in docs/design.md.

// The direction through a point of a face, unnormalised, with uv from -1 to 1 across it, in the cube's own space.
vec3 cubeDirection(int face, vec2 uv) {
    switch (face) {
        case 0: return vec3(1.0, -uv.y, -uv.x);
        case 1: return vec3(-1.0, -uv.y, uv.x);
        case 2: return vec3(uv.x, 1.0, uv.y);
        case 3: return vec3(uv.x, -1.0, -uv.y);
        case 4: return vec3(uv.x, -uv.y, 1.0);
        default: return vec3(-uv.x, -uv.y, -1.0);
    }
}

// The world direction through a point of a face, unnormalised. A cube map is mirrored compared with the camera that
// drew it, so z swaps back.
vec3 cubeWorldDirection(int face, vec2 uv) {
    vec3 c = cubeDirection(face, uv);
    return vec3(c.x, c.y, -c.z);
}

// The face a world direction goes through, and where on it as uv from -1 to 1: the reverse of cubeWorldDirection.
int cubeFaceOf(vec3 w, out vec2 uv) {
    vec3 c = vec3(w.x, w.y, -w.z);
    vec3 a = abs(c);
    if (a.x >= a.y && a.x >= a.z) {
        uv = (c.x > 0.0 ? vec2(-c.z, -c.y) : vec2(c.z, -c.y)) / a.x;
        return c.x > 0.0 ? 0 : 1;
    }

    if (a.y >= a.z) {
        uv = (c.y > 0.0 ? vec2(c.x, c.z) : vec2(c.x, -c.z)) / a.y;
        return c.y > 0.0 ? 2 : 3;
    }

    uv = (c.z > 0.0 ? vec2(c.x, -c.y) : vec2(-c.x, -c.y)) / a.z;
    return c.z > 0.0 ? 4 : 5;
}

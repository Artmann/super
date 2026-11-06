"""
Builds the courtyard in Blender and exports it as content/models/courtyard.glb.

Run it inside Blender, from the Text Editor with the file opened from disk, or with
exec(compile(source, path, "exec"), {"__name__": "__main__", "__file__": path}). It builds into its own scene,
"Courtyard", so whatever else is open is left alone, writes the model, and never saves
the .blend. Run as a module, it only defines build() and export(scene).

Blender is Z up, and the glTF exporter turns that into the game's Y up: Blender (x, y, z)
is the game's (x, z, -y). The brick wall faces -Y here, so it faces +Z in the game.

UVs are in metres. A material named cy_graph_* gets a material graph in the model's
cook sidecar, and the cooker divides its UVs by the graph's width, so a brick is the size
of a real brick wherever it is.
"""

import bpy
import bmesh
import math
import os
import random
from mathutils import Matrix, Vector, noise

SCENE = "Courtyard"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "content", "models", "courtyard.glb")

rng = random.Random(7)
noise.seed_set(7)

# The building's front face, its left corner and its height.
WALL_Y = 4.0
WALL_X0 = -2.8
WALL_X1 = 11.0
WALL_TOP = 6.4
WALL_BACK = 9.0

# The window: centre, sill, where the arch springs, and how high it rises.
WIN_X = 2.4
WIN_W = 2.0
SILL_Z = 1.15
SPRING_Z = 3.55
ARCH_RISE = 0.36
REVEAL = 0.34

# A brick pier standing out from the wall to the right of the lamp.
PIER_X0 = 6.3
PIER_X1 = 7.0
PIER_DEPTH = 0.14

# The low dry-stone wall runs straight out from the building, square to it, towards the camera.
# Its courtyard face is at DRY_X, and it reaches DRY_LENGTH out from the plinth.
DRY_X = -1.6
DRY_THICKNESS = 0.65
DRY_LENGTH = 7.0

PUDDLE = (3.0, 0.9, 1.7, 0.95)  # centre x, y and radii

# The flagstone path: in from the front left, round past the barrel and along the wall's foot.
PATH = [(-0.6, -9.0), (-0.4, -5.0), (0.2, -2.0), (0.9, 0.2), (1.2, 1.8), (2.0, 2.9), (3.4, 3.25), (4.6, 3.3)]
# A second arm leaves it left of the puddle and comes straight at the camera, so the
# paving runs out of the bottom of the frame.
SPUR = [(0.5, -0.9), (2.2, -2.0), (3.6, -3.0), (4.6, -4.4), (5.3, -6.4), (5.6, -9.0)]
PATHS = [PATH, SPUR]


# ---------------------------------------------------------------------------------------
# Scene and materials

def fresh_scene():
    sc = bpy.data.scenes.get(SCENE) or bpy.data.scenes.new(SCENE)
    for ob in list(sc.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    for me in list(bpy.data.meshes):
        if me.users == 0 and me.name.startswith("cy_"):
            bpy.data.meshes.remove(me)
    return sc


def material(name, color, metallic=0.0, roughness=0.6, emission=None, strength=1.0):
    full = "cy_" + name
    m = bpy.data.materials.get(full) or bpy.data.materials.new(full)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    if emission is not None:
        bsdf.inputs["Emission Color"].default_value = (*emission, 1.0)
        bsdf.inputs["Emission Strength"].default_value = strength
    m.diffuse_color = (*color, 1.0)
    return m


def make_materials():
    # The graph materials only need a colour for Blender's viewport; the cook replaces it.
    return {
        "bricks": material("graph_bricks", (0.45, 0.16, 0.1)),
        "stone": material("graph_stone", (0.45, 0.43, 0.38)),
        "flagstone": material("graph_flagstone", (0.4, 0.4, 0.38)),
        "dirt": material("graph_dirt", (0.25, 0.17, 0.11)),
        "wood": material("graph_wood", (0.5, 0.34, 0.2)),
        "rust": material("graph_rust", (0.35, 0.2, 0.12)),
        "mortar": material("mortar", (0.32, 0.3, 0.27), roughness=0.95),
        "arch1": material("arch_brick_a", (0.42, 0.13, 0.07), roughness=0.85),
        "arch2": material("arch_brick_b", (0.5, 0.17, 0.09), roughness=0.85),
        "arch3": material("arch_brick_c", (0.36, 0.12, 0.08), roughness=0.85),
        "iron": material("iron", (0.05, 0.048, 0.045), metallic=0.8, roughness=0.45),
        "frame": material("window_frame", (0.04, 0.035, 0.03), metallic=0.6, roughness=0.5),
        "glass": material("graph_glass", (0.3, 0.36, 0.34), roughness=0.05),
        "interior": material("interior", (0.015, 0.013, 0.012), roughness=1.0),
        "shade": material("shade_inner", (0.8, 0.75, 0.65), roughness=0.5),
        "bulb": material("bulb", (1.0, 0.85, 0.6), emission=(1.0, 0.75, 0.45), strength=12.0),
        "water": material("water", (0.03, 0.025, 0.02), roughness=0.02),
        "ivy": material("ivy", (0.12, 0.2, 0.06), roughness=0.7),
        "vine": material("vine", (0.12, 0.09, 0.06), roughness=0.9),
        "plaster": material("plaster", (0.72, 0.7, 0.66), roughness=0.95),
        "dead_leaf": material("dead_leaf", (0.42, 0.24, 0.1), roughness=0.8),
        "dead_leaf_orange": material("dead_leaf_orange", (0.7, 0.36, 0.08), roughness=0.75),
        "grass": material("grass", (0.2, 0.3, 0.08), roughness=0.7),
        "grass_dry": material("grass_dry", (0.42, 0.4, 0.18), roughness=0.8),
    }


def emit(sc, name, bm, mats, smooth=False):
    me = bpy.data.meshes.new("cy_" + name)
    if smooth:
        for f in bm.faces:
            f.smooth = True
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    for m in mats:
        me.materials.append(m)
    ob = bpy.data.objects.new("cy_" + name, me)
    sc.collection.objects.link(ob)
    return ob


# ---------------------------------------------------------------------------------------
# UVs, all in metres

def uv_layer(bm):
    return bm.loops.layers.uv.verify()


def box_uv(bm, faces, offset=(0.0, 0.0, 0.0)):
    """Projects each face on the plane its normal is closest to, in metres."""
    uv = uv_layer(bm)
    ox, oy, oz = offset
    for f in faces:
        n = f.normal
        ax, ay, az = abs(n.x), abs(n.y), abs(n.z)
        for loop in f.loops:
            x, y, z = loop.vert.co.x + ox, loop.vert.co.y + oy, loop.vert.co.z + oz
            if az >= ax and az >= ay:
                loop[uv].uv = (x, y)
            elif ax >= ay:
                loop[uv].uv = (y if n.x > 0 else -y, z)
            else:
                loop[uv].uv = (-x if n.y > 0 else x, z)


def faces_verts(faces):
    return list({v for f in faces for v in f.verts})


def faces_of(verts):
    out = set()
    for v in verts:
        out.update(v.link_faces)
    return list(out)


# ---------------------------------------------------------------------------------------
# Shapes

def prism(bm, outline, y0, y1, mat):
    """A solid from a polygon in x and z, from y0 to y1."""
    front = [bm.verts.new((x, y0, z)) for x, z in outline]
    back = [bm.verts.new((x, y1, z)) for x, z in outline]
    faces = [bm.faces.new(front), bm.faces.new(list(reversed(back)))]
    count = len(outline)
    for i in range(count):
        j = (i + 1) % count
        faces.append(bm.faces.new((front[i], back[i], back[j], front[j])))
    for f in faces:
        f.material_index = mat
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def box(bm, lo, hi, mat, bevel=0.0):
    lo, hi = Vector(lo), Vector(hi)
    result = bmesh.ops.create_cube(bm, size=1.0)
    verts = result["verts"]
    for v in verts:
        v.co = Vector((
            lo.x if v.co.x < 0 else hi.x,
            lo.y if v.co.y < 0 else hi.y,
            lo.z if v.co.z < 0 else hi.z,
        ))
    faces = faces_of(verts)
    if bevel > 0.0:
        edges = list({e for f in faces for e in f.edges})
        made = bmesh.ops.bevel(bm, geom=edges, offset=bevel, segments=1, affect="EDGES", profile=0.5)
        faces = [f for f in set(faces) | set(made["faces"]) if f.is_valid]
    for f in faces:
        f.material_index = mat
    return faces


def rock(bm, center, dims, mat, power=3.0, bumps=0.12, yaw=0.0, segments=(14, 9), uv_offset=True, detail=0.0, flat_front=None):
    """A stone: a superellipsoid, scaled, roughened with noise and turned about Z. detail adds
    small, sharp knobs on top of the broad lumps. flat_front, a fraction of the depth, shaves
    the -Y side flat, as a waller dresses the face of a stone."""
    result = bmesh.ops.create_uvsphere(bm, u_segments=segments[0], v_segments=segments[1], radius=1.0)
    verts = result["verts"]
    seed = Vector((rng.uniform(-100, 100), rng.uniform(-100, 100), rng.uniform(-100, 100)))
    cos_y, sin_y = math.cos(yaw), math.sin(yaw)
    e = 2.0 / power
    for v in verts:
        d = v.co.normalized()
        p = Vector(tuple(math.copysign(abs(c) ** e, c) for c in d))
        dressed = flat_front is not None and p.y < -flat_front
        if dressed:
            p.y = -flat_front + (p.y + flat_front) * 0.08
        n = noise.fractal(d * 1.6 + seed, 0.6, 2.0, 3)
        scale = 1.0 + (bumps * n) * (0.35 if dressed else 1.0) + detail * noise.noise(d * 6.0 + seed)
        local = Vector((p.x * dims[0] * 0.5, p.y * dims[1] * 0.5, p.z * dims[2] * 0.5)) * scale
        v.co = Vector((
            center[0] + local.x * cos_y - local.y * sin_y,
            center[1] + local.x * sin_y + local.y * cos_y,
            center[2] + local.z,
        ))
    faces = faces_of(verts)
    for f in faces:
        f.material_index = mat
        f.smooth = True
    bm.normal_update()
    offset = (rng.uniform(0, 2), rng.uniform(0, 2), rng.uniform(0, 2)) if uv_offset else (0, 0, 0)
    box_uv(bm, faces, offset)
    return verts


def lathe(bm, profile, segments, center, mat, close_top=False, close_bottom=False, angle0=0.0, angle1=2 * math.pi):
    """Turns a profile of (radius, z) about Z. UVs: u around in metres, v along the profile in metres."""
    uv = uv_layer(bm)
    full = abs(angle1 - angle0 - 2 * math.pi) < 1e-6
    steps = segments if full else segments + 1
    rings = []
    for i in range(steps):
        a = angle0 + (angle1 - angle0) * i / segments
        ca, sa = math.cos(a), math.sin(a)
        rings.append([bm.verts.new((center[0] + r * ca, center[1] + r * sa, center[2] + z)) for r, z in profile])
    lengths = [0.0]
    for k in range(1, len(profile)):
        (r0, z0), (r1, z1) = profile[k - 1], profile[k]
        lengths.append(lengths[-1] + math.hypot(r1 - r0, z1 - z0))
    faces = []
    count = segments
    for i in range(count):
        j = (i + 1) % steps if full else i + 1
        for k in range(len(profile) - 1):
            f = bm.faces.new((rings[i][k], rings[j][k], rings[j][k + 1], rings[i][k + 1]))
            f.material_index = mat
            faces.append(f)
            a_i = (angle1 - angle0) * i / segments
            a_j = (angle1 - angle0) * (i + 1) / segments
            for loop in f.loops:
                kk = k if loop.vert in (rings[i][k], rings[j][k]) else k + 1
                ai = a_i if loop.vert in (rings[i][k], rings[i][k + 1]) else a_j
                loop[uv].uv = (ai * profile[kk][0], lengths[kk])
    if close_top and full:
        f = bm.faces.new([ring[-1] for ring in rings])
        f.material_index = mat
        faces.append(f)
        box_uv(bm, [f])
    if close_bottom and full:
        f = bm.faces.new([ring[0] for ring in reversed(rings)])
        f.material_index = mat
        faces.append(f)
        box_uv(bm, [f])
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def tube(bm, points, radius, mat, segments=10):
    """A pipe along points, with closed ends."""
    pts = [Vector(p) for p in points]
    rings = []
    for i, p in enumerate(pts):
        t = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]).normalized()
        side = t.cross(Vector((0, 0, 1)))
        if side.length < 1e-4:
            side = t.cross(Vector((1, 0, 0)))
        side.normalize()
        up = side.cross(t).normalized()
        rings.append([bm.verts.new(p + (side * math.cos(a) + up * math.sin(a)) * radius)
                      for a in (2 * math.pi * k / segments for k in range(segments))])
    faces = []
    for i in range(len(rings) - 1):
        for k in range(segments):
            k1 = (k + 1) % segments
            faces.append(bm.faces.new((rings[i][k], rings[i][k1], rings[i + 1][k1], rings[i + 1][k])))
    faces.append(bm.faces.new(list(reversed(rings[0]))))
    faces.append(bm.faces.new(rings[-1]))
    for f in faces:
        f.material_index = mat
        f.smooth = True
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def smoothstep(e0, e1, x):
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


# ---------------------------------------------------------------------------------------
# The building: a brick wall with an arched window

def arch_points(steps=24):
    """The window's segmental arch from left to right: (x, z)."""
    half = WIN_W / 2
    radius = (half * half + ARCH_RISE * ARCH_RISE) / (2 * ARCH_RISE)
    cz = SPRING_Z + ARCH_RISE - radius
    a0 = math.atan2(SPRING_Z - cz, -half)
    a1 = math.atan2(SPRING_Z - cz, half)
    pts = []
    for i in range(steps + 1):
        a = a0 + (a1 - a0) * i / steps
        pts.append((WIN_X + radius * math.cos(a), cz + radius * math.sin(a)))
    return pts, (WIN_X, cz), radius, a0, a1


def build_wall(sc, M):
    bm = bmesh.new()
    x0, x1 = WIN_X - WIN_W / 2, WIN_X + WIN_W / 2
    faces = []
    faces += prism(bm, [(WALL_X0, 0), (x0, 0), (x0, WALL_TOP), (WALL_X0, WALL_TOP)], WALL_Y, WALL_BACK, 0)
    faces += prism(bm, [(x1, 0), (WALL_X1, 0), (WALL_X1, WALL_TOP), (x1, WALL_TOP)], WALL_Y, WALL_BACK, 0)
    faces += prism(bm, [(x0, 0), (x1, 0), (x1, SILL_Z), (x0, SILL_Z)], WALL_Y, WALL_BACK, 0)
    arch, _, _, _, _ = arch_points()
    top = list(arch) + [(x1, WALL_TOP), (x0, WALL_TOP)]
    faces += prism(bm, list(reversed(top)) if False else top, WALL_Y, WALL_BACK, 0)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    box_uv(bm, bm.faces[:])
    emit(sc, "wall", bm, [M["bricks"]])

    # The arch: two rings of bricks on end, the outer one with more bricks so the joints
    # don't line up, standing a little proud of the wall and running back into the reveal.
    bm = bmesh.new()
    pts, (cx, cz), radius, a0, a1 = arch_points()
    gap = 0.012
    span = (a0 - a1)
    ring_depth = 0.2
    # The rings run on past the springing a little, as the bricks of a real arch do.
    over = 0.05
    for ring, count in ((0, 17), (1, 21)):
        r0 = radius + ring * (ring_depth + 0.012)
        r1 = r0 + ring_depth
        start, sweep = a1 - over, span + 2 * over
        for i in range(count):
            b0 = start + sweep * (i / count) + gap / r0 / 2
            b1 = start + sweep * ((i + 1) / count) - gap / r0 / 2
            jitter = rng.uniform(-0.008, 0.008)
            outline = []
            for r, a in ((r0, b0), (r1 + jitter, b0), (r1 + jitter, b1), (r0, b1)):
                outline.append((cx + r * math.cos(a), cz + r * math.sin(a)))
            back = WALL_Y + (REVEAL if ring == 0 else 0.05)
            faces = prism(bm, outline, WALL_Y - 0.03 - rng.uniform(0, 0.012), back, 0)
            for f in faces:
                f.material_index = rng.randrange(3)
    # Mortar behind the arch bricks, so the gaps between them aren't holes.
    outer_r = radius + 2 * ring_depth + 0.012
    ring = []
    for i in range(25):
        a = a1 - over + (span + 2 * over) * i / 24
        ring.append((cx + (radius + 0.003) * math.cos(a), cz + (radius + 0.003) * math.sin(a)))
    for i in reversed(range(25)):
        a = a1 - over + (span + 2 * over) * i / 24
        ring.append((cx + (outer_r - 0.01) * math.cos(a), cz + (outer_r - 0.01) * math.sin(a)))
    prism(bm, ring, WALL_Y - 0.012, WALL_Y + 0.01, 3)
    emit(sc, "arch", bm, [M["arch1"], M["arch2"], M["arch3"], M["mortar"]])

    # The stone sill: three thick blocks, wider than the opening, standing well out from
    # the wall and running back under the window.
    bm = bmesh.new()
    sx0, sx1 = x0 - 0.2, x1 + 0.2
    joints = [sx0, sx0 + (sx1 - sx0) * 0.36, sx0 + (sx1 - sx0) * 0.7, sx1]
    for k in range(3):
        drop = rng.uniform(-0.008, 0.008)
        box(bm, (joints[k] + 0.006, WALL_Y - 0.15, SILL_Z - 0.2 + drop), (joints[k + 1] - 0.006, WALL_Y + REVEAL, SILL_Z + 0.03 + drop), 0, bevel=0.018)
    # Mortar between and behind the blocks.
    box(bm, (sx0 + 0.01, WALL_Y - 0.12, SILL_Z - 0.19), (sx1 - 0.01, WALL_Y + REVEAL, SILL_Z + 0.015), 1)
    box_uv(bm, bm.faces[:], (0.3, 0.7, 0.1))
    emit(sc, "sill", bm, [M["stone"], M["mortar"]])

    build_pier(sc, M)
    build_window(sc, M, x0, x1)


def build_pier(sc, M):
    """A brick pier standing out from the wall, full height, its bricks half a brick along
    from the wall's so its courses read as a separate piece of building."""
    bm = bmesh.new()
    box(bm, (PIER_X0, WALL_Y - PIER_DEPTH, 0.0), (PIER_X1, WALL_Y + 0.02, WALL_TOP + 0.08), 0)
    box_uv(bm, bm.faces[:], (0.19, 0.0, 0.0))
    emit(sc, "pier", bm, [M["bricks"]])


def build_window(sc, M, x0, x1):
    bm = bmesh.new()
    y = WALL_Y + REVEAL
    t = 0.07     # frame depth
    fw = 0.075   # outer frame width
    bar = 0.04   # glazing bars
    arch, (cx, cz), radius, a0, a1 = arch_points(32)

    # The outer frame: a band just inside the opening, following the arch.
    inner = []
    for x, z in arch:
        d = Vector((x - cx, z - cz)).normalized()
        inner.append((x - d.x * fw, z - d.y * fw))
    outline_outer = [(x0, SILL_Z + 0.02)] + arch[::-1][::-1] + [(x1, SILL_Z + 0.02)]
    outline_outer = [(x0, SILL_Z + 0.02)] + list(arch) + [(x1, SILL_Z + 0.02)]
    outline_inner = [(x0 + fw, SILL_Z + 0.02 + fw)] + [(max(x0 + fw, min(x1 - fw, x)), z) for x, z in inner] + [(x1 - fw, SILL_Z + 0.02 + fw)]
    # A band is easiest as strips: sides, bottom, and the arch as small blocks.
    box(bm, (x0, y - t, SILL_Z + 0.02), (x0 + fw, y, SPRING_Z), 0)
    box(bm, (x1 - fw, y - t, SILL_Z + 0.02), (x1, y, SPRING_Z), 0)
    box(bm, (x0, y - t, SILL_Z + 0.02), (x1, y, SILL_Z + 0.02 + fw), 0)
    for i in range(len(arch) - 1):
        (ax, az), (bx, bz) = arch[i], arch[i + 1]
        (ix, iz), (jx, jz) = inner[i], inner[i + 1]
        prism(bm, [(ax, az), (bx, bz), (jx, jz), (ix, iz)], y - t, y, 0)

    # Glazing bars: three columns and five rows below the arch, the bars running up into it.
    cols = 3
    rows = 5
    left, right = x0 + fw, x1 - fw
    bottom, top = SILL_Z + 0.02 + fw, SPRING_Z
    for c in range(1, cols):
        x = left + (right - left) * c / cols
        # Where this bar meets the arch.
        dx = x - cx
        z_top = cz + math.sqrt(max(radius - fw, 0) ** 2 - dx * dx)
        box(bm, (x - bar / 2, y - t * 0.8, bottom), (x + bar / 2, y - t * 0.1, z_top), 0)
    for r in range(1, rows + 1):
        z = bottom + (top - bottom) * r / (rows + 0.0) if r < rows else top
        box(bm, (left, y - t * 0.8, z - bar / 2), (right, y - t * 0.1, z + bar / 2), 0)
    box_uv(bm, bm.faces[:])
    emit(sc, "window_frame", bm, [M["frame"]])

    # The glass, one pane facing the courtyard so the light passes through one layer, and a
    # dark room behind it.
    bm = bmesh.new()
    outline = [(x0 + 0.01, SILL_Z + 0.03)] + [(x, z - 0.01) for x, z in arch[1:-1]] + [(x1 - 0.01, SILL_Z + 0.03)]
    face = bm.faces.new([bm.verts.new((x, y - t * 0.5, z)) for x, z in outline])
    face.normal_update()
    if face.normal.y > 0:
        face.normal_flip()
    box_uv(bm, [face])
    emit(sc, "glass", bm, [M["glass"]])
    bm = bmesh.new()
    box(bm, (x0 - 0.3, y + 0.05, 0.4), (x1 + 0.3, y + 3.5, SPRING_Z + 1.2), 0)
    bmesh.ops.reverse_faces(bm, faces=bm.faces[:])
    emit(sc, "interior", bm, [M["interior"]])


def build_plinth(sc, M):
    """One unbroken course of big rough stones along the foot of the brick wall: blocks of
    mixed length butted together, the joints between them narrow and filled with mortar.
    The course stands well proud of the brickwork, about 30 cm, so its rounded fronts catch
    the sun and throw a shadow on the ground."""
    bm = bmesh.new()
    depth = 0.52
    x = WALL_X0 - 0.05
    while x < WALL_X1:
        length = rng.uniform(0.38, 0.75)
        height = rng.uniform(0.46, 0.58)
        cx = x + length / 2
        # In front of the pier the course steps out with it.
        front = WALL_Y - (PIER_DEPTH if PIER_X0 - 0.1 < cx < PIER_X1 + 0.1 else 0.0)
        # The face is dressed only lightly, so most of the stone's round shoulder shows.
        rock(bm, (cx, front - 0.06 + rng.uniform(-0.03, 0.03), height / 2 - 0.05), (length + 0.03, depth, height), 0,
             power=4.0, bumps=0.1, yaw=rng.uniform(-0.05, 0.05), segments=(20, 14), detail=0.03, flat_front=0.85)
        x += length
    # The corner: stones down the building's side too.
    y = WALL_Y - 0.3
    while y < WALL_Y + 4.0:
        length = rng.uniform(0.38, 0.75)
        height = rng.uniform(0.46, 0.58)
        rock(bm, (WALL_X0 - 0.06, y + length / 2, height / 2 - 0.05), (depth, length + 0.03, height), 0,
             power=4.0, bumps=0.1, segments=(20, 14), detail=0.03)
        y += length
    # Mortar behind the stones, so a joint is a dark line and not a gap.
    box(bm, (WALL_X0 - 0.2, WALL_Y - 0.17, 0.0), (WALL_X1, WALL_Y + 0.05, 0.42), 1)
    box(bm, (PIER_X0 + 0.02, WALL_Y - PIER_DEPTH - 0.17, 0.0), (PIER_X1 - 0.02, WALL_Y, 0.42), 1)
    emit(sc, "plinth", bm, [M["stone"], M["mortar"]])


def build_lamp(sc, M):
    """A barn lamp: a tall plate on the wall, a gooseneck arm that rises out of it, curls over
    and drops into a wide dome shade, dark outside and pale inside."""
    bm = bmesh.new()
    lx, lz = WIN_X + 2.45, SPRING_Z + 0.55  # where the arm leaves the plate
    y = WALL_Y
    # The plate: a rounded slab standing on end, and a boss where the arm comes out.
    box(bm, (lx - 0.075, y - 0.035, lz - 0.24), (lx + 0.075, y + 0.01, lz + 0.1), 0, bevel=0.012)
    boss = lathe(bm, [(0.0, 0.0), (0.045, 0.0), (0.045, 0.035), (0.032, 0.05), (0.0, 0.05)], 20, (0, 0, 0), 0)
    for v in faces_verts(boss):
        v.co = Vector((lx + v.co.x, y - 0.03 - v.co.z, lz + v.co.y))
    # The gooseneck: out of the boss, then up and over in a half circle that swings out from
    # the wall and across towards the window, then straight down into the shade.
    start = Vector((lx, y - 0.08, lz))
    reach = Vector((-0.3, -0.42, 0.0))
    bend = reach.length / 2
    out = reach.normalized()
    arm = [(lx, y - 0.04, lz), tuple(start)]
    for i in range(1, 25):
        a = math.pi * i / 24
        p = start + out * (bend - bend * math.cos(a)) + Vector((0, 0, bend * 0.8 * math.sin(a)))
        arm.append(tuple(p))
    sx, sy = start.x + reach.x, start.y + reach.y
    arm.append((sx, sy, lz - 0.06))
    tube(bm, arm, 0.026, 0, segments=12)
    # The shade: a neck, a low dome, and a wide, shallow skirt with a rolled rim, so from the
    # ground you see into it.
    sz = lz - 0.08
    outer = [(0.035, 0.06), (0.06, 0.05), (0.08, 0.0), (0.16, -0.045), (0.3, -0.085), (0.42, -0.115), (0.45, -0.125), (0.455, -0.14)]
    inner = [(0.445, -0.135), (0.41, -0.11), (0.29, -0.078), (0.15, -0.035), (0.07, 0.005), (0.0, 0.02)]
    turned = lathe(bm, outer, 48, (sx, sy, sz), 0, close_top=True)
    turned += lathe(bm, inner, 48, (sx, sy, sz), 1)
    turned += lathe(bm, [(0.455, -0.14), (0.445, -0.135)], 48, (sx, sy, sz), 0)
    # The bulb holder under the dome.
    turned += lathe(bm, [(0.0, -0.01), (0.045, -0.01), (0.045, -0.06), (0.0, -0.06)], 16, (sx, sy, sz), 0)
    for f in turned + boss:
        f.smooth = True
    # The plate stays flat shaded, so its bevels read as edges.
    emit(sc, "lamp", bm, [M["iron"], M["shade"]])

    bm = bmesh.new()
    bulb = bmesh.ops.create_uvsphere(bm, u_segments=16, v_segments=10, radius=0.075)
    for v in bulb["verts"]:
        v.co += Vector((sx, sy, sz - 0.11))
    emit(sc, "bulb", bm, [M["bulb"]], smooth=True)


# ---------------------------------------------------------------------------------------
# The low dry-stone wall on the left

def build_dry_wall(sc, M):
    """Built lying along X with its good face towards -Y, then turned a quarter so it runs
    out from the building towards -Y with that face towards the courtyard, +X."""
    bm = bmesh.new()
    x_start, x_end = -DRY_LENGTH, 0.0
    y0 = 0.0
    thickness = DRY_THICKNESS
    z = 0.0
    course = 0
    # Big blocky stones of mixed sizes with dressed, nearly flat faces, laid in uneven courses
    # with the joints tight. The bottom courses use the biggest stones.
    while z < 1.25:
        h = rng.uniform(0.24, 0.36) if course < 2 else rng.uniform(0.17, 0.3)
        x = x_start + rng.uniform(0.0, 0.25)
        while x < x_end:
            length = rng.uniform(0.32, 0.95) if course < 2 else rng.uniform(0.25, 0.7)
            hh = h * rng.uniform(0.82, 1.0)
            cx = min(x + length / 2, x_end - length / 2 + 0.05)
            proud = rng.uniform(-0.025, 0.02)
            rock(bm, (cx, y0 + proud + 0.13, z + hh / 2), (length + 0.03, 0.36, hh + 0.025), 0,
                 power=4.5, bumps=0.1, yaw=rng.uniform(-0.05, 0.05), segments=(18, 12), detail=0.025, flat_front=0.75)
            # A second stone behind, so the wall looks the same from the far side.
            rock(bm, (cx + 0.1, y0 + thickness - 0.16, z + hh / 2), (length, 0.34, hh), 0,
                 power=4.0, bumps=0.12, yaw=rng.uniform(-0.08, 0.08), segments=(12, 8))
            x += length
        z += h * 0.97
        course += 1
    # Cope stones laid flat along the top, slightly wider than the wall.
    x = x_start
    while x < x_end:
        w = rng.uniform(0.35, 0.7)
        rock(bm, (x + w / 2, y0 + thickness / 2, z + 0.07), (w + 0.03, thickness + 0.06, 0.17), 0,
             power=4.0, bumps=0.1, yaw=rng.uniform(-0.04, 0.04), segments=(16, 10), detail=0.025)
        x += w
    # The hearting: a dark core, so the gaps between stones are shadow and not sky.
    box(bm, (x_start, y0 + 0.08, 0.0), (x_end, y0 + thickness - 0.1, z + 0.05), 1)
    # A quarter turn: along the wall becomes -Y from the plinth's face, and the good face becomes +X.
    for v in bm.verts:
        x, y = v.co.x, v.co.y
        v.co.x = DRY_X - (y - y0)
        v.co.y = WALL_Y - 0.1 + (x - x_end)
    emit(sc, "dry_wall", bm, [M["stone"], M["mortar"]])


# ---------------------------------------------------------------------------------------
# The ground: mud with a puddle, footprints and a rut, flagstones, pebbles and a deck

def path_distance(x, y):
    """How far (x, y) is from the middle of the nearest arm of the path."""
    best = 1e9
    for path in PATHS:
        for (ax, ay), (bx, by) in zip(path, path[1:]):
            abx, aby = bx - ax, by - ay
            t = max(0.0, min(1.0, ((x - ax) * abx + (y - ay) * aby) / (abx * abx + aby * aby)))
            px, py = ax + abx * t, ay + aby * t
            best = min(best, math.hypot(x - px, y - py))
    return best


PATH_HALF_WIDTH = 1.05


def puddle_shape(x, y):
    """Under 1 inside the puddle, about 1.15 at its edge."""
    cx, cy, rx, ry = PUDDLE
    return ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 + 0.35 * noise.noise(Vector((x * 1.3, y * 1.3, 5.0)))


def puddle_depth(x, y):
    return smoothstep(1.15, 0.35, puddle_shape(x, y))


def walk(points, stride):
    """Boot prints along a line through points: (x, y, heading, left or right)."""
    prints = []
    side = 1
    for (ax, ay), (bx, by) in zip(points, points[1:]):
        length = math.hypot(bx - ax, by - ay)
        heading = math.atan2(by - ay, bx - ax)
        steps = int(length / stride)
        for k in range(steps):
            t = k / max(steps, 1)
            px, py = ax + (bx - ax) * t, ay + (by - ay) * t
            nx, ny = -math.sin(heading), math.cos(heading)
            off = 0.09 * side
            prints.append((px + nx * off + rng.uniform(-0.02, 0.02), py + ny * off + rng.uniform(-0.02, 0.02),
                           heading + rng.uniform(-0.15, 0.15), side))
            side = -side
    return prints


# Someone walked from the path round the puddle to the crate, and back another way.
FOOTPRINTS = []

# A wheelbarrow's rut: one track across the mud, deep where it was wettest.
# It keeps to the right of the path's second arm.
RUT = [(7.4, -6.0), (7.2, -3.4), (7.0, -1.3), (7.3, 0.2), (7.8, 1.6)]


def footprint_depth(x, y):
    d = 0.0
    for px, py, heading, _ in FOOTPRINTS:
        dx, dy = x - px, y - py
        if dx * dx + dy * dy > 0.04:
            continue
        c, s = math.cos(heading), math.sin(heading)
        u, v = dx * c + dy * s, -dx * s + dy * c
        # A sole and a heel: two ellipses along the print, 28 cm long.
        sole = (((u - 0.04) / 0.09) ** 2 + (v / 0.05) ** 2)
        heel = (((u + 0.095) / 0.045) ** 2 + (v / 0.043) ** 2)
        d = max(d, smoothstep(1.0, 0.45, sole), smoothstep(1.0, 0.45, heel))
    return d


def rut_depth(x, y):
    best = 1e9
    for (ax, ay), (bx, by) in zip(RUT, RUT[1:]):
        abx, aby = bx - ax, by - ay
        t = max(0.0, min(1.0, ((x - ax) * abx + (y - ay) * aby) / (abx * abx + aby * aby)))
        best = min(best, math.hypot(x - ax - abx * t, y - ay - aby * t))
    # A trough 8 cm wide with a lip of pushed-up mud either side.
    return smoothstep(0.07, 0.0, best) - 0.35 * smoothstep(0.16, 0.09, best) * smoothstep(0.04, 0.09, best)


def ground_height(x, y):
    h = 0.03 * noise.fractal(Vector((x * 0.6, y * 0.6, 0.0)), 0.7, 2.0, 4)
    # Churned clods: sharp lumps from turbulence, strongest out in the open mud.
    h += 0.022 * noise.turbulence(Vector((x * 3.2, y * 3.2, 1.0)), 3, False, amplitude_scale=0.5)
    h += 0.01 * noise.noise(Vector((x * 9.0, y * 9.0, 4.0)))
    # Soft dents where the mud was trodden.
    h -= 0.02 * max(0.0, noise.noise(Vector((x * 2.5, y * 2.5, 9.0))))
    h -= 0.035 * footprint_depth(x, y)
    h -= 0.06 * rut_depth(x, y)
    h -= 0.09 * puddle_depth(x, y)
    # A muddy bank round the puddle, pushed up where feet went round it.
    edge = puddle_shape(x, y)
    h += 0.03 * math.exp(-((edge - 1.3) / 0.25) ** 2)
    # Under the flagstones the ground sits low, so only their tops show.
    h -= 0.03 * smoothstep(PATH_HALF_WIDTH + 0.3, PATH_HALF_WIDTH - 0.1, path_distance(x, y))
    # Behind the low wall it's a little higher and drier.
    h += 0.08 * smoothstep(WALL_Y + 0.6, WALL_Y + 1.5, y)
    # Pushed up against the foot of the walls.
    h += 0.04 * smoothstep(WALL_Y - 0.6, WALL_Y, y) * (1.0 if x > WALL_X0 - 0.2 else 0.5)
    # Under the deck it stays below the boards.
    dx0, dx1, dy0, dy1 = DECK
    if dx0 - 0.1 < x < dx1 + 0.1 and dy0 - 0.1 < y < dy1 + 0.1:
        h = min(h, DECK_TOP - 0.06)
    return h


def axis_steps(lo, hi, fine_lo, fine_hi, fine, coarse):
    """Grid lines from lo to hi, close together between fine_lo and fine_hi."""
    out = [lo]
    while out[-1] < hi:
        p = out[-1]
        out.append(p + (fine if fine_lo <= p < fine_hi else coarse))
    return out


def build_ground(sc, M):
    bm = bmesh.new()
    # Fine where the camera looks closely, coarse further off. A grid of lines rather than
    # patches, so the fine and coarse parts share their edges and nothing cracks.
    xs = axis_steps(-16.0, 14.0, -2.0, 11.0, 0.045, 0.3)
    ys = axis_steps(-12.0, 18.0, -5.0, 4.4, 0.045, 0.3)
    grid = []
    for y in ys:
        row = []
        for x in xs:
            row.append(bm.verts.new((x, y, ground_height(x, y))))
        grid.append(row)
    nx, ny = len(xs) - 1, len(ys) - 1
    for j in range(ny):
        for i in range(nx):
            bm.faces.new((grid[j][i], grid[j][i + 1], grid[j + 1][i + 1], grid[j + 1][i]))
    uv = uv_layer(bm)
    for f in bm.faces:
        f.smooth = True
        for loop in f.loops:
            loop[uv].uv = (loop.vert.co.x, loop.vert.co.y)
    emit(sc, "ground", bm, [M["dirt"]])

    # The water: one flat sheet at the puddle's level; the mud around it hides its edge.
    bm = bmesh.new()
    cx, cy, rx, ry = PUDDLE
    ring = [bm.verts.new((cx + rx * 1.4 * math.cos(a), cy + ry * 1.5 * math.sin(a), -0.045))
            for a in (2 * math.pi * k / 48 for k in range(48))]
    bm.faces.new(ring)
    emit(sc, "puddle", bm, [M["water"]])


def voronoi_cell(p, neighbours, max_radius, joint):
    """The cell around p, clipped by the bisectors with its neighbours, pulled in by half the joint."""
    poly = [(p[0] + max_radius * math.cos(a), p[1] + max_radius * math.sin(a))
            for a in (2 * math.pi * k / 20 for k in range(20))]
    for q in neighbours:
        nx, ny = q[0] - p[0], q[1] - p[1]
        dist = math.hypot(nx, ny)
        if dist < 1e-6:
            continue
        nx, ny = nx / dist, ny / dist
        limit = dist / 2 - joint / 2
        out = []
        for k in range(len(poly)):
            a, b = poly[k], poly[(k + 1) % len(poly)]
            da = (a[0] - p[0]) * nx + (a[1] - p[1]) * ny - limit
            db = (b[0] - p[0]) * nx + (b[1] - p[1]) * ny - limit
            if da <= 0:
                out.append(a)
            if (da <= 0) != (db <= 0):
                t = da / (da - db)
                out.append((a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t))
        poly = out
        if len(poly) < 3:
            return None
    return poly


def chaikin(poly, rounds=2):
    for _ in range(rounds):
        out = []
        for k in range(len(poly)):
            a, b = poly[k], poly[(k + 1) % len(poly)]
            out.append((a[0] * 0.75 + b[0] * 0.25, a[1] * 0.75 + b[1] * 0.25))
            out.append((a[0] * 0.25 + b[0] * 0.75, a[1] * 0.25 + b[1] * 0.75))
        poly = out
    return poly


def slab(bm, poly, centre, base_z, top, mat):
    """A flagstone: straight sides from below the ground up to a narrow chamfer, then a flat,
    gently uneven top that leans a little one way."""
    seed = Vector((rng.uniform(-50, 50), rng.uniform(-50, 50), 0))
    tilt = (rng.uniform(-0.025, 0.025), rng.uniform(-0.025, 0.025))

    def height(px, py, z):
        bump = 0.005 * noise.noise(Vector((px * 4, py * 4, 0)) + seed)
        return z + bump + (px - centre[0]) * tilt[0] + (py - centre[1]) * tilt[1]

    def ring(shrink, inset, z):
        out = []
        for x, y in poly:
            dx, dy = x - centre[0], y - centre[1]
            length = math.hypot(dx, dy) or 1.0
            px = centre[0] + dx * shrink - dx / length * inset
            py = centre[1] + dy * shrink - dy / length * inset
            out.append(bm.verts.new((px, py, height(px, py, z))))
        return out

    rings = [
        ring(1.0, 0.0, base_z - 0.07),
        ring(1.0, 0.0, base_z + top - 0.018),
        ring(1.0, 0.012, base_z + top - 0.004),
        ring(1.0, 0.024, base_z + top),
        # An inner ring, so the top can carry a little unevenness of its own.
        ring(0.55, 0.0, base_z + top + 0.003),
    ]
    faces = []
    n = len(poly)
    for r in range(len(rings) - 1):
        for k in range(n):
            k1 = (k + 1) % n
            faces.append(bm.faces.new((rings[r][k], rings[r][k1], rings[r + 1][k1], rings[r + 1][k])))
    faces.append(bm.faces.new(rings[-1]))
    for f in faces:
        f.material_index = mat
        f.smooth = True
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    box_uv(bm, faces, (rng.uniform(0, 2), rng.uniform(0, 2), 0))


def build_flagstones(sc, M):
    """Big flat stones cut from a Voronoi pattern, so they fit together with narrow joints."""
    bm = bmesh.new()
    points = []
    spacing = 0.62
    for j in range(-30, 30):
        for i in range(-30, 30):
            x = i * spacing + (spacing / 2 if j % 2 else 0) + rng.uniform(-0.16, 0.16) - 1.0
            y = j * spacing * 0.87 + rng.uniform(-0.16, 0.16) - 3.0
            edge = PATH_HALF_WIDTH + 0.15 * noise.noise(Vector((x * 1.3, y * 1.3, 3.0)))
            if path_distance(x, y) < edge and y < WALL_Y - 0.45 and puddle_depth(x, y) < 0.4:
                points.append((x, y))
    for p in points:
        near = [q for q in points if q is not p and abs(q[0] - p[0]) < 1.3 and abs(q[1] - p[1]) < 1.3]
        cell = voronoi_cell(p, near, rng.uniform(0.38, 0.46), rng.uniform(0.025, 0.045))
        if cell is None:
            continue
        # Round the corners only a little: these are cut stones, not cobbles.
        cell = chaikin(cell, 1)
        cx = sum(c[0] for c in cell) / len(cell)
        cy = sum(c[1] for c in cell) / len(cell)
        base = ground_height(cx, cy)
        slab(bm, cell, (cx, cy), max(base, -0.03), rng.uniform(0.035, 0.05), 0)
    emit(sc, "flagstones", bm, [M["flagstone"]])


def build_pebbles(sc, M):
    bm = bmesh.new()
    spots = []
    for _ in range(70):
        # Along the foot of the walls and the edges of the path.
        if rng.random() < 0.5:
            x, y = rng.uniform(DRY_X + 0.15, 10.5), WALL_Y - rng.uniform(0.38, 0.95)
        else:
            path = rng.choice(PATHS)
            t = rng.random()
            k = min(int(t * (len(path) - 1)), len(path) - 2)
            f = t * (len(path) - 1) - k
            (ax, ay), (bx, by) = path[k], path[k + 1]
            side = rng.choice((-1, 1)) * rng.uniform(PATH_HALF_WIDTH + 0.05, PATH_HALF_WIDTH + 0.5)
            dx, dy = bx - ax, by - ay
            length = math.hypot(dx, dy)
            x = ax + dx * f - dy / length * side
            y = ay + dy * f + dx / length * side
        if puddle_depth(x, y) > 0.2:
            continue
        s = rng.uniform(0.04, 0.12)
        rock(bm, (x, y, ground_height(x, y) + s * 0.15), (s * rng.uniform(1.0, 1.5), s, s * 0.6), 0,
             power=2.2, bumps=0.2, yaw=rng.uniform(0, math.pi), segments=(10, 7))
    emit(sc, "pebbles", bm, [M["stone"]])


def plank_uv(bm, faces, axis, index, along_offset):
    """Wood UVs: u along the board's length in metres, v placed so the board takes one of the
    graph's six planks, so its own grain and not a seam runs down it."""
    uv = uv_layer(bm)
    for f in faces:
        n = f.normal
        for loop in f.loops:
            co = loop.vert.co
            u = (co.x if axis == "x" else co.y if axis == "y" else co.z) + along_offset
            # Across the board: whichever of the other two axes the face spans most.
            others = [c for c, a in ((co.x, "x"), (co.y, "y"), (co.z, "z")) if a != axis]
            across = others[0] if abs(n.dot(Vector((1, 0, 0) if axis != "x" else (0, 1, 0)))) < 0.5 else others[1]
            loop[uv].uv = (u, (index % 6) / 6.0 + 0.012 + (across * 0.9) % (1 / 6.0 - 0.024))


DECK = (6.0, 9.6, 0.3, WALL_Y - 0.4)  # x0, x1, y0, y1
DECK_TOP = 0.085


def build_deck(sc, M):
    """A low deck of wide boards lying straight on bearers sunk in the mud, so no legs show.
    The boards are of different lengths and their ends are ragged."""
    bm = bmesh.new()
    x0, x1, y0, y1 = DECK
    board, gap = 0.2, 0.014
    i = 0
    y = y0
    while y + board <= y1:
        lift = rng.uniform(-0.005, 0.005)
        start = x0 + rng.uniform(-0.06, 0.06)
        end = x1 + rng.uniform(-0.06, 0.06)
        # Some rows are two boards butted end to end.
        if rng.random() < 0.5:
            split = rng.uniform(x0 + 1.0, x1 - 1.0)
            spans = ((start, split - 0.004), (split + 0.004, end))
        else:
            spans = ((start, end),)
        for a, b in spans:
            faces = box(bm, (a, y, DECK_TOP - 0.035 + lift), (b, y + board, DECK_TOP + lift), 0, bevel=0.007)
            plank_uv(bm, faces, "x", i, rng.uniform(0, 3))
            i += 1
        y += board + gap
    # Bearers under the boards, mostly below the mud.
    for x in (x0 + 0.15, (x0 + x1) / 2, x1 - 0.25):
        faces = box(bm, (x, y0 - 0.02, -0.08), (x + 0.1, y1, DECK_TOP - 0.035), 0)
        plank_uv(bm, faces, "y", 3, 0.0)
    emit(sc, "deck", bm, [M["wood"]])


crate_rng = random.Random(11)


def board(bm, length, width, thick, origin, ax, ay, az, index, bevel=None):
    """One bevelled board, laid out along ax, across ay and thick along az from origin. Its UVs
    are made before it's placed: u runs along it, and v keeps it inside one of the wood graph's
    six planks, so each board has its own grain and no seam."""
    faces = box(bm, (0, 0, 0), (length, width, thick), 0, bevel=bevel if bevel is not None else min(0.006, thick * 0.3))
    uv = uv_layer(bm)
    along = crate_rng.uniform(0, 3)
    band = (index % 6) / 6.0 + 0.012
    for f in faces:
        f.normal_update()
        n = f.normal
        for loop in f.loops:
            x, y, z = loop.vert.co
            if abs(n.x) > 0.7:
                loop[uv].uv = (y + along, band + z * 0.9)
            elif abs(n.y) > 0.7:
                loop[uv].uv = (x + along, band + z * 0.9)
            else:
                loop[uv].uv = (x + along, band + y * 0.9)
    verts = {v for f in faces for v in f.verts}
    for v in verts:
        x, y, z = v.co
        v.co = origin + ax * x + ay * y + az * z
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def nail(bm, position, normal, mat):
    """A rusty nail head: a squat disc standing about 5 mm out of the wood along normal."""
    turn = Vector((0, 0, 1)).rotation_difference(normal).to_matrix().to_4x4()
    made = bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=8, radius1=0.0075,
                                 radius2=0.006, depth=0.008,
                                 matrix=Matrix.Translation(position + normal * 0.001) @ turn)
    faces = faces_of(made["verts"])
    for f in faces:
        f.material_index = mat
        f.normal_update()
    box_uv(bm, faces, (crate_rng.uniform(0, 1), crate_rng.uniform(0, 1), 0))


def build_crate(sc, M):
    """A wooden crate, knocked together by hand. Each side is rows of boards of uneven width,
    each a little out of true, with a rail across the top and bottom and a diagonal brace
    between them. Thick bevelled posts stand proud at the corners. The lid overhangs the
    sides, has two cleats across it and has been put back a little crooked. Every board is
    held by rusty nail heads where it meets a post, a rail or a cleat."""
    bm = bmesh.new()
    crate_rng.seed(11)
    cx, cy, base = 7.0, WALL_Y - 1.2, DECK_TOP
    yaw = math.radians(-6)
    h = 0.42           # half the width of the box the boards are nailed to
    H = 0.8            # height of the sides
    t = 0.024          # board thickness
    fw = 0.11          # rails and brace
    post = 0.095       # the corner posts' side
    rows = 5
    Z = Vector((0, 0, 1))
    WOOD, INSIDE, IRON = 0, 1, 2
    index = 0

    for side, (normal, tangent) in enumerate((
        (Vector((0, -1, 0)), Vector((1, 0, 0))),
        (Vector((0, 1, 0)), Vector((-1, 0, 0))),
        (Vector((1, 0, 0)), Vector((0, 1, 0))),
        (Vector((-1, 0, 0)), Vector((0, -1, 0))),
    )):
        def at(u, v, d):
            return normal * (h + d) + tangent * u + Z * v

        # The boards: rows of uneven width with uneven gaps, each one a touch skew and
        # standing a little in or out, as a board does when it's nailed up by hand.
        half = h - 0.002
        gaps = [crate_rng.uniform(0.006, 0.02) for _ in range(rows - 1)]
        weights = [crate_rng.uniform(0.8, 1.2) for _ in range(rows)]
        unit = (H - sum(gaps)) / sum(weights)
        v0 = 0.0
        centres = []
        for r in range(rows):
            bh = weights[r] * unit
            tilt = crate_rng.uniform(-0.006, 0.006)
            along = tangent * math.cos(tilt) + Z * math.sin(tilt)
            up = Z * math.cos(tilt) - tangent * math.sin(tilt)
            board(bm, 2 * half, bh, t, at(-half, v0, crate_rng.uniform(-0.002, 0.002)), along, up, normal, index,
                  bevel=0.005)
            centres.append(v0 + bh / 2)
            index += 1
            v0 += bh + (gaps[r] if r < rows - 1 else 0.0)
        # Rails over the boards at the top and bottom, running between the posts.
        rail = h - 0.01
        lo, hi = 0.01, H - fw - 0.01
        board(bm, 2 * rail, fw, t, at(-rail, lo, t), tangent, Z, normal, index, bevel=0.007)
        board(bm, 2 * rail, fw, t, at(-rail, hi, t), tangent, Z, normal, index + 1, bevel=0.007)
        index += 2
        # A brace from one rail to the other, corner to corner, a little thinner than the rails
        # so its square ends tuck in behind them. Opposite sides lean opposite ways.
        lean = 1 if side % 2 == 0 else -1
        u0, u1 = -lean * (rail - 0.08), lean * (rail - 0.08)
        v_lo, v_hi = lo + fw, hi
        du, dv = u1 - u0, v_hi - v_lo
        length = math.hypot(du, dv)
        cu, sv = du / length, dv / length
        reach = abs(cu) * fw / (2 * sv) + 0.01
        along = tangent * cu + Z * sv
        across = Z * cu - tangent * sv
        start = at(u0 - cu * reach, v_lo - sv * reach, t) - across * (fw / 2)
        board(bm, length + 2 * reach, fw, t * 0.85, start, along, across, normal, index, bevel=0.006)
        index += 1

        # Nails: through each board into the posts, two to an end, missing the rows the
        # rails cover; two at each end of each rail; and one where the brace crosses a board.
        for r in range(1, rows - 1):
            for end in (-1, 1):
                for dz in (-0.028, 0.028):
                    nail(bm, at(end * (half - 0.04), centres[r] + dz, t), normal, IRON)
        for v in (lo + fw / 2, hi + fw / 2):
            for end in (-1, 1):
                for dz in (-0.028, 0.028):
                    nail(bm, at(end * (rail - 0.045), v + dz, 2 * t), normal, IRON)
        for r in range(rows):
            # Where the brace's middle line crosses this board's middle.
            k = (centres[r] - v_lo) / dv
            if 0.05 < k < 0.95:
                nail(bm, at(u0 + du * k, centres[r], t + t * 0.85), normal, IRON)

    # The corner posts: square timbers outside the boards, a little taller than the sides,
    # with their edges worn round.
    for sx in (-1, 1):
        for sy in (-1, 1):
            # Inside edge just within the boards' ends, so it covers them and stands out
            # past the rails.
            px = sx * (h - 0.01 + post / 2)
            py = sy * (h - 0.01 + post / 2)
            board(bm, H + 0.01, post, post, Vector((px - post / 2, py - post / 2, 0.0)),
                  Z, Vector((1, 0, 0)), Vector((0, 1, 0)), index, bevel=0.012)
            index += 1

    # A dark inside, so the gaps between the boards show shadow.
    box(bm, (-h + 0.005, -h + 0.005, 0.0), (h - 0.005, h - 0.005, H - 0.005), INSIDE)

    # The lid: boards from front to back that overhang the posts, and two cleats across
    # them. It is built square, then turned and slid as if it was dropped back on.
    before = set(bm.verts)
    lid_half = h - 0.01 + post + 0.03
    lid_t = 0.028
    lid_z = H + 0.01
    lid_rows = 6
    lid_gaps = [crate_rng.uniform(0.004, 0.014) for _ in range(lid_rows - 1)]
    lid_weights = [crate_rng.uniform(0.85, 1.15) for _ in range(lid_rows)]
    unit = (2 * lid_half - sum(lid_gaps)) / sum(lid_weights)
    x0 = -lid_half
    lid_centres = []
    for r in range(lid_rows):
        bw = lid_weights[r] * unit
        sag = crate_rng.uniform(-0.002, 0.002)
        board(bm, 2 * lid_half + crate_rng.uniform(-0.01, 0.01), bw, lid_t, Vector((x0, -lid_half, lid_z + sag)),
              Vector((0, 1, 0)), Vector((1, 0, 0)), Z, index, bevel=0.008)
        lid_centres.append(x0 + bw / 2)
        index += 1
        x0 += bw + (lid_gaps[r] if r < lid_rows - 1 else 0.0)
    cleat = 0.095
    for y0 in (-lid_half + 0.06, lid_half - 0.06 - cleat):
        board(bm, 2 * lid_half - 0.08, cleat, t, Vector((-lid_half + 0.04, y0, lid_z + lid_t)),
              Vector((1, 0, 0)), Vector((0, 1, 0)), Z, index, bevel=0.007)
        index += 1
        for x in lid_centres:
            nail(bm, Vector((x, y0 + cleat / 2, lid_z + lid_t + t)), Z, IRON)
    lid = [v for v in bm.verts if v not in before]
    turn = math.radians(2.5)
    cos_l, sin_l = math.cos(turn), math.sin(turn)
    for v in lid:
        x, y = v.co.x, v.co.y
        v.co.x = x * cos_l - y * sin_l + 0.012
        v.co.y = x * sin_l + y * cos_l - 0.01

    cos_y, sin_y = math.cos(yaw), math.sin(yaw)
    for v in bm.verts:
        x, y = v.co.x, v.co.y
        v.co.x = cx + x * cos_y - y * sin_y
        v.co.y = cy + x * sin_y + y * cos_y
        v.co.z += base
    emit(sc, "crate", bm, [M["wood"], M["interior"], M["rust"]])


def build_barrel(sc, M):
    bm = bmesh.new()
    cx, cy = 0.15, WALL_Y - 0.62
    height, r_end, r_mid = 1.3, 0.36, 0.48
    staves = 20
    gap = 0.004
    z0 = ground_height(cx, cy) - 0.01

    def radius(z):
        t = z / height
        return r_end + (r_mid - r_end) * math.sin(math.pi * t)

    for s in range(staves):
        a0 = 2 * math.pi * s / staves
        a1 = 2 * math.pi * (s + 1) / staves
        a0 += gap / r_mid
        a1 -= gap / r_mid
        rows = 12
        outer = []
        inner = []
        for k in range(rows + 1):
            z = height * k / rows
            r = radius(z)
            outer.append([bm.verts.new((cx + r * math.cos(a), cy + r * math.sin(a), z0 + z)) for a in (a0, (a0 + a1) / 2, a1)])
            ri = r - 0.032
            inner.append([bm.verts.new((cx + ri * math.cos(a), cy + ri * math.sin(a), z0 + z)) for a in (a0, (a0 + a1) / 2, a1)])
        faces = []
        for k in range(rows):
            for c in range(2):
                faces.append(bm.faces.new((outer[k][c], outer[k][c + 1], outer[k + 1][c + 1], outer[k + 1][c])))
                faces.append(bm.faces.new((inner[k][c + 1], inner[k][c], inner[k + 1][c], inner[k + 1][c + 1])))
            faces.append(bm.faces.new((outer[k][0], outer[k + 1][0], inner[k + 1][0], inner[k][0])))
            faces.append(bm.faces.new((inner[k][2], inner[k + 1][2], outer[k + 1][2], outer[k][2])))
        for c in range(2):
            faces.append(bm.faces.new((outer[-1][c], outer[-1][c + 1], inner[-1][c + 1], inner[-1][c])))
        bmesh.ops.recalc_face_normals(bm, faces=faces)
        uv = uv_layer(bm)
        for f in faces:
            f.material_index = 0
            f.smooth = True
            for loop in f.loops:
                co = loop.vert.co
                a = math.atan2(co.y - cy, co.x - cx)
                local = ((a - a0) % (2 * math.pi)) * r_mid
                loop[uv].uv = (co.z + s * 0.37, (s % 6) / 6.0 + 0.012 + local * 0.9 % (1 / 6.0 - 0.024))
    # The head, set well down inside the top so the staves' ends stand round it as a rim.
    head = lathe(bm, [(0.0, height - 0.07), (r_end - 0.025, height - 0.07)], 32, (cx, cy, z0), 0)
    for f in head:
        plank_uv(bm, [f], "x", 2, 0.0)
    # The hoops: broad, dark iron bands standing well out from the staves, following the
    # bulge, with their edges rounded over.
    for zc, width in ((height * 0.06, 0.07), (height * 0.27, 0.06), (height * 0.73, 0.06), (height * 0.94, 0.07)):
        za, zb = zc - width / 2, zc + width / 2
        ra, rb = radius(za) + 0.003, radius(zb) + 0.003
        band = [(ra, za), (ra + 0.01, za + 0.004), (rb + 0.01, zb - 0.004), (rb, zb)]
        hoop = lathe(bm, band, 48, (cx, cy, z0), 1)
        for f in hoop:
            f.smooth = True
    emit(sc, "barrel", bm, [M["wood"], M["iron"]])


# ---------------------------------------------------------------------------------------
# Ivy: vines up the wall with leaves along them

def build_ivy(sc, M):
    leaves = bmesh.new()
    vines = bmesh.new()

    def window_blocks(x, z):
        return WIN_X - WIN_W / 2 - 0.25 < x < WIN_X + WIN_W / 2 + 0.25 and SILL_Z - 0.3 < z < SPRING_Z + ARCH_RISE + 0.45

    def grow(x, z, heading, steps, depth, wall_y, pull=-math.pi / 2):
        """A stem that wanders over the wall, branching now and then. pull is the way it tends:
        down for a stem hanging from the top, up for one climbing from the ground."""
        pts = []
        for _ in range(steps):
            pts.append((x, wall_y - 0.025, z))
            heading += rng.uniform(-0.45, 0.45)
            heading += (pull - heading) * 0.08
            nx = x + math.cos(heading) * 0.07
            nz = z + math.sin(heading) * 0.07
            if window_blocks(nx, nz) or nx < WALL_X0 + 0.05:
                heading += math.pi * 0.5 * (1 if nx < WIN_X else -1)
                continue
            if nz < 0.5 or nz > WALL_TOP - 0.05:
                break
            x, z = nx, nz
            if depth < 2 and rng.random() < 0.035:
                branch = heading + rng.choice((-1, 1)) * rng.uniform(0.6, 1.2)
                grow(x, z, branch, int(steps * 0.5), depth + 1, wall_y, pull)
        if len(pts) > 2:
            tube(vines, pts, (0.016, 0.01, 0.007)[depth], 0, segments=6)
        for k, (px, py, pz) in enumerate(pts):
            # Big leaves along the stem, in strands with brick showing between them.
            count = 2 if depth == 0 else 1
            if k % 2 and depth > 0:
                continue
            for _ in range(count):
                size = rng.uniform(0.08, 0.15)
                angle = rng.uniform(0, 2 * math.pi)
                reach = rng.uniform(0.03, 0.12)
                leaf(leaves, (px + math.cos(angle) * reach, py - rng.uniform(0.02, 0.1), pz + math.sin(angle) * reach),
                     size, rng.uniform(0, 2 * math.pi))

    # The old plant: thick stems rooted in the ground between the barrel and the window, climbing
    # to the top of the wall and branching as they go.
    for sx, lean in ((1.05, 0.05), (0.75, 0.4)):
        grow(sx, 0.55, math.pi / 2 + lean, rng.randint(70, 90), 0, WALL_Y, pull=math.pi / 2 + lean * 0.5)
    # Hanging down from the top of the wall above them.
    for sx in (-1.6, -0.2):
        grow(sx + rng.uniform(-0.15, 0.15), WALL_TOP - 0.1, -math.pi / 2 + rng.uniform(-0.3, 0.3),
             rng.randint(30, 50), 1, WALL_Y)
    # One climbing up from where the low wall meets the building.
    grow(DRY_X + 0.25, 0.55, math.pi / 2, 50, 1, WALL_Y, pull=math.pi / 2)

    # Spilling over the top of the low wall and down its face, thickest where it meets the
    # building: leaves lying on top, and hanging ones on the courtyard face.
    top = 1.5
    for _ in range(320):
        y = rng.uniform(WALL_Y - 4.5, WALL_Y - 0.1)
        if rng.random() > ((y - (WALL_Y - 4.5)) / 4.4) ** 0.7:
            continue
        if rng.random() < 0.55:
            z = top + rng.uniform(-0.03, 0.12)
            x = DRY_X - rng.uniform(0.0, DRY_THICKNESS)
            leaf(leaves, (x, y, z), rng.uniform(0.08, 0.14), rng.uniform(0, 2 * math.pi), flat=True)
        else:
            # Hanging down the face, further where it's thickest.
            reach = 0.6 * ((y - (WALL_Y - 4.5)) / 4.4)
            z = top - rng.uniform(0.0, reach)
            leaf_x(leaves, (DRY_X + rng.uniform(0.02, 0.08), y, z), rng.uniform(0.08, 0.14), rng.uniform(0, 2 * math.pi))

    emit(sc, "ivy_vines", vines, [M["vine"]], smooth=True)
    emit(sc, "ivy_leaves", leaves, [M["ivy"]])


def leaf(bm, centre, size, angle, flat=False, facing_x=False):
    """A five-lobed ivy leaf. On the wall it faces out, -Y or with facing_x +X, and tips
    forward at the bottom; flat, it lies on top of something."""
    pts = []
    lobes = 5
    for k in range(lobes * 2):
        a = angle + math.pi * k / lobes
        r = size * (1.0 if k % 2 == 0 else 0.5) * (1.15 if k == 0 else 1.0)
        pts.append((math.cos(a) * r, math.sin(a) * r))
    tilt = rng.uniform(0.4, 1.1)
    centre_v = bm.verts.new(centre)
    ring = []
    for a, b in pts:
        if flat:
            ring.append(bm.verts.new((centre[0] + a, centre[1] + b, centre[2] + rng.uniform(-0.01, 0.01))))
        else:
            # The leaf's lower half leans out from the wall, so it catches the light.
            out = (size - b) * tilt * 0.45
            if facing_x:
                ring.append(bm.verts.new((centre[0] + out, centre[1] + a, centre[2] + b)))
            else:
                ring.append(bm.verts.new((centre[0] + a, centre[1] - out, centre[2] + b)))
    for k in range(len(ring)):
        f = bm.faces.new((centre_v, ring[(k + 1) % len(ring)], ring[k]))
        f.material_index = 0


def leaf_x(bm, centre, size, angle):
    """An ivy leaf on a wall that faces +X, such as the low wall's courtyard side."""
    leaf(bm, centre, size, angle, facing_x=True)


def build_plaster(sc, M):
    """What's left of an old coat of whitewash, in flakes high on the right of the wall.

    The coat is wherever a noise field is above a threshold. Marching squares trace that
    edge smoothly between the samples, and a narrow side runs from the edge back to the
    bricks, so each flake shows its thickness."""
    bm = bmesh.new()
    cx, cz = 8.8, 4.1
    cell = 0.025
    thick = 0.007
    seed = Vector((31.0, 7.0, 0.0))
    x0, x1, z0, z1 = PIER_X1 + 0.05, WALL_X1 - 0.05, cz - 1.6, WALL_TOP - 0.02
    nx, nz = int((x1 - x0) / cell), int((z1 - z0) / cell)

    def field(x, z):
        fall = 1.0 - math.hypot((x - cx) / 1.9, (z - cz) / 1.7)
        n = noise.fractal(Vector((x * 2.2, z * 2.2, 0)) + seed, 0.8, 2.2, 5)
        # Fine noise breaks the outline into chips.
        n += 0.12 * noise.noise(Vector((x * 14, z * 14, 3)))
        return fall * 0.9 + n * 0.7 - 0.25

    values = [[field(x0 + i * cell, z0 + j * cell) for i in range(nx + 1)] for j in range(nz + 1)]
    front_y = WALL_Y - thick
    corner = {}
    crossing = {}

    def surface(x, z):
        return bm.verts.new((x, front_y - 0.0015 * noise.noise(Vector((x * 7, z * 7, 2))), z))

    def corner_vert(i, j):
        if (i, j) not in corner:
            corner[(i, j)] = surface(x0 + i * cell, z0 + j * cell)
        return corner[(i, j)]

    def edge_vert(a, b):
        key = (min(a, b), max(a, b))
        if key not in crossing:
            va, vb = values[a[1]][a[0]], values[b[1]][b[0]]
            t = va / (va - vb)
            x = x0 + (a[0] + (b[0] - a[0]) * t) * cell
            z = z0 + (a[1] + (b[1] - a[1]) * t) * cell
            crossing[key] = surface(x, z)
        return crossing[key]

    for j in range(nz):
        for i in range(nx):
            # The square's corners, anticlockwise seen from the front (-Y), so the coat faces out.
            square = [(i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1)]
            inside = [values[q[1]][q[0]] > 0 for q in square]
            if not any(inside):
                continue
            # Walk round the square, keeping inside corners and the points where the edge
            # crosses a side. A saddle comes out as one polygon, which is fine at this size.
            poly = []
            for k in range(4):
                a, b = square[k], square[(k + 1) % 4]
                if inside[k]:
                    poly.append(corner_vert(*a))
                if inside[k] != inside[(k + 1) % 4]:
                    poly.append(edge_vert(a, b))
            if len(poly) >= 3:
                bm.faces.new(poly)
    # The flakes' sides: from each open edge of the coat back to the wall.
    for e in [e for e in bm.edges if len(e.link_faces) == 1]:
        a, b = e.verts
        a2 = bm.verts.new((a.co.x, WALL_Y + 0.001, a.co.z))
        b2 = bm.verts.new((b.co.x, WALL_Y + 0.001, b.co.z))
        f = e.link_faces[0]
        # Wind the side the same way round as the face it hangs from, so it faces outwards.
        loop = next(lp for lp in f.loops if lp.edge == e)
        if loop.vert == a:
            bm.faces.new((b, a, a2, b2))
        else:
            bm.faces.new((a, b, b2, a2))
    bm.normal_update()
    box_uv(bm, bm.faces[:])
    emit(sc, "plaster", bm, [M["plaster"]])


def dead_leaf(bm, centre, length, angle, mat):
    """A fallen leaf: a pointed oval with a midrib, curled up a little at the edges and tip."""
    c, s = math.cos(angle), math.sin(angle)
    curl = rng.uniform(0.15, 0.4) * length

    def at(u, v):
        # u along the leaf from stalk (0) to tip (1), v across it from -1 to 1.
        z = curl * (v * v * 0.6 + max(0.0, u - 0.6) * 0.8)
        lx, ly = (u - 0.5) * length, v * length * 0.32 * math.sin(math.pi * min(u, 0.999) ** 0.8)
        return bm.verts.new((centre[0] + lx * c - ly * s, centre[1] + lx * s + ly * c, centre[2] + z))

    steps = 6
    rib = [at(k / steps, 0.0) for k in range(steps + 1)]
    left = [at(k / steps, -1.0) for k in range(steps + 1)]
    right = [at(k / steps, 1.0) for k in range(steps + 1)]
    for k in range(steps):
        for side in (left, right):
            quad = (rib[k], side[k], side[k + 1], rib[k + 1]) if side is right else (rib[k], rib[k + 1], side[k + 1], side[k])
            f = bm.faces.new(quad)
            f.material_index = mat
            f.smooth = True
    # The stalk.
    stalk = bm.verts.new((centre[0] - (0.5 + 0.18) * length * c, centre[1] - (0.5 + 0.18) * length * s, centre[2]))
    f = bm.faces.new((stalk, rib[0], left[1]))
    f.material_index = mat


def build_fallen_leaves(sc, M):
    """Brown and orange leaves, blown into the corners: along the foot of the wall, by the deck
    and among the flagstones, and only a few out in the open mud."""
    bm = bmesh.new()
    placed = 0
    while placed < 200:
        roll = rng.random()
        if roll < 0.35:
            x, y = rng.uniform(DRY_X + 0.2, 11), WALL_Y - rng.uniform(0.3, 1.0)
        elif roll < 0.6:
            x, y = rng.uniform(DECK[0] - 0.8, DECK[1] + 0.3), rng.uniform(-3.0, DECK[2] + 0.4)
        else:
            x, y = rng.uniform(-3, 11), rng.uniform(-6, WALL_Y - 0.3)
        if puddle_depth(x, y) > 0.2:
            continue
        on_path = path_distance(x, y) < PATH_HALF_WIDTH - 0.05
        on_deck = DECK[0] < x < DECK[1] and DECK[2] < y < DECK[3]
        z = DECK_TOP + 0.004 if on_deck else ground_height(x, y) + (0.055 if on_path else 0.018)
        dead_leaf(bm, (x, y, z), rng.uniform(0.12, 0.2), rng.uniform(0, 2 * math.pi), rng.randrange(2))
        placed += 1
    emit(sc, "fallen_leaves", bm, [M["dead_leaf"], M["dead_leaf_orange"]])


def blade(bm, root, height, lean, angle, width, mat):
    """One grass blade: a narrow strip that tapers to a point and arches over."""
    c, s = math.cos(angle), math.sin(angle)
    side = Vector((-s, c, 0.0)) * width
    pts = []
    segments = 4
    for k in range(segments + 1):
        t = k / segments
        out = lean * t * t
        p = Vector(root) + Vector((c * out, s * out, height * t * (1 - 0.25 * t * lean / max(height, 0.01))))
        pts.append(p)
    left = [bm.verts.new(p - side * (1 - k / segments)) for k, p in enumerate(pts[:-1])]
    right = [bm.verts.new(p + side * (1 - k / segments)) for k, p in enumerate(pts[:-1])]
    tip = bm.verts.new(pts[-1])
    for k in range(segments - 1):
        f = bm.faces.new((left[k], right[k], right[k + 1], left[k + 1]))
        f.material_index = mat
    f = bm.faces.new((left[-1], right[-1], tip))
    f.material_index = mat


def tuft(bm, x, y, z, size):
    """A clump of blades from one spot, splaying out, a few of them dry."""
    for _ in range(int(24 + 36 * size)):
        a = rng.uniform(0, 2 * math.pi)
        r = rng.uniform(0, 0.07 * size)
        height = rng.uniform(0.16, 0.42) * size
        blade(bm, (x + math.cos(a) * r, y + math.sin(a) * r, z - 0.01), height, rng.uniform(0.03, 0.16) * size,
              a + rng.uniform(-0.4, 0.4), rng.uniform(0.006, 0.01), 1 if rng.random() < 0.25 else 0)


def build_grass(sc, M):
    """Tufts where feet and wheels don't go: the foot of both walls, the edges of the flagstones
    and the deck."""
    bm = bmesh.new()
    spots = []
    # Along the foot of the plinth and the low wall.
    for _ in range(40):
        spots.append((rng.uniform(DRY_X + 0.2, WALL_X1 - 0.5), WALL_Y - rng.uniform(0.32, 0.55), rng.uniform(0.7, 1.4)))
    for _ in range(26):
        spots.append((DRY_X + rng.uniform(0.15, 0.4), rng.uniform(WALL_Y - DRY_LENGTH, WALL_Y - 0.3), rng.uniform(0.8, 1.6)))
    # Along the flagstones' edges.
    for _ in range(55):
        path = rng.choice(PATHS)
        t = rng.random()
        k = min(int(t * (len(path) - 1)), len(path) - 2)
        f = t * (len(path) - 1) - k
        (ax, ay), (bx, by) = path[k], path[k + 1]
        dx, dy = bx - ax, by - ay
        length = math.hypot(dx, dy)
        side = rng.choice((-1, 1)) * (PATH_HALF_WIDTH + rng.uniform(0.0, 0.25))
        x, y = ax + dx * f - dy / length * side, ay + dy * f + dx / length * side
        # Not out in the middle of the other arm.
        if path_distance(x, y) > PATH_HALF_WIDTH - 0.1:
            spots.append((x, y, rng.uniform(0.6, 1.2)))
    # Round the deck.
    for _ in range(20):
        if rng.random() < 0.5:
            x, y = DECK[0] - rng.uniform(0.05, 0.3), rng.uniform(DECK[2], DECK[3])
        else:
            x, y = rng.uniform(DECK[0], DECK[1]), DECK[2] - rng.uniform(0.05, 0.3)
        spots.append((x, y, rng.uniform(0.7, 1.3)))
    for x, y, size in spots:
        if puddle_depth(x, y) > 0.05 or rut_depth(x, y) > 0.1:
            continue
        tuft(bm, x, y, ground_height(x, y), size)
    emit(sc, "grass", bm, [M["grass"], M["grass_dry"]])


# ---------------------------------------------------------------------------------------

def build():
    rng.seed(7)
    sc = fresh_scene()
    M = make_materials()
    # Footprints first: the ground, the stones on it and the grass all ask how high it is.
    FOOTPRINTS[:] = walk([(1.0, -1.7), (3.0, -0.6), (5.0, 0.0), (6.0, 0.6)], 0.34)
    FOOTPRINTS.extend(walk([(2.2, -3.6), (3.8, -2.0), (5.6, -1.3), (7.2, -0.4)], 0.34))
    build_wall(sc, M)
    build_plinth(sc, M)
    build_lamp(sc, M)
    build_dry_wall(sc, M)
    build_ground(sc, M)
    build_flagstones(sc, M)
    build_pebbles(sc, M)
    build_deck(sc, M)
    build_crate(sc, M)
    build_barrel(sc, M)
    build_ivy(sc, M)
    build_plaster(sc, M)
    build_fallen_leaves(sc, M)
    build_grass(sc, M)
    return sc


def export(sc):
    window = bpy.context.window
    previous = window.scene
    window.scene = sc
    try:
        bpy.ops.export_scene.gltf(
            filepath=OUT,
            export_format="GLB",
            use_active_scene=True,
            export_apply=True,
            export_yup=True,
            export_texcoords=True,
            export_normals=True,
            export_materials="EXPORT",
            export_cameras=False,
            export_lights=False,
        )
    finally:
        window.scene = previous


if __name__ == "__main__":
    built = build()
    export(built)
    print("objects:", len(built.objects), "triangles:", sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in built.objects))

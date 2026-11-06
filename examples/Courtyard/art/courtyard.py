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
from mathutils import Vector, noise

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
WIN_X = 2.2
WIN_W = 1.8
SILL_Z = 1.2
SPRING_Z = 3.15
ARCH_RISE = 0.28
REVEAL = 0.24

# The low dry-stone wall runs straight out from the building, square to it, towards the camera.
# Its courtyard face is at DRY_X, and it reaches DRY_LENGTH out from the plinth.
DRY_X = -1.6
DRY_THICKNESS = 0.65
DRY_LENGTH = 7.0

PUDDLE = (2.5, -0.6, 1.5, 0.8)  # centre x, y and radii

PATH = [(3.5, -9.0), (2.6, -5.5), (1.6, -2.8), (0.6, -0.6), (0.1, 1.2), (0.8, 2.4), (2.2, 2.9), (3.2, 3.1)]


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
        "cobble": material("graph_cobble", (0.4, 0.4, 0.38)),
        "dirt": material("graph_dirt", (0.25, 0.17, 0.11)),
        "wood": material("graph_wood", (0.5, 0.34, 0.2)),
        "rust": material("graph_rust", (0.35, 0.2, 0.12)),
        "mortar": material("mortar", (0.32, 0.3, 0.27), roughness=0.95),
        "arch1": material("arch_brick_a", (0.42, 0.13, 0.07), roughness=0.85),
        "arch2": material("arch_brick_b", (0.5, 0.17, 0.09), roughness=0.85),
        "arch3": material("arch_brick_c", (0.36, 0.12, 0.08), roughness=0.85),
        "iron": material("iron", (0.05, 0.048, 0.045), metallic=0.8, roughness=0.45),
        "frame": material("window_frame", (0.04, 0.035, 0.03), metallic=0.6, roughness=0.5),
        "glass": material("glass", (0.02, 0.022, 0.025), roughness=0.04),
        "interior": material("interior", (0.015, 0.013, 0.012), roughness=1.0),
        "shade": material("shade_inner", (0.8, 0.75, 0.65), roughness=0.5),
        "bulb": material("bulb", (1.0, 0.85, 0.6), emission=(1.0, 0.75, 0.45), strength=12.0),
        "water": material("water", (0.03, 0.025, 0.02), roughness=0.02),
        "ivy": material("ivy", (0.12, 0.2, 0.06), roughness=0.7),
        "vine": material("vine", (0.12, 0.09, 0.06), roughness=0.9),
        "plaster": material("plaster", (0.72, 0.7, 0.66), roughness=0.95),
        "dead_leaf": material("dead_leaf", (0.55, 0.32, 0.08), roughness=0.8),
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


def rock(bm, center, dims, mat, power=3.0, bumps=0.12, yaw=0.0, segments=(14, 9), uv_offset=True):
    """A rounded stone: a superellipsoid, scaled, roughened with noise and turned about Z."""
    result = bmesh.ops.create_uvsphere(bm, u_segments=segments[0], v_segments=segments[1], radius=1.0)
    verts = result["verts"]
    seed = Vector((rng.uniform(-100, 100), rng.uniform(-100, 100), rng.uniform(-100, 100)))
    cos_y, sin_y = math.cos(yaw), math.sin(yaw)
    e = 2.0 / power
    for v in verts:
        d = v.co.normalized()
        p = Vector(tuple(math.copysign(abs(c) ** e, c) for c in d))
        n = noise.fractal(d * 1.6 + seed, 0.6, 2.0, 3)
        scale = 1.0 + bumps * n
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

    # The arch: a ring of bricks on end, standing 2 cm proud of the wall.
    bm = bmesh.new()
    pts, (cx, cz), radius, a0, a1 = arch_points()
    count = 15
    depth = 0.3
    gap = 0.012
    span = (a0 - a1)
    for i in range(count):
        b0 = a1 + span * (i / count) + gap / radius / 2
        b1 = a1 + span * ((i + 1) / count) - gap / radius / 2
        r0, r1 = radius, radius + depth
        jitter = rng.uniform(-0.006, 0.006)
        outline = []
        for r, a in ((r0, b0), (r1 + jitter, b0), (r1 + jitter, b1), (r0, b1)):
            outline.append((cx + r * math.cos(a), cz + r * math.sin(a)))
        faces = prism(bm, outline, WALL_Y - 0.02 - rng.uniform(0, 0.008), WALL_Y + 0.05, 0)
        for f in faces:
            f.material_index = rng.randrange(3)
    # Mortar behind the arch bricks, so the gaps between them aren't holes.
    ring = []
    for i in range(25):
        a = a1 + span * i / 24
        ring.append((cx + (radius + 0.003) * math.cos(a), cz + (radius + 0.003) * math.sin(a)))
    for i in reversed(range(25)):
        a = a1 + span * i / 24
        ring.append((cx + (radius + depth - 0.01) * math.cos(a), cz + (radius + depth - 0.01) * math.sin(a)))
    for f in prism(bm, ring, WALL_Y - 0.008, WALL_Y + 0.01, 3):
        pass
    emit(sc, "arch", bm, [M["arch1"], M["arch2"], M["arch3"], M["mortar"]])

    # The stone sill, wider than the opening and standing out from the wall.
    bm = bmesh.new()
    sill = box(bm, (x0 - 0.16, WALL_Y - 0.09, SILL_Z - 0.13), (x1 + 0.16, WALL_Y + REVEAL, SILL_Z + 0.02), 0, bevel=0.01)
    box_uv(bm, bm.faces[:], (0.3, 0.7, 0.1))
    emit(sc, "sill", bm, [M["stone"]])

    build_window(sc, M, x0, x1)


def build_window(sc, M, x0, x1):
    bm = bmesh.new()
    y = WALL_Y + REVEAL
    t = 0.05     # frame depth
    fw = 0.055   # outer frame width
    bar = 0.028  # glazing bars
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
    cols = 4
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

    # The glass, and a dark room behind it.
    bm = bmesh.new()
    prism(bm, [(x0 + 0.01, SILL_Z + 0.03)] + [(x, z - 0.01) for x, z in arch[1:-1]] + [(x1 - 0.01, SILL_Z + 0.03)], y - t * 0.5, y - t * 0.45, 0)
    emit(sc, "glass", bm, [M["glass"]])
    bm = bmesh.new()
    box(bm, (x0 - 0.3, y + 0.05, 0.4), (x1 + 0.3, y + 3.5, SPRING_Z + 1.2), 0)
    bmesh.ops.reverse_faces(bm, faces=bm.faces[:])
    emit(sc, "interior", bm, [M["interior"]])


def build_plinth(sc, M):
    """A course of big rough stones along the foot of the brick wall."""
    bm = bmesh.new()
    x = WALL_X0 - 0.05
    while x < WALL_X1:
        length = rng.uniform(0.45, 0.8)
        height = rng.uniform(0.48, 0.56)
        cx = x + length / 2
        rock(bm, (cx, WALL_Y + 0.05, height / 2 - 0.04), (length - 0.02, 0.3, height), 0,
             power=6.0, bumps=0.05, yaw=rng.uniform(-0.02, 0.02), segments=(18, 12))
        x += length
    # The corner: stones down the building's side too.
    y = WALL_Y
    while y < WALL_Y + 4.0:
        length = rng.uniform(0.45, 0.8)
        height = rng.uniform(0.48, 0.56)
        rock(bm, (WALL_X0 - 0.03, y + length / 2, height / 2 - 0.04), (0.3, length - 0.02, height), 0,
             power=6.0, bumps=0.05, segments=(18, 12))
        y += length
    emit(sc, "plinth", bm, [M["stone"]])


def build_lamp(sc, M):
    bm = bmesh.new()
    lx, lz = 5.4, 4.05  # where the arm meets the wall
    y = WALL_Y
    # The wall plate: a disc on the wall, facing -Y.
    plate = lathe(bm, [(0.0, 0.0), (0.1, 0.0), (0.1, 0.012), (0.085, 0.03), (0.0, 0.03)], 24, (0, 0, 0), 0)
    for v in faces_verts(plate):
        v.co = Vector((lx + v.co.x, y - v.co.z, lz + v.co.y))
    # The gooseneck: straight out from the wall, then a quarter turn down to the shade.
    reach, drop, bend = 0.5, 0.32, 0.2
    arm = [(lx, y - 0.03 - (reach - bend) * i / 6, lz) for i in range(7)]
    for i in range(1, 13):
        a = math.pi * 0.5 * i / 12
        arm.append((lx, y - 0.03 - (reach - bend) - bend * math.sin(a), lz - bend * (1 - math.cos(a))))
    arm.append((lx, y - 0.03 - reach, lz - drop))
    tube(bm, arm, 0.022, 0)
    # The shade: a wide, shallow cone, dark outside and pale inside.
    sx, sy, sz = lx, y - 0.03 - reach, lz - drop - 0.04
    outer = [(0.04, 0.05), (0.07, 0.04), (0.12, 0.0), (0.24, -0.08), (0.29, -0.13)]
    inner = [(0.282, -0.126), (0.235, -0.075), (0.115, 0.005), (0.06, 0.03), (0.0, 0.035)]
    lathe(bm, outer, 40, (sx, sy, sz), 0, close_top=True)
    lathe(bm, inner, 40, (sx, sy, sz), 1)
    lathe(bm, [(0.29, -0.13), (0.282, -0.126)], 40, (sx, sy, sz), 0)
    emit(sc, "lamp", bm, [M["iron"], M["shade"]], smooth=True)

    bm = bmesh.new()
    bulb = bmesh.ops.create_uvsphere(bm, u_segments=16, v_segments=10, radius=0.055)
    for v in bulb["verts"]:
        v.co += Vector((sx, sy, sz - 0.04))
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
    while z < 1.45:
        h = rng.uniform(0.2, 0.3)
        x = x_start + (0.15 if course % 2 else 0.0)
        while x < x_end:
            length = rng.uniform(0.28, 0.75)
            hh = h * rng.uniform(0.85, 1.05)
            cx = min(x + length / 2, x_end - length / 2 + 0.05)
            proud = rng.uniform(-0.03, 0.03)
            rock(bm, (cx, y0 + proud + 0.04, z + hh / 2), (length * 1.02, 0.34, hh * 1.04), 0,
                 power=2.3, bumps=0.24, yaw=rng.uniform(-0.12, 0.12))
            # A second stone behind, so the wall looks the same from the far side.
            rock(bm, (cx + 0.1, y0 + thickness - 0.18, z + hh / 2), (length, 0.34, hh), 0,
                 power=2.3, bumps=0.24, yaw=rng.uniform(-0.12, 0.12))
            x += length
        z += h * 0.92
        course += 1
    # Cope stones on edge along the top.
    x = x_start
    while x < x_end:
        w = rng.uniform(0.14, 0.24)
        rock(bm, (x + w / 2, y0 + thickness / 2, z + 0.12), (w, thickness * 0.9, 0.3), 0,
             power=2.4, bumps=0.15, yaw=rng.uniform(-0.15, 0.15), segments=(12, 8))
        x += w * 0.95
    # The hearting: a dark core, so the gaps between stones are shadow and not sky.
    box(bm, (x_start, y0 + 0.05, 0.0), (x_end, y0 + thickness - 0.1, z + 0.05), 1)
    # A quarter turn: along the wall becomes -Y from the plinth's face, and the good face becomes +X.
    for v in bm.verts:
        x, y = v.co.x, v.co.y
        v.co.x = DRY_X - (y - y0)
        v.co.y = WALL_Y - 0.1 + (x - x_end)
    emit(sc, "dry_wall", bm, [M["stone"], M["mortar"]])


# ---------------------------------------------------------------------------------------
# The ground: mud with a puddle, cobbles, pebbles and a deck

def path_distance(x, y):
    best = 1e9
    for (ax, ay), (bx, by) in zip(PATH, PATH[1:]):
        abx, aby = bx - ax, by - ay
        t = max(0.0, min(1.0, ((x - ax) * abx + (y - ay) * aby) / (abx * abx + aby * aby)))
        px, py = ax + abx * t, ay + aby * t
        best = min(best, math.hypot(x - px, y - py))
    return best


PATH_HALF_WIDTH = 0.85


def puddle_depth(x, y):
    cx, cy, rx, ry = PUDDLE
    d = ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 + 0.35 * noise.noise(Vector((x * 1.3, y * 1.3, 5.0)))
    return smoothstep(1.15, 0.35, d)


def ground_height(x, y):
    h = 0.03 * noise.fractal(Vector((x * 0.6, y * 0.6, 0.0)), 0.7, 2.0, 4)
    h += 0.012 * noise.noise(Vector((x * 5.0, y * 5.0, 1.0)))
    # Ruts and footprints: short dents.
    h -= 0.015 * max(0.0, noise.noise(Vector((x * 2.5, y * 2.5, 9.0))))
    h -= 0.07 * puddle_depth(x, y)
    # Under the cobbles the ground sits low, so only their tops show.
    h -= 0.02 * smoothstep(PATH_HALF_WIDTH + 0.3, PATH_HALF_WIDTH - 0.1, path_distance(x, y))
    # Behind the low wall it's a little higher and drier.
    h += 0.08 * smoothstep(WALL_Y + 0.6, WALL_Y + 1.5, y)
    # Pushed up against the foot of the walls.
    h += 0.03 * smoothstep(WALL_Y - 0.5, WALL_Y, y) * (1.0 if x > WALL_X0 - 0.2 else 0.5)
    return h


def build_ground(sc, M):
    bm = bmesh.new()
    x0, x1, y0, y1 = -16.0, 14.0, -12.0, 18.0
    step = 0.12
    nx, ny = int((x1 - x0) / step), int((y1 - y0) / step)
    grid = []
    for j in range(ny + 1):
        y = y0 + j * step
        row = []
        for i in range(nx + 1):
            x = x0 + i * step
            # Coarser far away: only the middle needs the detail, but a flat grid is simplest.
            row.append(bm.verts.new((x, y, ground_height(x, y))))
        grid.append(row)
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
    ring = [bm.verts.new((cx + rx * 1.4 * math.cos(a), cy + ry * 1.5 * math.sin(a), -0.03))
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


def pillow(bm, poly, centre, base_z, top, mat):
    """A cobble: the outline at the bottom, rounded up to a domed top."""
    rings = []
    seed = Vector((rng.uniform(-50, 50), rng.uniform(-50, 50), 0))
    tilt = (rng.uniform(-0.02, 0.02), rng.uniform(-0.02, 0.02))
    for shrink, z in ((1.0, base_z - 0.06), (1.0, base_z + top * 0.55), (0.93, base_z + top * 0.85), (0.75, base_z + top)):
        ring = []
        for x, y in poly:
            px = centre[0] + (x - centre[0]) * shrink
            py = centre[1] + (y - centre[1]) * shrink
            bump = 0.006 * noise.noise(Vector((px * 9, py * 9, 0)) + seed)
            ring.append(bm.verts.new((px, py, z + bump + (px - centre[0]) * tilt[0] + (py - centre[1]) * tilt[1])))
        rings.append(ring)
    faces = []
    n = len(poly)
    for r in range(len(rings) - 1):
        for k in range(n):
            k1 = (k + 1) % n
            faces.append(bm.faces.new((rings[r][k], rings[r][k1], rings[r + 1][k1], rings[r + 1][k])))
    apex = bm.verts.new((centre[0], centre[1], base_z + top + 0.008))
    for k in range(n):
        faces.append(bm.faces.new((rings[-1][k], rings[-1][(k + 1) % n], apex)))
    for f in faces:
        f.material_index = mat
        f.smooth = True
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    box_uv(bm, faces, (rng.uniform(0, 2), rng.uniform(0, 2), 0))


def build_cobbles(sc, M):
    bm = bmesh.new()
    points = []
    spacing = 0.42
    for j in range(-40, 40):
        for i in range(-40, 40):
            x = i * spacing + (spacing / 2 if j % 2 else 0) + rng.uniform(-0.1, 0.1) - 1.0
            y = j * spacing * 0.87 + rng.uniform(-0.1, 0.1) - 3.0
            edge = PATH_HALF_WIDTH + 0.12 * noise.noise(Vector((x * 1.7, y * 1.7, 3.0)))
            if path_distance(x, y) < edge and y < WALL_Y - 0.35 and puddle_depth(x, y) < 0.55:
                points.append((x, y))
    for p in points:
        near = [q for q in points if q is not p and abs(q[0] - p[0]) < 0.9 and abs(q[1] - p[1]) < 0.9]
        cell = voronoi_cell(p, near, rng.uniform(0.26, 0.32), rng.uniform(0.035, 0.06))
        if cell is None:
            continue
        cell = chaikin(cell, 2)
        cx = sum(c[0] for c in cell) / len(cell)
        cy = sum(c[1] for c in cell) / len(cell)
        base = ground_height(cx, cy)
        pillow(bm, cell, (cx, cy), max(base, -0.03), rng.uniform(0.04, 0.06), 0)
    emit(sc, "cobbles", bm, [M["cobble"]])


def build_pebbles(sc, M):
    bm = bmesh.new()
    spots = []
    for _ in range(70):
        # Along the foot of the walls and the edges of the path.
        if rng.random() < 0.5:
            x, y = rng.uniform(DRY_X + 0.15, 10.5), WALL_Y - rng.uniform(0.25, 0.9)
        else:
            t = rng.random()
            k = min(int(t * (len(PATH) - 1)), len(PATH) - 2)
            f = t * (len(PATH) - 1) - k
            (ax, ay), (bx, by) = PATH[k], PATH[k + 1]
            side = rng.choice((-1, 1)) * rng.uniform(0.9, 1.4)
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


def build_deck(sc, M):
    bm = bmesh.new()
    x0, x1 = 7.6, 12.0
    y0 = -0.4
    board, gap = 0.15, 0.012
    count = 22
    for i in range(count):
        y = y0 + i * (board + gap)
        if y + board > WALL_Y - 0.25:
            break
        lift = rng.uniform(-0.004, 0.006)
        faces = box(bm, (x0 + rng.uniform(-0.03, 0.03), y, 0.06 + lift), (x1, y + board, 0.095 + lift), 0, bevel=0.006)
        plank_uv(bm, faces, "x", i, rng.uniform(0, 3))
    # Joists under the boards.
    for x in (x0 + 0.2, x0 + 1.6, x0 + 3.0):
        faces = box(bm, (x, y0 - 0.05, -0.04), (x + 0.08, WALL_Y - 0.25, 0.06), 0)
        plank_uv(bm, faces, "y", 3, 0.0)
    emit(sc, "deck", bm, [M["wood"]])


def build_crate(sc, M):
    bm = bmesh.new()
    cx, cy, size = 8.75, 3.25, 0.9
    yaw = math.radians(-6)
    lo = Vector((-size / 2, -size / 2, 0.095))
    hi = Vector((size / 2, size / 2, 0.095 + size))
    post = 0.075
    slat, gap = 0.19, 0.022
    plank = 0
    # Corner posts.
    for sx in (-1, 1):
        for sy in (-1, 1):
            px = lo.x if sx < 0 else hi.x - post
            py = lo.y if sy < 0 else hi.y - post
            faces = box(bm, (px, py, lo.z), (px + post, py + post, hi.z), 0, bevel=0.006)
            plank_uv(bm, faces, "z", plank, rng.uniform(0, 3))
            plank += 1
    # Slats on four sides, set just outside the posts' faces.
    z = lo.z + 0.02
    while z + slat <= hi.z + 0.001:
        for side in range(4):
            t = 0.022
            if side == 0:
                a, b = (lo.x + post, lo.y - t, z), (hi.x - post, lo.y + 0.004, z + slat)
                axis = "x"
            elif side == 1:
                a, b = (lo.x + post, hi.y - 0.004, z), (hi.x - post, hi.y + t, z + slat)
                axis = "x"
            elif side == 2:
                a, b = (lo.x - t, lo.y + post, z), (lo.x + 0.004, hi.y - post, z + slat)
                axis = "y"
            else:
                a, b = (hi.x - 0.004, lo.y + post, z), (hi.x + t, hi.y - post, z + slat)
                axis = "y"
            faces = box(bm, a, b, 0, bevel=0.004)
            plank_uv(bm, faces, axis, plank, rng.uniform(0, 3))
            plank += 1
        z += slat + gap
    # The lid: boards across the top.
    y = lo.y
    while y < hi.y - 0.01:
        w = min(0.18, hi.y - y)
        faces = box(bm, (lo.x - 0.01, y, hi.z), (hi.x + 0.01, y + w - 0.008, hi.z + 0.022), 0, bevel=0.004)
        plank_uv(bm, faces, "x", plank, rng.uniform(0, 3))
        plank += 1
        y += w
    # A dark inside, so the gaps show shadow.
    box(bm, (lo.x + 0.01, lo.y + 0.01, lo.z), (hi.x - 0.01, hi.y - 0.01, hi.z - 0.01), 1)
    # Iron corner brackets.
    for sx in (-1, 1):
        for sy in (-1, 1):
            for z0 in (lo.z + 0.03, hi.z - 0.13):
                px = lo.x - 0.026 if sx < 0 else hi.x - 0.1
                py = lo.y - 0.026 if sy < 0 else hi.y - 0.1
                box(bm, (px, py, z0), (px + 0.126, py + 0.126, z0 + 0.1), 2)
    cos_y, sin_y = math.cos(yaw), math.sin(yaw)
    for v in bm.verts:
        x, y = v.co.x, v.co.y
        v.co.x = cx + x * cos_y - y * sin_y
        v.co.y = cy + x * sin_y + y * cos_y
    emit(sc, "crate", bm, [M["wood"], M["interior"], M["iron"]])


def build_barrel(sc, M):
    bm = bmesh.new()
    cx, cy = -0.2, WALL_Y - 0.45
    height, r_end, r_mid = 0.95, 0.27, 0.33
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
            ri = r - 0.022
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
    # The head, set a little down inside the top.
    head = lathe(bm, [(0.0, height - 0.045), (r_end - 0.015, height - 0.045)], 32, (cx, cy, z0), 0)
    for f in head:
        plank_uv(bm, [f], "x", 2, 0.0)
    # The hoops: thin bands of rusty iron standing out from the staves.
    for zc in (0.08, 0.27, height - 0.27, height - 0.08):
        r0 = radius(zc) + 0.002
        band = [(r0, zc - 0.022), (r0 + 0.006, zc - 0.02), (r0 + 0.006, zc + 0.02), (r0, zc + 0.022)]
        lathe(bm, band, 48, (cx, cy, z0), 1)
    emit(sc, "barrel", bm, [M["wood"], M["rust"]])


def build_spheres(sc, M):
    def sphere(name, centre, r, m):
        bm = bmesh.new()
        result = bmesh.ops.create_uvsphere(bm, u_segments=48, v_segments=24, radius=r)
        uv = uv_layer(bm)
        for v in result["verts"]:
            v.co += Vector(centre)
        for f in bm.faces:
            f.smooth = True
            us = []
            for loop in f.loops:
                d = (loop.vert.co - Vector(centre)) / r
                us.append(math.atan2(d.y, d.x))
            # Keep a face from wrapping right round where the angle jumps.
            if max(us) - min(us) > math.pi:
                us = [u + 2 * math.pi if u < 0 else u for u in us]
            for loop, u in zip(f.loops, us):
                d = (loop.vert.co - Vector(centre)) / r
                loop[uv].uv = (u * r, math.asin(max(-1.0, min(1.0, d.z))) * r)
        emit(sc, name, bm, [m])

    sphere("ball_bricks", (DRY_X + 0.5, WALL_Y - 1.0, 0.36), 0.38, M["bricks"])
    sphere("ball_iron", (6.95, WALL_Y - 0.45, 0.47), 0.48, M["rust"])
    sphere("ball_rust", (6.2, WALL_Y - 1.35, 0.35), 0.36, M["rust"])


# ---------------------------------------------------------------------------------------
# Ivy: vines up the wall with leaves along them

def build_ivy(sc, M):
    leaves = bmesh.new()
    vines = bmesh.new()

    def window_blocks(x, z):
        return WIN_X - WIN_W / 2 - 0.25 < x < WIN_X + WIN_W / 2 + 0.25 and SILL_Z - 0.3 < z < SPRING_Z + ARCH_RISE + 0.45

    def grow(x, z, heading, steps, depth, wall_y):
        """A stem that wanders over the wall, mostly down, branching now and then."""
        pts = []
        for _ in range(steps):
            pts.append((x, wall_y - 0.025, z))
            heading += rng.uniform(-0.45, 0.45)
            # Gravity: pull the heading towards straight down.
            heading += (-math.pi / 2 - heading) * 0.08
            nx = x + math.cos(heading) * 0.07
            nz = z + math.sin(heading) * 0.07
            if window_blocks(nx, nz) or nx < WALL_X0 + 0.05:
                heading += math.pi * 0.5 * (1 if nx < WIN_X else -1)
                continue
            if nz < 0.6:
                break
            x, z = nx, nz
            if depth < 1 and rng.random() < 0.04:
                grow(x, z, heading + rng.choice((-1, 1)) * rng.uniform(0.5, 1.1), int(steps * 0.5), depth + 1, wall_y)
        if len(pts) > 2:
            tube(vines, pts, 0.009 if depth == 0 else 0.006, 0, segments=5)
        for px, py, pz in pts:
            # Leaves crowd round the stem, thicker near the top where the plant is older.
            count = 3 if pz > WALL_TOP - 2.0 else 2
            for _ in range(count):
                size = rng.uniform(0.055, 0.1)
                angle = rng.uniform(0, 2 * math.pi)
                reach = rng.uniform(0.03, 0.15)
                leaf(leaves, (px + math.cos(angle) * reach, py - rng.uniform(0.02, 0.09), pz + math.sin(angle) * reach),
                     size, rng.uniform(0, 2 * math.pi))

    # Hanging from the top of the wall, mostly on its left half, and one climbing the corner.
    for sx in (-1.4, -0.6, 0.4, 1.1):
        grow(sx + rng.uniform(-0.15, 0.15), WALL_TOP + 0.05, -math.pi / 2 + rng.uniform(-0.3, 0.3),
             rng.randint(40, 80), 0, WALL_Y)
    # One climbing up from where the low wall meets the building.
    grow(DRY_X + 0.2, 0.6, math.pi / 2, 60, 1, WALL_Y)

    # Spilling over the top of the low wall, thickest where it meets the building.
    for _ in range(140):
        y = rng.uniform(WALL_Y - 3.1, WALL_Y - 0.1)
        if rng.random() > (y - (WALL_Y - 3.1)) / 3.0:
            continue
        z = 1.55 + rng.uniform(-0.05, 0.2)
        x = DRY_X + 0.05 - rng.uniform(-0.05, 0.6)
        leaf(leaves, (x, y, z), rng.uniform(0.06, 0.11), rng.uniform(0, 2 * math.pi), flat=True)

    emit(sc, "ivy_vines", vines, [M["vine"]], smooth=True)
    emit(sc, "ivy_leaves", leaves, [M["ivy"]])


def leaf(bm, centre, size, angle, flat=False):
    """A five-lobed ivy leaf. On the wall it faces out and tips forward at the bottom;
    flat, it lies on top of something."""
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
            out = -(size - b) * tilt * 0.45
            ring.append(bm.verts.new((centre[0] + a, centre[1] + out, centre[2] + b)))
    for k in range(len(ring)):
        f = bm.faces.new((centre_v, ring[(k + 1) % len(ring)], ring[k]))
        f.material_index = 0


def build_plaster(sc, M):
    """What's left of an old coat of whitewash, in flakes high on the right of the wall."""
    bm = bmesh.new()
    cx, cz = 8.6, 4.6
    cell = 0.03
    seed = Vector((31.0, 7.0, 0.0))
    x0, x1, z0, z1 = cx - 1.9, min(cx + 1.9, WALL_X1 - 0.05), cz - 1.6, WALL_TOP - 0.02
    nx, nz = int((x1 - x0) / cell), int((z1 - z0) / cell)
    keep = []
    for j in range(nz):
        row = []
        for i in range(nx):
            x, z = x0 + (i + 0.5) * cell, z0 + (j + 0.5) * cell
            fall = 1.0 - math.hypot((x - cx) / 1.9, (z - cz) / 1.7)
            n = noise.fractal(Vector((x * 2.2, z * 2.2, 0)) + seed, 0.8, 2.2, 5)
            row.append(fall * 0.9 + n * 0.7 > 0.25)
        keep.append(row)
    verts = {}

    def vert(i, j):
        if (i, j) not in verts:
            x, z = x0 + i * cell, z0 + j * cell
            verts[(i, j)] = bm.verts.new((x, WALL_Y - 0.004 - 0.002 * noise.noise(Vector((x * 7, z * 7, 2))), z))
        return verts[(i, j)]

    for j in range(nz):
        for i in range(nx):
            if keep[j][i]:
                bm.faces.new((vert(i, j), vert(i, j + 1), vert(i + 1, j + 1), vert(i + 1, j)))
    box_uv(bm, bm.faces[:])
    emit(sc, "plaster", bm, [M["plaster"]])


def build_fallen_leaves(sc, M):
    bm = bmesh.new()
    for _ in range(90):
        x, y = rng.uniform(-6, 11), rng.uniform(-7, WALL_Y - 0.3)
        if puddle_depth(x, y) > 0.3:
            continue
        on_path = path_distance(x, y) < PATH_HALF_WIDTH
        leaf(bm, (x, y, ground_height(x, y) + (0.075 if on_path else 0.006)), rng.uniform(0.04, 0.07),
             rng.uniform(0, 2 * math.pi), flat=True)
    for f in bm.faces:
        f.material_index = rng.randrange(2) if False else f.material_index
    emit(sc, "fallen_leaves", bm, [M["dead_leaf"]])


# ---------------------------------------------------------------------------------------

def build():
    sc = fresh_scene()
    M = make_materials()
    build_wall(sc, M)
    build_plinth(sc, M)
    build_lamp(sc, M)
    build_dry_wall(sc, M)
    build_ground(sc, M)
    build_cobbles(sc, M)
    build_pebbles(sc, M)
    build_deck(sc, M)
    build_crate(sc, M)
    build_barrel(sc, M)
    build_spheres(sc, M)
    build_ivy(sc, M)
    build_plaster(sc, M)
    build_fallen_leaves(sc, M)
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

"""Builds the Archer example's firs, straw dummy and arrow in Blender.

    blender -b --python examples/Archer/models/build.py

Run from the repository root, with the Poly Haven downloads from the example's README in
assets. Writes firs.glb, dummy.glb and arrow.glb to assets/archer-props, where the
build cooks them. Everything is seeded, so the same files come out each time.
"""

import math
import os
import random

import bmesh
import bpy
import numpy as np
from mathutils import Vector

ROOT = os.getcwd()
SOURCE = os.path.join(ROOT, "assets")
OUT = os.path.join(SOURCE, "archer-props")
FIR = os.path.join(SOURCE, "props", "fir_sapling_medium", "textures")
TEXTURES = os.path.join(SOURCE, "textures")


def texture(folder, name):
    return os.path.join(TEXTURES, folder, "textures", name)


def load(path):
    image = bpy.data.images.load(path, check_existing=True)
    return image


def material(name, color=None, normal=None, base=(0.8, 0.8, 0.8), roughness=0.9, metallic=0.0, alpha=False):
    """A plain Principled material, with an optional colour and OpenGL normal texture."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    links = mat.node_tree.links
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (*base, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic
    if color is not None:
        node = nodes.new("ShaderNodeTexImage")
        node.image = color
        links.new(node.outputs["Color"], bsdf.inputs["Base Color"])
        if alpha:
            links.new(node.outputs["Alpha"], bsdf.inputs["Alpha"])
    if normal is not None:
        normal.colorspace_settings.name = "Non-Color"
        node = nodes.new("ShaderNodeTexImage")
        node.image = normal
        map_node = nodes.new("ShaderNodeNormalMap")
        links.new(node.outputs["Color"], map_node.inputs["Color"])
        links.new(map_node.outputs["Normal"], bsdf.inputs["Normal"])
    return mat


def mesh_object(name, bm, materials):
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    for mat in materials:
        mesh.materials.append(mat)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def export(path, prefix):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in bpy.context.scene.objects:
        obj.select_set(obj.name.startswith(prefix))
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=True)
    print("Wrote", path)


# ---------------------------------------------------------------------------------------
# Firs: a tapered trunk and whorls of branch cards cut from the fir sapling's twig atlas.

# Regions of the atlas as (u0, v0, u1, v1), each a spray whose stem starts at the bottom middle.
SPRAYS = [
    (0.307, 0.224, 0.640, 0.600),
    (0.635, 0.170, 0.952, 0.546),
    (0.654, 0.634, 0.933, 0.961),
    (0.190, 0.687, 0.425, 0.951),
]


def twig_image():
    """The twig colour with the separate cut-out mask packed into its alpha."""
    color = load(os.path.join(FIR, "fir_sapling_medium_twigs_diff_1k.jpg"))
    mask = load(os.path.join(FIR, "fir_sapling_medium_twigs_alpha_1k.png"))
    width, height = color.size
    rgba = np.array(color.pixels[:], dtype=np.float32).reshape(height, width, 4)
    alpha = np.array(mask.pixels[:], dtype=np.float32).reshape(height, width, 4)[:, :, 0]
    rgba[:, :, 3] = alpha
    packed = bpy.data.images.new("fir_twigs", width, height, alpha=True)
    packed.pixels[:] = rgba.ravel()
    packed.filepath_raw = os.path.join(OUT, "fir_twigs.png")
    packed.file_format = "PNG"
    packed.save()
    return packed


def card(bm, uv, base, out, side, length, droop, region, fold):
    """A branch card folded along its stem and bent down towards its tip."""
    u0, v0, u1, v1 = region
    width = length * (u1 - u0) / (v1 - v0) * 1.5
    up = out.cross(side).normalized()
    if up.z < 0:
        up = -up
    rows = []
    for t in (0.0, 0.5, 1.0):
        row = []
        for s in (-1.0, 0.0, 1.0):
            p = base + out * (length * t) + side * (width * 0.5 * s)
            p += Vector((0, 0, -droop * length * t * t))
            p += up * (fold * width * (1.0 - abs(s)))
            row.append((bm.verts.new(p), (u0 + (u1 - u0) * (s + 1) * 0.5, v0 + (v1 - v0) * t)))
        rows.append(row)
    for r in range(2):
        for c in range(2):
            quad = [rows[r][c], rows[r][c + 1], rows[r + 1][c + 1], rows[r + 1][c]]
            face = bm.faces.new([q[0] for q in quad])
            face.material_index = 1
            for loop, q in zip(face.loops, quad):
                loop[uv].uv = q[1]


def trunk(bm, uv, height, radius, sides=8, rings=6):
    verts = []
    for ring in range(rings + 1):
        t = ring / rings
        z = height * t
        r = radius * (1.0 - t) ** 1.2 + 0.01
        verts.append([bm.verts.new((math.cos(a) * r, math.sin(a) * r, z)) for a in (2 * math.pi * i / sides for i in range(sides))])
    around = 2 * math.pi * radius
    for ring in range(rings):
        for i in range(sides):
            j = (i + 1) % sides
            face = bm.faces.new([verts[ring][i], verts[ring][j], verts[ring + 1][j], verts[ring + 1][i]])
            face.material_index = 0
            corners = [(i, ring), (i + 1, ring), (i + 1, ring + 1), (i, ring + 1)]
            for loop, (a, b) in zip(face.loops, corners):
                loop[uv].uv = (a / sides * around / 1.5, b / rings * height / 1.5)


def fir(name, seed, height, spread, crown_base, layers, materials):
    rng = random.Random(seed)
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    trunk(bm, uv, height * 0.97, height * 0.018)
    for layer in range(layers):
        t = (layer / (layers - 1)) ** 0.85
        z = crown_base + (height - crown_base) * t
        reach = spread * (1.0 - t) ** 1.05 + 0.25
        count = max(6, int(round(10 + 8 * (1.0 - t))))
        turn = rng.random() * math.tau
        for j in range(count + 5):
            inner = j >= count  # A couple of short cards fill the crown between the long ones.
            angle = turn + math.tau * j / count + rng.uniform(-0.25, 0.25)
            length = reach * (rng.uniform(0.45, 0.65) if inner else rng.uniform(0.85, 1.1))
            pitch = math.radians(rng.uniform(8, 28) * (1.0 - t) - 12 * t)
            out = Vector((math.cos(angle) * math.cos(pitch), math.sin(angle) * math.cos(pitch), -math.sin(pitch)))
            side = Vector((-math.sin(angle), math.cos(angle), 0.0))
            roll = math.radians(rng.uniform(-35, 35))
            side = (side * math.cos(roll) + out.cross(side) * math.sin(roll)).normalized()
            base = Vector((0, 0, z + rng.uniform(-0.1, 0.1)))
            droop = rng.uniform(0.08, 0.22) * (1.0 - t)
            fold = rng.uniform(0.06, 0.14)
            if length > 1.6:
                # Long branches are two sprays end to end, the outer one smaller.
                card(bm, uv, base, out, side, length * 0.6, droop * 0.5, SPRAYS[rng.randrange(2)], fold)
                tip = base + out * (length * 0.45) + Vector((0, 0, -droop * length * 0.15))
                card(bm, uv, tip, out, side, length * 0.55, droop, SPRAYS[rng.randrange(4)], fold)
            else:
                card(bm, uv, base, out, side, length, droop, SPRAYS[rng.randrange(4)], fold)
    # The leader: two crossed sprays standing up at the top.
    top = Vector((0, 0, height * 0.94))
    for angle in (0.0, math.pi / 2):
        side = Vector((math.cos(angle), math.sin(angle), 0))
        card(bm, uv, top, Vector((0, 0, 1)), side, height * 0.09, 0.0, SPRAYS[2], 0.02)
    obj = mesh_object(name, bm, materials)
    print(name, len(obj.data.polygons), "faces")
    return obj


# ---------------------------------------------------------------------------------------
# The dummy: a stuffed burlap sack on a wooden post, with a painted straw target on its chest.

# Where the middle of the target goes, in Blender's space (z up, the front is -y).
TARGET_HEIGHT = 1.12
BODY_DEPTH = 0.17
BOSS_RADIUS = 0.21
BOSS_DEPTH = 0.07


def target_face():
    """The burlap weave with faded painted rings over it."""
    weave = load(texture("hessian_230", "hessian_230_diff_1k.jpg"))
    width, height = weave.size
    rgb = np.array(weave.pixels[:], dtype=np.float32).reshape(height, width, 4)[:, :, :3]
    light = rgb.mean(axis=2, keepdims=True)
    light = light / light.mean()
    y, x = np.mgrid[0:height, 0:width]
    r = np.hypot(x - width / 2 + 0.5, y - height / 2 + 0.5) / (width / 2)
    rings = [
        (0.20, (0.74, 0.52, 0.16)),  # gold
        (0.40, (0.50, 0.13, 0.08)),  # red ochre
        (0.58, (0.80, 0.74, 0.60)),  # chalk
        (0.70, (0.10, 0.09, 0.08)),  # charcoal
    ]
    paint = np.zeros((height, width, 3), dtype=np.float32)
    amount = np.zeros((height, width, 1), dtype=np.float32)
    inside = np.zeros((height, width), dtype=bool)
    for edge, color in rings:
        ring = (r < edge) & ~inside
        paint[ring] = color
        amount[ring] = 0.85
        inside |= ring
    # Worn paint: thinner where the weave is raised.
    rng = np.random.default_rng(3)
    amount *= np.clip(0.75 + 0.35 * rng.random((height, width, 1)), 0, 1)
    out = rgb * (1 - amount) + paint * light * amount
    image = bpy.data.images.new("target_face", width, height)
    pixels = np.concatenate([np.clip(out, 0, 1), np.ones((height, width, 1), np.float32)], axis=2)
    image.pixels[:] = pixels.ravel()
    image.filepath_raw = os.path.join(OUT, "target_face.png")
    image.file_format = "PNG"
    image.save()
    return image


def lumpy(bm, seed, amount):
    """Pushes a stuffed shape in and out a little, so it isn't a perfect ellipsoid."""
    rng = random.Random(seed)
    bumps = [(Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))).normalized(), rng.uniform(0.5, 1.0)) for _ in range(7)]
    for v in bm.verts:
        n = v.co.normalized()
        push = sum(w * max(0.0, n.dot(d)) ** 3 for d, w in bumps)
        v.co += n * (push * amount)


def sphere_part(name, center, scale, mat, seed, lump, uv_scale=1.0):
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    bmesh.ops.create_uvsphere(bm, u_segments=20, v_segments=14, radius=1.0, calc_uvs=True)
    lumpy(bm, seed, lump)
    for face in bm.faces:
        for loop in face.loops:
            loop[uv].uv *= uv_scale
    for v in bm.verts:
        v.co = Vector((v.co.x * scale[0], v.co.y * scale[1], v.co.z * scale[2])) + Vector(center)
    return mesh_object(name, bm, [mat])


def cylinder_part(name, start, end, radius, mat, sides=10, uv_width=0.15, uv_length=1.0):
    """A pole from one point to another, its texture a narrow strip along it, as one plank of a board."""
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    a, b = Vector(start), Vector(end)
    axis = (b - a).normalized()
    across = axis.orthogonal().normalized()
    other = axis.cross(across)
    rings = []
    for p in (a, b):
        rings.append([bm.verts.new(p + (across * math.cos(t) + other * math.sin(t)) * radius) for t in (math.tau * i / sides for i in range(sides))])
    length = (b - a).length
    for i in range(sides):
        j = (i + 1) % sides
        face = bm.faces.new([rings[0][i], rings[0][j], rings[1][j], rings[1][i]])
        for loop, (s, t) in zip(face.loops, [(i, 0), (i + 1, 0), (i + 1, 1), (i, 1)]):
            loop[uv].uv = (s / sides * uv_width, t * length * uv_length)
    # The caps face out of each end, so the start cap goes round the other way.
    bm.faces.new(list(reversed(rings[0])))
    bm.faces.new(rings[1])
    return mesh_object(name, bm, [mat])


def torus_part(name, center, radius, thickness, mat, squash=1.0):
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    major, minor = 18, 6
    grid = []
    for i in range(major):
        a = math.tau * i / major
        row = []
        for j in range(minor):
            b = math.tau * j / minor
            r = radius + math.cos(b) * thickness
            row.append(bm.verts.new((center[0] + math.cos(a) * r, center[1] + math.sin(a) * r * squash, center[2] + math.sin(b) * thickness)))
        grid.append(row)
    for i in range(major):
        for j in range(minor):
            quad = [grid[i][j], grid[(i + 1) % major][j], grid[(i + 1) % major][(j + 1) % minor], grid[i][(j + 1) % minor]]
            face = bm.faces.new(quad)
            for loop, (s, t) in zip(face.loops, [(i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1)]):
                loop[uv].uv = (s / major * 2.0, t / minor * 0.08)
    return mesh_object(name, bm, [mat])


def straw_tufts(name, seed, count, center, radius, height, outward, downward, mat):
    """Thin spikes of straw poking out of a seam."""
    rng = random.Random(seed)
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    for _ in range(count):
        a = rng.random() * math.tau
        root = Vector((center[0] + math.cos(a) * radius[0], center[1] + math.sin(a) * radius[1], center[2] + rng.uniform(-height, height)))
        direction = Vector((math.cos(a) * outward, math.sin(a) * outward, -downward)) + Vector((rng.uniform(-0.4, 0.4), rng.uniform(-0.4, 0.4), rng.uniform(-0.3, 0.5)))
        direction.normalize()
        length = rng.uniform(0.05, 0.13)
        side = direction.orthogonal().normalized() * rng.uniform(0.004, 0.008)
        tip = root + direction * length
        face = bm.faces.new([bm.verts.new(root - side), bm.verts.new(root + side), bm.verts.new(tip)])
        u = rng.random()
        for loop, coord in zip(face.loops, [(u, 0.0), (u + 0.01, 0.0), (u + 0.005, 0.25)]):
            loop[uv].uv = coord
    return mesh_object(name, bm, [mat])


def target_boss(name, face_mat, straw_mat):
    """A round straw boss, its front a disc of painted burlap."""
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    sides = 32
    front_y = -(BODY_DEPTH + BOSS_DEPTH)
    back_y = -BODY_DEPTH + 0.03
    def ring(y, r):
        return [bm.verts.new((math.cos(t) * r, y, TARGET_HEIGHT + math.sin(t) * r)) for t in (math.tau * i / sides for i in range(sides))]
    front = ring(front_y, BOSS_RADIUS)
    bevel = ring(front_y + 0.015, BOSS_RADIUS + 0.012)
    back = ring(back_y, BOSS_RADIUS + 0.005)
    center = bm.verts.new((0, front_y - 0.005, TARGET_HEIGHT))
    for i in range(sides):
        j = (i + 1) % sides
        face = bm.faces.new([center, front[i], front[j]])  # Wound to face forward, out of the boss.
        face.material_index = 0
        for loop, v in zip(face.loops, [center, front[i], front[j]]):
            loop[uv].uv = (0.5 + (v.co.x) / (2 * BOSS_RADIUS) * 0.98, 0.5 + (v.co.z - TARGET_HEIGHT) / (2 * BOSS_RADIUS) * 0.98)
        for a, b, mat_index in ((front, bevel, 1), (bevel, back, 1)):
            face = bm.faces.new([a[i], b[i], b[j], a[j]])
            face.material_index = mat_index
            for loop, (s, t) in zip(face.loops, [(i, 0), (i, 1), (i + 1, 1), (i + 1, 0)]):
                loop[uv].uv = (s / sides * 3.0, t * 0.1)
    return mesh_object(name, bm, [face_mat, straw_mat])


def dummy():
    burlap = material("dummy_burlap", load(texture("hessian_230", "hessian_230_diff_1k.jpg")), load(texture("hessian_230", "hessian_230_nor_gl_1k.jpg")), roughness=0.95)
    straw = material("dummy_straw", load(texture("thatch_roof_angled", "thatch_roof_angled_diff_1k.jpg")), load(texture("thatch_roof_angled", "thatch_roof_angled_nor_gl_1k.jpg")), roughness=0.85)
    wood = material("dummy_wood", load(texture("brown_planks_04", "brown_planks_04_diff_1k.jpg")), load(texture("brown_planks_04", "brown_planks_04_nor_gl_1k.jpg")), roughness=0.8)
    rope = material("dummy_rope", base=(0.36, 0.27, 0.16), roughness=0.9)
    tufts = material("dummy_straw_tufts", base=(0.62, 0.48, 0.22), roughness=0.85)
    face = material("dummy_target", target_face(), load(texture("hessian_230", "hessian_230_nor_gl_1k.jpg")), roughness=0.95)

    cylinder_part("dummy_post", (0, 0, 0), (0, 0, 1.62), 0.05, wood, uv_width=0.12, uv_length=0.5)
    cylinder_part("dummy_arms", (-0.48, 0, 1.3), (0.48, 0, 1.3), 0.032, wood, uv_width=0.08, uv_length=0.5)
    sphere_part("dummy_body", (0, 0, 1.06), (0.25, BODY_DEPTH, 0.36), burlap, seed=1, lump=0.06, uv_scale=2.0)
    sphere_part("dummy_head", (0, 0, 1.58), (0.12, 0.11, 0.14), burlap, seed=2, lump=0.08, uv_scale=1.0)
    torus_part("dummy_neck_rope", (0, 0, 1.43), 0.075, 0.014, rope)
    torus_part("dummy_waist_rope", (0, 0, 0.8), 0.17, 0.015, rope, squash=0.7)
    straw_tufts("dummy_neck_straw", 4, 70, (0, 0, 1.44), (0.07, 0.07), 0.01, 1.0, 0.3, tufts)
    straw_tufts("dummy_hem_straw", 5, 110, (0, 0, 0.74), (0.13, 0.1), 0.015, 0.4, 1.0, tufts)
    for x, seed in ((-0.48, 6), (0.48, 7)):
        straw_tufts(f"dummy_hand_straw_{seed}", seed, 30, (x, 0, 1.3), (0.03, 0.03), 0.02, 1.0, 0.2, tufts)
    target_boss("dummy_boss", face, tufts)


# ---------------------------------------------------------------------------------------
# The arrow: a wooden shaft with a steel head and three feathers, its point down -y (glTF +z).

HALF = 0.4


def arrow():
    shaft = material("arrow_shaft", base=(0.52, 0.36, 0.21), roughness=0.6)
    steel = material("arrow_head", base=(0.32, 0.32, 0.33), roughness=0.35, metallic=0.9)
    cream = material("arrow_feather_light", base=(0.78, 0.73, 0.63), roughness=0.8)
    barred = material("arrow_feather_dark", base=(0.2, 0.16, 0.13), roughness=0.8)
    nock = material("arrow_nock", base=(0.07, 0.07, 0.07), roughness=0.5)

    cylinder_part("arrow_shaft", (0, -HALF + 0.055, 0), (0, HALF - 0.012, 0), 0.0045, shaft, sides=6)
    cylinder_part("arrow_nock", (0, HALF - 0.012, 0), (0, HALF, 0), 0.0055, nock, sides=6)

    bm = bmesh.new()
    tip = bm.verts.new((0, -HALF, 0))
    base = [bm.verts.new((math.cos(t) * 0.008, -HALF + 0.055, math.sin(t) * 0.008)) for t in (math.tau * i / 4 + math.pi / 4 for i in range(4))]
    for i in range(4):
        bm.faces.new([tip, base[i], base[(i + 1) % 4]])
    bm.faces.new(list(reversed(base)))
    mesh_object("arrow_head", bm, [steel])

    # Three vanes 120 degrees apart, each tall at the back and sweeping down to the shaft.
    for i, mat in enumerate((barred, cream, cream)):
        a = math.tau * i / 3 + math.pi / 2
        out = Vector((math.cos(a), 0, math.sin(a)))
        bm = bmesh.new()
        back, front = HALF - 0.03, HALF - 0.15
        points = []
        for k in range(6):
            t = k / 5
            y = back + (front - back) * t
            height = 0.022 * (1 - t ** 1.6)
            points.append((y, height))
        root = [bm.verts.new(out * 0.0045 + Vector((0, y, 0))) for y, _ in points]
        edge = [bm.verts.new(out * (0.0045 + h) + Vector((0, y, 0))) for y, h in points]
        for k in range(5):
            bm.faces.new([root[k], root[k + 1], edge[k + 1], edge[k]])
        mesh_object(f"arrow_vane_{i}", bm, [mat])


def bounds(prefix):
    points = [obj.matrix_world @ Vector(c) for obj in bpy.context.scene.objects if obj.name.startswith(prefix) for c in obj.bound_box]
    low = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
    high = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
    return low, high


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    os.makedirs(OUT, exist_ok=True)

    bark = material("fir_bark", load(os.path.join(FIR, "fir_sapling_medium_branches_diff_1k.jpg")), load(os.path.join(FIR, "fir_sapling_medium_branches_nor_gl_1k.jpg")), roughness=0.9)
    twigs = material("fir_twigs", twig_image(), load(os.path.join(FIR, "fir_sapling_medium_twigs_nor_gl_1k.jpg")), roughness=0.8, alpha=True)
    fir("fir_tall", 21, height=15.0, spread=3.0, crown_base=0.5, layers=42, materials=[bark, twigs])
    fir("fir_broad", 22, height=11.0, spread=3.2, crown_base=0.3, layers=38, materials=[bark, twigs])
    fir("fir_young", 23, height=6.0, spread=1.9, crown_base=0.25, layers=24, materials=[bark, twigs])
    export(os.path.join(OUT, "firs.glb"), "fir_")

    dummy()
    export(os.path.join(OUT, "dummy.glb"), "dummy_")
    low, high = bounds("dummy_")
    # The model cooker centres a model on its bounds across the ground and stands it on its lowest point.
    center = (low + high) / 2
    print("Dummy bullseye after import (x, y, z):", round(-center.x, 3), round(TARGET_HEIGHT - low.z, 3), round(BODY_DEPTH + BOSS_DEPTH + center.y, 3))

    arrow()
    export(os.path.join(OUT, "arrow.glb"), "arrow_")


main()

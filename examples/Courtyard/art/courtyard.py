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
from mathutils import Matrix, Quaternion, Vector, noise

SCENE = "Courtyard"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "content", "models", "courtyard.glb")
# Models downloaded from Sketchfab under CC BY 4.0: the lantern, the crates and the wheel.
# The README credits them.
ASSETS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "assets")
# Where the lantern's plate meets the wall, level with its bulb, right of the window.
LAMP_X = 4.7
LAMP_Z = 3.95
LAMP_SIZE = 0.26

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
REVEAL = 0.48

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
        # The dry wall's stones, one graph tinted per slot in the sidecar, so each stone can
        # take its own colour.
        "dry_warm": material("graph_drystone_warm", (0.47, 0.42, 0.36)),
        "dry_cool": material("graph_drystone_cool", (0.4, 0.42, 0.44)),
        "dry_pale": material("graph_drystone_pale", (0.52, 0.5, 0.46)),
        "dry_damp": material("graph_drystone_damp", (0.32, 0.32, 0.3)),
        "dry_core": material("graph_drystone_core", (0.24, 0.23, 0.22)),
        "dirt": material("graph_dirt", (0.25, 0.17, 0.11)),
        "wood": material("graph_wood", (0.5, 0.34, 0.2)),
        # The same wood graph as variants: the barrel's staves a little weathered with rust
        # bleeding down from their hoops, and the deck's boards silvered by the weather.
        "staves": material("graph_staves", (0.5, 0.34, 0.2)),
        "deck": material("graph_deck", (0.52, 0.47, 0.4)),
        "mortar": material("mortar", (0.32, 0.3, 0.27), roughness=0.95),
        "arch1": material("arch_brick_a", (0.42, 0.13, 0.07), roughness=0.85),
        "arch2": material("arch_brick_b", (0.5, 0.17, 0.09), roughness=0.85),
        "arch3": material("arch_brick_c", (0.36, 0.12, 0.08), roughness=0.85),
        "iron": material("iron", (0.05, 0.048, 0.045), metallic=0.8, roughness=0.45),
        "hoop": material("graph_hoop", (0.25, 0.14, 0.08), metallic=0.5, roughness=0.6),
        "frame": material("window_frame", (0.04, 0.035, 0.03), metallic=0.6, roughness=0.5),
        "glass": material("graph_glass", (0.3, 0.36, 0.34), roughness=0.05),
        "interior": material("interior", (0.015, 0.013, 0.012), roughness=1.0),
        "room": material("room", (0.38, 0.31, 0.23), roughness=0.95),
        "bark": material("bark", (0.16, 0.13, 0.1), roughness=0.9),
        "tree_leaf": material("tree_leaf", (0.11, 0.19, 0.05), roughness=0.7),
        "bulb": material("bulb", (1.0, 0.85, 0.6), emission=(1.0, 0.75, 0.45), strength=12.0),
        "vine": material("vine", (0.12, 0.09, 0.06), roughness=0.9),
        "plaster": material("graph_whitewash", (0.72, 0.7, 0.66), roughness=0.95),
        "dead_leaf": material("dead_leaf", (0.42, 0.24, 0.1), roughness=0.8),
        "dead_leaf_orange": material("dead_leaf_orange", (0.7, 0.36, 0.08), roughness=0.75),
        "sack": material("graph_sack", (0.5, 0.4, 0.27), roughness=0.95),
        "twine": material("twine", (0.18, 0.14, 0.09), roughness=0.9),
        # Cards: flat quads that wear a leaf, blade or web graph, cut out by its opacity.
        "ivy_small": material("graph_ivy_small", (0.2, 0.32, 0.12)),
        "ivy_large": material("graph_ivy_large", (0.2, 0.32, 0.12)),
        "ivy_dead": material("graph_ivy_dead", (0.36, 0.26, 0.1)),
        "meadow": material("graph_blade_meadow", (0.25, 0.38, 0.14)),
        "broad": material("graph_blade_broad", (0.2, 0.34, 0.12)),
        "dry": material("graph_blade_dry", (0.55, 0.5, 0.3)),
        "cobweb": material("graph_cobweb", (0.8, 0.8, 0.8)),
        "leaf_tan": material("graph_deadleaf_tan", (0.55, 0.36, 0.16)),
        "leaf_dark": material("graph_deadleaf_dark", (0.36, 0.22, 0.1)),
        "leaf_red": material("graph_deadleaf_red", (0.55, 0.22, 0.1)),
        "wet_stone": material("graph_pebble_wet", (0.45, 0.43, 0.38)),
    }


def textured(name, folder, color, roughness, metallic, normal):
    """A material from a downloaded model's image textures, in its folder under art/assets.
    The glTF export embeds the images, so the cook needs nothing else."""
    m = bpy.data.materials.get("cy_" + name) or bpy.data.materials.new("cy_" + name)
    m.use_nodes = True
    nodes, links = m.node_tree.nodes, m.node_tree.links
    nodes.clear()
    out = nodes.new("ShaderNodeOutputMaterial")
    bsdf = nodes.new("ShaderNodeBsdfPrincipled")
    links.new(bsdf.outputs[0], out.inputs[0])

    def image(file, data):
        img = bpy.data.images.load(os.path.join(ASSETS, folder, file), check_existing=True)
        img.colorspace_settings.is_data = data
        node = nodes.new("ShaderNodeTexImage")
        node.image = img
        return node

    links.new(image(color, False).outputs["Color"], bsdf.inputs["Base Color"])
    links.new(image(roughness, True).outputs["Color"], bsdf.inputs["Roughness"])
    if metallic is None:
        bsdf.inputs["Metallic"].default_value = 0.0
    else:
        links.new(image(metallic, True).outputs["Color"], bsdf.inputs["Metallic"])
    bump = nodes.new("ShaderNodeNormalMap")
    links.new(image(normal, True).outputs["Color"], bump.inputs["Color"])
    links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return m


def import_asset(sc, path):
    """Imports a downloaded model into sc and returns its meshes, with their parents' moves
    baked into the vertices so each stands in world space where the file put it. The empties
    the file came with are removed, and so is anything new it brought that isn't used."""
    before = (set(bpy.data.objects), set(bpy.data.materials), set(bpy.data.images))
    window = bpy.context.window
    previous = window.scene
    window.scene = sc
    try:
        ext = os.path.splitext(path)[1].lower()
        if ext == ".glb":
            bpy.ops.import_scene.gltf(filepath=path)
        elif ext == ".fbx":
            bpy.ops.import_scene.fbx(filepath=path)
        else:
            bpy.ops.wm.obj_import(filepath=path)
        bpy.context.view_layer.update()
    finally:
        window.scene = previous
    new = [o for o in bpy.data.objects if o not in before[0]]
    meshes = [o for o in new if o.type == "MESH"]
    for o in meshes:
        world = o.matrix_world.copy()
        o.parent = None
        o.data.transform(world)
        o.matrix_world = Matrix.Identity(4)
        o.data.materials.clear()
    for o in new:
        if o.type != "MESH":
            bpy.data.objects.remove(o, do_unlink=True)
    for m in set(bpy.data.materials) - before[1]:
        if m.users == 0:
            bpy.data.materials.remove(m)
    for img in set(bpy.data.images) - before[2]:
        if img.users == 0:
            bpy.data.images.remove(img)
    return meshes


def bounds(obs):
    vs = [v.co for o in obs for v in o.data.vertices]
    return (Vector([min(v[i] for v in vs) for i in range(3)]), Vector([max(v[i] for v in vs) for i in range(3)]))


def adopt(o, name, mats, matrix):
    """Moves an imported mesh by matrix and names it as the script's own."""
    o.data.transform(matrix)
    o.name = "cy_" + name
    o.data.name = "cy_" + name
    for m in mats:
        o.data.materials.append(m)
    return o


def remove_mesh(o):
    me = o.data
    bpy.data.objects.remove(o, do_unlink=True)
    if me.users == 0:
        bpy.data.meshes.remove(me)


def emit(sc, name, bm, mats, smooth=False, bend=None, share=2.0):
    """bend gives leaf cards the normals of the surface they grow on, as foliage cards are lit:
    one normal for every face, or a list with one per face in the order they were made. Each
    card's normal leans share times as far towards it as its own, turned to the same side,
    so the leaves are lit like the wall or ground under them, with a little of their own turn."""
    me = bpy.data.meshes.new("cy_" + name)
    if smooth or bend is not None:
        for f in bm.faces:
            f.smooth = True
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    if bend is not None:
        surfaces = bend if isinstance(bend, list) else [bend] * len(me.polygons)
        assert len(surfaces) == len(me.polygons), f"{name}: {len(surfaces)} bends for {len(me.polygons)} faces"
        normals = [None] * len(me.loops)
        for poly, surface in zip(me.polygons, surfaces):
            own = Vector(poly.normal)
            if own.dot(surface) < 0:
                own = -own
            bent = (surface.normalized() * share + own).normalized()
            for loop in poly.loop_indices:
                normals[loop] = bent
        me.normals_split_custom_set(normals)
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
        # The bevel rebuilds the box's six faces as new ones and leaves the old ones invalid, and
        # made["faces"] holds only the bevel's own strips and corners, so the six are found again
        # through the corners' vertices. Without them, the box's sides keep no material or UVs.
        faces = list(dict.fromkeys(f for strip in made["faces"] for v in strip.verts for f in v.link_faces))
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


def connected_faces(start):
    """Every face joined to start through shared edges: one whole object in a shared bmesh."""
    seen = {start}
    stack = [start]
    while stack:
        f = stack.pop()
        for e in f.edges:
            for g in e.link_faces:
                if g not in seen:
                    seen.add(g)
                    stack.append(g)
    return list(seen)


def block_axis(h, bevel, step):
    """Grid positions along one axis of a block from -h to h: four steps round each bevel, so
    it turns in a smooth quarter circle, and an even spacing of about step across the face."""
    inner = h - bevel
    count = max(1, int(round(2 * inner / step)))
    face = [-inner + 2 * inner * k / count for k in range(count + 1)]
    return [-h, -inner - bevel * 0.6, -inner - bevel * 0.25] + face + [inner + bevel * 0.25, inner + bevel * 0.6, h]


def block(bm, center, half, mat, bevel=0.03, yaw=0.0, roll=(0.0, 0.0), warp=0.0, rough=0.006, bulge=0.0,
          chips=(), step=0.05, taper=0.0, seed=None, uv_z=None):
    """A dressed stone: a box with flat faces and narrow, rounded arrises, so it reads as cut
    stone and not a pebble. warp moves its eight corners about, so it's never quite square;
    taper narrows the top; rough is a fine, pitted unevenness over every face; bulge swells
    the faces a little in the middle, as a pitch-faced stone's do. chips are (corner, size)
    pairs: corner is a sign triple such as (1, -1, 1), and that corner is knocked off with a
    rough, broken face. The block is built square about center, then rolled about X and Y and
    turned by yaw about Z. Its UVs start at a random place, unless uv_z fixes where v starts, so a texture
    can be laid from the block's foot. Returns its vertices."""
    hx, hy, hz = half
    xs, ys, zs = block_axis(hx, bevel, step), block_axis(hy, bevel, step), block_axis(hz, bevel, step)
    seed = seed or Vector((rng.uniform(-100, 100), rng.uniform(-100, 100), rng.uniform(-100, 100)))
    corners = {(sx, sy, sz): Vector((rng.uniform(-warp, warp), rng.uniform(-warp, warp), rng.uniform(-warp, warp) * 0.5))
               for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)}

    def place(x, y, z):
        # The rounded box: the nearest point on the box shrunk by the bevel, pushed out by the bevel.
        ix = max(-(hx - bevel), min(hx - bevel, x))
        iy = max(-(hy - bevel), min(hy - bevel, y))
        iz = max(-(hz - bevel), min(hz - bevel, z))
        out = Vector((x - ix, y - iy, z - iz))
        normal = out.normalized() if out.length > 1e-9 else Vector((0, 0, 1))
        p = Vector((ix, iy, iz)) + normal * bevel
        # Faces swell gently in the middle, flattening fast towards the arrises.
        fs = sorted(((abs(p.x) / hx) ** 4, (abs(p.y) / hy) ** 4, (abs(p.z) / hz) ** 4))
        p += normal * bulge * (1 - fs[0]) * (1 - fs[1])
        # Fine pitting and broad unevenness, along the surface's normal.
        n = noise.fractal(p * 7.0 + seed, 0.55, 2.0, 3) + 0.5 * noise.noise(p * 26.0 + seed)
        p += normal * rough * n
        # Taper and the warped corners: a trilinear blend of the corner offsets.
        u, v, w = (p.x / hx + 1) / 2, (p.y / hy + 1) / 2, (p.z / hz + 1) / 2
        p.x *= 1.0 - taper * w
        p.y *= 1.0 - taper * w
        offset = Vector()
        for (sx, sy, sz), c in corners.items():
            offset += c * ((u if sx > 0 else 1 - u) * (v if sy > 0 else 1 - v) * (w if sz > 0 else 1 - w))
        return p + offset

    grid = {}

    def vert(i, j, k):
        key = (i, j, k)
        if key not in grid:
            grid[key] = bm.verts.new(place(xs[i], ys[j], zs[k]))
        return grid[key]

    nx, ny, nz = len(xs) - 1, len(ys) - 1, len(zs) - 1
    faces = []
    for i in range(nx):
        for j in range(ny):
            faces.append(bm.faces.new((vert(i, j, 0), vert(i, j + 1, 0), vert(i + 1, j + 1, 0), vert(i + 1, j, 0))))
            faces.append(bm.faces.new((vert(i, j, nz), vert(i + 1, j, nz), vert(i + 1, j + 1, nz), vert(i, j + 1, nz))))
    for i in range(nx):
        for k in range(nz):
            faces.append(bm.faces.new((vert(i, 0, k), vert(i + 1, 0, k), vert(i + 1, 0, k + 1), vert(i, 0, k + 1))))
            faces.append(bm.faces.new((vert(i, ny, k), vert(i, ny, k + 1), vert(i + 1, ny, k + 1), vert(i + 1, ny, k))))
    for j in range(ny):
        for k in range(nz):
            faces.append(bm.faces.new((vert(0, j, k), vert(0, j, k + 1), vert(0, j + 1, k + 1), vert(0, j + 1, k))))
            faces.append(bm.faces.new((vert(nx, j, k), vert(nx, j + 1, k), vert(nx, j + 1, k + 1), vert(nx, j, k + 1))))
    verts = list(grid.values())

    # Knocked corners: the corner is cut off along a plane, so the break has a clean edge
    # rather than following the grid in steps, and the hole is closed with a fan from a point
    # sunk a little into the stone, so the break is a shallow scoop. It's flat shaded, so it
    # reads as a fresh fracture against the worn faces. The middle of the bottom face is never
    # chipped, so the block is found from it.
    keep = grid[(nx // 2, ny // 2, 0)]
    flat = []
    for corner, size in chips:
        sx, sy, sz = corner
        tip = Vector((sx * hx, sy * hy, sz * hz))
        normal = Vector((sx * rng.uniform(0.6, 1.2), sy * rng.uniform(0.6, 1.2), sz * rng.uniform(0.6, 1.2))).normalized()
        plane = tip - normal * size
        edges = list({e for f in faces for e in f.edges})
        cut = bmesh.ops.bisect_plane(bm, geom=verts + edges + faces, plane_co=plane, plane_no=normal, dist=1e-5,
                                     clear_outer=True)
        rim = [e for e in cut["geom_cut"] if isinstance(e, bmesh.types.BMEdge) and e.is_valid and e.is_boundary]
        if not rim:
            faces = connected_faces(keep.link_faces[0])
            verts = list({v for f in faces for v in f.verts})
            continue
        broken = bmesh.ops.holes_fill(bm, edges=rim, sides=0)["faces"]
        poked = bmesh.ops.poke(bm, faces=broken)
        for v in poked["verts"]:
            v.co -= normal * size * 0.12
        flat.extend(poked["faces"])
        faces = connected_faces(keep.link_faces[0])
        verts = list({v for f in faces for v in f.verts})
        bmesh.ops.recalc_face_normals(bm, faces=faces)

    # Rolled and turned into place.
    turn = Matrix.Rotation(yaw, 3, "Z") @ Matrix.Rotation(roll[1], 3, "Y") @ Matrix.Rotation(roll[0], 3, "X")
    c = Vector(center)
    for v in verts:
        v.co = c + turn @ v.co
    for f in faces:
        f.material_index = mat
        f.smooth = True
    for f in flat:
        if f.is_valid:
            f.smooth = False
    bm.normal_update()
    offset = (rng.uniform(0, 2), rng.uniform(0, 2), rng.uniform(0, 2))
    if uv_z is not None:
        offset = (offset[0], offset[1], uv_z)
    box_uv(bm, faces, offset)
    return verts


def split_sharp(bm, degrees=38.0):
    """Splits the edges where faces meet at more than degrees, so smooth shading keeps a hard
    break there: a chipped corner, a stone's broken face."""
    sharp = [e for e in bm.edges if len(e.link_faces) == 2 and e.calc_face_angle(0.0) > math.radians(degrees)]
    if sharp:
        bmesh.ops.split_edges(bm, edges=sharp)


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
            # Each brick takes its own part of the brick-face graph. Its own random stream, so
            # the rest of the scene's draws stay the same.
            pick = random.Random(ring * 100 + i)
            box_uv(bm, faces, (pick.uniform(0, 1), pick.uniform(0, 1), pick.uniform(0, 1)))
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
    build_brick_details(sc, M, x0, x1)


# The bricks graph lays 12 courses in its 3.5 m, so these are the heights its joints fall at.
COURSE = 3.5 / 12
BRICK = 0.4375
# A string course one course deep, standing out from the wall above the window, and a row of
# dentils under it: every other brick end stood out a little, so it casts a row of teeth.
BAND_Z0, BAND_Z1 = 16 * COURSE, 17 * COURSE
BAND_OUT = 0.035


def build_brick_details(sc, M, x0, x1):
    """Brickwork that stands out of the wall's flat face, so the raking sun has something to catch:
    the string course and its dentils, quoins round the window and two iron anchor plates."""
    bm = bmesh.new()

    def brick(bx0, bx1, bz0, bz1, front, back, axis="x"):
        """One brick with its own colour, faces a little uneven, a hair of a bevel."""
        lift = rng.uniform(-0.002, 0.002)
        lean = rng.uniform(-0.003, 0.003)
        if axis == "x":
            lo, hi = (bx0 + 0.005, front + lean, bz0 + 0.005 + lift), (bx1 - 0.005, back, bz1 - 0.005 + lift)
        else:
            lo, hi = (front + lean, bx0 + 0.005, bz0 + 0.005 + lift), (back, bx1 - 0.005, bz1 - 0.005 + lift)
        box(bm, lo, hi, rng.randrange(3), bevel=0.004)

    def run(xa, xb, wall_y, out, start):
        """A string course along the wall face at wall_y from xa to xb, with dentils under it."""
        for row, (z0, z1) in enumerate(((BAND_Z0, BAND_Z1),)):
            x = xa - (start + row * BRICK / 2) % BRICK
            while x < xb:
                brick(max(x, xa), min(x + BRICK, xb), z0, z1, wall_y - out - row * 0.006, wall_y + 0.02)
                x += BRICK
        # The dentils: headers, a quarter brick wide, every other one.
        x = xa + 0.02
        while x + BRICK / 4 < xb:
            brick(x, x + BRICK / 4, BAND_Z0 - COURSE, BAND_Z0, wall_y - out * 0.6, wall_y + 0.02)
            x += BRICK / 2
        box(bm, (xa, wall_y - out + 0.008, BAND_Z0 + 0.004), (xb, wall_y + 0.01, BAND_Z1 - 0.004), 3)

    front = WALL_Y - PIER_DEPTH
    run(WALL_X0, PIER_X0, WALL_Y, BAND_OUT, 0.0)
    run(PIER_X0 - BAND_OUT, PIER_X1 + BAND_OUT, front, BAND_OUT, 0.1)
    run(PIER_X1, WALL_X1, WALL_Y, BAND_OUT, 0.19)
    # The course turns the pier's corners: short returns along its sides.
    for side, (sx0, sx1) in ((-1, (PIER_X0 - BAND_OUT, PIER_X0 + 0.01)), (1, (PIER_X1 - 0.01, PIER_X1 + BAND_OUT))):
        for z0, z1 in ((BAND_Z0, BAND_Z1),):
            box(bm, (sx0, front - BAND_OUT + 0.005, z0 + 0.005), (sx1, WALL_Y, z1 - 0.005), rng.randrange(3), bevel=0.004)

    # Quoins up each side of the window: bricks standing a little proud, long and short in
    # turn, each one returning round the corner into the reveal.
    for jamb, outward in ((x0, -1), (x1, 1)):
        for course in range(math.ceil(SILL_Z / COURSE), int(SPRING_Z / COURSE)):
            z0, z1 = course * COURSE, (course + 1) * COURSE
            length = BRICK if course % 2 == 0 else BRICK / 2 + 0.005
            if outward < 0:
                bx0, bx1 = jamb - length, jamb + 0.012
            else:
                bx0, bx1 = jamb - 0.012, jamb + length
            brick(bx0, bx1, z0, z1, WALL_Y - 0.022, WALL_Y + 0.11)
    box_uv(bm, bm.faces[:], (0.4, 0.1, 0.0))

    # Iron anchor plates: the ends of tie rods that hold the wall to the floor inside, a round
    # boss on a disc, high up at each end of the wall.
    anchors = []
    for ax in (-1.2, 10.1):
        az = 5.35
        made = lathe(bm, [(0.0, 0.0), (0.12, 0.0), (0.12, 0.012), (0.1, 0.02), (0.04, 0.026), (0.035, 0.05),
                          (0.02, 0.06), (0.0, 0.06)], 24, (0, 0, 0), 4)
        for v in faces_verts(made):
            v.co = Vector((ax + v.co.x, WALL_Y - 0.009 - v.co.z, az + v.co.y))
        for f in made:
            f.smooth = True
        anchors += made
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    emit(sc, "brick_details", bm, [M["arch1"], M["arch2"], M["arch3"], M["mortar"], M["iron"]])


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
    # room behind it with limewashed walls, which the game lights from inside.
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
    emit(sc, "room", bm, [M["room"]])


def build_plinth(sc, M):
    """One unbroken course of big rough stones along the foot of the brick wall: blocks of
    mixed length butted together, the joints between them narrow and filled with mortar.
    The course stands well proud of the brickwork, about 30 cm, so its rounded fronts catch
    the sun and throw a shadow on the ground."""
    bm = bmesh.new()
    depth = 0.46
    x = WALL_X0 - 0.05
    count = 0
    while x < WALL_X1:
        # Mostly long, squat blocks, with now and then a short tall one or a long low one,
        # so the top of the course steps up and down instead of running level.
        kind = rng.random()
        if kind < 0.2:
            length, height = rng.uniform(0.34, 0.46), rng.uniform(0.6, 0.68)
        elif kind < 0.4:
            length, height = rng.uniform(0.8, 1.05), rng.uniform(0.46, 0.52)
        else:
            length, height = rng.uniform(0.48, 0.78), rng.uniform(0.5, 0.6)
        cx = x + length / 2
        # In front of the pier the course steps out with it.
        front = WALL_Y - (PIER_DEPTH if PIER_X0 - 0.1 < cx < PIER_X1 + 0.1 else 0.0)
        # Some blocks were set a little forward or back, have sunk, or have tipped a little
        # as the ground under them settled.
        sink = rng.uniform(0.0, 0.035)
        proud = rng.uniform(-0.025, 0.03)
        # Most have lost a corner or an arris somewhere, nearly always along the top front
        # edge where things knock them.
        chips = []
        for _ in range(rng.choice((0, 1, 1, 1, 2))):
            chips.append(((rng.choice((-1, 1)), -1, 1 if rng.random() < 0.8 else -1), rng.uniform(0.05, 0.13)))
        if count % 6 == 4:
            # A big bite out of one, as if it was dropped when it was set.
            chips.append(((rng.choice((-1, 1)), -1, 1), rng.uniform(0.16, 0.22)))
        block(bm, (cx, front - 0.34 - proud + depth / 2, height / 2 - 0.08 - sink),
              (length / 2 - 0.008, depth / 2, height / 2), 0,
              bevel=rng.uniform(0.022, 0.04), yaw=rng.uniform(-0.03, 0.03),
              roll=(rng.uniform(-0.03, 0.02), rng.uniform(-0.025, 0.025)),
              warp=0.02, rough=0.009, bulge=rng.uniform(0.008, 0.02), chips=chips, step=0.045, taper=0.02)
        # The joint: a finger's width, filled with mortar behind.
        x += length + rng.uniform(0.006, 0.016)
        count += 1
    # The corner: blocks down the building's side too.
    y = WALL_Y - 0.3
    while y < WALL_Y + 4.0:
        length = rng.uniform(0.45, 0.8)
        height = rng.uniform(0.48, 0.58)
        block(bm, (WALL_X0 - 0.06, y + length / 2, height / 2 - 0.08), (depth / 2, length / 2 - 0.008, height / 2), 0,
              bevel=0.03, warp=0.02, rough=0.009, bulge=0.012, step=0.06)
        y += length + 0.01
    # Mortar behind the blocks, so a joint is a dark line and not a gap.
    box(bm, (WALL_X0 - 0.2, WALL_Y - 0.27, 0.0), (WALL_X1, WALL_Y + 0.05, 0.3), 1)
    box(bm, (PIER_X0 + 0.02, WALL_Y - PIER_DEPTH - 0.27, 0.0), (PIER_X1 - 0.02, WALL_Y, 0.3), 1)
    split_sharp(bm)
    emit(sc, "plinth", bm, [M["stone"], M["mortar"]])


def build_lamp(sc, M):
    """A wall lantern (rsg3tank's "Outdoor wall lamps"): an oval plate, a short arm, and a
    six-sided glass lantern with a pointed roof and a bulb inside. The file holds four lamps
    in a row along y; this is the one at y 13.6. Its plate is at -x and the lantern stands
    out along +x, so it is turned to stand out from the wall, and scaled to 0.78 m tall."""
    obs = import_asset(sc, os.path.join(ASSETS, "outdoor-wall-lamps", "outdoor_wall_lamps.glb"))
    lamp = []
    for o in obs:
        lo, hi = bounds([o])
        if 12.5 < (lo.y + hi.y) / 2 < 14.6:
            lamp.append(o)
        else:
            remove_mesh(o)
    # By size: the body and its plate, the glass round the bulb, and the bulb.
    lamp.sort(key=lambda o: (bounds([o])[1] - bounds([o])[0]).length, reverse=True)
    body, glass, bulb = lamp
    lo, _ = bounds([body])
    b_lo, b_hi = bounds([bulb])
    middle = (b_lo + b_hi) / 2
    # The back of the plate goes on the wall, level with the bulb's middle.
    anchor = Vector((lo.x, middle.y, middle.z))
    place = (Matrix.Translation((LAMP_X, WALL_Y, LAMP_Z)) @ Matrix.Rotation(-math.pi / 2, 4, "Z")
             @ Matrix.Scale(LAMP_SIZE, 4) @ Matrix.Translation(-anchor))
    for o in (glass, bulb):
        if not o.data.uv_layers:
            o.data.uv_layers.new()
    adopt(body, "lamp", [M["frame"]], place)
    adopt(glass, "lamp_glass", [M["glass"]], place)
    adopt(bulb, "bulb", [M["bulb"]], place)
    print("bulb at", tuple(round(c, 3) for c in place @ middle))



# ---------------------------------------------------------------------------------------
# The low dry-stone wall on the left

def build_dry_wall(sc, M):
    """Built lying along X with its good face towards -Y, then turned a quarter so it runs
    out from the building towards -Y with that face towards the courtyard, +X."""
    # The wall draws from its own random stream and puts the shared one back afterwards, so
    # changing the wall doesn't move the grass, leaves and rubble placed after it.
    saved = rng.getstate()
    rng.seed(11)
    try:
        build_dry_wall_stones(sc, M)
    finally:
        rng.setstate(saved)


def build_dry_wall_stones(sc, M):
    bm = bmesh.new()
    x_start, x_end = -DRY_LENGTH, 0.0
    y0 = 0.0
    thickness = DRY_THICKNESS
    # The stones' material slots: one graph, tinted in the sidecar. Most stones are one of the
    # three colours the quarry gave; the bottom course is mostly darkened by rising damp, and
    # the hearting behind the face is darker still, as it's always in shadow.
    warm, cool, pale, damp, core = range(5)

    def tint(course):
        if course == 0 and rng.random() < 0.6:
            return damp
        return rng.choices((warm, cool, pale), weights=(5, 3, 2))[0]

    # Split, rough-squared stones of mixed sizes: flat beds, but a broken, uneven face with
    # sharp arrises and knocked corners, each stone standing out from the wall by its own
    # amount and tipped a little, so the light catches each one differently. Laid in uneven
    # courses with tight joints, the biggest at the bottom.
    front_corners = [(sx, -1, sz) for sx in (-1, 1) for sz in (-1, 1)]

    def face_stone(cx, cz, length, height, course, proud=None, depth=0.3):
        if proud is None:
            proud = rng.uniform(-0.035, 0.03)
        chips = [(corner, rng.uniform(0.015, 0.035) + 0.05 * min(length, height))
                 for corner in rng.sample(front_corners, rng.choice((0, 1, 1, 2)))]
        block(bm, (cx, y0 + proud + depth / 2, cz), (length / 2 - 0.003, depth / 2, height / 2 - 0.003), tint(course),
              bevel=rng.uniform(0.006, 0.014), yaw=rng.uniform(-0.05, 0.05),
              roll=(rng.uniform(-0.06, 0.06), rng.uniform(-0.04, 0.04)),
              warp=0.008 + 0.02 * min(length, height), rough=0.012, bulge=rng.uniform(0.0, 0.008),
              chips=chips, step=0.05)

    # The courses' heights, chosen first so a tall stone can jump up through two of them.
    heights = []
    top = 0.0
    while top < 1.2:
        h = rng.uniform(0.24, 0.36) if len(heights) < 2 else rng.uniform(0.17, 0.3)
        heights.append(h)
        top += h * 0.97
    # The spans along each course already filled by a jumper from the course below.
    taken = [[] for _ in heights]
    # A through-stone every metre and a half or so along the middle course, running right
    # through the wall to tie its two faces together, and sticking out of the face.
    through_course = len(heights) // 2
    last_through = x_start

    # Where a stone's corner meets its neighbours there is a gap, and a waller knocks a small
    # pinning stone into it so the big ones can't rock.
    pins = []
    z = 0.0
    for course, h in enumerate(heights):
        x = x_start + rng.uniform(0.0, 0.25)
        while x < x_end:
            span = next((s for s in taken[course] if s[0] - 0.02 <= x < s[1]), None)
            if span is not None:
                x = span[1]
                continue
            kind = rng.random()
            if course < 2 and kind < 0.25:
                # A big footing stone.
                length = rng.uniform(0.85, 1.25)
            elif kind < 0.2:
                # A short one.
                length = rng.uniform(0.18, 0.3)
            else:
                length = rng.uniform(0.32, 0.9) if course < 2 else rng.uniform(0.25, 0.65)
            # Stop short of a jumper ahead, filling right up to it.
            ahead = min((s[0] for s in taken[course] if s[0] > x), default=None)
            if ahead is not None and x + length > ahead - 0.15:
                length = ahead - x
                if length < 0.06:
                    x = ahead
                    continue
            cx = min(x + length / 2, x_end - length / 2 + 0.05)
            if course == through_course and x - last_through > 1.3 and 0.25 < length < 0.5 and rng.random() < 0.6:
                hh = h * rng.uniform(0.95, 1.0)
                face_stone(cx, z + hh / 2, length, hh, course, proud=-rng.uniform(0.04, 0.065), depth=thickness + 0.08)
                last_through = x
            elif 0.35 < length < 0.7 and rng.random() < 0.18:
                # Two thin stones in one slot, one on the other.
                lower = h * rng.uniform(0.42, 0.55)
                face_stone(cx + rng.uniform(-0.03, 0.03), z + lower / 2, length * rng.uniform(0.92, 1.0), lower - 0.006, course)
                upper = h * 0.99 - lower
                face_stone(cx + rng.uniform(-0.03, 0.03), z + lower + upper / 2, length * rng.uniform(0.9, 1.0), upper - 0.006, course)
            elif 1 <= course < len(heights) - 1 and 0.28 < length < 0.6 and rng.random() < 0.1:
                # A jumper: a tall stone that rises through this course and the next.
                hh = h * 0.97 + heights[course + 1] * rng.uniform(0.95, 1.0)
                face_stone(cx, z + hh / 2, length, hh, course)
                taken[course + 1].append((cx - length / 2, cx + length / 2))
            else:
                hh = h * rng.uniform(0.94, 1.0)
                face_stone(cx, z + hh / 2, length, hh, course)
            # A second stone behind, so the wall looks the same from the far side.
            rock(bm, (cx + 0.1, y0 + thickness - 0.16, z + h / 2), (length, 0.34, h * 0.9), tint(course),
                 power=4.0, bumps=0.12, yaw=rng.uniform(-0.08, 0.08), segments=(12, 8))
            if rng.random() < 0.6 and x_start + 0.2 < x < x_end - 0.2:
                pins.append((x, z + rng.uniform(0.2, 0.8) * h, course))
            x += length
        z += h * 0.97
    for px, pz, course in pins:
        s = rng.uniform(0.06, 0.11)
        block(bm, (px + rng.uniform(-0.02, 0.02), y0 + 0.09 + rng.uniform(0.0, 0.015), pz),
              (s * rng.uniform(0.3, 0.5), 0.09, s * rng.uniform(0.25, 0.4)), tint(course),
              bevel=0.006, yaw=rng.uniform(-0.3, 0.3), roll=(0.0, rng.uniform(-0.5, 0.5)), warp=0.012, rough=0.006, step=0.03)
    # The coping: thin slabs stood on edge across the top, all leaning the same way as a
    # waller sets them, a tall one and a shorter one in turn, so the top of the wall has a
    # clear, even comb along it.
    x = x_start + 0.05
    k = 0
    while x < x_end - 0.08:
        w = rng.uniform(0.08, 0.13)
        tall = rng.uniform(0.25, 0.3) if k % 2 == 0 else rng.uniform(0.18, 0.22)
        lean = 0.2 + rng.uniform(-0.07, 0.07)
        across = thickness + rng.uniform(0.0, 0.06)
        chips = [((rng.choice((-1, 1)), rng.choice((-1, 1)), 1), rng.uniform(0.03, 0.06))] if rng.random() < 0.5 else []
        block(bm, (x + w / 2 + math.sin(lean) * tall / 2, y0 + thickness / 2 + rng.uniform(-0.02, 0.02), z + math.cos(lean) * tall / 2 - 0.03),
              (w / 2, across / 2, tall / 2), tint(len(heights)), bevel=0.008, yaw=rng.uniform(-0.05, 0.05), roll=(0.0, lean),
              warp=0.014, rough=0.01, bulge=0.003, chips=chips, step=0.05)
        x += w / math.cos(lean) + rng.uniform(0.004, 0.012)
        k += 1
    # The hearting: small stones packed in behind the face, so a joint shows stone deep in
    # shadow rather than a hole.
    box(bm, (x_start, y0 + 0.07, 0.0), (x_end, y0 + thickness - 0.1, z + 0.05), core)
    # A quarter turn: along the wall becomes -Y from the plinth's face, and the good face becomes +X.
    for v in bm.verts:
        x, y = v.co.x, v.co.y
        v.co.x = DRY_X - (y - y0)
        v.co.y = WALL_Y - 0.1 + (x - x_end)
    emit(sc, "dry_wall", bm, [M["dry_warm"], M["dry_cool"], M["dry_pale"], M["dry_damp"], M["dry_core"]])


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

# The corners of the flagstones, where the joints meet, for grass to grow in.
JOINTS = []

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
    # A trough 10 cm wide with steep sides and a lip of pushed-up mud either side.
    return smoothstep(0.065, 0.03, best) - 0.45 * smoothstep(0.17, 0.1, best) * smoothstep(0.05, 0.1, best)


def ground_height(x, y):
    h = ground_broad(x, y)
    # Under the flagstones the mud is packed into the joints, so it has no clods to poke
    # up through them.
    paved = smoothstep(PATH_HALF_WIDTH + 0.3, PATH_HALF_WIDTH - 0.1, path_distance(x, y))
    rough = 1.0 - 0.95 * paved
    # Churned clods: sharp lumps from turbulence, strongest out in the open mud.
    h += 0.022 * rough * noise.turbulence(Vector((x * 3.2, y * 3.2, 1.0)), 3, False, amplitude_scale=0.5)
    h += 0.01 * rough * noise.noise(Vector((x * 9.0, y * 9.0, 4.0)))
    # Soft dents where the mud was trodden.
    h -= 0.02 * rough * max(0.0, noise.noise(Vector((x * 2.5, y * 2.5, 9.0))))
    edge = puddle_shape(x, y)
    # Prints sink deeper in the soft mud near the water. Nobody leaves prints in stone.
    wet = smoothstep(2.4, 1.2, edge)
    h -= (0.03 + 0.035 * wet) * footprint_depth(x, y) * (1.0 - paved)
    h -= 0.085 * rut_depth(x, y) * (1.0 - paved)
    # The puddle drops off steeply from a bank, then flattens out under the water.
    h -= 0.06 * smoothstep(1.08, 0.98, edge) + 0.04 * puddle_depth(x, y)
    # A muddy bank round the puddle, pushed up where feet went round it, with a sharp lip.
    h += 0.035 * math.exp(-((edge - 1.2) / 0.13) ** 2) + 0.012 * math.exp(-((edge - 1.45) / 0.3) ** 2)
    # Under the flagstones the ground sits low, so only their tops show.
    h -= 0.02 * paved
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


def build_clutter(sc, M):
    """What fills the open mud between the path and the deck: a weathered plank someone laid
    over the wettest part and trod down into it, and broken stone and brick half sunk in the
    mud, the leavings of the walls."""
    bm = bmesh.new()
    # The plank: one board, long and a bit twisted, one end sunk deeper than the other.
    a, b = Vector((4.4, -0.9, 0.0)), Vector((5.8, 0.15, 0.0))
    along = (b - a).normalized()
    length = (b - a).length
    width = 0.24
    side = Vector((-along.y, along.x, 0.0))
    za = max(ground_height(*(a + side * t)[:2]) for t in (0.0, width / 2, width)) - 0.012
    zb = max(ground_height(*(b + side * t)[:2]) for t in (0.0, width / 2, width)) - 0.022
    ax = Vector((along.x * length, along.y * length, zb - za)).normalized()
    ay = side.copy()
    ay.z = 0.02
    ay.normalize()
    az = ax.cross(ay).normalized()
    wood = bmesh.new()
    board(wood, length, width, 0.032, Vector((a.x, a.y, za)), ax, ay, az, 4, bevel=0.008)
    for v in wood.verts:
        # Cupped across, as an old board left out in the wet goes.
        w = (v.co - Vector((a.x, a.y, za))).dot(ay) / width
        v.co.z += 0.008 * (2 * w - 1) ** 2
    emit(sc, "plank", wood, [M["wood"]])
    # Rubble: broken stone and old bricks, most of each under the mud.
    placed = 0
    tries = 0
    while placed < 22 and tries < 400:
        tries += 1
        x, y = rng.uniform(3.4, 6.2), rng.uniform(-3.6, 0.6)
        if puddle_depth(x, y) > 0.0 or path_distance(x, y) < PATH_HALF_WIDTH + 0.08:
            continue
        rel = Vector((x, y, 0.0)) - a
        if -0.15 < rel.dot(along) < length + 0.15 and abs(rel.dot(side) - width / 2) < width / 2 + 0.08:
            continue
        if footprint_depth(x, y) > 0.05:
            continue
        brick = rng.random() < 0.3
        if brick:
            half = (rng.uniform(0.06, 0.18), 0.05, 0.035)
            mat = 1 + rng.randrange(2)
            bevel, warp = 0.008, 0.006
        else:
            s = rng.uniform(0.04, 0.12) if rng.random() < 0.8 else rng.uniform(0.13, 0.18)
            half = (s * rng.uniform(0.8, 1.3), s * rng.uniform(0.6, 1.0), s * rng.uniform(0.4, 0.6))
            mat = 0
            bevel, warp = 0.012, 0.02
        chips = [((rng.choice((-1, 1)), rng.choice((-1, 1)), 1), min(half) * rng.uniform(0.6, 1.2))]
        sink = half[2] * rng.uniform(0.9, 1.4)
        block(bm, (x, y, ground_height(x, y) + half[2] - sink), half, mat, bevel=bevel,
              yaw=rng.uniform(0, math.pi), roll=(rng.uniform(-0.12, 0.12), rng.uniform(-0.12, 0.12)),
              warp=warp, rough=0.004, chips=chips, step=0.03)
        placed += 1
    split_sharp(bm)
    emit(sc, "clutter", bm, [M["stone"], M["arch1"], M["arch2"]])


def voronoi_cell(p, neighbours, max_radius, joint, weights=None):
    """The cell around p, clipped by the bisectors with its neighbours, pulled in by half the joint.
    With weights, a power diagram: a stone with a bigger weight pushes its joints further out.
    Where no neighbour clips it, at the edge of the paving, the cell ends in a broken outline
    rather than a circle."""
    corners = 9
    start = rng.uniform(0, math.pi)
    poly = []
    for k in range(corners):
        a = start + 2 * math.pi * (k + rng.uniform(-0.25, 0.25)) / corners
        r = max_radius * rng.uniform(0.82, 1.0)
        poly.append((p[0] + r * math.cos(a), p[1] + r * math.sin(a)))
    for q in neighbours:
        nx, ny = q[0] - p[0], q[1] - p[1]
        dist = math.hypot(nx, ny)
        if dist < 1e-6:
            continue
        nx, ny = nx / dist, ny / dist
        shift = 0.0 if weights is None else (weights[p] - weights[q]) / (2 * dist)
        limit = max(dist / 2 + shift, 0.12) - joint / 2
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


def knock_corners(poly, chance):
    """Knocks the tip off some corners of a cut outline, as wear and frost do."""
    out = []
    n = len(poly)
    for k in range(n):
        a, p, b = poly[k - 1], poly[k], poly[(k + 1) % n]
        if rng.random() >= chance:
            out.append(p)
            continue
        for q in (a, b):
            length = math.hypot(q[0] - p[0], q[1] - p[1])
            t = min(rng.uniform(0.03, 0.07), length * 0.35) / max(length, 1e-6)
            out.append((p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t))
    return out


def densify(poly, step):
    """The outline with points every step along its edges, so they can be bent and chipped."""
    out = []
    n = len(poly)
    for k in range(n):
        a, b = poly[k], poly[(k + 1) % n]
        count = max(1, int(math.hypot(b[0] - a[0], b[1] - a[1]) / step))
        for s in range(count):
            t = s / count
            out.append((a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t))
    return out


def ground_broad(x, y):
    """The broad lie of the land, without the clods and dents on top of it."""
    return 0.03 * noise.fractal(Vector((x * 0.6, y * 0.6, 0.0)), 0.7, 2.0, 4)


def slab(bm, poly, centre, step, mat, stone):
    """A flagstone: sheer sides from below the mud, a narrow arris that is broken away in
    places, and a flat top that leans a little one way. The top has its own vertices, so its
    smooth shading stops at the arris and the edge reads as a crisp line. It is bedded just
    proud of the mud under it, whichever way it leans, then raised or lowered by step.
    It wears stone, (row, column, turn), of the flagstones graph (see flag_uv)."""
    seed = Vector((rng.uniform(-50, 50), rng.uniform(-50, 50), 0))
    # Most stones lie almost level. A few have rocked, and stand a little proud on one side.
    rocked = rng.random() < 0.1
    lean = 0.022 if rocked else 0.007
    tilt = (rng.uniform(-lean, lean), rng.uniform(-lean, lean))
    cx, cy = centre
    samples = list(poly) + [centre]
    samples += [((a[0] + b[0]) / 2, (a[1] + b[1]) / 2) for a, b in zip(poly, poly[1:] + poly[:1])]
    samples += [((x + cx) / 2, (y + cy) / 2) for x, y in poly]
    level = max(ground_height(x, y) + 0.006 - (x - cx) * tilt[0] - (y - cy) * tilt[1] for x, y in samples) + step

    def top_z(px, py):
        wave = 0.0015 * noise.noise(Vector((px * 5, py * 5, 0)) + seed)
        return level + wave + (px - cx) * tilt[0] + (py - cy) * tilt[1]

    outline = densify(knock_corners(poly, 0.35), 0.035)
    # How badly each point of the arris is chipped: mostly a hair, now and then a bite.
    chips = []
    for x, y in outline:
        n = noise.noise(Vector((x * 9, y * 9, 2.0)) + seed)
        chips.append(smoothstep(0.15, 0.6, n))

    def toward_centre(x, y, inset):
        dx, dy = x - cx, y - cy
        length = math.hypot(dx, dy) or 1.0
        return x - dx / length * inset, y - dy / length * inset

    # The edge wanders in and out a little, as a hand-cut stone does.
    edge = []
    for (x, y), chip in zip(outline, chips):
        wander = 0.003 * noise.noise(Vector((x * 6, y * 6, 4.0)) + seed)
        edge.append(toward_centre(x, y, 0.002 + wander + 0.004 * chip))

    bottom = [bm.verts.new((x, y, top_z(x, y) - 0.1)) for x, y in edge]
    shoulder = []
    lip = []
    for (x, y), chip in zip(edge, chips):
        z = top_z(x, y)
        shoulder.append(bm.verts.new((x, y, z - 0.005 - 0.01 * chip)))
        ix, iy = toward_centre(x, y, 0.003 + 0.012 * chip)
        lip.append((ix, iy, z - 0.002 * chip))
    arris = [bm.verts.new(p) for p in lip]

    n = len(edge)
    hard = []
    for ring_a, ring_b in ((bottom, shoulder), (shoulder, arris)):
        for k in range(n):
            k1 = (k + 1) % n
            hard.append(bm.faces.new((ring_a[k], ring_a[k1], ring_b[k1], ring_b[k])))
    # Smooth round the stone, so the narrow strips don't each catch the sun as a flat patch.
    # The top has its own vertices, so the edge where it meets the arris stays sharp.
    for f in hard:
        f.material_index = mat
        f.smooth = True

    # The top: the arris again, then rings closing in on the middle.
    rings = [[bm.verts.new(p) for p in lip]]
    for shrink in (0.75, 0.48, 0.22):
        ring = []
        for x, y, _ in lip:
            px, py = cx + (x - cx) * shrink, cy + (y - cy) * shrink
            ring.append(bm.verts.new((px, py, top_z(px, py))))
        rings.append(ring)
    middle = bm.verts.new((cx, cy, top_z(cx, cy)))
    soft = []
    for r in range(len(rings) - 1):
        for k in range(n):
            k1 = (k + 1) % n
            soft.append(bm.faces.new((rings[r][k], rings[r][k1], rings[r + 1][k1], rings[r + 1][k])))
    for k in range(n):
        soft.append(bm.faces.new((rings[-1][k], rings[-1][(k + 1) % n], middle)))
    for f in soft:
        f.material_index = mat
        f.smooth = True
    faces = hard + soft
    bmesh.ops.recalc_face_normals(bm, faces=hard)
    # The top is a separate disc, so recalculating can't tell up from down; it faces up.
    for f in soft:
        f.normal_update()
        if f.normal.z < 0:
            f.normal_flip()
    # The sides face out, away from the middle of the stone.
    for f in hard:
        f.normal_update()
        if f.normal.dot(f.calc_center_median() - Vector((cx, cy, f.calc_center_median().z))) < 0:
            f.normal_flip()
    # The sides are box mapped at real scale, so a 10 cm side shows 10 cm of stone, not the
    # graph's joint squeezed onto it. Lifted half a stone, so they sit in the middle of a row
    # of the graph's stones, clear of the joints between rows.
    o1, o2 = rng.uniform(0, 2), rng.uniform(0, 2)
    box_uv(bm, hard, (o1, o2, (stone[0] + 0.5) * FLAG_WIDTH / FLAG_STONES))
    flag_uv(bm, (cx, cy), lip, rings, middle, stone)


# The grid of content/materials/flagstones.material.cs: its Width, its Stones across and its
# Gap, a fraction of a stone. They must match the graph.
FLAG_WIDTH = 5.2
FLAG_STONES = 8
FLAG_GAP = 0.06
# Its own generator, so picking stones doesn't change anything else the script makes.
flag_rng = random.Random(18)


def flag_uv(bm, centre, lip, rings, middle, stone):
    """Maps a slab's top onto one stone of the flagstones graph: its outline onto the edge of
    the stone's square, its middle onto the square's middle and every ring between in
    proportion, so the graph's chamfer, chips and moss follow the real edge and its crown
    sits on the middle. The square is turned by an angle of the slab's own, so the cracks
    run every way. The sides are box mapped in slab()."""
    uv = uv_layer(bm)
    row, column, turn = stone
    size = FLAG_WIDTH / FLAG_STONES
    half = size * (0.5 - FLAG_GAP / 2)
    mx, my = (column + 0.5) * size, (row + 0.5) * size
    cx, cy = centre
    at = {middle: (mx, my)}
    for k, (x, y, _) in enumerate(lip):
        angle = math.atan2(y - cy, x - cx) + turn
        dx, dy = math.cos(angle), math.sin(angle)
        # How far the square's edge is from its middle in this direction.
        edge = half / max(abs(dx), abs(dy))
        for ring, shrink in zip(rings, (1.0, 0.75, 0.48, 0.22)):
            at[ring[k]] = (mx + dx * edge * shrink, my + dy * edge * shrink)
    for v, (u, w) in at.items():
        for loop in v.link_loops:
            loop[uv].uv = (u, w)


def flag_stone_picker():
    """Returns pick(x, y): which stone of the graph the slab centred there wears, and how far
    it is turned. The stones are dealt from a shuffled deck of all 64, dealt again when it
    runs out, so each is used about as often as the others and the sandstone stays the one
    in eight the graph makes it. A slab never wears the same stone as a slab within 1.6 m."""
    placed = []
    deck = []

    def pick(x, y):
        taken = {stone for px, py, stone in placed if math.hypot(px - x, py - y) < 1.6}
        if not any(card not in taken for card in deck):
            fresh = [(r, c) for r in range(FLAG_STONES) for c in range(FLAG_STONES)]
            flag_rng.shuffle(fresh)
            deck.extend(fresh)
        choice = next(card for card in deck if card not in taken)
        deck.remove(choice)
        placed.append((x, y, choice))
        return choice[0], choice[1], flag_rng.uniform(0, 2 * math.pi)

    return pick


def build_flagstones(sc, M):
    """Big flat stones of mixed sizes cut from a power diagram, so they fit together with
    tight joints, laid nearly flush with the mud."""
    bm = bmesh.new()
    points = []
    ghosts = []
    weights = {}
    spacing = 0.66
    for j in range(-30, 30):
        for i in range(-30, 30):
            x = i * spacing + (spacing / 2 if j % 2 else 0) + rng.uniform(-0.2, 0.2) - 1.0
            y = j * spacing * 0.87 + rng.uniform(-0.2, 0.2) - 3.0
            edge = PATH_HALF_WIDTH + 0.15 * noise.noise(Vector((x * 1.3, y * 1.3, 3.0)))
            weight = rng.choice((-0.05, -0.02, 0.0, 0.02, 0.06))
            distance = path_distance(x, y)
            if distance < edge and y < WALL_Y - 0.45 and puddle_depth(x, y) < 0.4:
                points.append((x, y))
                # A big stone pushes its neighbours' joints back; a small one fills between.
                weights[(x, y)] = weight
            elif distance < edge + 1.0:
                # Just off the path: no stone here, but it still cuts its neighbours' cells, so
                # the stones at the edge end in straight cut sides and not round bulges.
                ghosts.append((x, y))
                weights[(x, y)] = weight
    everything = points + ghosts
    pick = flag_stone_picker()
    for p in points:
        near = [q for q in everything if q is not p and abs(q[0] - p[0]) < 1.4 and abs(q[1] - p[1]) < 1.4]
        cell = voronoi_cell(p, near, rng.uniform(0.62, 0.7), rng.uniform(0.008, 0.016), weights)
        if cell is None:
            continue
        cx = sum(c[0] for c in cell) / len(cell)
        cy = sum(c[1] for c in cell) / len(cell)
        # The tops sit just above the mud packed in the joints, a few a hair higher than their
        # neighbours, so the light catches a step here and there.
        slab(bm, cell, (cx, cy), rng.choice((0.0, 0.0, 0.0, 0.003, 0.006)), 0, pick(cx, cy))
        JOINTS.extend(cell)
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


def board_start(index):
    """Where along the wood graph's image the board in band index starts. The cooker flips v,
    so band index is the image's row 5 - index % 6, and each row starts half a board on from
    the one above it."""
    return 0.5 * ((5 - index % 6) % 2)


# How far into a board's end grain its sawn end face is put, as a fraction of a board: past the
# joint's groove and into the dark end.
END_GRAIN = 0.01

# How far in from a graph board's long edge a board fitted with a span starts, as a fraction of
# the image: past the graph's groove and its rounded edge (0.0158), onto the flat. The model's
# own bevel makes the edge, so the graph's rounding would put a second, sunlit edge beside it,
# which read as a bright line down each deck board.
FLAT_EDGE = 0.02


def plank_uv(bm, faces, axis, index, along_offset, span=None):
    """Wood UVs: u along the board's length in metres, v placed so the board takes one of the
    graph's six planks, so its own grain and not a seam runs down it.

    With span, the board's two ends (lo, hi along axis) are fitted to the two ends of one graph
    board, stretched a little so they land there, so a board has end grain at its sawn ends and
    nowhere else; keep such a board to about a metre. Its width is fitted across the graph
    board too, so it shows one tone and no joint."""
    uv = uv_layer(bm)
    axis_vector = Vector((1, 0, 0) if axis == "x" else (0, 1, 0) if axis == "y" else (0, 0, 1))
    if span is not None:
        lo, hi = span
        start = board_start(index)
        verts = [v.co for f in faces for v in f.verts]
        low = [min(co[k] for co in verts) for k in range(3)]
        high = [max(co[k] for co in verts) for k in range(3)]
    for f in faces:
        if span is not None:
            f.normal_update()
        n = f.normal
        end = span is not None and abs(n.dot(axis_vector)) > 0.7
        for loop in f.loops:
            co = loop.vert.co
            c = co.x if axis == "x" else co.y if axis == "y" else co.z
            if span is None:
                u = c + along_offset
            elif end:
                u = start + (END_GRAIN if n.dot(axis_vector) < 0 else 1 - END_GRAIN)
            else:
                u = start + (c - lo) / (hi - lo)
            # Across the board: whichever of the other two axes the face spans most.
            others = [k for k, a in enumerate("xyz") if a != axis]
            k = others[0] if abs(n.dot(Vector((1, 0, 0) if axis != "x" else (0, 1, 0)))) < 0.5 else others[1]
            band = (index % 6) / 6.0 + 0.012
            if span is None:
                loop[uv].uv = (u, band + (co[k] * 0.9) % (1 / 6.0 - 0.024))
            else:
                flat = (index % 6) / 6.0 + FLAT_EDGE
                loop[uv].uv = (u, flat + (co[k] - low[k]) / (high[k] - low[k]) * (1 / 6.0 - 2 * FLAT_EDGE))


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
            # The draw stays, so everything made after the deck comes out as it did; the board's
            # ends now fix where the graph's boards lie along it.
            rng.uniform(0, 3)
            # A board takes one graph board, which is a metre long, so a long run is laid as
            # shorter boards butted end to end, each with its own tone and end grain.
            for a, b in deck_pieces(a, b):
                faces = box(bm, (a, y, DECK_TOP - 0.035 + lift), (b, y + board, DECK_TOP + lift), 0, bevel=0.007)
                plank_uv(bm, faces, "x", i, 0.0, span=(a, b))
                i += 1
                # Two nails into each bearer the board crosses.
                for bearer in BEARERS:
                    xc = bearer + 0.05
                    if a + 0.05 < xc < b - 0.05:
                        for across in (0.25, 0.75):
                            nail_head(bm, xc, y + board * across, DECK_TOP + lift)
                # And two small ones near each end, where the board was fixed down before it was laid.
                for xe in (a + 0.03, b - 0.03):
                    for across in (0.3, 0.7):
                        stud(bm, (xe + end_nail_rng.uniform(-0.006, 0.006), y + board * across + end_nail_rng.uniform(-0.008, 0.008),
                                  DECK_TOP + lift), (0, 0, 1), 0.003, 0.001, 1, end_nail_rng)
        y += board + gap
    # Bearers under the boards, mostly below the mud.
    for x in BEARERS:
        faces = box(bm, (x, y0 - 0.02, -0.08), (x + 0.1, y1, DECK_TOP - 0.035), 0)
        plank_uv(bm, faces, "y", 3, 0.0)
    emit(sc, "deck", bm, [M["deck"], M["hoop"]])


# The shortest and longest boards the deck is laid in, in metres. Each takes one graph board,
# which is a metre long, so its grain is stretched or squeezed by at most half.
DECK_SHORTEST = 0.75
DECK_LONGEST = 1.5

deck_rng = random.Random(21)


def deck_pieces(a, b):
    """Cuts a run of deck from a to b along x into boards of 0.75 to 1.5 m, with 8 mm joints,
    staggered by their own random numbers. A cut that would leave a tail shorter than 0.75 m
    halves what is left instead."""
    pieces = []
    while b - a > DECK_LONGEST:
        step = deck_rng.uniform(DECK_SHORTEST, 1.2)
        if b - (a + step) < DECK_SHORTEST:
            step = (b - a) / 2
        pieces.append((a, a + step - 0.004))
        a += step + 0.004
    pieces.append((a, b))
    return pieces


# Where the deck's three bearers start along x; each is 10 cm wide.
BEARERS = (DECK[0] + 0.15, (DECK[0] + DECK[1]) / 2, DECK[1] - 0.25)

nail_rng = random.Random(20)
# The small nails at the boards' and the crates' ends, from their own numbers too.
end_nail_rng = random.Random(46)


def nail_head(bm, x, y, top):
    """A hand-made nail's head, a slightly domed iron disc about 12 mm across, driven not quite
    flush, in the hoop iron's material (slot 1). Its own random numbers, so adding nails moves
    nothing else in the scene."""
    x += nail_rng.uniform(-0.012, 0.012)
    y += nail_rng.uniform(-0.01, 0.01)
    sink = nail_rng.uniform(-0.0006, 0.0004)
    r = nail_rng.uniform(0.0055, 0.0068)
    profile = [(r, -0.0015), (r, 0.0006), (r * 0.65, 0.0014)]
    faces = lathe(bm, profile, 8, (x, y, top + sink), 1, close_top=True)
    # Each head takes its own patch of the iron, so they don't all rust alike.
    du, dv = nail_rng.uniform(0, 0.5), nail_rng.uniform(0, 0.5)
    uv = uv_layer(bm)
    for f in faces:
        f.smooth = False
        for loop in f.loops:
            loop[uv].uv = (loop[uv].uv[0] + du, loop[uv].uv[1] + dv)


crate_rng = random.Random(11)


def board(bm, length, width, thick, origin, ax, ay, az, index, bevel=None):
    """One bevelled board, laid out along ax, across ay and thick along az from origin. Its UVs
    are made before it's placed: u runs along it, and v keeps it inside one of the wood graph's
    six planks, so each board has its own grain and no seam."""
    faces = box(bm, (0, 0, 0), (length, width, thick), 0, bevel=bevel if bevel is not None else min(0.006, thick * 0.3))
    uv = uv_layer(bm)
    # The draw stays so the crates' numbers don't move; the board's ends are fitted to the ends
    # of one graph board instead, stretched to fit, so its sawn ends show end grain.
    crate_rng.uniform(0, 3)
    start = board_start(index)
    band = (index % 6) / 6.0 + 0.012
    for f in faces:
        f.normal_update()
        n = f.normal
        for loop in f.loops:
            x, y, z = loop.vert.co
            u = start + x / length
            if abs(n.x) > 0.7:
                loop[uv].uv = (start + (END_GRAIN if n.x < 0 else 1 - END_GRAIN), band + z * 0.9)
            elif abs(n.y) > 0.7:
                loop[uv].uv = (u, band + z * 0.9)
            else:
                # Across its whole width inside one graph board, so it shows one tone and no joint.
                loop[uv].uv = (u, band + y / width * (1 / 6.0 - 0.024))
    verts = {v for f in faces for v in f.verts}
    for v in verts:
        x, y, z = v.co
        v.co = origin + ax * x + ay * y + az * z
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def build_crates(sc, M):
    """Two of Memorie's "Wooden Crate"s on the deck: a big one by the wall and a small one at
    the front, each turned a little off square."""
    (big,) = import_asset(sc, os.path.join(ASSETS, "wooden-crate", "wooden-crate.fbx"))
    lo, hi = bounds([big])
    # Stood on its middle, foot at zero.
    base = Matrix.Translation((-(lo.x + hi.x) / 2, -(lo.y + hi.y) / 2, -lo.z))
    small = big.copy()
    small.data = big.data.copy()
    sc.collection.objects.link(small)
    wood = textured("crate_wood", "wooden-crate", "DefaultMaterial_Base_color.png", "DefaultMaterial_Roughness.png",
                    "DefaultMaterial_Metallic.png", "DefaultMaterial_Normal_OpenGL.png")
    # The file's crate is 2.3 m across. The big one comes out 0.97 m across and 0.78 m tall.
    nails = bmesh.new()
    for ob, name, x, y, degrees, size in ((big, "crate", 7.0, WALL_Y - 1.2, -6.0, 0.42),
                                          (small, "small_crate", 7.6, 1.2, 22.0, 0.26)):
        place = Matrix.Translation((x, y, DECK_TOP)) @ Matrix.Rotation(math.radians(degrees), 4, "Z") @ Matrix.Scale(size, 4)
        adopt(ob, name, [wood], place @ base)
        crate_nails(nails, ob, place, (lo, hi))
    emit(sc, "crate_nails", nails, [M["hoop"]])


def crate_nails(bm, ob, matrix, file_bounds):
    """Two nails at each end of the crate's top and bottom frame boards, on all four sides, where
    the boards meet the corner posts. They are found by casting rays in at the crate from outside,
    in the file's units with the crate stood on its middle (matrix takes those to the world, where
    adopt has already put the mesh), and each stands on the face it hits, square to it."""
    from mathutils.bvhtree import BVHTree
    me = ob.data
    tree = BVHTree.FromPolygons([v.co.copy() for v in me.vertices], [tuple(p.vertices) for p in me.polygons])
    lo, hi = file_bounds
    # The file's crate is centred on its middle once base has moved it; its frame boards run round
    # the foot from 0 to 0.28 of its 1.85 height and round the top from 1.57 up.
    half = (hi.x - lo.x) / 2
    height = hi.z - lo.z
    inverse = matrix.inverted()
    for side in range(4):
        a = side * math.pi / 2
        outward = Vector((math.cos(a), math.sin(a), 0.0))
        along = Vector((-math.sin(a), math.cos(a), 0.0))
        for end in (-1, 1):
            for z in (0.08, 0.2, 1.65, 1.77):
                local = outward * (half + 0.5) + along * end * (half - 0.17) + Vector((0, 0, z / 1.85 * height))
                origin = matrix @ local
                direction = (matrix.to_3x3() @ -outward).normalized()
                hit, normal, _, distance = tree.ray_cast(origin, direction, 2.0)
                if hit is None:
                    continue
                # Only on the crate's outside: a ray that slips through a gap finds the far side's inside.
                depth = (inverse @ hit - local).length - 0.5
                if depth > 0.12:
                    continue
                if normal.dot(direction) > 0:
                    normal = -normal
                stud(bm, hit, normal, 0.003, 0.001, 0, end_nail_rng)


def build_drainpipe(sc, M):
    """A cast-iron downpipe beside the pier: a hopper head high up, the pipe held off the
    bricks by collars, then a dog-leg out over the plinth and a shoe that spills onto a
    flat stone."""
    bm = bmesh.new()
    x = PIER_X1 + 0.13
    wall = WALL_Y - 0.09
    out = WALL_Y - 0.53
    r = 0.05
    head_z = 5.55
    # Down the wall, then a smooth dog-leg out over the plinth, straight down, and the shoe.
    points = [(x, wall, head_z + 0.02), (x, wall, 1.12)]
    for k in range(1, 12):
        t = k / 12
        s = t * t * (3 - 2 * t)
        points.append((x, wall + (out - wall) * s, 1.12 - 0.36 * t))
    points.append((x, out, 0.76))
    points.append((x, out, 0.3))
    for k in range(1, 7):
        a = (math.pi / 2) * k / 6
        points.append((x, out - 0.12 * (1 - math.cos(a)), 0.3 - 0.12 * math.sin(a)))
    pipe = tube(bm, points, r, 0, segments=16)
    # Sockets: a slightly fatter band where each length of pipe fits into the next.
    collars = []
    for z in (1.5, 3.3, 5.0):
        collars += lathe(bm, [(r + 0.001, -0.05), (r + 0.012, -0.045), (r + 0.012, 0.045), (r + 0.001, 0.05)], 20, (x, wall, z), 0)
        # The ears that nail it to the wall.
        box(bm, (x - 0.085, wall + 0.02, z - 0.035), (x + 0.085, WALL_Y + 0.005, z + 0.035), 0, bevel=0.006)
    # The hopper head: a square funnel, wide at the top, with a lip round it.
    hopper = lathe(bm, [(0.06, -0.06), (0.07, 0.0), (0.2, 0.17), (0.2, 0.36), (0.225, 0.37), (0.225, 0.4), (0.19, 0.4), (0.19, 0.2), (0.0, 0.2)],
                   4, (x, wall - 0.04, head_z), 0, angle0=math.pi / 4, angle1=math.pi / 4 + 2 * math.pi)
    for f in hopper:
        f.smooth = False
    for f in pipe + collars:
        f.smooth = True
    box_uv(bm, pipe)
    # The stone the water falls on.
    splash = bmesh.new()
    sx, sy = x, out - 0.24
    rock(splash, (sx, sy, ground_height(sx, sy) + 0.02), (0.34, 0.42, 0.08), 0, power=4.0, bumps=0.08,
         yaw=rng.uniform(-0.2, 0.2), segments=(16, 8), detail=0.02)
    emit(sc, "splash_stone", splash, [M["stone"]])
    emit(sc, "drainpipe", bm, [M["iron"]])


def build_wheel(sc, M):
    """An old cart wheel (DOMNIN's "Wooden_wheel") leaning against the plinth, tipped back
    against the stones. The file's axle runs along x; it is centred and scaled to 1.12 m
    across."""
    (wheel,) = import_asset(sc, os.path.join(ASSETS, "wooden-wheel", "Wheel_01.obj"))
    lo, hi = bounds([wheel])
    R = 0.56
    size = 2 * R / max(hi.y - lo.y, hi.z - lo.z)
    middle = Matrix.Translation(-(lo + hi) / 2)
    # Stand it up: the axle is square to a plane leaning back at 30 degrees and turned a
    # touch off the line of the wall.
    lean = math.radians(30)
    turn = math.radians(8)
    up = Vector((0, math.sin(lean), math.cos(lean)))
    along = Vector((math.cos(turn), math.sin(turn) * math.cos(lean), 0)).normalized()
    axle = along.cross(up).normalized()
    up = axle.cross(along).normalized()
    foot = Vector((5.55, WALL_Y - 0.76, 0.0))
    foot.z = max(ground_height(foot.x, foot.y), 0.0) + 0.01
    centre = foot + up * R
    # The file's x, y and z go to the axle, along the wall and up the leaning plane.
    stand = Matrix((axle, along, up)).transposed().to_4x4()
    wood = textured("wheel_wood", "wooden-wheel", "Wheel_01_None_color.jpg", "Wheel_01_None_rough.jpg",
                    None, "Wheel_01_None_nmap.jpg")
    adopt(wheel, "wheel", [wood], Matrix.Translation(centre) @ stand @ Matrix.Scale(size, 4) @ middle)


def sack(bm, base, size, lean, yaw, tied=True):
    """A full grain sack: the grain settles, so it slumps into a wide, soft-cornered lump that
    spreads at the foot and stays full almost to the top.

    A tied sack is gathered in creases to a short neck, tied with twine, and the loose cloth
    above it fans out in a ragged ruffle that flops over to one side. An untied one has its
    mouth pinched flat, like a paper bag folded shut, into a ridge that leans over. Returns
    its verts."""
    w, d, h = 0.5 * size, 0.38 * size, (0.46 if tied else 0.5) * size
    before = set(bm.verts)
    # v runs up the sides from 2 cm at the foot, so the burlap graph's grime, at the bottom of its image, is at the
    # bottom of the sack.
    block(bm, (0.0, 0.0, h / 2), (w / 2, d / 2, h / 2), 0, bevel=0.09 * size, warp=0.015, rough=0.004,
          bulge=0.035 * size, step=0.035, uv_z=0.02)
    body = [v for v in bm.verts if v not in before]
    seed = Vector((rng.uniform(-50, 50), rng.uniform(-50, 50), 0.0))
    folds = rng.randint(7, 10)
    turn = rng.uniform(0, math.pi)
    fold_side = rng.choice((-1.0, 1.0))
    for v in body:
        x, y, z = v.co
        t = max(0.0, min(1.0, z / h))
        # Sat down flat, spread a little at the foot and round at the hips.
        if t < 0.06:
            z = h * 0.06 * (0.3 + 0.7 * t / 0.06)
        spread = 1.0 + 0.16 * (1.0 - t) ** 2
        if tied:
            # The grain fills it to the shoulder, and only above that is the cloth drawn in to
            # the neck, in creases.
            gather = smoothstep(0.74, 1.0, t) ** 1.2
            angle = math.atan2(y / d, x / w)
            crease = 1.0 + 0.14 * gather * math.sin(angle * folds + turn + 2.0 * noise.noise(Vector((x * 4, y * 4, 3.0)) + seed))
            k = spread * (1.0 - 0.8 * gather) * crease
            x, y = x * k, y * k
            # The shoulders slump a little: the further from the middle, the lower.
            r = math.hypot(x / w, y / d)
            z -= 0.1 * h * smoothstep(0.55, 0.95, t) * min(1.0, r * 2.2)
        else:
            # The mouth is pressed flat across its depth into a ridge that runs the sack's
            # width. The corners of the ridge droop, and the ridge folds over to one side.
            pinch = smoothstep(0.62, 1.0, t)
            x *= spread * (1.0 + 0.08 * pinch)
            y *= spread * (1.0 - 0.88 * pinch)
            z -= 0.16 * h * pinch * (x / (0.5 * w)) ** 2
            y += fold_side * 0.12 * h * pinch ** 2.2
            z -= 0.05 * h * pinch ** 3
            # Creases run down from the ridge into the belly.
            z += 0.012 * size * pinch * math.sin(x / w * folds * 3.0 + turn)
        # Loose cloth wrinkles, mostly across.
        z += 0.008 * size * noise.noise(Vector((x * 9.0, y * 9.0, z * 30.0)) + seed)
        v.co = Vector((x + lean[0] * t * t * h, y + lean[1] * t * t * h, z))
    if tied:
        top = Vector((lean[0] * h, lean[1] * h, h * 0.97))
        # The neck and the twine round it, and the loose cloth above fanning out into a ragged
        # ruffle that flops over to one side.
        mark = set(bm.verts)
        neck = lathe(bm, [(0.075 * size, -0.05), (0.04 * size, 0.0), (0.042 * size, 0.03 * size), (0.08 * size, 0.06 * size),
                          (0.125 * size, 0.09 * size), (0.14 * size, 0.105 * size), (0.11 * size, 0.115 * size),
                          (0.05 * size, 0.1 * size)], 20, tuple(top), 0, close_top=True)
        flop = rng.uniform(0, 2 * math.pi)
        for v in bm.verts:
            if v in mark:
                continue
            rel = v.co - top
            above = max(0.0, rel.z - 0.025 * size) / (0.09 * size)
            # Ragged: the cloth's rim comes and goes in big folds round the neck.
            a = math.atan2(rel.y, rel.x)
            rag = 1.0 + 0.3 * min(1.0, above) * (math.sin(a * 5 + turn) * 0.6 + noise.noise(Vector((math.cos(a) * 2, math.sin(a) * 2, 7.0)) + seed))
            v.co.x = top.x + rel.x * rag + math.cos(flop) * 0.07 * size * above * above
            v.co.y = top.y + rel.y * rag + math.sin(flop) * 0.07 * size * above * above
            v.co.z += -0.05 * size * above * above * (0.5 + 0.5 * math.cos(a - flop)) + 0.012 * size * math.sin(a * 5 + turn) * min(1.0, above)
        twine = lathe(bm, [(0.04 * size, 0.0), (0.05 * size, 0.006), (0.05 * size, 0.02), (0.04 * size, 0.026)], 20, tuple(top), 1)
        for f in neck + twine:
            f.smooth = True
        box_uv(bm, neck)
    verts = [v for v in bm.verts if v not in before]
    spin = Matrix.Rotation(yaw, 4, "Z")
    for v in verts:
        v.co = spin @ v.co + base
    return verts


def build_sacks(sc, M):
    """Two sacks propped against the crate's side and one lying on the deck in front."""
    bm = bmesh.new()
    deck = DECK_TOP
    sack(bm, Vector((6.3, 2.95, deck)), 1.0, (0.16, 0.04), 0.3)
    sack(bm, Vector((6.34, 2.42, deck)), 0.9, (0.22, 0.1), -0.4, tied=False)
    # One lying on its side: built standing, tipped over about its width so the flattened
    # mouth lies flat, then squashed down, because a laid sack settles under its own weight.
    laid = sack(bm, Vector((0.0, 0.0, 0.0)), 0.85, (0.0, 0.0), 0.0, tied=False)
    tip = Matrix.Rotation(math.radians(-86), 4, "X")
    spin = Matrix.Rotation(math.radians(-115), 4, "Z")
    for v in laid:
        v.co = tip @ v.co
    low = min(v.co.z for v in laid)
    for v in laid:
        v.co.z = (v.co.z - low) * 0.72
        v.co.x *= 1.12
        v.co = spin @ v.co + Vector((6.75, 1.85, deck - 0.004))
    bm.normal_update()
    emit(sc, "sacks", bm, [M["sack"], M["twine"]])


# Where the staves' graph boards start, up from the barrel's foot, and how tall one is: from the
# middle of the second hoop to the middle of the top one, for the barrel's height of 1.6 m.
STAVE_FROM = 1.6 * 0.27
STAVE_SPAN = 1.6 * (0.94 - 0.27)


def build_barrel(sc, M):
    bm = bmesh.new()
    cx, cy = 0.2, WALL_Y - 0.75
    height, r_end, r_mid = 1.6, 0.46, 0.61
    # Few, broad staves, about 32 cm across at the bulge, as an old barrel's are.
    staves = 12
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
        start = board_start(s)
        for f in faces:
            f.material_index = 0
            f.smooth = True
            f.normal_update()
            top = f.normal.z > 0.7
            for loop in f.loops:
                co = loop.vert.co
                a = math.atan2(co.y - cy, co.x - cx)
                # Each stave wears one of the wood graph's six planks, stretched across its width,
                # the second six mirrored so no two staves match.
                across = min(1.0, ((a - a0) % (2 * math.pi)) / (a1 - a0))
                if s >= 6:
                    across = 1.0 - across
                v = (s % 6) / 6.0 + 0.012 + across * (1 / 6.0 - 0.024)
                if top:
                    # The sawn top of the stave is end grain.
                    loop[uv].uv = (start + 1.0 - END_GRAIN, v)
                else:
                    # One graph board runs from the second hoop to the top one, so its joints lie
                    # under those hoops and its rust bands (see barrel-staves.material.json) just
                    # under the first and third.
                    loop[uv].uv = (start + (co.z - z0 - STAVE_FROM) / STAVE_SPAN, v)
    # The head, set well down inside the top so the staves' ends stand round it as a rim.
    head = lathe(bm, [(0.0, height - 0.07), (r_end - 0.025, height - 0.07)], 32, (cx, cy, z0), 2)
    for f in head:
        plank_uv(bm, [f], "x", 2, 0.0)
    # The hoops: broad bands of dark iron flaking with rust, standing well out from the staves,
    # following the bulge, with their edges rounded over.
    for zc, width in ((height * 0.06, 0.07), (height * 0.27, 0.06), (height * 0.73, 0.06), (height * 0.94, 0.07)):
        za, zb = zc - width / 2, zc + width / 2
        ra, rb = radius(za) + 0.003, radius(zb) + 0.003
        band = [(ra, za), (ra + 0.012, za + 0.005), (rb + 0.012, zb - 0.005), (rb, zb)]
        hoop = lathe(bm, band, 48, (cx, cy, z0), 1)
        for f in hoop:
            f.smooth = True
    emit(sc, "barrel", bm, [M["staves"], M["hoop"], M["wood"]])


# ---------------------------------------------------------------------------------------
# Ivy: vines up the wall with leaves along them

def build_ivy(sc, M):
    leaves = bmesh.new()
    vines = bmesh.new()
    # The surface each leaf grows on, one per card, for its bent normal.
    surfaces = []

    def leaf(foot, normal, reference, roll=(180.0, 25.0)):
        ivy_leaf(leaves, foot, normal, reference, roll)
        surfaces.append(normal.normalized())

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
            # The old star leaves' draws stay, so everything made after the ivy comes out as it did:
            # their size, where round the stem, how far out, how far off the wall, their turn and
            # their tilt. The cards draw from their own numbers.
            if not (k % 2 and depth > 0):
                for _ in range(2 if depth == 0 else 1):
                    for _ in range(6):
                        rng.random()
            # Leaves along the stem, overlapping into a mat that hides it: thick and wide low on the
            # stem, where it is old, and thinning out towards its growing tip, where the stem shows.
            # Their normals are bent out of the wall (see emit), so a mat this thick still reads green.
            along = k / max(len(pts) - 1, 1)
            full = (17.0, 11.0, 6.0)[depth]
            reach = (0.29, 0.2, 0.12)[depth] * (1.0 - 0.45 * along)
            for _ in range(int(full * (1.0 - 0.6 * along) + ivy_rng.random())):
                a = ivy_rng.uniform(0, 2 * math.pi)
                r = reach * math.sqrt(ivy_rng.uniform(0.02, 1.0))
                leaf(Vector((px + math.cos(a) * r, wall_y - 0.02 - ivy_rng.uniform(0.0, 0.035),
                             pz + math.sin(a) * r)), Vector((0, -1, 0)), Vector((0, 0, 1)))

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
            # The old leaf's size, turn, tilt and ten corner heights.
            for _ in range(13):
                rng.random()
            # Lying on the stones, the tip any way round.
            leaf(Vector((x, y, top + 0.02 + max(0.0, z - top) * 0.3)), Vector((0, 0, 1)), Vector((1, 0, 0)),
                     roll=(0.0, 180.0))
        else:
            # Hanging down the face, further where it's thickest.
            reach = 0.6 * ((y - (WALL_Y - 4.5)) / 4.4)
            z = top - rng.uniform(0.0, reach)
            x = DRY_X + rng.uniform(0.02, 0.08)
            # The old leaf's size, turn and tilt.
            for _ in range(3):
                rng.random()
            leaf(Vector((x, y, z)), Vector((1, 0, 0)), Vector((0, 0, 1)))
    # More of the same from the cards' own numbers, so it is a mat rather than scattered leaves:
    # draped over the top's courtyard edge, facing up and out, and hanging down the face.
    for _ in range(700):
        y = ivy_rng.uniform(WALL_Y - 4.5, WALL_Y - 0.1)
        thick = (y - (WALL_Y - 4.5)) / 4.4
        if ivy_rng.random() > thick ** 0.7:
            continue
        roll = ivy_rng.random()
        if roll < 0.3:
            x = DRY_X - ivy_rng.uniform(0.0, DRY_THICKNESS * 0.6)
            leaf(Vector((x, y, top + 0.02)), Vector((0, 0, 1)), Vector((1, 0, 0)), roll=(0.0, 180.0))
        elif roll < 0.55:
            # Over the edge, the leaves facing half up and half out into the yard.
            leaf(Vector((DRY_X + 0.03, y, top - ivy_rng.uniform(0.0, 0.06))), Vector((0.7, 0, 0.7)),
                     Vector((0, 0, 1)))
        else:
            z = top - ivy_rng.uniform(0.0, 0.7 * thick) ** 1.3
            leaf(Vector((DRY_X + ivy_rng.uniform(0.02, 0.07), y, z)), Vector((1, 0, 0)), Vector((0, 0, 1)))

    emit(sc, "ivy_vines", vines, [M["vine"]], smooth=True)
    emit(sc, "ivy_leaves", leaves, [M["ivy_small"], M["ivy_large"], M["ivy_dead"]], bend=surfaces)


# The leaf, blade, web and dead leaf graphs' widths in metres, as their Build() sets them, and how
# many blades stand across each grass species' image (GrassBlade.Of). art/cards.py uses the same.
IVY = 0.2
BLADE = 0.3
COBWEB = 0.5
DEAD_LEAF = 0.15
COLUMNS = {"meadow": 24, "broad": 12, "dry": 16}

# The ivy cards' own random numbers, so their sizes and turns move nothing else in the scene.
ivy_rng = random.Random(41)


def card(bm, foot, right, up, width, height, mat, graph, u0=0.0, u1=None):
    """A flat card standing on the middle of its foot, width along right and height along up,
    wearing a card graph whose image is graph metres across: its foot on the image's foot and its
    top on the image's top, and across from u0 to u1 (the whole image unless given). It faces
    right x up. The glTF export flips v, so the foot's v of 0 becomes 1, as art/cards.py writes."""
    u1 = graph if u1 is None else u1
    foot, right, up = Vector(foot), Vector(right).normalized(), Vector(up).normalized()
    half = right * (width / 2)
    corners = [foot - half, foot + half, foot + half + up * height, foot - half + up * height]
    face = bm.faces.new([bm.verts.new(tuple(c)) for c in corners])
    face.material_index = mat
    uv = uv_layer(bm)
    for loop, t in zip(face.loops, ((u0, 0.0), (u1, 0.0), (u1, graph), (u0, graph))):
        loop[uv].uv = t
    return face


def ivy_leaf(bm, foot, normal, reference, roll=(180.0, 25.0)):
    """One ivy leaf card on its stalk at foot, on a surface facing normal: 6 to 12 cm, in two
    sizes, its tip turned roll[0] degrees from reference about the normal, give or take roll[1],
    and tilted 10 to 40 degrees off the surface. 8 in 100 are dead and brown. On a wall the
    reference is up, so the tip hangs down and out, as a climbing ivy's leaves do."""
    large = ivy_rng.random() < 0.5
    size = ivy_rng.uniform(0.09, 0.12) if large else ivy_rng.uniform(0.06, 0.085)
    turn = math.radians(roll[0] + ivy_rng.uniform(-roll[1], roll[1]))
    lean = math.radians(ivy_rng.uniform(10, 40))
    n = normal.normalized()
    up0 = (reference - n * reference.dot(n)).normalized()
    side0 = up0.cross(n)
    tip = up0 * math.cos(turn) - side0 * math.sin(turn)
    right = side0 * math.cos(turn) + up0 * math.sin(turn)
    up = tip * math.cos(lean) + n * math.sin(lean)
    mat = 2 if ivy_rng.random() < 0.08 else 1 if large else 0
    # The leaf fills about three quarters of its image, stalk to tip, so the card is a third bigger.
    card(bm, foot, right, up, size / 0.75, size / 0.75, mat, IVY)


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
        # The wash stops at the string course, which stands out of it, and the dentils under it.
        band = smoothstep(BAND_Z0 - COURSE - 0.03, BAND_Z0 - COURSE, z) * smoothstep(BAND_Z1 + 0.03, BAND_Z1, z)
        return fall * 0.9 + n * 0.7 - 0.25 - 2.0 * band

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


def dead_leaf(bm, centre, length, angle, mat, floor):
    """A fallen leaf: a narrow, pointed blade with a midrib, dried so it cups along the rib,
    crinkles and lifts at the tip. floor(x, y) is the height of whatever it lies on, so it
    drapes over the lumps instead of floating on them."""
    c, s = math.cos(angle), math.sin(angle)
    curl = rng.uniform(0.1, 0.3) * length
    lift = rng.uniform(0.0, 0.25) * length
    seed = Vector((rng.uniform(-50, 50), rng.uniform(-50, 50), 0.0))
    width = rng.uniform(0.2, 0.27)

    def at(u, v):
        # u along the leaf from stalk (0) to tip (1), v across it from -1 to 1. Widest a third
        # of the way along, then drawing out to a point.
        shape = math.sin(math.pi * min(u, 0.999) ** 0.65) * (1.0 - 0.35 * u)
        lx, ly = (u - 0.5) * length, v * length * width * shape
        x, y = centre[0] + lx * c - ly * s, centre[1] + lx * s + ly * c
        crinkle = 0.12 * length * noise.noise(Vector((u * 5, v * 2, 0.0)) + seed) * abs(v)
        z = curl * v * v + lift * max(0.0, u - 0.65) ** 2 / 0.12 + max(0.0, crinkle)
        return bm.verts.new((x, y, floor(x, y) + 0.008 + z))

    steps = 7
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
    sx, sy = centre[0] - 0.68 * length * c, centre[1] - 0.68 * length * s
    stalk = bm.verts.new((sx, sy, floor(sx, sy) + 0.01))
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
        if on_deck:
            def floor(px, py):
                return DECK_TOP
        elif on_path:
            # The stones' tops: a little above the mud packed between them, and flat.
            here = ground_height(x, y) + 0.014

            def floor(px, py):
                return here
        else:
            floor = ground_height
        dead_leaf(bm, (x, y, 0.0), rng.uniform(0.07, 0.13), rng.uniform(0, 2 * math.pi), rng.randrange(2), floor)
        placed += 1
    emit(sc, "fallen_leaves", bm, [M["dead_leaf"], M["dead_leaf_orange"]])


# Each grass species' card material slot in the grass mesh, and how wide its cards are, in metres.
# A card is one column of the species' image, so the blade in it is a little over half its width,
# or about a seventh for the dry stalks.
GRASS_SLOTS = {"meadow": 0, "broad": 1, "dry": 2}
GRASS_WIDTHS = {"meadow": (0.012, 0.02), "broad": (0.025, 0.04), "dry": (0.015, 0.025)}


def tuft(bm, x, y, z, height, species, count=None):
    """A tuft of blade cards from one spot, each one blade of the species' image, turned round
    the tuft by the golden angle and leaning out 4 to 22 degrees. height is the tallest blade.
    One card in ten is a dry stalk with a seed head standing a little above the rest; a dry tuft
    is a mix of dry stalks and meadow blades, as grass going over is."""
    count = count or int(10 + 55 * height)
    turn0 = rng.uniform(0, 2 * math.pi)
    # Broad grass sprawls from a wider foot and flops further over than the upright meadow grass.
    spread, leans = (0.06, (8, 40)) if species == "broad" else (0.035, (4, 26))
    for i in range(count):
        kind = species
        if species == "dry":
            kind = "dry" if rng.random() < 0.6 else "meadow"
        elif rng.random() < 0.1:
            kind = "dry"
        elif species == "broad" and rng.random() < 0.3:
            # Meadow blades come up through the broad grass, so a tuft isn't all one green.
            kind = "meadow"
        columns = COLUMNS[kind]
        column = BLADE / columns
        c = rng.randrange(columns)
        width = rng.uniform(*GRASS_WIDTHS[kind])
        # The blade fills about nine tenths of its column's height. A seed head stands up out of the tuft.
        tall = height * (rng.uniform(0.9, 1.15) if kind == "dry" and species != "dry" else rng.uniform(0.5, 1.0))
        turn = turn0 + i * 2.39996 + rng.uniform(-0.3, 0.3)
        lean = math.radians(rng.uniform(*leans))
        out = Vector((math.cos(turn), math.sin(turn), 0.0))
        right = Vector((-math.sin(turn), math.cos(turn), 0.0))
        up = out * math.sin(lean) + Vector((0, 0, math.cos(lean)))
        foot = Vector((x, y, z - 0.01)) + out * rng.uniform(0, spread)
        card(bm, foot, right, up, width, tall / 0.9, GRASS_SLOTS[kind], BLADE, c * column, (c + 1) * column)


def growth(x, y):
    """Where grass has taken hold: high in broad patches, nothing in between."""
    return smoothstep(-0.15, 0.35, noise.noise(Vector((x * 0.9, y * 0.9, 11.0))))


def trodden(x, y):
    """Whether (x, y) is on the walk the footprints show, where feet keep the mud bare."""
    return any((x - px) ** 2 + (y - py) ** 2 < 0.35 ** 2 for px, py, _, _ in FOOTPRINTS)


def clump(bm, x, y, size, species):
    """A patch of grass: a big tuft or two with smaller ones round them, 0.1 to 0.5 m tall."""
    tallest = {"meadow": (0.25, 0.5), "broad": (0.2, 0.38), "dry": (0.25, 0.5)}[species]
    for k in range(rng.randint(3, 6)):
        a = rng.uniform(0, 2 * math.pi)
        r = 0.0 if k == 0 else rng.uniform(0.06, 0.3) * size
        px, py = x + math.cos(a) * r, y + math.sin(a) * r
        if puddle_depth(px, py) > 0.05 or rut_depth(px, py) > 0.1 or path_distance(px, py) < PATH_HALF_WIDTH - 0.15:
            continue
        height = rng.uniform(*tallest) * min(1.0, size) * (1.0 if k == 0 else rng.uniform(0.4, 0.8))
        tuft(bm, px, py, ground_height(px, py), max(0.1, height), species)


def build_grass(sc, M):
    """Grass where feet and wheels don't go, as blade cards of three species: long meadow grass
    at the foot of both walls and thick in the corners, short broad lush grass in a few dense
    patches on the puddle's wet bank, and dry stalks going to seed at the deck's edge. The trodden
    mud and the path stay bare, but for the joints between the flagstones where nobody steps and
    now and then a small tuft at a flagstone's edge."""
    bm = bmesh.new()
    spots = []
    # Along the foot of the plinth and the low wall, thick where it has taken hold.
    for _ in range(170):
        x, y = rng.uniform(DRY_X + 0.2, WALL_X1 - 0.5), WALL_Y - rng.uniform(0.3, 0.6)
        if rng.random() < 0.15 + growth(x, y):
            spots.append((x, y, rng.uniform(0.8, 1.4)))
    for _ in range(80):
        x, y = DRY_X + rng.uniform(0.12, 0.35), rng.uniform(WALL_Y - DRY_LENGTH, WALL_Y - 0.3)
        if rng.random() < growth(x, y):
            spots.append((x, y, rng.uniform(0.9, 1.6)))
    # Thick in the corners, where nobody walks: where the low wall meets the building, at the low
    # wall's far end, and where the deck meets the building.
    for cx, cy in ((DRY_X + 0.3, WALL_Y - 0.35), (DRY_X + 0.3, WALL_Y - DRY_LENGTH + 0.4), (DECK[1] + 0.3, WALL_Y - 0.35)):
        for _ in range(rng.randint(3, 5)):
            spots.append((cx + rng.uniform(-0.2, 0.25), cy + rng.uniform(-0.25, 0.2), rng.uniform(0.9, 1.4)))
    for x, y, size in spots:
        clump(bm, x, y, size, "meadow")
    # In the joints between flagstones, away from the middle of the path where feet go.
    for x, y in rng.sample(JOINTS, min(len(JOINTS), 700)):
        if path_distance(x, y) > 0.55 and rng.random() < 0.12 * (0.3 + growth(x, y)):
            tuft(bm, x, y, ground_height(x, y) + 0.01, rng.uniform(0.1, 0.18), "meadow")
    # Now and then a small tuft right at a flagstone's edge, where the mower of feet misses it.
    # Further out is the trodden mud, which stays bare.
    for _ in range(40):
        path = rng.choice(PATHS)
        t = rng.random()
        k = min(int(t * (len(path) - 1)), len(path) - 2)
        f = t * (len(path) - 1) - k
        (ax, ay), (bx, by) = path[k], path[k + 1]
        dx, dy = bx - ax, by - ay
        length = math.hypot(dx, dy)
        side = rng.choice((-1, 1)) * (PATH_HALF_WIDTH + rng.uniform(-0.05, 0.05))
        x, y = ax + dx * f - dy / length * side, ay + dy * f + dx / length * side
        # Not out in the middle of the other arm, nor where the footprints go.
        if path_distance(x, y) > PATH_HALF_WIDTH - 0.1 and not trodden(x, y) and rng.random() < 0.05 + 0.2 * growth(x, y):
            tuft(bm, x, y, ground_height(x, y), rng.uniform(0.1, 0.2), "meadow")
    # Round the deck, dry: the boards keep the rain off its edge.
    for _ in range(20):
        if rng.random() < 0.5:
            x, y = DECK[0] - rng.uniform(0.05, 0.3), rng.uniform(DECK[2], DECK[3])
        else:
            x, y = rng.uniform(DECK[0], DECK[1]), DECK[2] - rng.uniform(0.05, 0.3)
        clump(bm, x, y, rng.uniform(0.7, 1.3), "dry")
    # On the puddle's bank, broad and lush, where the mud stays wet: just outside the water's
    # edge, off the paving and away from the footprints.
    # It grows in a few thick patches, each a dense clump of clumps where the noise says it took
    # hold, not spread round the bank, and some of the patches are meadow grass gone lush.
    def on_bank(x, y):
        return 1.15 < puddle_shape(x, y) < 1.9 and path_distance(x, y) > PATH_HALF_WIDTH + 0.05 and not trodden(x, y)

    patches = 0
    for _ in range(300):
        if patches >= 5:
            break
        a = rng.uniform(0, 2 * math.pi)
        out = rng.uniform(1.1, 1.45)
        x, y = PUDDLE[0] + math.cos(a) * PUDDLE[2] * out, PUDDLE[1] + math.sin(a) * PUDDLE[3] * out
        if not on_bank(x, y) or rng.random() > 0.1 + growth(x * 1.7, y * 1.7):
            continue
        species = "broad" if rng.random() < 0.65 else "meadow"
        for _ in range(rng.randint(6, 10)):
            b = rng.uniform(0, 2 * math.pi)
            r = 0.35 * math.sqrt(rng.random())
            px, py = x + math.cos(b) * r, y + math.sin(b) * r
            if on_bank(px, py):
                clump(bm, px, py, rng.uniform(0.6, 1.1), species)
        patches += 1
    # Lit like the ground they grow from, with a little of each blade's own turn.
    emit(sc, "grass", bm, [M["meadow"], M["broad"], M["dry"]], bend=Vector((0, 0, 1)), share=0.8)


def build_weeds(sc, M):
    """Weeds where the path meets something: a single blade or two, or a dandelion's rosette of
    five broad leaves lying almost flat, in 20 of the flagstones' joints and along the foot of the
    plinth. Their own random numbers, so they move nothing else."""
    weed_rng = random.Random(42)
    bm = bmesh.new()

    def rosette(x, y, z):
        turn0 = weed_rng.uniform(0, 2 * math.pi)
        for k in range(5):
            turn = turn0 + k * 2 * math.pi / 5 + weed_rng.uniform(-0.2, 0.2)
            lean = math.radians(weed_rng.uniform(60, 75))
            out = Vector((math.cos(turn), math.sin(turn), 0.0))
            right = Vector((-math.sin(turn), math.cos(turn), 0.0))
            up = out * math.sin(lean) + Vector((0, 0, math.cos(lean)))
            c = weed_rng.randrange(COLUMNS["broad"])
            column = BLADE / COLUMNS["broad"]
            card(bm, Vector((x, y, z)), right, up, weed_rng.uniform(0.03, 0.04), weed_rng.uniform(0.1, 0.16), 1,
                 BLADE, c * column, (c + 1) * column)

    def blades(x, y, z):
        for _ in range(weed_rng.randint(1, 3)):
            turn = weed_rng.uniform(0, 2 * math.pi)
            lean = math.radians(weed_rng.uniform(5, 30))
            out = Vector((math.cos(turn), math.sin(turn), 0.0))
            right = Vector((-math.sin(turn), math.cos(turn), 0.0))
            up = out * math.sin(lean) + Vector((0, 0, math.cos(lean)))
            c = weed_rng.randrange(COLUMNS["meadow"])
            column = BLADE / COLUMNS["meadow"]
            card(bm, Vector((x, y, z - 0.005)), right, up, weed_rng.uniform(0.012, 0.018), weed_rng.uniform(0.08, 0.2), 0,
                 BLADE, c * column, (c + 1) * column)

    # The joints on the paving, where nobody's boots wear them away.
    joints = [(x, y) for x, y in JOINTS if 0.25 < path_distance(x, y) < PATH_HALF_WIDTH - 0.1 and puddle_shape(x, y) > 1.3]
    for x, y in weed_rng.sample(joints, min(20, len(joints))):
        z = ground_height(x, y) + 0.012
        if weed_rng.random() < 0.4:
            rosette(x, y, z)
        else:
            blades(x, y, z)
    # Tight against the plinth's foot, left of the deck and clear of the barrel.
    placed = 0
    while placed < 9:
        x, y = weed_rng.uniform(-1.3, 5.9), WALL_Y - 0.37 - weed_rng.uniform(0.0, 0.04)
        if -0.5 < x < 0.9:
            continue
        placed += 1
        z = ground_height(x, y)
        if weed_rng.random() < 0.5:
            rosette(x, y, z + 0.005)
        else:
            blades(x, y, z)
    emit(sc, "weeds", bm, [M["meadow"], M["broad"]], bend=Vector((0, 0, 1)), share=0.8)


def build_debris(sc, M):
    """More of what blows and falls into a yard: 40 dead leaf cards in three tints, 12 twigs,
    six more bits of broken brick, and pebbles half drowned at the puddle's edge. It all draws
    from its own random numbers, and puts the scene's back as it found them, so nothing else
    moves."""
    saved = rng.getstate()
    rng.seed(45)
    leaf_rng = random.Random(43)

    def resting(x, y):
        """How high the floor is under (x, y): the deck's boards, a flagstone's top or the mud."""
        if DECK[0] < x < DECK[1] and DECK[2] < y < DECK[3]:
            return DECK_TOP
        if path_distance(x, y) < PATH_HALF_WIDTH - 0.05:
            return ground_height(x, y) + 0.014
        return ground_height(x, y)

    # Dead leaves lying flat, each on the highest point under it so it doesn't sink into a clod.
    leaves = bmesh.new()
    placed = 0
    while placed < 40:
        roll = leaf_rng.random()
        if roll < 0.4:
            x, y = leaf_rng.uniform(DRY_X + 0.2, 6.0), WALL_Y - leaf_rng.uniform(0.3, 1.2)
        elif roll < 0.6:
            x, y = DRY_X + leaf_rng.uniform(0.1, 0.6), leaf_rng.uniform(-3.0, WALL_Y - 0.3)
        else:
            x, y = leaf_rng.uniform(-1.0, 7.5), leaf_rng.uniform(-3.5, WALL_Y - 0.4)
        if puddle_shape(x, y) < 1.2:
            continue
        size = leaf_rng.uniform(0.08, 0.13) / 0.85
        yaw = leaf_rng.uniform(0, 2 * math.pi)
        lift = leaf_rng.uniform(0.0, 0.25)
        up = Vector((math.cos(yaw), math.sin(yaw), lift)).normalized()
        right = Vector((math.sin(yaw), -math.cos(yaw), 0.0))
        foot = Vector((x, y, 0.0)) - up * (size / 2)
        corners = [foot + right * sx * size / 2 + up * sy * size for sx in (-1, 1) for sy in (0, 1)]
        foot.z = max(resting(c.x, c.y) for c in corners) + 0.005
        card(leaves, foot, right, up, size, size, leaf_rng.randrange(3), DEAD_LEAF)
        placed += 1
    emit(sc, "leaf_litter", leaves, [M["leaf_tan"], M["leaf_dark"], M["leaf_red"]])

    # Twigs off the tree beyond the low wall: thin crooked sticks lying on the ground.
    twigs = bmesh.new()
    for _ in range(12):
        x, y = leaf_rng.uniform(-1.3, 5.5), leaf_rng.uniform(-3.0, WALL_Y - 0.45)
        if puddle_shape(x, y) < 1.2:
            x, y = DRY_X + leaf_rng.uniform(0.2, 0.7), leaf_rng.uniform(-2.0, WALL_Y - 0.5)
        r = leaf_rng.uniform(0.003, 0.006)
        length = leaf_rng.uniform(0.15, 0.4)
        heading = leaf_rng.uniform(0, 2 * math.pi)
        pts = []
        for k in range(4):
            if k:
                heading += leaf_rng.uniform(-0.35, 0.35)
                x, y = x + math.cos(heading) * length / 3, y + math.sin(heading) * length / 3
            pts.append((x, y, resting(x, y) + r * 0.8))
        tube(twigs, pts, r, 0, segments=5)
        # A side shoot off one in two.
        if leaf_rng.random() < 0.5:
            a = Vector(pts[1])
            d = heading + leaf_rng.choice((-1, 1)) * leaf_rng.uniform(0.5, 0.9)
            b = a + Vector((math.cos(d), math.sin(d), 0.0)) * length * 0.3
            b.z = resting(b.x, b.y) + r * 0.6
            tube(twigs, [tuple(a), tuple(b)], r * 0.6, 0, segments=4)
    box_uv(twigs, twigs.faces[:])
    emit(sc, "twigs", twigs, [M["bark"]], smooth=True)

    # Broken brick off the walls, half sunk in the mud along the wall's foot and by the path.
    bricks = bmesh.new()
    placed = 0
    while placed < 6:
        if placed < 3:
            x, y = leaf_rng.uniform(1.0, 5.8), WALL_Y - leaf_rng.uniform(0.45, 0.9)
        else:
            x, y = leaf_rng.uniform(-1.2, 6.0), leaf_rng.uniform(-3.5, 1.0)
        if puddle_shape(x, y) < 1.3 or path_distance(x, y) < PATH_HALF_WIDTH + 0.08 or footprint_depth(x, y) > 0.05:
            continue
        half = (leaf_rng.uniform(0.04, 0.1), 0.05, 0.033)
        sink = half[2] * leaf_rng.uniform(0.7, 1.2)
        chips = [((leaf_rng.choice((-1, 1)), leaf_rng.choice((-1, 1)), 1), min(half) * leaf_rng.uniform(0.6, 1.2))]
        block(bricks, (x, y, ground_height(x, y) + half[2] - sink), half, leaf_rng.randrange(2), bevel=0.008,
              yaw=leaf_rng.uniform(0, math.pi), roll=(leaf_rng.uniform(-0.15, 0.15), leaf_rng.uniform(-0.15, 0.15)),
              warp=0.006, rough=0.004, chips=chips, step=0.03)
        placed += 1
    split_sharp(bricks)
    emit(sc, "brick_bits", bricks, [M["arch1"], M["arch2"]])

    # Pebbles at the puddle's edge, their middles at the water, so they stand half out of it.
    pebbles = bmesh.new()
    placed = 0
    for _ in range(400):
        if placed >= 11:
            break
        a = leaf_rng.uniform(0, 2 * math.pi)
        out = leaf_rng.uniform(0.8, 1.0)
        x, y = PUDDLE[0] + math.cos(a) * PUDDLE[2] * out, PUDDLE[1] + math.sin(a) * PUDDLE[3] * out
        s = leaf_rng.uniform(0.04, 0.08)
        # Only where the bank has dropped just under the water, so each sits on the mud, its
        # foot buried, and none floats over a deeper hollow.
        ground = ground_height(x, y)
        if not -0.045 - 0.2 * s < ground < -0.05:
            continue
        rock(pebbles, (x, y, -0.045 + s * 0.03), (s * leaf_rng.uniform(1.0, 1.5), s, s * 0.6), 0,
             power=2.2, bumps=0.2, yaw=leaf_rng.uniform(0, math.pi), segments=(10, 7))
        placed += 1
    # Their own stone material, which the sidecar gives puddles, so what's under the water looks wet.
    emit(sc, "wet_pebbles", pebbles, [M["wet_stone"]])
    rng.setstate(saved)


def web(bm, corner, along, down, size):
    """A triangular cobweb card in a corner: its right angle at corner, one side running size
    metres along the top of the corner and the other size metres down its side. The cobweb graph
    puts its right angle at the image's top left, so the image spans the card whatever its size."""
    corner, along, down = Vector(corner), Vector(along).normalized(), Vector(down).normalized()
    verts = [bm.verts.new(tuple(corner + down * size)), bm.verts.new(tuple(corner + along * size)), bm.verts.new(tuple(corner))]
    face = bm.faces.new(verts)
    face.material_index = 0
    uv = uv_layer(bm)
    for loop, t in zip(face.loops, ((0.0, 0.0), (COBWEB, COBWEB), (0.0, COBWEB))):
        loop[uv].uv = t


def build_cobwebs(sc, M):
    """Old webs: one in each upper corner of the window's square panes, just in front of the
    glazing bars, and one strung from the pier's side to the drainpipe under its lowest collar."""
    bm = bmesh.new()
    x0, x1 = WIN_X - WIN_W / 2, WIN_X + WIN_W / 2
    fw, bar, t = 0.075, 0.04, 0.07
    # The bars' fronts, as build_window lays them, and a few millimetres in front.
    y = WALL_Y + REVEAL - t * 0.8 - 0.004
    z = SPRING_Z - bar / 2
    web(bm, (x0 + fw, y, z), (1, 0, 0), (0, 0, -1), 0.3)
    web(bm, (x1 - fw, y, z), (-1, 0, 0), (0, 0, -1), 0.26)
    # Behind the pipe, from the pier's side, under the ears that hold the lowest collar.
    web(bm, (PIER_X1, WALL_Y - 0.035, 1.5 - 0.037), (1, 0, 0), (0, 0, -1), 0.24)
    emit(sc, "cobwebs", bm, [M["cobweb"]])


def stud(bm, centre, normal, radius, proud, slot, nail_random):
    """A small nail head: an eight-sided disc of radius, standing proud of the surface at centre
    that faces normal, and sunk a millimetre into it. Each takes its own patch of its material."""
    n = Vector(normal).normalized()
    t = n.orthogonal().normalized()
    b = n.cross(t)
    centre = Vector(centre)
    du, dv = nail_random.uniform(0, 0.5), nail_random.uniform(0, 0.5)
    spin = nail_random.uniform(0, math.pi / 4)
    rings = []
    for height in (-0.001, proud):
        rings.append([bm.verts.new(tuple(centre + n * height + (t * math.cos(a) + b * math.sin(a)) * radius))
                      for a in (spin + 2 * math.pi * k / 8 for k in range(8))])
    faces = [bm.faces.new(rings[1])]
    for k in range(8):
        k1 = (k + 1) % 8
        faces.append(bm.faces.new((rings[0][k], rings[0][k1], rings[1][k1], rings[1][k])))
    uv = uv_layer(bm)
    for f in faces:
        f.material_index = slot
        f.smooth = False
        for loop in f.loops:
            rel = loop.vert.co - centre
            loop[uv].uv = (rel.dot(t) + du, rel.dot(b) + dv)
    bmesh.ops.recalc_face_normals(bm, faces=faces)


# ---------------------------------------------------------------------------------------

# A tree beyond the low wall, out of the picture to the left, stands between the afternoon
# sun and the building, so its leaves throw dappled shade over the left of the wall. Its
# centre is on the line from the wall left of the window back towards the sun.
TREE = (-7.4, -0.7)
CANOPY = (-7.1, -0.5, 7.35)


def build_tree(sc, M):
    """A trunk, a few limbs and a canopy of leaf clusters with gaps between them, so the sun
    comes through in patches."""
    tree_rng = random.Random(31)
    tx, ty = TREE
    cx, cy, cz = CANOPY
    bm = bmesh.new()
    base = Vector((tx, ty, ground_height(tx, ty) - 0.1))
    fork = Vector((tx + 0.15, ty + 0.1, 3.2))
    tube(bm, [tuple(base), tuple(base.lerp(fork, 0.5) + Vector((0.08, 0.0, 0.0))), tuple(fork)], 0.28, 0, segments=14)

    # Clusters over a broad, flattened dome, each with a limb running out to it from the fork.
    clusters = []
    while len(clusters) < 34:
        d = Vector((tree_rng.uniform(-1, 1), tree_rng.uniform(-1, 1), tree_rng.uniform(-1, 1)))
        if d.length > 1.0:
            continue
        clusters.append(Vector((cx + d.x * 2.6, cy + d.y * 2.4, cz + d.z * 1.6)))
    for c in clusters[:14]:
        mid = fork.lerp(c, 0.5) + Vector((0, 0, 0.35))
        tube(bm, [tuple(fork), tuple(mid), tuple(fork.lerp(c, 0.92))], 0.07, 0, segments=6)
    emit(sc, "tree", bm, [M["bark"]], smooth=True)

    bm = bmesh.new()
    # Each cluster is a dense clump, so it throws a solid patch of shade even through the
    # soft edge a shadow gets this far from what casts it, and the gaps between let sun through.
    for c in clusters:
        spread = tree_rng.uniform(0.3, 0.45)
        for _ in range(110):
            p = Vector((tree_rng.gauss(0, spread), tree_rng.gauss(0, spread), tree_rng.gauss(0, spread * 0.7)))
            centre = c + p
            # A leaf is a small diamond turned any way, mostly facing up.
            n = Vector((tree_rng.uniform(-0.6, 0.6), tree_rng.uniform(-0.6, 0.6), 1.0)).normalized()
            t = n.orthogonal().normalized()
            t.rotate(Quaternion(n, tree_rng.uniform(0, 2 * math.pi)))
            b = n.cross(t)
            size = tree_rng.uniform(0.13, 0.2)
            corners = [centre + t * size * 1.3, centre + b * size * 0.6, centre - t * size * 1.3, centre - b * size * 0.6]
            bm.faces.new([bm.verts.new(tuple(v)) for v in corners])
    emit(sc, "tree_leaf", bm, [M["tree_leaf"]])


def build():
    rng.seed(7)
    sc = fresh_scene()
    M = make_materials()
    # Footprints first: the ground, the stones on it and the grass all ask how high it is.
    FOOTPRINTS[:] = walk([(1.0, -1.7), (3.0, -0.6), (5.0, 0.0), (6.0, 0.6)], 0.34)
    FOOTPRINTS.extend(walk([(2.2, -3.6), (3.8, -2.0), (5.6, -1.3), (7.2, -0.4)], 0.34))
    JOINTS.clear()
    build_wall(sc, M)
    build_plinth(sc, M)
    build_lamp(sc, M)
    build_dry_wall(sc, M)
    build_ground(sc, M)
    build_flagstones(sc, M)
    build_pebbles(sc, M)
    build_clutter(sc, M)
    build_deck(sc, M)
    build_crates(sc, M)
    build_sacks(sc, M)
    build_wheel(sc, M)
    build_drainpipe(sc, M)
    build_barrel(sc, M)
    build_ivy(sc, M)
    build_plaster(sc, M)
    build_fallen_leaves(sc, M)
    build_grass(sc, M)
    build_weeds(sc, M)
    build_debris(sc, M)
    build_cobwebs(sc, M)
    build_tree(sc, M)
    mask_uvs(sc, ["cy_graph_wood", "cy_graph_staves", "cy_graph_deck", "cy_graph_hoop", "cy_graph_stone", "cy_crate_wood"])
    return sc


def mask_uvs(sc, materials):
    """Gives every mesh using one of materials a second UV set named "mask", which the cooker
    bakes wear and dirt masks into. The first set stays in metres and keeps the textures. For
    each material its faces, across all the meshes, are unwrapped together by Smart UV
    Project, so no two of them share a texel of its mask."""
    window = bpy.context.window
    previous = window.scene
    window.scene = sc
    try:
        for name in materials:
            obs = [o for o in sc.objects if o.type == "MESH" and any(m and m.name == name for m in o.data.materials)]
            if not obs:
                raise ValueError(f"mask_uvs: no mesh in scene {sc.name} uses material {name}, so it has nothing to unwrap.")
            for o in sc.objects:
                o.select_set(False)
            for o in obs:
                me = o.data
                if "mask" not in me.uv_layers:
                    me.uv_layers.new(name="mask")
                me.uv_layers.active = me.uv_layers["mask"]
                slots = {i for i, m in enumerate(me.materials) if m and m.name == name}
                chosen = [p for p in me.polygons if p.material_index in slots]
                verts = {i for p in chosen for i in p.vertices}
                keys = {k for p in chosen for k in p.edge_keys}
                for v in me.vertices:
                    v.select = v.index in verts
                for e in me.edges:
                    e.select = e.key in keys
                for p in me.polygons:
                    p.select = p.material_index in slots
                o.select_set(True)
            bpy.context.view_layer.objects.active = obs[0]
            bpy.ops.object.mode_set(mode="EDIT")
            bpy.ops.mesh.select_mode(type="FACE")
            bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.002)
            bpy.ops.object.mode_set(mode="OBJECT")
        # The metric set is the one the textures and the export's TEXCOORD_0 use.
        for o in sc.objects:
            if o.type == "MESH" and o.data.uv_layers:
                o.data.uv_layers.active_index = 0
                o.data.uv_layers[0].active_render = True
            o.select_set(False)
    finally:
        window.scene = previous


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

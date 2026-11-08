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
        "water": material("water", (0.03, 0.025, 0.02), roughness=0.02),
        "ivy": material("ivy", (0.12, 0.2, 0.06), roughness=0.7),
        "vine": material("vine", (0.12, 0.09, 0.06), roughness=0.9),
        "plaster": material("graph_whitewash", (0.72, 0.7, 0.66), roughness=0.95),
        "dead_leaf": material("dead_leaf", (0.42, 0.24, 0.1), roughness=0.8),
        "dead_leaf_orange": material("dead_leaf_orange", (0.7, 0.36, 0.08), roughness=0.75),
        "grass": material("grass", (0.2, 0.3, 0.08), roughness=0.7),
        "grass_dry": material("grass_dry", (0.42, 0.4, 0.18), roughness=0.8),
        "sack": material("graph_sack", (0.5, 0.4, 0.27), roughness=0.95),
        "twine": material("twine", (0.18, 0.14, 0.09), roughness=0.9),
        "damp": material("graph_damp", (0.2, 0.18, 0.15), roughness=0.7),
        "drip": material("graph_drip", (0.2, 0.18, 0.15), roughness=0.7),
        "rust": material("graph_rust", (0.35, 0.18, 0.08), roughness=0.8),
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
          chips=(), step=0.05, taper=0.0, seed=None):
    """A dressed stone: a box with flat faces and narrow, rounded arrises, so it reads as cut
    stone and not a pebble. warp moves its eight corners about, so it's never quite square;
    taper narrows the top; rough is a fine, pitted unevenness over every face; bulge swells
    the faces a little in the middle, as a pitch-faced stone's do. chips are (corner, size)
    pairs: corner is a sign triple such as (1, -1, 1), and that corner is knocked off with a
    rough, broken face. The block is built square about center, then rolled about X and Y and
    turned by yaw about Z. Returns its vertices."""
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
    box_uv(bm, faces, (rng.uniform(0, 2), rng.uniform(0, 2), rng.uniform(0, 2)))
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


def stain(bm, x0, x1, z_solid, z_faded, y, mat, streak=False):
    """A flat quad just in front of the wall at y, for a dithered grime graph. Its V runs from
    0 at z_solid, where the stain is solid, to 1 at z_faded, where it has faded out. A streak's
    U runs from 0 to 2 across it, so the graph fades it at both sides; a band's U is in metres."""
    corners = [Vector((x0, y, z_solid)), Vector((x1, y, z_solid)), Vector((x1, y, z_faded)), Vector((x0, y, z_faded))]
    if z_faded < z_solid:
        # Keep the face looking out of the wall, towards -y.
        corners = [corners[1], corners[0], corners[3], corners[2]]
    face = bm.faces.new([bm.verts.new(c) for c in corners])
    face.material_index = mat
    uv = uv_layer(bm)
    for loop in face.loops:
        co = loop.vert.co
        u = 2 * (co.x - x0) / (x1 - x0) if streak else co.x
        loop[uv].uv = (u, (co.z - z_solid) / (z_faded - z_solid))


def build_stains(sc, M):
    """Grime laid over the brickwork as dithered decals: rising damp along the foot of the
    wall above the plinth, dirty water run off the sill, and rust washed down from the two
    anchor plates. Each sits a millimetre or two in front of the wall."""
    bm = bmesh.new()
    # The damp stops below the sill, so it doesn't cross the window opening.
    # It starts solid just under the top of the plinth, so the wet edge shows above it.
    bottom, top = 0.42, SILL_Z - 0.05
    stain(bm, WALL_X0, PIER_X0, bottom, top, WALL_Y - 0.0015, 0)
    stain(bm, PIER_X1, WALL_X1, bottom, top, WALL_Y - 0.0015, 0)
    stain(bm, PIER_X0, PIER_X1, bottom, top - 0.1, WALL_Y - PIER_DEPTH - 0.0015, 0)
    # Off the sill, a little wider than the window, in front of the damp.
    half = WIN_W / 2 + 0.12
    stain(bm, WIN_X - half, WIN_X + half, SILL_Z - 0.2, SILL_Z - 0.95, WALL_Y - 0.003, 1, streak=True)
    # Under each anchor plate, from behind its disc.
    for ax in (-1.2, 10.1):
        stain(bm, ax - 0.16, ax + 0.16, 5.38, 4.3, WALL_Y - 0.002, 2, streak=True)
    emit(sc, "stains", bm, [M["damp"], M["drip"], M["rust"]])


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

    # The water: one flat sheet at the puddle's level; the mud around it hides its edge.
    bm = bmesh.new()
    cx, cy, rx, ry = PUDDLE
    ring = [bm.verts.new((cx + rx * 1.4 * math.cos(a), cy + ry * 1.5 * math.sin(a), -0.045))
            for a in (2 * math.pi * k / 48 for k in range(48))]
    bm.faces.new(ring)
    # Water standing in the deepest boot prints by the puddle: a small sheet in each, a
    # centimetre above the bottom of the print, whose sides hide its edge.
    for px, py, heading, _ in FOOTPRINTS:
        if smoothstep(2.4, 1.2, puddle_shape(px, py)) < 0.5 or puddle_depth(px, py) > 0.05:
            continue
        if path_distance(px, py) < PATH_HALF_WIDTH + 0.2:
            continue
        c, s = math.cos(heading), math.sin(heading)
        for u0, ru, rv in ((0.04, 0.07, 0.038), (-0.095, 0.032, 0.03)):
            mx, my = px + u0 * c, py + u0 * s
            z = ground_height(mx, my) + 0.01
            pts = []
            for k in range(12):
                a = 2 * math.pi * k / 12
                u, v = ru * math.cos(a), rv * math.sin(a)
                pts.append(bm.verts.new((mx + u * c - v * s, my + u * s + v * c, z)))
            bm.faces.new(pts)
    for f in bm.faces:
        f.normal_update()
        if f.normal.z < 0:
            f.normal_flip()
    emit(sc, "puddle", bm, [M["water"]])


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


def slab(bm, poly, centre, step, mat):
    """A flagstone: sheer sides from below the mud, a narrow arris that is broken away in
    places, and a flat top that leans a little one way. The top has its own vertices, so its
    smooth shading stops at the arris and the edge reads as a crisp line. It is bedded just
    proud of the mud under it, whichever way it leans, then raised or lowered by step."""
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
    box_uv(bm, faces, (rng.uniform(0, 2), rng.uniform(0, 2), 0))


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
    for p in points:
        near = [q for q in everything if q is not p and abs(q[0] - p[0]) < 1.4 and abs(q[1] - p[1]) < 1.4]
        cell = voronoi_cell(p, near, rng.uniform(0.62, 0.7), rng.uniform(0.008, 0.016), weights)
        if cell is None:
            continue
        cx = sum(c[0] for c in cell) / len(cell)
        cy = sum(c[1] for c in cell) / len(cell)
        # The tops sit just above the mud packed in the joints, a few a hair higher than their
        # neighbours, so the light catches a step here and there.
        slab(bm, cell, (cx, cy), rng.choice((0.0, 0.0, 0.0, 0.003, 0.006)), 0)
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
    for ob, name, x, y, degrees, size in ((big, "crate", 7.0, WALL_Y - 1.2, -6.0, 0.42),
                                          (small, "small_crate", 7.6, 1.2, 22.0, 0.26)):
        place = Matrix.Translation((x, y, DECK_TOP)) @ Matrix.Rotation(math.radians(degrees), 4, "Z") @ Matrix.Scale(size, 4)
        adopt(ob, name, [wood], place @ base)


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
    block(bm, (0.0, 0.0, h / 2), (w / 2, d / 2, h / 2), 0, bevel=0.09 * size, warp=0.015, rough=0.004,
          bulge=0.035 * size, step=0.035)
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
        for f in faces:
            f.material_index = 0
            f.smooth = True
            for loop in f.loops:
                co = loop.vert.co
                a = math.atan2(co.y - cy, co.x - cx)
                # Each stave wears one of the wood graph's six planks, stretched across its width.
                across = min(1.0, ((a - a0) % (2 * math.pi)) / (a1 - a0))
                loop[uv].uv = (co.z + s * 0.37, (s % 6) / 6.0 + 0.012 + across * (1 / 6.0 - 0.024))
    # The head, set well down inside the top so the staves' ends stand round it as a rim.
    head = lathe(bm, [(0.0, height - 0.07), (r_end - 0.025, height - 0.07)], 32, (cx, cy, z0), 0)
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
    emit(sc, "barrel", bm, [M["wood"], M["hoop"]])


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


def growth(x, y):
    """Where grass has taken hold: high in broad patches, nothing in between."""
    return smoothstep(-0.15, 0.35, noise.noise(Vector((x * 0.9, y * 0.9, 11.0))))


def clump(bm, x, y, size):
    """A patch of grass: a big tuft or two with smaller ones and short blades round them."""
    for k in range(rng.randint(2, 5)):
        a = rng.uniform(0, 2 * math.pi)
        r = 0.0 if k == 0 else rng.uniform(0.06, 0.2) * size
        px, py = x + math.cos(a) * r, y + math.sin(a) * r
        if puddle_depth(px, py) > 0.05 or rut_depth(px, py) > 0.1 or path_distance(px, py) < PATH_HALF_WIDTH - 0.15:
            continue
        s = size * (1.0 if k == 0 else rng.uniform(0.35, 0.75))
        tuft(bm, px, py, ground_height(px, py), s)


def build_grass(sc, M):
    """Grass where feet and wheels don't go: the foot of both walls, the edges of the flagstones
    and the deck. It grows in patches, with bare mud between them, and in a few of the joints
    between the flagstones where nobody steps."""
    bm = bmesh.new()
    spots = []
    # Along the foot of the plinth and the low wall, thick where it has taken hold.
    for _ in range(90):
        x, y = rng.uniform(DRY_X + 0.2, WALL_X1 - 0.5), WALL_Y - rng.uniform(0.3, 0.5)
        if rng.random() < growth(x, y):
            spots.append((x, y, rng.uniform(0.8, 1.4)))
    for _ in range(50):
        x, y = DRY_X + rng.uniform(0.12, 0.35), rng.uniform(WALL_Y - DRY_LENGTH, WALL_Y - 0.3)
        if rng.random() < growth(x, y):
            spots.append((x, y, rng.uniform(0.9, 1.6)))
    for x, y, size in spots:
        clump(bm, x, y, size)
    spots = []
    # In the joints between flagstones, away from the middle of the path where feet go.
    for x, y in rng.sample(JOINTS, min(len(JOINTS), 700)):
        if path_distance(x, y) > 0.55 and rng.random() < 0.12 * (0.3 + growth(x, y)):
            tuft(bm, x, y, ground_height(x, y) + 0.01, rng.uniform(0.22, 0.4))
    # Along the flagstones' edges.
    for _ in range(80):
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
        if path_distance(x, y) > PATH_HALF_WIDTH - 0.1 and rng.random() < 0.15 + growth(x, y):
            spots.append((x, y, rng.uniform(0.6, 1.2)))
    # Round the deck.
    for _ in range(20):
        if rng.random() < 0.5:
            x, y = DECK[0] - rng.uniform(0.05, 0.3), rng.uniform(DECK[2], DECK[3])
        else:
            x, y = rng.uniform(DECK[0], DECK[1]), DECK[2] - rng.uniform(0.05, 0.3)
        spots.append((x, y, rng.uniform(0.7, 1.3)))
    for x, y, size in spots:
        clump(bm, x, y, size)
    emit(sc, "grass", bm, [M["grass"], M["grass_dry"]])


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
    build_stains(sc, M)
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
    build_tree(sc, M)
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

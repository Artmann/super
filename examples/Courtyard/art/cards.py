"""
Writes content/models/cards.glb: the leaf, blade and cobweb cards that `--leaves` shows.

Plain Python, no Blender: python examples/Courtyard/art/cards.py. The model is in the game's
own axes, Y up, facing +Z, and every card is its own mesh, so it sways on its own foot.

UVs are in metres, as in courtyard.py: u runs across the card and v is 1 at the card's foot,
falling by a metre for each metre up. The cooker divides them by the graph's width, so a card
as wide as its graph shows the whole image, and a narrower one shows part of it.

Material names say which sidecar rule they get. graph_* are the courtyard's names, cooked with
the same rules, and swatch_* show a whole graph flat, cut out but not lit as foliage.
"""

import json
import math
import os
import random
import struct

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "content", "models", "cards.glb")

# The graphs' widths in metres, as their Build() sets them.
IVY = 0.2
BLADE = 0.3
COBWEB = 0.5
DEAD_LEAF = 0.15
# How many blades stand across each grass species' image, as GrassBlade.Of sets them.
COLUMNS = {"meadow": 24, "broad": 12, "dry": 16}

# The backdrop wall the swatches hang on, and where the ivy climbs.
WALL_Z = -0.6

rng = random.Random(22)
meshes = []  # (name, material, positions, uvs, indices)


def card(name, material, corners, uvs):
    """A flat card from its corners, in order round its edge, and their UVs."""
    indices = [0, 1, 2] if len(corners) == 3 else [0, 1, 2, 0, 2, 3]
    meshes.append((name, material, corners, uvs, indices))


def upright(name, material, x, y, z, width, height, u0=0.0, u1=None, foot=0.0):
    """A card standing in the XY plane, facing +Z, its foot's left corner at (x, y, z)."""
    u1 = width if u1 is None else u1
    card(name, material,
         [(x, y, z), (x + width, y, z), (x + width, y + height, z), (x, y + height, z)],
         [(u0, 1 - foot), (u1, 1 - foot), (u1, 1 - foot - height), (u0, 1 - foot - height)])


def turned(name, material, centre, right, up, width, height, graph):
    """A card of a whole graph, scaled to width by height, its middle at centre, spanned by two directions."""
    cx, cy, cz = centre
    corners = []
    for sx, sy in [(-0.5, 0), (0.5, 0), (0.5, 1), (-0.5, 1)]:
        corners.append((cx + right[0] * sx * width + up[0] * sy * height,
                        cy + right[1] * sx * width + up[1] * sy * height,
                        cz + right[2] * sx * width + up[2] * sy * height))
    card(name, material, corners, [(0, 1), (graph, 1), (graph, 1 - graph), (0, 1 - graph)])


def swatches():
    """Each graph whole and flat on the wall, at its own size, along the back."""
    x = -1.45
    y = 0.62
    z = WALL_Z + 0.01
    for material, size in [("swatch_ivy_small", IVY), ("swatch_ivy_large", IVY)]:
        upright(material, material, x, y + 0.05, z, size, size)
        x += size + 0.06
    for species in ["meadow", "broad", "dry"]:
        material = "swatch_blade_" + species
        upright(material, material, x, y, z, BLADE, BLADE)
        x += BLADE + 0.06
    for material in ["swatch_deadleaf_tan", "swatch_deadleaf_dark", "swatch_deadleaf_red"]:
        upright(material, material, x, y + 0.08, z, DEAD_LEAF, DEAD_LEAF)
        x += DEAD_LEAF + 0.04


def ivy():
    """A patch of ivy on the wall at the left: leaves 6 to 12 cm, tips down and out, half of each size."""
    for i in range(22):
        material = "graph_ivy_small" if i % 2 == 0 else "graph_ivy_large"
        size = rng.uniform(0.06, 0.08) if i % 2 == 0 else rng.uniform(0.09, 0.12)
        cx = -1.42 + rng.uniform(0, 0.42)
        cy = 0.08 + rng.uniform(0, 0.42)
        # Tipped out from the wall by the lean, then turned about the wall's normal by the roll, so the tip hangs
        # down and towards you. The stalk end is at the wall.
        roll = math.radians(180 + rng.uniform(-55, 55))
        lean = math.radians(rng.uniform(15, 40))
        up = (-math.cos(lean) * math.sin(roll), math.cos(lean) * math.cos(roll), math.sin(lean))
        right = (math.cos(roll), math.sin(roll), 0.0)
        centre = (cx, cy, WALL_Z + 0.02 + 0.01 * i / 22)
        turned("ivy_%02d" % i, material, centre, right, up, size, size, IVY)


def tuft(species, x, z, count):
    """A tuft of blade cards standing round one point, each a column of the species' image, leaning out a little."""
    columns = COLUMNS[species]
    column = BLADE / columns
    for i in range(count):
        c = rng.randrange(columns)
        turn = i * 2.39996 + rng.uniform(-0.3, 0.3)
        lean = math.radians(rng.uniform(4, 22))
        # The card faces along `turn` and leans away from the tuft's middle.
        out = (math.cos(turn), 0, math.sin(turn))
        right = (-math.sin(turn), 0, math.cos(turn))
        up = (out[0] * math.sin(lean), math.cos(lean), out[2] * math.sin(lean))
        foot = (x + out[0] * rng.uniform(0, 0.02), 0.0, z + out[2] * rng.uniform(0, 0.02))
        corners = []
        for sx, sy in [(-0.5, 0), (0.5, 0), (0.5, 1), (-0.5, 1)]:
            corners.append((foot[0] + right[0] * sx * column + up[0] * sy * BLADE,
                            foot[1] + up[1] * sy * BLADE,
                            foot[2] + right[2] * sx * column + up[2] * sy * BLADE))
        u0 = c * column
        card("blade_%s_%02d" % (species, i), "graph_blade_" + species, corners,
             [(u0, 1), (u0 + column, 1), (u0 + column, 1 - BLADE), (u0, 1 - BLADE)])


def dead_leaves():
    """Leaves lying flat on the ground at the front, in three tints, 8 to 12 cm long."""
    tints = ["graph_deadleaf_tan", "graph_deadleaf_dark", "graph_deadleaf_red"]
    for i in range(12):
        size = rng.uniform(0.08, 0.12)
        yaw = rng.uniform(0, 2 * math.pi)
        up = (math.sin(yaw), 0, math.cos(yaw))
        right = (-math.cos(yaw), 0, math.sin(yaw))
        centre = (0.35 + rng.uniform(0, 0.75) - up[0] * size * 0.5, 0.004 + 0.001 * i,
                  0.05 + rng.uniform(0, 0.4) - up[2] * size * 0.5)
        turned("deadleaf_%02d" % i, tints[i % 3], centre, right, up, size, size, DEAD_LEAF)


def cobweb():
    """A web in the top-left corner of a frame at the right, its right angle in the corner."""
    x = 1.0
    top = 1.15
    z = WALL_Z + 0.25
    size = COBWEB
    card("cobweb", "graph_cobweb",
         [(x, top - size, z), (x + size, top, z), (x, top, z)],
         [(0, 1), (size, 1 - size), (0, 1 - size)])


def write():
    materials = sorted({mesh[1] for mesh in meshes})
    binary = bytearray()
    views, accessors, gltf_meshes, nodes = [], [], [], []

    def add(data, target, component, kind, count, bounds=None):
        while len(binary) % 4:
            binary.append(0)
        views.append({"buffer": 0, "byteOffset": len(binary), "byteLength": len(data), "target": target})
        binary.extend(data)
        accessor = {"bufferView": len(views) - 1, "componentType": component, "count": count, "type": kind}
        if bounds:
            accessor["min"], accessor["max"] = bounds
        accessors.append(accessor)
        return len(accessors) - 1

    for name, material, corners, uvs, indices in meshes:
        # The face's normal from its first three corners; the cooker turns it out of the crown for foliage.
        a, b, c = corners[0], corners[1], corners[2]
        e1 = [b[i] - a[i] for i in range(3)]
        e2 = [c[i] - a[i] for i in range(3)]
        n = [e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0]]
        length = math.sqrt(sum(v * v for v in n))
        n = [v / length for v in n]
        positions = b"".join(struct.pack("<3f", *p) for p in corners)
        lows = [min(p[i] for p in corners) for i in range(3)]
        highs = [max(p[i] for p in corners) for i in range(3)]
        position = add(positions, 34962, 5126, "VEC3", len(corners), (lows, highs))
        normal = add(b"".join(struct.pack("<3f", *n) for _ in corners), 34962, 5126, "VEC3", len(corners))
        uv = add(b"".join(struct.pack("<2f", *t) for t in uvs), 34962, 5126, "VEC2", len(corners))
        index = add(b"".join(struct.pack("<H", i) for i in indices), 34963, 5123, "SCALAR", len(indices))
        gltf_meshes.append({"name": name, "primitives": [{
            "attributes": {"POSITION": position, "NORMAL": normal, "TEXCOORD_0": uv},
            "indices": index, "material": materials.index(material)}]})
        nodes.append({"name": name, "mesh": len(gltf_meshes) - 1})

    while len(binary) % 4:
        binary.append(0)
    document = {
        "asset": {"version": "2.0", "generator": "examples/Courtyard/art/cards.py"},
        "scene": 0,
        "scenes": [{"nodes": list(range(len(nodes)))}],
        "nodes": nodes,
        "meshes": gltf_meshes,
        "materials": [{"name": m, "doubleSided": True,
                       "pbrMetallicRoughness": {"baseColorFactor": [1, 1, 1, 1], "metallicFactor": 0, "roughnessFactor": 0.6}}
                      for m in materials],
        "accessors": accessors,
        "bufferViews": views,
        "buffers": [{"byteLength": len(binary)}],
    }
    text = json.dumps(document, separators=(",", ":")).encode()
    while len(text) % 4:
        text += b" "
    with open(OUT, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(text) + 8 + len(binary)))
        f.write(struct.pack("<II", len(text), 0x4E4F534A) + text)
        f.write(struct.pack("<II", len(binary), 0x004E4942) + bytes(binary))
    print("wrote %s: %d cards, %d materials" % (os.path.normpath(OUT), len(meshes), len(materials)))


swatches()
ivy()
tuft("meadow", -0.55, 0.0, 26)
tuft("broad", -0.1, 0.12, 14)
tuft("dry", 0.3, -0.15, 16)
dead_leaves()
cobweb()
write()

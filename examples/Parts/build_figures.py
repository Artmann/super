"""Writes assets/parts/figures.gltf: one static model of eight named meshes, for the Parts example.

Three bodies, three heads and two hats, all standing on y = 0 in one spot, so drawing the whole
file gives a pile. A part mask picks one body, one head and optionally a hat. Each mesh is its own
node, named the same as its mesh, with a flat base colour and flat normals. Stdlib only:

    python examples/Parts/build_figures.py

The output is committed with its sidecar (assets/parts/figures.gltf.cook.json, "keepMeshes": true),
so running this again only changes the file if the shapes change.
"""

import base64
import json
import math
import os
import struct

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "assets", "parts", "figures.gltf"))


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def normalise(v):
    length = math.sqrt(dot(v, v))
    return (v[0] / length, v[1] / length, v[2] / length)


class Mesh:
    """Triangles with flat normals. Each triangle's corners are stored apart, so its normal is exact."""

    def __init__(self):
        self.positions = []
        self.normals = []
        self.indices = []

    def tri(self, a, b, c, outward):
        """Adds a triangle, wound so its normal points the same way as `outward`."""
        n = cross(sub(b, a), sub(c, a))
        if dot(n, outward) < 0:
            b, c = c, b
            n = (-n[0], -n[1], -n[2])
        n = normalise(n)
        base = len(self.positions)
        for p in (a, b, c):
            self.positions.append(p)
            self.normals.append(n)
        self.indices.extend([base, base + 1, base + 2])

    def quad(self, a, b, c, d, outward):
        self.tri(a, b, c, outward)
        self.tri(a, c, d, outward)


def box(centre, half):
    cx, cy, cz = centre
    hx, hy, hz = half
    p = {}
    for sx in (-1, 1):
        for sy in (-1, 1):
            for sz in (-1, 1):
                p[(sx, sy, sz)] = (cx + sx * hx, cy + sy * hy, cz + sz * hz)
    mesh = Mesh()
    faces = [
        ((1, -1, -1), (1, 1, -1), (1, 1, 1), (1, -1, 1), (1, 0, 0)),
        ((-1, -1, 1), (-1, 1, 1), (-1, 1, -1), (-1, -1, -1), (-1, 0, 0)),
        ((-1, 1, -1), (-1, 1, 1), (1, 1, 1), (1, 1, -1), (0, 1, 0)),
        ((-1, -1, 1), (-1, -1, -1), (1, -1, -1), (1, -1, 1), (0, -1, 0)),
        ((-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1), (0, 0, 1)),
        ((1, -1, -1), (-1, -1, -1), (-1, 1, -1), (1, 1, -1), (0, 0, -1)),
    ]
    for a, b, c, d, outward in faces:
        mesh.quad(p[a], p[b], p[c], p[d], outward)
    return mesh


def icosahedron(centre, radius):
    phi = (1 + math.sqrt(5)) / 2
    raw = []
    for s1 in (-1, 1):
        for s2 in (-1, 1):
            raw += [(0, s1, s2 * phi), (s1, s2 * phi, 0), (s2 * phi, 0, s1)]
    verts = [normalise(v) for v in raw]
    verts = [(centre[0] + v[0] * radius, centre[1] + v[1] * radius, centre[2] + v[2] * radius) for v in verts]
    edge = min(math.dist(verts[i], verts[j]) for i in range(12) for j in range(i + 1, 12))
    mesh = Mesh()
    for i in range(12):
        for j in range(i + 1, 12):
            for k in range(j + 1, 12):
                pairs = (math.dist(verts[i], verts[j]), math.dist(verts[j], verts[k]), math.dist(verts[i], verts[k]))
                if all(abs(d - edge) < 1e-6 for d in pairs):
                    corners = (verts[i], verts[j], verts[k])
                    mean = tuple(sum(c[n] for c in corners) / 3 for n in range(3))
                    mesh.tri(*corners, sub(mean, centre))
    assert len(mesh.indices) == 60, "an icosahedron has 20 faces"
    return mesh


def cylinder(base, radius, height, sides):
    bx, by, bz = base
    mesh = Mesh()
    ring_bottom = []
    ring_top = []
    for i in range(sides):
        a = 2 * math.pi * i / sides
        x, z = math.cos(a) * radius, math.sin(a) * radius
        ring_bottom.append((bx + x, by, bz + z))
        ring_top.append((bx + x, by + height, bz + z))
    for i in range(sides):
        j = (i + 1) % sides
        a = 2 * math.pi * (i + 0.5) / sides
        mesh.quad(ring_bottom[i], ring_bottom[j], ring_top[j], ring_top[i], (math.cos(a), 0, math.sin(a)))
    top = (bx, by + height, bz)
    bottom = (bx, by, bz)
    for i in range(sides):
        j = (i + 1) % sides
        mesh.tri(top, ring_top[i], ring_top[j], (0, 1, 0))
        mesh.tri(bottom, ring_bottom[j], ring_bottom[i], (0, -1, 0))
    return mesh


def cone(base, radius, height, sides):
    bx, by, bz = base
    apex = (bx, by + height, bz)
    ring = []
    for i in range(sides):
        a = 2 * math.pi * i / sides
        ring.append((bx + math.cos(a) * radius, by, bz + math.sin(a) * radius))
    mesh = Mesh()
    for i in range(sides):
        j = (i + 1) % sides
        a = 2 * math.pi * (i + 0.5) / sides
        mesh.tri(ring[i], ring[j], apex, (math.cos(a), 0.5, math.sin(a)))
        mesh.tri(base, ring[i], ring[j], (0, -1, 0))
    return mesh


def disc(centre, radius, sides):
    cx, cy, cz = centre
    ring = [
        (cx + math.cos(2 * math.pi * i / sides) * radius, cy, cz + math.sin(2 * math.pi * i / sides) * radius)
        for i in range(sides)
    ]
    mesh = Mesh()
    for i in range(sides):
        j = (i + 1) % sides
        mesh.tri(centre, ring[i], ring[j], (0, 1, 0))
        mesh.tri(centre, ring[j], ring[i], (0, -1, 0))
    return mesh


BODY_HALF = (0.3, 0.45, 0.2)
BODY_TOP = 0.9
HEAD_TOP = 1.5
HEAD_MIDDLE = (BODY_TOP + HEAD_TOP) / 2

# Name, flat base colour (linear RGB), and the mesh. Every node is named the same as its mesh.
PARTS = [
    ("Body_Red", (0.78, 0.12, 0.10), lambda: box((0, BODY_TOP / 2, 0), BODY_HALF)),
    ("Body_Green", (0.12, 0.62, 0.22), lambda: box((0, BODY_TOP / 2, 0), BODY_HALF)),
    ("Body_Blue", (0.12, 0.28, 0.80), lambda: box((0, BODY_TOP / 2, 0), BODY_HALF)),
    ("Head_Round", (0.93, 0.76, 0.58), lambda: icosahedron((0, HEAD_MIDDLE, 0), 0.3)),
    ("Head_Square", (0.93, 0.76, 0.58), lambda: box((0, HEAD_MIDDLE, 0), (0.3, 0.3, 0.3))),
    ("Head_Cone", (0.93, 0.76, 0.58), lambda: cone((0, BODY_TOP, 0), 0.3, HEAD_TOP - BODY_TOP, 8)),
    ("Hat_Top", (0.32, 0.12, 0.46), lambda: cylinder((0, HEAD_TOP, 0), 0.2, 0.45, 8)),
    ("Hat_Cap", (0.95, 0.78, 0.15), lambda: disc((0, HEAD_TOP + 0.03, 0), 0.42, 12)),
]


def build():
    buffer = bytearray()
    buffer_views = []
    accessors = []
    materials = []
    meshes = []
    nodes = []

    def view(data, target):
        while len(buffer) % 4:
            buffer.append(0)
        offset = len(buffer)
        buffer.extend(data)
        buffer_views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(data), "target": target})
        return len(buffer_views) - 1

    def accessor(data, kind, count, component, target=34962, bounds=None):
        entry = {"bufferView": view(data, target), "componentType": component, "count": count, "type": kind}
        if bounds is not None:
            entry["min"], entry["max"] = bounds
        accessors.append(entry)
        return len(accessors) - 1

    for index, (name, colour, make) in enumerate(PARTS):
        mesh = make()
        flat_positions = [c for p in mesh.positions for c in p]
        flat_normals = [c for n in mesh.normals for c in n]
        xs, ys, zs = zip(*mesh.positions)
        bounds = ([min(xs), min(ys), min(zs)], [max(xs), max(ys), max(zs)])
        position = accessor(struct.pack(f"<{len(flat_positions)}f", *flat_positions), "VEC3", len(mesh.positions), 5126,
                            bounds=bounds)
        normal = accessor(struct.pack(f"<{len(flat_normals)}f", *flat_normals), "VEC3", len(mesh.normals), 5126)
        indices = accessor(struct.pack(f"<{len(mesh.indices)}H", *mesh.indices), "SCALAR", len(mesh.indices), 5123,
                           target=34963)
        materials.append({
            "name": name,
            "pbrMetallicRoughness": {"baseColorFactor": [*colour, 1.0], "metallicFactor": 0.0, "roughnessFactor": 0.8},
        })
        meshes.append({
            "name": name,
            "primitives": [{"attributes": {"POSITION": position, "NORMAL": normal}, "indices": indices, "material": index}],
        })
        nodes.append({"name": name, "mesh": index})

    return {
        "asset": {"version": "2.0", "generator": "examples/Parts/build_figures.py"},
        "scene": 0,
        "scenes": [{"nodes": list(range(len(nodes)))}],
        "nodes": nodes,
        "meshes": meshes,
        "materials": materials,
        "accessors": accessors,
        "bufferViews": buffer_views,
        "buffers": [{
            "byteLength": len(buffer),
            "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(buffer)).decode("ascii"),
        }],
    }


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        json.dump(build(), f, indent=1)
        f.write("\n")
    print(f"Wrote {OUT}")


if __name__ == "__main__":
    main()

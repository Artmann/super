# Lightmap UVs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every static-capable mesh (cooked models, built-in shapes, terrain, meshes along splines) ends up with a `Vertex.Uv2` where no two triangles share a texel, made by our own unwrapper in Rendition when the source doesn't have a good one, and wear-and-dirt masks no longer need an authored second UV set.

**Architecture:** Rendition gains `SkylinePacker` (rectangles into a page, reused by the baker's layout later) and `LightmapUnwrap` (charts by normal and hard edge, planar projection, split on self-overlap, scale by world area, pack with a two-texel gutter at 512) plus `LightmapUnwrap.Problem`, the validity check. The model cooker keeps a source `Uv2` that passes the check and unwraps otherwise, cached in the step cache, before masks bake. Built-in shapes lay out their own second UVs by treating each piece the shape builder makes as a chart.

**Tech Stack:** C# (.NET 10), System.Numerics, xunit 2.9. No native dependency.

**Spec:** `docs/superpowers/specs/2026-11-11-lighting-design.md`, section "Lightmap UVs". Roadmap: `docs/superpowers/plans/2026-11-11-lighting-roadmap.md`.

## Global Constraints

- A lightmap UV set: no two triangles share a texel, every chart keeps a two-texel gutter at a reference size of 512, texel density varies by less than 4× across the mesh, all inside 0–1.
- `Uv2` covers a whole mesh in 0–1, all its parts together. Triangle order and count never change, so parts' index ranges stay valid; only vertices are split along chart seams.
- The unwrapper is ours, in C#, in Rendition as `LightmapUnwrap`. Charts by face normal within a cone of about 60 degrees, never across a hard edge; projected onto the chart's mean normal's plane; split again if the projection overlaps itself; scaled by world area so texel density is even; packed with a skyline packer, two-texel gutter at 512.
- The cooker keeps a valid `Uv2` from the file; otherwise it makes one and logs a note, cached through the step cache. Masks bake after this, onto whichever `Uv2` the mesh ends up with.
- Built-in shapes get hand-made second UVs. Terrain uses its grid UVs.
- Meshes made by code are unwrapped when created static. `Static` arrives with the bake API plan, so this plan provides the call and that plan makes it.
- Exact thresholds and packing heuristics are the builder's.
- Braces on every control statement. Conventional commits, no Co-Authored-By lines. Docs change in the same commit as the code.

My call, differing slightly from the spec's wording: the validity check is `LightmapUnwrap.Problem` in Rendition, not `MaskBaker.Problem`, because the spec's rule adds gutter and density checks that masks don't need, and shapes' tests in `Super.Tests` can't reach `Super.Cook.Models`. `MaskBaker.Problem` stays as the masks' own check and should always agree on overlap.

## Review Focus

1. Scanned and organic meshes like the marble bust (17,456 triangles, UV seams everywhere in its first UV set): charts must grow across first-UV seams, which split vertices without being hard edges, or the bust becomes thousands of one-triangle charts. Test in Task 2 with a sphere whose vertices are split along a seam.
2. Degenerate input: zero-area triangles, a mesh of one triangle, and a mesh whose triangles share no vertices at all. Each must produce a valid set without dividing by zero. Test in Task 2.
3. A chart too long and thin to fit (a 40 m spline wall one triangle high): packing must shrink the scale until it fits rather than loop or throw. Test in Task 1 (packer reports failure) and Task 2 (unwrapper retries).
4. A mesh with a source `Uv2` that is valid for masks but has charts touching (no gutter): the cooker must replace it and log the note, since bilinear filtering would bleed between them. Test in Task 3.
5. Mirrored placements in the cooker (negative determinant) and `Shapes.Flat`: winding flips must not turn a chart's projection over so it overlaps itself. Test in Task 2 (mirrored input) and Task 4 (`Flat`).

---

### Task 1: The skyline packer

**Files:**
- Create: `src/Rendition/Lightmaps/SkylinePacker.cs`
- Test: `tests/Super.Tests/Lightmaps/SkylinePackerTests.cs`

**Interfaces:**
- Produces: `public static class SkylinePacker` with `public static bool TryPack(ReadOnlySpan<(int Width, int Height)> rectangles, int pageWidth, int pageHeight, int gutter, Span<(int X, int Y)> positions)`. Rectangles are placed tallest first (ties by width, then by index, so the result is the same every run), each at the lowest then leftmost skyline spot it fits. `gutter` texels are kept between any two rectangles and none at the page's edges (the page's own edge clamps). Returns false, positions undefined, when one doesn't fit.

- [ ] **Step 1: Write the failing tests**

```csharp
public class SkylinePackerTests
{
    [Fact]
    public void Keeps_the_gutter_between_rectangles()
    {
        (int, int)[] sizes = [(10, 10), (10, 10), (10, 10)];
        var at = new (int X, int Y)[3];
        Assert.True(SkylinePacker.TryPack(sizes, 64, 64, gutter: 2, at));
        for (int a = 0; a < 3; a++)
        {
            for (int b = a + 1; b < 3; b++)
            {
                bool apartX = at[a].X + 10 + 2 <= at[b].X || at[b].X + 10 + 2 <= at[a].X;
                bool apartY = at[a].Y + 10 + 2 <= at[b].Y || at[b].Y + 10 + 2 <= at[a].Y;
                Assert.True(apartX || apartY, $"rectangles {a} and {b} are closer than the gutter");
            }
        }
    }

    [Fact]
    public void Fills_a_page_exactly_without_a_gutter()
    {
        (int, int)[] sizes = [(32, 32), (32, 32), (32, 32), (32, 32)];
        Assert.True(SkylinePacker.TryPack(sizes, 64, 64, gutter: 0, new (int, int)[4]));
    }

    [Fact]
    public void Says_when_they_dont_fit() =>
        Assert.False(SkylinePacker.TryPack([(65, 1)], 64, 64, gutter: 0, new (int, int)[1]));

    [Fact]
    public void Packs_the_same_way_every_time()
    {
        (int, int)[] sizes = [(7, 3), (3, 7), (5, 5), (7, 3)];
        var first = new (int, int)[4];
        var second = new (int, int)[4];
        SkylinePacker.TryPack(sizes, 32, 32, 2, first);
        SkylinePacker.TryPack(sizes, 32, 32, 2, second);
        Assert.Equal(first, second);
    }
}
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Super.Tests --filter FullyQualifiedName~SkylinePackerTests`
Expected: build fails, `SkylinePacker` doesn't exist.

- [ ] **Step 3: Implement `SkylinePacker.TryPack`**

- [ ] **Step 4: Run them and make sure they pass**

Expected: PASS. Set the gutter to 0 inside `TryPack`, watch the gutter test fail, revert.

- [ ] **Step 5: Commit**

```bash
git add src/Rendition/Lightmaps/SkylinePacker.cs tests/Super.Tests/Lightmaps/SkylinePackerTests.cs
git commit -m "feat: add a skyline packer for lightmap charts and pages"
```

### Task 2: The unwrapper and its check

**Files:**
- Create: `src/Rendition/Lightmaps/LightmapUnwrap.cs`
- Test: `tests/Super.Tests/Lightmaps/LightmapUnwrapTests.cs`, `tests/Super.Tests/Lightmaps/TestShapes.cs` (small mesh builders for the tests: welded and seam-split spheres, a helical ramp, a box with hard edges)

**Interfaces:**
- Consumes: `SkylinePacker.TryPack`.
- Produces: `public static class LightmapUnwrap` with
  - `public const int ReferenceSize = 512;` `public const int Gutter = 2;`
  - `public static (Vertex[] Vertices, uint[] Indices) Unwrap(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)`: same triangles in the same order, vertices split where charts meet, `Uv2` set, every other vertex field copied unchanged.
  - `public static string? Problem(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> uvs, ReadOnlySpan<uint> indices)`: null for a valid lightmap set at `ReferenceSize`, otherwise one sentence saying which rule broke and where: outside 0–1, two triangles overlapping at a texel, two charts closer than `Gutter` texels, or density varying by 4× or more (density measured per chart as UV area over world area, ignoring zero-area triangles).
  - `public static double DensitySpread(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> uvs, ReadOnlySpan<uint> indices)`: largest chart density over smallest, for tests and the cooker's note.

Charts: triangles are joined across an edge when the edge's two corners are at the same positions (welded by position, so first-UV seams don't split charts), the vertex normals at both corners agree on both sides (otherwise it's a hard edge), and the new triangle's face normal is within about 60 degrees of the chart's running mean normal. A triangle whose winding was mirrored is handled by its face normal, not its winding. Each chart is projected onto the plane of its mean normal; if any two of its triangles overlap in that projection, the chart is split (grow again from the overlapping triangle) until none do. Charts are scaled by one shared factor so world area maps to UV area evenly, sized in texels at `ReferenceSize` rounded up, and packed; if packing fails the shared factor shrinks by 10% and it tries again. Zero-area triangles join the chart of a neighbour, or form a chart of one texel.

- [ ] **Step 1: Write the failing tests**

```csharp
public class LightmapUnwrapTests
{
    public static TheoryData<string> Meshes => ["welded sphere", "seam-split sphere", "box", "helical ramp", "single triangle", "unshared triangles", "mirrored box"];

    [Theory]
    [MemberData(nameof(Meshes))]
    public void Makes_a_valid_lightmap_set(string name)
    {
        var (vertices, indices) = TestShapes.Get(name);
        var (outVertices, outIndices) = LightmapUnwrap.Unwrap(vertices, indices);
        Assert.Null(LightmapUnwrap.Problem(
            outVertices.Select(v => v.Position).ToArray(), outVertices.Select(v => v.Uv2).ToArray(), outIndices));
    }

    [Fact]
    public void Keeps_triangle_order_and_every_corner()
    {
        var (vertices, indices) = TestShapes.Get("seam-split sphere");
        var (outVertices, outIndices) = LightmapUnwrap.Unwrap(vertices, indices);
        Assert.Equal(indices.Length, outIndices.Length);
        for (int i = 0; i < indices.Length; i++)
        {
            Assert.Equal(vertices[(int)indices[i]].Position, outVertices[(int)outIndices[i]].Position);
            Assert.Equal(vertices[(int)indices[i]].Uv, outVertices[(int)outIndices[i]].Uv);
        }
    }

    [Fact]
    public void Density_is_even_across_charts()
    {
        var (vertices, indices) = TestShapes.Get("box with one long side"); // 4 × 1 × 1 m
        var (v, i) = LightmapUnwrap.Unwrap(vertices, indices);
        Assert.InRange(LightmapUnwrap.DensitySpread(v.Select(x => x.Position).ToArray(), v.Select(x => x.Uv2).ToArray(), i), 1.0, 1.01);
    }

    [Fact]
    public void A_box_is_six_charts() =>
        Assert.Equal(6, LightmapUnwrap.Charts(TestShapes.Get("box").Vertices, TestShapes.Get("box").Indices));

    [Fact]
    public void A_seam_in_the_first_uvs_does_not_split_a_chart() =>
        Assert.Equal(
            LightmapUnwrap.Charts(TestShapes.Get("welded sphere").Vertices, TestShapes.Get("welded sphere").Indices),
            LightmapUnwrap.Charts(TestShapes.Get("seam-split sphere").Vertices, TestShapes.Get("seam-split sphere").Indices));

    [Fact]
    public void A_long_thin_strip_still_fits()
    {
        var (vertices, indices) = TestShapes.Get("strip 40 by 0.2"); // 40 m long, 0.2 m high
        var (v, i) = LightmapUnwrap.Unwrap(vertices, indices);
        Assert.Null(LightmapUnwrap.Problem(v.Select(x => x.Position).ToArray(), v.Select(x => x.Uv2).ToArray(), i));
    }
}

public class LightmapUnwrapProblemTests
{
    [Fact]
    public void Names_two_overlapping_triangles() { /* two triangles at the same UVs; message contains "overlap" and both triangle numbers */ }

    [Fact]
    public void Names_charts_closer_than_the_gutter() { /* two quads one texel apart at 512; message contains "gutter" */ }

    [Fact]
    public void Names_uneven_density() { /* a 1 m and a 0.1 m quad given equal UV areas: spread 10, message contains "density" */ }
}
```

`LightmapUnwrap.Charts(vertices, indices)` is an `internal` count of charts for tests. `TestShapes.Get` knows every name used above: "welded sphere", "seam-split sphere" (the same sphere with vertices split along a meridian, as a first-UV seam does), "box", "box with one long side" (4 × 1 × 1 m), "helical ramp" (1.5 turns, normals within 20 degrees of up), "single triangle", "unshared triangles", "mirrored box" (a box under scale (-1, 1, 1) with winding left as is) and "strip 40 by 0.2". Fill the three problem tests with the exact message each produces (decide the sentences, then assert them whole).

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Super.Tests --filter "FullyQualifiedName~LightmapUnwrap"`
Expected: build fails.

- [ ] **Step 3: Implement `Problem`, `DensitySpread`, `Charts` and `Unwrap`**

`Problem` rasterizes at `ReferenceSize` with every texel a triangle touches counted (conservative), giving each texel an owner triangle and chart. Overlap is two triangles of different charts, or of one chart that don't share an edge, owning a texel. Gutter is a texel of one chart within `Gutter` texels of another chart's texel.

- [ ] **Step 4: Run them and make sure they pass**

Expected: PASS. Turn off the self-overlap split, watch the helical ramp fail; weld by index instead of position, watch the seam test fail; revert both.

- [ ] **Step 5: Time it on the bust**

Write a throwaway console run (not committed) that reads `D:\Assets\marble_bust_01_2k\marble_bust_01_2k.gltf` through the cooker's Assimp import, unwraps, prints chart count, `DensitySpread`, `Problem` and milliseconds.
Expected: `Problem` null, spread under 1.01, a few hundred charts at most, and under 2 seconds. Note the numbers for the PR.

- [ ] **Step 6: Commit**

```bash
git add src/Rendition/Lightmaps/LightmapUnwrap.cs tests/Super.Tests/Lightmaps
git commit -m "feat: unwrap meshes into lightmap UVs without overlap and with even density"
```

### Task 3: The model cooker keeps or makes `Uv2`

**Files:**
- Modify: `src/Super.Cook.Models/ModelCooker.cs` (after vertices and indices are complete, before `BakeMasks` at line 427; drop the `withoutUv2` error path at lines 534-538)
- Test: `tests/Super.Cook.Tests/ModelCookerTests.cs`, `tests/Super.Cook.Tests/TinyGltf.cs` (add a way to write a second UV set)
- Docs: `docs/rendering.md` ("Wear and dirt": masks no longer need `TEXCOORD_1`; replace the example error at line 1115; a short note under "Models" that the cooker makes a second UV set for lighting when the file's isn't usable), `docs/content.md:362`, `docs/design.md` (the unwrapper's rules and the cooker step), `docs/command-line.md` (a `lightmap-uv` row in the step cache table, keyed by positions, normals, indices and the unwrapper's build)

**Interfaces:**
- Consumes: `LightmapUnwrap.Unwrap`, `LightmapUnwrap.Problem`, `input.Recall(step, key)` and `input.Keep(step, key, bytes)`.
- Produces: cooked models whose `Uv2` always passes `LightmapUnwrap.Problem`. Step name `"lightmap-uv"`. Note text: `{prefix}made a second UV set for lighting: {reason}` where reason is the file has none, or `Problem`'s sentence.

The source `Uv2` is checked across the whole model (all its meshes merged, as cooked), since a lightmap rectangle covers the whole mesh. The cached bytes are the unwrapped vertices' `Uv2` plus the remap from output vertex to input vertex, so a recall rebuilds the split vertex array without unwrapping.

- [ ] **Step 1: Write the failing tests**

- `Keeps_a_valid_second_uv_set`: a two-quad glTF whose `TEXCOORD_1` puts the quads in separate halves with a gutter: the cooked `Uv2` equals the file's, no note.
- `Makes_one_when_the_file_has_none`: the same model without `TEXCOORD_1`: cooked `Uv2` passes `LightmapUnwrap.Problem`, and the output has exactly the note `made a second UV set for lighting: the file has none`.
- `Replaces_one_whose_charts_touch`: `TEXCOORD_1` with the quads side by side and no gap: replaced, and the note names the gutter.
- `Bakes_masks_without_an_authored_second_uv_set`: a material rule with `bakeMasks` and wear on a model with no `TEXCOORD_1` cooks without error and its mask texture has covered texels.
- `Recalls_the_unwrap_from_the_step_cache`: cook twice with a step cache; the second cook's timings include `recall lightmap-uv` and its vertices equal the first's.

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Super.Cook.Tests --filter FullyQualifiedName~ModelCookerTests`
Expected: the new tests fail; the mask test fails with the old "needs a second UV set" error.

- [ ] **Step 3: Implement the step in `ModelCooker`**

- [ ] **Step 4: Run them and make sure they pass**

Run: `dotnet test tests/Super.Cook.Tests`
Expected: PASS, whole project. Skip the replacement on purpose, watch `Replaces_one_whose_charts_touch` fail, revert.

- [ ] **Step 5: Check the examples still cook and look the same**

Run: `dotnet run --project examples/Courtyard -- --screenshot shots/uv2-courtyard.png --validate --seed 1 --ticks 120 --size 1280x720` before (on `main`) and after.
Expected: 0 validation messages, the cook prints the note for each model without a usable second UV set, and the wear and dirt on the Courtyard's models look the same as before. Look at both images.

- [ ] **Step 6: Search docs and commit**

Run: `grep -rn "TEXCOORD_1\|second UV" docs/` and fix each stale line.

```bash
git add src/Super.Cook.Models tests/Super.Cook.Tests docs
git commit -m "feat: make a lightmap UV set when a model's own isn't usable"
```

### Task 4: Built-in shapes, terrain and spline meshes

**Files:**
- Modify: `src/Super/Shapes.cs` (`ShapeBuilder`: each `Grid`, `Revolve` and cap fan is a chart; `Build` lays charts out with `SkylinePacker`; `Flat` keeps `Uv2`)
- Modify: `src/Super/ShapesAlongSplines.cs` (run the result through `LightmapUnwrap.Unwrap`)
- Modify: `src/Super/Terrain/Heightfield.cs` (`Uv2` is the vertex's grid position over the grid's size, 0–1)
- Modify: `src/Super/Surface.cs` or `src/Super/Content.cs` (add `public GeneratedMesh WithLightmapUvs()` on `GeneratedMesh`, returning itself when its `Uv2` already passes `LightmapUnwrap.Problem`, otherwise unwrapped; the bake API plan calls it for meshes created static)
- Test: `tests/Super.Tests/ShapesTests.cs` (or a new `ShapeLightmapTests.cs`), `tests/Terrain.Tests`
- Docs: `docs/rendering.md` ("Built-in shapes" and "Meshes made by code": second UVs are set; `WithLightmapUvs`), `docs/design.md`

**Interfaces:**
- Consumes: `SkylinePacker.TryPack`, `LightmapUnwrap.Unwrap`, `LightmapUnwrap.Problem`, `LightmapUnwrap.ReferenceSize`, `LightmapUnwrap.Gutter`.
- Produces: every `Shapes.*` result, `Shapes.Flat` result, spline mesh and terrain tile has a valid `Uv2`. `GeneratedMesh.WithLightmapUvs()`.

A shape's chart keeps its own parameter layout (a grid's columns and rows), scaled by its world size so density is even, and placed by the packer. That is what "hand-made" means here: shapes know their pieces, so they never need the general unwrapper's guessing.

- [ ] **Step 1: Write the failing tests**

```csharp
public class ShapeLightmapTests
{
    public static TheoryData<string> AllShapes => ["box", "sphere", "hemisphere", "capsule", "cylinder", "cone", "plane", "quad", "torus", "prism", "hex", "flat sphere", "flat box"];

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void Has_a_valid_lightmap_set(string name)
    {
        var mesh = ShapeByName(name);
        Assert.Null(LightmapUnwrap.Problem(
            mesh.Vertices.Select(v => v.Position).ToArray(), mesh.Vertices.Select(v => v.Uv2).ToArray(), mesh.Indices));
    }

    [Fact]
    public void A_mesh_that_already_has_one_is_kept()
    {
        var box = Shapes.Box(Vector3.One);
        Assert.Same(box, box.WithLightmapUvs());
    }

    [Fact]
    public void A_mesh_without_one_gets_one()
    {
        var bare = Shapes.Box(Vector3.One) with { Vertices = [.. Shapes.Box(Vector3.One).Vertices.Select(v => v with { Uv2 = Vector2.Zero })] };
        var made = bare.WithLightmapUvs();
        Assert.Null(LightmapUnwrap.Problem(made.Vertices.Select(v => v.Position).ToArray(), made.Vertices.Select(v => v.Uv2).ToArray(), made.Indices));
    }
}
```

Also: a spline wall 40 m long passes `Problem`; a terrain tile's corner vertices have `Uv2` (0, 0) and (1, 1) and the tile passes `Problem`.

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Super.Tests --filter FullyQualifiedName~ShapeLightmapTests` and `dotnet test tests/Terrain.Tests`
Expected: FAIL, every shape's `Uv2` is zero ("overlap").

- [ ] **Step 3: Implement charts in `ShapeBuilder`, `Flat`, splines, terrain and `WithLightmapUvs`**

- [ ] **Step 4: Run them and make sure they pass**

Run: `dotnet test tests/Super.Tests` and `dotnet test tests/Terrain.Tests`
Expected: PASS, whole projects. Drop the gutter in `ShapeBuilder.Build`, watch the box fail, revert.

- [ ] **Step 5: Check nothing on screen changed**

Run the Shapes and Terrain examples with `--screenshot --validate --seed 1 --ticks 120 --size 1280x720`, before and after.
Expected: 0 validation messages and identical pictures, since nothing reads `Uv2` on these yet except masks, which shapes don't bake.

- [ ] **Step 6: Search docs and commit**

Run: `grep -rn "Uv2\|second UV\|WithLightmapUvs" docs/` and check each hit.

```bash
git add src/Super tests/Super.Tests tests/Terrain.Tests docs
git commit -m "feat: give built-in shapes, terrain and spline meshes lightmap UVs"
```

## Not in this plan

Calling `WithLightmapUvs` for meshes made by code when they're drawn static is the bake API plan's, since `Static` arrives there. Nothing in this plan changes a picture, so its PR has no shots; it reports the bust's chart count, density spread and unwrap time from Task 2.

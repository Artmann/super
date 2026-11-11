# Material graphs (step 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A game can describe a material as a C# graph in its content folder, and the
asset pipeline cooks it into the colour, normal, surface and height textures a material
reads. A Courtyard example shows six starter materials in a lineup view.

**Architecture:** `src/Super.Materials` is the node library and the evaluator. It's pure
managed code with no dependency on `Super` or `Rendition`, so a graph compiles against it
alone. Nodes are lazy: building a graph records nodes, and `GraphRunner` evaluates them
on the CPU at the cook size. `src/Super.Cook.Materials` is the cooker. It compiles each
`*.material.cs` alone with Roslyn, runs it, checks the values, and writes four texture
parts and a small data part. `Super` gets `content.Surface(name)` and `Surface.TileWidth`,
which `Shapes` uses as the default tiling.

**Tech Stack:** .NET 10, Microsoft.CodeAnalysis.CSharp (Roslyn), System.Text.Json,
StbImageSharp (decoding `Image()` inputs in the cooker), xunit.

**Spec:** `docs/superpowers/specs/2026-11-06-material-graphs-design.md`, step 1.

## Global Constraints

- Cooking is a pure function. The same graph, settings and seed give the same bytes on
  every run, on any number of threads.
- Graphs run on the CPU. Each node's pixels are worked out independently in
  `Parallel.For` over rows. Any sum over pixels (histograms, statistics) adds rows in
  order.
- Sine, cosine, power, exponent, log and atan2 come from `DMath` in `Super.Materials`,
  never `MathF`/`Math`. `MathF.Sqrt`, `MathF.Floor` and `MathF.Round` are exact and may
  be used.
- Images are 32-bit floats while the graph runs. They're clamped to 0..1 and rounded to
  bytes only when written. Blend modes never clamp.
- Every node wraps at the edges. Sizes and distances are fractions of the image. Counts
  of features across the image (`scale`, `columns`, `rows`) are whole numbers, so the
  pattern tiles.
- Pixel `(x, y)` has uv `((x + 0.5) / size, (y + 0.5) / size)`. Row 0 is the top of the
  image and is written first.
- Normals are OpenGL style: red is +x (right), green is +y (up the image, towards row 0).
- `Rgb` values are sRGB as authored. `#rrggbb` maps straight to 0..1 and is written
  straight back, as Substance Designer does.
- A node's seed is `new GameRandom(graph.Seed).Stream($"node {index}").NextBits()`, or
  `Stream($"node {index} seed {seed}")` when the node is given a `seed:`. `index` counts
  nodes in the order the graph makes them. Per-pixel and per-cell randomness is
  `Hash.Unit(nodeSeed, a, b)`, which is stateless.
- Default cook size 1024. A size must be a power of two from 16 to 4096.
- Graph files are `*.material.cs`, variants are `*.material.json`. Parts are `#color`,
  `#normal`, `#surface`, `#height` and `#material`.
- Braces on every `if` and `while`. `TreatWarningsAsErrors` is on.
- Conventional commits, with no Co-Authored-By lines and no Claude author.
- Docs change in the same commit as the code they describe. A new option goes in
  `docs/materials.md` or `docs/content.md`, and engine internals in `docs/design.md`.
- Work on a new branch, `feat/material-graphs`, from `main`. The first commit adds the
  spec and this plan.

## Review Focus

1. **A brick that crosses the image's edge.** `FloodFill` must count it as one island,
   so both halves get the same random value. Test in Task 5.
2. **A preview at 64 that looks different from the cook at 1024.** Every continuous
   generator and filter must give the same picture at two sizes. Shared check from
   Task 2, used in Tasks 2 to 4 and 13 to 14.
3. **A graph file the game compiles but the cooker can't**, such as one using a type
   outside the references the cooker gives it. It must be a cook error with a line, not
   a crash. Test in Task 8.
4. **A variant that names a missing graph, a graph in the wrong case, or another
   variant.** Each must be a cook error that says what to do. Test in Task 9.
5. **NaN or out-of-range values from a graph**, such as a division by zero in a `Pixel`
   lambda. They're clamped when written, NaN becomes 0, and the cook warns. Test in
   Task 8.

---

### Task 1: `Super.Materials` project, image handles and the evaluator

**Files:**
- Create: `src/Super.Materials/Super.Materials.csproj` (references `Super.Random`;
  `InternalsVisibleTo` `Super.Materials.Tests` and `Super.Cook.Materials`)
- Create: `src/Super.Materials/Image.cs`, `Node.cs`, `GraphContext.cs`, `GraphRunner.cs`,
  `DMath.cs`, `Hash.cs`, `Sampling.cs`, `Constants.cs`
- Create: `tests/Super.Materials.Tests/Super.Materials.Tests.csproj` (same xunit packages
  as `tests/Super.Cook.Tests`)
- Create: `tests/Super.Materials.Tests/EvaluatorTests.cs`, `DMathTests.cs`
- Modify: `Super.slnx` (both projects)

**Interfaces:**
- Produces:
  - `public abstract class Image` with `internal Node Node { get; }`.
  - `public sealed partial class Gray : Image`, `public sealed partial class Rgb : Image`,
    `public sealed partial class Normals : Image` (x, y, z from -1 to 1). Later tasks add
    methods to these partial classes in their own files.
  - `internal abstract class Node(int channels, params Node[] inputs)` with `int Index`,
    `ulong Seed`, `int Channels`, `Node[] Inputs`, and
    `internal abstract void Evaluate(EvaluationContext context, float[][] inputs, float[] output)`.
    Images are interleaved, `size * size * channels` floats. The constructor takes its
    index and seed from `GraphContext.Current`. A subclass that takes a `seed:` calls
    `UseSeed(int seed)`.
  - `internal sealed class EvaluationContext` with `int Size` and
    `void Rows(Action<int> row)`, which runs `Parallel.For` over `0..Size` with the
    runner's `MaxDegreeOfParallelism`.
  - `internal sealed class GraphContext` with `[ThreadStatic] Current`, `int Seed` and
    `IImageSource? Images`. `GraphContext.Use(int seed, IImageSource? images)` returns an
    `IDisposable` scope.
  - `public static Gray Gray.Of(float value)`, `public static Rgb Rgb.Of(float r, float g, float b)`,
    `public static Rgb Rgb.Of(string hex)`.
  - `internal static Gray Gray.FromArray(float[] values)` for tests (length must be
    `size * size` at evaluation, or it throws).
  - `internal static float[] GraphRunner.Evaluate(Func<Image> build, int size, int seed = 0, int? maxThreads = null)`.
  - `public static class DMath`: `Sin`, `Cos`, `Pow`, `Exp`, `Log`, `Atan2`, all `float`.
  - `internal static class Hash`: `ulong Of(ulong seed, int a, int b)` (SplitMix64
    finalizer over the three), `float Unit(ulong seed, int a, int b)` from 0 up to 1.
  - `internal static class Sampling`: `float Bilinear(float[] image, int size, int channels, int channel, float u, float v)`,
    wrapping. `int Wrap(int i, int size)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void A_node_used_twice_is_evaluated_once()
{
    // CountingNode is a test node in EvaluatorTests.cs that increments a counter in Evaluate.
    var counter = new int[1];
    GraphRunner.Evaluate(() => { var n = CountingNode.Make(counter); return n.Add(n); }, size: 8);
    Assert.Equal(1, counter[0]);
}

[Fact]
public void The_same_graph_gives_the_same_pixels_on_one_thread_and_many()
{
    Func<Image> build = () => Gray.Of(0.25f).Add(Gray.Of(0.5f));
    Assert.Equal(GraphRunner.Evaluate(build, 64, maxThreads: 1), GraphRunner.Evaluate(build, 64, maxThreads: 8));
}

[Fact]
public void Making_a_node_outside_a_graph_explains_where_nodes_are_made()
{
    var error = Assert.Throws<InvalidOperationException>(() => Gray.Of(1f));
    Assert.Equal("Nodes are made inside MaterialGraph.Build.", error.Message);
}

[Fact]
public void Rgb_of_hex_reads_srgb_values_as_written()
{
    var pixels = GraphRunner.Evaluate(() => Rgb.Of("#ff8000"), size: 1);
    Assert.Equal([1f, 128f / 255f, 0f], pixels);
}

[Fact]
public void Rgb_of_a_bad_hex_says_how_to_write_one()
{
    var error = Assert.Throws<ArgumentException>(() => GraphRunner.Evaluate(() => Rgb.Of("orange"), 1));
    Assert.Equal("Colours are written #rrggbb, not orange.", error.Message);
}
```

`Gray.Add` is a one-line `Blend` stand-in added here and kept by Task 6.

`DMathTests`: `Sin(π/2) == 1` and `Cos(0) == 1` within 1e-6, `Pow(2, 10) == 1024`
exactly, `Exp(0) == 1`, `Log(1) == 0`, `Atan2(1, 1) == π/4` within 1e-6, and each of `Sin`,
`Cos`, `Exp`, `Log`, `Atan2` within 2e-6 of `Math` (double) over 10,000 points spread over
its useful range.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Materials.Tests`
Expected: build fails, as the types don't exist.

- [ ] **Step 3: Implement the project, handles, nodes, context, runner, `DMath`, `Hash` and `Sampling`**

The runner walks the graph from the root and evaluates each node once, after its
inputs. It counts how many nodes still need each buffer, and returns a buffer to a pool
when none do, so a 2048 graph doesn't hold every intermediate image. `DMath` uses range
reduction plus minimax polynomials on `double` inside, and must not call
`Math.Sin`-family functions.

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Materials.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Materials tests/Super.Materials.Tests Super.slnx
git commit -m "feat: material graph images and evaluator"
```

---

### Task 2: Core generators

**Files:**
- Create: `src/Super.Materials/Noise.cs`, `src/Super.Materials/Tiles.cs`
- Create: `tests/Super.Materials.Tests/NodeChecks.cs`, `GeneratorTests.cs`

**Interfaces:**
- Consumes: Task 1's `Node`, `Hash`, `GraphRunner.Evaluate`.
- Produces:
  - `public static Gray Noise.Perlin(int scale, int octaves = 1, float persistence = 0.5f, int? seed = null)`.
    Octave `o` has a lattice of `scale * 2^o` cells that wraps. The output is 0 to 1.
  - `public enum CellOutput { F1, F2, Edge, Id }`.
  - `public static Gray Noise.Cells(int scale, CellOutput output = CellOutput.F1, float jitter = 1f, int? seed = null)`.
    Distances are in cell widths, clamped to 1. `Edge` is F2 − F1. `Id` is a random
    0..1 per cell. Neighbouring cells wrap at `scale`.
  - `public static Gray Noise.White(int? seed = null)`: one random value per pixel.
  - `public static Gray Tiles.Bricks(int columns, int rows, float gap = 0.04f, float bevel = 0.1f, float offset = 0.5f, float heightVariation = 0f, int? seed = null)`.
    `gap` and `bevel` are fractions of a brick's height. Each row is shifted by
    `offset` of a brick times its row number. The output is 0 in the gaps and rises over
    `bevel` to 1, then is lowered by up to `heightVariation` at random per brick.
  - `public static Gray Tiles.Grid(int columns, int rows, float gap = 0.04f, float bevel = 0.1f, float heightVariation = 0f, int? seed = null)`,
    which is `Bricks` with `offset: 0`.
  - Test helpers in `NodeChecks`: `void Tiles(Func<Image> build, int size = 64)` and
    `void SameAtSizes(Func<Image> build, int small = 64, int large = 128, float tolerance = 0.03f)`.

Scales, octaves, columns and rows below 1, and octaves above 12, throw
`ArgumentOutOfRangeException` naming the parameter and the range, such as
`"scale must be at least 1, not 0."`.

- [ ] **Step 1: Write the shared checks and the failing tests**

`NodeChecks.Tiles`: the mean of `|p[size-1, y] − p[0, y]|` over every row must be at
most 3 × the mean of `|p[x+1, y] − p[x, y]|` over every inside pair, plus 1e-4, and the
same for rows. `NodeChecks.SameAtSizes`: box-shrink the large evaluation to the small
size and compare. The mean absolute difference must be under `tolerance`.

```csharp
[Theory]
[MemberData(nameof(Generators))] // Perlin(4, 4), Cells F1, Edge and Id at 5, White, Bricks(4, 8), Grid(3, 3)
public void Every_generator_tiles(string name, Func<Image> build) => NodeChecks.Tiles(build);

[Theory]
[MemberData(nameof(SmoothGenerators))] // all of the above but White
public void Smooth_generators_look_the_same_at_two_sizes(string name, Func<Image> build) => NodeChecks.SameAtSizes(build);

[Fact]
public void Perlin_uses_the_whole_range_and_stays_in_it()
{
    var p = GraphRunner.Evaluate(() => Noise.Perlin(4, octaves: 4), 256);
    Assert.All(p, v => Assert.InRange(v, 0f, 1f));
    Assert.InRange(p.Average(), 0.45f, 0.55f);
    Assert.True(p.Min() < 0.2f && p.Max() > 0.8f);
}

[Fact]
public void Cells_without_jitter_put_one_point_in_the_middle_of_each_cell()
{
    // Size 64, scale 1: pixel (31, 31) has uv (0.5 - 1/128) on both axes, and pixel (0, 0) is 0.5 - 1/128 from the middle.
    var p = GraphRunner.Evaluate(() => Noise.Cells(1, jitter: 0f), 64);
    Assert.Equal(MathF.Sqrt(2f) / 128f, p[31 * 64 + 31], 4);
    Assert.Equal(MathF.Sqrt(2f) * (0.5f - 1f / 128f), p[0], 4);
}

[Fact]
public void Bricks_are_raised_with_gaps_between_and_alternate_rows_shifted()
{
    // 2 columns, 2 rows at size 64: bricks are 32 by 32. A gap of 0.25 is 8 pixels split around each seam.
    var p = GraphRunner.Evaluate(() => Tiles.Bricks(2, 2, gap: 0.25f, bevel: 0f, offset: 0.5f), 64);
    Assert.Equal(1f, p[16 * 64 + 16]);  // middle of the first brick in row 0
    Assert.Equal(0f, p[16 * 64 + 0]);   // the seam at x = 0 in row 0
    Assert.Equal(1f, p[48 * 64 + 0]);   // row 1 is shifted half a brick, so x = 0 is mid-brick
    Assert.Equal(0f, p[48 * 64 + 16]);  // and its seam is at x = 16
}

[Fact]
public void A_node_given_its_own_seed_changes_without_changing_its_neighbours()
{
    float[] Second(int seed) => GraphRunner.Evaluate(() => { Noise.Perlin(4); return Noise.Perlin(4, seed: seed); }, 32);
    float[] First(int seed) => GraphRunner.Evaluate(() => { var a = Noise.Perlin(4); Noise.Perlin(4, seed: seed); return a; }, 32);
    Assert.NotEqual(Second(1), Second(2));
    Assert.Equal(First(1), First(2));
}

[Fact]
public void A_scale_of_zero_names_the_parameter()
{
    var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Noise.Perlin(0), 8));
    Assert.StartsWith("scale must be at least 1, not 0.", error.Message);
}
```

Also a `[Fact]` that the graph seed changes Perlin's pixels.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Materials.Tests --filter GeneratorTests`
Expected: build fails.

- [ ] **Step 3: Implement `Noise` and `Tiles`**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Materials.Tests --filter GeneratorTests`
Expected: all pass. Then break the wrap in `Perlin` on purpose (drop the modulo on the
lattice), see `Every_generator_tiles` fail, and put it back.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Materials tests/Super.Materials.Tests
git commit -m "feat: perlin, cells, white noise and brick generators"
```

---

### Task 3: Core value filters and blur

**Files:**
- Create: `src/Super.Materials/Filters.cs`, `src/Super.Materials/Blur.cs`
- Create: `tests/Super.Materials.Tests/FilterTests.cs`

**Interfaces:**
- Produces, on `Gray`:
  - `Gray Levels(float inLow = 0f, float inHigh = 1f, float outLow = 0f, float outHigh = 1f, float gamma = 1f)`:
    `t = clamp((v − inLow) / (inHigh − inLow), 0, 1)`, then `outLow + (outHigh − outLow) × t^(1/gamma)`.
  - `Gray Remap(float low, float high)`: `low + (high − low) × v`, not clamped.
  - `Gray Invert()`: `1 − v`.
  - `Gray HistogramScan(float position, float contrast = 0.5f)`: with `t = 1 − position`
    and `w = max(1 − contrast, 0.001)`, `clamp((v − t) / w + 0.5, 0, 1)`.
  - `Gray Blur(float radius)`: Gaussian with a standard deviation of `radius × size`
    pixels, cut at 3 deviations, separable, wrapping. `radius` 0 copies.
- Produces, on `Rgb`: `Rgb Blur(float radius)`, the same per channel.

- [ ] **Step 1: Write the failing tests**

Hand-worked values: `Levels(0.2f, 0.6f)` maps 0.4 to 0.5 and 0.1 to 0. `Levels(gamma: 2)`
maps 0.25 to 0.5. `Remap(0.85f, 1f)` maps 0 to 0.85 and 1 to 1. `Invert` maps 0.3 to 0.7.

```csharp
[Fact]
public void Histogram_scan_at_full_contrast_keeps_the_share_it_is_given()
{
    // A gradient where pixel i of 100 has value i / 100: position 0.3 leaves 30 white pixels.
    var p = GraphRunner.Evaluate(() => Gray.FromArray(Ramp100x100()).HistogramScan(0.3f, contrast: 1f), 100);
    Assert.Equal(30 * 100, p.Count(v => v > 0.5f));
}

[Fact]
public void Blur_keeps_the_total_and_spreads_across_the_edge()
{
    var impulse = new float[64 * 64]; impulse[0] = 1f;
    var p = GraphRunner.Evaluate(() => Gray.FromArray(impulse).Blur(0.02f), 64);
    Assert.Equal(1f, p.Sum(), 3);
    Assert.Equal(p[1], p[63], 6);          // left and right of pixel 0 match, through the wrap
    Assert.Equal(p[64], p[63 * 64], 6);    // and above and below
}

[Fact]
public void Blurring_a_flat_image_changes_nothing() { /* Gray.Of(0.4f).Blur(0.1f) is 0.4 everywhere, within 1e-6 */ }
```

Plus `NodeChecks.Tiles` and `NodeChecks.SameAtSizes` for `Noise.Perlin(4).Blur(0.02f)`.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Materials.Tests --filter FilterTests`
Expected: build fails.

- [ ] **Step 3: Implement the filters and blur**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Materials.Tests --filter FilterTests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Materials tests/Super.Materials.Tests
git commit -m "feat: levels, remap, histogram scan and blur nodes"
```

---

### Task 4: Warp and slope blur

**Files:**
- Create: `src/Super.Materials/Warps.cs`
- Create: `tests/Super.Materials.Tests/WarpTests.cs`

**Interfaces:**
- Produces, on `Gray` and on `Rgb`:
  - `Warp(Gray by, float amount)`: each pixel reads from `uv − amount × ∇by`, bilinear and
    wrapping. `∇by` is the central difference per image width, so a 0 to 1 ramp across
    the whole image moves pixels by `amount` of the image.
  - `public enum SlopeBlurMode { Average, Min, Max }`.
  - `SlopeBlur(Gray slope, float amount, int samples = 16, SlopeBlurMode mode = SlopeBlurMode.Average)`:
    takes `samples` reads along `normalize(∇slope)` at `i × amount / samples` for
    `i = 0..samples−1`, and combines them by `mode`. Where `∇slope` is zero, it copies.

- [ ] **Step 1: Write the failing tests**

- `Warp_by_a_flat_image_changes_nothing`, and `Warp_with_amount_zero_changes_nothing`,
  both over `Noise.Perlin(4)`, exactly equal.
- `Warp_moves_pixels_down_the_slope`: input is vertical stripes 8 pixels wide at size 64.
  `by` is `sin(2πu)` from `FromArray`, and amount 0.01. At u near 0, the slope per image
  is 2π, so the pixel at x reads from x − 0.01 × 2π × 64 ≈ x − 4.02. Assert the value
  at pixel 2 equals the bilinear read at 2 − 4.02 (wrapping) within 1e-3.
- `Slope_blur_only_blurs_along_the_slope`: input is vertical stripes. `slope` varies in u
  only, so the blur runs along x. The stripes' contrast (max − min along a row) drops by
  more than half. Then the same input turned into horizontal stripes is unchanged.
- `Slope_blur_min_never_raises_a_pixel`: every output ≤ input for `SlopeBlurMode.Min`.
- `NodeChecks.Tiles` and `SameAtSizes` for `Noise.Perlin(4).Warp(Noise.Perlin(2), 0.05f)`
  and for `Tiles.Bricks(4, 8).SlopeBlur(Noise.Cells(6), 0.02f)`.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Materials.Tests --filter WarpTests`
Expected: build fails.

- [ ] **Step 3: Implement `Warp` and `SlopeBlur`**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Materials.Tests --filter WarpTests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Materials tests/Super.Materials.Tests
git commit -m "feat: warp and slope blur nodes"
```

---

### Task 5: Flood fill

**Files:**
- Create: `src/Super.Materials/FloodFill.cs`
- Create: `tests/Super.Materials.Tests/FloodFillTests.cs`

**Interfaces:**
- Produces:
  - `public sealed partial class Islands : Image` (internal channels: island number per
    pixel, −1 for background).
  - `Islands Gray.FloodFill(float threshold = 0f)`: pixels above `threshold` that touch
    on a side are one island, wrapping at all four edges. It uses union-find over rows in
    order, so island numbers are deterministic.
  - `Gray Islands.RandomGray(int? seed = null)`: one value from 0 to 1 per island, from
    `Hash.Unit(seed, qx, qy)`, where `(qx, qy)` is the island's first pixel in row order,
    as uv × 1024 rounded down. Background is 0. Values can differ between sizes, and
    `materials.md` says so.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void A_brick_across_the_edge_is_one_island()
{
    // Bricks(2, 2) with offset 0.5 at size 64: row 1's brick at x = 48..63 wraps to x = 0..15.
    var p = GraphRunner.Evaluate(() => Tiles.Bricks(2, 2, gap: 0.25f, bevel: 0f).FloodFill().RandomGray(), 64);
    Assert.Equal(p[48 * 64 + 2], p[48 * 64 + 60]);
    Assert.Equal(4, Distinct(p, background: 0f)); // two in row 0, two in row 1 (one of them wraps)
}

[Fact]
public void Separate_islands_get_different_values() { /* two 8x8 squares in FromArray get two different values */ }

[Fact]
public void Background_stays_zero() { /* pixels at or below the threshold are 0 */ }
```

`Distinct` counts distinct non-background values. Plus `NodeChecks.Tiles` on the
bricks' `RandomGray`, which must hold because of the wrap.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Materials.Tests --filter FloodFillTests`
Expected: build fails.

- [ ] **Step 3: Implement `FloodFill` and `RandomGray`**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Materials.Tests --filter FloodFillTests`
Expected: all pass. Then turn off the wrap in the union-find, see
`A_brick_across_the_edge_is_one_island` fail, and put it back.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Materials tests/Super.Materials.Tests
git commit -m "feat: flood fill node with wrapping islands"
```

---

### Task 6: Blending and colour

**Files:**
- Create: `src/Super.Materials/Blend.cs`, `src/Super.Materials/Color.cs`
- Create: `tests/Super.Materials.Tests/BlendTests.cs`, `ColorTests.cs`

**Interfaces:**
- Produces:
  - `public enum BlendMode { Copy, Add, Subtract, Multiply, Min, Max, Overlay, Screen }`.
  - `Gray Gray.Blend(Gray top, BlendMode mode = BlendMode.Copy, Gray? mask = null, float opacity = 1f)`:
    `lerp(base, f(base, top), mask × opacity)`, never clamped. Overlay is
    `base < 0.5 ? 2ab : 1 − 2(1−a)(1−b)`. Screen is `1 − (1−a)(1−b)`.
  - `Gray.Add`, `Subtract`, `Multiply`, `Min`, `Max`, each `(Gray other)` and
    `(float value)`, as shorthand for `Blend`.
  - `Rgb Rgb.Blend(Rgb top, BlendMode mode = BlendMode.Copy, Gray? mask = null, float opacity = 1f)`,
    and `Rgb.Multiply(Gray other)`, `Rgb.Multiply(Rgb other)`.
  - `public sealed record HeightBlendResult(Gray Height, Gray Mask)`.
  - `static HeightBlendResult Gray.HeightBlend(Gray bottom, Gray top, float offset = 0.5f, float contrast = 0.9f)`:
    `d = top + (offset − 0.5) − bottom`, `mask = clamp(d / max(1 − contrast, 0.001) + 0.5, 0, 1)`,
    `height = lerp(bottom, top + offset − 0.5, mask)`.
  - `public sealed class Ramp` with `static Ramp Of(params string[] hex)` (evenly spaced)
    and `static Ramp Of(params (float At, string Hex)[] stops)`.
  - `Rgb Gray.GradientMap(Ramp ramp)`: linear between stops, clamping the input to 0..1.

- [ ] **Step 1: Write the failing tests**

- One `[Theory]` row per blend mode with base 0.25, top 0.75 and the hand result: Copy
  0.75, Add 1.0, Subtract −0.5, Multiply 0.1875, Min 0.25, Max 0.75, Overlay 0.375,
  Screen 0.8125.
- `A_mask_of_zero_keeps_the_base` and `Half_opacity_lands_halfway`.
- `Add_does_not_clamp`: 0.75 + 0.75 = 1.5.
- Height blend: top 0.8 over bottom 0.2 gives mask 1 and height 0.8. Top 0.2 over
  bottom 0.8 gives mask 0 and height 0.8. Equal heights give mask 0.5.
- `Ramp.Of("#000000", "#ffffff", "#ff0000")`: 0 → black, 0.5 → white, 1 → red, 0.25 →
  (0.5, 0.5, 0.5).
- `Ramp_stops_must_go_up`: `Ramp.Of((0.5f, "#000000"), (0.2f, "#ffffff"))` throws
  `ArgumentException` with `"Ramp stops must be in order: 0.2 comes after 0.5."`.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Materials.Tests --filter "BlendTests|ColorTests"`
Expected: build fails.

- [ ] **Step 3: Implement blending, height blend, ramps and gradient map**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Materials.Tests --filter "BlendTests|ColorTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Materials tests/Super.Materials.Tests
git commit -m "feat: blend, height blend and gradient map nodes"
```

---

### Task 7: Normals, occlusion, `Material` and `MaterialGraph`

**Files:**
- Create: `src/Super.Materials/SurfaceNodes.cs`, `Material.cs`, `MaterialGraph.cs`, `MaterialImages.cs`
- Modify: `src/Super.Materials/GraphRunner.cs`
- Create: `tests/Super.Materials.Tests/SurfaceNodeTests.cs`, `MaterialTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1 to 6.
- Produces:
  - `Normals Gray.HeightToNormal(float width, float depth)`: a Sobel filter (weights
    1, 2, 1, divided by 8), with height in metres as `v × depth` and a pixel being
    `width / size` metres. The result is `normalize(−sx, −sy, 1)`, where `sy` is the
    rise towards row 0.
  - `Gray Gray.HeightToOcclusion(float width, float depth, float radius = 0.05f)`:
    horizon-based, 8 directions × 8 steps out to `radius` of the image. 1 is open.
  - `public sealed record Material` with `float Width = 1f`, `float Depth = 0.02f`,
    `Gray? Height`, `Rgb? Color`, `Gray? Roughness`, `Gray? Metallic`, `Gray? Opacity`,
    `Normals? Normal`, `float Occlusion = 0.5f`, all `init`.
  - `public abstract class MaterialGraph` with `public int Seed { get; init; }`,
    `public abstract Material Build()`, and `protected static Rgb Image(string path)`
    (Task 9 gives it a body; until then it throws `NotSupportedException`).
  - `public sealed record MaterialImages(int Size, float Width, float[] Color, float[] Normal, float[] Roughness, float[] Metallic, float[] Opacity, float[] Height)`.
  - `public static MaterialImages GraphRunner.Run(MaterialGraph graph, int size, IImageSource? images = null, int? maxThreads = null)`.
  - `public interface IImageSource { DecodedImage Read(string path); }` and
    `public sealed record DecodedImage(int Width, int Height, float[] Rgba)`.

What `Run` does with a `Material`:
- No `Height` means 0.5 everywhere.
- The normal is `Height.HeightToNormal(Width, Depth)`. If `Normal` is given too, it's laid
  over that with reoriented normal mapping. Task 14's `NormalCombine` reuses the same
  function.
- `Color` defaults to 0.5 grey and is multiplied by `lerp(1, occlusion, Occlusion)`.
- `Roughness` defaults to 0.6, `Metallic` to 0, and `Opacity` to 1.
- `Width` ≤ 0, `Depth` < 0, or `Occlusion` outside 0..1 throws `ArgumentOutOfRangeException`
  naming the property.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void A_flat_height_points_straight_out() { /* Gray.Of(0.3f).HeightToNormal(1, 0.1f) is (0, 0, 1) everywhere */ }

[Fact]
public void A_slope_in_metres_tilts_the_normal_by_the_hand_worked_amount()
{
    // h = 0.5 + 0.5 sin(2πu) at size 64, width 1 m, depth 0.1 m. At u near 0 the slope is
    // 0.5 × 2π × 0.1 = 0.314, shortened by the finite difference to 0.3138,
    // so x = −0.3138 / sqrt(1 + 0.3138²) = −0.2994.
    var n = GraphRunner.Evaluate(() => Gray.FromArray(SineInU(64)).HeightToNormal(1f, 0.1f), 64);
    Assert.Equal(-0.2994f, n[(10 * 64 + 0) * 3], 2);
}

[Fact]
public void Height_rising_towards_the_top_of_the_image_turns_green_down()
{
    // h rises as the row number falls. The face looks down the image, so y < 0.
    var n = GraphRunner.Evaluate(() => Gray.FromArray(RampTowardsRowZero(64)).HeightToNormal(1f, 0.1f), 64);
    Assert.True(n[(32 * 64 + 32) * 3 + 1] < 0f);
}

[Fact]
public void A_flat_height_is_not_occluded() { /* HeightToOcclusion of Gray.Of(0.5f) is 1 everywhere */ }

[Fact]
public void The_bottom_of_a_pit_is_darker_than_its_rim() { /* a cone pit from FromArray: centre < 0.8 × rim */ }

[Fact]
public void Running_a_material_fills_in_the_defaults()
{
    var images = GraphRunner.Run(new TestGraph(() => new Material()), 8);
    Assert.All(images.Roughness, v => Assert.Equal(0.6f, v));
    Assert.All(images.Metallic, v => Assert.Equal(0f, v));
    Assert.All(images.Opacity, v => Assert.Equal(1f, v));
    Assert.Equal(0.5f, images.Color[0]);
}

[Fact]
public void Occlusion_zero_leaves_the_colour_alone() { /* a pit height, Occlusion = 0: colour stays 0.5 everywhere */ }

[Fact]
public void A_negative_width_names_the_property() { /* message starts "Width must be more than 0, not -1." */ }

[Fact]
public void The_graph_seed_reaches_the_nodes() { /* TestGraph with Seed 1 vs 2 on Perlin height gives different Height arrays */ }
```

`TestGraph` is a `MaterialGraph` in the test project that runs a lambda in `Build`.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Materials.Tests --filter "SurfaceNodeTests|MaterialTests"`
Expected: build fails.

- [ ] **Step 3: Implement the surface nodes, `Material`, `MaterialGraph` and `GraphRunner.Run`**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Materials.Tests`
Expected: all pass. Flip the sign of `sy` on purpose, see the green test fail, and put
it back.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Materials tests/Super.Materials.Tests
git commit -m "feat: normals, occlusion and the material output"
```

---

### Task 8: The material cooker

**Files:**
- Create: `src/Super.Cook.Materials/Super.Cook.Materials.csproj` (references `Super.Cook`,
  `Super.Materials`, `Microsoft.CodeAnalysis.CSharp`)
- Create: `src/Super.Cook.Materials/MaterialCooker.cs`, `GraphCompiler.cs`, `MaterialWriter.cs`
- Modify: `src/Super/Super.csproj` (`InternalsVisibleTo` `Super.Cook.Materials`, for `CookedTexture`)
- Modify: `src/Super.Cook.Models/Super.Cook.Models.csproj` and `ModelCooker.cs:560-563`
  (`ModelCookers.All` becomes `[.. BuiltInCookers.All, new MaterialCooker(), new ModelCooker()]`)
- Modify: `src/Super.Testing/Super.Testing.csproj` and `MemoryContent.cs:122` (cook with
  `[.. BuiltInCookers.All, new MaterialCooker()]`)
- Modify: `tests/Super.Cook.Tests/Super.Cook.Tests.csproj`, `ModelCookerTests.cs:306`
  (the list of cooker ids now includes `material`)
- Modify: `Super.slnx`
- Create: `tests/Super.Cook.Tests/MaterialCookerTests.cs`
- Docs: create `docs/materials.md` (writing a graph, the parts, sizes, errors). Add the
  `*.material.cs` convention to `docs/content.md`.

**Interfaces:**
- Consumes: `GraphRunner.Run`, `MaterialImages`, `MaterialGraph`.
- Produces:
  - `public sealed class MaterialCooker : ICooker` with `Id => "material"`, `Extensions => [".material.cs", ".material.json"]`.
    `Version` is folded from the `ModuleVersionId`s of `Super.Materials` and
    `Super.Cook.Materials` plus a format number of 1, so rebuilding either library with
    a change cooks every graph again.
  - `public sealed record MaterialSettings { public int Size { get; init; } = 1024; }`,
    read with `input.SettingsAs<MaterialSettings>()`.
  - `internal static class GraphCompiler` with
    `static CompiledGraph Compile(string name, byte[] source, CookOutput output)`, which
    returns null after reporting errors. It caches by the SHA-256 of the source, keeping
    up to 32 entries. `CompiledGraph` holds the graph `Type` and the collectible
    `AssemblyLoadContext`.
  - `internal static class MaterialWriter` with
    `static void Write(MaterialImages images, string name, CookOutput output)`.

Compiling and running:
- Parse with the path set to the source's name, so stack frames and diagnostics carry
  it. Reference `Super.Materials`, `Super.Random`, `System.Private.CoreLib`,
  `System.Runtime`, `System.Numerics.Vectors`, `System.Linq` and `System.Collections`,
  from `TRUSTED_PLATFORM_ASSEMBLIES`.
- Emit with an embedded portable PDB, and load into a collectible
  `AssemblyLoadContext` that hands back the cooker's own `Super.Materials` and
  `Super.Random`, so `MaterialGraph` is the same type.
- Each compile error is `output.Error(message, line)`, with the Roslyn line + 1.
- The file must hold exactly one public non-abstract class deriving from
  `MaterialGraph`. Otherwise the error is `"{name} must hold one class that derives from MaterialGraph. It has none."`
  or `"... It has 2: Bricks, Stones."` (names in ordinal order).
- An exception thrown while building or running is an error with the exception's
  message and the line of the first stack frame in the graph file. If there's no such
  frame, it has no line.
- Size not a power of two from 16 to 4096 is the error
  `"{name}: size must be a power of two from 16 to 4096, not 100."`

What gets written, in the existing `CookedTexture` format (`AddPart`):
- `#color`: RGB from `Color`, alpha 255, `TextureKind.Color`.
- `#normal`: `n × 0.5 + 0.5`, alpha 255, `TextureKind.Linear`, normal map.
- `#surface`: red is metalness, green roughness, blue 0, alpha opacity. `TextureKind.Linear`.
- `#height`: grey, alpha 255, `TextureKind.Linear`.
- `#material`: `ContentKind.Data`, the JSON `{"width":<Width>}` written with invariant
  culture.
- Bytes are `(byte)Math.Clamp((int)MathF.Round(v × 255, MidpointRounding.AwayFromZero), 0, 255)`,
  and NaN is 0. If any value in an image is NaN, the cook warns
  `"{name}: {part} has pixels that aren't numbers. They were written as 0."`

- [ ] **Step 1: Write the failing tests**

All tests put `cook.json` with `{ "rules": [{ "match": "*.material.*", "material": { "size": 16 } }] }`
in a `MemoryRoot`, so they cook fast. `Bricks` below is a short graph string in the test file.

```csharp
[Fact]
public void A_graph_cooks_into_four_textures_and_its_width()
{
    var (result, store) = Cook(("bricks.material.cs", Bricks));
    Assert.True(result.Succeeded, string.Join("; ", result.Problems));
    var color = Texture(store, "bricks.material.cs#color");
    Assert.Equal((16, 16, TextureKind.Color, false), (color.Width, color.Height, color.Kind, color.NormalMap));
    Assert.True(Texture(store, "bricks.material.cs#normal").NormalMap);
    Assert.Equal(TextureKind.Linear, Texture(store, "bricks.material.cs#surface").Kind);
    Assert.Equal(TextureKind.Linear, Texture(store, "bricks.material.cs#height").Kind);
    Assert.Equal("""{"width":2}""", Encoding.UTF8.GetString(Data(store, "bricks.material.cs#material")));
}

[Fact]
public void Cooking_twice_gives_the_same_bytes() { /* two fresh pipelines, no cache: every part's hash matches */ }

[Fact]
public void A_compile_error_names_the_line()
{
    var (result, _) = Cook(("bad.material.cs", "using Super.Materials;\npublic sealed class Bad : MaterialGraph\n{\n    public override Material Build() => new Material { Width = nope };\n}\n"));
    var error = Assert.Single(result.Problems, p => p.IsError);
    Assert.Equal(4, error.Line);
    Assert.Contains("nope", error.Message);
}

[Fact]
public void A_type_the_cooker_does_not_reference_is_a_cook_error_not_a_crash() { /* uses System.Net.Http.HttpClient: error on its line */ }

[Fact]
public void A_file_with_no_graph_class_says_what_it_needs() { /* exact message "...It has none." */ }

[Fact]
public void A_file_with_two_graph_classes_names_both() { /* exact message "...It has 2: A, B." */ }

[Fact]
public void A_graph_that_throws_names_its_line_and_message() { /* throw new InvalidOperationException("boom") on line 6: line 6, message contains "boom" */ }

[Fact]
public void Nan_is_written_as_zero_with_a_warning()
{
    // NanHeight sets Height = Gray.Of(float.NaN). The normal and colour may warn too,
    // so this only looks for the height's warning.
    var (result, store) = Cook(("nan.material.cs", NanHeight));
    Assert.Contains(result.Problems, p => !p.IsError && p.Message == "nan.material.cs: height has pixels that aren't numbers. They were written as 0.");
    Assert.All(Texture(store, "nan.material.cs#height").Pixels.Where((_, i) => i % 4 == 0), b => Assert.Equal(0, b));
}

[Fact]
public void Values_outside_zero_to_one_are_clamped() { /* Color = Rgb.Of(2, -1, 0.5f): bytes 255, 0, 128 */ }

[Fact]
public void A_size_that_is_not_a_power_of_two_is_an_error() { /* exact message with 100 */ }

[Fact]
public void Linq_works_in_a_graph() { /* a graph that sums with Enumerable.Range cooks */ }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Cook.Tests --filter MaterialCookerTests`
Expected: build fails.

- [ ] **Step 3: Implement the cooker, compiler and writer, and register them**

- [ ] **Step 4: Run every cook test and see them pass**

Run: `dotnet test tests/Super.Cook.Tests`
Expected: all pass, including `ModelCookerTests` with the new id list.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Cook.Materials src/Super src/Super.Cook.Models src/Super.Testing tests/Super.Cook.Tests Super.slnx docs/materials.md docs/content.md
git commit -m "feat: cook material graphs into textures"
```

---

### Task 9: Variants and image inputs

**Files:**
- Modify: `src/Super.Cook.Materials/MaterialCooker.cs`
- Create: `src/Super.Cook.Materials/Variant.cs`, `src/Super.Cook.Materials/CookImages.cs`
- Modify: `src/Super.Materials/MaterialGraph.cs`, `src/Super.Materials/GraphRunner.cs`
  (an `Image` node that resamples a `DecodedImage` bilinearly with wrap)
- Test: `tests/Super.Cook.Tests/MaterialCookerTests.cs`
- Docs: variants and `Image()` in `docs/materials.md`.

**Interfaces:**
- Produces:
  - A `.material.json` reads `{"graph": "<path from the variant>", "seed": <int>, "<parameter>": <value>, ...}`.
    The graph is read with `input.Read`, so a change to it cooks the variant again.
    Parameters are the graph class's public `init` properties of type `float`, `int`,
    `bool` or `string`, plus `Seed`. JSON names match property names regardless of case.
  - `internal sealed class CookImages(CookInput input) : IImageSource`, which reads with
    `input.Read` and decodes with StbImageSharp to RGBA floats from 0 to 1.
  - `MaterialGraph.Image(string path)` reads through `GraphContext.Current.Images`.

Errors, exactly:
- No `graph`: `"{name} needs \"graph\", the .material.cs file it sets values for."`
- `graph` names a `.material.json`: `"{name}: graph must be a .material.cs file, not {graph}."`
- Unknown parameter: `"{name}: Bricks has no parameter called mos. It has moss, mortar, seed."`
  (lower-case names in ordinal order)
- Wrong type: `"{name}: moss is a number, not \"lots\"."`, and `is true or false` /
  `is a whole number` / `is text` for the other types.
- A missing graph or a case mismatch is the `FileNotFoundException` message from
  `CookInput.Read`, as an error.

- [ ] **Step 1: Write the failing tests**

- `A_variant_changes_the_pixels`: `mossy.material.json` with `"moss": 0.9` cooks
  `#color` bytes that differ from the plain graph's.
- `A_variant_cooks_again_when_its_graph_changes`: cook with a cache folder, change the
  graph's bytes (a different default), cook again, and the variant's `#color` hash
  changes.
- One test for each error above, checking the exact message, including
  `A_graph_named_in_the_wrong_case_says_so`
  (`"graph": "Bricks.material.cs"` beside `bricks.material.cs`; the message contains
  `"but bricks.material.cs is. Names are case-sensitive."`).
- `Seed_in_a_variant_reaches_the_graph`: the same graph with seeds 1 and 2 gives
  different `#height` bytes.
- `An_image_input_is_tracked`: a graph using `Image("rock.png").ToGray()` (Task 14 adds
  `ToGray`, so this test uses `Image("rock.png").Multiply(Gray.Of(1f))` as colour), cooks
  with a cache, then a changed `rock.png` cooks it again.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Cook.Tests --filter MaterialCookerTests`
Expected: the new tests fail.

- [ ] **Step 3: Implement variants and image inputs**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Cook.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Cook.Materials src/Super.Materials tests/Super.Cook.Tests docs/materials.md
git commit -m "feat: material variants and image inputs"
```

---

### Task 10: Checks on the values

**Files:**
- Create: `src/Super.Cook.Materials/MaterialChecks.cs`
- Modify: `src/Super.Cook.Materials/MaterialCooker.cs`
- Test: `tests/Super.Cook.Tests/MaterialCookerTests.cs`
- Docs: a "Checks" section in `docs/materials.md`.

**Interfaces:**
- Produces: `internal static IEnumerable<string> MaterialChecks.Problems(MaterialImages images, string name)`.
  Each one is reported as a warning.

The rules and their exact messages (percentages rounded to whole numbers):
- Pixels whose brightest channel is below 30/255, if more than 10% of them:
  `"{name}: 23% of the base colour is darker than sRGB 30. Nothing real is that dark. Charcoal is about 50."`
- Pixels whose darkest channel is above 240/255, if more than 10% of them:
  `"{name}: 15% of the base colour is brighter than sRGB 240. Fresh snow is about 240."`
- More than half the metalness between 0.1 and 0.9:
  `"{name}: most of the metalness is between 0.1 and 0.9. Surfaces are metal or not, and in-between values look like plastic."`
- Roughness standard deviation below 0.02:
  `"{name}: the roughness hardly varies. Real surfaces have smudges and wear, so vary it by a few percent at least."`

Statistics add rows in order, as the global constraints say.

- [ ] **Step 1: Write the failing tests**

One test per rule that triggers it with constant values (colour `#101010`, colour
`#f8f8f8`, metallic 0.5, roughness 0.6 flat) and checks the exact message. Then
`A_good_material_has_no_warnings`, with colour from Perlin through a mid-grey ramp,
roughness from Perlin remapped to 0.5..0.8, and metallic 0.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Cook.Tests --filter MaterialCookerTests`
Expected: the new tests fail.

- [ ] **Step 3: Implement `MaterialChecks` and report them in the cooker**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Cook.Tests`
Expected: all pass. The tests from Tasks 8 and 9 whose graphs have flat roughness now
also get a warning. Those tests only look at errors or at their own warning, so they
still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Cook.Materials tests/Super.Cook.Tests docs/materials.md
git commit -m "feat: warn about unrealistic material values"
```

---

### Task 11: `content.Surface` and tiling at the material's width

**Files:**
- Modify: `src/Super/Surface.cs` (add `TileWidth`)
- Modify: `src/Super/Content.cs` (add `Surface(string name)`)
- Modify: `src/Super/Shapes.cs:17-46, 164-198, 349-357, 432` (every shape that takes
  `uvPerMetre`)
- Test: `tests/Super.Tests/ContentTests.cs`, `tests/Super.Tests/ShapesTests.cs`
- Docs: `docs/rendering.md` where `uvPerMetre` is described, `docs/content.md`, and
  `docs/materials.md`.

**Interfaces:**
- Consumes: the parts from Task 8, through `MemoryContent` (which cooks graphs since Task 8).
- Produces:
  - `public float? TileWidth { get; init; }` on `Surface`: how many metres the textures
    cover. Null for surfaces not from a graph.
  - `public Surface Surface(string name)` on `Content`. It returns `Color = Vector3.One`,
    `Roughness = 1f`, `Metallic = 1f` (the surface texture's values pass straight
    through, because the material multiplies them), the three textures from `#color`,
    `#normal` and `#surface`, and `TileWidth` from `#material`. A name without a
    `#material` part throws `ContentException` with
    `"{name} is not a cooked material. Materials are .material.cs and .material.json files."`.
    A name that isn't there at all gets the usual `ContentNames.Missing` message.
  - In `Shapes`, the tiling is `uvPerMetre` if given, else `1 / surface.TileWidth` if
    set, else stretched once, as now.

- [ ] **Step 1: Write the failing tests**

- `Surface_from_a_graph_has_its_textures_and_width`: a `MemoryContent` holding a small
  graph with `Width = 2f` and a size rule of 16. The surface's three texture refs are
  the refs that `content.Texture("...#color")` and so on give, and `TileWidth` is 2.
- `Surface_of_a_texture_explains_what_a_material_is`: `content.Surface("a.png")` throws
  with the exact message.
- `A_plane_tiles_at_its_surfaces_width`:
  `Shapes.Plane(new Vector2(6f, 4f), surface: new Surface { TileWidth = 2f })`. The
  largest UV is (3, 2).
- `A_given_uv_per_metre_beats_the_surfaces_width`: the same with `uvPerMetre: 1f` gives
  (6, 4).
- `Without_a_width_a_plane_stretches_its_texture_once`: (1, 1).
- `A_cylinder_tiles_at_its_surfaces_width`, the same idea for the path at
  `Shapes.cs:432`.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Tests --filter "ContentTests|ShapesTests"`
Expected: build fails.

- [ ] **Step 3: Implement `TileWidth`, `Content.Surface` and the default in `Shapes`**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Super tests/Super.Tests docs
git commit -m "feat: content.Surface and real-size tiling for material graphs"
```

---

### Task 12: Graphs compile in the game for autocomplete

**Files:**
- Modify: `src/Super.Build/Super.targets`
- Docs: `docs/materials.md` (autocomplete, unique class names, graphs outside the
  game's folder), and `docs/design.md` (why each graph compiles alone and also in the game).

**Interfaces:**
- Produces: every game importing `Super.targets` gets
  `<ProjectReference Include="$(MSBuildThisFileDirectory)..\Super.Materials\Super.Materials.csproj" Private="false" PrivateAssets="all" />`,
  so graph files in its folder compile against `Super.Materials`. The library isn't
  copied to the game's output.

- [ ] **Step 1: Make a throwaway check**

Add `examples/Crypt/content/materials/check.material.cs` with a tiny valid graph.

- [ ] **Step 2: See it fail to build**

Run: `dotnet build examples/Crypt`
Expected: CS0246, `MaterialGraph` not found.

- [ ] **Step 3: Add the reference to `Super.targets`**

- [ ] **Step 4: See it build, cook, and stay out of the output**

Run: `dotnet build examples/Crypt`, then
`ls examples/Crypt/bin/Debug/net10.0/Super.Materials.dll`.
Expected: the build succeeds and cooks `materials/check.material.cs#color`, and the
`ls` finds no such file. Then put a type error in the graph and see the build fail on
that line. Delete `check.material.cs`.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Build/Super.targets docs
git commit -m "feat: material graphs compile in the game for autocomplete"
```

---

### Task 13: More generators and transforms

**Files:**
- Modify: `src/Super.Materials/Noise.cs`
- Create: `src/Super.Materials/Shapes.cs`, `src/Super.Materials/Transform.cs`
- Test: `tests/Super.Materials.Tests/GeneratorTests.cs`, `TransformTests.cs`
- Docs: the node table in `docs/materials.md`.

**Interfaces:**
- Produces:
  - `Noise.Directional(int scale, float angle = 0f, float stretch = 8f, int octaves = 3, int? seed = null)`:
    Perlin squashed by `stretch` along `angle` (degrees, counter-clockwise from +x). To
    stay tiling, `angle` snaps to the nearest direction whose rise and run in cells are
    whole numbers no larger than 8, and `materials.md` says so.
  - `public enum ShapeKind { Disc, Square, Gradient, Bell, Pyramid }` and
    `Shape.Of(ShapeKind kind, float size = 1f)`: one shape centred in the image,
    `size` its width as a fraction of the image.
  - `Gradient.Linear(float angle = 0f)` (0 to 1 across one image, tiling as a
    triangle wave), `Gradient.Radial()` (1 at the middle to 0 at the corners).
  - `Gray.Splatter(int count, float sizeMin = 0.05f, float sizeMax = 0.1f, bool rotate = true, BlendMode mode = BlendMode.Max, int? seed = null)`:
    stamps this image, shrunk, at `count` random places, combined with `mode`.
    Stamps that cross an edge wrap.
  - `Gray.Transform(Vector2 offset = default, float rotate = 0f, float scale = 1f, int tile = 1)`
    and the same on `Rgb`. `tile` repeats the image that many times. Rotation snaps
    like `Directional` so the result tiles.

- [ ] **Step 1: Write the failing tests**

- `NodeChecks.Tiles` and `SameAtSizes` for each new node.
- `A_disc_is_one_inside_and_zero_outside`: `Shape.Of(Disc, 0.5f)` at 64 is 1 at the
  middle and 0 at (2, 2).
- `Linear_gradient_rises_across_half_the_image_and_falls_back`: at angle 0, the values
  at u = 0.25 and 0.75 are equal, and the value at u = 0.5 is the largest.
- `Splatter_with_max_never_goes_below_zero_and_reaches_one`: on `Shape.Of(Disc)`.
- `Transform_tile_two_repeats_the_image`: the pixel at (x, y) equals the one at
  (x + size/2, y).
- `Transform_by_a_whole_image_changes_nothing`: offset (1, 0).
- `Directional_snaps_its_angle_and_says_so_in_the_docs`: angle 30 gives the same pixels
  as the snapped angle, atan(1/2) in degrees.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Materials.Tests --filter "GeneratorTests|TransformTests"`
Expected: build fails.

- [ ] **Step 3: Implement them**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Materials.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Materials tests/Super.Materials.Tests docs/materials.md
git commit -m "feat: directional noise, shapes, gradients, splatter and transform nodes"
```

---

### Task 14: More filters, colour and surface nodes, and `Pixel`

**Files:**
- Modify: `src/Super.Materials/Filters.cs`, `Warps.cs`, `FloodFill.cs`, `Color.cs`, `SurfaceNodes.cs`
- Create: `src/Super.Materials/Distance.cs`, `src/Super.Materials/Pixel.cs`
- Test: `tests/Super.Materials.Tests/FilterTests.cs`, `WarpTests.cs`, `FloodFillTests.cs`,
  `ColorTests.cs`, `SurfaceNodeTests.cs`, `PixelTests.cs`
- Docs: the node table in `docs/materials.md`.

**Interfaces:**
- Produces:
  - `Gray.Curve(params (float In, float Out)[] points)`: piecewise linear, and the points
    must go up in `In`.
  - `Gray.DirectionalWarp(Gray by, float amount, float angle)` and the same on `Rgb`.
  - `Gray.EdgeDetect(float width = 0.01f)`: 1 where the value crosses 0.5 within
    `width`, else 0.
  - `Gray.Distance(float reach)`: for each pixel at or below 0.5, the distance to the
    nearest pixel above 0.5, as a fraction of `reach`, clamped to 1 (`1 − d / reach`, so
    masks grow). It wraps.
  - `Gray Islands.ToGradient(float angle = 0f)`: 0 to 1 across each island's
    bounding box, along `angle`.
  - `Rgb.Hsl(float hue = 0f, float saturation = 0f, float lightness = 0f)` (offsets),
    `Gray Rgb.ToGray()` (Rec. 709 weights on the values as stored).
  - `Normals Normals.Combine(Normals detail)`: reoriented normal mapping, the same
    function `GraphRunner.Run` uses.
  - `Gray Normals.Curvature()`: 0.5 flat, above it convex, below it concave.
  - `public readonly ref struct PixelArgs` with `Vector2 Uv`, `float Gray(int input)`,
    `Vector3 Rgb(int input)`, `float SampleGray(int input, Vector2 uv)`,
    `Vector3 SampleRgb(int input, Vector2 uv)`.
  - `static Gray Pixel.Gray(PixelGray body, params Image[] inputs)` and
    `static Rgb Pixel.Rgb(PixelRgb body, params Image[] inputs)`, where the delegates
    are `delegate float PixelGray(in PixelArgs p)` and `delegate Vector3 PixelRgb(in PixelArgs p)`.
    `materials.md` says to use `DMath` in the body.

- [ ] **Step 1: Write the failing tests**

Hand-worked values for each:
- `Curve((0,0),(0.5,0.8),(1,1))` maps 0.25 to 0.4.
- `EdgeDetect` on a half-black, half-white image is 1 at the seam and 0 at a quarter.
- `Distance(0.25f)` on a single white column at x = 0 (size 64) is 1 − (8/64)/0.25 = 0.5
  at x = 8, and the same at x = 56 through the wrap.
- `ToGradient` on one 16-wide island is 0 at its left pixel and 1 at its right pixel
  (centres: 0.5/16 and 15.5/16, as a box gradient over pixel centres).
- `ToGray` of (1, 0, 0) is 0.2126.
- `Hsl(hue: 0.5f)` turns `#ff0000` into `#00ffff`.
- `Combine` of a normal with the flat normal gives the normal back.
- `Curvature` of a bump's peak is above 0.5, and of a pit's floor below.
- `Pixel.Gray(p => p.Uv.X)` at 4 gives 0.125, 0.375, 0.625, 0.875 along a row, and
  `Pixel.Gray(p => p.Gray(0) * 2f, Gray.Of(0.25f))` gives 0.5.
- `NodeChecks.Tiles` for `DirectionalWarp`, `Distance` and `EdgeDetect`.

Also `An_image_input_is_tracked` in `MaterialCookerTests` switches to `ToGray()`.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Materials.Tests`
Expected: build fails.

- [ ] **Step 3: Implement them**

- [ ] **Step 4: Run every test project that changed and see them pass**

Run: `dotnet test tests/Super.Materials.Tests` and `dotnet test tests/Super.Cook.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Materials tests docs/materials.md
git commit -m "feat: curve, distance, edge, colour, curvature and pixel nodes"
```

---

### Task 15: Courtyard with the lineup view and six starter materials

**Files:**
- Create: `examples/Courtyard/Courtyard.csproj` (like `examples/Crypt/Crypt.csproj`,
  with `Super.targets` and `<SuperContent Include="content" />`), `Program.cs`, `CourtyardGame.cs`
- Create: `examples/Courtyard/content/materials/bricks.material.cs`, `flagstones.material.cs`,
  `dirt.material.cs`, `wood.material.cs`, `leather.material.cs`, `rusty-metal.material.cs`
- Create: `examples/Courtyard/README.md`
- Create: `tests/Courtyard.Tests/` with one headless test
- Modify: `Super.slnx`, the examples list in `README.md`
- Media: `docs/media/material-lineup.webp`

**Interfaces:**
- Consumes: `content.Surface`, every node.
- Produces: `CourtyardGame` with a lineup only. The scene is step 2.

The lineup is a row per material, each with a sphere, a cube and a flat tile 1 m
across, with `content.Surface` and no `uvPerMetre`, so the real-size default shows.
Light comes from a low sun at about 20° from the side, so the normals read, plus sky
light. Copy the camera and light setup from `examples/Shapes/ShapeGallery.cs`. Each
graph follows its recipe in the spec's conversation:
- Bricks: `Tiles.Bricks`, `FloodFill` tilt and colour per brick, `SlopeBlur` with
  cells for chips.
- Flagstones: `Noise.Cells` edge for the gaps, `HistogramScan`, `Warp`, grit by
  `HeightBlend`.
- Dirt: Perlin and white grit, pebbles from `Splatter`, cracks from cells edge.
- Wood: warped `Gradient.Linear` rings and `Noise.Directional` grain.
- Leather: low-contrast cells, blurred, with lower roughness where Perlin wear is high.
- Rusty metal: Perlin patches through `HistogramScan`. The rust is metalness 0 and
  rough, the steel metalness 1.

- [ ] **Step 1: Write the failing headless test**

`tests/Courtyard.Tests/LineupTests.cs`: `Every_material_cooks_without_warnings`, which
cooks the example's content root with `ModelCookers.All` and asserts no problems at
all, so the starter materials stay a good example of the checks.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/Courtyard.Tests`
Expected: fails, as there's no content yet.

- [ ] **Step 3: Write the example and the six graphs**

- [ ] **Step 4: Run the test and see it pass**

Run: `dotnet test tests/Courtyard.Tests`
Expected: pass.

- [ ] **Step 5: Look at it**

Run: `dotnet run -c Release --project examples/Courtyard -- --screenshot shots/lineup.png --validate --seed 1 --ticks 60 --size 1600x900`
Expected: no validation messages. Open the image. Each row should look like its
material at a glance, with bumps lit from the side and no seams on the tiles. Fix the
graphs until it does. Make `docs/media/material-lineup.webp` (about 1200 wide,
`-c:v libwebp`), look at it, and use it in `docs/materials.md` and the README.

- [ ] **Step 6: Time the cook**

Run: `dotnet run -c Release --project tools/Super.Cook.Tool -- --content examples/Courtyard/content --out shots/cook --no-cache`,
with a rule setting size 1024, and then 2048.
Expected: under a second per graph at 1024 and under four at 2048, as the spec aims for.
Write down the times for the PR. If a graph is slower, profile it before moving on.

- [ ] **Step 7: Commit**

```bash
git add examples/Courtyard tests/Courtyard.Tests Super.slnx README.md docs/media/material-lineup.webp docs/materials.md
git commit -m "feat: Courtyard example with a lineup of six graph materials"
```

---

### Task 16: Docs sweep

**Files:**
- Modify: `docs/materials.md`, `docs/content.md`, `docs/design.md`, `docs/rendering.md`,
  `docs/roadmap.md`, `docs/README.md` (index), `docs/command-line.md` if `super-cook`
  output changed.

- [ ] **Step 1: Search for every name this branch touched**

Run: `grep -rn "uvPerMetre\|Surface\b\|material.cs\|TileWidth\|MaterialCooker\|ModelCookers" docs`
Expected: each hit describes the new behaviour. Fix any that don't.

- [ ] **Step 2: Check `design.md`**

It has the CPU-not-GPU reasoning, `DMath`, node seeds, compiling alone and in the game,
and the cooker's version from module ids. The design table gets a "Material graphs" row.

- [ ] **Step 3: Check the roadmap**

Step 1 of material graphs is done. Steps 2 to 6 are listed as next.

- [ ] **Step 4: Commit**

```bash
git add docs
git commit -m "docs: material graphs"
```

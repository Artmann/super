using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Gpu.Tests;

/// <summary>
/// <see cref="Renderer.BakeChunk"/>: small chunks whose sky and sun transfer can be worked out by hand, read back from
/// the <c>.lightbake</c> file it returns.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakeChunkGpuTests(GpuFixture gpu)
{
    private const int Density = 8;

    // Where the chunks stand in the world, so chunk space and world space differ.
    private static readonly Matrix4x4 Placed = Matrix4x4.CreateTranslation(10f, 0f, 5f);

    // The test quad turned to face up, 2 metres square, so its uv (u, v) lies at (2u - 1, 0, 1 - 2v).
    private static Matrix4x4 Floor(float size = 2f, float height = 0f) =>
        Matrix4x4.CreateScale(size) * Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, height, 0f);

    [Fact]
    public void Plane_chunk_under_a_white_sky_transfers_pi_l()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var chunk = Chunk(Placed);
            var draws = new[] { StaticDraw(quad, Floor() * Placed, gpu.Matte, 1) };

            var file = BakeChunk(chunk, draws);

            // Under a sky of radiance 1 every way, a plane's irradiance is the integral of cos over the hemisphere, pi.
            // Every path from a lone plane escapes at once with throughput 1 and adds pi Y(w). A white sky has only a
            // constant coefficient, so the dot reads only C0, which every path adds the same pi Y0 to: there is no
            // noise, and only the half float's rounding is left.
            const float tolerance = 0.01f;
            var white = ShL1.Uniform(1f);
            var (x0, y0, side) = Texels(file, RectOf(file, draws[0]));
            for (int y = y0; y < y0 + side; y++)
            {
                for (int x = x0; x < x0 + side; x++)
                {
                    var (red, green, blue) = SkyAt(file, x, y);
                    foreach (var (name, transfer) in new[] { ("red", red), ("green", green), ("blue", blue) })
                    {
                        float lit = ShL1.Dot(transfer, white);
                        Assert.True(MathF.Abs(lit - MathF.PI) <= tolerance,
                            $"Texel ({x}, {y}) transfers {lit} of a white sky in {name}, not pi within {tolerance}.");
                    }
                }
            }
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Turned_plane_chunk_transfers_the_same()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // The same chunk twice: once standing upright, once turned a quarter about x. Its plane goes with it.
            var turned = Matrix4x4.CreateRotationX(MathF.PI / 2f) * Placed;
            var upright = BakeChunk(Chunk(Placed), [StaticDraw(quad, Floor() * Placed, gpu.Matte, 1)]);
            var turnedFile = BakeChunk(Chunk(turned), [StaticDraw(quad, Floor() * turned, gpu.Matte, 1)]);

            var a = upright.Transfer!;
            var b = turnedFile.Transfer!;
            Assert.Equal(a.SkyRed.Pixels, b.SkyRed.Pixels);
            Assert.Equal(a.SkyGreen.Pixels, b.SkyGreen.Pixels);
            Assert.Equal(a.SkyBlue.Pixels, b.SkyBlue.Pixels);
            Assert.Equal(a.Sun.Pixels, b.Sun.Pixels);
            Assert.Equal(a.SunTint.Pixels, b.SunTint.Pixels);

            // The plane faces up in the chunk's own space, so its sky comes from +y there: C1 holds Y1 * y.
            var (x0, y0, side) = Texels(upright, RectOf(upright, upright.Draws[0].BakeId));
            var (red, _, _) = SkyAt(upright, x0 + side / 2, y0 + side / 2);
            Assert.True(red.C1 > 0.5f, $"The plane's sky transfer is {red}, which doesn't come from +y.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Chunk_bake_leaves_out_draws_it_does_not_own()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // The chunk's box ends at x = 1.5. A wall 4 metres square stands at x = 2, its middle outside the box, and
            // faces the plane, so it would hide a third of the sky from the plane's edge if it were baked.
            var chunk = Chunk(Placed) with { Min = new Vector3(-1.5f, -1f, -1.5f), Max = new Vector3(1.5f, 3f, 1.5f) };
            var plane = StaticDraw(quad, Floor() * Placed, gpu.Matte, 1);
            var wall = StaticDraw(quad,
                Matrix4x4.CreateScale(4f) * Matrix4x4.CreateRotationY(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(2f, 0f, 0f) * Placed,
                gpu.Matte, 2);

            var alone = BakeChunk(chunk, [plane]);
            var walled = BakeChunk(chunk, [plane, wall]);

            Assert.DoesNotContain(walled.Draws, entry => entry.BakeId == wall.BakeId);
            Assert.Equal(alone.Transfer!.SkyRed.Pixels, walled.Transfer!.SkyRed.Pixels);

            // Beside the wall, the open plane still sees the whole sky.
            var (x0, y0, side) = Texels(walled, RectOf(walled, plane.BakeId));
            var (red, _, _) = SkyAt(walled, x0 + side - 1, y0 + side / 2);
            float lit = ShL1.Dot(red, ShL1.Uniform(1f));
            Assert.True(MathF.Abs(lit - MathF.PI) < 0.01f, $"The plane's edge by the wall transfers {lit} of a white sky, not pi.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Closed_box_chunk_has_no_sky_transfer_inside()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // Six faces 3.3 metres square, each turned to face into the box and set 1.6 metres out, so they overlap at
            // the edges and leave no crack. The control plane sits above the box under open sky, in the same chunk.
            const float side = 3.3f;
            const float half = 1.6f;
            Matrix4x4[] faces =
            [
                Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, -half, 0f),
                Matrix4x4.CreateRotationX(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, half, 0f),
                Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(-half, 0f, 0f),
                Matrix4x4.CreateRotationY(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(half, 0f, 0f),
                Matrix4x4.CreateTranslation(0f, 0f, -half),
                Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0f, 0f, half),
            ];
            var draws = new List<DrawItem>();
            for (int i = 0; i < faces.Length; i++)
            {
                draws.Add(StaticDraw(quad, Matrix4x4.CreateScale(side) * faces[i] * Placed, gpu.Matte, 10 + (ulong)i));
            }

            draws.Add(StaticDraw(quad, Floor(height: 5f) * Placed, gpu.Matte, 20));
            var chunk = Chunk(Placed) with { Min = new Vector3(-3f), Max = new Vector3(3f, 6f, 3f) };

            var file = BakeChunk(chunk, draws.ToArray());

            // No path from inside reaches the sky, so every coefficient of the box's floor is 0.
            var (x0, y0, size) = Texels(file, RectOf(file, draws[0].BakeId));
            var (red, green, blue) = SkyAt(file, x0 + size / 2, y0 + size / 2);
            foreach (var transfer in new[] { red, green, blue })
            {
                Assert.True(MathF.Abs(transfer.C0) < 1e-3f && MathF.Abs(transfer.C1) < 1e-3f
                    && MathF.Abs(transfer.C2) < 1e-3f && MathF.Abs(transfer.C3) < 1e-3f,
                    $"The box's floor transfers {transfer} of the sky, not nothing.");
            }

            // Above the box, the open plane transfers pi of a white sky, so the sky transfer was made.
            var (cx, cy, csize) = Texels(file, RectOf(file, draws[^1].BakeId));
            var (open, _, _) = SkyAt(file, cx + csize / 2, cy + csize / 2);
            float lit = ShL1.Dot(open, ShL1.Uniform(1f));
            Assert.True(MathF.Abs(lit - MathF.PI) < 0.01f, $"The control plane transfers {lit} of a white sky, not pi.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Chunk_sun_transfer_holds_only_bounce()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var white = gpu.Renderer.CreateMaterial(new Material { BaseColor = Vector4.One, Roughness = 0.8f });
        try
        {
            // A lone plane sees nothing to bounce the sun from, and its own view of the sun is the runtime's.
            var plane = StaticDraw(quad, Floor() * Placed, gpu.Matte, 1);
            var lone = BakeChunk(Chunk(Placed), [plane]);
            var (x0, y0, side) = Texels(lone, RectOf(lone, plane.BakeId));
            for (int y = y0; y < y0 + side; y++)
            {
                for (int x = x0; x < x0 + side; x++)
                {
                    var sun = SunAt(lone, x, y);
                    Assert.True(sun == default, $"The lone plane's texel ({x}, {y}) has sun transfer {sun}.");
                }
            }

            // A white wall across x = 1, 4 metres square, faces the plane. The sun lights its face from -x and the face
            // bounces that onto the plane, most onto the side next to it.
            var wall = StaticDraw(quad,
                Matrix4x4.CreateScale(4f) * Matrix4x4.CreateRotationY(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(1f, 0f, 0f) * Placed,
                white, 2);
            var walled = BakeChunk(Chunk(Placed), [plane, wall]);
            (x0, y0, side) = Texels(walled, RectOf(walled, plane.BakeId));
            var near = SunAt(walled, x0 + side - 2, y0 + side / 2);
            var far = SunAt(walled, x0 + 1, y0 + side / 2);
            Assert.True(near.C0 > 0.05f, $"The plane next to the wall has sun transfer {near}, which holds no bounce.");
            Assert.True(near.C3 < 0f, $"The plane next to the wall has sun transfer {near}, which isn't from a sun on the -x side.");
            Assert.True(near.C0 > far.C0, $"The plane's side by the wall has sun transfer {near}, no more than its far side's {far}.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Chunk_lights_its_plane_from_its_own_lamp_wherever_it_stands()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // A Baked lamp at (0.5, 1, 0.3) in the chunk's space, placed in the world by the chunk's transform. The
            // chunk stands once as it is and once turned a quarter about y, so the lamp's world place differs in both
            // where and which way from the chunk, and only moving it into the chunk's space lights the plane the same.
            var local = new Vector3(0.5f, 1f, 0.3f);
            // Each lamp has its own key: one key in two places would be a light that moved, which is kept out of bakes.
            LightBakeFile BakeAt(Matrix4x4 transform, int key)
            {
                var lamp = new LocalLight
                {
                    Mode = LightMode.Baked, Key = key, Position = Vector3.Transform(local, transform),
                    Radiance = new Vector3(10f), Range = 100f, Radius = 0.02f,
                };
                return BakeChunk(Chunk(transform), [StaticDraw(quad, Floor() * transform, gpu.Matte, 1)], [lamp]);
            }

            var upright = BakeAt(Placed, 1201);
            var turned = BakeAt(Matrix4x4.CreateRotationY(MathF.PI / 2f) * Placed, 1203);

            Assert.NotNull(upright.Irradiance);
            Assert.NotNull(turned.Irradiance);
            var (x0, y0, side) = Texels(upright, RectOf(upright, upright.Draws[0].BakeId));
            Assert.Equal((x0, y0, side), Texels(turned, RectOf(turned, turned.Draws[0].BakeId)));

            // With no sky and nothing to bounce from, a floor point p has only the lamp's direct light, I h / d^3 with
            // I = 10 and h = 1, d being how far p is from the lamp. The texel (i, j) has its centre at
            // (2u - 1, 0, 1 - 2v) with u = (i + 0.5) / side.
            for (int j = 0; j < side; j++)
            {
                for (int i = 0; i < side; i++)
                {
                    var p = new Vector3(2f * (i + 0.5f) / side - 1f, 0f, 1f - 2f * (j + 0.5f) / side);
                    float d = Vector3.Distance(p, local);
                    float expected = 10f / (d * d * d);
                    float a = IrradianceAt(upright, x0 + i, y0 + j).X;
                    float b = IrradianceAt(turned, x0 + i, y0 + j).X;
                    Assert.True(MathF.Abs(a - expected) <= 0.05f * expected,
                        $"The upright chunk's texel ({i}, {j}) reads {a}, not the lamp's {expected} within 5%.");
                    Assert.True(MathF.Abs(b - a) <= 0.02f * a,
                        $"The turned chunk's texel ({i}, {j}) reads {b}, not the upright one's {a} within 2%.");
                }
            }
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Chunk_placed_twice_bakes_as_if_placed_once_from_either_copy()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // A floor, a wall a metre tall at x = 0.5 and a Baked lamp at (-0.5, 1, 0.3), all in the chunk's space. The
            // wall shades part of the floor from the lamp. The chunk stands once at Placed and once 20 metres away,
            // turned a quarter about y. Both copies have the same ids, as copies of a chunk do, and each its own lamp.
            var local = new Vector3(-0.5f, 1f, 0.3f);
            var wall = Matrix4x4.CreateRotationY(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(0.5f, 0.5f, 0f);
            var other = Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(-20f, 0f, 8f);
            DrawItem[] Copy(Matrix4x4 transform) =>
            [
                StaticDraw(quad, Floor() * transform, gpu.Matte, 1),
                StaticDraw(quad, wall * transform, gpu.Matte, 2),
            ];
            LocalLight Lamp(Matrix4x4 transform, int key) => new()
            {
                Mode = LightMode.Baked, Key = key, Position = Vector3.Transform(local, transform),
                Radiance = new Vector3(10f), Range = 100f, Radius = 0.02f,
            };

            var once = LightBakeFile.Read(BakeChunks([Chunk(Placed)], Copy(Placed), [Lamp(Placed, 1301)], 0));
            BakedChunk[] twice = [Chunk(Placed), Chunk(other)];
            DrawItem[] both = [.. Copy(Placed), .. Copy(other)];
            LocalLight[] lamps = [Lamp(Placed, 1301), Lamp(other, 1302)];
            var first = LightBakeFile.Read(BakeChunks(twice, both, lamps, 0));
            var second = LightBakeFile.Read(BakeChunks(twice, both, lamps, 1));

            // The first copy stands where the lone one does, so its bake is the same to the bit. The second is turned,
            // so its lamp is moved into the chunk through other floats: within the 2% a turned chunk's lamp gets.
            Assert.Equal(once.Draws, first.Draws);
            Assert.Equal(once.Irradiance![0].Pixels, first.Irradiance![0].Pixels);
            Assert.Equal(once.Transfer!.SkyRed.Pixels, first.Transfer!.SkyRed.Pixels);
            Assert.Equal(once.Draws, second.Draws);
            Assert.Equal(once.Transfer.SkyRed.Pixels, second.Transfer!.SkyRed.Pixels);
            var (x0, y0, side) = Texels(once, RectOf(once, Copy(Placed)[0]));
            for (int j = 0; j < side; j++)
            {
                for (int i = 0; i < side; i++)
                {
                    float a = IrradianceAt(once, x0 + i, y0 + j).X;
                    float b = IrradianceAt(second, x0 + i, y0 + j).X;
                    Assert.True(MathF.Abs(b - a) <= 0.02f * a + 1e-4f,
                        $"The turned copy's floor texel ({i}, {j}) reads {b}, not the lone chunk's {a} within 2%.");
                }
            }
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Mixed_chunk_light_gets_mask_channel_0_under_the_chunks_hash()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var lamp = new LocalLight
            {
                Mode = LightMode.Mixed, Key = 1202, Position = Vector3.Transform(new Vector3(0f, 1f, 0f), Placed),
                Radiance = new Vector3(10f), Range = 100f, Radius = 0.02f,
            };
            var plane = StaticDraw(quad, Floor() * Placed, gpu.Matte, 1);

            var file = BakeChunk(Chunk(Placed), [plane], [lamp]);

            var entry = Assert.Single(file.ChunkChannels);
            Assert.Equal(BakeInputHash.Light(lamp, Placed), entry.LightHash);
            Assert.Equal(0, entry.Channel);
            Assert.Empty(file.Channels);

            // The plane's middle sees all of the lamp, so channel 0 of its mask is full there.
            var (x0, y0, side) = Texels(file, RectOf(file, plane));
            int at = ((y0 + side / 2) * file.Mask[0].Width + x0 + side / 2) * 4;
            Assert.Equal(255, file.Mask[0].Pixels[at]);
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Chunk_keeps_its_plain_light_only_with_a_glowing_surface()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var glow = gpu.Renderer.CreateMaterial(new Material
        {
            BaseColor = Vector4.One, Roughness = 0.8f, Emissive = new Vector3(2f),
        });
        var swayingGlow = gpu.Renderer.CreateMaterial(new Material
        {
            BaseColor = Vector4.One, Roughness = 0.8f, Emissive = new Vector3(2f), Wind = 0.5f,
        });
        try
        {
            // A panel a metre above the plane, facing down at it.
            var plane = StaticDraw(quad, Floor() * Placed, gpu.Matte, 1);
            Matrix4x4 above = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationX(MathF.PI / 2f)
                * Matrix4x4.CreateTranslation(0f, 1f, 0f) * Placed;

            // With no light and nothing glowing there is no plain light to keep.
            var dark = BakeChunk(Chunk(Placed), [plane, StaticDraw(quad, above, gpu.Matte, 2)]);
            Assert.Null(dark.Irradiance);
            Assert.Null(dark.Direction);

            // A glowing panel lights the plane, so the plain light is kept.
            var lit = BakeChunk(Chunk(Placed), [plane, StaticDraw(quad, above, glow, 2)]);
            Assert.NotNull(lit.Irradiance);
            Assert.NotNull(lit.Direction);
            var (x0, y0, side) = Texels(lit, RectOf(lit, plane));
            float middle = IrradianceAt(lit, x0 + side / 2, y0 + side / 2).X;
            Assert.True(middle > 0.5f, $"The plane under the glowing panel reads {middle}, so the glow wasn't baked.");

            // A swaying panel gets no lightmap of its own, but it still glows onto the plane, so it keeps the plain
            // light too.
            var swaying = BakeChunk(Chunk(Placed), [plane, StaticDraw(quad, above, swayingGlow, 2)]);
            Assert.NotNull(swaying.Irradiance);
            Assert.DoesNotContain(swaying.Draws, entry => entry.BakeId != plane.BakeId);
            (x0, y0, side) = Texels(swaying, RectOf(swaying, plane));
            float swayingMiddle = IrradianceAt(swaying, x0 + side / 2, y0 + side / 2).X;
            Assert.True(swayingMiddle > 0.5f,
                $"The plane under the swaying glowing panel reads {swayingMiddle}, so its glow wasn't baked.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Baking_a_missing_chunk_throws()
    {
        int errors = gpu.ValidationErrors;
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => BakeChunk(Chunk(Placed), [], 3, Density));

        Assert.Contains("chunk 3", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("1 baked chunk", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Baking_a_chunk_that_owns_no_static_draws_throws()
    {
        int errors = gpu.ValidationErrors;
        var thrown = Assert.Throws<InvalidOperationException>(
            () => BakeChunk(Chunk(Placed) with { Name = "bare" }, [], 0, Density));

        Assert.Contains("\"bare\"", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("owns no static draws", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Baking_a_chunk_at_another_density_throws()
    {
        int errors = gpu.ValidationErrors;
        var thrown = Assert.Throws<ArgumentException>(
            () => BakeChunk(Chunk(Placed) with { Name = "dense" }, [], 0, Density * 2));

        Assert.Equal("settings", thrown.ParamName);
        Assert.Contains("\"dense\"", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("lit at 8 texels a metre, so it can't be baked at 16", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // A chunk 4 metres wide and 3 high round its origin, which the test plane sits on.
    private static BakedChunk Chunk(Matrix4x4 transform) =>
        new("chunk", transform, new Vector3(-2f, -1f, -2f), new Vector3(2f, 2f, 2f), Density);

    private LightBakeFile BakeChunk(BakedChunk chunk, DrawItem[] draws, LocalLight[]? lights = null) =>
        LightBakeFile.Read(BakeChunk(chunk, draws, 0, Density, lights));

    private byte[] BakeChunk(BakedChunk chunk, DrawItem[] draws, int index, int density, LocalLight[]? lights = null)
    {
        var view = gpu.View(DebugView.None);
        view.BakedChunks = new[] { chunk };
        view.LocalLights = lights ?? [];
        return gpu.Renderer.BakeChunk(view, draws, index, new BakeSettings(density, BakeQuality.Preview));
    }

    private byte[] BakeChunks(BakedChunk[] chunks, DrawItem[] draws, LocalLight[] lights, int index)
    {
        var view = gpu.View(DebugView.None);
        view.BakedChunks = chunks;
        view.LocalLights = lights;
        return gpu.Renderer.BakeChunk(view, draws, index, new BakeSettings(Density, BakeQuality.Preview));
    }

    private MeshHandle CreateQuad()
    {
        var (vertices, indices) = TestMeshes.Quad();
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Uv2 = vertices[i].Uv;
        }

        var handle = gpu.Renderer.CreateMesh(vertices, indices);
        gpu.RenderOneFrame(); // Uploads the mesh.
        return handle;
    }

    // A draw without detail is the point at its origin to the bake areas, so it is owned by the chunk holding that point.
    private static DrawItem StaticDraw(MeshHandle mesh, Matrix4x4 transform, MaterialHandle material, ulong instance) => new()
    {
        Mesh = mesh,
        IndexCount = 6,
        Material = material,
        Transform = transform,
        // An id keeps an instance hash's top 52 bits, so small numbers are moved up past the part's 12.
        BakeId = BakeIds.Of(instance << 12, 0),
    };

    private static LightmapRect RectOf(LightBakeFile file, DrawItem draw) => RectOf(file, draw.BakeId);

    private static LightmapRect RectOf(LightBakeFile file, ulong bakeId)
    {
        foreach (var (id, rect) in file.Draws)
        {
            if (id == bakeId)
            {
                return rect;
            }
        }

        throw new InvalidOperationException($"The bake has no rectangle for draw {bakeId}.");
    }

    // A chunk's page is as wide as its layout chose, and its rectangles are shares of that.
    private static (int X, int Y, int Side) Texels(LightBakeFile file, LightmapRect rect)
    {
        int page = file.Transfer!.SkyRed.Width;
        return (
            (int)MathF.Round(rect.Offset.X * page),
            (int)MathF.Round(rect.Offset.Y * page),
            (int)MathF.Round(rect.Scale.X * page));
    }

    private static (ShL1 Red, ShL1 Green, ShL1 Blue) SkyAt(LightBakeFile file, int x, int y) => (
        ShAt(file.Transfer!.SkyRed, x, y), ShAt(file.Transfer.SkyGreen, x, y), ShAt(file.Transfer.SkyBlue, x, y));

    private static ShL1 SunAt(LightBakeFile file, int x, int y) => ShAt(file.Transfer!.Sun, x, y);

    private static Vector3 IrradianceAt(LightBakeFile file, int x, int y)
    {
        var page = file.Irradiance![0];
        Assert.Equal(LightBakePageFormat.Rgba16f, page.Format);
        var halves = MemoryMarshal.Cast<byte, Half>(page.Pixels.AsSpan());
        int at = (y * page.Width + x) * 4;
        return new Vector3((float)halves[at], (float)halves[at + 1], (float)halves[at + 2]);
    }

    private static ShL1 ShAt(LightBakePage page, int x, int y)
    {
        Assert.Equal(LightBakePageFormat.Rgba16f, page.Format);
        var halves = MemoryMarshal.Cast<byte, Half>(page.Pixels.AsSpan());
        int at = (y * page.Width + x) * 4;
        return new ShL1((float)halves[at], (float)halves[at + 1], (float)halves[at + 2], (float)halves[at + 3]);
    }
}

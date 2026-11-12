using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// The baked shadow mask shading Mixed lights. A 2 metre plane faces the camera, and a wall 0.6 metres tall stands on it
/// at x = 0.3, edge on to the light, so a light 0.3 metres in front of the plane's centre, or a sun from -x, lights the
/// left of the plane and not the right. The plane is baked with the wall, then drawn with and without the bake. The sky
/// is off, so the only soft light is what the bake bounced, and every effect is off but baked lighting and the shadow
/// maps, so two captures differ only by what the bake changes.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class ShadowMaskGpuTests(GpuFixture gpu)
{
    private const int Size = 256;
    private const int NoisePeriod = 64;
    private const int TexelsPerMetre = 8;
    private const RenderEffects Off =
        RenderEffects.All & ~RenderEffects.BakedLighting & ~RenderEffects.SunShadows & ~RenderEffects.LightShadows;

    private static readonly BakedSpace Room =
        new("shadow-mask-room", new Vector3(-6f, -3f, -6f), new Vector3(6f, 7f, 6f), 1f, TexelsPerMetre);

    // x = -0.6 and 0.6 on the plane are 0.6 / (3 tan 22.5) = 0.483 of the half width either side of the centre, from
    // 3 metres away with a 45 degree view. The wall, from z = 0 to 0.6 at x = 0.3, covers 0.3 / 3 to 0.3 / 2.4 of the
    // distance, 0.080 to 0.101 of the half width, so neither point is behind it.
    private const float LeftX = 0.5f - 0.483f * 0.5f;
    private const float RightX = 0.5f + 0.483f * 0.5f;

    // From the light at (0, 0, 0.3), the way to x = 0.6 on the plane crosses x = 0.3 at z = 0.15, inside the wall; with
    // the light's 0.1 metre radius, from z = 0.1 to 0.2. The way to x = -0.6 never reaches x = 0.3.
    private static readonly Vector3 LampPosition = new(0f, 0f, 0.3f);

    [Fact]
    public void A_mixed_light_without_a_tile_is_shadowed_by_the_mask()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var plane = Plane(quad);
            var wall = Wall(quad, x: 0.3f, isStatic: true);
            LocalLight[] lamp = [Lamp(1, castsShadow: false, brightness: 2f)];
            var bake = Bake([plane, wall], lamp, []);
            Assert.Equal(new[] { (1, 0) }, bake.Channels);

            // Live, a light with no shadow tile shines through the wall.
            var live = Capture([plane, wall], lamp, []);
            gpu.Renderer.Bakes = new Loaded(bake);
            var baked = Capture([plane, wall], lamp, []);

            var liveRight = Pixel(live, RightX);
            var bakedRight = Pixel(baked, RightX);
            Assert.True(liveRight.X > 40f, $"The lamp leaves the plane behind the wall at {liveRight} when drawn live.");
            Assert.True(bakedRight.X < 0.25f * liveRight.X,
                $"Behind the wall the baked plane reads {bakedRight}, not shadowed against the live {liveRight}.");

            // In front of the wall the mask is 1, so the lamp shines as it does live, with a little bounce from the wall.
            var liveLeft = Pixel(live, LeftX);
            var bakedLeft = Pixel(baked, LeftX);
            Assert.True(bakedLeft.X > 0.9f * liveLeft.X,
                $"In front of the wall the baked plane reads {bakedLeft}, darker than the live {liveLeft}.");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_mixed_light_takes_the_darker_of_mask_and_real_time()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var plane = Plane(quad);
            LocalLight[] lamp = [Lamp(1, castsShadow: true, brightness: 2f)];
            var bake = Bake([plane, Wall(quad, x: 0.3f, isStatic: true)], lamp, []);
            Assert.Equal(new[] { (1, 0) }, bake.Channels);

            // After the bake the wall moves to x = -0.3, as something that isn't static, so the real-time shadow falls
            // on the left and the mask still holds the right.
            DrawItem[] moved = [plane, Wall(quad, x: -0.3f, isStatic: false)];
            var live = Capture(moved, lamp, []);
            gpu.Renderer.Bakes = new Loaded(bake);
            var baked = Capture(moved, lamp, []);

            var liveRight = Pixel(live, RightX);
            var liveLeft = Pixel(live, LeftX);
            Assert.True(liveRight.X > 40f, $"Live, the lamp leaves the plane's right at {liveRight}.");
            Assert.True(liveLeft.X < 0.25f * liveRight.X,
                $"Live, the moved wall's real-time shadow reads {liveLeft}, not dark against {liveRight}.");

            var bakedRight = Pixel(baked, RightX);
            var bakedLeft = Pixel(baked, LeftX);
            Assert.True(bakedRight.X < 0.25f * liveRight.X,
                $"Where the wall stood the baked plane reads {bakedRight}, not shadowed against {liveRight}.");
            Assert.True(bakedLeft.X < 0.25f * liveRight.X,
                $"Where the wall stands now the baked plane reads {bakedLeft}, not shadowed against {liveRight}.");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void The_mask_shadows_past_the_last_cascade() => ShadowsPastTheLastCascade(behindABakedSun: false);

    // A Baked sun is left out of the frame drawn, so the Mixed sun after it is drawn first, and must still find channel 0.
    [Fact]
    public void The_mask_shadows_a_mixed_sun_listed_after_a_baked_one() => ShadowsPastTheLastCascade(behindABakedSun: true);

    private void ShadowsPastTheLastCascade(bool behindABakedSun)
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var plane = Plane(quad);
            var wall = Wall(quad, x: 0.3f, isStatic: true);
            // A sun from -x and in front: the way from x = 0.6 towards it, (0.6 - t, 0, t), crosses x = 0.3 at z = 0.3,
            // inside the wall, while the way from x = -0.6 never reaches it. A Baked sun before it is too dim to show.
            DirectionalLight mixed = new()
            {
                Mode = LightMode.Mixed, Direction = Vector3.Normalize(new Vector3(-1f, 0f, 1f)), Radiance = new Vector3(2f), CastsShadow = true,
            };
            DirectionalLight dim = new() { Mode = LightMode.Baked, Direction = Vector3.UnitZ, Radiance = new Vector3(0.001f) };
            DirectionalLight[] sun = behindABakedSun ? [dim, mixed] : [mixed];
            var bake = Bake([plane, wall], [], sun);

            // One cascade of 0.1 metres, laid from the camera, ends long before the plane 3 metres away, so live the
            // wall casts no shadow on it.
            var live = Capture([plane, wall], [], sun);
            gpu.Renderer.Bakes = new Loaded(bake);
            var baked = Capture([plane, wall], [], sun);

            var liveRight = Pixel(live, RightX);
            var bakedRight = Pixel(baked, RightX);
            Assert.True(liveRight.X > 40f, $"Past the cascade the live sun leaves the plane at {liveRight}.");
            Assert.True(bakedRight.X < 0.25f * liveRight.X,
                $"Past the cascade the baked plane behind the wall reads {bakedRight}, not shadowed against {liveRight}.");

            var liveLeft = Pixel(live, LeftX);
            var bakedLeft = Pixel(baked, LeftX);
            Assert.True(bakedLeft.X > 0.9f * liveLeft.X,
                $"In the open the baked plane reads {bakedLeft}, darker than the live {liveLeft}.");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_light_without_a_channel_is_unchanged()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var plane = Plane(quad);
            var wall = Wall(quad, x: 0.3f, isStatic: true);
            // Five Mixed lights in one place: keys 11 to 14 take the four channels, nearly dark, and the bright key 15 is
            // left without one. Each channel holds the wall's shadow from that place, so a light reading any of them
            // would be shadowed behind the wall.
            LocalLight[] lamps =
            [
                Lamp(11, castsShadow: false, brightness: 0.001f),
                Lamp(12, castsShadow: false, brightness: 0.001f),
                Lamp(13, castsShadow: false, brightness: 0.001f),
                Lamp(14, castsShadow: false, brightness: 0.001f),
                Lamp(15, castsShadow: false, brightness: 2f),
            ];
            var bake = Bake([plane, wall], lamps, []);
            Assert.Equal(new[] { 15 }, bake.Report.LightsWithoutChannel);
            Assert.Equal(new[] { (11, 0), (12, 1), (13, 2), (14, 3) }, bake.Channels.OrderBy(c => c.LightKey));

            var live = Capture([plane, wall], lamps, []);
            gpu.Renderer.Bakes = new Loaded(bake);
            var baked = Capture([plane, wall], lamps, []);

            // Key 15 has no tile and no channel, so as today it shines through the wall. Behind the wall nothing bounces
            // onto the plane, so the baked plane reads as the live one.
            var liveRight = Pixel(live, RightX);
            var bakedRight = Pixel(baked, RightX);
            Assert.True(liveRight.X > 40f, $"The lamp leaves the plane behind the wall at {liveRight} when drawn live.");
            Assert.True(MathF.Abs(bakedRight.X - liveRight.X) <= 0.1f * liveRight.X,
                $"Behind the wall the baked plane reads {bakedRight}, not the live {liveRight}.");
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Light_near_a_chunk_edge_keeps_its_real_time_shadow_on_the_neighbour()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // Chunk A covers x = -4 to 0 and chunk B x = 0 to 4, each with a 4 metre floor. The lamp stands just inside A
            // at x = -0.4, a metre up. A wall 0.8 metres tall at x = -1 shades A's floor from x = -1 to -3.4 in A's bake;
            // B's wall at x = 1 shades B's floor from x = 1 out past its edge, and B's bake knows nothing of the lamp.
            BakedChunk[] chunks = [EdgeChunk("edge-a", -2f), EdgeChunk("edge-b", 2f)];
            LocalLight[] lamp = [ChunkLamp(1, new Vector3(-0.4f, 1f, 0f), new Vector3(16f), range: 4f, castsShadow: true)];
            var floorA = ChunkDraw(quad, FloorAt(-2f, 4f), 1);
            var wallA = ChunkDraw(quad, WallAt(-1f, 0.8f, facingPlusX: true), 2);
            var floorB = ChunkDraw(quad, FloorAt(2f, 4f), 3);
            var wallB = ChunkDraw(quad, WallAt(1f, 0.8f, facingPlusX: false), 4);
            DrawItem[] all = [floorA, wallA, floorB, wallB];
            var view = TopDown(lamp, chunks);
            var bakes = BakeChunks(view, all, 2);
            Assert.Equal(0, Assert.Single(bakes["edge-a"].ChunkChannels).Channel);
            Assert.Empty(bakes["edge-b"].ChunkChannels);

            var open = Capture(TopDown(lamp, chunks), [floorA, floorB]);

            // A's wall is taken away after the bake, so only A's mask can shade A's floor. B's wall stays, and only the
            // lamp's real-time shadow can shade B's floor behind it.
            DrawItem[] drawn = [floorA, floorB, wallB];
            gpu.Renderer.Bakes = new Named(bakes);
            Assert.True(RenderUntilFresh(view, drawn) <= MostFrames, $"The chunks' bakes weren't resident after {MostFrames} frames.");
            var baked = Capture(view, drawn);
            Assert.Empty(gpu.Renderer.LastBakeFrame!.UnslottedLights);

            foreach (float x in new[] { -2f, 0.5f, 2f })
            {
                Assert.True(FloorPixel(open, x).X > 30f, $"With no walls the lamp leaves the floor at x = {x} at {FloorPixel(open, x)}.");
            }

            var maskShadowA = FloorPixel(baked, -2f);
            Assert.True(maskShadowA.X < 0.25f * FloorPixel(open, -2f).X,
                $"In the lamp's mask shadow A's floor reads {maskShadowA}, not dark against the open {FloorPixel(open, -2f)}.");

            var behindWallB = FloorPixel(baked, 2f);
            Assert.True(behindWallB.X < 0.25f * FloorPixel(open, 2f).X,
                $"Behind B's wall B's floor reads {behindWallB}, not dark against the open {FloorPixel(open, 2f)}.");

            var inFrontOfWallB = FloorPixel(baked, 0.5f);
            Assert.True(inFrontOfWallB.X > 0.8f * FloorPixel(open, 0.5f).X,
                $"In front of B's wall B's floor reads {inFrontOfWallB}, darker than the open {FloorPixel(open, 0.5f)}.");
        }
        finally
        {
            LetGo(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Same_chunk_twice_with_overlapping_lights_reads_both_masks()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // One chunk, x = -1 to 1 with a 2 metre floor, a lamp at (0.5, 0.5, 0) and a wall 0.3 metres tall at
            // x = -0.1 that shades the floor from there to its -x edge. It is placed at x = -1 and at x = 1. The copies'
            // lamps are the same to the bake, which hashes neither key nor colour, so both read the one bake, where
            // each is channel 0. Their ranges of 2 overlap, so they need two slots: the first copy's red lamp, key 1,
            // takes slot 0 and the second's green lamp slot 1.
            BakedChunk[] chunks =
            [
                new("twin", Matrix4x4.CreateTranslation(-1f, 0f, 0f), new Vector3(-1f, -1f, -1f), new Vector3(1f, 2f, 1f), TexelsPerMetre),
                new("twin", Matrix4x4.CreateTranslation(1f, 0f, 0f), new Vector3(-1f, -1f, -1f), new Vector3(1f, 2f, 1f), TexelsPerMetre),
            ];
            LocalLight[] lamps =
            [
                ChunkLamp(1, new Vector3(-0.5f, 0.5f, 0f), new Vector3(8f, 0f, 0f), range: 2f, castsShadow: false),
                ChunkLamp(2, new Vector3(1.5f, 0.5f, 0f), new Vector3(0f, 8f, 0f), range: 2f, castsShadow: false),
            ];
            // Every copy has the same ids, as copies of a chunk do.
            DrawItem[] floors = [ChunkDraw(quad, FloorAt(-1f, 2f), 1), ChunkDraw(quad, FloorAt(1f, 2f), 1)];
            DrawItem[] all =
            [
                floors[0], ChunkDraw(quad, WallAt(-1.1f, 0.3f, facingPlusX: true), 2),
                floors[1], ChunkDraw(quad, WallAt(0.9f, 0.3f, facingPlusX: true), 2),
            ];
            var view = TopDown(lamps, chunks);
            var bakes = BakeChunks(view, all, 1);
            Assert.Equal(new[] { 0 }, bakes["twin"].ChunkChannels.Select(c => c.Channel));

            var open = Capture(view, floors);
            gpu.Renderer.Bakes = new Named(bakes);
            Assert.True(RenderUntilFresh(view, all) <= MostFrames, $"The chunk's bake wasn't resident after {MostFrames} frames.");
            var baked = Capture(view, all);
            Assert.Empty(gpu.Renderer.LastBakeFrame!.UnslottedLights);

            // 0.4 metres in from each copy's -x edge, its own lamp is behind its wall. The green lamp is out of reach of
            // the first copy's floor there, but the red lamp reaches the second copy's, where no wall of its own shades it.
            var firstOpen = FloorPixel(open, -1.6f);
            var firstShadow = FloorPixel(baked, -1.6f);
            Assert.True(firstOpen.X > 30f, $"With no walls the red lamp leaves the first floor at {firstOpen}.");
            Assert.True(firstShadow.X < 0.25f * firstOpen.X,
                $"In the red lamp's mask shadow the first copy reads {firstShadow}, not dark against the open {firstOpen}.");

            var secondOpen = FloorPixel(open, 0.4f);
            var secondShadow = FloorPixel(baked, 0.4f);
            Assert.True(secondOpen.Y > 30f && secondOpen.X > 30f, $"With no walls both lamps light the second floor at {secondOpen}.");
            Assert.True(secondShadow.Y < 0.25f * secondOpen.Y,
                $"In the green lamp's mask shadow the second copy reads {secondShadow}, not dark green against the open {secondOpen}.");
            Assert.True(secondShadow.X > 0.8f * secondOpen.X,
                $"The red lamp is shaded on the second copy, {secondShadow} against the open {secondOpen}, by a mask that isn't its own.");
        }
        finally
        {
            LetGo(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private const int MostFrames = 120;

    // From 6 metres up a 45 degree view sees 6 tan 22.5 = 2.485 metres either side of the centre.
    private const float TopDownHeight = 6f;
    private const float TopDownHalfWidth = 2.485f;

    private static BakedChunk EdgeChunk(string name, float x) =>
        new(name, Matrix4x4.CreateTranslation(x, 0f, 0f), new Vector3(-2f, -1f, -2f), new Vector3(2f, 3f, 2f), TexelsPerMetre);

    private static LocalLight ChunkLamp(int key, Vector3 position, Vector3 radiance, float range, bool castsShadow) => new()
    {
        Mode = LightMode.Mixed,
        Key = key,
        Position = position,
        Radiance = radiance,
        Range = range,
        CastsShadow = castsShadow,
    };

    // The quad turned to face up, size metres square, centred at x on the ground.
    private static Matrix4x4 FloorAt(float x, float size) =>
        Matrix4x4.CreateScale(size) * Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(x, 0f, 0f);

    // The quad stood on the ground at x, height metres tall and 4 metres along z, facing +x or -x.
    private static Matrix4x4 WallAt(float x, float height, bool facingPlusX) =>
        Matrix4x4.CreateScale(4f, height, 1f)
        * Matrix4x4.CreateRotationY(facingPlusX ? MathF.PI / 2f : -MathF.PI / 2f)
        * Matrix4x4.CreateTranslation(x, height / 2f, 0f);

    // A draw without detail is the point at its origin to the bake areas, so the chunk holding that point owns it.
    private DrawItem ChunkDraw(MeshHandle mesh, Matrix4x4 transform, ulong instance) => new()
    {
        Mesh = mesh,
        IndexCount = 6,
        Material = gpu.Matte,
        Transform = transform,
        BakeId = BakeIds.Of(instance << 12, 0),
    };

    // Looking straight down on the ground from 6 metres up, with +x to the right of the picture.
    private static FrameView TopDown(LocalLight[] lights, BakedChunk[] chunks) => new()
    {
        Width = Size,
        Height = Size,
        View = Matrix4x4.CreateLookAt(new Vector3(0f, TopDownHeight, 0f), Vector3.Zero, -Vector3.UnitZ),
        Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
        CameraPosition = new Vector3(0f, TopDownHeight, 0f),
        EnvironmentIntensity = 0f,
        Off = Off,
        LocalLights = lights,
        BakedChunks = chunks,
        Shadows = new ShadowSettings { Radius = 0.1f, Cascades = 1 },
    };

    // Bakes the first count chunks of the view, by name, after a frame without lights so none counts as moving.
    private Dictionary<string, LightBakeFile> BakeChunks(FrameView view, DrawItem[] draws, int count)
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        var dark = view;
        dark.LocalLights = [];
        gpu.Renderer.Render(dark, draws);

        var bakes = new Dictionary<string, LightBakeFile>();
        for (int c = 0; c < count; c++)
        {
            bakes[view.BakedChunks[c].Name] = LightBakeFile.Read(
                gpu.Renderer.BakeChunk(view, draws, c, new BakeSettings(TexelsPerMetre, BakeQuality.Preview)));
        }

        return bakes;
    }

    private int RenderUntilFresh(FrameView view, DrawItem[] draws)
    {
        for (int frames = 1; frames <= MostFrames; frames++)
        {
            gpu.Renderer.Render(view, draws);
            if (gpu.Renderer.LastBakeFrame!.ChunkFresh.All(fresh => fresh))
            {
                return frames;
            }
        }

        return MostFrames + 1;
    }

    // The mean of the 5 by 5 pixels at x on the ground, halfway up the top-down picture, as 0 to 255.
    private static Vector3 FloorPixel(byte[] frame, float x)
    {
        int px = (int)(Size / 2f + x / TopDownHalfWidth * Size / 2f);
        var sum = Vector3.Zero;
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int at = ((Size / 2 + dy) * Size + px + dx) * 4;
                sum += new Vector3(frame[at], frame[at + 1], frame[at + 2]);
            }
        }

        return sum / 25f;
    }

    // Puts the renderer back: no store, and a frame without chunks so nothing stays in the pool.
    private void LetGo(MeshHandle quad)
    {
        gpu.Renderer.Bakes = NoBakes.Instance;
        gpu.RenderOneFrame();
        gpu.Renderer.DestroyMesh(quad);
    }

    private static LocalLight Lamp(int key, bool castsShadow, float brightness) => new()
    {
        Mode = LightMode.Mixed,
        Key = key,
        Position = LampPosition,
        Radiance = new Vector3(brightness),
        Range = 5f,
        CastsShadow = castsShadow,
    };

    // The test quad, 2 metres square, facing the camera, static in the room.
    private DrawItem Plane(MeshHandle mesh) => new()
    {
        Mesh = mesh,
        IndexCount = 6,
        Material = gpu.Matte,
        Transform = Matrix4x4.CreateScale(2f),
        // An id keeps an instance hash's top 52 bits, so small numbers are moved up past the part's 12.
        BakeId = BakeIds.Of(1ul << 12, 0),
    };

    // The quad stood on the plane at x, 0.6 metres out from it and 2 metres along y, facing the lamp at x = 0. Turning
    // the quad a quarter about y lays its x along z, so it reaches from z = -0.3 to 0.3 before it is lifted by 0.3.
    private DrawItem Wall(MeshHandle mesh, float x, bool isStatic) => new()
    {
        Mesh = mesh,
        IndexCount = 6,
        Material = gpu.Matte,
        Transform = Matrix4x4.CreateScale(0.6f, 2f, 1f)
            * Matrix4x4.CreateRotationY(x > 0f ? -MathF.PI / 2f : MathF.PI / 2f)
            * Matrix4x4.CreateTranslation(x, 0f, 0.3f),
        BakeId = isStatic ? BakeIds.Of(2ul << 12, 0) : 0,
    };

    private LightBakeFile Bake(DrawItem[] draws, LocalLight[] lights, DirectionalLight[] suns)
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");

        // The renderer is shared, so a light another test drew differently under the same key would count as moving,
        // and the bake would leave it out of the mask. A frame without lights forgets them all.
        gpu.Renderer.Render(View([], []), draws);
        return LightBakeFile.Read(
            gpu.Renderer.Bake(View(lights, suns), draws, new BakeSettings(TexelsPerMetre, BakeQuality.Preview)));
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

    private static FrameView View(LocalLight[] lights, DirectionalLight[] suns) => new()
    {
        Width = Size,
        Height = Size,
        View = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY),
        Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
        CameraPosition = new Vector3(0, 0, 3),
        EnvironmentIntensity = 0f,
        Off = Off,
        BakedSpace = Room,
        LocalLights = lights,
        Lights = suns,
        Shadows = new ShadowSettings { Radius = 0.1f, Cascades = 1 },
    };

    // The 64th frame, so two captures fall on the same noise.
    private byte[] Capture(DrawItem[] draws, LocalLight[] lights, DirectionalLight[] suns)
    {
        var view = View(lights, suns);
        for (int i = 0; i < NoisePeriod - 1; i++)
        {
            gpu.Renderer.Render(view, draws);
        }

        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    // After at least 63 frames of the view, on the first frame of the dither's noise, so two captures fall on the same
    // frame of it however many frames came between them.
    private byte[] Capture(FrameView view, DrawItem[] draws)
    {
        for (int i = 0; i < NoisePeriod - 1 || gpu.Renderer.FrameNumber % NoisePeriod != 0; i++)
        {
            gpu.Renderer.Render(view, draws);
        }

        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    // The pixel at a share of the width, halfway up, as 0 to 255.
    private static Vector3 Pixel(byte[] frame, float x)
    {
        int at = (Size / 2 * Size + (int)(x * Size)) * 4;
        return new Vector3(frame[at], frame[at + 1], frame[at + 2]);
    }

    // A store that holds one bake and calls it fresh for every area, whatever the frame's inputs.
    private sealed class Loaded(LightBakeFile bake) : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => true;

        public LightBakeFile? Load(string file) => bake;
    }

    /// <summary>
    /// A store that holds a bake per chunk name and calls each fresh whatever the frame's inputs, so a chunk drawn
    /// without something it was baked with still reads the bake.
    /// </summary>
    private sealed class Named(Dictionary<string, LightBakeFile> bakes) : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => bakes.ContainsKey(NameOf(file));

        public LightBakeFile? Load(string file) => bakes.GetValueOrDefault(NameOf(file));

        // lighting/<name>/<hash>.lightbake
        private static string NameOf(string file) => file.Split('/')[1];
    }
}

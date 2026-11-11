using System.Numerics;
using Rendition;
using Super.Testing;
using Super.Tests.Animation;

namespace Super.Tests;

/// <summary>
/// Bake ids of static draws, given the way <see cref="SceneRenderer"/> gives them, without a GPU: each part's
/// bounds stand in for what the renderer would report.
/// </summary>
public class BakeIdentityTests
{
    private static readonly Vertex[] Triangle =
    [
        new(new Vector3(0f, 0f, 0f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.Zero),
        new(new Vector3(1f, 0f, 0f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.UnitX),
        new(new Vector3(0f, 0f, 1f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.UnitY),
    ];

    private static readonly uint[] Indices = [0, 1, 2];

    private static readonly Vector3 ChunkMin = new(-2f, 0f, -2f);
    private static readonly Vector3 ChunkMax = new(2f, 3f, 2f);

    /// <summary>Where a draw sits inside a chunk: a little turned, off its centre, on the 0.1 mm grid.</summary>
    private static readonly Matrix4x4 InChunk = Matrix4x4.CreateRotationY(0.3f) * Matrix4x4.CreateTranslation(1.5f, 0.25f, -0.75f);

    private readonly BakeIdentity.Scratch _scratch = new();

    /// <summary>
    /// The parts of the view's last draw as the renderer would hand them over, each a sphere of half a metre round the
    /// model's origin unless <paramref name="bounds"/> says otherwise. Part p's detail is <c>new DetailHandle(p)</c>.
    /// </summary>
    private DrawItem[] Dressed(View view, Content content, int parts = 1, BakeSphere[]? bounds = null)
    {
        ref readonly var draw = ref view.Drawn[^1];
        bounds ??= Enumerable.Repeat(new BakeSphere(Vector3.Zero, 0.5f), parts).ToArray();
        var model = Enumerable.Range(0, bounds.Length).Select(p => new DrawItem { Detail = new DetailHandle(p) }).ToArray();
        var shown = Array.Empty<DrawItem>();
        int count = SceneRenderer.AddShown(model, draw.Meshes, ref shown, 0);
        SceneRenderer.Dress(shown.AsSpan(0, count), in draw, default, bounds, content, view.BakedSpace, view.PlacedBakedChunks, _scratch);
        return shown[..count];
    }

    private ulong IdOf(Content content, ModelRef model, Matrix4x4 transform, Vector3? color = null)
    {
        var view = new View();
        view.Begin(0f);
        view.Model(1, model, transform, color, isStatic: true);
        return Assert.Single(Dressed(view, content)).BakeId;
    }

    private static Content Fresh() => new(new MemoryContent());

    private static GeneratedMesh Mesh(Vertex[] vertices, Surface? surface = null) => new(vertices, Indices, surface ?? new Surface());

    [Fact]
    public void Dynamic_draw_has_no_bake_id()
    {
        var view = new View();
        view.Begin(0f);
        view.BakedChunk("cottage", Matrix4x4.Identity, ChunkMin, ChunkMax);
        view.Model(1, ModelRef.Cube, Matrix4x4.Identity);

        var parts = Dressed(view, Fresh(), parts: 2);

        Assert.Equal(2, parts.Length);
        Assert.All(parts, part => Assert.Equal(0ul, part.BakeId));
    }

    [Fact]
    public void Static_draw_has_a_bake_id_per_part()
    {
        var view = new View();
        view.Begin(0f);
        view.Model(1, ModelRef.Cube, Matrix4x4.Identity, isStatic: true);

        var parts = Dressed(view, Fresh(), parts: 2);

        Assert.Equal(2, parts.Length);
        Assert.All(parts, part => Assert.NotEqual(0ul, part.BakeId));
        Assert.Equal(0, BakeIds.Part(parts[0].BakeId));
        Assert.Equal(1, BakeIds.Part(parts[1].BakeId));
        Assert.Equal(BakeIds.Instance(parts[0].BakeId), BakeIds.Instance(parts[1].BakeId));
    }

    [Fact]
    public void Masked_static_draw_keeps_each_parts_own_number()
    {
        var content = Fresh();
        var model = content.Mesh(Mesh(Triangle));
        var view = new View();
        view.Begin(0f);
        view.Model(1, model, Matrix4x4.Identity, null, new MeshMask(model.Index, 3, [2]), isStatic: true);

        var part = Assert.Single(Dressed(view, content, parts: 3));

        Assert.Equal(2, BakeIds.Part(part.BakeId));
    }

    [Fact]
    public void Same_static_draw_next_frame_keeps_its_ids()
    {
        var content = Fresh();
        var view = new View();
        view.Begin(0f);
        view.Model(1, ModelRef.Cube, InChunk, isStatic: true);
        var first = Dressed(view, content, parts: 2);

        view.Begin(0f);
        view.Model(1, ModelRef.Cube, InChunk, isStatic: true);
        var second = Dressed(view, content, parts: 2);

        Assert.Equal(first.Select(part => part.BakeId), second.Select(part => part.BakeId));
    }

    [Fact]
    public void Moving_a_static_draw_a_millimetre_changes_its_id()
    {
        var content = Fresh();

        ulong before = IdOf(content, ModelRef.Cube, Matrix4x4.CreateTranslation(1f, 0f, 0f));
        ulong after = IdOf(content, ModelRef.Cube, Matrix4x4.CreateTranslation(1.001f, 0f, 0f));

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Moving_it_a_fiftieth_of_a_millimetre_does_not()
    {
        var content = Fresh();

        ulong before = IdOf(content, ModelRef.Cube, Matrix4x4.CreateTranslation(1f, 0f, 0f));
        ulong after = IdOf(content, ModelRef.Cube, Matrix4x4.CreateTranslation(1.00002f, 0f, 0f));

        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData("tint")]
    [InlineData("material")]
    [InlineData("model")]
    public void Tint_material_and_model_each_change_the_id(string changed)
    {
        var content = Fresh();
        var grey = content.Mesh(Mesh(Triangle, new Surface { Color = new Vector3(0.5f) }));
        var shiny = content.Mesh(Mesh(Triangle, new Surface { Color = new Vector3(0.5f), Roughness = 0.1f }));
        var tint = new Vector3(0.2f, 0.4f, 0.6f);
        ulong before = IdOf(content, grey, InChunk, tint);

        ulong after = changed switch
        {
            "tint" => IdOf(content, grey, InChunk, new Vector3(0.2f, 0.4f, 0.7f)),
            "material" => IdOf(content, shiny, InChunk, tint),
            _ => IdOf(content, ModelRef.Sphere, InChunk, tint),
        };

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Built_in_shape_ids_use_its_name()
    {
        // The second content has named something first, so nothing about it but the shape is the same.
        var other = Fresh();
        other.Mesh(Mesh(Triangle));

        Assert.Equal(IdOf(Fresh(), ModelRef.Cube, InChunk), IdOf(other, ModelRef.Cube, InChunk));
        Assert.NotEqual(IdOf(Fresh(), ModelRef.Cube, InChunk), IdOf(Fresh(), ModelRef.Torus, InChunk));
    }

    [Fact]
    public void Code_mesh_ids_follow_its_vertices()
    {
        var first = Fresh();
        var second = Fresh();
        second.Mesh(Mesh(Triangle)); // So the same mesh has another index there.
        var moved = (Vertex[])Triangle.Clone();
        moved[1].Position.X += 0.001f;

        ulong a = IdOf(first, first.Mesh(Mesh((Vertex[])Triangle.Clone())), InChunk);
        ulong b = IdOf(second, second.Mesh(Mesh((Vertex[])Triangle.Clone())), InChunk);
        ulong c = IdOf(first, first.Mesh(Mesh(moved)), InChunk);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Static_draw_that_swims_is_explained()
    {
        var view = new View();
        view.Begin(0f);

        var error = Assert.Throws<ArgumentException>(() =>
            view.Model(7, ModelRef.Cube, Matrix4x4.Identity, null, new SwimMotion(0.1f, 1f, 0f), null, null, isStatic: true));

        Assert.Contains("Model 7 is static", error.Message);
        Assert.Contains("swim", error.Message);
    }

    [Fact]
    public void Static_draw_with_a_pose_is_explained()
    {
        var view = new View();
        view.Begin(0f);
        var pose = new AnimationPose(TestRig.Rig());

        var error = Assert.Throws<ArgumentException>(() =>
            view.Model(7, ModelRef.Cube, Matrix4x4.Identity, null, default, pose, null, isStatic: true));

        Assert.Contains("Model 7 is static", error.Message);
        Assert.Contains("pose", error.Message);
    }

    [Fact]
    public void Static_model_with_too_many_parts_is_explained()
    {
        var view = new View();
        view.Begin(0f);
        view.Model(1, ModelRef.Cube, Matrix4x4.Identity, isStatic: true);

        var error = Assert.Throws<InvalidOperationException>(() => Dressed(view, Fresh(), parts: 4096));

        Assert.Contains("cube", error.Message);
        Assert.Contains("4096 parts", error.Message);
        Assert.Contains("4095", error.Message);
    }

    [Fact]
    public void Static_model_with_the_most_parts_gets_ids()
    {
        var view = new View();
        view.Begin(0f);
        view.Model(1, ModelRef.Cube, Matrix4x4.Identity, isStatic: true);

        var parts = Dressed(view, Fresh(), parts: 4096 - 1);

        Assert.Equal(4094, BakeIds.Part(parts[^1].BakeId));
    }

    [Fact]
    public void Chunk_relative_ids_match_wherever_the_chunk_is()
    {
        var moved = Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(30f, 0f, 0f);

        ulong home = ChunkId(Matrix4x4.Identity, InChunk);
        ulong away = ChunkId(moved, InChunk * moved);

        Assert.Equal(home, away);
    }

    [Fact]
    public void Chunk_far_from_the_origin_gives_the_same_bake_ids()
    {
        var far = Matrix4x4.CreateRotationY(37f * MathF.PI / 180f) * Matrix4x4.CreateTranslation(400f, 0f, -300f);

        ulong home = ChunkId(Matrix4x4.Identity, InChunk);
        ulong away = ChunkId(far, InChunk * far);

        Assert.Equal(home, away);
    }

    [Fact]
    public void Draw_outside_every_chunk_uses_world_space()
    {
        // The draw moves with the chunk, but sits outside its box, so the chunk doesn't own it.
        var outside = Matrix4x4.CreateTranslation(10f, 0f, 0f);
        var moved = Matrix4x4.CreateTranslation(30f, 0f, 0f);

        ulong home = ChunkId(Matrix4x4.Identity, outside);
        ulong away = ChunkId(moved, outside * moved);

        Assert.NotEqual(home, away);
    }

    [Fact]
    public void Model_across_a_chunk_edge_is_filed_whole_under_the_chunk_that_gave_its_ids()
    {
        // Two chunks side by side: west (at x = 10) holds x from 8 to 12, east (at x = 14.5) from 12.5 to 16.5. The
        // model's two parts are half-metre balls 1.5 m either side of its origin, and it stands at x = 11.5, so its
        // parts sit at x = 10 (in west) and x = 13 (in east), and the whole model's centre, x = 11.5, is in west.
        var view = new View();
        view.Begin(0f);
        view.BakedChunk("west", Matrix4x4.CreateTranslation(10f, 0f, 0f), ChunkMin, ChunkMax);
        view.BakedChunk("east", Matrix4x4.CreateTranslation(14.5f, 0f, 0f), ChunkMin, ChunkMax);
        view.Model(1, ModelRef.Cube, Matrix4x4.CreateTranslation(11.5f, 0f, 0f), isStatic: true);
        BakeSphere[] bounds = [new(new Vector3(-1.5f, 0f, 0f), 0.5f), new(new Vector3(1.5f, 0f, 0f), 0.5f)];
        var parts = Dressed(view, Fresh(), bounds: bounds);
        var chunks = view.PlacedBakedChunks.ToArray();
        var settings = new BakeSettings(Quality: BakeQuality.Final);

        var resolved = BakeFreshness.Resolve(
            new FrameView { BakedChunks = chunks }, parts, detail => bounds[detail.Index], 0, settings,
            new AnimatedLights(new StringWriter()), NoBakes.Instance);

        Assert.Equal(2, parts.Length);
        Assert.Equal(BakeInputHash.Chunk([parts[0].BakeId, parts[1].BakeId], [], chunks[0], settings).Draws, resolved.Chunks[0].Draws);
        Assert.Equal(BakeInputHash.Chunk([], [], chunks[1], settings).Draws, resolved.Chunks[1].Draws);
        // The ids are relative to west: the model standing 1.5 m along a chunk at the origin gets the same ones.
        Assert.Equal(
            parts.Select(part => part.BakeId),
            ChunkModelIds(Matrix4x4.Identity, Matrix4x4.CreateTranslation(1.5f, 0f, 0f), bounds));
    }

    private ulong[] ChunkModelIds(Matrix4x4 chunk, Matrix4x4 transform, BakeSphere[] bounds)
    {
        var view = new View();
        view.Begin(0f);
        view.BakedChunk("cottage", chunk, ChunkMin, ChunkMax);
        view.Model(1, ModelRef.Cube, transform, isStatic: true);
        return Dressed(view, Fresh(), bounds: bounds).Select(part => part.BakeId).ToArray();
    }

    private ulong ChunkId(Matrix4x4 chunk, Matrix4x4 transform)
    {
        var view = new View();
        view.Begin(0f);
        view.BakedChunk("cottage", chunk, ChunkMin, ChunkMax);
        view.Model(1, ModelRef.Cube, transform, isStatic: true);
        return Assert.Single(Dressed(view, Fresh())).BakeId;
    }
}

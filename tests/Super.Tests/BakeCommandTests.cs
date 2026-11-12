using System.Numerics;
using Rendition;

namespace Super.Tests;

public sealed class BakeCommandTests
{
    [Fact]
    public void Bake_writes_one_file_per_distinct_chunk()
    {
        // Three copies of one room and one of another: chunks 0, 1 and 3 share a file.
        string[] files =
        [
            "lighting/room/aaaa.lightbake",
            "lighting/room/aaaa.lightbake",
            "lighting/hall/bbbb.lightbake",
            "lighting/room/aaaa.lightbake"
        ];

        var targets = BakeCommand.Targets("lighting/town.lightbake", files.Length, chunk => files[chunk]);

        // The space first, then each chunk file once, the first copy standing for it, in order of first appearance.
        Assert.Equal(
            [
                new BakeTarget("lighting/town.lightbake", BakeTarget.Space),
                new BakeTarget("lighting/room/aaaa.lightbake", 0),
                new BakeTarget("lighting/hall/bbbb.lightbake", 2)
            ],
            targets);
    }

    [Fact]
    public void Bake_without_a_space_starts_with_the_first_chunk()
    {
        var targets = BakeCommand.Targets(null, 1, _ => "lighting/room/aaaa.lightbake");

        Assert.Equal([new BakeTarget("lighting/room/aaaa.lightbake", 0)], targets);
    }

    [Fact]
    public void Bake_check_lists_every_chunk_file()
    {
        var targets = BakeCommand.Targets(
            "lighting/town.lightbake", 3, chunk => new[] { "lighting/room/aaaa.lightbake", "lighting/room/aaaa.lightbake", "lighting/hall/bbbb.lightbake" }[chunk]);
        var changes = new Dictionary<string, IReadOnlyList<string>?>
        {
            ["lighting/town.lightbake"] = [],
            ["lighting/room/aaaa.lightbake"] = null,
            ["lighting/hall/bbbb.lightbake"] = ["draws", "lights"]
        };
        var output = new StringWriter();

        int bad = BakeCommand.Check(targets, target => changes[target.File], output);

        Assert.Equal(2, bad);
        Assert.Equal(
            ["lighting/town.lightbake: fresh", "lighting/room/aaaa.lightbake: missing", "lighting/hall/bbbb.lightbake: stale, its draws, lights changed"],
            output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Bake_with_no_area_explains()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BakeCommand.Targets(null, 0, _ => "unused"));

        Assert.Equal(
            "--bake found no baked space or chunk in the frame. Name one with view.BakedLighting or view.BakedChunk.",
            error.Message);
    }

    [Fact]
    public void Bake_check_with_no_area_has_nothing_to_check()
    {
        var output = new StringWriter();

        int bad = BakeCommand.Check([], _ => throw new InvalidOperationException("nothing to ask"), output);

        Assert.Equal(0, bad);
        Assert.Equal("The frame names no baked space or chunk, so there is no bake to check.", output.ToString().Trim());
    }

    [Fact]
    public void A_frame_names_each_chunks_file_by_its_combined_hash()
    {
        var room = new BakedChunk("room", Matrix4x4.Identity, new Vector3(-2f, 0f, -2f), new Vector3(2f, 3f, 2f));
        var frame = new FrameView { BakedChunks = new[] { room }, Time = 1f };
        var resolver = new BakeResolver(NoBakes.Instance);

        var animated = new AnimatedLights(TextWriter.Null);

        var resolved = resolver.Resolve(frame, [], _ => new BakeSphere(Vector3.Zero, 1f), 0, animated);
        string first = resolved.ChunkFile(0);
        resolved = resolver.Resolve(frame, [], _ => new BakeSphere(Vector3.Zero, 1f), 0, animated);

        Assert.Equal(BakeFiles.Chunk("room", resolved.Chunks[0].Combined), first);
        // The resolver keeps the name, so a later frame hands back the same string rather than building another.
        Assert.Same(first, resolved.ChunkFile(0));
        Assert.Equal(0, resolved.ChunkStaticDraws(0));
    }

    [Fact]
    public void A_chunk_with_no_static_draws_is_reported_and_the_rest_are_still_baked()
    {
        BakeTarget[] targets =
        [
            new("lighting/town.lightbake", BakeTarget.Space),
            new("lighting/empty/aaaa.lightbake", 0),
            new("lighting/hall/bbbb.lightbake", 1)
        ];
        var baked = new List<string>();
        var output = new StringWriter();

        int failed = BakeCommand.Bake(targets, target => target.Chunk != 0, target => baked.Add(target.File), output);

        Assert.Equal(1, failed);
        Assert.Equal(["lighting/town.lightbake", "lighting/hall/bbbb.lightbake"], baked);
        Assert.Equal("lighting/empty/aaaa.lightbake: no static draws, nothing to bake", output.ToString().Trim());
    }
}

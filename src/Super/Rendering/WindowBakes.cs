using System.Globalization;
using Rendition;

namespace Super;

/// <summary>What <c>--bake</c> and <c>--bake-check</c> do in the window: bake the frame's areas, or check their bakes.</summary>
internal sealed class WindowBakes(OsWindow window, SceneRenderer scene, WindowFrames frames)
{
    /// <summary>
    /// Plays the given number of ticks, then bakes the frame's baked space, if it names one, and each distinct chunk file
    /// it draws, and saves them in <paramref name="folder"/>, the first content root, as
    /// <c>lighting/&lt;name&gt;.lightbake</c> and <c>lighting/&lt;name&gt;/&lt;hash&gt;.lightbake</c>. Chunks with the
    /// same contents share a file, which is baked once. Prints what each bake found. A chunk that owns no static draws is
    /// reported and skipped, and counted in <paramref name="failed"/>. Returns the validation message count.
    /// </summary>
    /// <exception cref="InvalidOperationException">The frame names no baked space and no chunk.</exception>
    public int Bake(GameLoop host, long ticks, BakeQuality quality, string folder, out int failed)
    {
        float time = frames.PlayAndDraw(host, ticks);
        var chunks = host.View.Indirect.BakedChunks;
        var frame = scene.Renderer.LastBakeFrame;
        var targets = BakeCommand.Targets(
            host.View.Indirect.BakedSpace is { } named ? BakeFiles.Space(named.Name) : null,
            chunks.Count,
            chunk => frame!.ChunkFile(chunk));

        var (width, height) = window.FramebufferSize();
        frames.ApplyGraphics(host.Graphics);
        failed = BakeCommand.Bake(
            targets,
            target => target.Chunk == BakeTarget.Space || frame!.ChunkStaticDraws(target.Chunk) > 0,
            target =>
            {
                // Each is baked at its area's own texel density, which is what a frame checks its bake against.
                byte[] bake = target.Chunk == BakeTarget.Space
                    ? scene.Bake(host.View, host.Content, new FrameRequest(width, height, time),
                        new BakeSettings(host.View.Indirect.BakedSpace!.Value.TexelsPerMetre, quality))
                    : scene.BakeChunk(host.View, host.Content, new FrameRequest(width, height, time), target.Chunk,
                        new BakeSettings(chunks[target.Chunk].TexelsPerMetre, quality));

                string path = Path.GetFullPath(Path.Combine(folder, target.File));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bake);
                Console.Write(DescribeBake(path, bake.Length, LightBakeFile.Read(bake).Report));
            },
            Console.Out);

        return scene.Renderer.ValidationErrors;
    }

    /// <summary>
    /// Plays the given number of ticks, draws the frame and checks the bake of its baked space and of each distinct
    /// chunk file in the cooked content, the one the frame was drawn with, printing a line for each: missing, stale
    /// with what changed, or fresh. Returns the number of missing or stale bakes.
    /// </summary>
    public int BakeCheck(GameLoop host, long ticks)
    {
        frames.PlayAndDraw(host, ticks);
        var frame = scene.Renderer.LastBakeFrame;
        if (frame is null)
        {
            return BakeCommand.Check([], _ => null, Console.Out);
        }

        var targets = BakeCommand.Targets(
            frame.Space is not null && host.View.Indirect.BakedSpace is { } space ? BakeFiles.Space(space.Name) : null,
            frame.Chunks.Count,
            chunk => frame.ChunkFile(chunk));
        return BakeCommand.Check(
            targets,
            target =>
            {
                var inputs = target.Chunk == BakeTarget.Space ? frame.Space!.Value : frame.Chunks[target.Chunk];
                return scene.Baking.Bakes.Saved(target.File) is { } saved ? inputs.Changes(saved) : null;
            },
            Console.Out);
    }

    /// <summary>What <see cref="Bake"/> prints about a bake it saved.</summary>
    internal static string DescribeBake(string path, long bytes, LightBakeReport report)
    {
        string lights = report.LightsWithoutChannel.Count == 0
            ? "none"
            : string.Join(", ", report.LightsWithoutChannel.Select(key => key.ToString(CultureInfo.InvariantCulture)));
        return string.Create(CultureInfo.InvariantCulture,
            $"""
            {path}: {bytes / 1024.0 / 1024.0:0.0} MB
              time: {report.Time.TotalSeconds:0.0} s
              samples: {report.Samples} a texel
              invalid texels: {report.InvalidTexelShare * 100f:0.00}%
              Mixed lights without a shadow mask channel: {lights}

            """);
    }
}

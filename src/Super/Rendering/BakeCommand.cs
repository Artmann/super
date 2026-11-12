namespace Super;

/// <summary>One file <c>--bake</c> writes and <c>--bake-check</c> checks: the space's, or the first chunk that stands for a chunk file.</summary>
/// <param name="File">The file's name in the content, such as <c>lighting/town.lightbake</c>.</param>
/// <param name="Chunk">The frame's chunk that stands for the file, or <see cref="Space"/> for the baked space.</param>
internal readonly record struct BakeTarget(string File, int Chunk)
{
    /// <summary>The <see cref="Chunk"/> of the baked space.</summary>
    public const int Space = -1;
}

/// <summary>What <c>--bake</c> and <c>--bake-check</c> decide before and after they touch the GPU or the disk.</summary>
internal static class BakeCommand
{
    /// <summary>The message of a frame that names no baked space and no chunk.</summary>
    public const string NoAreaMessage =
        "--bake found no baked space or chunk in the frame. Name one with view.BakedLighting or view.BakedChunk.";

    /// <summary>
    /// The files a frame draws, in the order to bake them: the space's, if it has one, then each distinct chunk file in
    /// order of first appearance. Chunks with the same contents share a file however many copies stand in the frame, so
    /// it is baked once, from the first copy.
    /// </summary>
    /// <exception cref="InvalidOperationException">The frame names no space and no chunk.</exception>
    public static List<BakeTarget> Targets(string? spaceFile, int chunkCount, Func<int, string> chunkFile)
    {
        var targets = new List<BakeTarget>();
        if (spaceFile is not null)
        {
            targets.Add(new BakeTarget(spaceFile, BakeTarget.Space));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            string file = chunkFile(chunk);
            if (seen.Add(file))
            {
                targets.Add(new BakeTarget(file, chunk));
            }
        }

        if (targets.Count == 0)
        {
            throw new InvalidOperationException(NoAreaMessage);
        }

        return targets;
    }

    /// <summary>
    /// Writes one line a file to <paramref name="output"/>: <c>missing</c>, <c>stale, its … changed</c> or <c>fresh</c>.
    /// <paramref name="changesOf"/> answers null for a target with no bake, and what changed for one that has. Returns the
    /// number of files that are missing or stale.
    /// </summary>
    public static int Check(
        IReadOnlyList<BakeTarget> targets, Func<BakeTarget, IReadOnlyList<string>?> changesOf, TextWriter output)
    {
        if (targets.Count == 0)
        {
            output.WriteLine("The frame names no baked space or chunk, so there is no bake to check.");
            return 0;
        }

        int bad = 0;
        foreach (var target in targets)
        {
            var changes = changesOf(target);
            if (changes is null)
            {
                output.WriteLine($"{target.File}: missing");
                bad++;
            }
            else if (changes.Count > 0)
            {
                output.WriteLine($"{target.File}: stale, its {string.Join(", ", changes)} changed");
                bad++;
            }
            else
            {
                output.WriteLine($"{target.File}: fresh");
            }
        }

        return bad;
    }
}

using Rendition;

namespace Super;

/// <summary>
/// Says once, on the error stream, why the first area a frame wanted a bake for isn't baked, when the game would bake
/// from source but the GPU can't trace. A player isn't told, so <see cref="GameWindow"/> turns it on only from source.
/// </summary>
internal sealed class NotBakingNotice
{
    private readonly TextWriter _errors;
    private bool _said;

    public NotBakingNotice(TextWriter errors)
    {
        _errors = errors;
    }

    /// <summary>Whether a frame that wants a bake says why it isn't made.</summary>
    public bool On { get; set; }

    /// <summary>
    /// Says why the first area in <paramref name="bakes"/> with no fresh bake, and none on its way, isn't baked: the
    /// space first, then the chunks in order. Says nothing when off, when baked lighting is switched off, when there's
    /// no frame, or after it has said it once.
    /// </summary>
    public void Check(
        BakeFrame? bakes, BakedSpace? space, ReadOnlySpan<BakedChunk> chunks, bool bakedLightingOff, string? reason)
    {
        if (!On || _said || bakedLightingOff || bakes is null)
        {
            return;
        }

        string? wanted = Wanted(bakes, space, chunks);
        if (wanted is null)
        {
            return;
        }

        _said = true;
        _errors.WriteLine($"Not baking {wanted}: {reason}");
    }

    private static string? Wanted(BakeFrame bakes, BakedSpace? space, ReadOnlySpan<BakedChunk> chunks)
    {
        if (space is { } named && bakes.Space is not null && !bakes.SpaceFresh)
        {
            return named.Name;
        }

        for (int i = 0; i < bakes.Chunks.Count && i < chunks.Length; i++)
        {
            if (!bakes.ChunkFresh[i] && !bakes.ChunkPending(i))
            {
                return chunks[i].Name;
            }
        }

        return null;
    }
}

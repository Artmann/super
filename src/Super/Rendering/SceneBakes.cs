using Rendition;

namespace Super;

/// <summary>
/// Where the scene's light bakes come from and go to: the bakes in the content, the <see cref="BakeCache"/> of bakes
/// made on this machine, and the bakes the renderer makes by itself as areas arrive.
/// </summary>
internal sealed class SceneBakes
{
    private readonly Renderer _renderer;

    // The chunk files written to the cache on the thread pool, for the render thread to forget the bakes it holds.
    private readonly WrittenBakes _written = new();
    private readonly NotBakingNotice _notBaking = new(Console.Error);
    private BakeSaver? _saver;
    private IContentFiles? _files;
    private int _filesReloads;

    /// <summary>Gives the renderer the bake store, which reads through this.</summary>
    public SceneBakes(Renderer renderer)
    {
        _renderer = renderer;
        Bakes = new LightBakeStore(ReadBake, Console.Error);
        renderer.Bakes = Bakes;
    }

    /// <summary>The bakes in the content last drawn with, which the renderer checks each frame's baked areas against.</summary>
    internal LightBakeStore Bakes { get; }

    /// <summary>
    /// The chunk bakes made on this machine, read when the cooked content has no bake of that name. Null, the default,
    /// reads cooked content only. Read and written where the bake files are, never in a frame's own work.
    /// </summary>
    internal BakeCache? BakeCache { get; set; }

    /// <summary>
    /// Set when the game would bake from source but the GPU can't trace, so the first area that wants a bake says once,
    /// on the error stream, why it isn't baked.
    /// </summary>
    internal bool SaysWhyNotBaking
    {
        get => _notBaking.On;
        set => _notBaking.On = value;
    }

    /// <summary>
    /// Bakes what <paramref name="plan"/> says in the background, or nothing when it is null. A space's bake is written
    /// into the plan's folder and a chunk's into <see cref="BakeCache"/>; see <see cref="BakeSaver"/>. Setting it stops
    /// the bake that runs and waits for the GPU the first time, so call it between frames.
    /// </summary>
    internal void BakeOnArrival(AutoBakePlan? plan)
    {
        if (plan is null)
        {
            _renderer.AutoBake = null;
            return;
        }

        // One saver while the folder and cache stay, so bakes are kept in the order they finished across changes.
        if (_saver is null || _saver.SpaceFolder != plan.SpaceFolder || _saver.Cache != BakeCache)
        {
            _saver?.Wait(TimeSpan.FromSeconds(10));
            _saver = new BakeSaver(plan.SpaceFolder, BakeCache, _written.Written, Console.Out, Console.Error);
        }

        _renderer.AutoBake = new AutoBake(plan.Budget, plan.Spaces, plan.Qualities, _saver.Done);
    }

    /// <summary>Reads the bakes again when the content's files changed, and those the background saver wrote since the last frame.</summary>
    public void Follow(Content content)
    {
        // The bakes read so far came from other files, or from these before they were read again, so they're read again.
        if (!ReferenceEquals(_files, content.Store.Files) || _filesReloads != content.Data.Reloads)
        {
            Bakes.Forget();
            _filesReloads = content.Data.Reloads;
        }

        // A chunk baked in the background is in the cache now, so the bake held for its file is read again.
        _written.ForgetIn(Bakes);
        _files = content.Store.Files;
    }

    /// <summary>Says, once, why the first area the last frame wanted a bake for isn't baked.</summary>
    public void SayWhyNotBaking(View view, RenderEffects off) => _notBaking.Check(
        _renderer.LastBakeFrame, view.Indirect.BakedSpace, view.Indirect.PlacedBakedChunks, off.HasFlag(RenderEffects.BakedLighting), _renderer.NoTraceReason);

    /// <summary>Waits for a bake still being written, which would leave a temp file in the sources if the process ended under it.</summary>
    public void WaitForSaver() => _saver?.Wait(TimeSpan.FromSeconds(10));

    // A bake is cooked as data under its own name, so anything else of that name isn't one. Failing that, the cache
    // of bakes made on this machine may have it.
    private byte[]? ReadBake(string name) =>
        _files is { } files && files.KindOf(name) == ContentKind.Data
            ? files.Read(name)
            : BakeCache?.Read(name);
}

namespace Super.Physics;

/// <summary>
/// Whether a <see cref="Physics"/> is disposed, shared by the parts it hands out, so a call on any of them after
/// Dispose throws naming the Physics.
/// </summary>
internal sealed class DisposalGuard(object owner)
{
    public bool IsDisposed { get; set; }

    public void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, owner);
}

namespace Super;

/// <summary>
/// Decides when <see cref="GameWindow"/> hands the renderer a new background bake. It stays off until
/// <see cref="TurnOn"/>, which only <see cref="GameWindow.Run{TGame}"/> calls, so screenshots, recordings and tests never
/// bake by themselves. Once on, <see cref="Changed"/> says yes the first time and then only when
/// <see cref="Graphics.BakeOnArrival"/> or <see cref="Graphics.BakeBudget"/> changes, since a new
/// <see cref="Rendition.AutoBake"/> would start its queue again.
/// </summary>
internal sealed class AutoBakeSwitch
{
    private bool _on;
    private (bool On, float? Budget)? _applied;

    /// <summary>Whether <see cref="TurnOn"/> was called.</summary>
    public bool IsOn => _on;

    /// <summary>Lets <see cref="Changed"/> answer yes from now on.</summary>
    public void TurnOn() => _on = true;

    /// <summary>
    /// Whether the renderer needs a new bake plan for <paramref name="graphics"/>: on, and either never applied or with a
    /// different <see cref="Graphics.BakeOnArrival"/> or <see cref="Graphics.BakeBudget"/> than last time. A yes counts
    /// as applied.
    /// </summary>
    public bool Changed(Graphics graphics)
    {
        if (!_on)
        {
            return false;
        }

        var wanted = (graphics.BakeOnArrival, graphics.BakeBudget);
        if (_applied == wanted)
        {
            return false;
        }

        _applied = wanted;
        return true;
    }
}

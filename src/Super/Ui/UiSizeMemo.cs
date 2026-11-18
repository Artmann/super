namespace Super.Ui;

/// <summary>
/// The last sizes a node gave, by the offer that asked. Layout asks the same question a
/// few times, and four answers cover what stacks ask.
/// </summary>
internal sealed class UiSizeMemo
{
    private const int Capacity = 4;

    private readonly (Proposal Proposal, UiSize Size)[] _answers = new (Proposal, UiSize)[Capacity];
    private int _count;

    public bool TryRecall(Proposal proposal, out UiSize size)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_answers[i].Proposal == proposal)
            {
                size = _answers[i].Size;
                return true;
            }
        }

        size = default;
        return false;
    }

    public void Remember(Proposal proposal, UiSize size)
    {
        _answers[_count % Capacity] = (proposal, size);
        _count = Math.Min(_count + 1, Capacity);
    }

    public void Clear() => _count = 0;
}

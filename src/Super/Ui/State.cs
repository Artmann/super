namespace Super.Ui;

/// <summary>Lets a view forget a state without knowing what's inside it.</summary>
internal interface IWatchedState
{
    void Forget(UiNode reader);
}

/// <summary>
/// A value owned by a place in the interface. Reading it during Body signs that component
/// up for changes, and setting it runs those components again on the next frame.
/// Pass one to a child to give the child read and write access to it.
/// </summary>
public sealed class State<T> : IWatchedState
{
    private readonly List<UiNode> _readers = [];
    private T _value;

    internal State(T value)
    {
        _value = value;
    }

    public T Value
    {
        get
        {
            if (UiNode.Rendering is { } reader && !_readers.Contains(reader))
            {
                _readers.Add(reader);
                reader.Watch(this);
            }

            return _value;
        }

        set
        {
            if (EqualityComparer<T>.Default.Equals(_value, value))
            {
                return;
            }

            _value = value;
            foreach (var reader in _readers)
            {
                reader.MarkDirty();
            }
        }
    }

    /// <summary>How many places are signed up for changes. Tests use it to check a removed view lets go.</summary>
    internal int ReaderCount => _readers.Count;

    void IWatchedState.Forget(UiNode reader) => _readers.Remove(reader);
}

namespace Super.Ui;

/// <summary>
/// A value owned by a place in the interface. Reading it during Body signs that component
/// up for changes, and setting it runs those components again on the next frame.
/// Pass one to a child to give the child read and write access to it.
/// </summary>
public sealed class State<T>
{
    private readonly List<UiBodyHooks> _readers = [];
    private T _value;

    internal State(T value)
    {
        _value = value;
    }

    public T Value
    {
        get
        {
            if (UiBodyHooks.Running is { } reader && !_readers.Contains(reader))
            {
                _readers.Add(reader);
                reader.Reading(() => _readers.Remove(reader));
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
                reader.Changed();
            }
        }
    }

    /// <summary>How many places are signed up for changes. Tests use it to check a removed view lets go.</summary>
    internal int ReaderCount => _readers.Count;
}

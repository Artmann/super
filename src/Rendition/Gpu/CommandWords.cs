using System.Runtime.InteropServices;

namespace Rendition.Gpu;

/// <summary>
/// The storage behind a <see cref="CommandList"/>: a growing int array, and the label names the
/// words refer to by index. Both are kept between frames, so once grown they allocate nothing.
/// </summary>
internal sealed class CommandWords
{
    private readonly List<string> _labels = [];
    private int[] _words = new int[256];
    private int _count;

    public ReadOnlySpan<int> All => _words.AsSpan(0, _count);

    public string Label(int index) => _labels[index];

    /// <summary>Keeps <paramref name="name"/> and returns the index a word can refer to it by.</summary>
    public int AddLabel(string name)
    {
        _labels.Add(name);
        return _labels.Count - 1;
    }

    public void Clear()
    {
        _count = 0;
        _labels.Clear();
    }

    public void Write(Command command, ReadOnlySpan<int> arguments)
    {
        var words = Take(1 + arguments.Length);
        words[0] = (int)command;
        arguments.CopyTo(words[1..]);
    }

    /// <summary>Writes <paramref name="command"/>, the byte count, and the bytes four to a word in memory order, padded with zeros.</summary>
    public void Write(Command command, ReadOnlySpan<byte> bytes)
    {
        var words = Take(2 + (bytes.Length + 3) / 4);
        words[0] = (int)command;
        words[1] = bytes.Length;
        var data = words[2..];
        if (data.Length > 0)
        {
            data[^1] = 0;
        }

        bytes.CopyTo(MemoryMarshal.AsBytes(data));
    }

    public Span<int> Take(int count)
    {
        if (_count + count > _words.Length)
        {
            Array.Resize(ref _words, Math.Max(_words.Length * 2, _count + count));
        }

        var words = _words.AsSpan(_count, count);
        _count += count;
        return words;
    }
}

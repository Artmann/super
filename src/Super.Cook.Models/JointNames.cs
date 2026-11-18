namespace Super.Cook.Models;

/// <summary>A rig's joint names, and the joint each name, or the part of it after a colon, belongs to.</summary>
internal sealed class JointNames
{
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _byBareName = new(StringComparer.Ordinal);

    public JointNames(string[] names)
    {
        Names = names;
        for (int i = 0; i < names.Length; i++)
        {
            _byName.TryAdd(names[i], i);
        }
    }

    public string[] Names { get; }

    /// <summary>The joint of each name, the first when two share one.</summary>
    public IReadOnlyDictionary<string, int> ByName => _byName;

    /// <summary>
    /// The joint a clip's channel drives. Mixamo names joints "mixamorig:Hips", but adds a
    /// number to the prefix in some downloads ("mixamorig12:Hips"), so when the full name
    /// isn't a joint, the part after the colon is matched on its own.
    /// </summary>
    public int? Of(string channel)
    {
        if (_byName.TryGetValue(channel, out int joint))
        {
            return joint;
        }

        if (_byBareName.Count == 0)
        {
            for (int i = 0; i < Names.Length; i++)
            {
                _byBareName.TryAdd(BareName(Names[i]), i);
            }
        }

        return _byBareName.TryGetValue(BareName(channel), out joint) ? joint : null;
    }

    private static string BareName(string name) => name[(name.LastIndexOf(':') + 1)..];
}

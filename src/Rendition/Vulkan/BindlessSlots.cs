using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// One binding of the <see cref="ResourceTable"/>'s set: an array of texture descriptors that shaders index directly.
/// A texture takes a free slot, a destroyed one waits as dying for the frames that still read it, and a freed slot
/// points at <see cref="Fallback"/> so a stale material draws something harmless.
/// </summary>
internal sealed unsafe class BindlessSlots : IDisposable
{
    private readonly VulkanContext _context;
    private readonly (DescriptorSet Set, uint Binding) _target;
    private readonly (Sampler Repeat, Sampler Clamp) _samplers;
    private readonly string _what;
    private readonly List<GpuTexture?> _textures = [];
    private readonly Stack<int> _free = [];
    private readonly bool[] _dying;

    /// <summary>
    /// Slots for <paramref name="capacity"/> textures in a binding of a set. <paramref name="what"/> names them in the
    /// error a full binding throws, such as "textures".
    /// </summary>
    public BindlessSlots(
        VulkanContext context, (DescriptorSet Set, uint Binding) target, int capacity, (Sampler Repeat, Sampler Clamp) samplers,
        string what)
    {
        _context = context;
        _target = target;
        _samplers = samplers;
        _what = what;
        Capacity = capacity;
        _dying = new bool[capacity];
    }

    /// <summary>How many textures the binding holds.</summary>
    public int Capacity { get; }

    /// <summary>The slot whose texture a freed slot shows until it's taken again.</summary>
    public int Fallback { get; set; }

    /// <summary>
    /// Whether <see cref="Add"/> has a slot to give now. Ask before handing over a texture that must not be freed at
    /// once, since a full binding frees the texture it refuses.
    /// </summary>
    public bool HasRoom => _free.Count > 0 || _textures.Count < Capacity;

    /// <summary>
    /// Takes a slot for a texture and points it at the image, sampled with clamped edges or repeating. The slot is
    /// free in every frame in flight, which is what lets the set be written while bound.
    /// </summary>
    public int Add(GpuTexture texture, bool clampEdges = false)
    {
        int slot;
        if (_free.Count > 0)
        {
            slot = _free.Pop();
            _textures[slot] = texture;
        }
        else
        {
            if (_textures.Count == Capacity)
            {
                texture.Dispose();
                throw new InvalidOperationException($"The renderer holds at most {Capacity} {_what}.");
            }

            slot = _textures.Count;
            _textures.Add(texture);
        }

        // The slot's descriptor is written once, so its sampler is the texture's for life. A
        // material can't change it, which would change it for every other material using it.
        Write(slot, texture, clampEdges);
        return slot;
    }

    /// <summary>Whether a slot holds a texture that hasn't been destroyed.</summary>
    public bool IsLive(int slot) => Holds(slot) && !_dying[slot];

    /// <summary>Whether a slot holds a texture that has been destroyed but still waits for the frames reading it.</summary>
    public bool IsDying(int slot) => Holds(slot) && _dying[slot];

    /// <summary>
    /// Marks a live texture as destroyed, so destroying it again fails, and hands back the
    /// image for the time until <see cref="Release"/> frees it.
    /// </summary>
    public GpuTexture MarkDying(int slot)
    {
        _dying[slot] = true;
        return _textures[slot]!;
    }

    /// <summary>Frees a texture's slot once no frame can read it, pointing the slot at <see cref="Fallback"/>.</summary>
    public void Release(int slot)
    {
        var texture = _textures[slot];
        if (texture is null)
        {
            return;
        }

        texture.Dispose();
        _textures[slot] = null;
        _dying[slot] = false;
        Write(slot, _textures[Fallback]!, clampEdges: false);
        _free.Push(slot);
    }

    public void Dispose()
    {
        foreach (var texture in _textures)
        {
            texture?.Dispose();
        }
    }

    private bool Holds(int slot) => slot >= 0 && slot < _textures.Count && _textures[slot] is not null;

    private void Write(int slot, GpuTexture texture, bool clampEdges)
    {
        var imageInfo = new DescriptorImageInfo(
            clampEdges ? _samplers.Clamp : _samplers.Repeat, texture.View, ImageLayout.ShaderReadOnlyOptimal);
        var write = Descriptors.ImageWrite(_target.Set, _target.Binding, DescriptorType.CombinedImageSampler, &imageInfo);
        write.DstArrayElement = (uint)slot;
        _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }
}

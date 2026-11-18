using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>What the CPU needs to know of a material to draw it, kept beside the copy the shaders read.</summary>
/// <param name="Flags">The material's flags.</param>
/// <param name="Shader">Its surface shader, 0 for the standard one.</param>
/// <param name="Cutouts">Whether its texture cuts holes in it.</param>
/// <param name="Wind">How far it sways in the wind, as a share of its strength: 0 for not at all.</param>
/// <param name="Fade">How it fades out with distance, null when it never does.</param>
/// <param name="Glows">Whether it gives light of its own: some channel of its emissive colour is above 0.</param>
/// <param name="Subsurface">Whether it scatters light under its surface.</param>
internal readonly record struct MaterialFacts(
    MaterialFlags Flags, int Shader, bool Cutouts, float Wind, DistanceFade? Fade, bool Glows, bool Subsurface);

/// <summary>
/// Every material, in a buffer the shaders index by <see cref="MaterialHandle"/>, binding 1 of the
/// <see cref="ResourceTable"/>'s set, with what the CPU needs to know of each beside it.
/// </summary>
internal sealed unsafe class MaterialTable
{
    public const int MaxMaterials = 1024;

    /// <summary>
    /// Texture slots a material can name: base colour, normal, surface, emissive, height,
    /// detail colour, detail normal, mask, occlusion and toon ramp, then the four shader textures.
    /// </summary>
    public const int TexturesPerMaterial = OwnTextures + ShaderTextures.Length;

    /// <summary>How many of a material's texture slots come before its shader textures.</summary>
    private const int OwnTextures = 10;

    private readonly GpuBuffer _buffer;
    private readonly MaterialDefaults _defaults;
    private readonly MaterialFacts[] _facts = new MaterialFacts[MaxMaterials];
    private readonly int[] _textures = new int[MaxMaterials * TexturesPerMaterial];
    private readonly Stack<int> _free = [];
    private readonly bool[] _live = new bool[MaxMaterials];
    private int _count;

    /// <summary>
    /// Writes materials into <paramref name="buffer"/>, a host-visible buffer of <see cref="BufferSize"/> bytes that the
    /// caller owns, with <paramref name="defaults"/> where a material has no texture of its own.
    /// </summary>
    public MaterialTable(GpuBuffer buffer, in MaterialDefaults defaults)
    {
        _buffer = buffer;
        _defaults = defaults;
        SwayOf = material => _facts[material.Index].Wind;
        FadeOf = material => _facts[material.Index].Fade;
    }

    /// <summary>The bytes every material takes on the GPU.</summary>
    public static ulong BufferSize => (ulong)(MaxMaterials * sizeof(GpuMaterial));

    /// <summary>What the CPU knows of a material.</summary>
    public MaterialFacts this[MaterialHandle material] => _facts[material.Index];

    /// <summary>How far each material sways in the wind, as a lookup that passes and culling hold on to, made once.</summary>
    public Func<MaterialHandle, float> SwayOf { get; }

    /// <summary>How each material fades out with distance, null when it never does, as a lookup made once.</summary>
    public Func<MaterialHandle, DistanceFade?> FadeOf { get; }

    /// <summary>True when a live material scatters light under its surface, so the frame needs the subsurface blur.</summary>
    public bool AnySubsurface
    {
        get
        {
            for (int i = 0; i < _count; i++)
            {
                if (_live[i] && _facts[i].Subsurface)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether a handle names a material that hasn't been destroyed.</summary>
    public bool IsLive(MaterialHandle material) => material.Index >= 0 && material.Index < _count && _live[material.Index];

    /// <summary>Marks a live material as destroyed, so destroying it again fails.</summary>
    public void MarkDying(int slot) => _live[slot] = false;

    public MaterialHandle Add(in Material material)
    {
        if (_free.Count > 0)
        {
            int slot = _free.Pop();
            Write(slot, material);
            _live[slot] = true;
            return new MaterialHandle(slot);
        }

        if (_count == MaxMaterials)
        {
            throw new InvalidOperationException($"The renderer holds at most {MaxMaterials} materials.");
        }

        // Earlier frames only read earlier slots, so this write needs no synchronization.
        Write(_count, material);
        _live[_count] = true;
        return new MaterialHandle(_count++);
    }

    /// <summary>Lets a material slot be reused. Call once no frame in flight reads it.</summary>
    public void Release(int slot) => _free.Push(slot);

    /// <summary>The texture slots a material names, or -1 where it uses a built-in one.</summary>
    public ReadOnlySpan<int> TexturesOf(MaterialHandle material) =>
        _textures.AsSpan(material.Index * TexturesPerMaterial, TexturesPerMaterial);

    /// <summary>
    /// Changes a material in place. A frame still on the GPU may see the new values,
    /// which at worst shows them one frame early.
    /// </summary>
    public void Update(MaterialHandle handle, in Material material)
    {
        if (handle.Index < 0 || handle.Index >= _count)
        {
            throw new ArgumentException("The material does not exist.", nameof(handle));
        }

        Write(handle.Index, material);
    }

    private void Write(int index, in Material material)
    {
        ((GpuMaterial*)_buffer.Mapped)[index] = GpuMaterials.ToGpu(material, _defaults);
        _facts[index] = new MaterialFacts(
            material.Flags,
            material.Custom.Shader?.Index ?? 0,
            material.AlphaCutoff > 0f,
            GpuMaterials.Sway(material),
            material.Fade,
            material.Emissive.X > 0f || material.Emissive.Y > 0f || material.Emissive.Z > 0f,
            material.Subsurface.IsOn);

        var owned = _textures.AsSpan(index * TexturesPerMaterial, TexturesPerMaterial);
        owned[0] = material.Textures.BaseColor?.Index ?? -1;
        owned[1] = material.Textures.Normal?.Index ?? -1;
        owned[2] = material.Textures.Surface?.Index ?? -1;
        owned[3] = material.Textures.Emissive?.Index ?? -1;
        owned[4] = material.Height.Texture?.Index ?? -1;
        owned[5] = material.Detail.Color?.Index ?? -1;
        owned[6] = material.Detail.Normal?.Index ?? -1;
        owned[7] = material.Weathering.Mask?.Index ?? -1;
        owned[8] = material.Textures.Occlusion?.Index ?? -1;
        owned[9] = material.Toon?.Ramp?.Index ?? -1;
        for (int i = 0; i < ShaderTextures.Length; i++)
        {
            owned[OwnTextures + i] = material.Custom.Textures[i]?.Index ?? -1;
        }
    }
}

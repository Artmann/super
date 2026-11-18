using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// What's been destroyed while a frame might still read it. Slot i holds what was queued while frame slot i was the
/// last one submitted; its fence says when the GPU is done with all of it. See "Resource lifetime" in docs/design.md.
/// </summary>
internal sealed class RetiredResources(FrameRing ring, MeshStore meshes, ResourceTable resources)
{
    private readonly RetiredSlot[] _slots = [.. Enumerable.Range(0, FrameRing.Count).Select(_ => new RetiredSlot())];

    /// <summary>The slot whose fence covers the frame most recently submitted, for what goes now.</summary>
    public RetiredSlot AfterLastSubmitted => _slots[ring.LastSubmitted];

    /// <summary>The slot of the frame being recorded, for what that frame still reads.</summary>
    public RetiredSlot AfterRecording => _slots[ring.Index];

    /// <summary>Frees what was queued against a frame slot, once its fence has signalled.</summary>
    public void Release(int frameSlot)
    {
        var slot = _slots[frameSlot];
        foreach (var mesh in slot.FreedMeshes)
        {
            meshes.FreeDetailsOf(mesh);
            mesh.Dispose();
        }

        foreach (int texture in slot.FreedTextures)
        {
            resources.ReleaseTexture(texture);
        }

        foreach (int array in slot.FreedTextureArrays)
        {
            resources.ReleaseTextureArray(array);
        }

        foreach (int material in slot.FreedMaterials)
        {
            resources.ReleaseMaterial(material);
        }

        foreach (var other in slot.FreedOthers)
        {
            other.Dispose();
        }

        slot.Clear();
    }
}

/// <summary>One frame slot's share of <see cref="RetiredResources"/>.</summary>
internal sealed class RetiredSlot
{
    public readonly List<Mesh> FreedMeshes = [];
    public readonly List<int> FreedTextures = [];
    public readonly List<int> FreedTextureArrays = [];
    public readonly List<int> FreedMaterials = [];
    public readonly List<IDisposable> FreedOthers = [];

    public void Clear()
    {
        FreedMeshes.Clear();
        FreedTextures.Clear();
        FreedTextureArrays.Clear();
        FreedMaterials.Clear();
        FreedOthers.Clear();
    }
}

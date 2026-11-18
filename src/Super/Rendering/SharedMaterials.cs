using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rendition;

namespace Super;

/// <summary>
/// The materials of generated meshes, one for each surface however many meshes use it. A game that builds hundreds of
/// meshes from one surface, such as terrain chunks, would otherwise run out of the renderer's materials.
/// </summary>
/// <remarks>
/// Surfaces match by value, their shader's <see cref="SurfaceShader.Parameters"/>, <see cref="SurfaceShader.Textures"/> and
/// <see cref="SurfaceShader.Arrays"/> included. A material keeps a copy of the surface it was made from, so a game
/// that changes one of those arrays in place gets a new material for the meshes it adds after, as before sharing.
/// A mesh that carries its own texture never comes here, since its material names that texture.
/// </remarks>
internal sealed class SharedMaterials(Action<MaterialHandle> destroy)
{
    private readonly Dictionary<Surface, (MaterialHandle Handle, int Users)> _materials = new(SurfaceValues.Instance);
    private readonly Dictionary<MaterialHandle, Surface> _surfaces = [];

    /// <summary>How many materials are held. Tests read it.</summary>
    public int Count => _materials.Count;

    /// <summary>Takes one more use of the surface's material, when there is one.</summary>
    public bool TryShare(Surface surface, out MaterialHandle handle)
    {
        ref var known = ref CollectionsMarshal.GetValueRefOrNullRef(_materials, surface);
        if (Unsafe.IsNullRef(ref known))
        {
            handle = default;
            return false;
        }

        known.Users++;
        handle = known.Handle;
        return true;
    }

    /// <summary>Keeps a material just made for the surface, with its first use.</summary>
    public void Add(Surface surface, MaterialHandle handle)
    {
        var copy = surface with
        {
            Shader = surface.Shader is { } shader
                ? shader with { Parameters = [.. shader.Parameters], Textures = [.. shader.Textures], Arrays = [.. shader.Arrays] }
                : null,
        };
        _materials.Add(copy, (handle, 1));
        _surfaces.Add(handle, copy);
    }

    /// <summary>Lets go of one use of the material, and destroys it once nothing uses it.</summary>
    public void Release(MaterialHandle handle)
    {
        var surface = _surfaces[handle];
        ref var known = ref CollectionsMarshal.GetValueRefOrNullRef(_materials, surface);
        if (--known.Users > 0)
        {
            return;
        }

        _materials.Remove(surface);
        _surfaces.Remove(handle);
        destroy(handle);
    }

    /// <summary>Surfaces equal in every value, with their arrays compared item by item rather than by reference.</summary>
    private sealed class SurfaceValues : IEqualityComparer<Surface>
    {
        public static readonly SurfaceValues Instance = new();

        public bool Equals(Surface? x, Surface? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null
                && WithoutArrays(x) == WithoutArrays(y)
                && (x.Shader?.Parameters ?? []).AsSpan().SequenceEqual(y.Shader?.Parameters ?? [])
                && (x.Shader?.Textures ?? []).AsSpan().SequenceEqual(y.Shader?.Textures ?? [])
                && (x.Shader?.Arrays ?? []).AsSpan().SequenceEqual(y.Shader?.Arrays ?? []));

        public int GetHashCode(Surface surface)
        {
            var hash = new HashCode();
            hash.Add(WithoutArrays(surface));
            foreach (var parameter in surface.Shader?.Parameters ?? [])
            {
                hash.Add(parameter);
            }

            foreach (var texture in surface.Shader?.Textures ?? [])
            {
                hash.Add(texture);
            }

            foreach (var array in surface.Shader?.Arrays ?? [])
            {
                hash.Add(array);
            }

            return hash.ToHashCode();
        }

        // Empty collection expressions are one shared empty array, so what's left compares as a record does.
        private static Surface WithoutArrays(Surface surface) =>
            surface with { Shader = surface.Shader is { } shader ? shader with { Parameters = [], Textures = [], Arrays = [] } : null };
    }
}

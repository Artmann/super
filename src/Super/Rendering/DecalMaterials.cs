using Rendition;

namespace Super;

/// <summary>
/// The view's decals for a frame, each with its surface's material. A material is made the first time a surface is
/// used, with the frame it was last used in, and kept until content unloads, or until there are more than
/// <see cref="MaxDecalMaterials"/>.
/// </summary>
internal sealed class DecalMaterials(Renderer renderer, UploadedItems items)
{
    /// <summary>
    /// How many decal materials are kept. Past this, the ones this frame didn't use are let go, so a game that
    /// makes a new <see cref="Surface"/> value every frame doesn't fill the material slots. Twice what one
    /// frame can use, so the materials of a steady scene are never made again.
    /// </summary>
    internal const int MaxDecalMaterials = 2 * Rendition.Passes.DecalBatch.MaxDecals;

    private readonly Dictionary<Surface, (MaterialHandle Handle, long Frame)> _materials = [];
    private readonly List<Surface> _unusedSurfaces = [];
    private DecalDraw[] _decals = new DecalDraw[16];
    private int _count;
    private long _frame;

    /// <summary>The decals <see cref="Fill"/> gave the frame.</summary>
    public ReadOnlySpan<DecalDraw> Drawn => _decals.AsSpan(0, _count);

    /// <summary>The view's decals, each with its surface's material, or none while decals are off.</summary>
    public void Fill(View view, Content content, RenderEffects off)
    {
        _count = 0;
        if (off.HasFlag(RenderEffects.Decals))
        {
            return;
        }

        var placed = view.Effects.DrawnDecals;
        if (_decals.Length < placed.Length)
        {
            _decals = new DecalDraw[Math.Max(placed.Length, _decals.Length * 2)];
        }

        _frame++;
        foreach (ref readonly var decal in placed)
        {
            _decals[_count++] = new DecalDraw(decal.Box, MaterialFor(decal.Material, content), decal.Blend);
        }

        ForgetUnused();
    }

    /// <summary>Destroys every decal material, since they point at textures being freed. They're made again when next used.</summary>
    public void DestroyAll()
    {
        foreach (var material in _materials.Values)
        {
            renderer.Assets.DestroyMaterial(material.Handle);
        }

        _materials.Clear();
    }

    /// <summary>
    /// Past <see cref="MaxDecalMaterials"/>, lets go of every decal material this frame didn't use. The renderer
    /// destroys them once the frames still in flight are done with them.
    /// </summary>
    private void ForgetUnused()
    {
        if (_materials.Count <= MaxDecalMaterials)
        {
            return;
        }

        _unusedSurfaces.Clear();
        foreach (var (surface, material) in _materials)
        {
            if (material.Frame != _frame)
            {
                _unusedSurfaces.Add(surface);
            }
        }

        foreach (var surface in _unusedSurfaces)
        {
            renderer.Assets.DestroyMaterial(_materials[surface].Handle);
            _materials.Remove(surface);
        }
    }

    /// <summary>
    /// The material a decal lays on: the surface's colour, roughness and opacity, and its colour, normal and
    /// surface textures. A surface used before gets the material made then.
    /// </summary>
    private MaterialHandle MaterialFor(Surface surface, Content content)
    {
        if (_materials.TryGetValue(surface, out var known))
        {
            _materials[surface] = (known.Handle, _frame);
            return known.Handle;
        }

        var material = SurfaceMaterials.MaterialOf(surface) with
        {
            Textures = new MaterialTextures
            {
                BaseColor = items.TextureFor(surface.ColorTexture, content),
                Normal = items.TextureFor(surface.NormalTexture, content),
                Surface = items.TextureFor(surface.SurfaceTexture, content),
            },
            Flags = MaterialFlags.None,
        };
        var handle = renderer.Assets.CreateMaterial(material);
        _materials.Add(surface, (handle, _frame));
        return handle;
    }
}

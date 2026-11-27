using Rendition;

namespace Super;

/// <summary>The checks on what a game hands back by index, each naming the field that was wrong.</summary>
internal static class ItemChecks
{
    /// <summary>Throws unless <paramref name="index"/> names an item.</summary>
    public static void RequireNamed(ContentItems items, int index, string field)
    {
        if (index < 1 || index > items.Items.Count)
        {
            throw new ArgumentException($"{field} names {index}, but nothing was named with that index.", field);
        }
    }

    /// <summary>
    /// Throws if the item at <paramref name="index"/> was let go, or if its slot has been given to another since the
    /// ref was made, which its <paramref name="generation"/> tells. Kinds whose slot is never given again pass 0.
    /// </summary>
    public static void RequireLoaded(ContentItems items, int index, int generation, string field)
    {
        if (items.Slots.IsGone(index, generation))
        {
            throw new ArgumentException($"{field} names {index}, which was unloaded.", field);
        }
    }

    /// <summary>
    /// The item at <paramref name="index"/>, which must be named, not let go, and a <typeparamref name="T"/>. The
    /// <paramref name="generation"/> is the ref's, as for <see cref="RequireLoaded"/>.
    /// </summary>
    public static T Require<T>(ContentItems items, int index, int generation, string field, string kind)
        where T : class
    {
        RequireNamed(items, index, field);
        RequireLoaded(items, index, generation, field);
        if (items.Items[index - 1] is not T item)
        {
            throw new ArgumentException($"{field} names {index}, which is not a {kind}.", field);
        }

        return item;
    }

    /// <summary>
    /// Throws unless everything the surface names was named first and its arrays fit, as for a generated mesh or a
    /// decal.
    /// </summary>
    public static void RequireSurface(ContentItems items, Surface surface)
    {
        RequireTexture(items, surface.ColorTexture, nameof(surface.ColorTexture));
        RequireTexture(items, surface.NormalTexture, nameof(surface.NormalTexture));
        RequireTexture(items, surface.SurfaceTexture, nameof(surface.SurfaceTexture));
        RequireTexture(items, surface.Height?.Map, "Height.Map");
        RequireTexture(items, surface.Detail?.ColorTexture, "Detail.ColorTexture");
        RequireTexture(items, surface.Detail?.NormalTexture, "Detail.NormalTexture");
        RequireTexture(items, surface.Occlusion?.Map, "Occlusion.Map");
        if (surface.Toon is { } toon)
        {
            RequireTexture(items, toon.Ramp, "Toon.Ramp");
            if (new ToonShading { Steps = toon.Steps, Rim = toon.Rim }.Problem() is { } problem)
            {
                throw new ArgumentException($"The surface's {problem}", nameof(surface));
            }
        }

        if (surface.Shader is { } shader)
        {
            RequireShader(items, shader);
        }
    }

    /// <summary>Whether the surface names the texture anywhere, its shader's textures too.</summary>
    public static bool Names(Surface surface, TextureRef texture)
    {
        TextureRef?[] own =
        [
            surface.ColorTexture, surface.NormalTexture, surface.SurfaceTexture, surface.Height?.Map,
            surface.Detail?.ColorTexture, surface.Detail?.NormalTexture, surface.Occlusion?.Map, surface.Toon?.Ramp,
        ];
        return own.Contains(texture) || (surface.Shader?.Textures.Contains(texture) ?? false);
    }

    private static void RequireShader(ContentItems items, SurfaceShader shader)
    {
        Require<ShaderItem>(items, shader.Fragment.Index, 0, "Shader.Fragment", "shader");
        RequireCount(shader.Parameters.Length, SurfaceShader.MaxParameters, "Shader.Parameters");
        RequireCount(shader.Textures.Length, SurfaceShader.MaxTextures, "Shader.Textures");
        RequireCount(shader.Arrays.Length, SurfaceShader.MaxTextures, "Shader.Arrays");
        for (int i = 0; i < shader.Textures.Length; i++)
        {
            RequireTexture(items, shader.Textures[i], $"Shader.Textures[{i}]");
        }

        for (int i = 0; i < shader.Arrays.Length; i++)
        {
            Require<TextureArrayItem>(items, shader.Arrays[i].Index, 0, $"Shader.Arrays[{i}]", "texture array");
        }
    }

    private static void RequireTexture(ContentItems items, TextureRef? texture, string field)
    {
        if (texture is { } named)
        {
            Require<TextureItem>(items, named.Index, named.Generation, field, "texture");
        }
    }

    private static void RequireCount(int count, int max, string field)
    {
        if (count > max)
        {
            throw new ArgumentException($"{field} has {count} entries, but a surface shader takes at most {max}.", field);
        }
    }
}

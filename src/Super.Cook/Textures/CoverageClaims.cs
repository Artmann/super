using System.Globalization;
using Rendition;

namespace Super.Cook;

/// <summary>The coverage a cut-out's texture keeps: none, its own alpha's, or its alpha times a partner's.</summary>
internal readonly record struct CoverageClaim(float Cutoff, TextureData? Partner);

/// <summary>
/// The coverage each texture keeps through compression, as the cooker kept it. The shader cuts where the colour's alpha
/// times the surface's falls below the cutoff. When both carry partial alpha, the colour keeps the product's coverage
/// against the surface, and the surface is left whole, so compressing it never moves the product. When only one does, it
/// keeps its own. A texture with no cut-out material keeps nothing. When two cut-out materials want different coverage
/// from one texture, the first one's is kept and a warning says so.
/// </summary>
internal sealed class CoverageClaims
{
    private readonly TextureData[] _textures;
    private readonly CoverageClaim?[] _claims;
    private readonly int[] _claimedBy;
    private readonly Action<string>? _warn;

    private CoverageClaims(TextureData[] textures, Action<string>? warn)
    {
        _textures = textures;
        _claims = new CoverageClaim?[textures.Length];
        _claimedBy = new int[textures.Length];
        _warn = warn;
    }

    /// <summary>Each of <paramref name="textures"/>' claim, or null for none. <paramref name="warn"/> hears when two materials disagree.</summary>
    public static CoverageClaim?[] Of(TextureData[] textures, IReadOnlyList<ModelMaterial> materials, Action<string>? warn)
    {
        var claims = new CoverageClaims(textures, warn);
        for (int m = 0; m < materials.Count; m++)
        {
            claims.Add(materials[m], m);
        }

        return claims._claims;
    }

    /// <summary>
    /// The cutoff as a texture's own alpha meets it: the shader cuts by the texture's alpha times the colour's
    /// <paramref name="opacity"/>, so the texture's alpha must reach <paramref name="cutoff"/> over that. 0, for no
    /// cutoff or for one the texture can't reach, leaves the texture's mips as the filter made them.
    /// </summary>
    public static float TextureCutoff(float cutoff, float opacity)
    {
        if (cutoff <= 0f || opacity <= 0f || cutoff > opacity)
        {
            return 0f;
        }

        return cutoff / opacity;
    }

    /// <summary>Claims the coverage the cut-out <paramref name="material"/> wants from its colour and surface textures.</summary>
    private void Add(ModelMaterial material, int index)
    {
        float cutoff = TextureCutoff(material.AlphaCutoff, material.BaseColor.W);
        if (!(cutoff > 0f))
        {
            return;
        }

        int color = material.BaseColorTexture;
        int surface = material.SurfaceTexture;
        var partner = HasPartialAlpha(color) && HasPartialAlpha(surface) && color != surface
            ? _textures[surface]
            : null;
        Claim(color, new CoverageClaim(cutoff, partner), index);
        Claim(surface, new CoverageClaim(partner is null ? cutoff : 0f, null), index);
    }

    private void Claim(int index, CoverageClaim claim, int material)
    {
        if (index < 0 || index >= _textures.Length)
        {
            return;
        }

        if (_claims[index] is not { } existing)
        {
            _claims[index] = claim;
            _claimedBy[index] = material;
            return;
        }

        if (existing != claim)
        {
            _warn?.Invoke($"texture {index} is cut out differently by materials {_claimedBy[index]} and {material} "
                + $"({Describe(existing)}, then {Describe(claim)}); it keeps the coverage of material {_claimedBy[index]}.");
        }
    }

    private bool HasPartialAlpha(int index) =>
        index >= 0 && index < _textures.Length && _textures[index].Format == TextureFormat.Rgba8 && _textures[index].AlphaRange().Lowest < 1f;

    private static string Describe(CoverageClaim claim) => claim.Cutoff <= 0f
        ? "whole, under another texture's alpha"
        : claim.Partner is null
            ? $"cutoff {claim.Cutoff.ToString("0.###", CultureInfo.InvariantCulture)}"
            : $"cutoff {claim.Cutoff.ToString("0.###", CultureInfo.InvariantCulture)} times the surface's alpha";
}

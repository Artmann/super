using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// The frame's draws: for each model the view drew, copies of the parts it shows, each given the draw's transform,
/// tint, bones and the rest, and a static draw's bake ids. Kept in buffers reused every frame.
/// </summary>
internal sealed class DressedDraws(Renderer renderer, UploadedItems items)
{
    /// <summary>The built-in shapes' color when a draw doesn't give one.</summary>
    private static readonly Vector3 CubeColor = new(0.7f);

    private readonly PoseSkinning _skinning = new();
    private readonly BakeIdScratch _scratch = new();
    private DrawItem[] _draws = new DrawItem[64];
    private BakeSphere[] _partBounds = new BakeSphere[16];
    private int _count;

    /// <summary>The draws <see cref="Fill"/> made.</summary>
    public ReadOnlySpan<DrawItem> Shown => _draws.AsSpan(0, _count);

    /// <summary>The bone matrices of the posed draws <see cref="Fill"/> made.</summary>
    public ReadOnlySpan<Matrix4x4> Bones => _skinning.Bones;

    /// <summary>Makes the frame's draws from the view's, skinning each posed draw once however many of its parts are shown.</summary>
    public void Fill(View view, Content content)
    {
        _count = 0;
        _skinning.Clear();
        var context = new DressContext(content, new BakeIdAreas(view.Indirect.BakedSpace, view.Indirect.PlacedBakedChunks, _scratch));
        foreach (ref readonly var draw in view.Drawn)
        {
            var model = items.ModelFor(draw.Model.Index, content);
            int first = _count;
            _count = MeshMaskParts.AddShown(model.Parts, draw.Meshes, ref _draws, _count);
            if (_count == first)
            {
                continue;
            }

            var bones = draw.Pose is { } pose ? _skinning.Skin(pose, draw.PoseAhead, model, draw.Model.Index, content) : default;
            var partBounds = draw.Static ? BoundsOf(model.Parts) : default;
            Dress(_draws.AsSpan(first, _count - first), in draw, bones, partBounds, in context);
        }
    }

    /// <summary>
    /// Gives the parts of one draw that <see cref="MeshMaskParts.AddShown"/> copied what the draw says about them. A
    /// static draw's parts get bake ids, from <paramref name="partBounds"/>, which holds every part of the model; others
    /// keep zero. Throws for a static draw of a mesh made in code without <c>isStatic</c>, which has no lightmap UVs.
    /// </summary>
    internal static void Dress(
        Span<DrawItem> shown, in ModelDraw draw, BoneRange bones, ReadOnlySpan<BakeSphere> partBounds, in DressContext context)
    {
        var tint = draw.Color ?? (draw.Model.IsBuiltIn ? CubeColor : null);
        for (int i = 0; i < shown.Length; i++)
        {
            ref var item = ref shown[i];
            item.Transform = draw.Transform;
            item.Swim = draw.Swim;
            item.Tint = tint;
            item.Bones = bones;
            item.Ghost = draw.Ghost;
            item.Key = draw.Key;
            item.Fade = draw.Fade;
        }

        if (!draw.Static)
        {
            return;
        }

        var content = context.Content;
        if (!content.Store.Identities.MadeStatic(draw.Model))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Model {draw.Model.Index} is a mesh made in code and drawn static, but it wasn't made static, so it has no lightmap UVs. Pass isStatic: true to Content.Mesh."));
        }

        if (partBounds.Length > BakeIds.MaxParts)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Model {draw.Model.Index} ({UploadedItems.NameOf(draw.Model.Index, content, "a mesh made in code")}) is drawn static with {partBounds.Length} parts, but a static model can have at most {BakeIds.MaxParts}. Draw it without isStatic, or split it."));
        }

        var identity = new BakeIdDraw(content.Store.IdentityOf(draw.Model), tint, draw.Transform);
        BakeIdentity.Assign(shown, draw.Meshes, partBounds, in identity, context.Areas);
    }

    /// <summary>The sphere around each of a model's parts, in the model's units, kept in a buffer reused each draw.</summary>
    private ReadOnlySpan<BakeSphere> BoundsOf(DrawItem[] parts)
    {
        if (_partBounds.Length < parts.Length)
        {
            _partBounds = new BakeSphere[Math.Max(parts.Length, _partBounds.Length * 2)];
        }

        for (int p = 0; p < parts.Length; p++)
        {
            _partBounds[p] = renderer.Assets.BoundsOf(parts[p].Detail);
        }

        return _partBounds.AsSpan(0, parts.Length);
    }
}

/// <summary>What <see cref="DressedDraws.Dress"/> needs from the frame: the content, and the baked areas bake ids are relative to.</summary>
internal readonly ref struct DressContext(Content content, BakeIdAreas areas)
{
    public Content Content { get; } = content;

    public BakeIdAreas Areas { get; } = areas;
}

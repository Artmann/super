using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Rendition;

namespace Super.Cook.Models;

/// <summary>
/// Checks each used material's wear and dirt, and bakes the masks of those whose rule asks for them, laid out by the
/// second UV set. The rest of the model shades them, so a barrel's foot is dark where it meets the deck.
/// </summary>
internal sealed class ModelMasks
{
    /// <summary>How many of a model's materials' masks bake at once. Each bake runs its texels in parallel already.</summary>
    private const int MasksAtOnce = 4;

    private const string Step = "mask";

    /// <summary>
    /// How many masks bake at once in the process, whichever models they're for. A bake holds its own buffers at the mask
    /// size and traces against the rest of the model, so this, not the number of workers, sets how much memory bakes take.
    /// </summary>
    internal static readonly int BakesAtOnce = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    private static readonly SemaphoreSlim BakePermits = new(BakesAtOnce);

    private readonly ModelCook _cook;
    private readonly Vertex[] _vertices;
    private readonly ModelMeshes _meshes;

    // The corners and second UVs every mask is baked from, made when the first material asks for masks.
    private Vector3[]? _positions;
    private Vector2[]? _uvs;

    // The hash of _positions and _uvs, made when the first material looks in the step cache.
    private string? _shape;

    // Whether a material's rule was wrong, so the model isn't cooked.
    private bool _failed;

    private ModelMasks(ModelCook cook, Vertex[] vertices, ModelMeshes meshes)
    {
        _cook = cook;
        _vertices = vertices;
        _meshes = meshes;
    }

    /// <summary>
    /// Each material's masks, null for a material without them, laid out by the second UV set of
    /// <paramref name="vertices"/>. Null when something was wrong, which was reported. It times its own phases: taking
    /// masks from the step cache, baking the rest, and keeping them.
    /// </summary>
    public static ModelMaterialMasks?[]? Bake(ModelCook cook, Vertex[] vertices, ModelMeshes meshes, List<string> materialNames)
    {
        var masks = new ModelMasks(cook, vertices, meshes);
        var bakes = new List<MaskBake>();
        foreach (int material in meshes.Parts.Select(part => part.Material).Distinct().Order())
        {
            if (masks.Plan(material, materialNames[material]) is { } bake)
            {
                bakes.Add(bake);
            }
        }

        // Nothing is baked when a material was wrong, since the model isn't cooked then.
        if (!masks._failed)
        {
            masks.BakeMissing(bakes.Where(bake => bake.Rgba is null).ToList());
        }

        var result = masks.AddTextures(bakes, materialNames.Count);
        return masks._failed ? null : result;
    }

    /// <summary>A material's masks to bake, or null when it has none or its rule is wrong, which was reported.</summary>
    private MaskBake? Plan(int material, string name)
    {
        if (_cook.RuleFor(name) is not { } rule)
        {
            return null;
        }

        void Error(string message)
        {
            _cook.MaterialError(name, message);
            _failed = true;
        }

        if (!Wants(rule, name, Error)
            || MaterialExtras.Layer(rule.Wear, "wear", Error) is not { } wear
            || MaterialExtras.Layer(rule.Dirt, "dirt", Error) is not { } dirt)
        {
            return null;
        }

        _positions ??= _vertices.Select(vertex => vertex.Position).ToArray();
        _uvs ??= _vertices.Select(vertex => vertex.Uv2).ToArray();
        var (mesh, rest) = MaskMeshes(material);
        if (MaskBaker.Problem(mesh, MaskBaker.DefaultSize) is { } problem)
        {
            Error(problem + ".");
            return null;
        }

        // The meshes aren't kept: each holds most of the model's indices, so they're made again when it bakes.
        byte[]? rgba = Recall(mesh, rest, out string? key);
        return new MaskBake(material, name, key, wear, dirt) { Rgba = rgba };
    }

    /// <summary>Whether a rule asks for masks and can have them. What's wrong with it goes to <paramref name="error"/>.</summary>
    private bool Wants(MaterialRule rule, string name, Action<string> error)
    {
        if (!rule.BakeMasks)
        {
            if (rule.Wear is not null || rule.Dirt is not null)
            {
                error("wear and dirt need bakeMasks, since they're laid by the masks it bakes.");
            }

            return false;
        }

        if (rule.Wear is null && rule.Dirt is null)
        {
            error("bakeMasks bakes masks for wear and dirt, but the rule has neither.");
            return false;
        }

        if (rule.Transparent)
        {
            _cook.MaterialWarning(name, "transparent materials don't show wear and dirt, so bakeMasks is ignored.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// A material's masks from the step cache, or null when it has none. <paramref name="key"/> is their key there, or null
    /// when there's no step cache.
    /// </summary>
    private byte[]? Recall(MeshData mesh, MeshData rest, out string? key)
    {
        key = null;
        if (!_cook.Input.StepResults.HasCache)
        {
            return null;
        }

        using (_cook.Input.Time("recall masks"))
        {
            _shape ??= MaskKeys.Shape(_positions!, _uvs!);
            key = MaskKeys.Key(_shape, mesh, rest);
            return _cook.Input.StepResults.Recall(Step, key) is { Length: MaskBaker.DefaultSize * MaskBaker.DefaultSize * 4 } kept ? kept : null;
        }
    }

    /// <summary>
    /// Bakes the masks the step cache didn't have. The materials bake at once, since each bake has serial steps that leave
    /// cores idle.
    /// </summary>
    private void BakeMissing(List<MaskBake> toBake)
    {
        Parallel.For(0, toBake.Count, new ParallelOptions { MaxDegreeOfParallelism = MasksAtOnce }, i =>
        {
            using (_cook.Input.Time("bake masks"))
            {
                var (mesh, rest) = MaskMeshes(toBake[i].Material);
                BakePermits.Wait();
                try
                {
                    toBake[i].Rgba = MaskBaker.Bake(mesh, MaskBaker.DefaultSize, rest).Rgba;
                }
                finally
                {
                    BakePermits.Release();
                }

                toBake[i].Baked = true;
            }
        });
    }

    /// <summary>
    /// Keeps the masks baked now in the step cache, and adds every material's masks as a texture. They're added in
    /// material order, so the textures' order doesn't depend on which bake finished first.
    /// </summary>
    private ModelMaterialMasks?[] AddTextures(List<MaskBake> bakes, int materialCount)
    {
        var masks = new ModelMaterialMasks?[materialCount];
        foreach (var bake in bakes)
        {
            if (bake.Rgba is null)
            {
                continue;
            }

            if (bake.Baked && bake.Key is not null)
            {
                using (_cook.Input.Time("keep masks"))
                {
                    _cook.Input.StepResults.Keep(Step, bake.Key, bake.Rgba);
                }
            }

            int texture = _cook.Textures.AddPixels($"{bake.Name}#mask", MaskBaker.DefaultSize, MaskBaker.DefaultSize, TextureKind.Linear, bake.Rgba);
            masks[bake.Material] = new ModelMaterialMasks(texture, bake.Wear, bake.Dirt);
        }

        return masks;
    }

    /// <summary>
    /// The mesh a material's masks are baked from, its own parts, and the rest of the model it traces against. Each part is
    /// one of the model's meshes, and height is measured from each one's own foot.
    /// </summary>
    private (MeshData Mesh, MeshData Others) MaskMeshes(int material)
    {
        var (parts, indices) = (_meshes.Parts, _meshes.Indices);
        var own = parts.Where(part => part.Material == material).ToList();
        var mesh = new MeshData(
            _positions!, _uvs!, IndicesOf(own, indices),
            own.SelectMany((part, i) => Enumerable.Repeat(i, (int)part.IndexCount / 3)).ToArray());
        var rest = new MeshData(_positions!, _uvs!, IndicesOf(parts.Where(part => part.Material != material), indices));
        return (mesh, rest);
    }

    private static int[] IndicesOf(IEnumerable<ModelPart> parts, List<uint> indices) =>
        parts.SelectMany(part => Enumerable.Range((int)part.FirstIndex, (int)part.IndexCount).Select(i => (int)indices[i])).ToArray();

    /// <summary>One material's masks: what they're laid with, and their pixels once recalled or baked.</summary>
    private sealed record MaskBake(int Material, string Name, string? Key, MaterialLayer Wear, MaterialLayer Dirt)
    {
        public byte[]? Rgba { get; set; }

        public bool Baked { get; set; }
    }
}

/// <summary>The step cache's keys for masks.</summary>
internal static class MaskKeys
{
    /// <summary>
    /// Names the code that bakes masks: this library's code (<see cref="CodeId"/>), which holds <see cref="MaskBaker"/>,
    /// and the size it bakes at.
    /// </summary>
    private static readonly string Version = string.Join(',',
        CodeId.Of(typeof(MaskBaker).Assembly).ToString("N"),
        MaskBaker.DefaultSize.ToString(CultureInfo.InvariantCulture));

    /// <summary>The hash of the corners and second UVs every material's mask is baked from.</summary>
    public static string Shape(Vector3[] positions, Vector2[] uvs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(positions.Length));
        hash.AppendData(MemoryMarshal.AsBytes(positions.AsSpan()));
        hash.AppendData(BitConverter.GetBytes(uvs.Length));
        hash.AppendData(MemoryMarshal.AsBytes(uvs.AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>The key for one material's masks: everything <see cref="MaskBaker.Bake"/> reads.</summary>
    public static string Key(string shape, MeshData mesh, MeshData rest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (int[]? list in new[] { mesh.Indices, mesh.Parts, rest.Indices, rest.Parts })
        {
            hash.AppendData(BitConverter.GetBytes(list?.Length ?? -1));
            hash.AppendData(MemoryMarshal.AsBytes((list ?? []).AsSpan()));
        }

        return string.Join('|', Version, shape, Convert.ToHexString(hash.GetHashAndReset()));
    }
}

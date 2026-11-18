using System.Globalization;
using System.Numerics;
using Rendition;
using Silk.NET.Assimp;
using Assimp = Silk.NET.Assimp.Assimp;
using AssimpMaterial = Silk.NET.Assimp.Material;

namespace Super.Cook.Models;

/// <summary>
/// A material's occlusion texture before it's cooked: its file, whether the second UV set reads it, and the
/// material when a rule named it, so a missing file stops the cook rather than warns.
/// </summary>
internal sealed record WantedOcclusion(string File, bool OnUv2, string? NamedBy);

/// <summary>
/// What a material rule adds beside the material itself: a surface shader, toon shading, an occlusion texture, and wear
/// and dirt.
/// </summary>
internal static class MaterialExtras
{
    /// <returns>
    /// The surface shader a rule names, with its textures as their names in the cooked content, or null when it names
    /// none or something about it is wrong, which was reported.
    /// </returns>
    public static ModelMaterialShader? ImportShader(MaterialRule rule, string materialName, ModelCook cook)
    {
        bool wrong = false;
        void Error(string message)
        {
            cook.MaterialError(materialName, message);
            wrong = true;
        }

        if (rule.Shader is not { } name)
        {
            if (rule.ShaderParameters is not null || rule.ShaderTextures is not null)
            {
                Error("shaderParameters and shaderTextures are read by a shader, so they need one.");
            }

            return null;
        }

        // The game reads shaders/<name>.frag.spv, so a name must stay inside that folder.
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
        {
            Error($"shader \"{name}\" must be the file name of a shader in the shaders folder, such as \"puddles\", with no /, \\ or \"..\".");
        }

        if (rule.Transparent)
        {
            Error("transparent materials can't use a surface shader yet.");
        }

        var vectors = ShaderVectors(rule.ShaderParameters ?? [], Error);
        var names = ShaderTextureNames(rule.ShaderTextures ?? [], cook.Input, Error);
        return wrong ? null : new ModelMaterialShader(name, vectors, names);
    }

    /// <summary>
    /// The material rule's <c>toon</c>, with its ramp cooked as linear data, or null for none. Steps out of range, or a
    /// rim that isn't three numbers of 0 or more, stop the cook.
    /// </summary>
    public static ModelMaterialToon? ImportToon(string materialName, ModelCook cook)
    {
        if (cook.RuleFor(materialName)?.Toon is not { } rule)
        {
            return null;
        }

        if (rule.Rim is { } rim && rim.Length != 3)
        {
            cook.MaterialError(materialName, $"toon rim must be three numbers, red, green and blue, not {rim.Length}.");
            return null;
        }

        var shading = new ToonShading { Steps = rule.Steps, Rim = rule.Rim is { } r ? new Vector3(r[0], r[1], r[2]) : Vector3.Zero };
        if (shading.Problem() is { } problem)
        {
            cook.MaterialError(materialName, problem);
            return null;
        }

        int ramp = cook.Textures.Add(rule.Ramp, TextureKind.Linear, namedBy: materialName);
        return new ModelMaterialToon(shading.Steps, ramp, shading.Rim);
    }

    /// <summary>
    /// The occlusion texture a material wants: the rule's own, or else the one a glTF file links, which Assimp files as
    /// its lightmap, unless the rule says <c>noOcclusionMap</c>. Null for none, or when the rule's UV set is wrong or
    /// the file's is past the two the cook keeps, which was reported.
    /// </summary>
    public static unsafe WantedOcclusion? OcclusionWanted(Assimp assimp, AssimpMaterial* source, string materialName, ModelCook cook)
    {
        var rule = cook.RuleFor(materialName) ?? new MaterialRule();
        if (rule.OcclusionUv is { } asked && asked is not (0 or 1))
        {
            cook.MaterialError(materialName, $"occlusionUv must be 0 or 1, not {asked}.");
            return null;
        }

        uint linkedUv = 0;
        string? linked = cook.IsGltf && !rule.NoOcclusionMap
            ? AssimpMaterials.GetTexture(assimp, source, TextureType.Lightmap, out linkedUv)
            : null;
        if ((rule.Occlusion ?? linked) is not { } file)
        {
            return null;
        }

        uint uv = rule.OcclusionUv is { } set ? (uint)set : rule.Occlusion is null ? linkedUv : 0u;
        if (uv > 1)
        {
            cook.MaterialWarning(materialName,
                $"its occlusion texture is on TEXCOORD_{uv}, and the cook keeps only the first two UV sets, so it's left out.");
            return null;
        }

        return new WantedOcclusion(file, uv == 1, rule.Occlusion is null ? null : materialName);
    }

    /// <summary>
    /// Cooks each material's occlusion texture once the second UV set is settled, and returns each one's index, -1 for
    /// none, or nothing when no material has one, as <see cref="ModelData.MaterialOcclusion"/> wants. A texture on that
    /// set was painted for the file's own layout, so where the cook <paramref name="made"/> a new set for lighting it
    /// would land in the wrong places, and it's left out with a warning.
    /// </summary>
    public static int[] AddOcclusion(
        ModelCook cook, List<ModelMaterial> materials, List<string> materialNames, List<WantedOcclusion?> wanted, HashSet<int> made)
    {
        var cooked = new int[materials.Count];
        Array.Fill(cooked, -1);
        for (int i = 0; i < materials.Count; i++)
        {
            if (wanted[i] is not { } occlusion)
            {
                continue;
            }

            if (occlusion.OnUv2 && made.Contains(i))
            {
                cook.MaterialWarning(materialNames[i],
                    "its occlusion texture is on the second UV set, which the cook made again for lighting, so it's left out. Give the model a second UV set that fits a lightmap, or put the occlusion on the first.");
                continue;
            }

            int texture = cook.Textures.Add(occlusion.File, TextureKind.Linear, namedBy: occlusion.NamedBy);
            if (texture >= 0 && occlusion.OnUv2)
            {
                var material = materials[i];
                material.Flags |= MaterialFlags.OcclusionOnUv2;
                materials[i] = material;
            }

            cooked[i] = texture;
        }

        return cooked.Any(texture => texture >= 0) ? cooked : [];
    }

    /// <summary>
    /// A rule's wear or dirt as the renderer takes it: none is an amount of 0, and no roughness keeps the surface's. Null
    /// when it's wrong, which went to <paramref name="error"/>.
    /// </summary>
    public static MaterialLayer? Layer(LayerRule? rule, string name, Action<string> error)
    {
        if (rule is null)
        {
            return new MaterialLayer(Vector3.Zero, -1f, 0f);
        }

        if (LayerProblem(rule, name) is { } problem)
        {
            error(problem);
            return null;
        }

        // The rule's colour is sRGB, as a colour picker shows it; the shader mixes in linear.
        static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        float[] color = rule.Color!;
        return new MaterialLayer(new Vector3(Linear(color[0]), Linear(color[1]), Linear(color[2])), rule.Roughness ?? -1f, rule.Amount);
    }

    private static string? LayerProblem(LayerRule rule, string name)
    {
        static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);
        static bool OutOfRange(float value) => value < 0f || value > 1f;
        if (rule.Color is not { Length: 3 } color)
        {
            return $"{name} color needs three numbers, red, green and blue.";
        }

        foreach (float c in color)
        {
            if (OutOfRange(c))
            {
                return $"{name} color is a colour as a picker shows it, each number from 0 to 1, not {Number(c)}.";
            }
        }

        if (OutOfRange(rule.Amount))
        {
            return $"{name} amount must be from 0 to 1, not {Number(rule.Amount)}.";
        }

        if (rule.Roughness is { } roughness && OutOfRange(roughness))
        {
            return $"{name} roughness must be from 0 to 1, not {Number(roughness)}.";
        }

        return null;
    }

    /// <summary>A rule's <c>shaderParameters</c> as the shader reads them. What's wrong with them goes to <paramref name="error"/>.</summary>
    private static Vector4[] ShaderVectors(float[][] parameters, Action<string> error)
    {
        if (parameters.Length > ShaderParameters.Length)
        {
            error($"shaderParameters has {parameters.Length} entries, but a surface shader takes at most {ShaderParameters.Length}.");
        }

        var vectors = new Vector4[Math.Min(parameters.Length, ShaderParameters.Length)];
        for (int i = 0; i < vectors.Length; i++)
        {
            float[] numbers = parameters[i] ?? [];
            if (numbers.Length > 4)
            {
                error($"shaderParameters[{i}] has {numbers.Length} numbers, but a parameter holds at most 4.");
                continue;
            }

            float At(int component) => component < numbers.Length ? numbers[component] : 0f;
            vectors[i] = new Vector4(At(0), At(1), At(2), At(3));
        }

        return vectors;
    }

    /// <summary>
    /// A rule's <c>shaderTextures</c> as their names in the cooked content. What's wrong with them goes to
    /// <paramref name="error"/>.
    /// </summary>
    private static string[] ShaderTextureNames(string[] paths, CookInput input, Action<string> error)
    {
        if (paths.Length > ShaderTextures.Length)
        {
            error($"shaderTextures has {paths.Length} entries, but a surface shader takes at most {ShaderTextures.Length}.");
        }

        var names = new string[Math.Min(paths.Length, ShaderTextures.Length)];
        for (int i = 0; i < names.Length; i++)
        {
            if (paths[i] is not { } path || input.NameOf(path) is not { } found)
            {
                error($"shaderTextures[{i}] {paths[i]} isn't in the source root.");
                continue;
            }

            // The game loads the texture by its name, so it must be cooked too, which Only can leave out.
            if (!input.IsPicked(found))
            {
                error($"shaderTextures[{i}] {found} isn't cooked, because the source root's Only leaves it out. Pick it in Only too, such as \"{found}\".");
                continue;
            }

            names[i] = found;
        }

        return names;
    }
}

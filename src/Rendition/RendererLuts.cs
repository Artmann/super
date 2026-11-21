using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The artists' colour tables, for <see cref="ColorGrade.Lut"/>. Reached through <see cref="Renderer.Luts"/>. See "A
/// colour table" in docs/rendering.md.
/// </summary>
public sealed class RendererLuts
{
    private readonly GradeLut _grade;
    private readonly RetiredResources _retired;

    internal RendererLuts(GradeLut grade, RetiredResources retired)
    {
        _grade = grade;
        _retired = retired;
    }

    /// <summary>
    /// Uploads an artist's colour table, cooked from a .cube file or a PNG strip. It waits for the upload and stays until
    /// <see cref="Destroy"/> or until the renderer is disposed.
    /// </summary>
    public ColorLut Create(ColorLutData data) => _grade.Luts.Create(data);

    /// <summary>
    /// Lets go of a table <see cref="Create"/> made. Its image is freed once no frame in flight can read it, the next
    /// table made takes its slot, and a picture whose <see cref="ColorGrade.Lut"/> still names it throws. Throws when
    /// the table isn't live.
    /// </summary>
    public void Destroy(ColorLut lut) => _grade.DestroyLut(lut, _retired.AfterLastSubmitted);
}

namespace Rendition.Gpu;

/// <summary>
/// The image formats the renderer uses, named the way WebGPU names them. Vertex attribute
/// formats are a separate thing in WebGPU and are not here.
/// </summary>
internal enum GpuFormat
{
    None,
    R8Unorm,
    R32Float,
    Rg16Float,
    Rgba8Unorm,
    Rgba8UnormSrgb,
    Bgra8UnormSrgb,
    Rg11b10Ufloat,
    Rgba16Float,
    Rgba32Float,
    Depth32Float,
    Bc4RUnorm,
    Bc5RgUnorm,
    Bc6hRgbUfloat,
    Bc7RgbaUnorm,
    Bc7RgbaUnormSrgb,
}

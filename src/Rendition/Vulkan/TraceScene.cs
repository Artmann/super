namespace Rendition.Vulkan;

/// <summary>
/// One part of a mesh as a ray sees it: the run of indices a draw item covers, and the material that decides whether
/// rays can pass through it.
/// </summary>
public readonly record struct TracePart(uint FirstIndex, uint IndexCount, MaterialHandle Material);

namespace Rendition.Gpu;

// GPU objects are numbers, not classes, so the browser backend can hand them to JavaScript
// as they are. A Value of 0 means none.

internal readonly record struct BufferId(int Value);

internal readonly record struct ViewId(int Value);

internal readonly record struct PipelineId(int Value);

internal readonly record struct BindGroupId(int Value);

internal readonly record struct SamplerId(int Value);

internal readonly record struct ShaderId(int Value);

internal readonly record struct QuerySetId(int Value);

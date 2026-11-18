namespace Super;

/// <summary>The size in pixels of a frame to draw, and its time in seconds.</summary>
internal readonly record struct FrameRequest(int Width, int Height, float Time);

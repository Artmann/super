namespace Super.Physics;

/// <summary>The collision layers, kept apart from the physics world so the components can name them without it.</summary>
internal static class LayerBits
{
    /// <summary>Every layer: bits 0 to 15.</summary>
    public const uint All = 0xFFFF;
}

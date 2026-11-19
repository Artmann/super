using System.Numerics;

namespace Super;

/// <summary>Where things are drawn this frame, between the last two ticks.</summary>
public static class ViewPlacement
{
    /// <summary>
    /// Blends between a thing's transform on the last two ticks, which keeps motion
    /// smooth at any frame rate. It suits small steps, not whole turns in one tick.
    /// </summary>
    public static Matrix4x4 Smooth(this View view, in Matrix4x4 previous, in Matrix4x4 current) =>
        Matrix4x4.Lerp(previous, current, view.Blend);

    /// <summary>
    /// Where an entity is drawn this frame: its matrix in the world, between its last two ticks and following its
    /// parents, as its model is drawn. Use it to put frame-only things, such as a hand-drawn glow, on an entity.
    /// </summary>
    public static Matrix4x4 TransformOf(this View view, Entity entity)
    {
        var world = view.World ?? throw new InvalidOperationException("This view draws no world.");
        if (!world.IsAlive(entity) || !world.Has<Transform>(entity))
        {
            throw new InvalidOperationException($"Entity {entity.Id} has no Transform.");
        }

        return WorldFrame.WorldMatrix(world, entity, view.Blend, view.Clock.PoseAhead);
    }
}

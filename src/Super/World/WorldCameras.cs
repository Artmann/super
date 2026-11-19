using System.Numerics;

namespace Super;

/// <summary>The camera at the last tick, which drawing blends from, like <see cref="LastTick"/> for a transform.</summary>
internal struct LastCamera
{
    internal Camera Previous;
    internal bool HasPrevious;
}

/// <summary>The camera the world's frame is taken from: the newest entity with a <see cref="Camera"/>.</summary>
public static class WorldCameras
{
    /// <summary>
    /// The newest camera as it stands on this tick, placed in the world by its entity's <see cref="Transform"/> and
    /// <see cref="Parent"/>, with no blend, so Update can pick with the camera the player sees. Null when no entity has
    /// one.
    /// </summary>
    public static Camera? ActiveCamera(this World world) =>
        Newest.Of<Camera>(world) is { } entity ? Placed(world, entity, blend: 1f) : null;

    /// <summary>The newest camera blended between its last two ticks, or null when there's none.</summary>
    internal static Camera? FrameCamera(World world, float blend) =>
        Newest.Of<Camera>(world) is { } entity ? Placed(world, entity, blend) : null;

    /// <summary>Copies every camera into its <see cref="LastCamera"/>, before each Update.</summary>
    internal static void Remember(World world)
    {
        var archetypes = world.Archetypes;
        int cameraType = archetypes.TypeIdOrMissing<Camera>();
        int lastType = archetypes.TypeIdOrMissing<LastCamera>();
        if (cameraType < 0 || lastType < 0)
        {
            return;
        }

        int index = -1;
        while (archetypes.NextMatching(ref index, default(ComponentMask).With(cameraType).With(lastType)) is { } archetype)
        {
            var cameras = archetype.Column<Camera>(cameraType).Values;
            var lasts = archetype.Column<LastCamera>(lastType).Values;
            for (int i = 0; i < archetype.Count; i++)
            {
                lasts[i].Previous = cameras[i];
                lasts[i].HasPrevious = true;
            }
        }
    }

    // A blend of 1 is the camera as it is now, which is all a camera with no last tick has.
    private static Camera Placed(World world, Entity entity, float blend)
    {
        var camera = world.Get<Camera>(entity);
        if (blend < 1f && world.Has<LastCamera>(entity) && world.Get<LastCamera>(entity) is { HasPrevious: true } last)
        {
            camera = Camera.Lerp(in last.Previous, in camera, blend);
        }

        Entity? frame = world.Has<Transform>(entity) ? entity : world.Has<Parent>(entity) ? world.Get<Parent>(entity).Value : null;
        if (frame is not { } placedBy)
        {
            return camera;
        }

        var matrix = WorldFrame.WorldMatrix(world, placedBy, blend);
        return camera with
        {
            Position = Vector3.Transform(camera.Position, matrix),
            Target = Vector3.Transform(camera.Target, matrix),
        };
    }
}

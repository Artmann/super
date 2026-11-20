using System.Numerics;

namespace Super;

/// <summary>
/// Makes an entity the camera a <see cref="CameraRig"/> shows, so the game only steps the rig:
/// <c>scene.Spawn(new OnRig(boom))</c>. After each tick the entity's <see cref="Camera"/> becomes the rig's
/// <see cref="CameraRig.Current"/>, blended between ticks like any camera entity, and the tick after a
/// <see cref="CameraRig.Jump"/> it cuts instead of swinging. Until the rig's first update the camera is a plain
/// one looking at the middle of the world, so update the rig once before, or in the tick, the entity is spawned.
/// </summary>
/// <param name="Rig">The rig to show. Keep it in your game and call its Update once a tick.</param>
public readonly record struct OnRig(CameraRig Rig);

/// <summary>Copies every <see cref="OnRig"/>'s rig to its entity's camera.</summary>
internal static class WorldRigs
{
    /// <summary>What an entity on a rig shows before the rig's first update.</summary>
    public static readonly Camera Unplaced = new(new Vector3(0f, 5f, 8f), Vector3.Zero);

    /// <summary>
    /// Writes each placed rig's camera to its entity, cutting when the rig was just put in place. The game loop calls
    /// it at the end of each tick, after a scene change, so a camera built with a scene shows on its first frame.
    /// </summary>
    public static void Show(World world)
    {
        if (world.Archetypes.TypeIdOrMissing<OnRig>() < 0)
        {
            return;
        }

        foreach (var row in world.Query<OnRig, Camera, LastCamera>())
        {
            var rig = row.First.Rig;
            ArgumentNullException.ThrowIfNull(rig);
            if (!rig.Placed)
            {
                continue;
            }

            row.Second = rig.Current;
            if (rig.Cut)
            {
                row.Third.Previous = rig.Current;
                row.Third.HasPrevious = true;
            }
        }
    }
}

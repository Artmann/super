using System.Numerics;
using Rendition;

namespace Super;

/// <summary>When a reflection probe placed with <see cref="ViewIndirect.ReflectionProbe"/> is photographed again.</summary>
public enum ProbeUpdate
{
    /// <summary>
    /// When the light on it changes: the suns, the sky, a bake, the light probes, or a point or spot light that reaches
    /// its box appearing, going, moving or changing its range. A light's colour isn't watched, so a flicker costs nothing.
    /// </summary>
    OnChange,

    /// <summary>Only when the game changes the version it places the probe with.</summary>
    OnRequest,

    /// <summary>Again and again, taking turns with the other probes, for things that move or change all the time.</summary>
    Always,
}

/// <summary>
/// A reflection probe placed with <see cref="ViewIndirect.ReflectionProbe"/>: where it photographs the scene
/// from, the box its reflections are bent to land on, how far towards the box's sides it fades out,
/// which probe blends first where boxes overlap, and when it's photographed again.
/// </summary>
public readonly record struct ReflectionProbeRequest(
    int Key, Vector3 Position, Vector3 Min, Vector3 Max, float Blend, float Priority = 0f, ProbeUpdate Update = ProbeUpdate.OnChange,
    int Version = 0)
{
    /// <summary>
    /// Throws for a blend that isn't 0 or more metres, a priority that isn't a finite number, or an update that isn't one
    /// of the three, each naming its argument as <see cref="ViewIndirect.ReflectionProbe"/> does.
    /// </summary>
    internal static void CheckSettings(float blend, float priority, ProbeUpdate update)
    {
        if (!(float.IsFinite(blend) && blend >= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(blend), blend, "The reflection probe's blend must be 0 or more metres.");
        }

        if (!float.IsFinite(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "The reflection probe's priority must be a finite number.");
        }

        if (!Enum.IsDefined(update))
        {
            throw new ArgumentOutOfRangeException(nameof(update), update, "The reflection probe's update must be OnChange, OnRequest or Always.");
        }
    }

    /// <summary>The probe as the renderer takes it.</summary>
    internal ReflectionProbe ToProbe() => new()
    {
        Position = Position,
        BoxMin = Min,
        BoxMax = Max,
        BlendDistance = Blend,
        Priority = Priority,
    };
}

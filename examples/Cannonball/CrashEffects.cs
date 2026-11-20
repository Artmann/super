using System.Numerics;
using Rendition;
using Super;
using Super.Physics;

namespace Cannonball;

/// <summary>
/// The screen's side of a shot: a blur that grows with the stuntman's speed while he flies, and on the hit that knocks
/// him limp a white flash, a red edge and a burst of colour fringes, all easing away as he tumbles.
/// </summary>
public sealed class CrashEffects(World world, Physics physics)
{
    // The blur starts at a jog and is strongest near the top speed, kept light enough to follow the flight through.
    private const float BlurFromSpeed = 6f;
    private const float BlurPerSpeed = 1f / 40f;
    private const float MostBlur = 0.4f;

    // A hit this fast, in metres per second, gives the brightest flash and the reddest edge.
    private const float HardestHit = 14f;

    // How fast each part of a hit fades, in strength a second.
    private const float FlashFade = 4f;
    private const float EdgeFade = 0.6f;
    private const float FringeFade = 1.5f;

    private static readonly Vector3 Hurt = new(0.7f, 0.02f, 0f);

    private Entity _screen;
    private Stage _last;

    /// <summary>Puts the effects on the screen, all off.</summary>
    public void Load() => _screen = world.Spawn(new ScreenEffects());

    /// <summary>Moves the effects on by a tick, from the stage of the shot and how hard the hit was.</summary>
    public void Update(StuntFlight flight, float seconds)
    {
        ref var screen = ref world.Get<ScreenEffects>(_screen);
        if (flight.Stage == Stage.Tumbling && _last == Stage.Flying && flight.HitSpeed > 0f)
        {
            float hard = Math.Clamp(flight.HitSpeed / HardestHit, 0.3f, 1f);
            screen.Flash = 0.6f * hard;
            screen.EdgeTint = new EdgeTint(Hurt, 0.6f * hard);
            screen.ChromaticAberration = hard;
        }

        _last = flight.Stage;
        float speed = flight.Stage == Stage.Flying ? physics.Bodies.Velocity(flight.Stuntman).Length() : 0f;
        screen.SpeedBlur = Math.Clamp((speed - BlurFromSpeed) * BlurPerSpeed, 0f, MostBlur);
        screen.Flash = MathF.Max(0f, screen.Flash - (FlashFade * seconds));
        screen.EdgeTint = screen.EdgeTint with { Strength = MathF.Max(0f, screen.EdgeTint.Strength - (EdgeFade * seconds)) };
        screen.ChromaticAberration = MathF.Max(0f, screen.ChromaticAberration - (FringeFade * seconds));
    }
}

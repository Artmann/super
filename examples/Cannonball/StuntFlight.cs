using System.Globalization;
using System.Numerics;
using Super;
using Super.Ecs;
using Super.Physics;

namespace Cannonball;

/// <summary>
/// One shot, from sitting in the cannon to standing up again: fires the stuntman, watches for the hit that knocks him
/// limp, waits for him to come to rest, and gets him up.
/// </summary>
public sealed class StuntFlight(World world, Physics physics, RandomStream random)
{
    /// <summary>The fastest the stuntman leaves the muzzle, in metres per second.</summary>
    public const float TopSpeed = 22.5f;

    /// <summary>The slowest shot, in metres per second, so each lands a little differently.</summary>
    public const float LowestSpeed = TopSpeed * 0.8f;

    /// <summary>A hit at least this fast, in metres per second, knocks the fight out of him.</summary>
    public const float KnockoutSpeed = 4f;

    private const float LoadedSeconds = 1.5f;
    private const float StillSpeed = 0.4f;
    private const float StillSeconds = 0.75f;
    private const float LongestTumble = 8f;
    private const float StandingSeconds = 1f;
    private const float LongestFlight = 6f;

    private float _seconds;
    private float _still;

    public Stage Stage { get; private set; }

    /// <summary>The stuntman: a rigged model with a ragdoll.</summary>
    public Entity Stuntman { get; private set; }

    /// <summary>How hard the hit was that knocked him limp, in metres per second, or 0 before one.</summary>
    public float HitSpeed { get; private set; }

    private Animator Animator => world.Get<Animated>(Stuntman).Value;

    /// <summary>Starts a shot with a stuntman just sat in the cannon.</summary>
    public void Load(Entity stuntman)
    {
        Stuntman = stuntman;
        Stage = Stage.Loaded;
        HitSpeed = 0f;
        _seconds = 0f;
    }

    /// <summary>Moves the shot on by a tick, saying whether it's over and the next can be loaded.</summary>
    public bool Update(float seconds)
    {
        _seconds += seconds;
        switch (Stage)
        {
            case Stage.Loaded when _seconds >= LoadedSeconds:
                Fire();
                break;
            case Stage.Flying:
                WatchForHits();
                break;
            case Stage.Tumbling:
                WatchForRest(seconds);
                break;
            case Stage.GettingUp:
                WatchForGetUp();
                break;
            case Stage.Cheering:
                // The moment of standing counts from the end of the cheer.
                if (Animator.Moves.IsPlaying(Move.Cheer))
                {
                    _seconds = 0f;
                }

                return _seconds >= StandingSeconds;
        }

        return false;
    }

    /// <summary>What's happening, for the screen.</summary>
    public string Caption() => Stage switch
    {
        Stage.Loaded => "Ready...",
        Stage.Flying => string.Create(CultureInfo.InvariantCulture, $"Flying at {physics.Bodies.Velocity(Stuntman).Length():0} m/s"),
        Stage.Tumbling => string.Create(CultureInfo.InvariantCulture, $"Hit at {HitSpeed:0.0} m/s"),
        _ => string.Create(CultureInfo.InvariantCulture, $"Hit at {HitSpeed:0.0} m/s, and back on his feet"),
    };

    private void Fire()
    {
        // A little muscle keeps the flail in the arms and legs, but the flight is his own.
        world.Get<Ragdoll>(Stuntman) = world.Get<Ragdoll>(Stuntman) with { Mode = RagdollMode.Simulated, Strength = 0.3f };
        Animator.Set(Stance.Flail);
        physics.Bodies.SetVelocity(Stuntman, CannonballField.Aim * random.Between(LowestSpeed, TopSpeed));
        physics.Bodies.SetAngularVelocity(Stuntman, new Vector3(1.5f, 0f, 0.4f));
        Stage = Stage.Flying;
        _seconds = 0f;
    }

    private void WatchForHits()
    {
        foreach (var contact in physics.Contacts)
        {
            if ((contact.Entity == Stuntman || contact.Other == Stuntman) && contact.Speed >= KnockoutSpeed)
            {
                HitSpeed = contact.Speed;
                GoLimp();
                return;
            }
        }

        if (_seconds >= LongestFlight)
        {
            GoLimp();
        }
    }

    private void GoLimp()
    {
        world.Get<Ragdoll>(Stuntman) = world.Get<Ragdoll>(Stuntman) with { Strength = 0f };
        Stage = Stage.Tumbling;
        _seconds = 0f;
        _still = 0f;
    }

    private void WatchForRest(float seconds)
    {
        _still = physics.Bodies.Velocity(Stuntman).Length() < StillSpeed ? _still + seconds : 0f;
        if (_still < StillSeconds && _seconds < LongestTumble)
        {
            return;
        }

        bool faceUp = physics.Ragdolls.IsFaceUp(Stuntman);
        world.Get<Ragdoll>(Stuntman) = world.Get<Ragdoll>(Stuntman) with { Mode = RagdollMode.Off };
        Animator.Set(Stance.Idle);
        Animator.Moves.Play(faceUp ? Move.StandUp : Move.StandUpFromFront);
        Stage = Stage.GettingUp;
        _seconds = 0f;
    }

    /// <summary>Waits out the get-up, then has him rally the crowd.</summary>
    private void WatchForGetUp()
    {
        if (Animator.Moves.IsPlaying(Move.StandUp) || Animator.Moves.IsPlaying(Move.StandUpFromFront))
        {
            return;
        }

        Animator.Moves.Play(Move.Cheer);
        Stage = Stage.Cheering;
        _seconds = 0f;
    }
}

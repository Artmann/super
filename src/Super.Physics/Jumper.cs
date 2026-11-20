using System.Numerics;

namespace Super.Physics;

/// <summary>
/// Gives a <see cref="Character"/> what a platform game's hero needs, for <see cref="PhysicsCharacters.Walk"/>: it
/// falls, jumps, hops lower when the button is let go early, can still jump just after walking off an edge, and
/// rides what it stands on. With the defaults a full jump rises about 1.9 metres and a hop about half a metre.
/// The character also climbs its <see cref="Character.StepHeight"/>, so it reaches ledges that much higher.
/// </summary>
/// <param name="JumpSpeed">How fast a jump leaves the ground, in metres per second.</param>
/// <param name="Gravity">How fast it speeds up falling, in metres per second each second.</param>
/// <param name="Forgiveness">A jump still works this many seconds after walking off an edge.</param>
/// <param name="ShortHop">How much of its rise is kept when the button is let go on the way up, from 0 to 1.</param>
public record struct Jumper(float JumpSpeed = 9.5f, float Gravity = 24f, float Forgiveness = 0.12f, float ShortHop = 0.5f)
{
    /// <summary>How fast it's rising, in metres per second, and falling when below zero. Set it for a bounce pad.</summary>
    public float Rise;

    /// <summary>Seconds since it last stood on something, and 0 on the ground.</summary>
    public float AirTime;

    // How fast its ground moved at the last walk, which the next one adds to ride along.
    private Vector3 _carried;

    // The jump button at the last walk, to tell a press and a letting go. None before the first walk, so a button
    // already down then, such as the one that started the game, isn't a jump.
    private bool? _held;

    // It left the ground by jumping, so it can't jump again until it lands.
    private bool _jumped;

    /// <summary>A jumper with the defaults; without this, <c>new Jumper()</c> would be all zeros.</summary>
    public Jumper()
        : this(9.5f, 24f, 0.12f, 0.5f)
    {
    }

    /// <summary>The velocity to move at this tick: the walk, the fall or jump, and the ground's own speed.</summary>
    internal Vector3 Start(Vector3 walk, bool jump, float seconds)
    {
        bool pressed = jump && _held == false;
        bool released = !jump && _held == true;
        _held = jump;
        Rise -= Gravity * seconds;
        if (pressed && !_jumped && AirTime <= Forgiveness)
        {
            Rise = JumpSpeed;
            _jumped = true;
        }
        else if (released && Rise > 0f)
        {
            Rise *= ShortHop;
        }

        return new Vector3(walk.X, Rise, walk.Z) + _carried;
    }

    /// <summary>Takes in where the move left it: what it really rose at, and whether it stands.</summary>
    internal void Finish(in CharacterMove move, float seconds)
    {
        Rise = move.Grounded ? 0f : move.Velocity.Y - _carried.Y;
        AirTime = move.Grounded ? 0f : AirTime + seconds;
        _jumped &= !move.Grounded;
        _carried = move.GroundVelocity;
    }
}

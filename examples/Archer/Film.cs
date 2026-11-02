using System.Numerics;
using Super;

namespace Archer;

/// <summary>
/// The storyboard of "The Clearing", as a cutscene: every shot and every moment, in
/// seconds from the start. Moments count real seconds, so they keep their place while
/// the world runs slow.
/// </summary>
public static class Film
{
    /// <summary>How high Erika stands on the boulder.</summary>
    public const float RockHeight = 1.23f;

    /// <summary>Where her dive off the boulder ends, more or less.</summary>
    public static readonly Vector3 DiveEnd = new(-3.6f, 0f, 11.4f);

    private static readonly Vector3 Chest = new(0f, 1.35f, 0f);

    // High and wide, behind her right shoulder, with the dummies and their arrows beyond her.
    private static readonly Spot EndSpot = new(2.6f, 6f, 2.4f);
    private static readonly Vector3 EndLook = new(-1.5f, 1.2f, 5f);
    private const float EndFieldOfView = 0.85f;

    /// <summary>Where the camera rests once the film is over, around wherever Erika ended up.</summary>
    public static Camera End(Vector3 erika) => At(erika, EndSpot, EndLook, EndFieldOfView);

    public static Cutscene Make(ArcherGame game)
    {
        var erika = () => game.Erika;
        var dummies = game.Dummies;
        var rockTop = Clearing.Rock + new Vector3(0f, RockHeight, 0f);

        return new Cutscene()
            // 1. Golden hour. The camera swoops down and round to her face as she looks about.
            .Then(Shot.Custom(2f, time => Swing(erika().Position, new Spot(1.1f, 10f, 2.6f), new Spot(-0.7f, 2.4f, 1.5f), time.Amount, Chest, 0.75f, 0.6f), Easing.InOut))
            .At(0.1f, () => erika().Animator.Play(Move.Look))

            // 2. Over her shoulder, pushing in, as she draws on the first dummy.
            .Then(Shot.Move(1.5f,
                new Camera(new Vector3(-0.8f, 1.65f, -1.7f), dummies[0].Target, 0.6f),
                new Camera(new Vector3(-0.55f, 1.6f, -0.95f), dummies[0].Target, 0.42f)))
            .At(2.1f, () => Draw(erika()))

            // 3. She lets go, the world slows, and the camera rides the arrow in.
            .At(3.35f, () => Shoot(game, dummies[0]))
            .Slow(3.2f, 4.75f, 0.3f, ramp: 0.25f)
            .Then(Shot.Custom(1.5f, _ => FollowArrow(game)))

            // 4. A dodge to her right, then a sprint for the boulder, tracked from the side.
            .At(5f, () =>
            {
                erika().Animator.Play(Move.Dodge);
                erika().Animator.Set(Pose.Idle);
            })
            .At(5.7f, () =>
            {
                erika().Face(Clearing.Rock);
                erika().Animator.Set(Pose.Run);
            })
            .Then(Shot.Track(1.8f, () => erika().Position, new Vector3(3f, 1.4f, 2.8f), new Vector3(3.6f, 1.1f, 0.8f), new Vector3(0f, 1f, 0f), 0.75f))

            // 5. Up onto the boulder, from low down.
            .At(6.8f, () =>
            {
                var her = erika();
                her.Animator.Play(Move.Jump);
                her.Animator.Set(Pose.Fall);
                her.LeapTo(rockTop, apex: 0.8f, seconds: 0.6f, landed: () =>
                {
                    her.Animator.Play(Move.Land);
                    her.Animator.Set(Pose.Idle);
                    her.Face(dummies[1].Position);
                });
            })
            .Then(Shot.Hold(1f, new Camera(Clearing.Rock + new Vector3(2.6f, 0.25f, 2.8f), Clearing.Rock + new Vector3(0.3f, 1.7f, -0.6f), 0.85f)))

            // 6. On the rock, in slow motion, the camera circles her as she shoots the second.
            .At(7.7f, () => Draw(erika()))
            .Slow(8.4f, 10.2f, 0.25f, ramp: 0.3f)
            .At(8.9f, () => Shoot(game, dummies[1]))
            .Then(Shot.Orbit(2.3f, () => erika().Position, radius: 3.4f, height: 0.3f,
                fromAngle: Toward(rockTop, dummies[1].Position) + 2.5f,
                toAngle: Toward(rockTop, dummies[1].Position) + 0.9f,
                look: new Vector3(0f, 1.4f, 0f), fieldOfView: 0.6f))
            .Then(Shot.Hold(0.7f, Beside(dummies[1], rockTop, 2.2f, 1.3f)))

            // 7. A dive off the rock into a roll, seen wide.
            .At(10.7f, () =>
            {
                erika().Animator.Set(Pose.Idle);
                erika().Face(DiveEnd);
            })
            .At(10.85f, () =>
            {
                erika().Animator.Play(Move.Dive);
                erika().DropTo(0f, 0.5f);
            })
            .At(12.1f, () =>
            {
                erika().Face(dummies[2].Position);
                Draw(erika());
            })
            .Then(Shot.Track(2.2f, () => erika().Position, new Vector3(4.5f, 2.4f, -1.5f), new Vector3(4.2f, 1.4f, 1.8f), new Vector3(0f, 0.8f, 0f), 0.7f))

            // 8. Two quick shots at the third dummy, cut fast.
            .At(13.1f, () => Shoot(game, dummies[2]))
            .At(13.8f, () => Shoot(game, dummies[2]))
            .Then(Shot.Custom(0.3f, _ => OverShoulder(erika(), dummies[2])))
            .Then(Shot.Custom(0.45f, _ => Beside(dummies[2], erika().Position, 1.8f, 1.25f)))
            .Then(Shot.Custom(0.25f, _ => InFront(erika(), dummies[2])))
            .Then(Shot.Custom(0.6f, _ => Beside(dummies[2], erika().Position, -1.6f, 1f)))

            // 9. Pleased with herself. The camera pulls back and up over the clearing.
            .At(14.7f, () =>
            {
                erika().Animator.Play(Move.Cocky);
                erika().Animator.Set(Pose.Idle);
            })
            .Then(Shot.Custom(2.8f, time =>
            {
                var at = erika().Position;
                var near = InFront(erika(), dummies[2]).Position - at;
                var start = new Spot(MathF.Atan2(near.X, near.Z), new Vector2(near.X, near.Z).Length(), near.Y);
                // Round her the short way rather than through her, rising as it pulls away.
                float finish = start.Angle + MathF.IEEERemainder(EndSpot.Angle - start.Angle, MathF.Tau);

                var look = Vector3.Lerp(Chest, EndLook, time.Amount);
                return Swing(at, start, EndSpot with { Angle = finish }, time.Amount, look, 0.55f, EndFieldOfView);
            }, Easing.InOut));
    }

    // She draws, and holds her aim once the draw is done.
    private static void Draw(Erika erika)
    {
        erika.Animator.Play(Move.Draw);
        erika.Animator.Set(Pose.Aim);
    }

    private static void Shoot(ArcherGame game, Dummy target)
    {
        game.Erika.Animator.Play(Move.Recoil);
        game.Shoot(target);
    }

    // A place round a point: the angle from +z towards +x, how far out, and how high.
    private readonly record struct Spot(float Angle, float Radius, float Height);

    private static Camera At(Vector3 center, Spot spot, Vector3 look, float fieldOfView)
    {
        var position = center + new Vector3(MathF.Sin(spot.Angle) * spot.Radius, spot.Height, MathF.Cos(spot.Angle) * spot.Radius);
        return new Camera(position, center + look, fieldOfView);
    }

    // Arcs round a point from one spot to another, closing in or pulling away as it goes,
    // so the camera never cuts straight through whoever stands there.
    private static Camera Swing(Vector3 center, Spot from, Spot to, float amount, Vector3 look, float fromFieldOfView, float toFieldOfView)
    {
        var spot = new Spot(
            float.Lerp(from.Angle, to.Angle, amount),
            float.Lerp(from.Radius, to.Radius, amount),
            float.Lerp(from.Height, to.Height, amount));
        return At(center, spot, look, float.Lerp(fromFieldOfView, toFieldOfView, amount));
    }

    // The way from one point to another, as an angle round from +z towards +x.
    private static float Toward(Vector3 from, Vector3 to) => MathF.Atan2(to.X - from.X, to.Z - from.Z);

    // Just behind the arrow and a little above it, looking where it's going.
    private static Camera FollowArrow(ArcherGame game)
    {
        if (game.LastArrow is not { } arrow)
        {
            return new Camera(new Vector3(-0.55f, 1.6f, -0.95f), game.Dummies[0].Target, 0.42f);
        }

        var side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, arrow.Direction));
        var position = arrow.Center - arrow.Direction * 1.3f + new Vector3(0f, 0.16f, 0f) + side * 0.28f;
        return new Camera(position, arrow.Center + arrow.Direction * 3f, 0.7f);
    }

    // Off to one side of a dummy, looking at its chest, so an arrow comes in across the picture.
    private static Camera Beside(Dummy dummy, Vector3 shooter, float side, float height)
    {
        var toward = Vector3.Normalize((shooter - dummy.Position) with { Y = 0f });
        var across = Vector3.Cross(Vector3.UnitY, toward);
        var position = dummy.Position + across * side + toward * 1.4f + new Vector3(0f, height, 0f);
        return new Camera(position, dummy.Target, 0.6f);
    }

    private static Camera OverShoulder(Erika erika, Dummy target)
    {
        var ahead = Vector3.Normalize((target.Position - erika.Position) with { Y = 0f });
        var right = Vector3.Cross(ahead, Vector3.UnitY);
        var position = erika.Position - ahead * 1.3f + right * 0.55f + new Vector3(0f, 1.65f, 0f);
        return new Camera(position, target.Target, 0.5f);
    }

    private static Camera InFront(Erika erika, Dummy target)
    {
        var ahead = Vector3.Normalize((target.Position - erika.Position) with { Y = 0f });
        var right = Vector3.Cross(ahead, Vector3.UnitY);
        var position = erika.Position + ahead * 2.3f - right * 0.6f + new Vector3(0f, 1.3f, 0f);
        return new Camera(position, erika.Position + Chest, 0.55f);
    }
}

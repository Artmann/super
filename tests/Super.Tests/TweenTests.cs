using System.Numerics;

namespace Super.Tests;

public class TweenTests
{
    private const float Tolerance = 0.0001f;

    private static Tween<float> Ten(float seconds = 2f) => Tween.Between(0f, 10f, seconds);

    private static Tween<float> At(Tween<float> tween, float seconds)
    {
        tween.Advance(seconds);
        return tween;
    }

    [Fact]
    public void A_tween_moves_from_start_to_end_over_its_seconds()
    {
        Assert.Equal(0f, Ten().Value);
        Assert.Equal(5f, At(Ten(), 1f).Value, Tolerance);
        Assert.Equal(10f, At(Ten(), 2f).Value, Tolerance);
    }

    [Fact]
    public void An_easing_shapes_the_way()
    {
        // Quad in at half time is 0.25 of the way.
        Assert.Equal(2.5f, At(Ten().Ease(Easing.QuadIn), 1f).Value, Tolerance);
    }

    [Fact]
    public void Update_follows_the_scaled_tick()
    {
        var time = new GameTime(10) { Scale = 0.5f };
        var tween = Ten();
        for (int tick = 0; tick < 4; tick++)
        {
            tween.Update(time);
        }

        // Four ticks of a tenth of a second, at half speed, is 0.2 s.
        Assert.Equal(0.2f, tween.Elapsed, Tolerance);
        Assert.Equal(1f, tween.Value, Tolerance);
    }

    [Fact]
    public void A_delayed_tween_holds_its_start_value_until_it_begins()
    {
        var waiting = At(Ten().After(1f), 0.5f);
        var going = At(Ten().After(1f), 1.5f);

        Assert.Equal(0f, waiting.Value);
        Assert.False(waiting.IsPlaying);
        Assert.Equal(2.5f, going.Value, Tolerance);
        Assert.True(going.IsPlaying);
    }

    [Fact]
    public void A_tween_completes_at_its_end_and_stays_there()
    {
        var almost = At(Ten(), 1.9f);
        var done = At(Ten().After(0.5f), 9f);

        Assert.False(almost.Completed);
        Assert.True(done.Completed);
        Assert.False(done.IsPlaying);
        Assert.Equal(10f, done.Value);
        Assert.Equal(2.5f, done.Elapsed);
    }

    [Fact]
    public void A_loop_starts_again_from_the_beginning()
    {
        var tween = Ten().Loop(3);

        Assert.Equal(6f, tween.Duration);
        Assert.Equal(2.5f, At(tween, 2.5f).Value, Tolerance);
        Assert.False(At(tween, 5.9f).Completed);
        Assert.Equal(10f, At(tween, 6f).Value, Tolerance);
        Assert.True(At(tween, 6f).Completed);
    }

    [Fact]
    public void A_yoyo_goes_back_the_way_it_came_and_ends_at_the_start()
    {
        var tween = Ten().Yoyo(2);

        Assert.Equal(7.5f, At(tween, 2.5f).Value, Tolerance);
        Assert.Equal(5f, At(tween, 3f).Value, Tolerance);
        Assert.Equal(0f, At(tween, 4f).Value, Tolerance);
        Assert.True(At(tween, 4f).Completed);
    }

    [Fact]
    public void A_yoyo_with_an_odd_number_of_plays_ends_at_the_end()
    {
        Assert.Equal(10f, At(Ten().Yoyo(3), 6f).Value, Tolerance);
    }

    [Fact]
    public void A_yoyo_plays_its_easing_backwards_on_the_way_back()
    {
        // Half a second into the way back is 0.75 of the way along, and quad in gives 0.5625.
        Assert.Equal(5.625f, At(Ten().Ease(Easing.QuadIn).Yoyo(2), 2.5f).Value, Tolerance);
    }

    [Fact]
    public void A_tween_that_repeats_forever_never_completes_and_keeps_its_place()
    {
        var loop = At(Ten().Loop(), 1000.5f);
        var yoyo = At(Ten().Yoyo(), 1002.5f);

        Assert.Equal(float.PositiveInfinity, loop.Duration);
        Assert.False(loop.Completed);
        Assert.Equal(2.5f, loop.Value, Tolerance);

        // 501 plays done, so it's on a way back, a quarter of the way in.
        Assert.Equal(7.5f, yoyo.Value, Tolerance);
    }

    [Fact]
    public void A_curve_is_read_from_its_first_key_to_its_last_over_the_tween()
    {
        var curve = new Curve(
            new CurveKey(10f, 0f, CurveTangent.Linear),
            new CurveKey(15f, 2f, CurveTangent.Linear),
            new CurveKey(20f, 1f, CurveTangent.Linear));

        // A quarter of the tween is a quarter of the curve: time 12.5, value 1.
        Assert.Equal(10f, At(Ten().Ease(curve), 0.5f).Value, Tolerance);
        Assert.Equal(10f, At(Ten().Ease(curve), 2f).Value, Tolerance);
    }

    [Fact]
    public void An_easing_after_a_curve_replaces_it()
    {
        var tween = Ten().Ease(Curve.From(Easing.QuadIn)).Ease(Easing.Linear);

        Assert.Null(tween.Curve);
        Assert.Equal(5f, At(tween, 1f).Value, Tolerance);
    }

    [Fact]
    public void Vectors_blend_each_part()
    {
        var tween = Tween.Between(Vector3.Zero, new Vector3(2f, 4f, -8f), 1f);
        tween.Advance(0.25f);

        Assert.Equal(new Vector3(0.5f, 1f, -2f), tween.Value);
    }

    [Fact]
    public void Rotations_turn_the_short_way_round()
    {
        var tween = Tween.Between(Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), 1f);
        tween.Advance(0.5f);

        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);
        Assert.Equal(1f, MathF.Abs(Quaternion.Dot(expected, tween.Value)), Tolerance);
    }

    [Fact]
    public void Colors_blend_each_channel_and_the_opacity()
    {
        var tween = Tween.Between(new Color(0f, 0.2f, 1f, 1f), new Color(1f, 0.6f, 0f, 0f), 1f);
        tween.Advance(0.5f);

        var value = tween.Value;
        Assert.Equal(0.5f, value.R, Tolerance);
        Assert.Equal(0.4f, value.G, Tolerance);
        Assert.Equal(0.5f, value.B, Tolerance);
        Assert.Equal(0.5f, value.A, Tolerance);
    }

    [Fact]
    public void An_overshooting_easing_carries_past_the_end()
    {
        var tween = Ten(1f).Ease(Easing.BackOut);
        tween.Advance(0.5f);

        Assert.Equal(10.876975f, tween.Value, Tolerance);
    }

    [Fact]
    public void Smooth_blends_between_the_last_tick_and_this_one()
    {
        var tween = Ten(1f);
        tween.Advance(0.25f);
        tween.Advance(0.25f);

        Assert.Equal(2.5f, tween.Smooth(0f), Tolerance);
        Assert.Equal(3.75f, tween.Smooth(0.5f), Tolerance);
        Assert.Equal(5f, tween.Smooth(1f), Tolerance);
    }

    [Fact]
    public void Restart_goes_back_to_the_beginning()
    {
        var tween = At(Ten(), 1.5f);
        tween.Restart();

        Assert.Equal(0f, tween.Elapsed);
        Assert.Equal(0f, tween.Value);
    }

    [Fact]
    public void Setting_the_elapsed_time_jumps_to_that_moment()
    {
        var tween = Ten();
        tween.Elapsed = 1.5f;

        Assert.Equal(7.5f, tween.Value, Tolerance);
    }

    [Fact]
    public void A_tween_with_no_length_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Tween.Between(0f, 1f, 0f));

        Assert.StartsWith("A tween must last more than zero seconds.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_delay_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Ten().After(-1f));

        Assert.StartsWith("A tween's delay must be a finite number of at least zero.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_number_of_plays_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Ten().Loop(-1));

        Assert.StartsWith("A tween can't play a negative number of times. Use 0 for forever.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tween_cant_go_backwards()
    {
        var tween = Ten();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => tween.Advance(-0.1f));

        Assert.StartsWith("A tween only moves forward, by a finite number of seconds.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_a_tween_cant_blend_is_explained()
    {
        var tween = default(Tween<int>) with { From = 1, To = 2, Seconds = 1f };

        var error = Assert.Throws<NotSupportedException>(() => tween.Value);

        Assert.Equal("A tween can't blend Int32. Use float, Vector2, Vector3, Vector4, Quaternion or Color.", error.Message);
    }

    [Fact]
    public void A_sequence_plays_each_tween_in_turn()
    {
        var sequence = Tween.Sequence(Ten(1f), Tween.Between(10f, 0f, 2f).After(0.5f));

        Assert.Equal(5f, Sequence(sequence, 0.5f).Value, Tolerance);
        Assert.Equal(10f, Sequence(sequence, 1.25f).Value, Tolerance);
        Assert.Equal(5f, Sequence(sequence, 2.5f).Value, Tolerance);
        Assert.Equal(0f, Sequence(sequence, 3.5f).Value, Tolerance);
    }

    [Fact]
    public void A_sequence_finishes_at_its_summed_duration()
    {
        var time = new GameTime(30);
        var sequence = Tween.Sequence(Ten(1f), Ten(1.5f).Yoyo(2), Ten(0.5f).After(0.5f));
        int ticks = 0;
        while (!sequence.Completed)
        {
            Assert.True(sequence.IsPlaying);
            sequence.Update(time);
            ticks++;
        }

        // 1 + 1.5 × 2 + 0.5 + 0.5 = 5 seconds, at 30 ticks a second.
        Assert.Equal(5f, sequence.Duration, Tolerance);
        Assert.InRange(ticks, 150, 151);
        Assert.Equal(10f, sequence.Value, Tolerance);
    }

    [Fact]
    public void A_sequence_wont_take_a_tween_that_repeats_forever()
    {
        var error = Assert.Throws<ArgumentException>(() => Tween.Sequence(Ten(), Ten().Loop(), Ten()));

        Assert.StartsWith("Tween 1 in the sequence repeats forever, so the ones after it would never play.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_sequence_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => Tween.Sequence<float>());

        Assert.StartsWith("A sequence needs at least one tween.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_plays_together_and_ends_with_its_longest_tween()
    {
        var group = Tween.Parallel(Ten(1f), Ten(4f));
        group.Advance(1f);

        Assert.Equal(4f, group.Duration);
        Assert.Equal(10f, group[0], Tolerance);
        Assert.Equal(2.5f, group[1], Tolerance);
        Assert.False(group.Completed);

        group.Advance(3f);

        Assert.True(group.Completed);
        Assert.Equal(10f, group[1], Tolerance);
    }

    [Fact]
    public void A_group_starts_playing_when_its_first_tween_does()
    {
        var group = Tween.Parallel(Ten().After(1f), Ten().After(2f));
        group.Advance(0.5f);
        Assert.False(group.IsPlaying);

        group.Advance(0.5f);
        Assert.True(group.IsPlaying);
    }

    private static TweenSequence<float> Sequence(TweenSequence<float> sequence, float seconds)
    {
        sequence.Advance(seconds);
        return sequence;
    }
}

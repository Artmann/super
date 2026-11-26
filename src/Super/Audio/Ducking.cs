namespace Super;

/// <summary>
/// Dips channels under others, on the mixer side: a rule ducks a channel while any sound plays on another, and a
/// moment ducks one for a while. Each duck has a depth from 0 (not ducked) to 1 (its full decibels) that moves in a
/// straight line, over the attack going down and the release coming back, and the decibels follow the depth, so the
/// level glides evenly in dB. Where several ducks reach a channel, the deepest wins. It works a block at a time and
/// allocates nothing.
/// </summary>
internal sealed class Ducking
{
    private static readonly int ChannelCount = Enum.GetValues<Channel>().Length;

    // A rule for every target and trigger pair, at [target * ChannelCount + by]. Decibels of 0 means no rule.
    private readonly Duck[] _rules = new Duck[ChannelCount * ChannelCount];

    // A moment for every channel, ducked until its end on the mixer's clock.
    private readonly Duck[] _moments = new Duck[ChannelCount];
    private readonly long[] _momentEnds = new long[ChannelCount];

    private readonly float[] _gains = new float[ChannelCount];

    // Where the next block starts on the mixer's clock: where the last one Update was given ended.
    private long _clock;

    /// <summary>The attack a duck takes when the game gives none, in seconds.</summary>
    public const float AttackSeconds = 0.1f;

    /// <summary>The release a duck takes when the game gives none, in seconds.</summary>
    public const float ReleaseSeconds = 0.8f;

    /// <summary>Starts with Music down 10 dB and Ambience down 6 dB while anything plays on Dialogue.</summary>
    public Ducking()
    {
        Array.Fill(_gains, 1f);
        SetRule(Channel.Music, Channel.Dialogue, -10, AttackSeconds, ReleaseSeconds);
        SetRule(Channel.Ambience, Channel.Dialogue, -6, AttackSeconds, ReleaseSeconds);
    }

    /// <summary>Ducks <paramref name="target"/> by <paramref name="decibels"/> while a sound plays on <paramref name="by"/>. 0 dB removes the rule.</summary>
    public void SetRule(Channel target, Channel by, float decibels, float attackSeconds, float releaseSeconds)
    {
        ref Duck rule = ref _rules[(int)target * ChannelCount + (int)by];
        rule.Decibels = decibels;
        rule.AttackFrames = MixRate.ToFrames(attackSeconds);
        rule.ReleaseFrames = MixRate.ToFrames(releaseSeconds);
        if (decibels == 0)
        {
            rule.Depth = 0;
        }
    }

    /// <summary>
    /// Ducks the channel from the next block for <paramref name="holdSeconds"/>, then lets it come back. One already
    /// under way keeps the deeper decibels and the later end.
    /// </summary>
    public void DuckFor(Channel channel, float decibels, float holdSeconds, float attackSeconds, float releaseSeconds)
    {
        long clock = _clock;
        int index = (int)channel;
        // One that has ended and come back is cleared to 0 dB by Update, so it takes this one's decibels.
        ref Duck moment = ref _moments[index];
        moment.Decibels = Math.Min(moment.Decibels, decibels);
        moment.AttackFrames = MixRate.ToFrames(attackSeconds);
        moment.ReleaseFrames = MixRate.ToFrames(releaseSeconds);
        _momentEnds[index] = Math.Max(_momentEnds[index], clock + MixRate.ToFrames(holdSeconds));
    }

    /// <summary>How much ducking scales the channel this block, from 0 to 1.</summary>
    public float Gain(Channel channel) => _gains[(int)channel];

    /// <summary>
    /// Moves every duck on by a block of <paramref name="frames"/> starting at <paramref name="clock"/>, given which
    /// channels have a sound playing (bit <c>1 &lt;&lt; channel</c> of <paramref name="playing"/>), and works out each
    /// channel's gain for the block from where they end up.
    /// </summary>
    public void Update(int playing, int frames, long clock)
    {
        for (int target = 0; target < ChannelCount; target++)
        {
            float decibels = 0;
            for (int by = 0; by < ChannelCount; by++)
            {
                ref Duck rule = ref _rules[target * ChannelCount + by];
                if (rule.Decibels != 0)
                {
                    rule.Move((playing & (1 << by)) != 0, frames);
                    decibels = Math.Min(decibels, rule.Decibels * rule.Depth);
                }
            }

            ref Duck moment = ref _moments[target];
            if (moment.Decibels != 0)
            {
                moment.Move(clock < _momentEnds[target], frames);
                decibels = Math.Min(decibels, moment.Decibels * moment.Depth);
                if (moment.Depth == 0 && clock >= _momentEnds[target])
                {
                    moment.Decibels = 0;
                }
            }

            _gains[target] = decibels == 0 ? 1 : MathF.Pow(10, decibels / 20);
        }

        _clock = clock + frames;
    }

    private struct Duck
    {
        public float Decibels;
        public int AttackFrames;
        public int ReleaseFrames;
        public float Depth;

        /// <summary>Moves the depth towards 1 while <paramref name="on"/>, towards 0 otherwise, over this many frames.</summary>
        public void Move(bool on, int frames)
        {
            if (on)
            {
                Depth = AttackFrames == 0 ? 1 : Math.Min(1, Depth + (float)frames / AttackFrames);
            }
            else
            {
                Depth = ReleaseFrames == 0 ? 0 : Math.Max(0, Depth - (float)frames / ReleaseFrames);
            }
        }
    }
}

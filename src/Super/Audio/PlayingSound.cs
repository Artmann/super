namespace Super;

/// <summary>
/// One sound the mixer is playing. <paramref name="Clip"/> is the clip's content path. <paramref name="Gain"/> is
/// voice volume × fade × distance falloff × channel × master. <paramref name="Pan"/> is -1 (left) to 1 (right), 0 for a flat sound that isn't panned, or its pan for a flat sound that is (scattered ambience sounds).
/// <paramref name="Seconds"/> is how far into the clip it is.
/// </summary>
public readonly record struct PlayingSound(string Clip, Channel Channel, bool Looping, float Gain, float Pan, double Seconds);

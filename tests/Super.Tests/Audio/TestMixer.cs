namespace Super.Tests.Audio;

/// <summary>Builds mixers with the audio random stream the game would give them.</summary>
internal static class TestMixer
{
    public static Mixer Make(int seed = 1)
    {
        return new Mixer(new GameRandom(seed).Stream("audio"));
    }
}

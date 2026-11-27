using Super.Ui;

namespace Super;

/// <summary>
/// Plays the sounds the interface asked for, flat on <see cref="Channel.Interface"/>, before the frame hands its audio
/// over. The interface is updated after the draw hooks, so what it asks for in one frame plays at the start of the
/// next, a sixtieth of a second later. A built-in sound is made into a clip the first time it plays.
/// </summary>
internal sealed class InterfaceSounds(Audio audio, Content content) : IDrawHook
{
    private readonly Dictionary<UiSound, SoundRef> _clips = [];

    public void AfterDraw(View view)
    {
        if (view.Effects.Ui is not { } ui)
        {
            return;
        }

        foreach (var sound in ui.Cues.Take())
        {
            audio.Play(Clip(sound), volume: sound.Volume, channel: Channel.Interface, pitchVariation: sound.PitchVariation);
        }
    }

    private SoundRef Clip(UiSound sound)
    {
        if (!_clips.TryGetValue(sound, out var clip))
        {
            clip = sound.Make is { } make
                ? content.Sounds.AddClip(SoundClip.Made(sound.Name, UiSynth.SampleRate, make()))
                : content.Sounds.Sound(sound.Name);
            _clips.Add(sound, clip);
        }

        return clip;
    }
}

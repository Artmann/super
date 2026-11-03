using System.Numerics;

namespace Super.Ecs;

/// <summary>
/// Keeps the audio in step with the entities that have a <see cref="Sound"/> or an <see cref="Ambience"/>, after each
/// frame's Draw. Entities are told apart by <see cref="Entity"/> (id and generation), so an id reused by a new entity
/// in the same tick starts a fresh sound and lets the old one fade. Only changes are sent: the queue to the mixer is
/// drained once a tick, but this runs every frame.
/// </summary>
internal sealed class SoundSync(World world, Audio audio) : IDrawHook
{
    private const int NoId = -1;

    // What was sent for an entity, to tell what changed. A voice of -1 never started, so it's tried again.
    private struct PlayingLoop
    {
        public int Voice;
        public SoundRef Clip;
        public Channel Channel;
        public float Volume;
        public float Near;
        public float Far;
        public bool Placed;
        public Vector3 At;
    }

    private struct PlayingAmbience
    {
        public int Id;
        public AmbienceRef Value;
        public float Volume;
    }

    // Each frame fills one and empties the other, which is then swapped in. Whatever is left in the old one lost its
    // component.
    private Dictionary<Entity, PlayingLoop> _loops = [];
    private Dictionary<Entity, PlayingLoop> _lastLoops = [];
    private Dictionary<Entity, PlayingAmbience> _ambiences = [];
    private Dictionary<Entity, PlayingAmbience> _lastAmbiences = [];

    public void AfterDraw(View view)
    {
        SyncSounds(view.Blend);
        SyncAmbiences();
    }

    private void SyncSounds(float blend)
    {
        foreach (var row in world.Query<Sound>())
        {
            Entity entity = row.Entity;
            Sound sound = row.First;
            bool placed = world.Has<Transform>(entity);
            Vector3 at = placed ? ViewExtensions.WorldMatrix(world, entity, blend, depth: 0).Translation : default;

            if (!_lastLoops.Remove(entity, out PlayingLoop loop))
            {
                loop = StartLoop(sound, placed, at);
            }
            else if (loop.Voice == NoId || loop.Clip != sound.Clip || loop.Channel != sound.Channel
                || loop.Near != sound.Near || loop.Far != sound.Far || loop.Placed != placed)
            {
                audio.StopLoop(loop.Voice);
                loop = StartLoop(sound, placed, at);
            }
            else
            {
                if (placed && loop.At != at)
                {
                    audio.Move(loop.Voice, at);
                    loop.At = at;
                }

                if (loop.Volume != sound.Volume)
                {
                    audio.ChangeVolume(loop.Voice, sound.Volume);
                    loop.Volume = sound.Volume;
                }
            }

            _loops[entity] = loop;
        }

        foreach (var gone in _lastLoops.Values)
        {
            audio.StopLoop(gone.Voice);
        }

        _lastLoops.Clear();
        (_loops, _lastLoops) = (_lastLoops, _loops);
    }

    private PlayingLoop StartLoop(Sound sound, bool placed, Vector3 at)
    {
        return new PlayingLoop
        {
            Voice = audio.StartLoop(sound.Clip, sound.Volume, sound.Channel, placed ? at : null, sound.Near, sound.Far),
            Clip = sound.Clip,
            Channel = sound.Channel,
            Volume = sound.Volume,
            Near = sound.Near,
            Far = sound.Far,
            Placed = placed,
            At = at,
        };
    }

    private void SyncAmbiences()
    {
        foreach (var row in world.Query<Ambience>())
        {
            Entity entity = row.Entity;
            Ambience ambience = row.First;

            if (!_lastAmbiences.Remove(entity, out PlayingAmbience playing))
            {
                playing = StartAmbience(ambience);
            }
            else if (playing.Id == NoId || playing.Value != ambience.Value)
            {
                audio.StopAmbience(playing.Id);
                playing = StartAmbience(ambience);
            }
            else if (playing.Volume != ambience.Volume)
            {
                audio.ChangeAmbienceVolume(playing.Id, ambience.Volume);
                playing.Volume = ambience.Volume;
            }

            _ambiences[entity] = playing;
        }

        foreach (var gone in _lastAmbiences.Values)
        {
            audio.StopAmbience(gone.Id);
        }

        _lastAmbiences.Clear();
        (_ambiences, _lastAmbiences) = (_lastAmbiences, _ambiences);
    }

    private PlayingAmbience StartAmbience(Ambience ambience)
    {
        return new PlayingAmbience { Id = audio.StartAmbience(ambience.Value, ambience.Volume), Value = ambience.Value, Volume = ambience.Volume };
    }
}

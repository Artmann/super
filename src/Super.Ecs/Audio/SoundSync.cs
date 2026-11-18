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

    // What was sent for an entity, to tell what changed. A voice of -1 never started, so it's tried again; one the
    // voice limit dropped is started again once a slot is free.
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

        /// <summary>Whether this loop still plays <paramref name="sound"/>: the same clip, channel, distances and placing.</summary>
        public readonly bool Plays(Sound sound, bool placed) =>
            Clip == sound.Clip && Channel == sound.Channel && Near == sound.Near && Far == sound.Far && Placed == placed;
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
        bool completed = false;
        try
        {
            foreach (var row in world.Query<Sound>())
            {
                Entity entity = row.Entity;
                Sound sound = row.First;
                bool placed = world.Has<Transform>(entity);
                Vector3 at = placed ? ViewExtensions.WorldMatrix(world, entity, blend, depth: 0).Translation : default;
                Check(entity, sound, placed, at);
                _loops[entity] = SyncLoop(entity, sound, placed, at);
                _lastLoops.Remove(entity);
            }

            completed = true;
        }
        finally
        {
            EndSync(ref _loops, ref _lastLoops, completed, audio, static (audio, left) => audio.Loops.Stop(left.Voice));
        }
    }

    /// <summary>Starts the entity's loop, restarts it when it changed or was dropped, or moves it and sets its volume.</summary>
    private PlayingLoop SyncLoop(Entity entity, Sound sound, bool placed, Vector3 at)
    {
        if (!_lastLoops.TryGetValue(entity, out PlayingLoop loop))
        {
            return StartLoop(sound, placed, at);
        }

        if (loop.Voice == NoId || !loop.Plays(sound, placed))
        {
            return Restart(entity, loop, sound, placed, at);
        }

        if (audio.Loops.WasDropped(loop.Voice))
        {
            // The voice limit dropped it. It comes back once the mixer has a slot free; until then nothing is
            // sent for it, so a full mixer isn't asked again every frame.
            return audio.Loops.TryClaimVoice() ? Restart(entity, loop, sound, placed, at) : loop;
        }

        if (placed && loop.At != at)
        {
            audio.Loops.Move(loop.Voice, at);
            loop.At = at;
        }

        if (loop.Volume != sound.Volume)
        {
            audio.Loops.ChangeVolume(loop.Voice, sound.Volume);
            loop.Volume = sound.Volume;
        }

        return loop;
    }

    private static void Check(Entity entity, Sound sound, bool placed, Vector3 at)
    {
        if (!(sound.Volume >= 0) || !float.IsFinite(sound.Volume))
        {
            throw new ArgumentOutOfRangeException(nameof(sound), sound.Volume, $"Entity {entity.Id}'s Sound has Volume {sound.Volume}; it must be a number of at least 0.");
        }

        if ((uint)sound.Channel > (uint)Channel.Interface)
        {
            throw new ArgumentOutOfRangeException(nameof(sound), sound.Channel, $"Entity {entity.Id}'s Sound has Channel {sound.Channel}; it must be one of the Channel values.");
        }

        if (!(sound.Near >= 0) || !float.IsFinite(sound.Near))
        {
            throw new ArgumentOutOfRangeException(nameof(sound), sound.Near, $"Entity {entity.Id}'s Sound has Near {sound.Near}; it must be a distance of at least 0.");
        }

        if (!(sound.Far > sound.Near) || !float.IsFinite(sound.Far))
        {
            throw new ArgumentOutOfRangeException(nameof(sound), sound.Far, $"Entity {entity.Id}'s Sound has Far {sound.Far}; it must be finite and further than its Near, {sound.Near}.");
        }

        if (placed && !IsFinite(at))
        {
            throw new ArgumentOutOfRangeException(nameof(sound), at, $"Entity {entity.Id}'s Sound is at {at}, which isn't a finite position. Check its Transform and its parents'.");
        }
    }

    private static bool IsFinite(Vector3 at) => float.IsFinite(at.X) && float.IsFinite(at.Y) && float.IsFinite(at.Z);

    private PlayingLoop Restart(Entity entity, PlayingLoop loop, Sound sound, bool placed, Vector3 at)
    {
        // Written back first, so a throw below leaves the entity tracked with no voice, not with a stopped one.
        audio.Loops.Stop(loop.Voice);
        loop.Voice = NoId;
        _lastLoops[entity] = loop;
        return StartLoop(sound, placed, at);
    }

    private PlayingLoop StartLoop(Sound sound, bool placed, Vector3 at)
    {
        return new PlayingLoop
        {
            Voice = audio.Loops.Start(new SoundLoop
            {
                Clip = sound.Clip,
                Volume = sound.Volume,
                Channel = sound.Channel,
                At = placed ? at : null,
                Near = sound.Near,
                Far = sound.Far,
            }),
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
        bool completed = false;
        try
        {
            foreach (var row in world.Query<Ambience>())
            {
                _ambiences[row.Entity] = SyncAmbience(row.Entity, row.First);
                _lastAmbiences.Remove(row.Entity);
            }

            completed = true;
        }
        finally
        {
            EndSync(ref _ambiences, ref _lastAmbiences, completed, audio, static (audio, left) => audio.Ambiences.Stop(left.Id));
        }
    }

    /// <summary>Starts the entity's ambience, restarts it when it changed, or sets its volume.</summary>
    private PlayingAmbience SyncAmbience(Entity entity, Ambience ambience)
    {
        if (!(ambience.Volume >= 0) || !float.IsFinite(ambience.Volume))
        {
            throw new ArgumentOutOfRangeException(nameof(ambience), ambience.Volume, $"Entity {entity.Id}'s Ambience has Volume {ambience.Volume}; it must be a number of at least 0.");
        }

        if (!_lastAmbiences.TryGetValue(entity, out PlayingAmbience playing))
        {
            return StartAmbience(ambience);
        }

        if (playing.Id == NoId || playing.Value != ambience.Value)
        {
            // Written back first, so a throw below leaves the entity tracked with no ambience, not with a stopped one.
            audio.Ambiences.Stop(playing.Id);
            playing.Id = NoId;
            _lastAmbiences[entity] = playing;
            return StartAmbience(ambience);
        }

        if (playing.Volume != ambience.Volume)
        {
            audio.Ambiences.ChangeVolume(playing.Id, ambience.Volume);
            playing.Volume = ambience.Volume;
        }

        return playing;
    }

    /// <summary>
    /// Ends a sync: stops what it didn't meet, then makes what it met the ones playing. After a throw, what wasn't
    /// reached keeps playing as it was, rather than counting as removed.
    /// </summary>
    private static void EndSync<T>(ref Dictionary<Entity, T> met, ref Dictionary<Entity, T> last, bool completed, Audio audio, Action<Audio, T> stop)
    {
        foreach (var (entity, left) in last)
        {
            if (completed)
            {
                stop(audio, left);
            }
            else
            {
                met[entity] = left;
            }
        }

        last.Clear();
        (met, last) = (last, met);
    }

    private PlayingAmbience StartAmbience(Ambience ambience)
    {
        return new PlayingAmbience { Id = audio.Ambiences.Start(ambience.Value, ambience.Volume), Value = ambience.Value, Volume = ambience.Volume };
    }
}

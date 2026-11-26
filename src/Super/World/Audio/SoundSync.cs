using System.Numerics;

namespace Super;

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
        public Vector3 Velocity;
        public float Doppler;
        public HeardMotion Motion;

        /// <summary>Whether this loop still plays <paramref name="sound"/>: the same clip, channel, distances and placing.</summary>
        public readonly bool Plays(Sound sound, bool placed) =>
            Clip == sound.Clip && Channel == sound.Channel && Near == sound.Near && Far == sound.Far && Placed == placed;
    }

    // An entity's sound as this frame draws it: where it is, if it's placed, at what time on screen.
    private readonly record struct Heard(Sound Sound, bool Placed, Vector3 At, double Now);

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
        SyncSounds(view.Blend, view.Clock.Now);
        SyncAmbiences();
    }

    private void SyncSounds(float blend, double now)
    {
        bool completed = false;
        try
        {
            foreach (var row in world.Query<Sound>())
            {
                Entity entity = row.Entity;
                if (WorldFrame.IsHidden(world, entity))
                {
                    continue;
                }

                Sound sound = row.First;
                bool placed = world.Has<Transform>(entity);
                Vector3 at = placed ? WorldFrame.WorldMatrix(world, entity, blend).Translation : default;
                Check(entity, sound, placed, at);
                _loops[entity] = SyncLoop(entity, new Heard(sound, placed, at, now));
                _lastLoops.Remove(entity);
            }

            completed = true;
        }
        finally
        {
            EndSync(ref _loops, ref _lastLoops, completed, audio, static (audio, left) => audio.Loops.Stop(left.Voice));
        }
    }

    /// <summary>
    /// Starts the entity's loop, restarts it when it changed or was dropped, or moves it, with how fast it moves for
    /// the Doppler effect, and sets its volume.
    /// </summary>
    private PlayingLoop SyncLoop(Entity entity, Heard heard)
    {
        if (!_lastLoops.TryGetValue(entity, out PlayingLoop loop))
        {
            return StartLoop(heard);
        }

        if (loop.Voice == NoId || !loop.Plays(heard.Sound, heard.Placed))
        {
            return Restart(entity, loop, heard);
        }

        if (audio.Loops.WasDropped(loop.Voice))
        {
            // The voice limit dropped it. It comes back once the mixer has a slot free; until then nothing is
            // sent for it, so a full mixer isn't asked again every frame.
            return audio.Loops.TryClaimVoice() ? Restart(entity, loop, heard) : loop;
        }

        if (heard.Placed)
        {
            Follow(ref loop, heard);
        }

        if (loop.Volume != heard.Sound.Volume)
        {
            audio.Loops.ChangeVolume(loop.Voice, heard.Sound.Volume);
            loop.Volume = heard.Sound.Volume;
        }

        return loop;
    }

    /// <summary>Moves a placed loop when where it is, how fast it moves or its Doppler amount changed.</summary>
    private void Follow(ref PlayingLoop loop, Heard heard)
    {
        Vector3 velocity = loop.Motion.Update(heard.At, heard.Now);
        float doppler = heard.Sound.Doppler;
        if (loop.At == heard.At && loop.Velocity == velocity && loop.Doppler == doppler)
        {
            return;
        }

        audio.Loops.Move(loop.Voice, heard.At, velocity, doppler);
        loop.At = heard.At;
        loop.Velocity = velocity;
        loop.Doppler = doppler;
    }

    private static void Check(Entity entity, Sound sound, bool placed, Vector3 at)
    {
        CheckLevels(entity, sound);
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

    // The amounts that scale the sound, its volume and Doppler, must be finite and at least 0.
    private static void CheckLevels(Entity entity, Sound sound)
    {
        if (!(sound.Volume >= 0) || !float.IsFinite(sound.Volume))
        {
            throw new ArgumentOutOfRangeException(nameof(sound), sound.Volume, $"Entity {entity.Id}'s Sound has Volume {sound.Volume}; it must be a number of at least 0.");
        }

        if (!(sound.Doppler >= 0) || !float.IsFinite(sound.Doppler))
        {
            throw new ArgumentOutOfRangeException(nameof(sound), sound.Doppler, $"Entity {entity.Id}'s Sound has Doppler {sound.Doppler}; it must be a number of at least 0.");
        }
    }

    private static bool IsFinite(Vector3 at) => float.IsFinite(at.X) && float.IsFinite(at.Y) && float.IsFinite(at.Z);

    private PlayingLoop Restart(Entity entity, PlayingLoop loop, Heard heard)
    {
        // Written back first, so a throw below leaves the entity tracked with no voice, not with a stopped one.
        audio.Loops.Stop(loop.Voice);
        loop.Voice = NoId;
        _lastLoops[entity] = loop;
        return StartLoop(heard);
    }

    /// <summary>Starts the loop still: its speed is known from the next frame on.</summary>
    private PlayingLoop StartLoop(Heard heard)
    {
        (Sound sound, bool placed, Vector3 at, double now) = heard;
        var motion = new HeardMotion();
        motion.Update(at, now);
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
                Doppler = sound.Doppler,
            }),
            Clip = sound.Clip,
            Channel = sound.Channel,
            Volume = sound.Volume,
            Near = sound.Near,
            Far = sound.Far,
            Placed = placed,
            At = at,
            Doppler = sound.Doppler,
            Motion = motion,
        };
    }

    private void SyncAmbiences()
    {
        bool completed = false;
        try
        {
            foreach (var row in world.Query<Ambience>())
            {
                if (WorldFrame.IsHidden(world, row.Entity))
                {
                    continue;
                }

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

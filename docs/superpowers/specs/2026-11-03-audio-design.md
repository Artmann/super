# Audio design

Super has no audio. A game should be able to play music, layer ambient loops into a
place (a forest of rain, wind and birds; an arctic of wind and ice cracks), play a
one-shot when something happens, and keep a loop going while something is happening,
such as hammering while a builder works. Some sounds are flat (music, menu clicks),
others sit in the world and get quieter and move left or right with the camera, like a
gunshot next to you versus far away.

The first game to use it is the user's forest demo: forest ambience, music, someone
building a log cabin with hammer sounds, and inventory clicks.

## The idea that matters most

Sound follows how animation already works in Super: **the game describes what's going
on, and the engine plays it.** No sound calls in `Draw`, no per-tick stepping, no
handles to stop. A loop is a component on an entity; it plays while the component is
there and fades out when it's gone. The user asked for this explicitly ("I want this
to be built into the end user so they don't have to think about it, similar to what we
did with animations"). Any design choice that makes the game author babysit a sound
goes against the point of this work.

## What the game writes

```csharp
public class Forest(Audio audio, Content content, World world) : IGame
{
    readonly SoundRef hammering = content.Sound("sounds/hammering.wav");
    readonly SoundRef pickup = content.Sound("sounds/accept.wav");
    readonly AmbienceRef forest = content.Ambience("sounds/forest.ambience.json");

    public void Load()
    {
        // Ambience is an entity, so it works with states: leave the forest and it fades out.
        var mood = world.Spawn();
        world.Add(mood, new Ambience(forest));
        world.Add(mood, new ScopedTo<Screen>(Screen.Forest));

        audio.Music(content.Sound("music/clearing.ogg"));   // cross-fades from what was playing
    }

    public void Update(GameTime time)
    {
        // A loop on an entity: it plays from the entity's Transform and follows it.
        if (builder.StartedBuilding) { world.Add(builder.Entity, new Sound(hammering)); }
        if (builder.StoppedBuilding) { world.Remove<Sound>(builder.Entity); }

        // One-shots, flat or placed.
        if (picked) { audio.Play(pickup); }                      // Interface channel
        if (fired) { audio.Play(gunshot, at: muzzlePosition); }  // Effects channel, in the world
    }
}
```

Player settings:

```csharp
audio.Volume(Channel.Music, 0.5f);
audio.Volume(Channel.Master, 0.8f);
```

## Pieces

### Channels

`Channel.Master`, `Music`, `Ambience`, `Effects`, `Interface`. Each has a volume from 0
to 1; Master scales all of them. Defaults: `Music` for `audio.Music`, `Ambience` for
`Ambience`, `Interface` for flat `audio.Play`, `Effects` for `Sound` components and
placed `audio.Play`. Every call that plays something takes an optional `channel:` to
override. Saving the player's volume choices is the game's job, not Super's.

### `content.Sound(path)` → `SoundRef`

Reads WAV, OGG and MP3. Short files are decoded fully into memory at load; long ones
(music, long ambient loops) stream. Where the line sits is the builder's call. Files are
used as they are, with no cooking step. A missing or unreadable file throws with the path
in the message, matching how `content.Model` reports problems.

### `Sound` component

`new Sound(SoundRef clip, float volume = 1, Channel channel = Channel.Effects)`. Loops.
If the entity has a `Transform`, it's a world sound at that position, following it as it
moves (using the smoothed position when the entity is `Smoothed`). Without a `Transform`
it plays flat. It fades in over a short time when added and fades out when removed, when
the entity is destroyed, or when its `ScopedTo` state ends. Replacing it with a different
clip cross-fades. One `Sound` per entity, like every other component; an entity that
needs two loops uses a child entity.

Entities that already have a `Sound` when the game loads or starts simply start
playing; nothing replays.

### `audio.Play(clip, at:, volume:, channel:, pitchVariation:)`

A one-shot. Without `at:` it's flat. `pitchVariation` (e.g. 0.05 for ±5%) picks a random
pitch each time, so repeated hits don't sound identical. It can be called from `Update`
and from interface click handlers. Calls made while a save loads or the first tick runs
behave like any other call; there's nothing to suppress because nothing replays from
state.

### `Ambience` component and `.ambience.json`

An ambience is layers played together, written in JSON (the user likes content in JSON):

```json
{
  "layers": [
    { "file": "ambient/rain.wav", "volume": 0.6 },
    { "file": "ambient/wind2.wav", "volume": 0.3 }
  ],
  "scattered": [
    {
      "files": ["ice/crack1.wav", "ice/crack2.wav", "ice/crack3.wav"],
      "every": [4, 12],
      "volume": [0.4, 0.8],
      "pitchVariation": 0.1
    }
  ]
}
```

- **Layers** loop for as long as the ambience plays, each at its own volume. Each starts
  at a random point in its file so layers don't line up the same way every time.
- **Scattered** sounds fire at random intervals between the two `every` values (seconds),
  picking a file and a volume at random. This is for things that shouldn't loop: ice
  cracks, a distant bird, a branch snapping. They play flat but panned to a random side.
- An `Ambience` is flat and plays on the `Ambience` channel. It fades in when added and
  out when removed. Two at once mix; swapping one for another cross-fades.
- `new Ambience(ambience, volume)` scales the whole thing. Changing the component's
  volume fades to the new level, so a game can bring rain up when a storm starts by
  setting the volume, or by having a second ambience for the rain alone.

### `audio.Music(clip)`

One music track at a time, looping. Calling it with a new track cross-fades (2 seconds by
default, `fadeSeconds:` to change). `audio.Music(null)` fades it out. Calling it with the
track already playing does nothing, so it's safe to call every tick.

### World sounds

The listener is the camera the view used on the latest frame. A world sound's loudness
falls with distance between `near` (full volume, default 2 m) and `far` (silent, default
50 m); both can be set on `Sound` and `audio.Play`. It's panned left and right by where
it sits relative to the camera's facing, with equal-power panning. Stereo files placed in
the world are folded to mono first.

### Time

When the game's `Scale` is 0, world sounds and `Sound` components pause; when it's below
1 they play slower and lower, matching slow motion. Music, ambience and interface sounds
ignore `Scale`, so the pause menu still clicks and the music keeps going.

Pause in Super is a state (`States<Pause>`), not a time scale, so a pause screen freezes
world sounds by setting `Scale = 0`, as games already do to freeze the world. `docs/audio.md`
and the example show this. Freezing on a pause state by itself is a possible later change.

Audio is never part of the simulation. Its random choices (scattered timing, pitch
variation, layer start points) use their own random source, never the game's, so audio
can't change how a seed plays out.

## How it's built (my calls)

These were left to me; the user hands technical decisions over.

- **Super mixes the audio itself, in C#.** All the gain, fade, pan and distance work
  happens in Super's own mixer, which produces finished 48 kHz stereo float samples. The
  speakers get only the finished stream. Reasons: the mix can be tested with no sound
  card, it can be held to the zero-allocation rule while the game runs, and nothing about
  behaviour depends on a native library.
- **Output: OpenAL Soft through `Silk.NET.OpenAL`**, used only as a streaming output for
  the finished mix. Super already uses Silk.NET for GLFW. If OpenAL turns out awkward as a
  plain output, WASAPI directly is an acceptable swap; the mixer doesn't care.
- **Decoding: NVorbis for OGG, NLayer for MP3, WAV read by hand** (16-bit and float PCM,
  mono and stereo). All are pure C#, MIT licensed, no extra installs. Files at other
  sample rates are resampled to 48 kHz.
- **Threads.** The mixer runs on the audio thread. The game thread sends it what changed
  (components added and removed, one-shots, volumes, listener) through a queue that
  doesn't allocate once warmed up. The game thread never waits on the audio thread.
- **Headless.** With no window, or in tests, there's no output device; the mixer still
  runs and can be pulled by hand. A machine with no sound device runs the game silently
  rather than failing.
- **Where it lives.** `Audio`, `SoundRef`, the mixer and decoders in `Super`; the `Sound`
  and `Ambience` components and the system that turns entity changes into mixer commands
  in `Super.Ecs`, next to `Animated`. `Audio` is a service a game asks for in its
  constructor, like `Actions<T>`.

## Testing

Tests need no sound card. Two levels:

- **What's playing.** Tests can ask the engine (through `Super.Testing`) what's playing
  right now: each voice's clip, channel, whether it's looping, its current gain and pan.
  That's how a test says "the hammering is playing on the left at about 40%", "the
  forest ambience faded out after leaving the state", or "the pause menu froze the
  hammering but not the music".
- **The real mix.** Tests can pull samples from the mixer and check them: a known tone
  at a known gain, silence after a fade, a left-only signal for a sound off to the left.

Fades and scattered sounds are timed by the mixer's sample clock, not the wall clock, so
tests step them exactly. The repo's test rules in `CLAUDE.md` apply.

`--record` should include the game's audio in the mp4 (it already uses ffmpeg). If muxing
the audio in turns out hard, writing a `.wav` next to the video is acceptable for a first
version. A recording runs faster than real time, so its audio can't come from the device:
the mixer is pulled by hand, one video frame's worth of samples per frame, as in a
headless run.

The audio's own random source has a fixed seed in headless runs, tests and recordings,
so a recording sounds the same every time and tests can predict when scattered sounds
fire. Live play may use an unseeded one. Either way it stays separate from the game's.

## The example

`examples/Forest`: a ground, some trees, a builder figure that alternates between
building (with a hammering `Sound` loop) and walking, the forest ambience (rain and wind
layers, plus scattered sounds if suitable ones exist), a music track, and a key or click
that plays an interface sound. Moving the camera around the builder shows distance and
panning. Keys change channel volumes so they can be heard working.

Sounds come from the **Ninja Adventure asset pack** (CC0) in
`C:\Users\Artga\Downloads\Ninja Adventure - Asset Pack\Ninja Adventure - Asset Pack\Audio`:
`Sounds/Ambient` (Rain, Rain2, Rain3, River, Wind, Wind2, Storm), `Musics` (e.g.
`11 - Clearing.ogg`, `5 - Peaceful.ogg`, `37 - Dark Forest.ogg`), `Sounds/Menu` (Accept,
Cancel, Move) and `Sounds/Hit & Impact` for knocks. Copy only what the example uses into
the repo's assets and credit the pack in `CREDITS.md`. The builder may also fetch CC0
birdsong, insects or a hammer-on-wood recording (e.g. from Freesound) and credit each.
Do not use the Pixabay files (blacksmith-workshop, firewood-burning) or the Warcraft clips
in Downloads; they can't go in a public repo.

The pack has no real hammering loop. A loop built from repeated `Hit & Impact` knocks, or
a fetched CC0 recording, is fine.

## Docs

A new `docs/audio.md` user guide; `Sound` and `Ambience` in the components table in
`docs/ecs.md`; the "play a creak here, once audio exists" comment in `docs/views.md`
replaced with real code; audio removed from the gaps in `docs/roadmap.md`; engine
internals (mixer, threads, output) in `docs/design.md`; `--record` audio in
`docs/command-line.md`.

## Not now

- Syncing sounds to animation moments (no animation events exist yet; `Crossed` could
  drive a one-shot by hand).
- Echo, reverb, walls muffling sound, Doppler.
- Headphone 3D (HRTF) and surround; output is stereo.
- Area-based ambience that blends by where the camera stands. States and entities cover
  "the forest" versus "the arctic" for now.
- Non-Windows platforms.
- Ducking (music dipping under speech).

## Left to the builder

The stream-versus-decode threshold, fade lengths, the exact falloff curve between `near`
and `far`, the queue's design, the shape of the `Super.Testing` query, how many voices can
play at once and what happens past that (dropping the quietest is a sensible default),
the example's layout, where file paths in `.ambience.json` are read from (match how
models find their textures), and how a `Sound` on a child entity finds its world
position (through `Parent`).

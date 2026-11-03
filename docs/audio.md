# Audio

Sounds and ambiences are named from content, the way models and images are. Naming
reads and checks the file at once, on the calling thread, so a missing or broken file
stops the game with a `ContentException` that names it.

## Sounds and ambiences

```csharp
SoundRef pop = content.Sound("sounds/pop.wav");
AmbienceRef forest = content.Ambience("sounds/forest.ambience.json");
```

Paths are inside the content folder. Naming the same path again gives the same ref.

### Formats

| Extension | Read |
| --- | --- |
| `.wav` | 16-bit and 32-bit float PCM, mono or stereo |
| `.ogg` | Vorbis, mono or stereo |
| `.mp3` | mono or stereo |

Any other extension, or a clip with more than two channels, is a `ContentException`
naming the path. Clips up to 10 seconds are decoded once and kept in memory; longer ones
are decoded as they play. A longer `.ogg` or `.mp3` costs a little memory churn as it
decodes, so keep effects that play often under 10 seconds. The sample rate can be anything,
because the mixer outputs 48 000 Hz and converts.

### Ambience files

An ambience is a JSON file, conventionally named `<name>.ambience.json`. **Layers** loop
for as long as the ambience plays. **Scattered** sounds are one-shots: a random file from
`files` plays every `every` seconds (a random wait between the two numbers), at a random
volume from `volume`, with its pitch moved up or down by up to `pitchVariation`.

```json
{
  "layers": [
    { "file": "ambient/wind.ogg", "volume": 0.6 },
    { "file": "ambient/birds.ogg", "volume": 0.3 }
  ],
  "scattered": [
    {
      "files": ["ambient/crow.wav", "ambient/owl.wav"],
      "every": [4, 12],
      "volume": [0.4, 0.8],
      "pitchVariation": 0.1
    }
  ]
}
```

| Key | Meaning | Default |
| --- | --- | --- |
| `layers[].file` | The clip to loop | required |
| `layers[].volume` | Layer volume, 1 is the clip's own level | 1 |
| `scattered[].files` | Clips to pick from, at least one | required |
| `scattered[].every` | `[min, max]` seconds between plays, min not above max | required |
| `scattered[].volume` | `[min, max]` volume of each play | `[1, 1]` |
| `scattered[].pitchVariation` | How far a play's pitch may move, up or down | 0 |

Both `layers` and `scattered` may be left out.

When an ambience plays, each layer starts at a random point in its clip, so it doesn't
begin the same way every time. Each scattered entry waits a random time in `every`, plays
one random file at a random volume, a pitch moved by up to `pitchVariation` (0.1 is
10%) either way, and a random spot between left and right, then waits again. Up to 8
ambiences can play at once, and their sounds count against the mixer's 64 voices, but the voice limit never drops a layer (it may drop a scattered sound). Stopping
an ambience fades its layers out and starts no new scattered sounds; one already
playing finishes. The sounds are picked by the game's audio random stream, so a fixed
audio seed (headless, screenshots, recordings) gives the same ambience every run.

### Path rules

Paths inside an ambience file are relative to the folder of that file, so
`sounds/forest.ambience.json` naming `"ambient/rain.wav"` reads `sounds/ambient/rain.wav`.
`..` steps up a folder, and `\` works like `/`. Paths given to `content.Sound` and `content.Ambience` are
relative to the content folder.

A mistake in an ambience, such as `"every": [12, 4]`, a missing file or invalid JSON, is
a `ContentException` that names the ambience file and what is wrong. A missing clip
names both the ambience file and the clip.

## Playing sounds

Ask for `Audio` in a constructor, as for `Content`, and play a clip once with `Play`:

```csharp
class Combat(Audio audio, Content content)
{
    private readonly SoundRef _click = content.Sound("sounds/click.wav");
    private readonly SoundRef _hit = content.Sound("sounds/hit.wav");

    public void Strike(Vector3 where)
    {
        audio.Play(_hit, at: where, pitchVariation: 0.05f);
    }

    public void ButtonPressed() => audio.Play(_click);
}
```

Without `at:` a sound is flat: it keeps its own stereo, plays on the `Interface` channel
and ignores the time scale. With `at:` it's placed in the world on the `Effects` channel,
heard from the camera the last frame showed: full volume within `near` metres, silent
from `far` on, and panned by where it sits relative to the camera's facing. A stereo clip
placed in the world is folded to mono.

| Argument | Meaning | Default |
| --- | --- | --- |
| `at` | Where in the world, or null for flat | null |
| `volume` | 0 or more. 1 is the clip as recorded | 1 |
| `channel` | The channel to play on | `Interface` when flat, `Effects` when placed |
| `pitchVariation` | How far the pitch may move up or down each play, from 0 up to 1. 0.05 is ±5% | 0 |
| `near` | Full volume up to this many metres, 0 or more | 2 |
| `far` | Silent from this many metres, beyond `near` | 50 |

A volume below 0, a `pitchVariation` outside 0 up to 1, a negative `near`, a `far` not
beyond `near` or an `at` that isn't a finite position throws
`ArgumentOutOfRangeException` naming the argument. Pitch variation is picked from audio's
own random stream, never the game's `GameRandom`, so playing sounds can't change how a
seed plays out. Headless runs, tests, screenshots and recordings use a fixed audio seed,
so they sound the same every time; only live play picks a new one.

`Play` can be called from `Update` and from interface handlers. It never waits: calls are
queued and handed to the mixer together at the end of each frame. If more than 4096 are
queued before a frame ends, the extra ones are dropped rather than holding the game up.

Up to 64 sounds play at once. Past that the quietest sound gives way to a new one, or the
new one isn't played if it's the quietest. Music and ambience layers never give way.

## Sounds and ambiences on entities

For a sound that lasts as long as something exists, a humming machine or a stream, put a
[`Sound`](ecs.md#sounds-from-entities) component on the entity. Super plays it, moves it
and stops it for you, so `Draw` and `Update` make no sound calls:

```csharp
world.Add(machine, Transform.At(new Vector3(8f, 0f, 3f)));
world.Add(machine, new Sound(content.Sound("sounds/hum.ogg"), Volume: 0.6f, Near: 3, Far: 30));
```

| Field | Meaning | Default |
| --- | --- | --- |
| `Clip` | The clip, which loops | |
| `Volume` | 0 or more | 1 |
| `Channel` | The channel to play on | `Effects` |
| `Near`, `Far` | Full volume within `Near` metres, silent from `Far` | 2, 50 |

- With a `Transform` it's placed where the entity is drawn, including `Smoothed` and
  `Parent`, and follows it. Without one it's flat, but it still follows the time scale:
  it pauses at `Scale` 0 and slows below 1, unlike a flat `audio.Play`.
- It fades in over 0.1 s. Removing the component or destroying the entity fades it out
  over 0.3 s. An entity that has a `Sound` when the game loads simply starts playing.
- Changing `Volume` fades to it over 0.1 s. Changing `Clip`, `Channel`, `Near`, `Far`, or
  whether the entity has a `Transform`, fades the old sound out and starts the new one.
- A sound that can't start, such as when all 1024 ids are in use, is tried again each
  frame until it can.

An [`Ambience`](ecs.md#sounds-from-entities) component plays an ambience (fading in and
out over 2 s) for as long as the entity has it. Tag the entity with `ScopedTo<TState>` and
the ambience belongs to that [state](states.md): leaving the state destroys the entity,
and the layers fade out.

```csharp
forest.OnEnter(Screen.Forest, () =>
{
    var entity = world.Create();
    world.Add(entity, new Ambience(content.Ambience("ambience/forest.ambience.json"), Volume: 0.7f));
    world.Add(entity, new ScopedTo<Screen>(Screen.Forest));
});
```

These need `services.AddSuperEcs()`. A frame sends the mixer a command only for what
changed, so a thousand still sounds cost nothing frame to frame. The flip side: if the queue to
the mixer is full and a move or volume change is dropped, it isn't sent again until that value
changes again.

A bad value on a component, such as a negative `Volume`, a `Far` that isn't beyond `Near` or a
position that isn't finite, throws `ArgumentOutOfRangeException` from the frame, naming the
entity and the field. The sounds already playing carry on, and once the value is fixed (or the
component removed) the next frame picks up from there without restarting or doubling anything.

## Music

```csharp
audio.Music(content.Sound("music/forest.ogg"));
audio.Music(content.Sound("music/battle.ogg"), fadeSeconds: 0.5f);
audio.Music(null);
```

One track plays at a time, looping, on the `Music` channel. A new track cross-fades from
the old one over `fadeSeconds` (2 by default), and the first track fades in over the same
time. Asking for the track that's already playing does nothing, so calling `Music` every
tick from whatever decides the mood is fine. `Music(null)` fades the music out. Asking
for a track while it is fading out, after a switch away or `Music(null)`, starts it again
from the beginning rather than bringing the fading one back.

## Channel volumes

Every sound plays on one of five channels: `Master`, `Music`, `Ambience`, `Effects` and
`Interface`. Each has a volume from 0 to 1, starting at 1, and `Master` scales all the
others, so a sound's loudness is its own volume × its channel × `Master`.

```csharp
audio.Volume(Channel.Music, 0.5f);
audio.Volume(Channel.Master, 0.8f);
float music = audio.Volume(Channel.Music);
```

A volume outside 0 to 1 throws `ArgumentOutOfRangeException` naming the channel. Saving
the player's choices is the game's job, for example with [user data](user-data.md).

## Time scale and pausing

Placed sounds follow [`GameTime.Scale`](getting-started.md#a-first-game): below 1 they play slower and lower,
matching slow motion, and at 0 they pause where they are and carry on from there when the
scale comes back. Flat sounds, music and ambiences ignore the scale, so a pause menu still
clicks and the music keeps going.

A pause is a [state](states.md), not a time scale, so a pause screen freezes world sounds
the same way it freezes the world, by setting `Scale` to 0:

```csharp
pause.OnEnter(Pause.Paused, () => time.Scale = 0);
pause.OnExit(Pause.Paused, () => time.Scale = 1);
```

The scale is read once a frame, along with the camera.

## Speakers

Playing in the window sends the sound to the computer's default sound device, through
OpenAL Soft, which ships with Super, so there's nothing to install. Sound runs on its own
thread with about 43 ms queued, so a slow frame doesn't make it stutter. Screenshots,
recordings, profiles and `HeadlessGame` runs never open the device; only live play does.

### Recording

`--record` and `GameCapture.StartRecording` put the game's sound in the video; see
[command-line.md](command-line.md#videos). In live play the speakers go quiet while a
recording runs, because the game then mixes the sound a tick at a time for the file.

### No sound device

If there's no sound device, or it won't open, the game still runs, silently, and prints
one line saying why:

```
No sound device (the default device didn't open); playing silently.
```

Everything else carries on as if it were heard: sounds still start, move and end on
time, so nothing piles up while it's silent.

If a sound fails while it plays, such as a streamed `.ogg` with a damaged part, that
sound stops, the others carry on, and the game prints a line such as
`A sound failed and stopped (1 so far): ...` with the reason. If the device itself fails
while playing, the game prints `Sound stopped: ...` once and carries on silently.

## In tests

There's no sound card in a test, and none is needed. `run.Sounds` lists what the mixer is
playing: each sound's clip path, channel, whether it loops, its gain (volume × fade ×
distance × channel × `Master`), its pan from −1 (left) to 1 (right) and how many seconds
into the clip it is. Audio moves on by exactly one tick each tick, by the mixer's own
sample clock, so fades and lengths can be checked to the tick.

```csharp
run.Ticks(1);
PlayingSound hit = Assert.Single(run.Sounds);
Assert.Equal("sounds/hit.wav", hit.Clip);
Assert.Equal(Channel.Effects, hit.Channel);
Assert.True(hit.Pan < 0);
```

A sound started in an `Update` is in `run.Sounds` after that tick, once its frame has
handed it over. It starts moving on the tick after.

## Example

The [Forest example](../examples/Forest) puts it together: rain and wind as an ambience scoped to the
forest state, music, a builder whose `Sound` component is added while hammering and removed
while walking, `Space` for a flat click, `1` to `4` to cycle the channel volumes and `P` to
pause with `Scale` 0. Its tests in [tests/Forest.Tests](../tests/Forest.Tests) check the layers, the
hammering and its pan with `run.Sounds`.

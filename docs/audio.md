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
are decoded as they play. The sample rate can be anything, because the mixer outputs
48 000 Hz and converts.

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
| `layers[].volume` | Layer volume, 0 to 1 | 1 |
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
`..` steps up a folder. Paths given to `content.Sound` and `content.Ambience` are
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

## Music

```csharp
audio.Music(content.Sound("music/forest.ogg"));
audio.Music(content.Sound("music/battle.ogg"), fadeSeconds: 0.5f);
audio.Music(null);
```

One track plays at a time, looping, on the `Music` channel. A new track cross-fades from
the old one over `fadeSeconds` (2 by default), and the first track fades in over the same
time. Asking for the track that's already playing does nothing, so calling `Music` every
tick from whatever decides the mood is fine. `Music(null)` fades the music out.

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

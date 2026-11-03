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

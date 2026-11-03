# Super design

Super is a C# game engine used as a library, with no editor. Rendition is its
renderer and stays a separate project. The other pages in `docs` describe the engine as a user
would meet it. This file records the reasons behind it, then Rendition's design.

## Engine decisions

These are decided. The game loop, window, randomness, input, game states, JSON content, cameras, picking, view memory, user data and headless runs are built;
the rest is not yet.

| Area | Choice | Why |
| --- | --- | --- |
| Projects | `Super`, `Super.Random`, optional `Super.Ecs` and `Super.Testing`, and `Rendition`, which knows nothing of the others | Each layer stays usable without those above it |
| Namespaces | `Super.Random` keeps its types in the `Super` namespace | `using Super;` is enough, and it can't hide `System.Random` |
| Random generator | xoshiro256**, with streams seeded by hashing their name with FNV-1a | Fast and well tested. `string.GetHashCode` changes between runs, so it can't be used. A test pins the sequence, because changing it breaks every recorded seed |
| Per-draw color | A tint pushed with each draw, multiplied into the model's own material | Textured models keep their textures, and no draw needs a material of its own, so the 1024 material slots can't run out however many colors a game uses |
| Falling behind | The clock drops time beyond 0.25 seconds of backlog | Trying to catch up after a stall would slow a struggling game further |
| Structure | Game code is plain classes behind `IGame`, with no built-in scheduler | The game's author owns the logic and the order it runs in |
| Time | `Update` runs on fixed ticks, `Draw` once per displayed frame and blends between ticks | Repeatable simulation, whatever the frame rate |
| Drawing | Views are plain functions of state, called every frame | Nothing to keep in sync, and loading needs no special cases |
| View memory | Kept by Super under a key and the calling line. A new key goes straight to its target, and a key not drawn for a frame is forgotten | A loaded game appears finished, with no animations replaying, and memory can't grow forever |
| Virtual tree for the world | None, unlike React and SwiftUI | Games redraw most things each frame, so comparing trees gains little and allocates |
| Virtual tree for interfaces | Yes, with SwiftUI's layout and React's hook-style state | Interfaces are trees that mostly stay the same, so skipping unchanged parts pays. They can allocate a little, and live apart from the render path |
| Interface code | CPU only, in `Super.Ui`. Rendition only sees textured quads | Layout and text run for real in headless tests, and Rendition stays usable alone |
| Interface mouse | Two speeds: hover, press and scrolling follow the mouse each frame, while clicks and drops queue and run just before `Update` | Ticks are too slow to feel right under a cursor, but anything that changes game state has to land on a tick for a seed to replay |
| Interface clipping | Per pixel in `overlay.frag`, each quad carrying a rounded clip rectangle, not scissor rectangles | One draw call, draw order kept, and rounded corners clip what's inside them |
| Interface box | Background, border, radius, opacity and margin written side by side merge into one box. Padding and frame go by order | It reads like CSS, where these never depend on order, and `UiNode` only sees one box |
| Hierarchy | Flat, with attachment by transform or bone | Colony and dungeon games have thousands of independent things and little nesting |
| Cameras | A `Camera` is a plain value the game keeps, since picking happens in `Update` and showing in `Draw` | Input can only be read in `Update`, so the camera can't belong to `Draw` alone |
| Camera kinds | Perspective by default, orthographic with `Size`, asymmetric with `Shift` in half screens | A shift keeps working when the window changes shape, which fixed frustum edges wouldn't |
| Camera boom | The game tests `BoomCamera.Arm` against its own walls and passes in the distance | Super has no physics, and this needs no delegate |
| Orthographic shading | Super gives the renderer an eye far behind the camera | Shaders keep one way of finding the view direction |
| Unchanging objects | `View.Cached`, by key and version number | Static worlds aren't described again each frame |
| Dependency injection | `Microsoft.Extensions.DependencyInjection`, resolved at startup only | Familiar, makes fakes one line, and costs nothing per frame |
| Global state | None | Many headless games can share one process |
| Randomness | `GameRandom` with named streams from one seed | A new roll in one system doesn't disturb another |
| Content | JSON read by `System.Text.Json` into records, loaded up front | No extra library, and data errors surface at load time |
| Input sampling | Per-tick snapshot rolled before `Update`; a tap inside one tick is Down and Pressed now, Released next tick | The window polls faster than the game ticks, and a short tap must not vanish |
| Input reads | Only in `Update`; `Draw`, `Ui` and `Load` throw | The screen stays a function of state, so a seed replays the same |
| Actions | The game's own enum, bound to a list of `Binding` values | Typed, no strings, and the list is plain data a settings screen can edit later |
| Game states | `States<TEnum>`, checked with a plain `Is`, not systems registered per state | There's no scheduler, so the game's author keeps the order, and a check costs nothing |
| State changes | Requested with `Set`, landing at the end of the tick after `Update` | Every system in a tick sees one state, and the Draw after it shows the new one |
| Scene-owned entities | A `ScopedTo<TEnum>` tag, cleared by `World` through `IStateHook` | Everything else carries through for free, so there's no scene to hand things across. Children aren't cascaded, since that would make every destroy cost a search |
| Asset loading | Prepared on background threads, uploaded one per frame, and a drawn model is waited for. Only the interactive window streams | A loading screen stays alive, nothing pops in, and disk speed can't change what a seed does |
| JSON content | One record per file, read into a `Catalog<T>` by `LoadAll`. Live reload runs only in the window and only from source | Every mistake is reported with its file and line at load, and a reload can't change a seeded run. See `docs/superpowers/specs/2026-11-01-json-content-design.md` |
| Saves and levels | JSON early on, expected to be replaced | Easy to read and repair during development |
| User data | `UserData` keeps one JSON file per class in the platform's per-user folder, named by `GameOptions.Id`. `Get` loads once and shares the copy; `Save` writes a `.tmp` file and moves it over the old one; a broken file is renamed `.broken` and defaults are used. `GameHost` uses a throwaway temp folder unless given one, and the window only passes the player's folder when it's visible | Players can read and fix the files, a crash mid-save can't lose settings, and a screenshot, recording or test can neither read nor change the player's own settings, so it repeats the same anywhere |
| Physics | Left out of the first version | Must run in ticks and repeat exactly, and the target games need little |
| Animation | An `Animator` per character is gameplay state. The game sets it in `Update`, and `GameHost` moves every animator from a `content.Rig` rig on one tick right after, through `Content.Animators`, which holds them weakly. It writes its blend as clip layers into an `AnimationPose`, which the renderer samples at draw time, `PoseAhead` seconds past the tick | Games never step or hand over poses themselves, dropped characters stop being run without a call, clips stay smooth at any frame rate, gameplay can wait on moments in a clip, and a seed replays the same, since an animator nothing holds can't affect the game |
| Root motion | `Rig.RootMotion` strips one joint's travel over the ground out of every clip at load and keeps it as a track per clip; the animator mixes it by layer weight into `RootMotion` each tick | The game owns where a character stands, so travel has to come back as state rather than live in the pose |
| Cutscenes | A `Cutscene` is a list of shots, moments and slow stretches built in code; the `Director` runs it on the tick clock, cueing before `Update` and placing the camera after it, and sets `GameTime.Scale` | Moments land on ticks, so a recorded film replays the same. Shots keep real time while the world slows. The camera sees where things are once they've moved |
| Time scale | `GameTime.Scale` scales `DeltaSeconds`, never the tick rate | Input and the camera stay smooth in slow motion, and a frozen world still ticks |
| Content folder | Found once by walking up from the program, behind a `Lazy` | Textures load on background threads, and a plain flag set before the path was found let a thread read no path |
| Animation states | States and actions are the game's enums. One base state loops, keeping its phase across changes; actions play once over it, optionally masked to part of the skeleton | Walk and wave, or run and shoot, need no state per combination |
| Audio mixing | Super's own `Mixer` writes 48 kHz stereo floats into a span, with a fixed 64 voice slots; OpenAL only plays the result. See [Audio mixer](#audio-mixer) | The same code runs with a device or without one, so headless runs and tests hear exactly what a player would, sample for sample |

The rule for dividing data: if gameplay cares, it's state; if only the eyes care, it's
view. Views read state and never write it, so animation can't change a simulation's
outcome. Repeatability is promised for the same build on the same kind of CPU, because
floating point results can differ between CPU types.

Rendition needs work to support this: keys passed through to per-object motion
vectors, and a check of how it copes with many thousands of draws. Skeletal animation
is in: the importer keeps skeletons and clips, and the vertex shader skins. Particles
are in too: a `ParticleEffect` of chained emitters turns a key and an age into particles with no state
from frame to frame; see [Particles](#particles).

## Audio mixer

`Mixer` (in `src/Super/Audio`) owns every playing voice. `Mix(span)` clears the span,
adds each voice into it and moves the sample clock (`Clock`, frames mixed) on by the
span's frame count. It allocates nothing: voices live in 64 slots made up front, each
with its own read buffer, and a slot keeps the reader of the last decoded clip it played
and rebinds it for the next one. A streamed clip (over 10 s) opens a decoder when it
starts, which is the one allocation allowed, and disposes it when the voice ends. A
looping clip goes back to its start with `Seek(0)`; nothing else seeks while mixing.

**Gain.** A voice's gain is its volume × its fade × distance falloff × its channel ×
Master (a voice on `Master` is scaled once). Volume and fade move per frame along
straight ramps, so a fade-in of 0.1 s is exactly 0.5 at 2400 frames. Channel volumes,
the listener and voice positions are read once per `Mix` call, and each voice's left and
right gain is worked out from them. A voice ramps those two gains in a straight line from
where the last block left them to this block's values, frame by frame, so a camera turn,
a moving source or a channel change never steps at a block edge (that would be zipper
noise). A voice's first block starts at its gains, so there is no fade from 0 beyond its
own fade-in. Once the gains are steady the ramp is flat and the output is exact.

**Pitch and rate.** Each output frame steps through the source by
`clip rate / 48000 × pitch`, times the world rate for voices that follow `Scale`, and
reads between source frames with linear interpolation. So a 44.1 kHz clip plays at its
own length and pitch. At a world rate of 0 a voice holds its place and is silent, then
carries on from the same frame. Fades still run while it's held. So a pause doesn't
click, the voice fades out over 5 ms (240 frames) while it keeps playing at its last
speed, and fades back in over 5 ms when it resumes.

**Placing.** A voice with a position is folded to mono, `(L + R) / 2`, then scaled by
the falloff and panned. A voice without one keeps its own stereo and isn't panned.

- Falloff is 1 up to `near`, 0 from `far` on, and `(near/d − near/far) / (1 − near/far)`
  in between: the inverse-distance curve, shifted and stretched to reach 0 exactly at
  `far`. With near 2 and far 50 that's 0.48 at 4 m and 0.17 at 10 m.
- Pan is the dot of the direction to the source with the listener's right,
  `normalize(cross(forward, up))`, from −1 (left) to 1 (right). It's 0 at the listener
  and when looking straight up or down.
- Equal-power panning: left `cos((pan + 1)·π/4)`, right `sin((pan + 1)·π/4)`, so the centre
  is 0.707 on each side and a sound keeps the same power as it moves across.

**Voice limit.** With all 64 slots taken, starting a voice drops the quietest voice
that isn't music or an ambience layer, judged by the volume it's heading for × falloff × channel (a stopping
voice counts at its current fade). If the new voice would be quieter than every
candidate, or every voice is protected, the new one is dropped instead and `Start` returns
`Mixer.Dropped` (−1). Music and ambience layers are never dropped, and starting one drops
the quietest other voice whatever its own volume. A dropped voice is cut at once, without a
fade, since its slot is needed now.

**Voice ids.** An id is the slot in the low 8 bits and the slot's start count above
them. An id kept after its voice ended can't reach the next sound in that slot, and calls
on unknown, ended or dropped ids do nothing.

**Bad voices.** A voice whose reader throws while mixing (say a corrupt frame far into a
streamed ogg) is ended and the rest carry on. Only the clip's reader is guarded, so any
other exception still reaches the caller. `FailedVoices` and `LastFailure` on the mixer
count these (and skipped scattered sounds) for a later task to log. Starting a voice opens its reader before the
slot is marked in use, so a clip that fails to open throws from `Start` and leaves the
slot free; a voice that was to be dropped for it keeps playing until the new one has begun.

**Ambience.** `StartAmbience(definition, volume, fadeSeconds)` makes the layers and
scattered timers of an `AmbienceDefinition` and returns an id (`NoAmbience`, −1, when
`MaxAmbiences`, 8, are already playing). `SetAmbienceVolume` and `StopAmbience` take that
id; stale or unknown ids do nothing. The mixer is built with a `RandomStream` (the game
gives it `new GameRandom(seed).Stream("audio")`, never its own), and everything random
below comes from it, so one seed gives the same ambience every run.

- Each layer is a looping voice on the Ambience channel at `layer volume × ambience volume`,
  fading in over `fadeSeconds`, starting at a random frame of its clip. A decoded clip
  just starts its cursor there; a streamed clip seeks once, as the ambience starts.
- Each scattered entry waits a random `MinSeconds..MaxSeconds` of the sample clock, then
  plays a random one of its clips once, at a random volume within its range times the
  ambience volume, pitch `1 ± PitchVariation`, and a random flat pan in −0.8..0.8 (the
  balance law `Spatial.Balance`: both ears at 1 in the centre, the far ear turned down),
  and waits again. The next wait counts from the time it was due, not from when the block
  happened to start, so the gaps don't drift. A sound starts at the start of the block
  that it is due in, so it starts early, by less than one block.
- Ambience voices count against the 64 slots and show up in `Snapshot`. Layers are
  protected from the voice limit like music; scattered sounds are ordinary voices and can
  be dropped. If a layer can't start, the layers already started are stopped and the
  error reaches the caller of `StartAmbience`. If a scattered sound can't start (say its
  clip fails to open), that one play is skipped and the next is timed as usual.
- `SetAmbienceVolume` fades the layers (and the volume new scattered sounds use) to the new
  level over the seconds given. A scattered sound already playing keeps the volume it began with.
- `StopAmbience` fades every layer out over the seconds given and starts no more
  scattered sounds. A scattered sound already playing plays to its end without a fade.
- Nothing allocates while mixing: the players are made when the mixer is built, and their
  layer and timer arrays only grow when an ambience starts, and every voice slot holds a
  decoded-clip reader from the moment the mixer is built. A scattered clip that is
  streamed (over 10 s) opens a decoder as it starts, which is the one exception.

A voice that isn't looping is removed when its clip runs out. `Stop` fades the voice to
0 over the seconds given (0 removes it at once) and removes it then.

## Audio service

`Audio` (in `src/Super/Audio`) is what games call. It owns a `Mixer` but the game thread
never touches it: every change (starts, stops, volumes, positions, channel volumes, the
listener, the world rate, ambiences) becomes an `AudioCommand` pushed into
`AudioCommands`, and the mixer side applies them in order before it mixes.

**The command ring.** `AudioCommands` is a single-producer single-consumer ring of 4096
commands, made once. The producer's pushes stay hidden until `Publish`, so a frame's
changes arrive together. Its counters only grow, so full and empty can't be confused. A
push into a full ring drops the command and counts it (`Audio.DroppedCommands`); the game
thread never waits. The size is far beyond any normal frame, and a dropped command is
recovered from where it matters. A dropped start frees its id again and returns −1; for
music, the next `Music` call with that track tries again. A dropped stop would leave a
loop, an ambience or a protected music track playing for ever. So when a start reaches a
mixer-side slot that still holds a game id, that id's stop must have been dropped, and the
old voice or ambience is stopped over 0.3 s before the slot is reused. Ids are freed and
taken last in, first out, so the next start usually lands in that slot soon after.
`TryPop` clears each slot it reads, so the ring doesn't keep an ambience definition alive.

**Ids.** `StartLoop`, `StartAmbience` and `Music` pick ids on the game thread from
preallocated pools (1024 voice ids, 64 ambience ids): a slot in the low 16 bits and the
slot's use count above, as in the mixer. They return at once, with no round trip.
Releasing an id frees it straight away, which is safe because the ring keeps order: the
stop reaches the mixer before any later start in that slot. The game side ignores calls
on ids it doesn't hold. The mixer side keeps, per slot, the game id last started there and
the mixer voice or ambience id it got, `Mixer.Dropped` or `Mixer.NoAmbience` if the
mixer refused it. A command reaches a mixer voice only if its game id matches the one
stored, so a command whose start was dropped can't reach an older sound. One-shots have
no id and no mapping. A start whose clip fails to open is counted in the mixer's
`FailedVoices` and mapped to `Dropped`, so one bad clip can't stop the mixer.

**Fades.** Loops fade in over 0.1 s, change volume over 0.1 s and fade out over 0.3 s.
Ambiences fade in, change and fade out over 2 s. Music cross-fades over `fadeSeconds`:
the old track's voice stops with that fade and the new one starts with it as its fade-in.
Music takes its ids from the same pool as loops.

**Sounds from entities.** `SoundSync` (in `src/Super.Ecs/Audio`) is an `IDrawHook` that
`AddSuperEcs` registers. After each frame's Draw it walks the entities with `Sound` and
with `Ambience` and compares them with what it sent last frame, keyed by `Entity` (id and
generation, so an id reused in the same tick is a new sound). Per entity it remembers the
loop's id and the clip, channel, near, far, volume and world position it last sent. A new
entity starts a loop, a changed clip/channel/near/far/placement stops and restarts it, a
changed position sends `Move` (an exact compare) and a changed volume sends
`ChangeVolume`; whatever wasn't seen is stopped. A still entity sends nothing, which
matters because draw hooks run every frame while the ring drains once a tick in manual
mode (`Audio.CommandsQueued` is what the test reads). Two dictionaries swap roles each
frame so nothing allocates once warm. The position is `ViewExtensions.WorldMatrix`, the
same transform, `Smoothed` blend and `Parent` chain `Models` draws with. A start that
returned id -1 is tried again next frame. A dropped `Move` or `ChangeVolume` is not resent until the
value changes again. Values are checked before `Audio` is called, so a bad one throws an
`ArgumentOutOfRangeException` naming the entity and field; a `finally` still completes the swap and
keeps entities not yet reached tracked as they were, so the next frame neither restarts nor leaks.

**Randomness.** `Audio` is built with an audio seed and makes `new GameRandom(seed)`
from it: the `"audio"` stream goes to the mixer (ambiences) and `"audio pitch"` stays on
the game thread for `Play`'s pitch variation. Two streams, because each is used by one
thread only. `GameHost` takes `int? audioSeed`; null is 1. `Game.StartWindowsGameLoop`
passes `Random.Shared.Next()` for live play only, so screenshots, recordings, profiles,
headless runs and tests all sound the same each run.

**The game loop.** In `GameHost.Draw`: the game's `Draw`, then a playing cutscene's
camera, then every `IDrawHook.AfterDraw(view)`, then `Audio.EndFrame(View.Shot,
Time.Scale)`, which pushes the listener (the camera's position, facing `Target −
Position`) and the world rate and publishes the frame's commands. In manual mode
(`Audio.Manual`: no device, or the device paused) `GameHost.Tick` calls
`Audio.Advance(TickSeconds)` before `Update`. `Advance` publishes anything still queued, as
a frame would, so ticks with no `Draw` between them lose nothing; then it drains the ring
and mixes `round(seconds × 48000)` frames into a scratch buffer in blocks of up to 2048,
carrying the fraction of a frame to the next call so a long run doesn't drift. The mix
itself is thrown away. A sound started in `Update` is handed over at that tick's `Draw` and
first mixed by the next tick's `Advance`. `Render(span)` drains then mixes one span; it's
the one call the audio thread makes.

**Reading it.** `Audio.Playing` (manual mode only, else `InvalidOperationException`)
drains what's been published, so it shows what the mixer has been told so far, and
copies `Mixer.Snapshot` into a new list. `HeadlessRun.Sounds` returns it.
`Audio.MixedFrames` is the mixer's sample clock (`Mixer.Clock`, a volatile field), the
one piece of mixer state any thread may read.

## Audio output

`DeviceOutput` (in `src/Super/Audio/DeviceOutput.cs`) plays the mix through the speakers.
`Game.StartWindowsGameLoop` calls `Audio.UseDevice(Console.Error)` just before
`window.Run`, so only live play opens a device; screenshots, recordings and profiles
return earlier and stay manual. `UseDevice` returns false and stays manual if the device
won't open, so the game loop keeps advancing the mixer per tick and voices still end on
time. It writes one line, `No sound device ({reason}); playing silently.`, where the
reason is the exception message from opening. Tests force this with `openOverride`, a
`Func<string?>` whose non-null result fails the open with that reason.

**The device.** `OpenAlSpeakers` opens the default device through OpenAL Soft
(`Silk.NET.OpenAL` with the `Silk.NET.OpenAL.Soft.Native` binaries, so nothing has to be
installed), asks for a 48 kHz context, needs `AL_EXT_float32` and queues
`AL_FORMAT_STEREO_FLOAT32` buffers on one source: 4 buffers of 512 frames, about 43 ms.
OpenAL only plays what Super mixed; none of its 3D features are used. It sits behind
`IAudioSink` (`Free`, `Queue`, `KeepPlaying`), so tests run the real audio thread into a
stand-in with no sound card.

**The audio thread.** `DeviceOutput` starts one background thread, "Super audio", above
normal priority. Each pass it asks the sink how many buffers have finished playing,
renders one 512-frame block through `Audio.Render` for each (which drains the command
ring, then mixes) and queues it, then makes sure the source is playing. It then waits
up to 2 ms (often about 15 ms on Windows) on an event that `Resume` and `Dispose` set. If
it falls behind, every processed buffer is refilled in one pass, and a source that ran
dry and stopped is played again, so it never stays stopped. It allocates nothing per
pass. After each pass it compares `Mixer.FailedVoices` with the count it last saw, and
when it grew writes `A sound failed and stopped ({count} so far): {LastFailure}` to the
log given to `UseDevice`, a plain `WriteLine` inside a `try`. Anything else that throws
on the thread is caught: it logs `Sound stopped: {message}` and stops rendering for good,
because the mixer may be half way through a change; the game carries on in silence. The
thread stays alive and keeps emptying the command ring with `Audio.Discard()`, so the
game's commands don't pile up and count as dropped. `OpenAlSpeakers.Queue` checks
`alGetError` after queueing and throws on an error, which lands here, so a device error
gives exactly one line. If opening fails part way, `Open` deletes whatever source and
buffers it made before closing the context and device.

**Handing the mixer over.** Each pass runs while holding a lock. `Pause` takes the same
lock and sets a flag, so when it returns the thread has finished its block and won't
touch the mixer until `Resume` clears it; the lock also makes each side's writes to the
mixer visible to the other. `Audio.PauseDevice()` pauses and makes `Manual` true, so
`Advance`, `Playing` and `Render` work on the calling thread and the game loop advances
the mixer per tick again. `Audio.ResumeDevice()` hands it back. Both do nothing without
a device. Pausing is the one time the game thread waits on the audio thread, for at most
one block; it's there for recording sound from live play. The speakers run dry while paused, and the
first pass after `Resume` refills them and starts the source again.

**Recording.** `Audio.StartRecording(wavPath)` makes the audio manual (pausing a device
if there is one, which `Audio.StopRecording()` resumes) and opens an `AudioRecording`, a
48 kHz stereo float WAV writer. `Advance` writes each block it mixes to it from the
scratch buffer it already uses, so nothing is allocated per tick, and the fractional
frames carried between ticks mean the file holds exactly the frames the clock mixed.
`StopRecording` goes back and fixes the header sizes. `GameCapture` starts it when the video gets its first frame
and stops it first in `StopRecording`, in a `finally` so the video is still finished; then `VideoFile.AddSound` runs
`ffmpeg -nostdin -y -i video -i wav -c:v copy -c:a aac -shortest` into a temp file beside the
video, moves it over the video and deletes the WAV. On any failure it deletes the temp
file, keeps both and writes the one-line message. Tests give it a stand-in for the
ffmpeg run.

**Closing.** `GameHost.Dispose` calls `Audio.Close()` before the services go: it stops
the thread, joins it, closes the source, buffers, context and device, then
`Mixer.StopAll()` ends every ambience and voice, so streamed clips close their readers.
`Audio.Close()` runs inside a `try`, so the services and the throwaway content folder are
still disposed if it throws.
`Mixer` itself isn't `IDisposable`; this is the only place voices still playing are
released.

# Rendition design

Rendition is a deferred, PBR renderer in modern .NET, generic enough for any 3D game.
It only does rendering and owns nothing beyond the window it draws into.

## Goals

- AAA-style architecture from the first commit: the rotating cube goes through
  the real deferred path, not a throwaway demo.
- No managed allocations per frame, measured and enforced by `--check`.
- Clean under the Vulkan validation layers.

## Non-goals (for now)

Editor, physics, audio, game logic and non-Windows platforms. Rendition keeps to
rendering. Game-facing features belong in Super.

## Decisions

| Area | Choice | Why |
| --- | --- | --- |
| Runtime | .NET 10, unsafe code enabled | Current LTS; direct control over memory |
| Graphics API | Vulkan 1.3 minimum | Dynamic rendering and synchronization2 remove boilerplate; leaves Linux open |
| Bindings | Silk.NET.Vulkan | Thin, no per-call allocations |
| Window | Silk.NET.GLFW | Small and portable; the renderer never depends on it directly |
| Shaders | GLSL compiled to SPIR-V by `glslc` at build time | No compiler in the shipped game; a game compiles its custom shaders the same way |
| Math | System.Numerics | SIMD-accelerated value types |
| Model import | Separate tool using Assimp, writing our own `.rmodel` | The game loads a fast flat file and never ships an FBX or glTF parser |
| Textures and materials | One global descriptor set indexed by material | Adding materials adds no binding work per draw |
| Skinning | Four joints and weights per vertex, packed as bytes. Each frame's bone matrices go in one storage buffer (set 0, binding 1, up to `Renderer.MaxBones` = 16,384), and a draw's push constants hold its first bone, or -1 when it has none | Unskinned models use the same shaders and vertex format, and many characters cost one upload |

## Frame structure

```
ShadowPass    scene geometry   -> depth from the key light (2048 x 2048, up to 4 cascades)
GBufferPass   scene geometry   -> albedo+metallic, normal+roughness+AO, emissive+clearcoat, depth
AoPass        fullscreen       -> ambient occlusion from depth and normals
AoBlurPass    fullscreen       -> the same with the grain averaged away
LightingPass  fullscreen       -> HDR colour (Cook-Torrance GGX PBR, traced reflections, probes)
ParticlePass  camera squares   -> soft puffs blended over HDR colour, back to front
TransparentPass  glass         -> blended over HDR colour, forward shaded
TaaPass       fullscreen       -> this frame blended with history, then copied to history
BloomPass     fullscreen x 9   -> glow, by shrinking and regrowing the image
TonemapPass   fullscreen       -> display colour (ACES filmic, colour grade)
FinishPass    fullscreen       -> swapchain, with sharpening, vignette, grain and dither
```

G-buffer layout:

| Target | Format | Contents |
| --- | --- | --- |
| 0 | `R8G8B8A8_SRGB` | base colour, metallic |
| 1 | `R16G16B16A16_SFLOAT` | octahedral normal (xy), roughness, AO |
| 2 | `R16G16B16A16_SFLOAT` | emissive colour, clearcoat (or minus the translucency, for foliage) |
| depth | `D32_SFLOAT` | reversed-Z, position is reconstructed from it |

Games talk to the renderer with plain structs: `Renderer.Render(in FrameView,
ReadOnlySpan<DrawItem>)`. Meshes, textures and materials are created up front and
referenced by handles. A `DrawItem` is an index range of a mesh plus a material,
so one model shares a vertex buffer across its parts.

Models and meshes from `CreateModel` and `CreateDrawItem` also carry a bounding sphere
and up to four simpler versions, made while loading. Vertices that share a grid cell and
face the same way merge into one of them, so a simpler version is only another index
range, with no new vertices. Each frame, the camera and the shadow map each skip what
they can't see and pick the simplest version that strays by at most 1 pixel on screen
or 2 texels in the shadow map. `DrawItem`s written by hand have neither, and always draw
in full.

Model files often link only colour and normal maps. Each model has an
`assets/<name>.materials.json` that connects the remaining textures and sets
material values. The surface texture holds metalness in red, roughness in green
and opacity in alpha.

A cut-out's opacity is the colour texture's alpha times the colour's alpha times the
surface texture's alpha. Asset packs and glTF keep leaf alpha in the colour texture, so
it works without a surface texture. The importer fails when a cutoff can't cut: no
pixel has alpha below it, or none has alpha above it. A `foliage` rule is for a tree's
leaves. It points the normals out of the crown, so the crown is lit as one shape and
not as a pile of separate cards, draws both sides, and keeps back faces from flipping
their normal. The lighting pass then lets sunlight through the leaves from behind.

Copies are drawn instanced. `SceneDraws.Batch` sorts a pass's opaque and shadow draws
by mesh, part, material, tint, ghost and swim, and each run of draws that match is one
`vkCmdDrawIndexed`. Skinned draws never match. The order is safe because the depth test
decides what shows. Glass keeps its order and only joins neighbours. Each copy's model
matrix goes into the frame's `InstanceBuffer` (set 0, binding 2, `instances.glsl`), and
the vertex shaders read it by `gl_InstanceIndex`. The buffer grows before a frame is
recorded, sized from the selected draws. The push constants hold only what copies
share. Coplanar surfaces can trade places in a depth tie, which moved 0.03% of the
pixels in Archer by at most 3 of 255.

A draw's tint rides in its push constants and multiplies the material's colour in
`writeSurface()`, so every surface shader gets it, and copies of one textured model can
each have their own hue.

A ghost rides in the same push constants, as a colour and an opacity, with opacity 0
meaning no ghost. `writeSurface()` then keeps only the surface's normal and writes one
matte colour with a little of it as emission (`GHOST_GLOW` in `material.glsl`), so a
ghost reads the same over any material and in shadow. A ghost picks its pass by its own
opacity, not its material's: solid ghosts go to the G-buffer and see-through ones to
the glass pass, where `transparent.frag` does the same swap. `SceneDraws.Belongs`
decides this, and keeps ghosts out of the shadow maps.

glTF files (`.glb`, `.gltf`) carry more of this themselves, so the importer reads their
embedded images, the link to their metal and roughness texture, and their colour,
metal and roughness values. glTF keeps metalness in blue, so the importer moves it to
red. A rule in the materials file still wins over the model file. The renderer has one
UV set, and the importer keeps the first. That's fine for the adventurer model, whose four
sets are identical.

Shadows blur with distance from the object casting them: the lighting pass finds
how far away the blocker is, then widens its sampling to match. The game says
which area the shadow map covers through `FrameView.Shadows`. A smaller area gives
sharper shadows, so the sample fits it to the model. A wide outdoor scene asks for up
to four cascades: each covers three times the radius of the one before, all centred on
the target, and the lighting pass picks the first cascade a point falls inside. The
shadow pass draws each cascade into its own layer of the same depth image, with its
own culling and detail choice. Anything outside the last cascade, in depth as well as
sideways, counts as lit.

Two frames can be in flight. Render targets are shared between frames, and
pipeline barriers order the access; per-frame state is limited to command
buffers, sync objects and the uniform buffer.

## Environment lighting

`Renderer.SetEnvironment` describes the surroundings as colour bands plus up to
eight glowing panels, and a compute shader bakes that into two cube maps. One holds
a blurred reflection per roughness step and the other the soft light on matte
surfaces. Lighting then needs a single lookup per pixel. It's generated rather than
loaded from a photo, so a game can describe its own space and the softboxes can
line up with the lights. Baking waits for the GPU, so it belongs in loading.

Panels appear only in reflections. The direct lights already provide their diffuse
light, and baking it too would count that light twice and overexpose the scene.

`FrameView.ShowEnvironment` draws the reflection cube, times `EnvironmentIntensity`,
as the background instead of the flat `BackgroundRadiance`, and the distance fade then
mixes toward the cube in the direction of the view. A far hill melts into the sky just
behind it rather than into one grey. Super's `view.Sky` turns it on; the studio and
underwater views leave it off.

Super keeps one set of bands, from water first, then the sky, then the plain studio, and
bakes them again only when they change, so a steady sky costs nothing per frame but a
sky animated every frame would rebake every frame.

## Reflections

A reflection comes from the most accurate source that can answer. First, a ray walks
through the depth buffer (screen-space reflection) and reads what it hits from the
previous finished frame. Slightly rough surfaces tilt the ray at random per pixel and
per frame, and temporal anti-aliasing averages that into a blur. Rays that leave the
screen fall back to a reflection probe, and without a probe to the studio.

A probe is a cube map captured from one spot by `Renderer.CaptureReflectionProbe`,
using the normal scene passes, then blurred per roughness step. It waits for the GPU,
so it belongs in loading. Each probe has a box for the space it describes, and a
reflected ray runs on to that box before the lookup, so reflections line up with where
they really land instead of sliding as the camera moves. Up to four probes blend at
their box edges. The sample leaves the car out of its probe, because the probe sits
inside the car, and captures only the floor and the shadow on it.

## Paint and glass

Clearcoat adds a second, very smooth reflection on top of the surface, like the
lacquer on a car, and dims what's underneath by the light it reflects. Its roughness
and strength of reflection are fixed in `lighting.frag`, so the G-buffer stores only
how much coat a surface has.

The G-buffer holds one surface per pixel, so glass can't go through it. Glass is
shaded as it's drawn, after lighting, and tests against the depth of the opaque scene
without writing to it. Its reflection stays at full strength however clear the glass
is, and only its tint is scaled by opacity. The colour is premultiplied so the
blend can express that.

## Particles

A particle is a plain 40-byte struct: middle, width, linear colour and opacity, a turn
and a variant that picks its lumpy outline. `FrameView.Particles` hands the renderer
up to `Particle.MaxPerFrame` (32,768) a frame; more are dropped. `ParticlePass` sorts
them furthest first along the view direction on the CPU, into the frame's own
host-visible vertex buffer, with scratch arrays made once, so nothing allocates. Each
particle is one instance of a six-vertex square facing the camera.

The pass runs after lighting and before glass, so glass in front of dust still covers
it. It has no depth attachment. The fragment shader reads the depth buffer itself and
thins a puff over half its width as it nears the surface behind, so dust melts into the
ground instead of showing a hard line where it crosses it. It also thins puffs the
camera is about to pass through.

Lighting is cheap but matches the scene. Each corner of the square looks up the
shadow map in the vertex shader, five filtered taps, so a puff is shaded where a tree's
shadow falls on it without any per-pixel shadow cost. The fragment shader lights a
pseudo-sphere normal, bent toward the camera, with the sky's irradiance and wrapped
sunlight, so a puff is brighter on its sunny side but never dark like a solid ball.
Haze and the underwater fade are the same as for surfaces. Colour is premultiplied.

On the Super side, a `ParticleEmitter` is an immutable list of nodes, built like a
Unity or Unreal particle graph but always a chain, never a graph with branches. The
constructor sorts the nodes into stages and every particle runs through them in a
fixed order: birth time, spawn shape, start speeds (summed), forces, motions (in
turn), then looks (in turn, multiplying from one metre, white and solid). Every node
must be a closed-form function of the particle's age, because nothing is stepped
or kept between frames. That's why forces can't be arbitrary: drag, gravity and wind
fold into one drag `k` and one steady pull `g`, wind adding `k` times its speed, so
travel is `v(1 - e^-kt)/k` and the pull adds `g(t - that)/k`. A new force has to fit
that form; anything else goes in as a motion node that moves the finished position.

Randomness comes from an integer hash. The key and the emitter's place in its
`ParticleEffect` make a seed, mixed with the emitter's own `Seed` and the particle's
number. Each node rolls its own dice, salted by its place in the list, so adding a
node doesn't change what the nodes before it roll. An effect is a list of emitters,
each with a delay and an offset; the delay is taken off the age. Spawn shapes, speeds
and offsets go through the call's transform; the from/to call stretches only the x of
shapes and offsets, so speeds keep their size. Gravity, wind, sizes and colours stay
in the world. An emitter drawn alone turns into an effect once and keeps it, so a
frame allocates nothing.

## Custom shaders

A surface shader is a game's own fragment shader for the G-buffer pass. It decides
what a surface is made of (colour, metalness, roughness, normal, glow), and lighting,
shadows and post-processing treat the result like any other surface. A shader can't
change how light behaves, which keeps every material consistent and keeps the benefit
of deferred rendering.

```glsl
#version 460
#extension GL_GOOGLE_include_directive : require
#include "surface.glsl"

void main() {
    Surface surface = materialSurface();
    surface.emissive += materials[draw.material].shaderParameters[0].rgb * sin(frame.time);
    writeSurface(surface);
}
```

`materialSurface()` gives the surface the material's textures and values describe, so
a shader only changes what it cares about. A shader can also read `inUv`, `inPosition`
and `frame`. Each material carries eight `vec4`s in `shaderParameters`, four textures
read with `shaderTexture(slot, uv)` and four texture arrays read with
`shaderLayer(slot, uv, layer)`, where a fractional layer blends two layers. Unset slots
read white. A texture array comes from `Renderer.CreateTextureArray`, which stacks
same-sized images with the same mip count. Terrain uses this: one array of ground
colours, one of their normal maps, a splat texture in a slot and thresholds in the
parameters, as `examples/Terrain/Shaders/terrain.frag` shows. A game compiles
the shader with `glslc`, with `src/Rendition/Shaders` on the include path, and passes
the result to `Renderer.CreateSurfaceShader`, then sets `Material.Shader`. Importing
`src/Shaders.targets` does the compiling. `src/Rendition.Sample/Shaders/glow_bands.frag`
is a working example, shown by `--glow`.

Each shader adds a pair of pipelines to the G-buffer pass, built while loading. Picking
one in `Render` is an array lookup, so it allocates nothing.

## Post-processing

Ambient occlusion darkens creases and contact areas by checking how much of the
nearby screen rises above each point. It reduces environment light, plus the
direct lights that have no shadow map of their own. It only knows what the camera
sees, so it can't tell that a car's underside hangs over the floor. To cover that, the
same pass searches a wide area of the shadow map for anything above an upward-facing
point. The result is a soft shadow under and around objects, and it costs no extra
render pass. It works only inside the area the shadow map covers.

Bloom has no brightness threshold. Every pixel gives away a small share of its
light, so only really bright things show a visible glow. That's physically closer
to how a lens scatters light, and nothing pops as a surface crosses a threshold.

Tonemapping uses Stephen Hill's fit of the ACES film transform rather than the
simpler Narkowicz curve. It works in ACES's own colour space, so very bright colours
fade towards white instead of shifting hue. `ACES_EXPOSURE` in `tonemap.frag` keeps
the scene as bright as the lighting was balanced for.

Anti-aliasing is temporal. The camera shifts by less than a pixel each frame, and each
pixel blends with where that surface was last frame, so many samples build up per
pixel. It also smooths the grain from soft shadows, ambient occlusion and rough
reflections. Colours are blended in a squeezed range so bright pixels don't dominate,
and history is pulled back into the range of nearby colours so old images don't trail
behind. It runs before bloom and tonemapping, on HDR colour. It replaced FXAA, which
couldn't remove shimmer.

`FrameView.Post` holds the look of the image: exposure, contrast and saturation in the
tonemap pass, then sharpening, vignette and film grain in the finish pass. Grain and
dither also hide the steps an 8-bit screen would show in slow gradients.

## Verification without a window

`--check`, `--screenshot` and `--record` create the GLFW window hidden, so they never
take focus.
Vulkan still renders and presents to it. `Renderer.Capture` copies the finished
frame into a CPU-readable buffer that exists only for that call, so `Render` pays
nothing but a null check for the feature.

Videos are built from those same frames. `GameCapture` pipes them as raw RGBA to an
ffmpeg process, and the game steps a fixed `1 / fps` per frame while recording, so
the output doesn't depend on how fast the machine is. Waiting on the GPU for each
frame is slow, but it's only paid while capturing.

## Resource lifetime

Meshes and textures are made during play, so neither creating nor destroying one
may wait for the GPU.

Uploads ride the next frame. `CreateMesh`, `CreateDrawItem`, `CreateTexture` and
`CreateTextureArray` copy their bytes into a staging buffer and queue the copy in
`UploadQueue`. The next `Render` records every queued copy, with its layout
transitions, at the start of its command buffer, so a resource made before a
`Render` is drawable in that same frame. The staging buffer is retired through the
destroy queue below. `CreateSurfaceShader`, `SetEnvironment`, `SetOverlayAtlas` and
probe capture still wait, and say so: they're for loading screens.

Destroying returns at once. The resource is added to the destroy slot of the frame
most recently submitted, and released when a later `Render` has waited on that
frame's fence, so no frame in flight can still read it. Destroying something whose
upload is still queued cancels the copy first, so the copy never runs into freed
memory. A released texture's descriptor slot is rewritten to the white texture, so
whatever takes the slot next starts from something harmless; a material that still
names a destroyed texture must not be drawn, which Super's `Content` enforces by
refusing to unload a texture that a live mesh names.

Handles are recycled through a free list per kind, and each kind remembers which
slots are on their way out, so destroying a handle twice fails rather than freeing
whatever reused the slot. Nothing here allocates once the lists have grown.

Device memory is sub-allocated from 64 MB blocks in `GpuMemory`, one set of pools
per memory type, kept apart for buffers and images and for host-visible memory.
Anything larger than a block gets its own allocation.

## Known simplifications to revisit

- Textures are uncompressed RGBA8 with mips. Block compression (BC7) would use
  far less GPU memory.
- The table holds at most 1024 textures, 1024 materials and 256 texture arrays.
- Transparent draws aren't sorted, so overlapping glass can blend in the wrong order.
  Glass also casts no shadow and shows no refraction.
- Particles cast no shadow, don't show in probes or reflections, and give no motion
  vectors, so fast puffs can smear slightly under temporal anti-aliasing. Underwater
  they fade with distance but miss the shafts of light. Particles and glass are sorted
  separately, so a puff behind glass is drawn under it correctly but a puff in front of
  glass is covered by it.
- The car's surface map channels are an educated guess, because the model has no
  documentation. The paint's metalness is set to 0.2 by eye. Its normal maps were
  measured: they use the green direction the renderer expects.
- The adventurer model doesn't use its normal map. The map creases the skin, most
  visibly on the face, whichever way up it's read, and adds almost nothing to the
  clothing, so its materials file sets `noNormalMap`.
- The importer reads glTF's `MASK` cut-outs, but not its emissive maps, glass or
  two-sided flags yet. Set those in the materials file.
- Foliage translucency is a constant in `surface.glsl`, and a material can't have both
  foliage and clearcoat, since they share a G-buffer channel.
- Clips are sampled at 30 frames a second on import and blended linearly between
  frames. Root motion only carries travel over the ground, not turns.
- The adventurer model has 929,000 triangles, which suits a showcase more than a game. A
  game character would need a reduced version and levels of detail.
- Shadow cascades are centred on the camera's target rather than fitted to the view
  frustum, so some of each map covers ground behind the camera.
- Only directional lights so far. Point and spot lights come later.
- Only one light casts shadows, so the others shine through objects, held back only
  by ambient occlusion.
- The environment is treated as infinitely far away, and ambient occlusion is all
  that blocks it.
- The ambient occlusion radius and strength, and the bloom strength, are constants in
  their shaders. A game will want to set them per scene.
- Temporal anti-aliasing follows the camera only, with no motion per object, so moving
  objects rely on the colour clamp and can show rough edges. Skinned models, now
  supported, will need motion vectors from the G-buffer pass too.
- Screen-space reflections only know what is on screen, and read last frame's image.
  Glass still reflects the studio, not probes or traced rays.
- Probes are captured once and don't update. They affect reflections only, not
  soft diffuse light, and a scene gets at most four.
- Custom shaders replace only the G-buffer fragment shader. Shadows still use the
  material's standard cut-out, so a shader that discards extra pixels leaves the full
  shadow. Glass can't use one, and vertices can't be moved by one yet.

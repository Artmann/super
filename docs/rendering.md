# Rendering

Super draws with [Rendition](../src/Rendition/README.md), a deferred, physically based
Vulkan renderer. You don't call it directly: `Draw` describes the world on a `View`, and
Super hands that to Rendition each frame.

What it does for every game, with no setup:

- Cook-Torrance GGX shading with up to eight directional lights, and up to 1,024 point
  and spot lights with soft shadows. Rough surfaces keep the light that bounces between
  their tiny facets, so worn iron and brushed steel stay as bright as real metal does
- Soft, contact-hardening shadows, in up to four cascades, with screen-space contact shadows where things meet the ground
- A generated environment that gives surfaces reflections and soft fill light
- [Bounced light](#bounced-light) from a volume of light probes, which doesn't leak through walls
- Screen-space reflections and box-corrected [reflection probes](#reflections), which glass reflects too
- Temporal anti-aliasing, [ambient occlusion](#ambient-occlusion) (with light bounced back from the surface's colour) and bloom
- [Glass](#glass) that bends and frosts what's behind it, car paint clearcoat and surfaces that glow
- [Height maps](#height-and-detail-maps) that sink mortar and grooves into a flat surface, and detail maps for fine grain
- Skin, wax and jade that [let light spread under the surface](#skin-and-wax)
- Soft, lit [particles](views.md#particles) for dust and smoke
- Culling and simpler versions of distant models, for the camera and for shadows
- AgX or ACES filmic tonemapping, white balance, colour grading, sharpening, vignette, grain and dithering

## Graphics settings

Everything above is on by default, and vsync keeps the frame rate at the display's, often 60.
A game that must run fast on modest hardware turns off what it doesn't need:

```csharp
Game.StartWindowsGameLoop<MyGame>(new GameOptions
{
    Title = "My game",
    Graphics = new Graphics { AmbientOcclusion = false, Reflections = false, MaxFramesPerSecond = 144 },
});
```

`Graphics.Fastest()` turns every effect and vsync off (but keeps the materials), a start to turn back on only what
the game needs. Ask for `Graphics` in a constructor to change it while the game runs; the
next frame follows, so it can back an options menu.

| Setting | Off, or set |
|---|---|
| `Vsync` | Frames come as fast as the machine draws them, and fast movement may tear. `--no-vsync` turns it off too |
| `MaxFramesPerSecond` | The most frames drawn a second. 0, the default, is no limit. It saves power, and with vsync off it holds a steady rate |
| `SunShadows` | Nothing is drawn into the sun's shadow maps, often the biggest saving. Ambient occlusion still darkens nooks |
| `ShadowResolution` | How many texels across each of the sun's shadow maps is: 1024, 2048 (the default) or 4096. Each step up sharpens shadow edges for four times the memory. A change rebuilds the maps before the next frame, which waits for the GPU once |
| `RenderScale` | How large the scene is drawn against the window, from 0.5 to 2 each way. 1, the default, draws it at the window's size with nothing resized. At 2 the scene has four times the pixels and is shrunk to fit, which smooths grass blades, railings and other thin things, for about four times the lighting and effects work. At 0.5 it has a quarter of the pixels and is stretched, which is much faster and softer. Below 1, meshes also switch to their simpler versions sooner, since they cover fewer scene pixels. A scene larger than the GPU can draw is shrunk to fit, keeping its shape. The interface, screenshots and recordings stay at the window's size. Anything else is refused. `--render-scale` sets it too. A change rebuilds the scene's targets before the next frame, which waits for the GPU once and restarts anti-aliasing |
| `ContactShadows` | No thin dark line where things meet the ground in sunlight, only what the shadow maps show. `view.Shadows`' `contact` only changes [how far they look](#shadows) |
| `LightShadows` | Point and spot lights shine through walls |
| `AmbientOcclusion` | Corners and creases are as bright as open ground, and shiny things in them reflect the sky as brightly. `view.AmbientOcclusion` only changes [how far and how dark](#ambient-occlusion) |
| `MultiscatterSpecular` | Rough surfaces lose the light that bounces between their tiny facets, so rough metals such as worn iron come out darker than they should, by up to half at full roughness. It costs almost nothing, so leave it on unless you're comparing |
| `LightProbes` | No [bounced light](#bounced-light): shade takes its soft light from the sky alone, as if no `view.LightProbes` were set, and no probes are captured |
| `Reflections` | Shiny surfaces, water and glass reflect only the sky: not what's on screen, and no [reflection probes](#reflections) are photographed |
| `AntiAliasing` | Edges show their pixels, and moving things stay sharp |
| `Bloom` | No glow round bright things. `PostSettings.Bloom` only changes how much there is |
| `Fog` | Clear air, whatever `SkySettings.Fog` asks for. [Haze](#the-sky) stays, since it isn't fog. `SkySettings.Fog` only changes [how thick it is](#fog) |
| `AutoExposure` | Views that ask for [auto exposure](#auto-exposure) keep the fixed `PostSettings.Exposure` instead, as if they hadn't asked. It saves a compute pass over the frame |
| `DepthOfField` | Everything is sharp, even in views that open the lens with `PostSettings.Aperture`. It saves three passes, two of them at half size. See [focus](#focus) |
| `Decals` | No [decals](views.md#decals): surfaces show as they are, without the grime and stains `view.Decal` lays on them. It saves a copy of the normal target and a pass over the decals' pixels |
| `Materials` | Every surface is the same light grey, matte clay, shaped only by its mesh, so you can judge the lighting and the models without textures in the way. Glows still glow, cut-outs still cut and glass stays see-through. `--no-materials` turns it off too |

`Graphics.Fastest()` leaves `RenderScale` at 1 too, since a smaller picture is a change the
player can see, not only a saving. A game for slow GPUs on big screens sets it to 0.75 or 0.5
itself, or offers it in an options menu.

`Materials` is for looking, not for speed, so `Graphics.Fastest()` leaves it on. A key that
flips it while the game runs makes a handy lighting check:

```csharp
internal sealed class MyGame(Graphics graphics, Input input) : IGame
{
    public void Update(GameTime time)
    {
        if (input.Pressed(Key.M))
        {
            graphics.Materials = !graphics.Materials;
        }
    }
}
```

![The Shapes example with its materials, and with Materials off](media/graphics-materials.webp)

An effect turned off skips its work on the GPU, not only its look. In the Crypt, the
GPU time of a frame halves:

![Crypt with every effect on, 4.4 ms of GPU a frame, and with Graphics.Fastest, 2.0 ms](media/graphics-fastest.webp)

Screenshots, recordings and profiles keep vsync and ignore the frame limit, so they come
out the same on every machine, but they do follow the effect switches. Turning
`AntiAliasing` on or off waits for the GPU to finish once, so do it from a menu rather than
every frame.

## Lights

```csharp
view.Sun(direction: new Vector3(1f, 2f, 1f), color: new Vector3(3f));
view.Sun(direction: new Vector3(-1f, 0.5f, -1f), color: new Vector3(0.4f, 0.5f, 0.8f));
```

`direction` points towards the light. Colours are linear, so a bright light goes well
past 1. One sun casts the shadows: the first, unless you pick another with
`shadows: true`:

```csharp
view.Sun(new Vector3(-1f, 0.5f, -1f), new Vector3(0.4f, 0.5f, 0.8f));
view.Sun(new Vector3(1f, 2f, 1f), new Vector3(3f), shadows: true);
```

Marking two suns in one frame throws.

### Point and spot lights

![The Crypt example: torches, candelabras, a lantern's beam and 126 candles](media/crypt.webp)

```csharp
view.PointLight(torch.Id, torch.Flame, color: new Vector3(3.2f, 1.5f, 0.5f), range: 9f, radius: 0.5f);
view.PointLight(candle.Id, candle.Wick, new Vector3(0.12f, 0.06f, 0.02f), range: 1.4f, shadows: false);
view.SpotLight(lantern.Id, lantern.Position, direction: -Vector3.UnitY, new Vector3(6f, 5.5f, 4.5f),
    range: 7f, innerAngle: 0.25f, outerAngle: 0.4f);
```

A point light shines every way from a spot, like a candle or a torch, and a spot light
shines in a cone, like a lantern's beam. The colour is how bright the light is a metre
away. It dims with the square of the distance and fades smoothly to nothing at the
range, so a small range is cheaper and keeps a candle's light on its own table. A spot
is full inside its inner angle and fades out by its outer one. Both angles are measured
from the direction, in radians, and the outer one must be under a right angle.

A light also has a radius, the size of its glowing part, 0.1 m unless you pass
`radius:`. No surface gets much brighter than it would be that far from the light. On
its own, the square of the distance makes a torch a hand from a wall blow the stone out
white while the room stays dim. A bigger radius calms the wall but leaves the light
just as bright a metre away and beyond, so raise it for lights in brackets, sconces and
lamps against walls. The Crypt's torches use 0.5. A bigger light also casts softer
shadows. The radius must be less than the range.

![A torch on a pillar with the default radius of 0.1 and with 0.5](media/torch-radius.webp)

Draw lights in `Draw` like everything else, each frame, so a torch can flicker by
changing its colour. The key names the light from frame to frame, the same as a model's
key, and keeps its shadow steady as the camera moves. Use the ID of the thing holding it.

Lights cast soft shadows unless `shadows: false` is passed. Shadows are sharp where an
object touches the ground and blur further away, as the sun's do. All the lights share
one shadow atlas, and a light that is bigger on screen gets a sharper shadow. A point
light costs six times as much shadow as a spot. When the atlas is full, the lights that
are smallest on screen lose their shadows. Shadows are what cost the most, so leave
them off on small lights, such as rows of candles.

A frame can have up to 1,024 lights, and one more throws. Lights are culled to what the
camera sees, and each part of the screen only works through the lights that reach it, so
a frame can have hundreds of small lights. 256 can reach any one spot on the screen,
and more than that are dropped there. The `Crypt` example has four torches, five
candelabras, a lantern, a beam from a grate and 126 candles, about 150 lights.

Point and spot lights light models, glass and [particles](views.md#particles). Glass and
particles get no shadows from them. They aren't seen in reflection probes.

## Shadows

```csharp
view.Shadows(40f, cascades: 3);
```

The first number is the radius of the sharpest shadow map, in metres. Each extra cascade
covers three times the radius of the one before. The cascades are laid along what the
camera sees: the sharpest one starts at the camera, and each next one picks up where the
last ends, so no part of a map is spent behind the camera. A cascade keeps its size as the
camera turns and moves in whole shadow texels, so shadow edges don't shimmer or crawl.

Where two cascades meet, points hand over to the coarser one gradually, so there's no line
where the sharpness changes. Shadows fade out over the last part of the furthest cascade
instead of stopping at an edge.

An orthographic camera centres every cascade on its target instead, and widens its
shadows to cover the screen.

Things out of frame still cast into it. A tree behind the camera or off to the side, up-sun
of what you see, throws its shade into the picture, up to six cascade radii away.

```csharp
view.Shadows(7f, cascades: 3, sunSize: 0.02f);
```

`sunSize` is how wide the sun looks, in radians, from 0 to 0.5. It sets how fast a shadow
blurs with distance from what casts it. The default of 0.12 is a soft, hazy sun that keeps
shadows gentle. The real sun is about 0.01, and something near that lets a canopy several
metres up throw crisp dappled light, where the default blurs it into one soft patch.
0 gives hard edges everywhere.

Each map is 2048 texels across unless `Graphics.ShadowResolution` asks for 1024 or 4096, so
a 7 m radius gives texels about 7 mm wide. That's too coarse for the last centimetre where
a barrel stands on the floor, so the sun also has contact shadows. Each pixel looks towards
the sun through what the camera sees, and darkens where something is in the way, which
draws a thin dark line where things touch the ground:

```csharp
view.Shadows(7f, cascades: 3, sunSize: 0.02f, contact: 0.3f);
```

`contact` is how far each pixel looks, in metres. The default of 0.25 suits barrels, crates
and people; a longer one reaches under bigger things and costs no more. 0 turns them off, as
`Graphics.ContactShadows` does. They're drawn only inside the first cascade's radius, fading
out over its last tenth, since further away a centimetre is less than a pixel. They can only
see what's on screen, so something just out of frame or hidden behind another object casts
none. Something more than 15 cm in front of a surface never counts, so a near object puts no
dark outline on the wall behind it. Materials marked `foliage` get none, because the camera
only sees the front leaf.

## Ambient occlusion

```csharp
view.AmbientOcclusion(radius: 1.2f, strength: 0.7f);
```

Corners, creases and the ground under things get less of the soft light from the sky,
because the surfaces around them hide part of it. `radius` is how far, in metres, a point
looks for those surfaces, and must be above 0. `strength`, from 0 to 1, is how dark they
make it: 0 turns the darkening off and 1 is all of it. The defaults, a radius of 0.6 and a
strength of 0.9, suit rooms and streets seen from head height. A wider radius darkens
whole alcoves and the space under a cart, at the cost of losing fine creases.

A point in a corner also sees the sky only through the opening, so the soft light it gets
comes from that way rather than straight off the surface. Reflections of the sky fade in
the same corners, most on rough surfaces, so a gap behind a barrel doesn't shine.
It only knows what the camera sees, plus what stands over a point in the sun's shadow map.

## Bounced light

```csharp
view.LightProbes(min: new(-2f, -0.5f, -9f), max: new(9f, 5f, 6f), spacing: 1f);
```

Sunlight that lands on a brick wall doesn't stop there: some of it bounces off and lights
the ground and everything near it, tinted the colour of the brick. Without bounced light, shade
gets its soft light from the sky alone, so a yard in shadow looks blue and flat. A light probe
volume fills a box with a grid of probes, `spacing` metres apart, that each record the light
reaching them from every direction. Surfaces inside the box take their soft light from the
probes round them instead of from the sky, so the shade under a sunlit wall takes on its
warmth, and the inside of a closed room stays dark however bright the sky is.

Give the box a little room around what it lights, and keep the spacing near the size of the
smallest space that should be lit differently: 1 metre suits a yard or a room, 0.5 a corridor.
The box may hold at most 4,096 probes, so 1 metre spacing covers about 15 by 15 by 15 metres;
a bigger box throws when it is set. `spacing` must be more than zero, and `max` above `min` on
every axis.

Each probe also notes how far away the surfaces it sees are, so a point behind a wall from a
probe gets nothing from it, and light doesn't leak into a room from the probes outside. A
probe that ends up inside a wall or under the ground sees mostly the backs of surfaces, and
is left out. Outside the box, or where no probe can see a point, the soft light comes from
the sky as before. Reflections don't change; probes give only the soft, diffuse light.

The probes are drawn by the renderer itself, from each probe's position, with the sun and the
sky but without point and spot lights. That happens over the first frames after the box is set,
64 probes a frame, and then once more, now lit by the first round, so light bounces twice. A
change to the sun's direction or colour, the sky, the sun's shadow softness or cascades or the
`Materials` switch starts it again (the camera doesn't: the sun's shadow is framed on the box), drawing over the old probes in place, so for a few frames
old and new probes mix and the new ones have only one bounce. A change to the box throws the
old probes away, and the sky lights the scene until the first round is done. Moving or adding objects doesn't, so
probes suit what stands still. Each frame that draws probes takes longer, so a sun that moves
every frame keeps them drawing; set it once and leave it, or turn `Graphics.LightProbes` off.

## Reflections

```csharp
view.ReflectionProbe(key: 1, position: new(3.5f, 1.6f, -1.5f), boxMin: new(-3f, -1f, -10f), boxMax: new(10f, 6f, 7f));
```

Shiny surfaces first mirror what's on screen. What they'd see off the edge of the screen, and
everything glass sees, comes from the sky unless a reflection probe covers it. A reflection
probe is a photograph of the scene in every direction from one spot, so a window in a yard
reflects the walls and the house opposite rather than a sky it can't see. Put it at about head
height in the middle of the space, and make the box match the space's walls, ground and
roof: a reflected ray runs on to the box before it looks the probe up, so what it shows lands
where it would really be and doesn't slide as the camera moves. Towards the sides of its box a
probe fades out over `blend` metres, 0.5 by default, into the sky or the next probe.

The key names the probe across frames, so place it every frame like a light. It is
photographed the first frame it's placed and again only when it moves, its box changes or the
lighting does: the sun's direction or colour, the sky, the shadow softness or cascades, the
ambient occlusion settings, the `SunShadows`, `Materials` or `AmbientOcclusion` switches. Moving
the camera doesn't, because the photograph frames the sun's shadow on the probe's own box, and
nor does moving or adding objects, so probes suit what stands still. While the [light
probes](#bounced-light) fill in at the start, a probe can be photographed two or three times.
Each photograph waits for the GPU and takes a moment, so a sun that moves every frame keeps
them drawing. Point and spot lights aren't in the photograph, so a lantern's glow doesn't show in
a reflection. A probe left out of a frame is dropped, and the rest are photographed again.

At most 4 probes fit in a frame, and a fifth throws. The position must be inside the box,
`boxMax` above `boxMin` on every axis, `blend` 0 or more, and each key used once a frame.
With `Graphics.Reflections` off, no probes are photographed and shiny surfaces and glass
reflect only the sky.

## The look of the picture

```csharp
view.Finish(new PostSettings
{
    Exposure = 0.2f,     // stops: +1 is twice as bright
    Contrast = 0.1f,
    Saturation = 0.1f,   // -1 is black and white
    Vignette = 0.3f,
    Sharpen = 0.4f,
    Grain = 0.01f,
    Bloom = 0.1f,                              // more glow round bright things
    Lift = new Vector3(0.03f, 0.02f, 0.05f),   // lilac in the shade
    Gain = new Vector3(0.05f, 0.02f, -0.04f),  // cream in the highlights
});
```

Leaving a value at zero leaves that part unchanged. `Lift` adds colour to the dark parts
and fades out towards white, so shade takes a tint and white stays white. `Gain` makes
each channel that much brighter, by its share, so it shows most in the highlights.

| Setting | What it does |
| --- | --- |
| `Tonemapper` | The curve that fits the scene's light into what a screen shows. `Tonemapper.Agx`, the default, or `Tonemapper.Aces` for the older, punchier look. See [tone mapping](#tone-mapping) |
| `AgxLook` | The look on top of AgX. `AgxLook.Punchy`, the default, deepens the mid-tones and strengthens colour; `AgxLook.Base` is the plain, softer curve. ACES ignores it |
| `Temperature` | White balance, from -1 to 1. Above 0 warms the picture, as if the camera were balanced for light bluer than daylight: 1 balances for 12000 K. Below 0 cools it, down to 4000 K at -1. 0.1 is a gentle afternoon warmth |
| `Tint` | White balance the other way, from -1 to 1. Above 0 pulls the picture towards magenta, below 0 towards green |
| `FocusDistance` | Where the lens is focused, in metres from the camera. See [focus](#focus) |
| `Aperture` | The lens's f-number, such as 2.8 or 8. Smaller opens the lens wider and blurs more. 0, the default, is no depth of field |
| `FocalLength` | The lens's focal length in millimetres, for the blur only. 0, the default, means 35. It doesn't change the field of view, which the camera sets |

A view refuses a `Temperature` or `Tint` outside -1 to 1, or one that isn't a number.
Rendition used on its own holds them at the nearest end instead, and counts a value that
isn't a number as 0. White balance works on the light, after exposure and before contrast and the
curve, the way a camera's does, so white surfaces in the sun take the warmth and the
highlights still fade to white.

### Tone mapping

The scene's light runs far brighter than a screen can show, so a curve squeezes it in.
AgX, the default, is the curve Blender 4 uses. It moves bright, strongly coloured light
gradually towards white, so a sunlit brick wall keeps the difference between its bricks.
ACES, the older look, is punchier in the mid-tones but takes saturated highlights to one
flat colour.

On its own AgX reads soft and pale, so it comes with Blender's Punchy look by default,
which deepens the mid-tones and strengthens colour. Punchy also puts middle grey about
0.8 of a stop darker on screen than ACES or the plain curve, so a scene moving over from
ACES wants more `Exposure`: the Courtyard uses 1.05 with auto exposure, with `Contrast`
0.25 and `Saturation` 0.15. `AgxLook.Base` is the plain curve, which keeps middle grey where
ACES puts it and leaves the most room in the highlights.

```csharp
view.Finish(new PostSettings { Tonemapper = Tonemapper.Aces });   // the older look
view.Finish(new PostSettings { AgxLook = AgxLook.Base });         // AgX without the Punchy look
```

Both curves and both looks cost about the same, so `Graphics.Fastest()` leaves the choice alone.

![The Courtyard's sunlit wall at about the same brightness through ACES on the left, which runs the sunlit bricks hotter and more orange, and AgX with the Punchy look on the right, which keeps them a deeper red](media/agx.webp)

`Sharpen` gives back the crispness anti-aliasing blends away, from 0 to 1. It sharpens fine,
faint detail such as a texture's grain the most and edges that already stand out hardly at
all, so they get no bright halos. While anti-aliasing is on, textures are also read a little
sharper than their size on screen asks for. One frame alone would shimmer, but anti-aliasing
blends the frames and they settle. Reflection probes are captured without it, since nothing
blends their frames.

### Auto exposure

```csharp
view.Finish(new PostSettings
{
    AutoExposure = true,
    Exposure = -0.3f,        // now compensation: a third of a stop darker than metered
    AdaptationSpeed = 2f,    // stops a second; 0 means the default of 2
});
```

With `AutoExposure` on, the camera meters the picture and sets the brightness itself, so a
scene lit much brighter or dimmer comes out about the same. It sorts the lit picture's pixels
by brightness, before bloom, leaves out the darkest 40 per cent and the brightest 5 per
cent, and puts the average of the rest at middle grey. Deep shade and the sun's disc so don't
pull the picture one way or the other. `Exposure` then shifts that, in stops, for a
brighter or darker picture than the meter picks.

When the scene gets brighter or darker the exposure follows at `AdaptationSpeed` stops a second,
the way eyes adjust walking out of a cellar. The first frame starts at the metered brightness,
not from black, and so does the first after `AutoExposure` or [`Graphics.AutoExposure`](#graphics-settings)
is turned back on, or after time goes backwards, as when the game restarts. A speed must be above 0, or 0 for the
default. A view refuses anything else; Rendition used on its own takes the default instead. The meter sees from about 1/256 to 65000 times a white of 1. Anything darker or
brighter counts as the darkest or brightest it sees.

Screenshots and recordings step time a tick at a fixed rate, so the same `--seed` and `--ticks`
give the same exposure and the same picture every run. Reflection and light probes don't
use it: they hold light, and exposure is applied after lighting.

![The Courtyard with a fixed exposure of +0.35 on the left, and with auto exposure and +0.3 of compensation on the right, which meters it to nearly the same brightness, with the shade open and the sunlit wall bright](media/auto-exposure.webp)

### Focus

```csharp
view.Finish(new PostSettings
{
    FocusDistance = 11f,   // metres to what should be sharp
    Aperture = 8f,         // the f-number; 0, the default, keeps everything sharp
    FocalLength = 35f,     // millimetres; 0 means 35
});
```

With an aperture set, the picture blurs nearer and further than the focus distance, as a
camera lens does. The blur is worked out from a thin lens on a 36 mm full-frame sensor: a
point d metres away spreads into a circle (f / N) * |d - S1| / d * f / (S1 - f) across,
where f is the focal length, N the aperture and S1 the focus distance. A smaller f-number, a
longer lens or a nearer focus blurs more. A 50 mm lens at f/2.8 focused at 10 m spreads a
point at 5 m, or at the horizon, over 0.09 mm, about 4 pixels of a 1600 pixel picture.
The blur is measured in the scene's own pixels, so it looks the same at any
[render scale](#graphics-settings). No circle grows past 16 pixels in radius (32 across).

`FocalLength` only sizes the blur. The field of view still comes from the camera, so set
them to match if it matters. The sensor is 36 mm across the picture's width, so in a 16:9
picture 35 mm sees about 0.56 radians top to bottom, the camera's `fieldOfView`, and 50 mm
about 0.40.

Small apertures blur gently. The Courtyard is focused on its window at f/8, so the window and
the wall stay sharp, and the flagstones and grass at the camera's feet soften a little, by
about a pixel. A portrait look wants f/1.4 to f/2.8 and a longer lens; cutscenes can change
the focus each shot to pull attention.

What's out of focus behind blurs only with other things behind, so a sharp thing never
smears into the background. A blurred thing in front spreads over what's behind it, past its
own edge, as a branch close to the lens veils the scene. The blur is gathered at half size
and drawn back at full size, so edges in focus stay crisp.

The blur goes by the depth of solid surfaces, so glass and particles take the blur of what's
behind them: a window pane is as sharp or as soft as the room through it, and smoke in front
of a blurred wall blurs with the wall. Where a sharp edge in front sits on a blurred
background, the background can pick up a pixel-wide fringe of the edge's colour, since each
half-size pixel mixes the four pixels under it.

A view refuses a negative or broken `FocusDistance`, `Aperture` or `FocalLength`, and a focus
distance no further than the focal length while the aperture is open. Rendition used on its
own draws no depth of field for those instead. [`Graphics.DepthOfField`](#graphics-settings)
turns it off for every view, and `Graphics.Fastest()` turns it off. Probes never see it, so
changing the focus never captures them again.

![The Courtyard's window, barrel, puddle and near grass with everything sharp on the left, and through an 85 mm lens at f/2.8 focused on the window 11 m away on the right, where the window and the wall stay sharp and the grass and mud in front blur strongly](media/depth-of-field.webp)

The picture above uses a stronger lens than the Courtyard's own f/8 and 35 mm, so the blur shows.

## The sky

Without a sky the world sits in a plain grey studio. `view.Sky` gives it a gradient from
the ground up through the horizon to straight up, which shows behind everything, lights
the scene from all around and shows in reflections:

```csharp
view.Sky(new SkySettings
{
    Zenith = new Vector3(0.18f, 0.36f, 0.85f),   // straight up
    Horizon = new Vector3(1f, 0.62f, 0.32f),
    Ground = new Vector3(0.22f, 0.2f, 0.13f),    // below the horizon
    Brightness = 0.9f,                           // 0 counts as 1
    HazeStart = 25f,                             // metres
    HazeEnd = 110f,
});
```

Haze fades things into the sky behind them, from nothing at `HazeStart` to all sky at
`HazeEnd`, so the far edge of a world melts away instead of stopping. Leave both at
zero for no haze. A negative brightness, or haze that ends nearer than it starts, throws.

`HazeFromFocus` measures the haze across the ground from the point the camera looks at
instead of from the camera. Use it with a far-off camera, such as an isometric one: from there everything on
screen is about the same distance away, so haze from the camera fades everything evenly.
Haze from the focus leaves the middle clear and fades the edges of the view. `HazeColor`
sets what things fade into; zero fades them into the sky behind them, which for a camera
looking down is the dark ground below the horizon.

![The Courtyard sky as a gradient, then as a clear sky, then the clear sky round the sun's disc](media/clear-sky.webp)

`Turbidity` swaps the gradient for a clear sky worked out from the sun, after Preetham,
Shirley and Smits (1999). It is brighter and whiter towards the sun, a deeper blue
straight up and away from it, and paler at the horizon, and the sun itself shows as a
disc:

```csharp
view.Sun(direction: new Vector3(-0.75f, 0.5f, 0.42f), color: new Vector3(7.2f, 6.7f, 5.8f));
view.Sky(new SkySettings
{
    Ground = new Vector3(0.2f, 0.17f, 0.14f),
    Brightness = 0.75f,
    Turbidity = 2.5f,   // 1.7 is a very clear day, 10 a hazy one
});
```

Turbidity is how hazy the air is, from 1.7 to 10; 2 to 3 is a clear day. Zero, the
default, keeps the gradient, and is the way to turn the clear sky off: there is no
switch for it in `Graphics`. Any other value throws. The sky follows the sun that casts
the shadows, or the first sun when none does. Its disc is as wide as `sunSize` in
`view.Shadows` and as bright as the sun's colour spread over it, so a smaller sun is a
brighter disc, held under what the screen can store. `Zenith` and `Horizon` are not used;
`Ground` still colours what is below the horizon, and `Brightness` still scales the
whole sky, disc included. A sun below the horizon is treated as on it, so this is a
daytime sky.

The disc shows in the background and in sharp reflections from the sky, but not in the
soft light, in rough reflections or in what [probes](#bounced-light) capture: the sun's
own light already gives those their highlight and their warmth. Moving the sun or
changing the turbidity rebuilds the sky's lighting, the same as changing its colours.

Changing the colours rebuilds the sky's lighting, which takes a moment, so keep them
steady rather than animating them every frame. [Water](#water), when set, takes over the
sky. The [Archer example](../examples/Archer) uses a golden-hour sky, and the
[Courtyard example](../examples/Courtyard) a clear one.

### Fog

Fog is air you can see: it thickens towards the ground, so the far side of a yard sits
back in it while the near ground stays crisp. Set it on the sky:

```csharp
view.Sky(new SkySettings
{
    Turbidity = 2.5f,
    Fog = new FogSettings(Density: 0.004f, Height: 0f, Falloff: 0.15f, SunScatter: 0.6f),
});
```

![The Courtyard with Graphics.Fog off, and with fog of density 0.004 and falloff 0.15](media/fog.webp)

`Density` is how much light the air takes per metre at `Height`, so 0.004 hides about 4%
of what is 10 m away and a third of what is 100 m away. Above `Height` the fog thins by e
every 1 / `Falloff` metres, 6.7 m at 0.15, and below it thickens the same way, so a
valley fills and a tower's top stands clearer than its foot. A `Falloff` of 0 is fog of
one density at every height. The default, a density of 0, is clear air.

Fog takes its colour from the sky behind what it hides, without the sun's disc. Looking
down, it takes the sky's colour at the horizon that way, since fog is lit by the sky all
round it, not by the ground. Looking towards a sun it glows, by `SunScatter` times the
sun's colour, in a lobe that narrows to nothing a right angle away; 0 gives no glow.
Lit surfaces, glass and particles all sit in the same fog. The sky itself is drawn
clear, as is everything under [water](#water).

Haze and fog work side by side. Haze fades things into the sky between two distances,
which hides the edge of a world; fog is worked out along each line of sight and gives
depth inside it. A negative density, falloff or sun scatter throws, as does a height that
isn't a number. Fog is drawn for the camera only: [light probes](#bounced-light) and
[reflection probes](#reflections) capture clear air, so changing the fog captures nothing
again and costs nothing but its pixels. `Graphics.Fog` turns it off.

## Water

`view.Water` puts the camera underwater, with light absorbed by depth, moving caustics on
sunlit surfaces and shafts of light:

```csharp
view.Water(new UnderwaterSettings
{
    Absorption = new Vector3(0.075f, 0.026f, 0.018f),   // per metre; red should be largest
    ShallowColor = new Vector3(0.11f, 0.42f, 0.55f),
    DeepColor = new Vector3(0.01f, 0.09f, 0.17f),
    SurfaceHeight = 30f,
    CausticStrength = 2.2f,
    CausticScale = 0.11f,
    ShaftStrength = 0.11f,
    ShaftDistance = 55f,
});
```

Leaving `Absorption` at zero means there's no water. The [Reef example](../examples/Reef)
uses this.

## Models

Models are cooked from FBX, glTF or OBJ into Rendition's `.rmodel` format by the model
cooker in `Super.Cook.Models`, through the asset pipeline. Its settings are the ones
below, read from the model's `<file>.cook.json` sidecar, and a `parts` entry cooks named
pieces of one file, each as `file#part`. [`super-cook`](command-line.md#cooking-content)
runs it, and so does a build that imports `Super.targets`. The game reads only the
cooked content.

A game names a model by its source's [name](content.md#names), the path from the
content root:

```csharp
readonly ModelRef coral = content.Model("reef/coral/coral.glb");
```

A name that wasn't cooked, or that isn't a model, stops with a `ContentException` when
it's named.

Naming a model starts loading it. A model drawn before it's ready is waited for, so
nothing pops in; to show a loading screen instead, see
[Game states](states.md#loading-screens). `ModelRef.Cube` and the other
[built-in shapes](#built-in-shapes) need no file.

The sidecar sets the model's size and fixes materials by name. Each rule
applies to every material whose name contains `match`:

```json
// models/fir.glb.cook.json
{
  "targetLength": 6.0,
  "materials": [
    { "match": "bark", "roughness": 0.9 },
    { "match": "leaves", "alphaCutoff": 0.5, "foliage": true }
  ]
}
```

`alphaCutoff` cuts out pixels whose opacity is below it. Opacity is the colour texture's
alpha times the surface texture's alpha, so leaves with alpha in their colour texture
need nothing else, and a glTF file's own `MASK` cutoff is read without a rule. The
import fails if the cutoff would cut out nothing, or everything, instead of quietly
giving you solid cards.

`foliage` is for a tree's leaves. It points their normals out of the crown so it's lit
as one rounded shape instead of speckled cards, lets sunlight through the back of the
leaves, and draws both sides. Every material the rule matches counts as one crown, so
give the trunk its own material.

Textures are looked for beside the source file, then in any folder under it, as
Poly Haven's downloads keep them in `textures`. Some plants ship their leaf cut-outs
as a separate grey image beside a colour JPEG with no alpha; name it with `alpha`, and
its red becomes the colour texture's opacity. A mesh without UVs whose material uses a
texture warns, since every read lands on one texel; an untextured mesh needs no UVs. A
texture a rule names that can't be found stops the cook; one the model file links to only
warns. A texture path must match the file's name exactly, case included, even on Windows,
so a model that cooks there cooks on every OS:

```json
// props/fern_02/fern_02.gltf.cook.json
{
  "node": "fern_02_a",
  "materials": [
    { "match": "fern_02", "alpha": "fern_02_alpha_1k.png", "alphaCutoff": 0.5, "foliage": true }
  ]
}
```

`node` keeps only what's under the nodes whose name contains it, for files that hold
several variants side by side, as Poly Haven's plant sets do. The cook fails if no
node with meshes matches. It's not for rigged models.

`keepMeshes` keeps each mesh of a static model as its own part, named by the node that holds
it, instead of merging the meshes that share a material. It's `false` by default, so a static
model has one part per material and no mesh names. Set it to `true` to
[show some of its meshes](#showing-some-meshes-of-a-model). Each kept mesh is its own draw
call, so a model of hundreds of small meshes costs more to draw with it. Rigged models always
keep their meshes.

`keepOrigin` keeps the file's own origin. The model is still turned by `rotateY` and scaled by
`targetLength`, but about that origin: it isn't centred or stood on the ground, so it stays where
the file put it. Use it when the origin means something, such as a character whose file also holds
a few hundred alternative weapons, heads and shields spread around. Those would move the bounds,
and the character would end up shifted by an amount you can't predict. A rigged model's skeleton
stays in the same place as its mesh. It's `false` by default.

`compressTextures` block-compresses the model's textures, which is on by default. Normal maps
become BC5, height maps BC4 and the rest BC7, so a texture takes a quarter of the memory or less.
Compressing takes a while: BC7 takes about 8 seconds for a 2048-pixel texture, and the whole
Courtyard model cooks in about 10 minutes. That is paid only when the model or a graph it uses
changes, since the result is cached. Set it to `false` to keep every pixel
exactly. The cook checks textures before it compresses them, so its warnings are the same
either way.

To use several pieces of one file, list them as `parts`. Each part is cooked as its own
model, named `file#part`, and takes every setting written beside `parts` unless it sets
that one itself. A file with parts cooks only its parts:

```json
// props/dandelion_01/dandelion_01_1k.gltf.cook.json
{
  "materials": [
    { "match": "dandelion_01", "alpha": "dandelion_01_alpha_1k.png", "alphaCutoff": 0.5, "foliage": true }
  ],
  "parts": {
    "a": { "node": "dandelion_01_a" },
    "b": { "node": "dandelion_01_b" }
  }
}
```

```csharp
var flowers = new[] { content.Model("props/dandelion_01/dandelion_01_1k.gltf#a"), content.Model("props/dandelion_01/dandelion_01_1k.gltf#b") };
```

### Showing some meshes of a model

One file can hold many looks: several bodies, heads and weapons for one character, of which
each draw shows one of each. A rigged model keeps every mesh in its source file, named by the
node that holds it, the name an artist sees in the outliner. A static model keeps them too,
but only with `"keepMeshes": true` in its sidecar, since by default its meshes that share a
material merge into one part with no name. Cook the model again after adding it. Pick the
meshes by name with `content.Meshes` once, and keep the mask. Each draw then shows only
those meshes. This is different from the `#part` files above, which are separate models cut
at cook time:

```csharp
MeshMask mask = content.Meshes(model, "Body_03a", "Head_05b");
view.Model(key, model, transform, mask);
```

A mask fits only the model it was made for, so make it from the model you draw. See
[Mesh names](content.md#mesh-names) for the rules and the error when a name isn't there,
and [Showing some meshes](views.md#showing-some-meshes) for how a draw uses it.

The textures a model uses are read while it cooks, so they don't need to be assets of
their own. Turn cooking off for them with a rule in a [`cook.json`](content.md#rules-and-sidecars),
as the shared `assets` folder does for Poly Haven's `textures` folders:

```json
// assets/cook.json
{
  "rules": [
    { "match": "**/textures/**", "cook": false },
    { "match": "reef/**/*.png", "cook": false }
  ]
}
```

Other options are the `baseColor`, `normal`, `surface` and `emissive` textures,
`color`, `emissiveColor`, `metallic`, `roughness`, `clearcoat`, `subsurface`, `heightDepth`,
`detailScale`, `detailMask`, `twoSided`, `transparent`, `opacity`, `ior`, `noNormalMap`,
`worldVariation`, `dithered` and `rotateY`, and `graph`, `graphSize`, `detailGraph` and
`detailGraphSize`, below; see `src/Super.Cook.Models/ImportSettings.cs`.

`worldVariation` darkens and lightens the colour in broad patches a few metres across, laid by
where the surface is in the world rather than by its UVs, with a little more roughness in the
dark patches. A texture repeated across a big wall or floor then doesn't show its repeats. It's
on the Courtyard's bricks, stone, flagstones and mud.

`dithered` fades a material out softly by its opacity instead of cutting it at `alphaCutoff`.
Each frame a share of its pixels as large as its opacity is drawn, a different share each
time, and anti-aliasing blends them into a smooth fade. Its shadow fades the same way. With
`AntiAliasing` off the pattern shows as grain. Like any opaque surface, a dithered one
replaces the normal and roughness of what's behind it. For grime, damp or a stain on a
wall, use a [decal](views.md#decals) instead of a dithered quad in front of it: a decal
blends its colour and roughness into the wall and lays its bumps over the wall's own, so
the bricks still show through, and it never floats off the surface or flickers against it.
Keep `dithered` for things that should fade as a whole, such as a leaf or a cobweb.

### A material graph on a model

A rule's `graph` gives the materials it matches a [material graph](materials.md), so a
model exported from Blender can wear procedural bricks, dirt or wood. The path is from the
model's folder and names a `.material.cs` graph or a `.material.json` variant:

```json
// models/courtyard.glb.cook.json
{
  "materials": [
    { "match": "graph_bricks", "graph": "../materials/bricks.material.cs" },
    { "match": "graph_dirt", "graph": "../materials/dirt.material.cs", "graphSize": 512 }
  ]
}
```

The graph runs while the model cooks, at `graphSize` pixels across, 1024 unless you give
one. Like a graph's own [size](materials.md#size), it must be a power of two from 16 to
4096, and anything else stops the cook:

```text
material graph_dirt: graphSize must be a power of two from 16 to 4096, not 500.
```

The graph's colour, normal and surface textures go into the model like any other textures.
The material's colour is white and its `metallic` and `roughness` are 1, because they
multiply the textures. Set `color`, `metallic` or `roughness` in the rule to tint or scale
them. The model file's own colour and factors are ignored, since they were meant for its
own textures. Since the graph makes the textures, a rule that also sets `baseColor`,
`normal`, `surface` or `alpha` stops the cook. `emissive`, `noNormalMap`, `alphaCutoff` and
the other switches work as usual. A graph's `Opacity` goes into the surface texture's alpha,
so for glass whose grime clouds it, add `"transparent": true` and the graph's opacity
scales the tint, as the Courtyard's window does. With `"dithered": true` the opacity fades the
material out instead. A stain such as the Courtyard's `grime.material.cs` looks better as a
[decal](views.md#decals): `content.Surface` loads the graph on its own, and its opacity
fades the decal.

A graph that sets a `Height` also gives the material a height map, so its grooves sink in
with [parallax](#height-and-detail-maps) up close. The depth is the graph's `Depth`. A rule's
`heightDepth` sets another, and `"heightDepth": 0` turns the parallax off. A rule's own
`height` texture wins over the graph's.

A rule's `detailGraph` names a second, small graph to lay over the first as its detail
textures, for grain that still holds up when the camera is a step away and the main graph's
pixels run out. Its normal map becomes the detail normal. Its colour becomes the detail
colour, divided by its average brightness so it brightens and darkens without changing the
main graph's colour on the whole. It tiles at its own `Width`, so a 0.25 m grit graph over
a 2 m brick graph repeats 8 times across each repeat of the bricks; a rule's `detailScale`
sets another. It runs at `detailGraphSize`, 1024 by default:

```json
{
  "match": "graph_bricks",
  "graph": "../materials/bricks.material.cs",
  "detailGraph": "../materials/grit.material.cs",
  "worldVariation": true
}
```

A detail graph needs a main graph, and it makes the detail textures, so a rule can't also
give `detailColor` or `detailNormal`:

```text
material bark: detailGraph tiles at a size the main graph's width sets, so it needs a graph.
material bark: detailGraph makes the detail textures, so it can't be used with detailColor or detailNormal.
```

![The Courtyard's wall before and after taller bricks, parallax, grit detail, world variation and sharper texture reads](media/courtyard-textures.webp)

A graph material reads its UVs in metres. The cook divides the UVs of every mesh that uses
it by the graph's `Width`, so a 2 m brick graph repeats once every 2 units of UV. In
Blender, unwrap the surfaces at real-world scale, one UV unit to a metre, and a tile starts
at the UV origin, so a wall unwrapped from its foot starts a row of bricks there. The
division happens before `targetLength` is applied. A model cooked with `targetLength`
changes size but keeps its UVs, so its bricks grow or shrink with it.

Errors in the graph stop the model's cook and name the material, and the graph and its
line where there is one:

```text
material graph_bricks: ../materials/bricks.material.cs line 12: scale must be at least 1, not 0. (Parameter 'scale')
material graph_dirt: materials/dirt.material.cs isn't in the source root.
```

Editing the graph, its variant or an image it reads cooks the model again. A graph that
several materials or parts use runs once per model cook at each size. The graph is a
source of its own too, so it also cooks to its own textures. If only models use it, turn
that off with a `"cook": false` rule in `cook.json`, as you would for a model's textures.

A rigged model keeps its skeleton, bone weights and clips, and `animations` adds clips
from other files of the same rig, such as Mixamo's:
`"animations": [{ "file": "walk.fbx", "name": "Walk" }]`. See [Animation](animation.md).

A colour passed when drawing a model tints it and keeps its textures, so one tree model
can be drawn in many autumn shades at no extra cost.

Draw a model as many times as you like. After culling, the renderer draws copies of one
part that look the same (same level of detail, material, tint, ghost and swim) as one
instanced draw, so a forest of one tree or a grid of tiles costs a few draws, not
thousands. Only where each copy stands can differ. A different tint splits copies into
separate draws but still works. Skinned models are drawn one at a time.

The Archer, Fox and Reef examples share their models through the `assets` folder at the
top of the repository, which each lists as a content root. Only `assets/cook.json`,
the sidecars and its README are in git; the models themselves aren't, because of their
licenses or their size ([CREDITS.md](../CREDITS.md)). [assets/README.md](../assets/README.md)
says where each one comes from and the name to save it under. Without them the examples
still build: the cook warns `No source file <name>; skipped.` for each sidecar whose model
is missing, and the game reports a missing model when it names it.

## Glass

![Glass panes before coloured posts: a clear one, a frosted one and a green one, with glass balls bending the floor and dust rising between the panes](media/glass.webp)

A surface below full opacity is glass. Its colour shows by its opacity, and the rest
lets through what's behind it. Roughness frosts it, blurring what's seen through, and
`Ior` bends it. Leave `Ior` at 1 for a thin pane, which hardly bends light, and use
about 1.5 for solid glass such as a marble or a bottle.

```csharp
var window = new Surface { Color = new Vector3(0.75f, 0.85f, 0.95f), Roughness = 0.05f, Opacity = 0.12f };
var frosted = new Surface { Color = new Vector3(0.9f), Roughness = 0.45f, Opacity = 0.2f };
var marble = new Surface { Color = new Vector3(0.95f), Roughness = 0.02f, Opacity = 0.08f, Ior = 1.5f };
```

A cooked model's material is glass with `"transparent": true` and an `opacity`, and
bends with `"ior": 1.5` in its [material rule](#models).

Glass and particles are drawn together, furthest first, so a pane in front of dust
covers it and dust in front of a pane covers that. Glass sees the scene through a copy
of the picture, made again before a pane that something drawn since overlaps, at most
four times a frame. Past that, overlapping glass still blends but doesn't bend or
frost. What's behind is bent once, at the near side, and followed half a metre at
most, so solid glass looks right but isn't exact. Glass casts a lighter shadow, by how
much light it lets through, and even clear glass keeps a quarter of its shadow, as
bent light spreads. Glass reflects [reflection probes](#reflections) where they cover it and the sky elsewhere, but not what's on screen.
The `Glass` example shows each kind, and `--flat` draws it all without bending or
frosting.

## Skin and wax

Light goes a little way into skin, wax, jade and marble before it comes back out, so
shadow edges on them are soft and warm instead of sharp and grey. A `Subsurface` on the
material turns this on:

![Skin, wax and jade, each plain on the left and with subsurface scattering on the right](media/wax.webp)

```csharp
var skin = new Surface
{
    Color = new Vector3(0.85f, 0.6f, 0.48f),
    Subsurface = new Subsurface
    {
        Radius = 0.012f,                     // metres
        Color = new Vector3(1f, 0.4f, 0.25f), // red travels furthest
        Transmission = 0.3f,                 // light from behind through thin parts
    },
};
```

`Radius` is how far light travels under the surface, in metres: about 0.01 for skin, a
few centimetres for wax. `Color` is how far red, green and blue each go, as fractions of
the radius; leave it out and they all go the same distance. `Transmission`, from 0 to 1,
lets light from behind shine through thin parts such as ears or a candle's rim, tinted by
the colour and by `Color`.

A model sets it per material in its cook settings:

```json
{ "match": "skin", "subsurface": { "radius": 0.01, "color": [1, 0.4, 0.25], "transmission": 0.3 } }
```

Subsurface can't be combined with `clearcoat` or `foliage`. The renderer refuses such a
material and the cook stops with an error naming it. Under water the light isn't spread.
When no material in the world scatters light the blur doesn't run, so it costs nothing.
The [Wax](../examples/Wax) example puts each kind beside a plain twin.

## Ghosts

`view.Ghost` draws a model as a preview, such as a building the player is about to
place: green where it fits and red where it doesn't.

```csharp
var fits = new Vector3(0.2f, 1f, 0.3f);
var blocked = new Vector3(1f, 0.15f, 0.1f);
view.Ghost(PreviewKey, cabin, Matrix4x4.CreateTranslation(cursor), canPlace ? fits : blocked, opacity: 0.6f);
```

A ghost leaves out the model's own colours, textures and tint, and is one matte colour
that glows a little, so it reads the same over a dark model or a light one, in sun or
in shadow. Its shape still shows through the light. The colour is linear, from 0 to 1.
Opacity is 1 by default, and below 1 the world shows through, as with glass. It must be
more than 0 and at most 1. A ghost casts no shadow. Pass a
[mesh mask](#showing-some-meshes-of-a-model) before the colour to preview only some of a
model's meshes, such as a unit in the look it will have.

## Built-in shapes

![Every built-in shape at the back, and shapes made with Shapes at the front](media/shapes.webp)

Every game has eight ready shapes that need no file and no loading, handy for trying out
an idea: a capsule as the player, spheres as resources, cylinders as trees. Each is one
unit wide and centred on zero, and comes out light grey unless the draw gives a colour.

```csharp
view.Model(1, ModelRef.Capsule, Matrix4x4.CreateTranslation(player.Position + Vector3.UnitY), playerColor);
```

| Shape | Size |
| --- | --- |
| `ModelRef.Cube` | 1 × 1 × 1 |
| `ModelRef.Sphere` | 1 across |
| `ModelRef.Capsule` | 1 wide, 2 tall, standing up |
| `ModelRef.Cylinder` | 1 wide, 1 tall, standing up |
| `ModelRef.Cone` | 1 wide at the base, 1 tall, pointing up |
| `ModelRef.Plane` | 1 × 1, lying flat and facing up. One-sided |
| `ModelRef.Quad` | 1 × 1, standing up and facing along +z. One-sided |
| `ModelRef.Torus` | 1 across, with a tube 0.25 thick, lying flat |

They can't be unloaded, and `content.IsReady` is always true for them.

For other sizes, more or fewer segments, or a surface of their own, `Shapes` builds the
same shapes and more as a `GeneratedMesh` to hand to `content.Mesh`:

```csharp
ModelRef pillar = content.Mesh(Shapes.Cylinder(topRadius: 0.3f, bottomRadius: 0.4f, height: 3f));
ModelRef tile = content.Mesh(Shapes.Hex(radius: 1f, height: 0.2f, new Surface { Color = grass }));
ModelRef rock = content.Mesh(Shapes.Flat(Shapes.Sphere(0.5f, segments: 7, rings: 5)));
ModelRef floor = content.Mesh(Shapes.Plane(new Vector2(20f, 20f), surface: tiles, uvPerMetre: 0.5f));
```

| Call | Makes |
| --- | --- |
| `Shapes.Box(size)` | A box of that width, height and depth |
| `Shapes.Sphere(radius, segments, rings)` | A sphere. Segments go round the middle, rings from pole to pole |
| `Shapes.Hemisphere(radius, segments, rings)` | The top half of a sphere, closed underneath, with its base at zero |
| `Shapes.Capsule(radius, height, segments, rings)` | A capsule. The height is end to end, at least twice the radius; rings are per end |
| `Shapes.Cylinder(topRadius, bottomRadius, height, segments)` | A cylinder closed at both ends. A top radius of 0 is a cone |
| `Shapes.Cone(radius, height, segments)` | A cone standing on its base |
| `Shapes.Plane(size, subdivisions)` | A one-sided square on x and z, facing up, split into squares |
| `Shapes.Quad(size)` | A one-sided rectangle on x and y, facing along +z |
| `Shapes.Torus(radius, tube, segments, sides)` | A ring lying flat. The radius is to the middle of the tube, which must be thinner |
| `Shapes.Prism(sides, radius, height)` | A column with that many flat sides. The radius is to a corner |
| `Shapes.Hex(radius, height)` | A six-sided tile with corners along x, for hex maps |
| `Shapes.Flat(mesh)` | Any mesh with hard edges: each triangle lit as one flat face, for a low-poly look |

Every shape is centred on zero apart from the hemisphere, faces outwards, and has normals,
tangents and UVs, so normal maps work on it. Curved shapes are smooth, while the box,
prism and hex have sharp edges. Every call also takes a `Surface`, which defaults to matte
grey. UVs stretch once across each part, such as each face of a box or the side of a
cylinder, unless `uvPerMetre` is given; then they count metres, so a tiled texture is the
same size on every shape whatever its size. Round shapes measure round their widest point.
A surface from `content.Surface` (a material graph) tiles at its graph's width when
`uvPerMetre` is not given, and `uvPerMetre` wins when it is. Ribbons, tubes and extrusions
follow the same rule. A `uvPerMetre` or `TileWidth` must be a finite number above zero, or
the shape throws an `ArgumentOutOfRangeException` naming it.

## Meshes made by code

A mesh built in code, such as ground from a height function, is handed over with
`content.Mesh`. Its `Surface` says how it looks: a colour, roughness and metalness,
optional colour, normal and surface textures, opacity and IOR for [glass](#glass), and
optionally a surface shader with its own parameters and textures.

```csharp
var mesh = new GeneratedMesh(vertices, indices, new Surface
{
    Color = new Vector3(0.8f, 0.3f, 0.2f),
    Roughness = 0.6f,
});

ModelRef rock = content.Mesh(mesh);
```

Textures come from a cooked image (PNG, JPEG or TGA), or from pixels you made. Whether a
cooked one is a normal map or linear data is a cook setting, set by a
[`cook.json` rule](content.md#rules-and-sidecars) such as
`{ "match": "**/*-normal.*", "texture": { "normalMap": true } }`, so code names only the file:

```csharp
TextureRef bark = content.Texture("textures/bark.png");
TextureRef bump = content.Texture("textures/bark-normal.png");
TextureRef noise = content.Texture(new GeneratedTexture(256, rgbaPixels), TextureKind.Linear);
TextureArrayRef ground = content.TextureArray(sand, grass, rock, snow);
```

A mesh or texture you're done with is let go with `content.Unload`, which frees it after
the frames in flight. `content.IsReady(model)` says whether a mesh is on the GPU yet, so a
game can skip one still loading instead of waiting. A texture is freed after every mesh
that names it; a texture array keeps its own copy of each layer, so the single layers can
go as soon as the array is made.

## Height and detail maps

A height map sinks the low parts of a flat surface, such as the mortar between bricks or
the joints of a street, so they move with the view as deep ones would instead of looking
painted on. It's a linear texture where white is the top and black is `HeightDepth`
metres down. Pair it with the normal map of the same height, which shades the grooves.

A detail map adds grain finer than a surface's own textures, such as grit on stone or the
weave of cloth, without a bigger texture. `DetailColorTexture` is linear and multiplies
the colour, with mid grey (128) leaving it as it is, so a detail colour around mid grey
keeps the surface's own colour on average. `DetailNormalTexture` is a normal map laid
over the surface's own normal. Both repeat `DetailScale` times (8 if left out) for each
time the surface's textures repeat. With `DetailMask`, the surface texture's blue
channel says how strongly the detail shows, from none at 0 to full at 1.

```csharp
TextureRef height = content.Texture("textures/bricks-height.png");
TextureRef grit = content.Texture("textures/grit.png");
TextureRef gritNormal = content.Texture("textures/grit-normal.png");

ModelRef wall = content.Mesh(Shapes.Box(new Vector3(16f, 3f, 0.4f), new Surface
{
    ColorTexture = bricks,
    NormalTexture = bricksNormal,
    HeightTexture = height,
    HeightDepth = 0.025f,
    DetailColorTexture = grit,
    DetailNormalTexture = gritNormal,
    DetailScale = 6f,
}, uvPerMetre: 1f / 0.9f));
```

A cooked model takes the same textures and values in its sidecar, as `height`,
`heightDepth`, `detailColor`, `detailNormal`, `detailScale` and `detailMask` on a material
rule. These textures are cooked as linear data, and `detailNormal` as a normal map.
`detailScale` is 8 unless given, or, with a [`detailGraph`](#a-material-graph-on-a-model),
the main graph's width over the detail graph's. A graph material gets its height map from
its graph.

The depth shows up close: it fades out between 15 and 25 metres from the camera, where
the normal map alone looks the same and costs less. Keep `HeightDepth` to a few
centimetres. Deeper looks stretched at low angles, because the surface's edge and its
shadows stay flat. Transparent surfaces ignore height and detail maps. The [Bricks
example](../examples/Bricks) shows a wall and a street; P turns the height maps off and D the detail.

## Meshes along a spline

`Shapes` can also build meshes that follow a [spline](splines.md), such as a road, a
river, a rail or a cable:

```csharp
Vector2[] bank = [new(3.8f, -1.6f), new(3f, 0f), new(-3f, 0f), new(-3.8f, -1.6f)];
ModelRef road = content.Mesh(Shapes.Extrude(path, bank, step: 1f, surface: tarmac));
ModelRef river = content.Mesh(Shapes.Ribbon(path, width: 6f, surface: water));
ModelRef rail = content.Mesh(Shapes.Tube(path, radius: 0.05f, sides: 8));
ModelRef deck = content.Mesh(Shapes.Bend(Shapes.Plane(new Vector2(4f, 20f), subdivisions: 40), path));
```

| Call | Makes |
| --- | --- |
| `Shapes.Extrude(spline, outline, closedOutline, smooth, step)` | Sweeps an outline along the spline |
| `Shapes.Ribbon(spline, width, step)` | A flat strip facing up, centred on the spline |
| `Shapes.Tube(spline, radius, sides, step)` | A round pipe with smooth sides |
| `Shapes.Bend(mesh, spline)` | Bends a generated mesh so its +z runs along the spline |

The outline is in metres, seen looking along the spline from its start: x to the right
and y up. Each edge faces to the right of the way it runs. So an outline that goes
anticlockwise faces outwards, and a flat road listed from right to left faces up.
`closedOutline` joins the last point back to the first. Edges are sharp across the
outline unless it's `smooth`, as for a pipe. The ends are left open. A ring of vertices
goes every `step` metres. UVs are in metres: u runs across the outline and v runs along
the spline, scaled by `uvPerMetre`, which is 1 unless the surface has a `TileWidth`.

`Bend` measures along the spline from the back of the mesh (its lowest z). Each vertex
keeps its x and y across the spline, so the mesh keeps its length. Past the end of an
open spline the mesh carries on straight. Only vertices move, so give the mesh plenty of
them along z, such as a `Shapes.Plane` with many subdivisions.

An outline folds over itself where the spline turns tighter than the outline is wide.
Keep half the width narrower than the tightest turn. Only generated meshes can be bent;
a loaded model can't be.

## Terrain

`Super.Terrain.Heightfield` builds chunk meshes from a height function on a shared grid,
so neighbouring chunks meet exactly:

```csharp
var field = new Heightfield((x, z) => Noise.Hills(x, z) * 30f, spacing: 1f);

var ground = content.Mesh(field.Chunk(chunk.X, chunk.Z, quads: 32, new Surface
{
    Shader = content.Shader("terrain"),
    Parameters = [new Vector4(snowHeight, snowFade, rockSteepness, rockFade)],
    ShaderTextures = [splat],
    ShaderArrays = [groundColors, groundNormals],
}, uvPerMetre: 0.125f));
```

`field.Height(x, z)` and `field.Normal(x, z)` answer gameplay questions about the same
ground. The [Terrain example](../examples/Terrain) loads chunks around the camera and
unloads the ones left behind.

## Surface shaders

A surface shader is your own fragment shader for the G-buffer pass. It decides what a
surface is made of (colour, metalness, roughness, normal, glow), and Rendition's
lighting, shadows, reflections and post-processing treat the result like any other
surface.

```glsl
// Shaders/pulse.frag
#version 460
#extension GL_GOOGLE_include_directive : require
#include "surface.glsl"

void main() {
    Surface surface = materialSurface();
    surface.emissive += materials[draw.material].shaderParameters[0].rgb * sin(frame.time);
    writeSurface(surface);
}
```

`materialSurface()` gives the surface the material's textures and values describe, so a
shader only changes what it cares about. A shader can also read `inUv`, `inPosition`,
`inNormal`, `inTangent` and `frame`.

| From the `Surface` | In the shader |
| --- | --- |
| `Parameters`, up to 8 `Vector4` | `materials[draw.material].shaderParameters[i]` |
| `ShaderTextures`, up to 4 | `shaderTexture(slot, uv)` |
| `ShaderArrays`, up to 4 | `shaderLayer(slot, uv, layer)`, where a fractional layer blends two |

A shader that moves its UVs can still use the material: `materialUv()` is `inUv` moved by
the material's height map, and `materialSurface(uv)` reads the material at any UV.
`parallaxUv(slot, uv, depth)` does the same depth trick with a shader texture as the height
map, and `blendNormals(base, detail)` lays one tangent-space normal over another.

Unset slots read white. To compile shaders, put them in a `Shaders` folder in your game
project and import the targets file, which runs `glslc` from the Vulkan SDK:

```xml
<Import Project="..\..\src\Shaders.targets" />
```

Then name the shader by its file name:

```csharp
var surface = new Surface
{
    Shader = content.Shader("pulse"),
    Parameters = [new Vector4(1f, 0.4f, 0.1f, 0f)],
};
```

`examples/Terrain/Shaders/terrain.frag` blends four ground layers by height, slope and a
splat texture. More detail is in [design.md](design.md#custom-shaders).

## Using Rendition alone

Rendition knows nothing of Super and can be used directly, with your own loop and
window. Its [README](../src/Rendition/README.md) covers the API and the sample, and [design.md](design.md#rendition-design) explains the frame structure.

# Gallery

Plaster rooms along one corridor, lit only the way Rendition lights things today: a sun,
the sky, [light probes](../../docs/rendering.md#bounced-light),
[reflection probes](../../docs/rendering.md#reflections) and ordinary point lights. It's the
baseline that baked and mixed lighting will be compared against later.

```
dotnet run -c Release --project examples/Gallery
```

![The entrance hall, the colour room's bust, the bronze whale and the chapel](../../docs/media/gallery.webp)

The entrance hall has a long skylight, and a colonnade throws its shadows down a corridor
42 m long, further than the sun's last shadow cascade reaches. Off the corridor are four
rooms:

- A colour room, with a red wall, a blue wall and a white floor, where sun from a high
  window falls on a marble bust on a plinth.
- A whale room, with a bronze whale under a skylight.
- A dark lamp room, with a strip of glowing floor panels, three hanging lamps with large
  soft lights, and a brass sphere rolling back and forth under them.
- A chapel, lit by a lantern swinging on its chain and a row of flickering candles on the
  altar.

At the end of the corridor a tinted glass screen closes off an open courtyard.

Everything except the two statues is built in code from boxes, cylinders and spheres in
`Building.cs`, with flat matte materials: light-grey plaster with a faint noise and a few
strong colours. The glowing discs and panels use a small surface shader,
`Shaders/glow.frag`, because a `Surface` has no emissive colour of its own. The glow
doesn't light anything; the point lights under the lamps do.

The camera walks a slow, fixed tour from `Tour.cs`, worked out from time alone, so the same
tick always gives the same picture. Press F to fly it yourself: W, A, S and D move, E and
Q go up and down, and the mouse looks around. F again goes back to the tour.

## Shots

Each room's hold is halfway through at these ticks, which make repeatable shots:

| Room | `--ticks` |
| --- | --- |
| Overview from above | 150 |
| Entrance hall | 660 |
| Colour room | 1065 |
| Whale room | 1515 |
| Lamp room | 1965 |
| Chapel | 2415 |
| Courtyard, through the glass | 2820 |

```
dotnet run -c Release --project examples/Gallery -- --seed 1 --ticks 1065 --size 1280x720 --screenshot colour.png
```

## Models

The marble bust and the bronze whale are Poly Haven models, kept in `content/models` with
their textures, at their real sizes of about 0.5 m and 1.1 m. See [CREDITS.md](../../CREDITS.md).

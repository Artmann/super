# Crypt

A stone hall lit only by fire and two beams. Four torches cast shadows, five brass
candelabras stand on the altar, 126 loose candles sit on the floor, a lantern swings
over a chest, and a cold beam falls from a grate. That's about 150 lights a frame.

```
dotnet run -c Release --project examples/Crypt
```

![The Crypt example](../../docs/media/crypt.webp)

## Getting the assets

The models aren't in git, because of their size. The statue, the chest, the barrels, the
lantern and the candelabras come from the shared `assets` folder, which the build cooks:
[assets/README.md](../../assets/README.md#crypt) says where to download each one.

The stone is this example's own. Download these Poly Haven textures and save them in
`content/textures`, inside this folder:

| Saved as | Poly Haven texture |
| --- | --- |
| `floor.jpg`, `floor-normal.jpg`, `floor-surface.png` | [monastery_stone_floor](https://polyhaven.com/a/monastery_stone_floor) at 2k |
| `wall.jpg`, `wall-normal.jpg`, `wall-surface.png` | [old_stone_wall](https://polyhaven.com/a/old_stone_wall) at 2k |
| `blocks.jpg`, `blocks-normal.jpg`, `blocks-surface.png` | [large_sandstone_blocks](https://polyhaven.com/a/large_sandstone_blocks) at 1k |

The `.jpg` is the diffuse JPEG and `-normal.jpg` the OpenGL normal map. The
`-surface.png` is made from the roughness map, put into the green channel with no metal
and full opacity:

```
ffmpeg -i floor-rough.jpg -vf "format=gray,geq=r=0:g='p(X,Y)':b=0:a=255,format=rgba" floor-surface.png
```

`content/cook.json` cooks the `-normal` files as normal maps and the `-surface` files as
linear data.

See [CREDITS.md](../../CREDITS.md) for who made what.

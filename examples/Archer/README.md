# The Clearing

A 17-second film made in Super. Erika, an archer, shoots a straw dummy, dodges, runs
and leaps onto a boulder, shoots another in slow motion, dives off and puts two arrows
in a third. The whole thing is one [cutscene](../../docs/cutscenes.md): `Film.cs` is
the storyboard, and the game only moves her, the arrows and the dummies. R plays it again.

The look is a forest clearing in golden-hour light: a low warm sun from the side, a soft
warm fill, a blue sky overhead, a dark wall of firs around a bright meadow, and a gentle grade.

```
dotnet run -c Release --project examples/Archer
dotnet run -c Release --project examples/Archer -- --record out/clearing.mp4 --seconds 18
```

| File | What's in it |
| --- | --- |
| `Film.cs` | Every shot and every moment, in seconds from the start |
| `ArcherGame.cs` | Loading, the sky and light, the grade, and drawing |
| `Erika.cs` | Where she stands, root motion, leaps and drops |
| `Targets.cs` | The dummies and the arrows |
| `Clearing.cs` | The ground, the boulder, the forest and the scattered plants |
| `Shaders/clearing.frag` | The ground's surface shader, blending grass, forest floor and leaves |
| `models/build.py` | Builds the firs, the dummy and the arrow in Blender |

## Getting the assets

The models aren't in git, because of their licenses or their size. Erika, the plants,
the rocks, the firs, the dummy and the arrow come from the shared `assets` folder, which
the build cooks: [assets/README.md](../../assets/README.md) says where to download each
one and the name to save it under. `models/build.py` builds the firs, the dummy and the
arrow in Blender, and is described there too.

**The ground.** Download these Poly Haven textures at 1k and save their diffuse JPEG and
OpenGL normal map in `content/textures`, inside this folder:

| Saved as | Poly Haven texture |
| --- | --- |
| `grass-ground.jpg`, `grass-ground-normal.jpg` | [grass_ground](https://polyhaven.com/a/grass_ground) |
| `forest-floor.jpg`, `forest-floor-normal.jpg` | [forrest_ground_03](https://polyhaven.com/a/forrest_ground_03) |
| `leaves.jpg`, `leaves-normal.jpg` | [forest_leaves_04](https://polyhaven.com/a/forest_leaves_04) |

The three share one texture array, so they must be the same size. `content/cook.json`
cooks the `-normal` files as normal maps.

See [CREDITS.md](../../CREDITS.md) for who made what.

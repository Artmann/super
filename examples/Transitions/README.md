# Transitions

Every [transition](../../docs/transitions.md) in one small plaza of built-in shapes: a
warm station, a blue one and a green one. It opens with a showreel, a cutscene that
fades in, dissolves, wipes, opens an iris, blends the camera, wipes softly, fades
through white, cuts and fades out. Then the number keys move to the next station, each
with its own transition, and Space plays the showreel again.

```
dotnet run -c Release --project examples/Transitions
dotnet run -c Release --project examples/Transitions -- --record out/showreel.mp4 --seconds 25
```

| Key | Transition |
| --- | --- |
| 1 | Cut |
| 2 | Blend, 1.6 seconds |
| 3 | Fade through black, 1 second |
| 4 | Dissolve, 0.8 seconds |
| 5 | Wipe to the left, 0.8 seconds |
| 6 | Iris from the middle, 1 second |
| Space | The showreel again |

The captions are interface, so they stay sharp through every transition. Everything is
in `TransitionsGame.cs`: `PlayShowreel` is the cutscene, and `Update` plays the
gameplay transitions through the `Transitions` service.

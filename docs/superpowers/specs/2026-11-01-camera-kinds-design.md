# Camera kinds and rigs

The request: perspective, asymmetric and orthographic cameras, a camera boom, and the usual
setups for third-person, first-person, top-down and isometric games.

## What a camera is

`Camera` stays a plain value with `Position`, `Target` and `FieldOfView`, and gains two
optional properties:

- `Size` is how many world units the screen shows from bottom to top. Above zero, the camera
  is orthographic. `Camera.Orthographic(position, target, size)` says this more plainly.
- `Shift` slides the picture. It's measured in half screens, so `(0.5, 0)` puts the target a
  quarter of the screen right of centre. This is the asymmetric camera, and it works with
  both kinds.

`Camera.Ray`, `Lerp` and the renderer all read these, so a click lands on what's drawn with
any camera.

## Rigs

Each rig is a class the game keeps, with `Update` once a tick and `At(blend)` in `Draw`, like
`OrbitCamera` and `FollowCamera`.

| Rig | For | What it adds |
| --- | --- | --- |
| `BoomCamera` | Third person | An arm behind the target that shortens when something blocks it |
| `FirstPersonCamera` | First person | Mouse look from a height above the feet |
| `TopDownCamera` | Top-down | Orthographic, straight down, with pan and zoom |
| `IsometricCamera` | Isometric | Orthographic at the true isometric angle, turning in quarters |

## Calls made without asking

- **Shift, not four frustum edges.** Edges fix the shape of the picture, so they stretch when
  the window changes. A shift keeps working at any size. It covers lens shift and keeping the
  player clear of a side panel. It doesn't cover head tracking or tiled displays, which need
  explicit edges. Add those when a game needs them.
- **The boom doesn't know about walls.** Super has no physics, so `boom.Arm(target)` hands the
  game a `Ray`, the game answers with how far along it something is, and `Update` takes that
  distance. This needs no delegate and works with any collision a game has.
- **The arm pulls in at once and eases back out.** Pulling in slowly would show the inside of
  a wall.
- **An orthographic far edge of 10,000 units.** An orthographic camera can't have an infinite
  far plane. Depth is a 32-bit float and evenly spread here, so this leaves about a millimetre
  of precision.
- **Top-down can't rotate.** Looking straight down, `Camera.Up` is fixed to -z. A top-down game
  that needs rotation can use `IsometricCamera` with a pitch just under straight down.
- **A perspective top-down view needs no rig.** `FollowCamera` with an offset straight up
  already does it.

## How the renderer copes with parallel rays

The renderer assumed perspective in three places.

- **Level of detail** used distance from the eye. Under orthographic, size on screen doesn't
  change with distance, so it now uses pixels per unit, as shadow maps already do.
- **Shading** takes the view direction from the camera's position. Rather than teach every
  shader about parallel rays, Super gives the renderer an eye 1,000 screen heights behind the
  camera. Rays from it differ by under a tenth of a degree across the screen.
- **Reflections** recovered distance from depth with a formula that only holds for perspective.
  They now measure depth directly when the view is parallel.

Shadows reach `Size` across the wider screen axis at least, so a zoomed-out view isn't left
partly unshadowed. `view.Shadows` can ask for more but not less.

## Not covered

Underwater views, which measure water from the eye, are wrong under orthographic because the
eye sits far behind the camera. Rolling the camera isn't supported.

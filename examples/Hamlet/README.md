# Hamlet

A village on a hill, made from the seed, through a day that lasts two minutes. Forty
buildings stand at any angle along winding paths: cottages with garden walls, workshops
with a big open door and a forge, round towers, and wells in small paved yards. The sun
crosses the sky and the sky's colours follow it. At dusk each building's lamps come on
and flicker, and its windows glow and throw a little warm light onto the ground. A cart
rolls round the ring road with a lantern at night.

Everything is lit by ordinary real-time lights. At night that's 56 lamps with shadows
(eight of them forge fires that burn all day), 144 lit windows without shadows, the
cart's lantern and the moon: about 200 lights a frame.

```
dotnet run -c Release --project examples/Hamlet
```

![Hamlet at midday and at night](../../docs/media/hamlet.webp)

The camera circles the village for forty seconds, then comes down to a cottage's garden
gate, walks in at the door and turns to the window the sun comes through. The day is two
tours long, so it visits the same cottage once by day and once by lamplight. All of it is
worked out from the tick, so `--ticks` always gives the same shot. Press `R` for a new
village from the next seed.

Every building is built in its own space and drawn under one placement, and the code
that builds each kind (`Cottage`, `Workshop`, `Tower`, `Well`) is apart from the code
that places them (`Village`). Cottages put the bed, table and shelf against different
walls. There are no textures: only built-in shapes with flat, matte materials, a light
grey plaster with faint noise, and a few strong colours on doors, roofs and bands.

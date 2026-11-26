# Storybook

Every interface element Super has built in, each on its own page: alone, in its variations
and sizes, turned off, and next to the others, in the dark theme and the light one. Pick a
page in the sidebar, and switch the theme in the top right. It's the place to see what an
element looks like before you use it, and a test bed for changing one.

![The storybook's shapes page in the dark theme](../../docs/media/storybook.webp)

```
dotnet run -c Release --project examples/Storybook
dotnet test tests/Storybook.Tests -c Release
```

`--story badges` opens a page by its name in lower case with dashes, such as `key-hints`,
and `--theme light` starts in the light theme. Both work with `--screenshot`, so a shot of
one element is one command:

```
dotnet run -c Release --project examples/Storybook -- --story badges --theme light --screenshot badges.png
```

| File | What's in it |
| --- | --- |
| `Program.cs` | Reading the options and starting the game |
| `Story.cs` | What a page is, `--story` and `--theme`, and the list of pages in sidebar order |
| `StorybookGame.cs` | The sidebar, the theme switch and the open page |
| `Specimen.cs` | The titled cards each variation sits on, and the layouts for them |
| `Foundations.cs` | Pages for text styles, colours, shadows, shapes, gradients and icons |
| `Elements.cs` | Pages for buttons, badges, labels, dividers and key hints |
| `Controls.cs` | Pages for buttons, toggles, sliders, steppers, pickers, progress and gauges |
| `Typing.cs` | Pages for text fields and for moving focus with the keys and the gamepad |
| `Touching.cs` | The touch page: a quest list to tap, hold, drag and fling, and a strip that scrolls sideways |

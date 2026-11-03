# User data

`UserData` keeps what belongs to the person playing rather than to a saved game:
volume, key choices, whether they've seen the intro, which chapters they've unlocked.
It lasts across every save slot and survives a new game. Saved games themselves are
[planned](roadmap.md) and separate.

## Using it

Write a plain class with the defaults you want, ask for `UserData` in a constructor,
and call `Get` to read it and `Save` to write it:

```csharp
public class Progress
{
    public bool SeenIntro;
    public int ChaptersUnlocked { get; set; } = 1;
}

class MyGame(UserData userData, States<Screen> states) : IGame
{
    public void Load()
    {
        if (!userData.Get<Progress>().SeenIntro)
        {
            states.Set(Screen.Intro);
        }
    }

    void FinishIntro()
    {
        userData.Get<Progress>().SeenIntro = true;
        userData.Save<Progress>();
    }
}
```

- `Get<T>()` hands out one shared copy of `T`, so every class asking for it sees the same
  changes. The first call reads the file. With no file yet, it's a new `T` with your
  defaults.
- `Save<T>()` writes that copy to disk straight away. Call it after a change you want kept.
  Nothing is saved by itself, and nothing is written before the first `Save`.
- The class needs a constructor with no parameters. Public properties and public fields
  are both kept. It can't be generic, and two stored classes can't share a name, because
  the name becomes the file name.

## Where it's kept

Each class gets its own indented JSON file, named after the class, such as
`Progress.json`, in the player's own folder for the game:

| Platform | Folder |
| --- | --- |
| Windows | `%APPDATA%\<id>` |
| macOS | `~/Library/Application Support/<id>` |
| Linux | `$XDG_CONFIG_HOME/<id>`, or `~/.config/<id>` |

The id comes from `GameOptions.Id`:

```csharp
Game.StartWindowsGameLoop<MyGame>(new GameOptions { Title = "Reef: Deep Sea", Id = "reef" });
```

Never change the id once the game ships, or players lose what was kept. Without one,
the title is used, minus the characters a folder name can't hold (`< > : " / \ | ? *`),
so the folder above would be `Reef Deep Sea`. An id with those characters stops the
game at start with a message saying what's wrong.

## When things go wrong

- **A broken file.** If a file isn't valid JSON, it's renamed to `Progress.json.broken`,
  a warning naming both files goes to the error output, and the class starts from its
  defaults. The game keeps running. The next `Save` writes a good file.
- **A crash while saving.** `Save` writes to `Progress.json.tmp` and only swaps it in once
  it's complete, so a crash or an error part way leaves the old file as it was.
- **An older file.** Values the file doesn't have keep the class's defaults, and values
  the class no longer has are ignored. Adding a field needs no extra work.

## Runs without a window and tests

`--screenshot`, `--record`, `--profile` and [headless tests](testing.md) keep user data
in a throwaway folder that's deleted afterwards. They never read or change the
player's real settings, so a screenshot looks the same whatever you've set while playing.

To run with a particular folder, pass `--user-data`. It works in the window too:

```
dotnet run --project MyGame -- --user-data test-settings --ticks 100 --screenshot muted.png
```

A test can pick the folder by replacing the service:

```csharp
using var run = HeadlessGame.Create<MyGame>(42, services => services.AddSingleton(new UserData(folder)));
```

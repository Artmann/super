using Super;
using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The game feel story: presses that squash, springs, numbers that count, losses that trail, views that leave and pages that slide.</summary>
internal static class Feeling
{
    private enum Room
    {
        Armoury,
        Vault,
    }

    public static UiView GameFeel() => Page(
        new Specimen(
            "Presses that squash",
            "Every button shrinks a little while it's held and springs back when it's let go. Theme.Motion.PressScale sets how far, and ThemeMotion.None turns it off.",
            Row(
                12f,
                new Button("Continue", () => { }).Id("feel-continue"),
                new Button("Options", () => { }) { Style = ButtonStyle.Secondary }.Id("feel-options"),
                new Button("Quit", () => { }) { Style = ButtonStyle.Outline }.Id("feel-quit"))),
        new Specimen(
            "Springs",
            "Easing.Spring shoots past the end and settles, for something popping into place. .Scale(...) draws a view bigger or smaller without moving anything round it.",
            new Live<int>(0, (popped, pop) => Row(
                16f,
                new Button("Pop", () => pop(popped + 1)) { Style = ButtonStyle.Outline }.Id("pop"),
                new PopIn(Reward("Gem")).Id($"gem-{popped}"),
                new PopIn(Reward("Key")).Id($"key-{popped}")))),
        new Specimen(
            "Numbers that count",
            "CountingText counts to a new value instead of jumping there, with digits all as wide so it doesn't shake.",
            new Live<float>(1200f, Gold)),
        new Specimen(
            "Losses that trail",
            "A gauge that drops leaves what it lost behind as a pale piece that drains after it, so a big hit reads as big.",
            new Live<float>(0.8f, Health)),
        new Specimen(
            "Leaving as well as arriving",
            "Presence eases its view in when it turns on and plays that backwards before it goes. While it leaves, the mouse goes through it.",
            new Live<bool>(true, (shown, show) => Row(
                16f,
                new Button(shown ? "Hide the quest" : "Show the quest", () => show(!shown)) { Style = ButtonStyle.Outline }.Id("quest-toggle"),
                new Presence(shown, Quest()) { Entrance = Entrance.Rise, Seconds = 0.3f }))),
        new Specimen(
            "Pages that slide",
            "A navigation stack slides a pushed page in from the side and the page under it out. Going back slides the other way.",
            Rooms()));

    private static UiView Gold(float gold, Action<float> set) => Row(
        16f,
        new CountingText(gold) { Format = n => $"{n:0} gold" }.FontSize(22f).Weight(FontWeight.SemiBold).Frame(width: 160f, alignment: Alignment.Leading),
        new Button("Loot a chest", () => set(gold + 250f)) { Style = ButtonStyle.Secondary }.Id("loot"),
        new Button("Buy a sword", () => set(Math.Max(gold - 700f, 0f))) { Style = ButtonStyle.Outline }.Id("buy"));

    private static UiView Health(float health, Action<float> set) => new VStack(HorizontalAlignment.Leading, 16f)
    {
        Row(
            32f,
            new Gauge("Health", health) { Style = MeterStyle.Linear, Tint = Color.Hex("#EF4444"), ValueLabel = $"{health * 100f:0}" }.Frame(width: 280f),
            new Gauge("Shield", health) { ValueLabel = $"{health * 100f:0}" }),
        Row(
            8f,
            new Button("Take a hit", () => set(Math.Max(health - 0.3f, 0f))) { Style = ButtonStyle.Destructive }.Id("feel-hit"),
            new Button("Heal", () => set(1f)) { Style = ButtonStyle.Secondary }.Id("feel-heal")),
    };

    /// <summary>A small tile for a reward that pops in.</summary>
    private static UiView Reward(string name) =>
        new Text(name)
            .Weight(FontWeight.SemiBold)
            .Padding(22f, 12f)
            .Background(new RoundedRectangle(8f).Foreground(Tone.Primary).Opacity(0.12f));

    private static UiView Quest() =>
        new VStack(HorizontalAlignment.Leading, 4f)
        {
            new Text("The drowned bell").Weight(FontWeight.SemiBold),
            new Text("Find the bell under the old pier.").TextStyle(TextStyle.Callout).Foreground(Tone.Muted),
        }
            .Padding(16f, 12f)
            .Background(new RoundedRectangle(8f).Foreground(Tone.Primary).Opacity(0.06f));

    private static UiView Rooms() => new Live<NavigationPath<Room>>(new NavigationPath<Room>(), (path, _) =>
        new NavigationStack<Room>(
            path,
            "Castle",
            new VStack(HorizontalAlignment.Leading, 8f)
            {
                new Button("Go to the armoury", () => path.Push(Room.Armoury)) { Style = ButtonStyle.Menu }.Id("armoury"),
                new Button("Go to the vault", () => path.Push(Room.Vault)) { Style = ButtonStyle.Menu }.Id("vault"),
            }.Padding(12f),
            room => new Text($"The {room.ToString().ToLowerInvariant()} is empty.").Padding(16f))
            .Frame(width: 420f, height: 220f));
}

/// <summary>A view that springs up from nothing when it appears.</summary>
internal sealed record PopIn(UiView Content) : UiView
{
    public override UiView Body => Content.Scale(UseAnimation(1f, 0.5f, Easing.Spring, from: 0f));
}

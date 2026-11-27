using System.Numerics;
using Super;
using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The compass and map story: a compass bar, a round minimap frame and numbers that rise and fade.</summary>
internal static class Wayfinding
{
    private static readonly Color Quest = Color.Hex("#F2C14E");
    private static readonly Color Friend = Color.Hex("#5BA8F5");
    private static readonly Color Foe = Color.Hex("#E2513C");

    public static UiView Story() => Page(
        new Specimen(
            "A compass bar",
            "Compass shows which way the player faces, with marks for what's around. Compass.HeadingOf(camera) gives the heading and Compass.BearingTo(from, to) a mark's bearing.",
            new Live<float>(0f, (heading, turn) => new VStack(HorizontalAlignment.Leading, 16f)
            {
                new Compass(heading)
                {
                    Marks = [new CompassMark(0.6f, Quest, "Quest"), new CompassMark(-1.1f, Friend), new CompassMark(2.8f, Foe)],
                }.Id("compass"),
                Row(
                    12f,
                    new Button("Turn left", () => turn(heading - MathF.PI / 4f)) { Style = ButtonStyle.Outline }.Id("turn-left"),
                    new Button("Turn right", () => turn(heading + MathF.PI / 4f)) { Style = ButtonStyle.Outline }.Id("turn-right")),
            })),
        new Specimen(
            "A round minimap",
            "MinimapFrame clips the game's map to a circle, with north at the top, the player facing their heading in the middle, and marks. A mark past the rim waits on it.",
            Row(
                24f,
                Captioned(new MinimapFrame(Map()) { Heading = 0.5f, Marks = [new MinimapMark(new Vector2(0.4f, -0.3f), Quest), new MinimapMark(new Vector2(-2f, 1f), Foe)] }, "Facing north-east"),
                Captioned(new MinimapFrame(Map().Offset(-40f, 30f)) { Size = 120f, Heading = 3f }, "Smaller, the map panned"))),
        new Specimen(
            "Numbers that rise",
            "FloatingText pops in, rises and fades. FloatingTexts keeps a list of them over points in the world, each placed with .AtWorld.",
            new Live<int>(0, (hits, hit) => Row(
                24f,
                new Button("Hit", () => hit(hits + 1)).Id("hit"),
                new ZStack
                {
                    new Text("Training dummy").Padding(16f).Border(1f, Quest).CornerRadius(8f),
                    hits == 0 ? null : new FloatingText($"{12 + hits % 5 * 7}") { Color = Quest }.Offset(0f, -30f).Id($"hit-{hits}"),
                }))));

    // A painted map: grass, a road and a lake.
    private static UiView Map() => new ZStack
    {
        new Rectangle().Fill(Color.Hex("#4E7A45")),
        new Rectangle().Fill(Color.Hex("#C9B48A")).Frame(width: 18f, height: 400f).Offset(20f, 0f),
        new Rectangle().Fill(Color.Hex("#C9B48A")).Frame(width: 400f, height: 14f).Offset(0f, 30f),
        new Ellipse().Fill(Color.Hex("#3D7CB8")).Frame(width: 70f, height: 50f).Offset(-50f, -40f),
    };
}

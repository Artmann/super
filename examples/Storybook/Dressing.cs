using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The art panels story: cards, buttons and popups drawn from images, and any image drawn as a 9-slice.</summary>
internal static class Dressing
{
    public static UiView ArtPanels() => new ArtPanelsPage();
}

/// <summary>Reads the theme, so it shows the art of the textured theme and the boxes of the others.</summary>
internal sealed record ArtPanelsPage : UiView
{
    public override UiView Body
    {
        get
        {
            var panels = Environment.Theme.Panels;
            return Page(
                panels.Card is null
                    ? new Text("This theme draws boxes. Pick Textured at the top right to see these drawn from art.").Foreground(Tone.Warning)
                    : null,
                new Specimen(
                    "Cards",
                    "A theme's Panels.Card is drawn behind every Card in place of its box, as a 9-slice, so it fits any size.",
                    Row(
                        16f,
                        new Card
                        {
                            new Text("The drowned bell").Weight(FontWeight.SemiBold),
                            new Text("Find the bell under the old pier.").Foreground(Tone.Muted),
                        }.Frame(width: 260f),
                        new Card { new Text("Gold"), new Text("1,240").TextStyle(TextStyle.Title) }.Frame(width: 140f))),
                new Specimen(
                    "Buttons",
                    "Panels.Button is behind the filled styles. Tinted art takes each style's colour, so one pale picture makes them all. Outline, ghost and menu buttons stay as they are.",
                    Row(
                        12f,
                        new Button("Continue", () => { }).Id("art-continue"),
                        new Button("Options", () => { }) { Style = ButtonStyle.Secondary }.Id("art-options"),
                        new Button("Abandon", () => { }) { Style = ButtonStyle.Destructive }.Id("art-abandon"),
                        new Button("Back", () => { }) { Style = ButtonStyle.Outline }.Id("art-back"))),
                new Specimen(
                    "Popups",
                    "Panels.Popup is behind dialogs, sheets, popovers, menus and toasts.",
                    new Live<bool>(false, (asking, ask) => new Button("Rest at the camp", () => ask(true)) { Style = ButtonStyle.Secondary }
                        .Id("art-rest")
                        .Alert(asking, () => ask(false), new Dialog("Rest until morning?", "The fire will burn out and the wolves may come closer."))
                        .Frame(alignment: Alignment.Leading))),
                Slices(panels.Card));
        }
    }

    /// <summary>The card's picture as an image of its own at three sizes, its corners the same in each.</summary>
    private static UiView? Slices(PanelArt? card) => card is null
        ? null
        : new Specimen(
            "Any image as a 9-slice",
            "Image takes Slices, in the image's pixels: its corners keep their size, its edges stretch along and its middle both ways. SliceScale draws the corners bigger.",
            Row(
                16f,
                Captioned(new Image(card.Image) { Slices = card.Slices }.Frame(width: 64f, height: 64f), "64 × 64"),
                Captioned(new Image(card.Image) { Slices = card.Slices }.Frame(width: 240f, height: 64f), "240 × 64"),
                Captioned(new Image(card.Image) { Slices = card.Slices, SliceScale = 2f }.Frame(width: 160f, height: 120f), "SliceScale 2")));
}

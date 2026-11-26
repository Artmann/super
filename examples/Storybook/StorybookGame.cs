using Super;
using Super.Ui;

namespace StorybookExample;

/// <summary>
/// A storybook for Super's interface: a sidebar of stories, each showing an element on its own and in its
/// variations, with a switch between the dark and light themes.
/// </summary>
public sealed class StorybookGame : IGame
{
    private const float SidebarWidth = 232f;

    public StorybookGame(StoryChoice? choice = null)
    {
        Story = choice?.Story is { } id ? Stories.Find(id) : Stories.All[0];
        Theme = choice?.Light == true ? Theme.Light : Theme.Dark;
    }

    /// <summary>The story on screen.</summary>
    public Story Story { get; set; }

    public Theme Theme { get; set; }

    public void Update(GameTime time)
    {
    }

    public UiView? Ui => new HStack(VerticalAlignment.Top, 0f)
    {
        Sidebar(),
        new Divider(),
        Page(),
    }
    .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading)
    .Background(Theme.Colors.Background)
    .Theme(Theme);

    private UiView Sidebar()
    {
        var list = new VStack(HorizontalAlignment.Leading, 2f)
        {
            new VStack(HorizontalAlignment.Leading, 2f)
            {
                new Text("Super UI").TextStyle(TextStyle.Headline),
                new Text("Storybook").TextStyle(TextStyle.Footnote).Foreground(Tone.Muted),
            }.Padding(10f, 4f),
        };

        foreach (var group in Stories.All.GroupBy(story => story.Group))
        {
            list.Add(new Text(group.Key).TextStyle(TextStyle.Caption).Foreground(Tone.Muted).Padding(18f, 10f, 6f, 10f));
            foreach (var story in group)
            {
                list.Add(StoryRow(story));
            }
        }

        return new ScrollView { list.Padding(12f) }
            .Frame(width: SidebarWidth, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading)
            .Background(Theme.Colors.Surface);
    }

    private UiView StoryRow(Story story)
    {
        bool selected = story == Story;
        return new Pressable(
            look => new Text(story.Name)
                .Weight(selected ? FontWeight.Medium : FontWeight.Regular)
                .Foreground(selected ? Tone.Default : Tone.Muted)
                .Padding(10f, 7f)
                .Frame(maxWidth: UiExtensions.Infinity, alignment: Alignment.Leading)
                .Background(selected ? Theme.Colors.Muted : look.Hovered ? Theme.Colors.Hover : default)
                .CornerRadius(Theme.Radius),
            () => Story = story)
            .Id(story.Id);
    }

    private UiView Page() => new VStack(HorizontalAlignment.Leading, 0f)
    {
        new HStack(VerticalAlignment.Center, 16f)
        {
            new VStack(HorizontalAlignment.Leading, 4f)
            {
                new Text(Story.Group).TextStyle(TextStyle.Caption).Foreground(Tone.Muted),
                new Text(Story.Name).TextStyle(TextStyle.Title),
                new Text(Story.Summary).Foreground(Tone.Muted),
            },
            new Spacer(),
            ThemeSwitch(),
        }.Padding(32f, 24f),
        new Divider(),
        new ScrollView { Story.Show().Padding(32f).Frame(maxWidth: UiExtensions.Infinity, alignment: Alignment.TopLeading) },
    }
    .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    /// <summary>Dark and light, side by side in a track, the one in use raised.</summary>
    private UiView ThemeSwitch() => new HStack(Spacing: 2f)
    {
        ThemeOption("Dark", Theme.Dark),
        ThemeOption("Light", Theme.Light),
    }
    .Padding(3f)
    .Background(Theme.Colors.Muted)
    .CornerRadius(Theme.Radius + 3f);

    private UiView ThemeOption(string name, Theme theme)
    {
        bool chosen = Theme == theme;
        return new Pressable(
            look =>
            {
                var option = new Text(name)
                    .TextStyle(TextStyle.Subheadline)
                    .Foreground(chosen || look.Hovered ? Tone.Default : Tone.Muted)
                    .Padding(12f, 5f)
                    .Background(chosen ? Theme.Colors.Background : default)
                    .CornerRadius(Theme.Radius);
                return chosen ? option.Shadow(ShadowSize.Small) : option;
            },
            () => Theme = theme)
            .Id($"theme-{name.ToLowerInvariant()}");
    }
}

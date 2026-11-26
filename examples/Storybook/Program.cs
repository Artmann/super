using Microsoft.Extensions.DependencyInjection;
using StorybookExample;
using Super;

// --story opens one story by its id, such as --story buttons, and --theme light starts in the light theme.
var choice = StoryChoice.Parse(args);
Game.Run<StorybookGame>(
    new GameOptions { Title = "Storybook", Id = "storybook", Width = 1280, Height = 800 },
    services => services.AddSingleton(choice));

using Super;
using Super.Ui;

namespace Examples;

/// <summary>How a scene with a bake is lit, chosen with keys 1, 2 and 3.</summary>
public enum LightingMode
{
    /// <summary>Key 1: every light live and bounced light from the live probes, as if nothing were baked.</summary>
    Live,

    /// <summary>Key 2: the bakes where they are fresh.</summary>
    Baked,

    /// <summary>Key 3: direct light only, with no bake and no bounced light.</summary>
    Direct,
}

/// <summary>
/// Switches the graphics settings between live lighting, the bake and direct light only with keys 1, 2 and 3, and
/// shows the three in a panel in the bottom-left corner, with the current one in bold. The Gallery and the Hamlet share
/// it: this file is linked into both.
/// </summary>
internal sealed class LightingSwitch(Input input, Graphics graphics)
{
    /// <summary>The colour of the panel's lines that aren't on.</summary>
    public static readonly Color Muted = new(0.8f, 0.82f, 0.86f);

    private static readonly Color PanelColor = new(0.06f, 0.07f, 0.09f, 0.72f);

    /// <summary>The lighting now on.</summary>
    public LightingMode Current { get; private set; } = LightingMode.Baked;

    /// <summary>Switches the graphics settings to <paramref name="mode"/>.</summary>
    public void Use(LightingMode mode)
    {
        Current = mode;
        graphics.Baking.BakedLighting = mode == LightingMode.Baked;
        graphics.Lighting.LightProbes = mode != LightingMode.Direct;
    }

    /// <summary>Switches to the lighting whose key was pressed this tick, if any.</summary>
    public void Update()
    {
        if (input.Pressed(Key.D1))
        {
            Use(LightingMode.Live);
        }
        else if (input.Pressed(Key.D2))
        {
            Use(LightingMode.Baked);
        }
        else if (input.Pressed(Key.D3))
        {
            Use(LightingMode.Direct);
        }
    }

    /// <summary>The panel: the three lighting modes, then the lines <paramref name="addLines"/> adds.</summary>
    public UiView Panel(Action<VStack> addLines)
    {
        var modes = new VStack(HorizontalAlignment.Leading, 4f);
        foreach (var (mode, key, name) in new[]
        {
            (LightingMode.Live, 1, "Live lighting"),
            (LightingMode.Baked, 2, "Baked lighting"),
            (LightingMode.Direct, 3, "Direct light only"),
        })
        {
            var text = new Text($"{key}  {name}");
            modes.Add(mode == Current ? text.Bold() : text.Foreground(Muted));
        }

        addLines(modes);

        return modes
            .Padding(12f)
            .Background(PanelColor)
            .CornerRadius(8f)
            .Padding(20f)
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.BottomLeading);
    }
}

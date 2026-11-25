using Super.Ui;

namespace Super.Cook;

/// <summary>Proves a font can be read and has letters, then keeps its bytes as they are.</summary>
public sealed class FontCooker : ICooker
{
    public string Id => "font";

    public int Version => 1;

    public IReadOnlyList<string> Extensions { get; } = [".ttf", ".otf"];

    public void Cook(CookInput input, CookOutput output)
    {
        try
        {
            GameFont.Read(input.Name, input.Bytes);
        }
        catch (ContentException exception)
        {
            foreach (string problem in exception.Problems)
            {
                output.Error(problem);
            }

            return;
        }

        output.Add(ContentKind.Data, input.Bytes);
    }
}

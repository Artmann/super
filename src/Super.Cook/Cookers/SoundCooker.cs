namespace Super.Cook;

/// <summary>Proves a sound decodes, then keeps its bytes as they are.</summary>
public sealed class SoundCooker : ICooker
{
    public string Id => "sound";

    public int Version => 1;

    public IReadOnlyList<string> Extensions { get; } = [".wav", ".ogg", ".mp3"];

    public void Cook(CookInput input, CookOutput output)
    {
        try
        {
            SoundClip.Read(input.Name, input.Bytes);
        }
        catch (ContentException exception)
        {
            foreach (string problem in exception.Problems)
            {
                output.Error(problem);
            }

            return;
        }

        output.Add(ContentKind.Sound, input.Bytes);
    }
}

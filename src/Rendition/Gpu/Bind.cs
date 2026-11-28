namespace Rendition.Gpu;

/// <summary>One resource for a bind group, given by the name the shader calls it.</summary>
internal readonly struct Bind
{
    private Bind(string name, ViewId view, SamplerId sampler)
    {
        Name = name;
        View = view;
        Sampler = sampler;
    }

    public string Name { get; }

    public ViewId View { get; }

    public SamplerId Sampler { get; }

    /// <summary>An image read through a sampler, which GLSL declares as a <c>sampler2D</c> and its kin.</summary>
    public static Bind Image(string name, ViewId view, SamplerId sampler) => new(name, view, sampler);
}

namespace Rendition.Gpu;

/// <summary>One resource for a bind group, given by the name the shader calls it.</summary>
internal readonly struct Bind
{
    private Bind(string name, ViewId view, SamplerId sampler, BufferId buffer)
    {
        Name = name;
        View = view;
        Sampler = sampler;
        BufferId = buffer;
    }

    public string Name { get; }

    public ViewId View { get; }

    public SamplerId Sampler { get; }

    /// <summary>The buffer, for a uniform or storage buffer binding; 0 for an image.</summary>
    public BufferId BufferId { get; }

    /// <summary>An image read through a sampler, which GLSL declares as a <c>sampler2D</c> and its kin.</summary>
    public static Bind Image(string name, ViewId view, SamplerId sampler) => new(name, view, sampler, default);

    /// <summary>An image a compute shader writes or reads texel by texel, which GLSL declares as an <c>image2D</c> and its kin.</summary>
    public static Bind StorageImage(string name, ViewId view) => new(name, view, default, default);

    /// <summary>
    /// A whole uniform or storage buffer, named as GLSL names its instance, such as <c>lights</c> in
    /// <c>buffer Lights { … } lights;</c>, or by its block, <c>Lights</c>, when it has no instance name.
    /// </summary>
    public static Bind Buffer(string name, BufferId buffer) => new(name, default, default, buffer);
}

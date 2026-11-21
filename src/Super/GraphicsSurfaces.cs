namespace Super;

/// <summary>What lies on surfaces and moves on them: their materials, wear, height, water, wind, decals and particles.</summary>
public sealed class GraphicsSurfaces
{
    /// <summary>
    /// Every surface's own colour, textures, normal maps and shine. Off, everything is the same light grey,
    /// matte clay, shaped only by its mesh, so the lighting and the models can be judged without the
    /// textures in the way. Glows still glow, cut-outs still cut and glass stays see-through. It's
    /// for looking, not for speed, so <see cref="Graphics.Fastest"/> leaves it on.
    /// </summary>
    public bool Materials { get; set; } = true;

    /// <summary>
    /// Worn edges and dirt that models' materials lay by masks the cooker baked from their shape: paler,
    /// rubbed edges, and darker hollows and feet. Off, those surfaces show only their own colour and shine,
    /// and their masks aren't read.
    /// </summary>
    public bool WearAndDirt { get; set; } = true;

    /// <summary>
    /// The depth that materials' height maps give their surfaces, so bricks stand out from the mortar and stones from the
    /// path as the view moves. Off, those surfaces are flat, shaded only by their normal maps, and the height maps aren't
    /// stepped through. It costs up to 32 lookups on each near pixel, so <see cref="GraphicsQuality.Low"/> and
    /// <see cref="Graphics.Fastest"/> turn it off.
    /// </summary>
    public bool HeightMaps { get; set; } = true;

    /// <summary>
    /// Water and a damp band on surfaces whose material asks for puddles, below and just above the level set with
    /// <see cref="ViewSurroundings.Puddles"/>. Off, every surface is dry. It costs a few noise lookups on each wet pixel, so
    /// <see cref="Graphics.Fastest"/> turns it off.
    /// </summary>
    public bool Puddles { get; set; } = true;

    /// <summary>
    /// Grass, leaves and anything else whose material sways in the wind set with <see cref="ViewSurroundings.Wind"/>, along with
    /// their shadows. Off, everything stands still. It costs a little on each swaying vertex, in the picture and in
    /// every shadow map, so <see cref="Graphics.Fastest"/> turns it off.
    /// </summary>
    public bool Wind { get; set; } = true;

    /// <summary>
    /// The stains, grime and damp laid onto surfaces with <see cref="ViewEffects.Decal"/>. Off, they aren't drawn and the
    /// surfaces show as they are. It costs a copy of the normal target and a pass over the boxes' pixels, so
    /// <see cref="Graphics.Fastest"/> turns it off.
    /// </summary>
    public bool Decals { get; set; } = true;

    /// <summary>
    /// Light spreading under the surface of skin and wax, from a material's <c>Subsurface</c>. Off, those surfaces are
    /// lit as plain matte and light no longer shines through their thin parts, which saves two blurs across the picture.
    /// <see cref="Graphics.Fastest"/> turns it off.
    /// </summary>
    public bool Subsurface { get; set; } = true;

    /// <summary>Particles, such as dust, smoke, sparks and flames. Off, none is uploaded or drawn.</summary>
    public bool Particles { get; set; } = true;

    /// <summary>
    /// The blades grown over models with a <see cref="Super.Grass"/> component. Off, no blade is scattered or drawn and the
    /// ground shows bare. It costs a scatter pass, the blades in the picture and in the two nearest shadow cascades, so
    /// <see cref="Graphics.Fastest"/> turns it off; <see cref="GraphicsDrawing.GrassDetail"/> sets how far it reaches.
    /// </summary>
    public bool Grass { get; set; } = true;

    /// <summary>Takes every setting from <paramref name="other"/>.</summary>
    internal void CopyFrom(GraphicsSurfaces other)
    {
        Materials = other.Materials;
        WearAndDirt = other.WearAndDirt;
        HeightMaps = other.HeightMaps;
        Puddles = other.Puddles;
        Wind = other.Wind;
        Decals = other.Decals;
        Subsurface = other.Subsurface;
        Particles = other.Particles;
        Grass = other.Grass;
    }
}

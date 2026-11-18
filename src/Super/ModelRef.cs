namespace Super;

/// <summary>
/// Names a model. <see cref="Content"/> hands these out, and there are built-in shapes one
/// unit wide, centred on zero. Build other sizes with <see cref="Shapes"/>.
/// </summary>
public readonly record struct ModelRef(int Index)
{
    /// <summary>A cube one unit wide, centred on zero.</summary>
    public static ModelRef Cube { get; } = new(0);

    /// <summary>A sphere one unit across.</summary>
    public static ModelRef Sphere { get; } = new(-1);

    /// <summary>A capsule standing up, one unit wide and two tall, like a person.</summary>
    public static ModelRef Capsule { get; } = new(-2);

    /// <summary>A cylinder standing up, one unit wide and one tall.</summary>
    public static ModelRef Cylinder { get; } = new(-3);

    /// <summary>A cone pointing up, one unit wide at the base and one tall.</summary>
    public static ModelRef Cone { get; } = new(-4);

    /// <summary>A one-sided square one unit wide, lying flat and facing up.</summary>
    public static ModelRef Plane { get; } = new(-5);

    /// <summary>A one-sided square one unit wide, standing up and facing along z.</summary>
    public static ModelRef Quad { get; } = new(-6);

    /// <summary>A ring lying flat, one unit across, with a tube a quarter unit thick.</summary>
    public static ModelRef Torus { get; } = new(-7);

    /// <summary>Whether this is the cube or one of the other shapes every game has.</summary>
    public bool IsBuiltIn => Index <= 0;
}

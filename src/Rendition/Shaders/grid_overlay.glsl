// The grid overlays of a frame and the bits of the cells they cover. Must match GpuGridOverlay in
// Passes/GridOverlayBatch.cs.

struct GridOverlay {
    mat4 toClip;      // The overlay's box, the cube from -0.5 to 0.5, to the screen.
    mat4 fromClip;    // The screen back into the box.
    vec4 area;        // The box's lowest corner on the grid's X and Z, then its size.
    vec4 cell;        // The cell size, then how far the box reaches above and below the grid.
    vec4 lineColor;   // sRGB, with opacity.
    vec4 fillColor;
    vec4 borderColor;
    vec4 widths;      // Line width, border width, softness and focus radius, in metres.
    vec4 focus;       // Where the lines are strongest, on the grid's X and Z.
    ivec4 window;     // The first cell's two numbers, then the window's columns and rows.
    ivec4 info;       // The first word of the window's bits, then the shape.
};

layout(std430, set = 1, binding = 0) readonly buffer Overlays {
    GridOverlay overlays[];
};

layout(std430, set = 1, binding = 1) readonly buffer Cells {
    uint cellWords[];
};

const int SHAPE_SQUARE = 0;
const int SHAPE_POINTY = 1;
const int SHAPE_FLAT = 2;

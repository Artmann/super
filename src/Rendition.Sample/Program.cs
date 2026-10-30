using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;

// --check runs a fixed number of frames with validation on, then fails if the
// renderer produced Vulkan errors or allocated managed memory while rendering.
bool check = args.Contains("--check");
bool validation = check || args.Contains("--validate");

const int warmupFrames = 240;
const int checkFrames = 900;

unsafe
{
    var glfw = Glfw.GetApi();
    if (!glfw.Init())
    {
        throw new InvalidOperationException("Could not initialize GLFW.");
    }

    glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
    var window = glfw.CreateWindow(1280, 720, "Rendition", null, null);
    glfw.GetFramebufferSize(window, out int width, out int height);

    var renderer = new Renderer(new RendererOptions
    {
        InstanceExtensions = GetInstanceExtensions(glfw),
        CreateSurface = instance =>
        {
            VkNonDispatchableHandle surface;
            int result = glfw.CreateWindowSurface(new VkHandle(instance), window, null, &surface);
            return result == 0
                ? surface.Handle
                : throw new InvalidOperationException($"Could not create a window surface: {result}");
        },
        Width = width,
        Height = height,
        EnableValidation = validation,
    });

    var cube = renderer.CreateMesh(Cube.Vertices, Cube.Indices);

    var cameraPosition = new Vector3(0f, 1.2f, 3.5f);
    var viewMatrix = Matrix4x4.CreateLookAt(cameraPosition, Vector3.Zero, Vector3.UnitY);
    var draws = new DrawItem[1];
    draws[0].Mesh = cube;
    draws[0].Material = new Material
    {
        BaseColor = new Vector3(0.9f, 0.55f, 0.2f),
        Metallic = 0.9f,
        Roughness = 0.35f,
    };

    var clock = Stopwatch.StartNew();
    int frame = 0;
    long allocatedAtWarmup = 0;

    while (!glfw.WindowShouldClose(window) && glfw.GetKey(window, Keys.Escape) != (int)InputAction.Press)
    {
        glfw.PollEvents();
        glfw.GetFramebufferSize(window, out width, out height);

        float time = (float)clock.Elapsed.TotalSeconds;
        draws[0].Transform = Matrix4x4.CreateRotationY(time * 0.8f) * Matrix4x4.CreateRotationX(time * 0.5f);

        var view = new FrameView
        {
            Width = width,
            Height = height,
            View = viewMatrix,
            Projection = Projection.PerspectiveInfiniteReversedZ(
                MathF.PI / 3f, width / (float)Math.Max(height, 1), 0.1f),
            CameraPosition = cameraPosition,
            Sun = new DirectionalLight
            {
                Direction = Vector3.Normalize(new Vector3(0.5f, 0.8f, 0.6f)),
                Radiance = new Vector3(3.5f, 3.3f, 3.0f),
            },
            AmbientRadiance = new Vector3(0.12f, 0.14f, 0.18f),
        };

        renderer.Render(view, draws);
        frame++;

        if (check)
        {
            if (frame == warmupFrames)
            {
                allocatedAtWarmup = GC.GetAllocatedBytesForCurrentThread();
            }
            else if (frame == warmupFrames + checkFrames)
            {
                break;
            }
        }
    }

    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedAtWarmup;
    int validationErrors = renderer.ValidationErrors;

    renderer.Dispose();
    glfw.DestroyWindow(window);
    glfw.Terminate();

    if (check)
    {
        Console.WriteLine($"{checkFrames} frames: {allocated} bytes allocated, {validationErrors} validation messages");
        return allocated == 0 && validationErrors == 0 ? 0 : 1;
    }

    return 0;
}

static unsafe string[] GetInstanceExtensions(Glfw glfw)
{
    byte** names = glfw.GetRequiredInstanceExtensions(out uint count);
    var extensions = new string[count];
    for (int i = 0; i < count; i++)
    {
        extensions[i] = Marshal.PtrToStringUTF8((nint)names[i])!;
    }

    return extensions;
}

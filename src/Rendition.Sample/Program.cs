using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;

// --check runs a fixed number of frames with validation on, then fails if the
// renderer produced Vulkan errors or allocated managed memory while rendering.
// --model <file> shows a cooked model instead of the spinning cube.
bool check = args.Contains("--check");
bool validation = check || args.Contains("--validate");
int modelArgument = Array.IndexOf(args, "--model");
string? modelPath = modelArgument >= 0 ? args[modelArgument + 1] : null;

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

    DrawItem[] draws;
    OrbitCamera camera;
    if (modelPath is not null)
    {
        draws = renderer.CreateModel(ModelData.Read(modelPath));
        camera = new OrbitCamera { Target = new Vector3(0f, 0.5f, 0f), Yaw = 0.7f, Pitch = 0.3f, Distance = 7f };
    }
    else
    {
        var material = renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0.9f, 0.55f, 0.2f, 1f),
            Metallic = 0.9f,
            Roughness = 0.35f,
        });
        draws =
        [
            new DrawItem
            {
                Mesh = renderer.CreateMesh(Cube.Vertices, Cube.Indices),
                IndexCount = (uint)Cube.Indices.Length,
                Material = material,
            },
        ];
        camera = new OrbitCamera { Yaw = 0f, Pitch = 0.33f, Distance = 3.7f };
    }

    float wheelSteps = 0f;
    GlfwCallbacks.ScrollCallback onScroll = (_, _, y) => wheelSteps += (float)y;
    glfw.SetScrollCallback(window, onScroll);

    var clock = Stopwatch.StartNew();
    int frame = 0;
    long allocatedAtWarmup = 0;
    bool dragging = false;
    double lastX = 0, lastY = 0;

    while (!glfw.WindowShouldClose(window) && glfw.GetKey(window, Keys.Escape) != (int)InputAction.Press)
    {
        glfw.PollEvents();
        glfw.GetFramebufferSize(window, out width, out height);

        glfw.GetCursorPos(window, out double x, out double y);
        bool buttonDown = glfw.GetMouseButton(window, (int)MouseButton.Left) == (int)InputAction.Press;
        if (buttonDown && dragging)
        {
            camera.Orbit((float)(x - lastX), (float)(y - lastY));
        }

        dragging = buttonDown;
        lastX = x;
        lastY = y;
        camera.Zoom(wheelSteps);
        wheelSteps = 0f;

        if (modelPath is null)
        {
            float time = (float)clock.Elapsed.TotalSeconds;
            draws[0].Transform = Matrix4x4.CreateRotationY(time * 0.8f) * Matrix4x4.CreateRotationX(time * 0.5f);
        }

        var view = new FrameView
        {
            Width = width,
            Height = height,
            View = camera.View,
            Projection = Projection.PerspectiveInfiniteReversedZ(
                MathF.PI / 4f, width / (float)Math.Max(height, 1), 0.1f),
            CameraPosition = camera.Position,
            Sun = new DirectionalLight
            {
                Direction = Vector3.Normalize(new Vector3(0.5f, 0.8f, 0.6f)),
                Radiance = new Vector3(3.5f, 3.3f, 3.0f),
            },
            AmbientRadiance = new Vector3(0.45f, 0.48f, 0.55f),
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
    GC.KeepAlive(onScroll);
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

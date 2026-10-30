using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;

// --check runs a fixed number of frames with validation on, then fails if the
// renderer produced Vulkan errors or allocated managed memory while rendering.
// --screenshot <file.png> saves one frame, with validation on, then quits.
// Neither of those shows a window, so they can run while someone works.
// --model <file> shows a cooked model instead of the spinning cube.
// --yaw, --pitch and --distance <number> place the camera.
bool check = args.Contains("--check");
string? screenshotPath = StringOption(args, "--screenshot");
bool hidden = check || screenshotPath is not null;
bool validation = hidden || args.Contains("--validate");
string? modelPath = StringOption(args, "--model");

const int warmupFrames = 240;
const int checkFrames = 900;
const int screenshotFrame = 3;

unsafe
{
    var glfw = Glfw.GetApi();
    if (!glfw.Init())
    {
        throw new InvalidOperationException("Could not initialize GLFW.");
    }

    glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
    glfw.WindowHint(WindowHintBool.Visible, !hidden);
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
        var floor = new DrawItem
        {
            Mesh = renderer.CreateMesh(Ground.Vertices, Ground.Indices),
            IndexCount = (uint)Ground.Indices.Length,
            Material = renderer.CreateMaterial(new Material
            {
                BaseColor = new Vector4(0.42f, 0.42f, 0.43f, 1f),
                Roughness = 0.9f,
            }),
            Transform = Matrix4x4.Identity,
        };
        draws = [.. renderer.CreateModel(ModelData.Read(modelPath)), floor];
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

    camera.Yaw = NumberOption(args, "--yaw", camera.Yaw);
    camera.Pitch = NumberOption(args, "--pitch", camera.Pitch);
    camera.Distance = NumberOption(args, "--distance", camera.Distance);

    // The studio the car reflects, with a softbox lined up with each of the three lights.
    renderer.SetEnvironment(new EnvironmentDescription
    {
        Ceiling = new Vector3(0.2f, 0.2f, 0.21f),
        Horizon = new Vector3(0.42f, 0.42f, 0.44f),
        Floor = new Vector3(0.3f, 0.3f, 0.31f),
        Panels = (EnvironmentPanel[])
        [
            new()
            {
                Direction = new Vector3(0.1f, 1f, 0.05f),
                HalfWidth = 0.55f,
                HalfHeight = 1.0f,
                Radiance = new Vector3(3.5f, 3.45f, 3.3f),
            },
            new()
            {
                Direction = new Vector3(-0.8f, 0.35f, 0.5f),
                HalfWidth = 0.7f,
                HalfHeight = 0.35f,
                Radiance = new Vector3(1.4f, 1.5f, 1.7f),
            },
            new()
            {
                Direction = new Vector3(-0.15f, 0.45f, -0.9f),
                HalfWidth = 1.1f,
                HalfHeight = 0.25f,
                Radiance = new Vector3(3.5f, 3.5f, 3.7f),
            },
        ],
    });

    float wheelSteps = 0f;
    GlfwCallbacks.ScrollCallback onScroll = (_, _, y) => wheelSteps += (float)y;
    glfw.SetScrollCallback(window, onScroll);

    // Three-point lighting: a strong key from above, a soft fill on the opposite side to
    // lift the shadows, and a rim from behind to separate the car from the backdrop.
    DirectionalLight[] lights =
    [
        new()
        {
            Direction = new Vector3(0.35f, 0.85f, 0.4f),
            Radiance = new Vector3(2.8f, 2.75f, 2.6f),
            CastsShadow = true,
        },
        new() { Direction = new Vector3(-0.8f, 0.35f, 0.5f), Radiance = new Vector3(0.65f, 0.72f, 0.82f) },
        new() { Direction = new Vector3(-0.15f, 0.45f, -0.9f), Radiance = new Vector3(1.6f, 1.6f, 1.68f) },
    ];

    var clock = Stopwatch.StartNew();
    int frame = 0;
    long allocatedAtWarmup = 0;
    bool dragging = false;
    double lastX = 0, lastY = 0;

    while (!glfw.WindowShouldClose(window) && glfw.GetKey(window, Keys.Escape) != (int)InputAction.Press)
    {
        // Resizing allocates, so --check tries it during warmup, before it starts counting.
        if (check)
        {
            switch (frame)
            {
                case 60: glfw.SetWindowSize(window, 800, 600); break;
                case 120: glfw.SetWindowSize(window, 1600, 500); break;
                case 180: glfw.SetWindowSize(window, 300, 900); break;
                case 210: glfw.SetWindowSize(window, 1280, 720); break;
            }
        }

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
            Lights = lights,
            Shadows = new ShadowSettings { Center = new Vector3(0f, 0.5f, 0f), Radius = 5f, LightAngle = 0.12f },
            EnvironmentIntensity = 1f,
            BackgroundRadiance = new Vector3(0.47f, 0.47f, 0.485f),
            BackgroundFadeStart = 12f,
            BackgroundFadeEnd = 40f,
        };

        if (screenshotPath is not null && frame == screenshotFrame)
        {
            byte[] pixels = renderer.Capture(view, draws, out int capturedWidth, out int capturedHeight);
            PngFile.Write(screenshotPath, pixels, capturedWidth, capturedHeight);
            break;
        }

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

    if (screenshotPath is not null)
    {
        Console.WriteLine($"{screenshotPath}: {validationErrors} validation messages");
        return validationErrors == 0 ? 0 : 1;
    }

    return 0;
}

static string? StringOption(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 ? args[index + 1] : null;
}

static float NumberOption(string[] args, string name, float fallback) =>
    StringOption(args, name) is { } text ? float.Parse(text, CultureInfo.InvariantCulture) : fallback;

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

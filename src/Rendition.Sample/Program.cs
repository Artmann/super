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
// --yaw, --pitch, --distance and --height <number> place the camera.
// --glow draws the cube with a custom surface shader.
bool check = args.Contains("--check");
string? screenshotPath = StringOption(args, "--screenshot");
bool hidden = check || screenshotPath is not null;
bool validation = hidden || args.Contains("--validate");
string? modelPath = StringOption(args, "--model");
bool glow = args.Contains("--glow");

const int warmupFrames = 240;
const int checkFrames = 900;

// Anti-aliasing builds up over several frames, so a screenshot waits for it to settle.
const int screenshotFrame = 48;

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
    DrawItem[] probeDraws = [];
    OrbitCamera camera;
    float shadowRadius = 5f;
    float shadowHeight = 0.5f;
    if (modelPath is not null)
    {
        var floor = new DrawItem
        {
            Mesh = renderer.CreateMesh(Ground.Vertices, Ground.Indices),
            IndexCount = (uint)Ground.Indices.Length,
            Material = renderer.CreateMaterial(new Material
            {
                BaseColor = new Vector4(0.13f, 0.13f, 0.14f, 1f),
                Roughness = 0.26f,
            }),
            Transform = Matrix4x4.Identity,
        };
        var model = ModelData.Read(modelPath);
        draws = [.. renderer.CreateModel(model), floor];
        probeDraws = [floor];

        // Frames the model by its size, so a person and a car both fill the window.
        var size = Vector3.Zero;
        foreach (ref readonly var vertex in model.Vertices.AsSpan())
        {
            size = Vector3.Max(size, Vector3.Abs(vertex.Position));
        }

        camera = new OrbitCamera
        {
            Target = new Vector3(0f, size.Y * 0.5f, 0f),
            Yaw = 0.7f,
            Pitch = 0.3f,
            Distance = 3f * MathF.Max(size.X, MathF.Max(size.Y * 0.5f, size.Z)),
        };

        // A shadow map spread over less ground gives a smaller model sharper shadows.
        shadowRadius = 2.15f * MathF.Max(size.X, MathF.Max(size.Y * 0.5f, size.Z));
        shadowHeight = size.Y * 0.5f;
    }
    else
    {
        var glowing = new Material
        {
            BaseColor = new Vector4(0.9f, 0.55f, 0.2f, 1f),
            Metallic = 0.9f,
            Roughness = 0.35f,
            Shader = glow ? renderer.CreateSurfaceShader(LoadShader("glow_bands.frag")) : null,
        };
        glowing.ShaderParameters[0] = new Vector4(4f, 1.2f, 0.3f, 3f);
        var material = renderer.CreateMaterial(glowing);
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
    camera.Target.Y = NumberOption(args, "--height", camera.Target.Y);

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
                Radiance = new Vector3(1.9f, 1.9f, 2.0f),
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
            Radiance = new Vector3(2.2f, 2.16f, 2.05f),
            CastsShadow = true,
        },
        new() { Direction = new Vector3(-0.8f, 0.35f, 0.5f), Radiance = new Vector3(0.65f, 0.72f, 0.82f) },
        new() { Direction = new Vector3(-0.15f, 0.45f, -0.9f), Radiance = new Vector3(0.55f, 0.55f, 0.58f) },
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

        float time = (float)clock.Elapsed.TotalSeconds;
        if (modelPath is null)
        {
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
            Time = time,
            Lights = lights,
            Shadows = new ShadowSettings { Center = new Vector3(0f, shadowHeight, 0f), Radius = shadowRadius, LightAngle = 0.12f },
            EnvironmentIntensity = 0.85f,
            BackgroundRadiance = new Vector3(0.15f, 0.15f, 0.16f),
            BackgroundFadeStart = 9f,
            BackgroundFadeEnd = 26f,
            Post = new PostSettings { Contrast = 0.08f, Saturation = 0.1f, Vignette = 0.3f, Sharpen = 0.4f, Grain = 0.006f },
        };

        // The car sees the floor and the shadow it casts there. It leaves itself out of
        // the picture, because the probe sits inside it. The room has a floor but no real
        // walls, so the box only needs to be accurate at the bottom.
        if (probeDraws.Length > 0)
        {
            renderer.CaptureReflectionProbe(
                new ReflectionProbe
                {
                    Position = new Vector3(0f, 0.6f, 0f),
                    BoxMin = new Vector3(-200f, -0.02f, -200f),
                    BoxMax = new Vector3(200f, 200f, 200f),
                    BlendDistance = 0.01f,
                },
                view,
                probeDraws,
                draws);
            probeDraws = [];
        }

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

static byte[] LoadShader(string name) =>
    File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "shaders", name + ".spv"));

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

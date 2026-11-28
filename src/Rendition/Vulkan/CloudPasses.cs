using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The clouds' three compute passes on the GPU layer: cloud_shadow.comp draws the shadow round the camera,
/// cloud_march.comp marches a quarter of the picture's pixels through the clouds, and cloud_resolve.comp fills out the
/// rest from last frame's picture. Each reads the frame set and a group laid out by Shaders/cloud_sets.glsl, with one
/// group for each pass and each of the two histories the resolve writes in turn, since a group belongs to one pipeline.
/// </summary>
internal sealed unsafe class CloudPasses : IDisposable
{
    private const int Shadow = 0;
    private const int March = 1;
    private const int Resolve = 2;

    private readonly VulkanGpu _gpu;
    private readonly CloudNoise _noise;
    private readonly ComputePass[] _passes;
    private readonly ImageGroup[] _groups = new ImageGroup[6];
    private readonly CommandList _commands = new();
    private ImageView _skyView;
    private CloudTargets? _targets;

    public CloudPasses(VulkanGpu gpu, DescriptorSetLayout frameSetLayout, CloudNoise noise)
    {
        _gpu = gpu;
        _noise = noise;
        _passes =
        [
            new ComputePass(gpu, "cloud_shadow.comp", [frameSetLayout]),
            new ComputePass(gpu, "cloud_march.comp", [frameSetLayout]),
            new ComputePass(gpu, "cloud_resolve.comp", [frameSetLayout]),
        ];
        var linear = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
        for (int i = 0; i < _groups.Length; i++)
        {
            _groups[i] = new ImageGroup(gpu, linear);
        }
    }

    /// <summary>
    /// Points the march at the physical sky's sky-view table, which rests in General and which it takes the sky's light
    /// from. Only while no frame in flight uses the groups.
    /// </summary>
    public void SetSources(ImageView skyView)
    {
        _skyView = skyView;
        MakeGroups();
    }

    /// <summary>Points the groups at new targets and at the noise. Only while no frame in flight uses them.</summary>
    public void PointAt(CloudTargets targets)
    {
        _targets = targets;
        MakeGroups();
    }

    /// <summary>
    /// Draws the shadow map when <paramref name="shadowed"/>, marches the clouds and fills out their picture into
    /// history <paramref name="history"/>. The layer's barriers let each pass see what the one before it wrote, and
    /// the lighting see the picture.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, CloudTargets targets, int history, bool shadowed)
    {
        var frame = _gpu.Adopt(frameSet);
        _commands.Reset();
        if (shadowed)
        {
            const int shadowGroups = CloudModel.ShadowResolution / 8;
            Run(Shadow, (frame, history), (shadowGroups, shadowGroups));
        }

        Run(March, (frame, history), (Groups(targets.MarchSize.Width), Groups(targets.MarchSize.Height)));
        Run(Resolve, (frame, history), (Groups(targets.Size.Width), Groups(targets.Size.Height)));
        _gpu.Play(_commands, cmd);
    }

    public void Dispose()
    {
        foreach (var group in _groups)
        {
            group.Dispose();
        }

        foreach (var pass in _passes)
        {
            pass.Dispose();
        }
    }

    private static int Groups(int texels) => (texels + 7) / 8;

    private void Run(int pass, (BindGroupId Frame, int History) sets, (int X, int Y) workgroups) =>
        _passes[pass].Record(_commands, [sets.Frame, _groups[pass * 2 + sets.History].Id], [], (workgroups.X, workgroups.Y, 1));

    // Makes every group once the noise volumes, the sky-view and the targets are all there: group i of a pass writes
    // history i and reads the other as last frame's.
    private void MakeGroups()
    {
        if (_noise.Volumes is not { } volumes || _skyView.Handle == 0 || _targets is not { } targets)
        {
            return;
        }

        var repeat = _noise.RepeatId;
        for (int pass = 0; pass < _passes.Length; pass++)
        {
            for (int history = 0; history < 2; history++)
            {
                _groups[pass * 2 + history].Make(_passes[pass].Pipeline, 1,
                [
                    new("cloudShapeTexture", volumes.Shape, repeat, GroupImageUse.Stored),
                    new("cloudDetailTexture", volumes.Detail, repeat, GroupImageUse.Stored),
                    new("cloudWeatherTexture", _noise.Weather, repeat, GroupImageUse.Stored),
                    new("cloudSkyView", _skyView, Use: GroupImageUse.Stored),
                    new("cloudMarchTarget", targets.March.View, Use: GroupImageUse.Written),
                    new("cloudMarchTexture", targets.March.View, Use: GroupImageUse.Stored),
                    new("cloudHistoryTexture", targets.Histories[1 - history].View, Use: GroupImageUse.Stored),
                    new("cloudPictureTarget", targets.Picture.View, Use: GroupImageUse.Written),
                    new("cloudShadowTarget", targets.Shadow.View, Use: GroupImageUse.Written),
                    new("cloudNextHistoryTarget", targets.Histories[history].View, Use: GroupImageUse.Written),
                ]);
            }
        }
    }
}

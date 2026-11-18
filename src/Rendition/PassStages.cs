using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>The pipeline stages and accesses the frame's barriers name most.</summary>
internal static class PassStages
{
    public const PipelineStageFlags2 SampledStage = PipelineStageFlags2.FragmentShaderBit;

    // The shadow maps are read by the lighting pass and by the volumetric fog's compute pass.
    public const PipelineStageFlags2 ShadowReadStages = SampledStage | PipelineStageFlags2.ComputeShaderBit;
    public const AccessFlags2 SampledAccess = AccessFlags2.ShaderSampledReadBit;
    public const PipelineStageFlags2 ColorStage = PipelineStageFlags2.ColorAttachmentOutputBit;
    public const AccessFlags2 ColorWrite = AccessFlags2.ColorAttachmentWriteBit;
    public const PipelineStageFlags2 DepthStages =
        PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
    public const AccessFlags2 DepthWrite = AccessFlags2.DepthStencilAttachmentWriteBit;
}

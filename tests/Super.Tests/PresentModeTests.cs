using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Super.Tests;

public sealed class PresentModeTests
{
    [Fact]
    public void Immediate_comes_first()
    {
        Assert.Equal(
            PresentModeKHR.ImmediateKhr,
            Swapchain.ChoosePresentMode([PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr, PresentModeKHR.ImmediateKhr]));
    }

    [Fact]
    public void Mailbox_is_next()
    {
        Assert.Equal(PresentModeKHR.MailboxKhr, Swapchain.ChoosePresentMode([PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr]));
    }

    [Fact]
    public void Otherwise_vsync_stays_on()
    {
        Assert.Equal(PresentModeKHR.FifoKhr, Swapchain.ChoosePresentMode([PresentModeKHR.FifoKhr]));
    }
}

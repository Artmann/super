using Rendition.Vulkan;

namespace Super.Tests;

public sealed class ResourceTableSizeTests
{
    [Fact]
    public void A_gpu_that_allows_many_descriptors_in_a_set_gets_every_texture_slot()
    {
        Assert.Equal(1024u, ResourceTable.TexturesFor(1_000_000));
    }

    [Fact]
    public void Moltenvks_limit_leaves_room_for_the_texture_arrays_and_the_materials()
    {
        // 1212 descriptors, less 256 texture arrays and the materials' buffer.
        Assert.Equal(955u, ResourceTable.TexturesFor(1212));
    }

    [Fact]
    public void A_gpu_with_too_few_descriptors_is_refused_with_a_reason()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ResourceTable.TexturesFor(400));

        Assert.Equal(
            "This GPU allows 400 descriptors in one set, too few for the renderer's table of every texture.",
            error.Message);
    }
}

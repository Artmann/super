using Rendition.Vulkan;

namespace Super.Tests;

public sealed class MemoryArenaTests
{
    [Fact]
    public void Allocations_respect_alignment()
    {
        var arena = new MemoryArena(4096);
        Assert.True(arena.TryAllocate(100, 256, out ulong first));
        Assert.True(arena.TryAllocate(100, 256, out ulong second));
        Assert.Equal(0ul, first);
        Assert.Equal(256ul, second);
    }

    [Fact]
    public void Freed_neighbours_merge()
    {
        var arena = new MemoryArena(3000);
        Assert.True(arena.TryAllocate(1000, 1, out ulong a));
        Assert.True(arena.TryAllocate(1000, 1, out ulong b));
        Assert.True(arena.TryAllocate(1000, 1, out ulong c));
        Assert.False(arena.TryAllocate(1, 1, out _));

        arena.Free(b);
        arena.Free(a);
        arena.Free(c);

        Assert.True(arena.TryAllocate(3000, 1, out ulong whole));
        Assert.Equal(0ul, whole);
    }

    [Fact]
    public void Best_fit_picks_the_smallest_hole()
    {
        var arena = new MemoryArena(10_000);
        Assert.True(arena.TryAllocate(500, 1, out ulong bigHole));
        Assert.True(arena.TryAllocate(10, 1, out _));
        Assert.True(arena.TryAllocate(200, 1, out ulong smallHole));
        Assert.True(arena.TryAllocate(10, 1, out _));
        arena.Free(bigHole);
        arena.Free(smallHole);

        Assert.True(arena.TryAllocate(200, 1, out ulong offset));
        Assert.Equal(smallHole, offset);
    }

    [Fact]
    public void Too_large_fails_and_leaves_the_arena_untouched()
    {
        var arena = new MemoryArena(1000);
        Assert.False(arena.TryAllocate(1001, 1, out _));
        Assert.Equal(0ul, arena.Used);
        Assert.True(arena.TryAllocate(1000, 1, out _));
        Assert.Equal(1000ul, arena.Used);
    }

    [Fact]
    public void Freeing_twice_throws()
    {
        var arena = new MemoryArena(1000);
        Assert.True(arena.TryAllocate(10, 1, out ulong offset));
        arena.Free(offset);
        Assert.Throws<InvalidOperationException>(() => arena.Free(offset));
    }

    [Fact]
    public void Used_tracks_allocations_without_alignment_padding()
    {
        var arena = new MemoryArena(1000);
        arena.TryAllocate(10, 1, out _);
        arena.TryAllocate(10, 64, out ulong aligned);
        Assert.Equal(64ul, aligned);
        Assert.Equal(20ul, arena.Used);
        Assert.False(arena.IsEmpty);
    }
}

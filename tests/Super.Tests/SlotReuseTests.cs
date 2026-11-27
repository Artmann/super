using System.Numerics;
using Rendition;
using Super.Testing;

namespace Super.Tests;

/// <summary>Unloaded models, textures and interface images give their slot to what's named next, and old refs stay unloaded.</summary>
public class SlotReuseTests
{
    private static readonly Vertex[] Triangle =
    [
        new(new Vector3(0f, 0f, 0f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.Zero),
        new(new Vector3(1f, 0f, 0f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.UnitX),
        new(new Vector3(0f, 0f, 1f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.UnitY),
    ];

    private static GeneratedMesh Mesh(Surface? surface = null) => new(Triangle, [0, 1, 2], surface ?? new Surface());

    private static MemoryContent Textures(params string[] names)
    {
        var files = new MemoryContent();
        foreach (string name in names)
        {
            files.Add(name, Ui.ImageTests.Png(2, 2));
        }

        return files;
    }

    [Fact]
    public void Streaming_meshes_in_and_out_keeps_the_slots_from_growing()
    {
        var content = new Content(new MemoryContent());
        var kept = content.Mesh(Mesh());
        for (int i = 0; i < 350; i++)
        {
            content.Models.Unload(content.Mesh(Mesh()));
        }

        Assert.Equal(2, content.Store.Items.Count);
        Assert.Equal(1, kept.Index);
    }

    [Fact]
    public void A_ref_kept_after_its_model_was_unloaded_still_says_so_once_another_takes_its_slot()
    {
        var content = new Content(new MemoryContent());
        var view = new View { Sources = (content, null) };
        var old = content.Mesh(Mesh());
        content.Models.Unload(old);

        var taken = content.Mesh(Mesh());

        Assert.Equal(old.Index, taken.Index);
        Assert.NotEqual(old, taken);
        Assert.False(content.Models.IsReady(old));
        Assert.True(content.Models.IsReady(taken));
        view.Begin(0f);
        var drawn = Assert.Throws<InvalidOperationException>(() => view.Model(1, old, Matrix4x4.Identity));
        Assert.Equal($"Model {old.Index} was unloaded, so it can't be drawn.", drawn.Message);
        var again = Assert.Throws<ArgumentException>(() => content.Models.Unload(old));
        Assert.Equal($"model names {old.Index}, which was unloaded. (Parameter 'model')", again.Message);
        view.Model(1, taken, Matrix4x4.Identity);
    }

    [Fact]
    public void A_cooked_model_named_again_after_an_unload_takes_its_old_slot_with_a_new_ref()
    {
        var files = new MemoryContent();
        files.AddModel("rock");
        var content = new Content(files);
        var first = content.Model("rock");
        content.Models.Unload(first);

        var second = content.Model("rock");

        Assert.Equal(first.Index, second.Index);
        Assert.NotEqual(first, second);
        Assert.Equal(second, content.Model("rock"));
    }

    [Fact]
    public void A_stale_texture_ref_cannot_be_named_by_a_mesh_once_another_texture_takes_its_slot()
    {
        var content = new Content(Textures("textures/a.png", "textures/b.png"));
        var old = content.Texture("textures/a.png");
        content.Unload(old);
        var taken = content.Texture("textures/b.png");
        Assert.Equal(old.Index, taken.Index);

        var problem = Assert.Throws<ArgumentException>(() => content.Mesh(Mesh(new Surface { ColorTexture = old })));

        Assert.Equal($"ColorTexture names {old.Index}, which was unloaded. (Parameter 'ColorTexture')", problem.Message);
        content.Mesh(Mesh(new Surface { ColorTexture = taken }));
    }

    [Fact]
    public void A_stale_image_cannot_be_let_go_again_once_another_image_takes_its_slot()
    {
        var content = new Content(new MemoryContent());
        var old = content.UiImages.Image(1, 1, new byte[4]);
        content.UiImages.Unload(old);
        var taken = content.UiImages.Image(1, 1, new byte[4]);
        Assert.Equal(old.Index, taken.Index);

        var problem = Assert.Throws<ArgumentException>(() => content.UiImages.Unload(old));

        Assert.Equal($"image names {old.Index}, which was unloaded. (Parameter 'image')", problem.Message);
        content.UiImages.Unload(taken);
    }

    [Fact]
    public void Colour_tables_and_shaders_keep_their_slot_after_an_unload()
    {
        string cube = "LUT_3D_SIZE 2\n" + string.Concat(Enumerable.Range(0, 8).Select(i => $"{i & 1} {(i >> 1) & 1} {i >> 2}\n"));
        var content = new Content(new MemoryContent().Add("grades/dusk.cube", System.Text.Encoding.UTF8.GetBytes(cube)));
        var lut = content.Lut("grades/dusk.cube");
        content.Unload(lut);

        var mesh = content.Mesh(Mesh());

        Assert.NotEqual(lut.Index, mesh.Index);
        Assert.IsType<UnloadedItem>(content.Store.Items[lut.Index - 1]);
    }

    [Fact]
    public void A_slot_the_window_saw_is_only_taken_again_once_the_window_has_freed_it()
    {
        var content = new Content(new MemoryContent());
        var old = content.Mesh(Mesh());
        content.Store.Slots.Seen = content.Store.Items.Count;
        content.Models.Unload(old);

        var before = content.Mesh(Mesh());
        Assert.NotEqual(old.Index, before.Index);
        Assert.Equal([old.Index], content.Store.Slots.PendingUnloads.ToArray());

        content.Store.Slots.ReleasePendingUnloads();
        var after = content.Mesh(Mesh());

        Assert.Equal(old.Index, after.Index);
        Assert.Equal([old.Index], content.Store.Slots.Retaken.ToArray());
    }
}
